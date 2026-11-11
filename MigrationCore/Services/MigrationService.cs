using System.Diagnostics;
using MigrationCore.Models;

namespace MigrationCore.Services;

/// <summary>
/// 核心迁移服务（手动模式）
/// 
/// 重构说明：
/// - 复制进度逻辑已抽取到 CopyOperationExecutor，确保与一键迁移模式使用相同算法
/// - 符号链接、备份、回滚等通用流程已抽取到 MigrationWorkflowHelper
/// - 本服务保持原有的 6 阶段流程和对外接口不变
/// </summary>
public class MigrationService
{
    private readonly MigrationConfig _config;
    private string? _backupPath;

    public MigrationService(MigrationConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// 执行迁移操作
    /// </summary>
    public async Task<MigrationResult> ExecuteMigrationAsync(
        IProgress<MigrationProgress>? progress = null,
        IProgress<string>? logProgress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new MigrationResult
        {
            SourcePath = _config.SourcePath,
            TargetPath = _config.TargetPath
        };

        try
        {
            // Phase 0: 文件占用检测
            ReportPhase(progress, logProgress, 0, "检测文件占用");
            var probe = await MigrationWorkflowHelper.ProbeAsync(_config, MigrationMode.Migrate, logProgress, cancellationToken);
            MigrationWorkflowHelper.ApplyProbeResult(result, probe);
            if (!probe.CanProceed) return result;
            logProgress?.Report("✅ 文件占用检测通过");

            // Phase 1: 路径解析与验证
            ReportPhase(progress, logProgress, 1, "事务路径已通过准入");

            // Phase 2: 扫描源目录
            ReportPhase(progress, logProgress, 2, "扫描源目录");
            var stats = await ScanSourceDirectoryAsync(progress, logProgress, cancellationToken);
            result.Stats = stats;

            // Phase 3: 复制文件
            ReportPhase(progress, logProgress, 3, "复制文件");
            var copy = await CopyFilesAsync(probe.Transaction ?? throw new IOException("缺少已保存事务。"), stats, progress, logProgress, cancellationToken);
            MigrationWorkflowHelper.ApplyCopyResult(result, copy);
            if (!copy.Success) return result;

            // Phase 4: 创建符号链接
            ReportPhase(progress, logProgress, 4, "创建符号链接");
            await CreateSymbolicLinkAsync(logProgress);

            // Phase 5: 健康检查
            ReportPhase(progress, logProgress, 5, "健康检查");
            await VerifySymbolicLinkAsync(logProgress);

            // Phase 6: 清理备份
            ReportPhase(progress, logProgress, 6, "清理备份");
            await CleanupBackupAsync(logProgress);

            result.Success = true;
            logProgress?.Report("✅ 迁移完成！");
            
            // 报告最终100%进度
            progress?.Report(new MigrationProgress
            {
                CurrentPhase = 6,
                PhaseDescription = "完成",
                PercentComplete = 100,
                Message = "迁移完成"
            });

            return result;
        }
        catch (Exception ex)
        {
            logProgress?.Report($"❌ 错误: {ex.Message}");
            result.Success = false;
            result.ErrorMessage = ex.Message;

            // 尝试回滚
            try
            {
                await RollbackAsync(logProgress);
                result.RollbackOutcome = RollbackOutcome.RecoveryRequired;
            }
            catch (Exception rollbackEx)
            {
                logProgress?.Report($"回滚失败: {rollbackEx.Message}");
            }

            return result;
        }
    }


    private async Task<FileStats> ScanSourceDirectoryAsync(
        IProgress<MigrationProgress>? progress,
        IProgress<string>? logProgress,
        CancellationToken cancellationToken)
    {
        logProgress?.Report("正在扫描源目录...");

        long thresholdBytes = (long)_config.LargeFileThresholdMB * 1024 * 1024;
        var stats = await MigrationWorkflowHelper.ScanDirectoryAsync(_config.SourcePath, thresholdBytes, logProgress, cancellationToken);

        MigrationWorkflowHelper.ReportScanResults(stats, _config.LargeFileThresholdMB, logProgress);

        // 检查磁盘空间
        var (sufficient, available, required) = PathValidator.CheckDiskSpace(_config.TargetPath, stats.TotalBytes);
        logProgress?.Report($"目标磁盘可用空间: {FileStatsService.FormatBytes(available)}");
        logProgress?.Report($"所需空间(含10%余量): {FileStatsService.FormatBytes(required)}");

        if (!sufficient)
        {
            throw new InvalidOperationException("目标磁盘空间不足！");
        }

        return stats;
    }

    private Task<CopyExecutionResult> CopyFilesAsync(MigrationTransactionRecord transaction,
        FileStats stats, IProgress<MigrationProgress>? progress, IProgress<string>? logProgress,
        CancellationToken cancellationToken)
    {
        var executor = new CopyOperationExecutor(transaction, stats,
            _config.RobocopyThreads, _config.SampleMilliseconds, "复制");
        return executor.ExecuteAsync(progress, logProgress, cancellationToken);
    }

    private async Task CreateSymbolicLinkAsync(IProgress<string>? logProgress)
    {
        _backupPath = await MigrationWorkflowHelper.CreateSymbolicLinkWithBackupAsync(
            _config.SourcePath,
            _config.TargetPath,
            logProgress);
    }

    private async Task VerifySymbolicLinkAsync(IProgress<string>? logProgress)
    {
        await MigrationWorkflowHelper.VerifySymbolicLinkAsync(_config.SourcePath, logProgress);
    }

    private async Task CleanupBackupAsync(IProgress<string>? logProgress)
    {
        await MigrationWorkflowHelper.CleanupBackupAsync(_backupPath, logProgress);
    }

    private async Task RollbackAsync(IProgress<string>? logProgress)
    {
        await MigrationWorkflowHelper.RollbackMigrationAsync(
            _config.SourcePath,
            _config.TargetPath,
            _backupPath,
            logProgress);
    }

    private void ReportPhase(IProgress<MigrationProgress>? progress, IProgress<string>? logProgress, int phase, string description)
    {
        logProgress?.Report($"[{phase}/6] {description}");

        // 只在非复制阶段报告基于阶段的进度，复制阶段由 CopyFilesAsync 自己报告
        if (phase != 3)
        {
            // 进度分配：0=0-5%, 1=5-10%, 2=10-15%, 3=15-90%, 4=90-93%, 5=93-96%, 6=96-100%
            double percentComplete = phase switch
            {
                0 => 0,
                1 => 5,
                2 => 10,
                4 => 90,
                5 => 93,
                6 => 96,
                _ => 0
            };

            progress?.Report(new MigrationProgress
            {
                CurrentPhase = phase,
                PhaseDescription = description,
                PercentComplete = percentComplete,
                Message = description
            });
        }
    }

}

