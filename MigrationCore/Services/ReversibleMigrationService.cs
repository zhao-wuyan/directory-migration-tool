using System.Diagnostics;
using MigrationCore.Models;

namespace MigrationCore.Services;

/// <summary>
/// 可逆迁移服务 - 支持迁移和还原两种模式（一键迁移模式）
/// 
/// 重构说明：
/// - 复制进度逻辑已抽取到 CopyOperationExecutor，与手动模式共享相同算法
/// - 符号链接、备份、回滚等通用流程已抽取到 MigrationWorkflowHelper
/// - 本服务保持迁移/还原双模式支持，对外接口不变
/// </summary>
public class ReversibleMigrationService
{
    private readonly MigrationConfig _config;
    private readonly MigrationMode _mode;
    private readonly bool _keepTargetOnRestore;
    private string? _backupPath;

    public ReversibleMigrationService(
        MigrationConfig config, 
        MigrationMode mode = MigrationMode.Migrate,
        bool keepTargetOnRestore = false)
    {
        _config = config;
        _mode = mode;
        _keepTargetOnRestore = keepTargetOnRestore;
    }

    /// <summary>
    /// 执行迁移或还原操作
    /// </summary>
    public async Task<MigrationResult> ExecuteAsync(
        IProgress<MigrationProgress>? progress = null,
        IProgress<string>? logProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (_mode == MigrationMode.Migrate)
        {
            return await ExecuteMigrationAsync(progress, logProgress, cancellationToken);
        }
        else
        {
            return await ExecuteRestoreAsync(progress, logProgress, cancellationToken);
        }
    }

    #region Migration (Migrate Mode)

    private async Task<MigrationResult> ExecuteMigrationAsync(
        IProgress<MigrationProgress>? progress,
        IProgress<string>? logProgress,
        CancellationToken cancellationToken)
    {
        var result = new MigrationResult
        {
            SourcePath = _config.SourcePath,
            TargetPath = _config.TargetPath
        };

        try
        {
            // Phase 0: 文件占用检测
            ReportPhase(progress, logProgress, 0, "检测文件占用", _mode);
            var probe = await MigrationWorkflowHelper.ProbeAsync(_config, MigrationMode.Migrate, logProgress, cancellationToken);
            MigrationWorkflowHelper.ApplyProbeResult(result, probe);
            if (!probe.CanProceed) return result;
            logProgress?.Report("✅ 文件占用检测通过");

            // Phase 1: 路径解析与验证
            ReportPhase(progress, logProgress, 1, "事务路径已通过准入", _mode);

            // Phase 2: 扫描源目录
            ReportPhase(progress, logProgress, 2, "扫描源目录", _mode);
            var stats = await ScanDirectoryAsync(_config.SourcePath, progress, logProgress, cancellationToken);
            result.Stats = stats;

            // Phase 3: 复制文件
            ReportPhase(progress, logProgress, 3, "复制文件", _mode);
            var copy = await CopyFilesAsync(probe.Transaction ?? throw new IOException("缺少已保存事务。"), stats, progress, logProgress, cancellationToken);
            MigrationWorkflowHelper.ApplyCopyResult(result, copy);
            if (!copy.Success) return result;

            // 创建迁移完成标记
            MigrationStateDetector.CreateMigrateDoneFile(_config.TargetPath);

            // Phase 4: 创建符号链接
            ReportPhase(progress, logProgress, 4, "创建符号链接", _mode);
            await CreateSymbolicLinkAsync(logProgress);

            // Phase 5: 健康检查
            ReportPhase(progress, logProgress, 5, "健康检查", _mode);
            await VerifySymbolicLinkAsync(logProgress);

            // Phase 6: 清理备份
            ReportPhase(progress, logProgress, 6, "清理备份", _mode);
            await CleanupBackupAsync(logProgress);

            result.Success = true;
            logProgress?.Report("✅ 迁移完成！");

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

            try
            {
                await RollbackMigrationAsync(logProgress);
                result.RollbackOutcome = RollbackOutcome.RecoveryRequired;
            }
            catch (Exception rollbackEx)
            {
                logProgress?.Report($"回滚失败: {rollbackEx.Message}");
            }

            return result;
        }
    }


