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
            // Phase 1: 路径解析与验证
            ReportPhase(progress, logProgress, 1, "路径解析与验证", _mode);
            await ValidatePathsForMigrationAsync(logProgress);

            // Phase 2: 扫描源目录
            ReportPhase(progress, logProgress, 2, "扫描源目录", _mode);
            var stats = await ScanDirectoryAsync(_config.SourcePath, progress, logProgress, cancellationToken);
            result.Stats = stats;

            // Phase 3: 复制文件
            ReportPhase(progress, logProgress, 3, "复制文件", _mode);
            await CopyFilesAsync(_config.SourcePath, _config.TargetPath, stats, progress, logProgress, cancellationToken);

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
                result.WasRolledBack = true;
            }
            catch (Exception rollbackEx)
            {
                logProgress?.Report($"回滚失败: {rollbackEx.Message}");
            }

            return result;
        }
    }

    private async Task ValidatePathsForMigrationAsync(IProgress<string>? logProgress)
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

            var (isValidSource, sourceError, sourceWarning) = PathValidator.ValidateSourcePath(_config.SourcePath);
            if (!isValidSource)
                throw new InvalidOperationException(sourceError);

            if (sourceWarning != null)
                logProgress?.Report($"⚠️ {sourceWarning}");

            string sourceLeafForTarget = Path.GetFileName(_config.SourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            if (Directory.Exists(_config.TargetPath))
            {
                string targetLeafName = Path.GetFileName(_config.TargetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(targetLeafName))
                {
                    targetLeafName = new DirectoryInfo(_config.TargetPath).Name;
                }

                // 检查目录是否包含用户数据（忽略标记文件）
                bool isNonEmpty = PathValidator.HasUserContent(_config.TargetPath);

                if (isNonEmpty && !string.Equals(targetLeafName, sourceLeafForTarget, StringComparison.OrdinalIgnoreCase))
                {
                    string newTargetPath = Path.Combine(_config.TargetPath, sourceLeafForTarget);
                    logProgress?.Report($"⚠️ 目标目录非空且不以源目录名结尾");
                    logProgress?.Report($"   自动调整目标路径: {_config.TargetPath} -> {newTargetPath}");
                    _config.TargetPath = newTargetPath;
                }
            }

            var (isValidTarget, targetError) = PathValidator.ValidateTargetPath(_config.TargetPath);
            if (!isValidTarget)
                throw new InvalidOperationException(targetError);

            var (isEmpty, emptyError) = PathValidator.IsTargetDirectoryEmpty(_config.TargetPath);
            if (!isEmpty)
                throw new InvalidOperationException(emptyError);

            var (isValidRelation, relationError) = PathValidator.ValidatePathRelation(_config.SourcePath, _config.TargetPath);
            if (!isValidRelation)
                throw new InvalidOperationException(relationError);

            if (!PathValidator.IsAdministrator())
            {
                logProgress?.Report("⚠️ 当前非管理员权限，若未启用开发者模式，创建符号链接可能失败");
            }

            string targetParent = Path.GetDirectoryName(_config.TargetPath) ?? throw new InvalidOperationException("无法解析目标父目录");
            if (!Directory.Exists(targetParent))
            {
                Directory.CreateDirectory(targetParent);
            }

            if (!Directory.Exists(_config.TargetPath))
            {
                Directory.CreateDirectory(_config.TargetPath);
            }

            // 创建迁移锁文件
            MigrationStateDetector.CreateMigrateLockFile(_config.TargetPath, _config.SourcePath);

            logProgress?.Report($"源目录: {_config.SourcePath}");
            logProgress?.Report($"目标目录: {_config.TargetPath}");
        });
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
            // Phase 1: 路径解析与验证
            ReportPhase(progress, logProgress, 1, "路径解析与验证", _mode);
            await ValidatePathsForRestoreAsync(logProgress);

            // Phase 2: 扫描目标目录（还原时的数据源）
            ReportPhase(progress, logProgress, 2, "扫描数据目录", _mode);
            var stats = await ScanDirectoryAsync(_config.TargetPath, progress, logProgress, cancellationToken);
            result.Stats = stats;

            // Phase 3: 复制文件（目标 → 源）
            ReportPhase(progress, logProgress, 3, "还原文件", _mode);
            
            // 创建临时目录用于还原
            string tempRestorePath = _config.SourcePath + ".restore_temp_" + DateTime.Now.ToString("yyyyMMddHHmmss");
            await CopyFilesAsync(_config.TargetPath, tempRestorePath, stats, progress, logProgress, cancellationToken);

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
                result.WasRolledBack = true;
            }
            catch (Exception rollbackEx)
            {
                logProgress?.Report($"回滚失败: {rollbackEx.Message}");
            }

            return result;
        }
    }

    private async Task ValidatePathsForRestoreAsync(IProgress<string>? logProgress)
    {
        await Task.Run(() =>
        {
            // 验证源路径必须是符号链接
            if (!Directory.Exists(_config.SourcePath))
            {
                throw new InvalidOperationException("源路径不存在");
            }

            if (!SymbolicLinkHelper.IsSymbolicLink(_config.SourcePath))
            {
                throw new InvalidOperationException("源路径不是符号链接，无法还原");
            }

            // 验证目标路径存在
            if (!Directory.Exists(_config.TargetPath))
            {
                throw new InvalidOperationException("目标路径不存在，无法还原");
            }

            // 检查源所在磁盘空间
            string? sourceDrive = Path.GetPathRoot(_config.SourcePath);
            if (string.IsNullOrEmpty(sourceDrive))
            {
                throw new InvalidOperationException("无法确定源路径所在磁盘");
            }

            long targetSize = FileStatsService.GetDirectorySize(_config.TargetPath);
            var (sufficient, available, required) = PathValidator.CheckDiskSpace(_config.SourcePath, targetSize);

            logProgress?.Report($"源磁盘可用空间: {FileStatsService.FormatBytes(available)}");
            logProgress?.Report($"所需空间(含10%余量): {FileStatsService.FormatBytes(required)}");

            if (!sufficient)
            {
                throw new InvalidOperationException("源磁盘空间不足！");
            }

            // 创建还原锁文件
            MigrationStateDetector.CreateRestoreLockFile(_config.SourcePath, _config.TargetPath);

            logProgress?.Report($"符号链接: {_config.SourcePath}");
            logProgress?.Report($"数据位置: {_config.TargetPath}");
            logProgress?.Report($"还原模式: {(_keepTargetOnRestore ? "保留目标数据" : "删除目标数据")}");
        });
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

    private async Task CopyFilesAsync(
        string sourceDir,
        string targetDir,
        FileStats stats,
        IProgress<MigrationProgress>? progress,
        IProgress<string>? logProgress,
        CancellationToken cancellationToken)
    {
        string actionName = _mode == MigrationMode.Migrate ? "复制" : "还原";
        
        var executor = new CopyOperationExecutor(
            sourceDir,
            targetDir,
            stats,
            _config.RobocopyThreads,
            _config.SampleMilliseconds,
            actionName);

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

    #endregion
}


