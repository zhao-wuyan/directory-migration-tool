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
            // Phase 1: 路径解析与验证
            ReportPhase(progress, logProgress, 1, "路径解析与验证");
            await ValidatePathsAsync(logProgress);

            // Phase 2: 扫描源目录
            ReportPhase(progress, logProgress, 2, "扫描源目录");
            var stats = await ScanSourceDirectoryAsync(progress, logProgress, cancellationToken);
            result.Stats = stats;

            // Phase 3: 复制文件
            ReportPhase(progress, logProgress, 3, "复制文件");
            await CopyFilesAsync(stats, progress, logProgress, cancellationToken);

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
                result.WasRolledBack = true;
            }
            catch (Exception rollbackEx)
            {
                logProgress?.Report($"回滚失败: {rollbackEx.Message}");
            }

            return result;
        }
    }

    private async Task ValidatePathsAsync(IProgress<string>? logProgress)
    {
        await Task.Run(() =>
        {
            // 清理源目录可能存在的旧标记文件
            // 这些标记可能是之前作为目标目录时创建的，被还原操作复制回来了
            if (Directory.Exists(_config.SourcePath))
            {
                MigrationStateDetector.DeleteMigrateMarkers(_config.SourcePath);
                MigrationStateDetector.DeleteRestoreMarkers(_config.SourcePath);
            }

            // 验证源路径
            var (isValidSource, sourceError, sourceWarning) = PathValidator.ValidateSourcePath(_config.SourcePath);
            if (!isValidSource)
                throw new InvalidOperationException(sourceError);

            if (sourceWarning != null)
                logProgress?.Report($"⚠️ {sourceWarning}");

            // 获取源目录名称（用于可能的目标路径调整）
            string sourceLeafForTarget = Path.GetFileName(_config.SourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            
            // 若目标路径是一个已存在的非空文件夹，且不以源目录名结尾，则自动拼接源目录名
            if (Directory.Exists(_config.TargetPath))
            {
                string targetLeafName = Path.GetFileName(_config.TargetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(targetLeafName))
                {
                    targetLeafName = new DirectoryInfo(_config.TargetPath).Name;
                }
                
                // 检查目标目录是否非空
                // 检查目录是否包含用户数据（忽略标记文件）
                bool isNonEmpty = PathValidator.HasUserContent(_config.TargetPath);
                
                // 如果目标目录非空，且目标目录名不等于源目录名，则自动拼接
                if (isNonEmpty && !string.Equals(targetLeafName, sourceLeafForTarget, StringComparison.OrdinalIgnoreCase))
                {
                    string newTargetPath = Path.Combine(_config.TargetPath, sourceLeafForTarget);
                    logProgress?.Report($"⚠️ 目标目录非空且不以源目录名结尾");
                    logProgress?.Report($"   自动调整目标路径: {_config.TargetPath} -> {newTargetPath}");
                    _config.TargetPath = newTargetPath;
                }
            }

            // 验证目标路径
            var (isValidTarget, targetError) = PathValidator.ValidateTargetPath(_config.TargetPath);
            if (!isValidTarget)
                throw new InvalidOperationException(targetError);

            // 检查最终目标目录是否为空（在路径调整之后）
            var (isEmpty, emptyError) = PathValidator.IsTargetDirectoryEmpty(_config.TargetPath);
            if (!isEmpty)
                throw new InvalidOperationException(emptyError);

            // 验证路径关系
            var (isValidRelation, relationError) = PathValidator.ValidatePathRelation(_config.SourcePath, _config.TargetPath);
            if (!isValidRelation)
                throw new InvalidOperationException(relationError);

            // 权限检查
            if (!PathValidator.IsAdministrator())
            {
                logProgress?.Report("⚠️ 当前非管理员权限，若未启用开发者模式，创建符号链接可能失败");
            }

            // 创建目标目录
            string targetParent = Path.GetDirectoryName(_config.TargetPath) ?? throw new InvalidOperationException("无法解析目标父目录");
            if (!Directory.Exists(targetParent))
            {
                Directory.CreateDirectory(targetParent);
            }

            if (!Directory.Exists(_config.TargetPath))
            {
                Directory.CreateDirectory(_config.TargetPath);
            }

            logProgress?.Report($"源目录: {_config.SourcePath}");
            logProgress?.Report($"目标目录: {_config.TargetPath}");
        });
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

    private async Task CopyFilesAsync(
        FileStats stats,
        IProgress<MigrationProgress>? progress,
        IProgress<string>? logProgress,
        CancellationToken cancellationToken)
    {
        var executor = new CopyOperationExecutor(
            _config.SourcePath,
            _config.TargetPath,
            stats,
            _config.RobocopyThreads,
            _config.SampleMilliseconds,
            "复制");

        await executor.ExecuteAsync(progress, logProgress, cancellationToken);
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
            // 进度分配：1=0-5%, 2=5-10%, 3=10-90%, 4=90-93%, 5=93-96%, 6=96-100%
            double percentComplete = phase switch
            {
                1 => 0,
                2 => 5,
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