    #endregion

    #region Restore Mode

    private async Task<MigrationResult> ExecuteRestoreAsync(
        IProgress<MigrationProgress>? progress,
        IProgress<string>? logProgress,
        CancellationToken cancellationToken)
    {
        var result = new MigrationResult
        {
            SourcePath = _config.SourcePath,
            TargetPath = _config.TargetPath
        };

        try
        {
            // Phase 0: 文件占用检测
            ReportPhase(progress, logProgress, 0, "检测文件占用", _mode);
            var probe = await MigrationWorkflowHelper.ProbeAsync(_config, MigrationMode.Restore, logProgress, cancellationToken);
            MigrationWorkflowHelper.ApplyProbeResult(result, probe);
            if (!probe.CanProceed) return result;
            logProgress?.Report("✅ 文件占用检测通过");

            // Phase 1: 路径解析与验证
            ReportPhase(progress, logProgress, 1, "事务路径已通过准入", _mode);

            // Phase 2: 扫描目标目录（还原时的数据源）
            ReportPhase(progress, logProgress, 2, "扫描数据目录", _mode);
            var stats = await ScanDirectoryAsync(_config.TargetPath, progress, logProgress, cancellationToken);
            result.Stats = stats;

            // Phase 3: 复制文件（目标 → 源）
            ReportPhase(progress, logProgress, 3, "还原文件", _mode);
            
            var copy = await CopyFilesAsync(probe.Transaction ?? throw new IOException("缺少已保存事务。"), stats, progress, logProgress, cancellationToken);
            MigrationWorkflowHelper.ApplyCopyResult(result, copy);
            if (!copy.Success) return result;
            string tempRestorePath = copy.StagingPath;

            // 创建还原完成标记
            MigrationStateDetector.CreateRestoreDoneFile(tempRestorePath);

            // Phase 4: 解除符号链接
            ReportPhase(progress, logProgress, 4, "解除符号链接", _mode);
            await RemoveSymbolicLinkAsync(tempRestorePath, logProgress);

            // Phase 5: 健康检查
            ReportPhase(progress, logProgress, 5, "健康检查", _mode);
            await VerifyRestoredDirectoryAsync(logProgress);

            // Phase 6: 清理目标数据
            ReportPhase(progress, logProgress, 6, "清理收尾", _mode);
            await CleanupAfterRestoreAsync(logProgress);

            result.Success = true;
            logProgress?.Report("✅ 还原完成！");

            progress?.Report(new MigrationProgress
            {
                CurrentPhase = 6,
                PhaseDescription = "完成",
                PercentComplete = 100,
                Message = "还原完成"
            });

            return result;
        }
        catch (Exception ex)
        {
            logProgress?.Report($"❌ 错误: {ex.Message}");
            result.Success = false;
            result.ErrorMessage = ex.Message;

            try
            {
                await RollbackRestoreAsync(logProgress);
                result.RollbackOutcome = RollbackOutcome.RecoveryRequired;
            }
            catch (Exception rollbackEx)
            {
                logProgress?.Report($"回滚失败: {rollbackEx.Message}");
            }

            return result;
        }
    }


    private async Task RemoveSymbolicLinkAsync(string tempRestorePath, IProgress<string>? logProgress)
    {
        await Task.Run(() =>
        {
            logProgress?.Report($"删除符号链接: {_config.SourcePath}");
            
            // 删除符号链接
            if (SymbolicLinkHelper.IsSymbolicLink(_config.SourcePath))
            {
                Directory.Delete(_config.SourcePath, false);
            }

            // 将临时还原目录移动到源位置
            logProgress?.Report($"还原目录: {tempRestorePath} -> {_config.SourcePath}");
            Directory.Move(tempRestorePath, _config.SourcePath);
        });
    }

    private async Task VerifyRestoredDirectoryAsync(IProgress<string>? logProgress)
    {
        await Task.Run(() =>
        {
            logProgress?.Report("验证还原目录...");

            if (!Directory.Exists(_config.SourcePath))
            {
                throw new InvalidOperationException("还原后的源目录无法访问");
            }

            if (SymbolicLinkHelper.IsSymbolicLink(_config.SourcePath))
            {
                throw new InvalidOperationException("还原后的源目录仍然是符号链接");
            }

            logProgress?.Report("✅ 还原目录验证成功");
        });
    }

