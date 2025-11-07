using System.Diagnostics;
using MigrationCore.Models;

namespace MigrationCore.Services;

/// <summary>
/// 核心迁移服务
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

        var scanProgress = new Progress<string>(msg => logProgress?.Report(msg));
        long thresholdBytes = (long)_config.LargeFileThresholdMB * 1024 * 1024;

        var stats = await FileStatsService.ScanDirectoryAsync(_config.SourcePath, thresholdBytes, scanProgress, cancellationToken);

        logProgress?.Report($"总文件数: {stats.TotalFiles}");
        logProgress?.Report($"总大小: {FileStatsService.FormatBytes(stats.TotalBytes)}");
        logProgress?.Report($"大文件 (≥{_config.LargeFileThresholdMB}MB): {stats.LargeFiles} 个");

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
        logProgress?.Report("开始复制文件 (robocopy)...");

        // 报告初始进度 - 复制阶段从10%开始
        progress?.Report(new MigrationProgress
        {
            CurrentPhase = 3,
            PhaseDescription = "复制文件",
            PercentComplete = 10,
            CopiedBytes = 0,
            TotalBytes = stats.TotalBytes,
            SpeedBytesPerSecond = 0,
            Message = $"准备复制 {FileStatsService.FormatBytes(stats.TotalBytes)}..."
        });

        var robocopyArgs = new List<string>
        {
            $"\"{_config.SourcePath}\"",
            $"\"{_config.TargetPath}\"",
            "/MIR",
            "/COPYALL",
            "/DCOPY:DAT",
            "/R:0",
            "/W:0",
            "/XJ",
#if !DEBUG
            // Release 模式下减少输出，提高性能
            "/NFL",  // No File List
            "/NDL",  // No Directory List
            // 注意：不使用 /NP，因为我们需要解析百分比来更新进度
#endif
            $"/MT:{_config.RobocopyThreads}"
        };

        var processStartInfo = new ProcessStartInfo
        {
            FileName = "robocopy.exe",
            Arguments = string.Join(" ", robocopyArgs),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(processStartInfo);
        if (process == null)
            throw new InvalidOperationException("无法启动 robocopy 进程");

        // 从 Robocopy 输出解析的百分比（所有模式下使用）
        double robocopyPercent = 0;
        object robocopyPercentLock = new object();

        // 异步读取并打印 Robocopy 日志（同时解析百分比）
        _ = Task.Run(async () =>
        {
            try
            {
                while (!process.StandardOutput.EndOfStream)
                {
                    string? line = await process.StandardOutput.ReadLineAsync();
                    if (!string.IsNullOrWhiteSpace(line))
                    {
#if DEBUG
                        logProgress?.Report($"[Robocopy] {line}");
                        System.Diagnostics.Debug.WriteLine($"[Robocopy] {line}");
#endif
                        // 解析 Robocopy 的百分比输出（如 "  18%"）
                        string trimmed = line.Trim();
                        if (trimmed.EndsWith("%") && trimmed.Length <= 5)
                        {
                            // 尝试解析百分比
                            string percentStr = trimmed.TrimEnd('%').Trim();
                            if (double.TryParse(percentStr, out double percent))
                            {
                                lock (robocopyPercentLock)
                                {
                                    robocopyPercent = percent;
                                }
#if DEBUG
                                System.Diagnostics.Debug.WriteLine($"[Robocopy Percent] {percent}%");
#endif
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Robocopy Log Error] {ex.Message}");
            }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                while (!process.StandardError.EndOfStream)
                {
                    string? line = await process.StandardError.ReadLineAsync();
                    if (!string.IsNullOrWhiteSpace(line))
                    {
#if DEBUG
                        logProgress?.Report($"[Robocopy Error] {line}");
                        System.Diagnostics.Debug.WriteLine($"[Robocopy Error] {line}");
#endif
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Robocopy Error Log Error] {ex.Message}");
            }
        });

        // 监控复制进度
        var stopwatch = Stopwatch.StartNew();
        long prevBytes = 0;
        TimeSpan prevTime = TimeSpan.Zero;
        
        // 用于平滑进度的变量 - 使用速度累加法而不是直接平滑字节数
        long displayedBytes = 0; // 显示给用户的字节数，通过速度累加
        double smoothedSpeed = 0;
        const double speedSmoothingFactor = 0.3; // 速度平滑系数
        
        // 用于检测停滞的变量
        int noChangeCount = 0;
        const int maxNoChangeCount = 10; // 连续10次无变化才认为可能卡住

        while (!process.HasExited)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                logProgress?.Report("正在终止 robocopy 进程及其子进程...");
                KillProcessTree(process.Id);
                throw new OperationCanceledException("用户取消操作");
            }

            await Task.Delay(_config.SampleMilliseconds, cancellationToken);

            long actualCopiedBytes = FileStatsService.GetDirectorySize(_config.TargetPath);
            TimeSpan elapsed = stopwatch.Elapsed;

            // 计算实际增量
            long actualDeltaBytes = Math.Max(0, actualCopiedBytes - prevBytes);
            double deltaTime = (elapsed - prevTime).TotalSeconds;
            
            // 检测是否有变化
            if (actualDeltaBytes == 0)
            {
                noChangeCount++;
            }
            else
            {
                noChangeCount = 0;
            }
            
            // 计算瞬时速度（基于实际增量）
            double instantSpeed = deltaTime > 0 ? actualDeltaBytes / deltaTime : 0;
            
            // 平滑速度
            if (smoothedSpeed == 0 && instantSpeed > 0)
            {
                // 第一次有效速度采样
                smoothedSpeed = instantSpeed;
            }
            else if (instantSpeed > 0)
            {
                // 使用指数移动平均平滑速度
                smoothedSpeed = speedSmoothingFactor * instantSpeed + (1 - speedSmoothingFactor) * smoothedSpeed;
            }
            
            // 优先使用 Robocopy 输出的百分比（如果可用）
            double currentRobocopyPercent;
            lock (robocopyPercentLock)
            {
                currentRobocopyPercent = robocopyPercent;
            }
            
            if (currentRobocopyPercent > 0)
            {
                // 使用 Robocopy 报告的百分比计算已复制字节数
                displayedBytes = (long)(stats.TotalBytes * currentRobocopyPercent / 100.0);
                
                // 基于 Robocopy 百分比重新计算速度
                if (deltaTime > 0)
                {
                    long bytesFromPercent = displayedBytes - prevBytes;
                    instantSpeed = bytesFromPercent / deltaTime;
                    if (instantSpeed > 0)
                    {
                        if (smoothedSpeed == 0)
                        {
                            smoothedSpeed = instantSpeed;
                        }
                        else
                        {
                            smoothedSpeed = speedSmoothingFactor * instantSpeed + (1 - speedSmoothingFactor) * smoothedSpeed;
                        }
                    }
                }
            }
            else
            {
                // Fallback: 通过速度累加来更新显示的字节数
                // 这样可以避免文件预分配导致的瞬间跳跃
                if (deltaTime > 0 && smoothedSpeed > 0)
                {
                    long speedBasedIncrement = (long)(smoothedSpeed * deltaTime);
                    displayedBytes += speedBasedIncrement;
                    
                    // 确保显示值不超过实际值（边界保护）
                    if (displayedBytes > actualCopiedBytes)
                    {
                        displayedBytes = actualCopiedBytes;
                    }
                }
                else if (displayedBytes == 0 && actualCopiedBytes > 0)
                {
                    // 初始情况：如果还没有速度数据，但已经有复制的字节，使用一个小的初始值
                    displayedBytes = Math.Min(actualCopiedBytes, stats.TotalBytes / 100); // 最多显示1%
                }
            }
            
            // 确保显示值不超过总大小
            displayedBytes = Math.Min(displayedBytes, stats.TotalBytes);

            // 使用平滑后的值计算进度
            long copiedBytes = displayedBytes;
            double speed = smoothedSpeed;

            // 复制阶段占10%-90%，即80%的总进度
            double copyPercent = stats.TotalBytes > 0 ? Math.Min(100, (copiedBytes * 100.0) / stats.TotalBytes) : 0;
            double percent = 10 + (copyPercent * 0.8);  // 映射到10-90%

            TimeSpan? eta = null;
            if (speed > 0 && stats.TotalBytes > 0)
            {
                long remainingBytes = Math.Max(0, stats.TotalBytes - copiedBytes);
                int etaSeconds = (int)Math.Ceiling(remainingBytes / speed);
                eta = TimeSpan.FromSeconds(etaSeconds);
            }

            // 构建状态消息
            string statusMessage;
            if (noChangeCount >= maxNoChangeCount && speed < 1024) // 速度小于1KB/s
            {
                statusMessage = $"{percent:F1}% | {FileStatsService.FormatBytes(copiedBytes)} / {FileStatsService.FormatBytes(stats.TotalBytes)} | 正在处理...";
            }
            else
            {
                statusMessage = $"{percent:F1}% | {FileStatsService.FormatBytes(copiedBytes)} / {FileStatsService.FormatBytes(stats.TotalBytes)} | {FileStatsService.FormatSpeed(speed)}";
            }

            var migrationProgress = new MigrationProgress
            {
                PercentComplete = percent,
                CopiedBytes = copiedBytes,
                TotalBytes = stats.TotalBytes,
                SpeedBytesPerSecond = speed,
                EstimatedTimeRemaining = eta,
                CurrentPhase = 3,
                PhaseDescription = "复制文件",
                Message = statusMessage
            };

            progress?.Report(migrationProgress);

#if DEBUG
            // Debug 模式下输出详细的进度调试信息
            System.Diagnostics.Debug.WriteLine(
                $"[Progress] " +
                $"Actual: {FileStatsService.FormatBytes(actualCopiedBytes)}, " +
                $"Displayed: {FileStatsService.FormatBytes(displayedBytes)}, " +
                $"Delta: {FileStatsService.FormatBytes(actualDeltaBytes)}, " +
                $"Speed: {FileStatsService.FormatSpeed(speed)}, " +
                $"Percent: {percent:F1}%, " +
                $"NoChange: {noChangeCount}");
#endif

            prevBytes = actualCopiedBytes;
            prevTime = elapsed;
        }

        await process.WaitForExitAsync(cancellationToken);

        // Robocopy 退出码 0-7 为成功
        if (process.ExitCode >= 8)
        {
            throw new InvalidOperationException($"Robocopy 复制失败，退出码: {process.ExitCode}");
        }

        // 验证复制完整性
        long finalSize = FileStatsService.GetDirectorySize(_config.TargetPath);
        if (stats.TotalBytes > 0)
        {
            double ratio = (double)finalSize / stats.TotalBytes;
            if (ratio < 0.98)
            {
                logProgress?.Report($"⚠️ 警告: 目标大小仅为源的 {ratio:P1}，请确认复制是否完整");
            }
        }

        // 报告复制完成进度（90%）
        progress?.Report(new MigrationProgress
        {
            CurrentPhase = 3,
            PhaseDescription = "复制文件",
            PercentComplete = 90,
            CopiedBytes = finalSize,
            TotalBytes = stats.TotalBytes,
            SpeedBytesPerSecond = 0,
            Message = $"复制完成: {FileStatsService.FormatBytes(finalSize)}"
        });

        logProgress?.Report($"复制完成，最终大小: {FileStatsService.FormatBytes(finalSize)}");
    }

    private async Task CreateSymbolicLinkAsync(IProgress<string>? logProgress)
    {
        await Task.Run(() =>
        {
            // 创建备份
            string parent = Path.GetDirectoryName(_config.SourcePath) ?? throw new InvalidOperationException("无法解析源目录父路径");
            string name = Path.GetFileName(_config.SourcePath);
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            _backupPath = Path.Combine(parent, $"{name}.bak_{timestamp}");

            logProgress?.Report($"备份源目录到: {_backupPath}");
            Directory.Move(_config.SourcePath, _backupPath);

            // 创建符号链接
            logProgress?.Report($"创建符号链接: {_config.SourcePath} -> {_config.TargetPath}");

            // 优先使用 P/Invoke 方法
            bool success = SymbolicLinkHelper.CreateDirectorySymbolicLink(_config.SourcePath, _config.TargetPath);

            if (!success)
            {
                // 备选: 使用 cmd mklink
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

    private async Task RollbackAsync(IProgress<string>? logProgress)
    {
        if (string.IsNullOrEmpty(_backupPath))
            return;

        await Task.Run(() =>
        {
            logProgress?.Report("开始回滚...");

            try
            {
                // 删除符号链接
                if (Directory.Exists(_config.SourcePath))
                {
                    if (SymbolicLinkHelper.IsSymbolicLink(_config.SourcePath))
                    {
                        Directory.Delete(_config.SourcePath, false);
                    }
                }

                // 还原备份
                if (Directory.Exists(_backupPath))
                {
                    Directory.Move(_backupPath, _config.SourcePath);
                    logProgress?.Report("✅ 已回滚至迁移前状态");
                }
            }
            catch (Exception ex)
            {
                logProgress?.Report($"❌ 回滚失败: {ex.Message}");
                throw;
            }
        });
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
}

