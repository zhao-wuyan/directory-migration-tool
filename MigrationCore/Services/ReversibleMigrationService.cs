using System.Diagnostics;
using MigrationCore.Models;

namespace MigrationCore.Services;

/// <summary>
/// 可逆迁移服务 - 支持迁移和还原两种模式
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

                bool isNonEmpty = false;
                try
                {
                    isNonEmpty = Directory.EnumerateFileSystemEntries(_config.TargetPath).Any();
                }
                catch
                {
                    // 忽略错误
                }

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
        }

        // 清理还原标记
        MigrationStateDetector.DeleteRestoreMarkers(_config.SourcePath);
    }

    #endregion

    #region Shared Methods

    private async Task<FileStats> ScanDirectoryAsync(
        string directoryPath,
        IProgress<MigrationProgress>? progress,
        IProgress<string>? logProgress,
        CancellationToken cancellationToken)
    {
        logProgress?.Report($"正在扫描: {directoryPath}");

        var scanProgress = new Progress<string>(msg => logProgress?.Report(msg));
        long thresholdBytes = (long)_config.LargeFileThresholdMB * 1024 * 1024;

        var stats = await FileStatsService.ScanDirectoryAsync(directoryPath, thresholdBytes, scanProgress, cancellationToken);

        logProgress?.Report($"总文件数: {stats.TotalFiles}");
        logProgress?.Report($"总大小: {FileStatsService.FormatBytes(stats.TotalBytes)}");
        logProgress?.Report($"大文件 (≥{_config.LargeFileThresholdMB}MB): {stats.LargeFiles} 个");

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
        logProgress?.Report($"开始{actionName}文件 (robocopy)...");

        progress?.Report(new MigrationProgress
        {
            CurrentPhase = 3,
            PhaseDescription = $"{actionName}文件",
            PercentComplete = 10,
            CopiedBytes = 0,
            TotalBytes = stats.TotalBytes,
            SpeedBytesPerSecond = 0,
            Message = $"准备{actionName} {FileStatsService.FormatBytes(stats.TotalBytes)}..."
        });

        var robocopyArgs = new List<string>
        {
            $"\"{sourceDir}\"",
            $"\"{targetDir}\"",
            "/MIR",
            "/COPYALL",
            "/DCOPY:DAT",
            "/R:0",
            "/W:0",
            "/XJ",
            "/NFL",
            "/NDL",
            "/NP",
            "/Z",  // 可续传模式
            "/ZB", // 回退到备份模式
            $"/MT:{_config.RobocopyThreads}"
        };

        var processStartInfo = new ProcessStartInfo
        {
            FileName = "robocopy.exe",
            Arguments = string.Join(" ", robocopyArgs),
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(processStartInfo);
        if (process == null)
            throw new InvalidOperationException("无法启动 robocopy 进程");

        var stopwatch = Stopwatch.StartNew();
        long prevBytes = 0;
        TimeSpan prevTime = TimeSpan.Zero;

        while (!process.HasExited)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                logProgress?.Report("正在终止 robocopy 进程及其子进程...");
                KillProcessTree(process.Id);
                throw new OperationCanceledException("用户取消操作");
            }

            await Task.Delay(_config.SampleMilliseconds, cancellationToken);

            long copiedBytes = FileStatsService.GetDirectorySize(targetDir);
            TimeSpan elapsed = stopwatch.Elapsed;

            long deltaBytes = Math.Max(0, copiedBytes - prevBytes);
            double deltaTime = (elapsed - prevTime).TotalSeconds;
            double speed = deltaTime > 0 ? deltaBytes / deltaTime : 0;

            double copyPercent = stats.TotalBytes > 0 ? Math.Min(100, (copiedBytes * 100.0) / stats.TotalBytes) : 0;
            double percent = 10 + (copyPercent * 0.8);

            TimeSpan? eta = null;
            if (speed > 0 && stats.TotalBytes > 0)
            {
                long remainingBytes = Math.Max(0, stats.TotalBytes - copiedBytes);
                int etaSeconds = (int)Math.Ceiling(remainingBytes / speed);
                eta = TimeSpan.FromSeconds(etaSeconds);
            }

            var migrationProgress = new MigrationProgress
            {
                PercentComplete = percent,
                CopiedBytes = copiedBytes,
                TotalBytes = stats.TotalBytes,
                SpeedBytesPerSecond = speed,
                EstimatedTimeRemaining = eta,
                CurrentPhase = 3,
                PhaseDescription = $"{actionName}文件",
                Message = $"{percent:F1}% | {FileStatsService.FormatBytes(copiedBytes)} / {FileStatsService.FormatBytes(stats.TotalBytes)} | {FileStatsService.FormatSpeed(speed)}"
            };

            progress?.Report(migrationProgress);

            prevBytes = copiedBytes;
            prevTime = elapsed;
        }

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode >= 8)
        {
            throw new InvalidOperationException($"Robocopy {actionName}失败，退出码: {process.ExitCode}");
        }

        long finalSize = FileStatsService.GetDirectorySize(targetDir);
        if (stats.TotalBytes > 0)
        {
            double ratio = (double)finalSize / stats.TotalBytes;
            if (ratio < 0.98)
            {
                logProgress?.Report($"⚠️ 警告: 目标大小仅为源的 {ratio:P1}，请确认{actionName}是否完整");
            }
        }

        progress?.Report(new MigrationProgress
        {
            CurrentPhase = 3,
            PhaseDescription = $"{actionName}文件",
            PercentComplete = 90,
            CopiedBytes = finalSize,
            TotalBytes = stats.TotalBytes,
            SpeedBytesPerSecond = 0,
            Message = $"{actionName}完成: {FileStatsService.FormatBytes(finalSize)}"
        });

        logProgress?.Report($"{actionName}完成，最终大小: {FileStatsService.FormatBytes(finalSize)}");
    }

    private async Task CreateSymbolicLinkAsync(IProgress<string>? logProgress)
    {
        await Task.Run(() =>
        {
            string parent = Path.GetDirectoryName(_config.SourcePath) ?? throw new InvalidOperationException("无法解析源目录父路径");
            string name = Path.GetFileName(_config.SourcePath);
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            _backupPath = Path.Combine(parent, $"{name}.bak_{timestamp}");

            logProgress?.Report($"备份源目录到: {_backupPath}");
            Directory.Move(_config.SourcePath, _backupPath);

            logProgress?.Report($"创建符号链接: {_config.SourcePath} -> {_config.TargetPath}");

            bool success = SymbolicLinkHelper.CreateDirectorySymbolicLink(_config.SourcePath, _config.TargetPath);

            if (!success)
            {
                logProgress?.Report("P/Invoke 失败，尝试使用 cmd mklink...");
                success = SymbolicLinkHelper.CreateSymbolicLinkViaCmdAsync(_config.SourcePath, _config.TargetPath, out string error);

                if (!success)
                {
                    throw new InvalidOperationException($"创建符号链接失败: {error}");
                }
            }

            if (!Directory.Exists(_config.SourcePath))
            {
                throw new InvalidOperationException("符号链接创建后无法访问");
            }
        });
    }

    private async Task VerifySymbolicLinkAsync(IProgress<string>? logProgress)
    {
        await Task.Run(() =>
        {
            logProgress?.Report("验证符号链接...");

            if (!SymbolicLinkHelper.IsSymbolicLink(_config.SourcePath))
            {
                throw new InvalidOperationException("创建的对象不是符号链接（重解析点）");
            }

            if (!Directory.Exists(_config.SourcePath))
            {
                throw new InvalidOperationException("符号链接无法访问");
            }

            logProgress?.Report("✅ 符号链接验证成功");
        });
    }

    private async Task CleanupBackupAsync(IProgress<string>? logProgress)
    {
        if (string.IsNullOrEmpty(_backupPath) || !Directory.Exists(_backupPath))
            return;

        await Task.Run(() =>
        {
            try
            {
                logProgress?.Report($"清理备份目录: {_backupPath}");
                Directory.Delete(_backupPath, true);
                logProgress?.Report("✅ 备份已清理");
            }
            catch (Exception ex)
            {
                logProgress?.Report($"⚠️ 清理备份失败: {ex.Message}");
            }
        });
    }

    private async Task RollbackMigrationAsync(IProgress<string>? logProgress)
    {
        if (string.IsNullOrEmpty(_backupPath))
            return;

        await Task.Run(() =>
        {
            logProgress?.Report("开始回滚...");

            try
            {
                if (Directory.Exists(_config.SourcePath))
                {
                    if (SymbolicLinkHelper.IsSymbolicLink(_config.SourcePath))
                    {
                        Directory.Delete(_config.SourcePath, false);
                    }
                }

                if (Directory.Exists(_backupPath))
                {
                    Directory.Move(_backupPath, _config.SourcePath);
                    logProgress?.Report("✅ 已回滚至迁移前状态");
                }

                // 清理迁移标记
                MigrationStateDetector.DeleteMigrateMarkers(_config.TargetPath);
            }
            catch (Exception ex)
            {
                logProgress?.Report($"❌ 回滚失败: {ex.Message}");
                throw;
            }
        });
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

    /// <summary>
    /// 终止进程及其所有子进程
    /// </summary>
    private static void KillProcessTree(int processId)
    {
        try
        {
            // 使用 taskkill /T (tree) /F (force) 终止进程树
            var killProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/PID {processId} /T /F",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            killProcess.Start();
            killProcess.WaitForExit(5000); // 最多等待5秒
        }
        catch
        {
            // 如果 taskkill 失败，回退到 Kill()
            try
            {
                var proc = Process.GetProcessById(processId);
                proc.Kill();
            }
            catch { }
        }
    }

    #endregion
}


