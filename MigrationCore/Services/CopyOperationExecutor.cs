using System.Diagnostics;
using System.Globalization;
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
            // 注意：不使用 /NFL（会禁止文件列表输出，导致无法解析文件大小）
            // 注意：不使用 /NDL（会禁止目录列表输出）
            // 注意：不使用 /NP（会禁止百分比输出，导致无法解析进度）
            // 为了准确的进度显示，我们需要保留这些输出
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
        double robocopyLastPercent = 0;
        long robocopyCurrentFileSize = 0;
        long robocopyCumulativeBytes = 0;
        bool hasFileContext = false; // 是否已经获取到有效文件上下文（解析到文件行或有累计字节）
        bool isFirstFile = true; // 是否是第一个文件
        object robocopyPercentLock = new object();

        // 使用信号量确保日志读取任务已启动
        var logReaderStarted = new TaskCompletionSource<bool>();

        // 异步读取并打印 Robocopy 日志（同时解析百分比）
        var logReaderTask = Task.Run(async () =>
        {
            try
            {
                // 标记日志读取任务已启动
                logReaderStarted.TrySetResult(true);
                
                while (!process.StandardOutput.EndOfStream)
                {
                    string? line = await process.StandardOutput.ReadLineAsync();
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        string trimmed = line.Trim();
                        
#if DEBUG
                        logProgress?.Report($"[Robocopy-{_actionName}] {line}");
                        System.Diagnostics.Debug.WriteLine($"[Robocopy-{_actionName}] {line}");
#else
                        // Release 模式下只输出关键信息（新文件、百分比、错误等）
                        if (trimmed.Contains("新文件") || 
                            trimmed.Contains("多余文件") ||
                            (trimmed.EndsWith("%") && trimmed.Length <= 5) ||
                            trimmed.Contains("错误") ||
                            trimmed.Contains("失败"))
                        {
                            logProgress?.Report($"[Robocopy-{_actionName}] {line}");
                        }
#endif
                        // 解析 Robocopy 的百分比输出（如 "  18%"）

                        if (TryParseRobocopyNewFileLine(trimmed, out long newFileSize))
                        {
                            lock (robocopyPercentLock)
                            {
                                // 只有不是第一个文件时，才累计上一个文件
                                if (!isFirstFile && robocopyCurrentFileSize > 0)
                                {
                                    // 将上一个文件按最后记录的百分比计入累计值
                                    // 避免把未完成的文件按100%累加导致进度跳跃
                                    if (robocopyLastPercent > 0)
                                    {
                                        long completedBytes = (long)Math.Round(robocopyCurrentFileSize * robocopyLastPercent / 100.0);
                                        completedBytes = Math.Max(0, Math.Min(completedBytes, robocopyCurrentFileSize));
                                        robocopyCumulativeBytes += completedBytes;
#if DEBUG
                                        System.Diagnostics.Debug.WriteLine($"[Robocopy-{_actionName}] 累计上一个文件: {FileStatsService.FormatBytes(completedBytes)} (百分比: {robocopyLastPercent}%), 累计总计: {FileStatsService.FormatBytes(robocopyCumulativeBytes)}");
#endif
                                        logProgress?.Report($"[文件完成] 累计: {FileStatsService.FormatBytes(robocopyCumulativeBytes)}");
                                    }
                                    else
                                    {
                                        // 如果没有百分比记录，假定上一个文件已完成
                                        robocopyCumulativeBytes += robocopyCurrentFileSize;
#if DEBUG
                                        System.Diagnostics.Debug.WriteLine($"[Robocopy-{_actionName}] 累计上一个文件(无%): {FileStatsService.FormatBytes(robocopyCurrentFileSize)}, 累计总计: {FileStatsService.FormatBytes(robocopyCumulativeBytes)}");
#endif
                                        logProgress?.Report($"[文件完成] 累计: {FileStatsService.FormatBytes(robocopyCumulativeBytes)}");
                                    }
                                }

                                robocopyCurrentFileSize = newFileSize;
                                robocopyPercent = 0;
                                robocopyLastPercent = 0;
                                hasFileContext = true; // 已获取到文件上下文
                                
#if DEBUG
                                System.Diagnostics.Debug.WriteLine($"[Robocopy-{_actionName}] 开始新文件: {FileStatsService.FormatBytes(newFileSize)}, 是否首个: {isFirstFile}");
#endif
                                logProgress?.Report($"[新文件] 大小: {FileStatsService.FormatBytes(newFileSize)}, 是否首个: {isFirstFile}");
                                
                                isFirstFile = false; // 标记已经不是第一个文件了
                            }

                            continue;
                        }

                        if (trimmed.EndsWith("%") && trimmed.Length <= 5)
                        {
                            // 尝试解析百分比
                            string percentStr = trimmed.TrimEnd('%').Trim();
                            if (double.TryParse(percentStr, out double percent))
                            {
                                lock (robocopyPercentLock)
                                {
                                    double previousPercent = robocopyLastPercent;

                                    // 检测到百分比回退（可能是新文件开始，但没捕获到"新文件"行）
                                    if (percent + 1 < previousPercent && robocopyCurrentFileSize > 0)
                                    {
                                        // 按上一个百分比累计，而不是整个文件
                                        long completedBytes = (long)Math.Round(robocopyCurrentFileSize * previousPercent / 100.0);
                                        completedBytes = Math.Max(0, Math.Min(completedBytes, robocopyCurrentFileSize));
                                        robocopyCumulativeBytes += completedBytes;
#if DEBUG
                                        System.Diagnostics.Debug.WriteLine($"[Robocopy-{_actionName}] 检测到%回退，累计: {FileStatsService.FormatBytes(completedBytes)}, 总计: {FileStatsService.FormatBytes(robocopyCumulativeBytes)}");
#endif
                                        logProgress?.Report($"[百分比回退] 累计: {FileStatsService.FormatBytes(robocopyCumulativeBytes)}");
                                        robocopyCurrentFileSize = 0;
                                    }

                                    robocopyLastPercent = percent;
                                    robocopyPercent = percent;
                                }
#if DEBUG
                                System.Diagnostics.Debug.WriteLine($"[Robocopy Percent] {percent}%, 当前文件: {FileStatsService.FormatBytes(robocopyCurrentFileSize)}, 累计: {FileStatsService.FormatBytes(robocopyCumulativeBytes)}");
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

        // 等待日志读取任务启动，避免漏读开始的几行日志
        await logReaderStarted.Task.ConfigureAwait(false);
        
        // 再等待一小段时间，确保 StandardOutput 管道已经准备好
        await Task.Delay(50, cancellationToken).ConfigureAwait(false);

        var stopwatch = Stopwatch.StartNew();
        long prevBytes = 0;
        long prevReportedBytes = 0;
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
            long currentRobocopyFileSize;
            long currentRobocopyCumulative;
            bool currentHasFileContext;

            lock (robocopyPercentLock)
            {
                currentRobocopyPercent = robocopyPercent;
                currentRobocopyFileSize = robocopyCurrentFileSize;
                currentRobocopyCumulative = robocopyCumulativeBytes;
                currentHasFileContext = hasFileContext;
                
                // 一旦有累计字节，说明至少完成了一个文件的部分或全部，标记为有上下文
                if (!hasFileContext && robocopyCumulativeBytes > 0)
                {
                    hasFileContext = true;
                    currentHasFileContext = true;
                }
            }

            double clampedPercent = Math.Min(100, Math.Max(currentRobocopyPercent, 0));
            long bytesFromRobocopy = currentRobocopyCumulative;
            
            // 只有在有文件上下文时才使用 Robocopy 估算
            bool hasRobocopyEstimate = currentHasFileContext && (currentRobocopyCumulative > 0 || currentRobocopyFileSize > 0 || clampedPercent > 0);

            if (currentRobocopyFileSize > 0 && clampedPercent >= 0)
            {
                long partial = (long)Math.Round(currentRobocopyFileSize * clampedPercent / 100.0);
                partial = Math.Max(0, Math.Min(partial, currentRobocopyFileSize));
                bytesFromRobocopy += partial;
            }

            if (hasRobocopyEstimate && bytesFromRobocopy > 0)
            {
                bytesFromRobocopy = Math.Min(bytesFromRobocopy, _stats.TotalBytes);

                if (deltaTime > 0)
                {
                    long bytesFromPercent = Math.Max(0, bytesFromRobocopy - prevReportedBytes);
                    if (bytesFromPercent > 0)
                    {
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

                // 使用 Robocopy 估算值，确保单调递增
                displayedBytes = Math.Max(displayedBytes, bytesFromRobocopy);
                
                // 不使用 actualCopiedBytes，避免文件预分配导致进度跳跃
                displayedBytes = Math.Min(displayedBytes, _stats.TotalBytes);
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
                    // 注意：由于文件预分配，actualCopiedBytes 可能虚高，这里仅作为上限
                    if (displayedBytes > actualCopiedBytes)
                    {
                        displayedBytes = actualCopiedBytes;
                    }
                }
                else if (displayedBytes == 0 && actualCopiedBytes > 0)
                {
                    // 初始情况：不使用 actualCopiedBytes，避免预分配导致跳跃
                    // 保持为0，等待速度数据或 Robocopy 上下文
                    displayedBytes = 0;
                }
                
                // 在没有 Robocopy 估算时，确保不超过总大小
                displayedBytes = Math.Min(displayedBytes, _stats.TotalBytes);
            }

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
            prevReportedBytes = copiedBytes;
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

    private static bool TryParseRobocopyNewFileLine(string line, out long fileSize)
    {
        fileSize = 0;

        const string marker = "新文件";
        int markerIndex = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return false;

        string afterMarker = line.Substring(markerIndex + marker.Length).Trim();
        if (string.IsNullOrEmpty(afterMarker))
            return false;

        var tokens = afterMarker.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return false;

#if DEBUG
        System.Diagnostics.Debug.WriteLine($"[ParseNewFile] 原始行: {line}");
        System.Diagnostics.Debug.WriteLine($"[ParseNewFile] afterMarker: '{afterMarker}'");
        System.Diagnostics.Debug.WriteLine($"[ParseNewFile] tokens数量: {tokens.Length}, tokens[0]: '{tokens[0]}'");
#endif

        string sizeToken = tokens[0];
        string unitToken = string.Empty;

        // 处理例如 1.4g / 1.4GB 的格式
        int lastAlphaIndex = sizeToken.Length - 1;
        while (lastAlphaIndex >= 0 && char.IsLetter(sizeToken[lastAlphaIndex]))
        {
            lastAlphaIndex--;
        }

        if (lastAlphaIndex < sizeToken.Length - 1)
        {
            unitToken = sizeToken.Substring(lastAlphaIndex + 1);
            sizeToken = sizeToken.Substring(0, lastAlphaIndex + 1);
        }

        if (string.IsNullOrWhiteSpace(unitToken) && tokens.Length >= 2 && IsSizeUnitToken(tokens[1]))
        {
            unitToken = tokens[1];
        }

        sizeToken = sizeToken.Replace(",", string.Empty).Trim();

        if (!double.TryParse(sizeToken, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            if (!double.TryParse(sizeToken, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            {
                return false;
            }
        }

        long multiplier = GetSizeMultiplier(unitToken);
        double rawBytes = value * multiplier;
        if (double.IsNaN(rawBytes) || double.IsInfinity(rawBytes))
            return false;

        fileSize = (long)Math.Max(0, Math.Round(rawBytes));
        return true;
    }

    private static bool IsSizeUnitToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        token = token.Trim();
        if (token.IndexOfAny(new[] { ':', '\\', '/' }) >= 0)
            return false;

        token = token.ToUpperInvariant();
        return token is "B" or "K" or "KB" or "M" or "MB" or "G" or "GB" or "T" or "TB" or "P" or "PB";
    }

    private static long GetSizeMultiplier(string unit)
    {
        if (string.IsNullOrWhiteSpace(unit))
            return 1;

        return unit.Trim().ToUpperInvariant() switch
        {
            "B" => 1,
            "K" or "KB" => 1024L,
            "M" or "MB" => 1024L * 1024,
            "G" or "GB" => 1024L * 1024 * 1024,
            "T" or "TB" => 1024L * 1024 * 1024 * 1024,
            "P" or "PB" => 1024L * 1024 * 1024 * 1024 * 1024,
            _ => 1
        };
    }
}

