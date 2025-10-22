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
            // 验证源路径
            var (isValidSource, sourceError, sourceWarning) = PathValidator.ValidateSourcePath(_config.SourcePath);
            if (!isValidSource)
                throw new InvalidOperationException(sourceError);

            if (sourceWarning != null)
                logProgress?.Report($"⚠️ {sourceWarning}");

            // 验证目标路径
            var (isValidTarget, targetError) = PathValidator.ValidateTargetPath(_config.TargetPath);
            if (!isValidTarget)
                throw new InvalidOperationException(targetError);

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
            "/NFL",
            "/NDL",
            "/NP",
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

        // 监控复制进度
        var stopwatch = Stopwatch.StartNew();
        long prevBytes = 0;
        TimeSpan prevTime = TimeSpan.Zero;

        while (!process.HasExited)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                process.Kill();
                throw new OperationCanceledException("用户取消操作");
            }

            await Task.Delay(_config.SampleMilliseconds, cancellationToken);

            long copiedBytes = FileStatsService.GetDirectorySize(_config.TargetPath);
            TimeSpan elapsed = stopwatch.Elapsed;

            long deltaBytes = Math.Max(0, copiedBytes - prevBytes);
            double deltaTime = (elapsed - prevTime).TotalSeconds;
            double speed = deltaTime > 0 ? deltaBytes / deltaTime : 0;

            double percent = stats.TotalBytes > 0 ? Math.Min(100, (copiedBytes * 100.0) / stats.TotalBytes) : 0;

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
                PhaseDescription = "复制文件",
                Message = $"{percent:F1}% | {FileStatsService.FormatBytes(copiedBytes)} / {FileStatsService.FormatBytes(stats.TotalBytes)} | {FileStatsService.FormatSpeed(speed)}"
            };

            progress?.Report(migrationProgress);

            prevBytes = copiedBytes;
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

        progress?.Report(new MigrationProgress
        {
            CurrentPhase = phase,
            PhaseDescription = description,
            PercentComplete = (phase - 1) * 100.0 / 6,
            Message = description
        });
    }
}