    private async Task CleanupAfterRestoreAsync(IProgress<string>? logProgress)
    {
        if (!_keepTargetOnRestore && Directory.Exists(_config.TargetPath))
        {
            await Task.Run(() =>
            {
                try
                {
                    logProgress?.Report($"清理目标数据: {_config.TargetPath}");
                    Directory.Delete(_config.TargetPath, true);
                    logProgress?.Report("✅ 目标数据已清理");
                }
                catch (Exception ex)
                {
                    logProgress?.Report($"⚠️ 清理目标数据失败: {ex.Message}");
                }
            });
        }
        else
        {
            logProgress?.Report("保留目标数据");
            
            // 如果保留目标数据，清理其中的迁移标记文件
            if (Directory.Exists(_config.TargetPath))
            {
                MigrationStateDetector.DeleteMigrateMarkers(_config.TargetPath);
            }
        }

        // 清理源目录的所有标记文件
        // 还原时，源目录会从目标复制回数据，标记文件也会被复制过来，需要清理
        MigrationStateDetector.DeleteRestoreMarkers(_config.SourcePath);
        MigrationStateDetector.DeleteMigrateMarkers(_config.SourcePath);
    }

    #endregion

    #region Shared Methods

    private async Task<FileStats> ScanDirectoryAsync(
        string directoryPath,
        IProgress<MigrationProgress>? progress,
        IProgress<string>? logProgress,
        CancellationToken cancellationToken)
    {
        long thresholdBytes = (long)_config.LargeFileThresholdMB * 1024 * 1024;
        var stats = await MigrationWorkflowHelper.ScanDirectoryAsync(directoryPath, thresholdBytes, logProgress, cancellationToken);

        MigrationWorkflowHelper.ReportScanResults(stats, _config.LargeFileThresholdMB, logProgress);

        return stats;
    }

    private Task<CopyExecutionResult> CopyFilesAsync(MigrationTransactionRecord transaction,
        FileStats stats, IProgress<MigrationProgress>? progress, IProgress<string>? logProgress,
        CancellationToken cancellationToken)
    {
        string actionName = _mode == MigrationMode.Migrate ? "复制" : "还原";
        var executor = new CopyOperationExecutor(transaction, stats,
            _config.RobocopyThreads, _config.SampleMilliseconds, actionName);
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

    private async Task RollbackMigrationAsync(IProgress<string>? logProgress)
    {
        await MigrationWorkflowHelper.RollbackMigrationAsync(
            _config.SourcePath,
            _config.TargetPath,
            _backupPath,
            logProgress);
    }

    private async Task RollbackRestoreAsync(IProgress<string>? logProgress)
    {
        await Task.Run(() =>
        {
            logProgress?.Report("开始回滚还原操作...");

            try
            {
                // 查找临时还原目录
                string? parentDir = Path.GetDirectoryName(_config.SourcePath);
                if (!string.IsNullOrEmpty(parentDir))
                {
                    string sourceName = Path.GetFileName(_config.SourcePath);
                    var tempDirs = Directory.GetDirectories(parentDir, $"{sourceName}.restore_temp_*");
                    
                    foreach (var tempDir in tempDirs)
                    {
                        try
                        {
                            Directory.Delete(tempDir, true);
                            logProgress?.Report($"清理临时目录: {tempDir}");
                        }
                        catch
                        {
                            // 忽略
                        }
                    }
                }

                // 清理还原标记
                MigrationStateDetector.DeleteRestoreMarkers(_config.TargetPath);

                logProgress?.Report("✅ 回滚完成");
            }
            catch (Exception ex)
            {
                logProgress?.Report($"❌ 回滚失败: {ex.Message}");
                throw;
            }
        });
    }

    private void ReportPhase(
        IProgress<MigrationProgress>? progress, 
        IProgress<string>? logProgress, 
        int phase, 
        string description,
        MigrationMode mode)
    {
        string prefix = mode == MigrationMode.Migrate ? "迁移" : "还原";
        logProgress?.Report($"[{phase}/6] {description}");

        if (phase != 3)
        {
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

    #endregion
}


