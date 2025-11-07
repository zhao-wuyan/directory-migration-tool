using System.Diagnostics;
using MigrationCore.Models;

namespace MigrationCore.Services;

/// <summary>
/// 封装文件复制操作的执行器，包含进度监控、速度平滑等逻辑
/// 
/// 该类提取自 MigrationService 和 ReversibleMigrationService 的重复代码，
/// 统一管理 robocopy 进程调用、输出解析、进度计算和速度平滑算法。
/// 
/// 主要功能：
/// - Robocopy 进程启动与参数配置
/// - 实时解析 robocopy 百分比输出
/// - 目录大小监控与增量计算
/// - 指数移动平均速度平滑
/// - 10%-90% 进度映射
/// - 取消操作与进程树终止
/// </summary>
public class CopyOperationExecutor
{
    private readonly string _sourceDir;
    private readonly string _targetDir;
    private readonly FileStats _stats;
    private readonly int _robocopyThreads;
    private readonly int _sampleMilliseconds;
    private readonly string _actionName;

    public CopyOperationExecutor(
        string sourceDir,
        string targetDir,
        FileStats stats,
        int robocopyThreads,
        int sampleMilliseconds,
        string actionName = "复制")
    {
        _sourceDir = sourceDir;
        _targetDir = targetDir;
        _stats = stats;
        _robocopyThreads = robocopyThreads;
        _sampleMilliseconds = sampleMilliseconds;
        _actionName = actionName;
    }

    /// <summary>
    /// 执行复制操作，并通过回调报告进度
    /// </summary>
    public async Task ExecuteAsync(
        IProgress<MigrationProgress>? progress,
        IProgress<string>? logProgress,
        CancellationToken cancellationToken)
    {
        logProgress?.Report($"开始{_actionName}文件 (robocopy)...");

        progress?.Report(new MigrationProgress
        {
            CurrentPhase = 3,
            PhaseDescription = $"{_actionName}文件",
            PercentComplete = 10,
            CopiedBytes = 0,
            TotalBytes = _stats.TotalBytes,
            SpeedBytesPerSecond = 0,
            Message = $"准备{_actionName} {FileStatsService.FormatBytes(_stats.TotalBytes)}..."
        });

        var robocopyArgs = new List<string>
        {
            $"\"{_sourceDir}\"",
            $"\"{_targetDir}\"",
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
            "/Z",  // 可续传模式
            "/ZB", // 回退到备份模式
            $"/MT:{_robocopyThreads}"
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
                        logProgress?.Report($"[Robocopy-{_actionName}] {line}");
                        System.Diagnostics.Debug.WriteLine($"[Robocopy-{_actionName}] {line}");
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
                System.Diagnostics.Debug.WriteLine($"[Robocopy-{_actionName} Log Error] {ex.Message}");
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
                        logProgress?.Report($"[Robocopy-{_actionName} Error] {line}");
                        System.Diagnostics.Debug.WriteLine($"[Robocopy-{_actionName} Error] {line}");
#endif
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Robocopy-{_actionName} Error Log Error] {ex.Message}");
            }
        });

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
                
                // 等待进程真正退出，避免文件占用问题
                try
                {
                    if (!process.HasExited)
                    {
                        process.WaitForExit(3000); // 最多等待3秒
                    }
                }
                catch { }
                
                logProgress?.Report("robocopy 进程已终止");
                throw new OperationCanceledException("用户取消操作");
            }

            await Task.Delay(_sampleMilliseconds, cancellationToken);

            long actualCopiedBytes = FileStatsService.GetDirectorySize(_targetDir);
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
                displayedBytes = (long)(_stats.TotalBytes * currentRobocopyPercent / 100.0);
                
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
                    displayedBytes = Math.Min(actualCopiedBytes, _stats.TotalBytes / 100); // 最多显示1%
                }
            }
            
            // 确保显示值不超过总大小
            displayedBytes = Math.Min(displayedBytes, _stats.TotalBytes);

            // 使用平滑后的值计算进度
            long copiedBytes = displayedBytes;
            double speed = smoothedSpeed;

            double copyPercent = _stats.TotalBytes > 0 ? Math.Min(100, (copiedBytes * 100.0) / _stats.TotalBytes) : 0;
            double percent = 10 + (copyPercent * 0.8);

            TimeSpan? eta = null;
            if (speed > 0 && _stats.TotalBytes > 0)
            {
                long remainingBytes = Math.Max(0, _stats.TotalBytes - copiedBytes);
                int etaSeconds = (int)Math.Ceiling(remainingBytes / speed);
                eta = TimeSpan.FromSeconds(etaSeconds);
            }

            // 构建状态消息
            string statusMessage;
            if (noChangeCount >= maxNoChangeCount && speed < 1024) // 速度小于1KB/s
            {
                statusMessage = $"{percent:F1}% | {FileStatsService.FormatBytes(copiedBytes)} / {FileStatsService.FormatBytes(_stats.TotalBytes)} | 正在处理...";
            }
            else
            {
                statusMessage = $"{percent:F1}% | {FileStatsService.FormatBytes(copiedBytes)} / {FileStatsService.FormatBytes(_stats.TotalBytes)} | {FileStatsService.FormatSpeed(speed)}";
            }

            var migrationProgress = new MigrationProgress
            {
                PercentComplete = percent,
                CopiedBytes = copiedBytes,
                TotalBytes = _stats.TotalBytes,
                SpeedBytesPerSecond = speed,
                EstimatedTimeRemaining = eta,
                CurrentPhase = 3,
                PhaseDescription = $"{_actionName}文件",
                Message = statusMessage
            };

            progress?.Report(migrationProgress);

#if DEBUG
            // Debug 模式下输出详细的进度调试信息
            System.Diagnostics.Debug.WriteLine(
                $"[Progress-{_actionName}] " +
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

        if (process.ExitCode >= 8)
        {
            throw new InvalidOperationException($"Robocopy {_actionName}失败，退出码: {process.ExitCode}");
        }

        long finalSize = FileStatsService.GetDirectorySize(_targetDir);
        if (_stats.TotalBytes > 0)
        {
            double ratio = (double)finalSize / _stats.TotalBytes;
            if (ratio < 0.98)
            {
                logProgress?.Report($"⚠️ 警告: 目标大小仅为源的 {ratio:P1}，请确认{_actionName}是否完整");
            }
        }

        progress?.Report(new MigrationProgress
        {
            CurrentPhase = 3,
            PhaseDescription = $"{_actionName}文件",
            PercentComplete = 90,
            CopiedBytes = finalSize,
            TotalBytes = _stats.TotalBytes,
            SpeedBytesPerSecond = 0,
            Message = $"{_actionName}完成: {FileStatsService.FormatBytes(finalSize)}"
        });

        logProgress?.Report($"{_actionName}完成，最终大小: {FileStatsService.FormatBytes(finalSize)}");
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

