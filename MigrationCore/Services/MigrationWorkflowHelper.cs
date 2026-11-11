using MigrationCore.Models;

namespace MigrationCore.Services;

/// <summary>
/// 迁移流程的公共帮助类，封装符号链接创建、验证、备份清理等通用逻辑
/// 
/// 该类提取自 MigrationService 和 ReversibleMigrationService 的重复代码，
/// 将符号链接操作、备份管理、目录扫描等通用流程抽取为静态方法。
/// 
/// 主要功能：
/// - 创建符号链接并备份源目录
/// - 验证符号链接有效性
/// - 清理备份目录
/// - 回滚迁移操作（删除符号链接、恢复备份）
/// - 目录扫描与结果报告
/// 
/// 使用此类可确保 MigrationService 和 ReversibleMigrationService 
/// 使用完全一致的符号链接和备份逻辑，便于维护和测试。
/// </summary>
public static class MigrationWorkflowHelper
{
    public static async Task<MigrationTransactionRecord> PrepareTransactionAsync(MigrationConfig config,
        MigrationMode mode, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        config.SourcePath = PathIdentityService.Normalize(config.SourcePath);
        config.TargetPath = PathIdentityService.Normalize(config.TargetPath);
        MigrationRuntime.AssertDataPath(config.SourcePath, allowLink: mode == MigrationMode.Restore);
        MigrationRuntime.AssertDataPath(config.TargetPath);
        var relation = PathValidator.ValidatePathRelation(config.SourcePath, config.TargetPath);
        if (!relation.IsValid) throw new IOException(relation.Error);
        var targetAdmission = PathValidator.ValidateTargetPath(config.TargetPath);
        if (!targetAdmission.IsValid) throw new IOException(targetAdmission.Error);
        var sourceAdmission = PathValidator.ValidateSourcePath(mode == MigrationMode.Restore ? config.TargetPath : config.SourcePath);
        if (!sourceAdmission.IsValid) throw new IOException(sourceAdmission.Error);
        var identity = new PathIdentityService();
        DirectoryIdentity sourceIdentity = identity.ReadDirectory(config.SourcePath, false);
        var store = new MigrationTransactionStore();
        if (mode == MigrationMode.Restore)
        {
            if (!sourceIdentity.IsReparsePoint) throw new IOException("还原源不是已登记的链接。");
            DirectoryIdentity targetIdentity = identity.ReadDirectory(config.TargetPath, false);
            if (targetIdentity.IsReparsePoint) throw new IOException("还原目标不是普通实体目录。");
            MigrationTransactionRecord record = (await store.ReadAllAsync(ct).ConfigureAwait(false))
                .SingleOrDefault(r => r.SourcePath.Equals(config.SourcePath, StringComparison.OrdinalIgnoreCase)
                    && r.TargetPath.Equals(config.TargetPath, StringComparison.OrdinalIgnoreCase)
                    && r.LinkIdentity == sourceIdentity && r.TargetIdentity == targetIdentity
                    && r.Stage == MigrationStage.AwaitingUserVerification)
                ?? throw new IOException("没有身份匹配的可还原事务，不能按目录名认领。");
            identity.RequireSame(config.SourcePath, targetIdentity, true);
            if (record.BackupIdentity is not null && record.BackupState is not (BackupState.Deleted or BackupState.Recycled))
                identity.RequireSame(record.BackupPath ?? throw new IOException("备份路径缺失。"), record.BackupIdentity);
            return await store.SaveAsync(record with
            {
                Mode = MigrationMode.Restore, Stage = MigrationStage.Prepared,
                ProbePath = config.TargetPath + "_lock_test_" + record.TransactionId.ToString("D"),
                VerificationSummary = null, UserVerifiedAtUtc = null, DeletionRequest = null
            }, record.Revision, ct).ConfigureAwait(false);
        }
        if (mode != MigrationMode.Migrate || sourceIdentity.IsReparsePoint)
            throw new IOException("迁移源必须是普通实体目录。");
        DirectoryIdentity? target = identity.ReadIfExists(config.TargetPath);
        if (target is not null && (target.IsReparsePoint || !target.IsDirectory
            || Directory.EnumerateFileSystemEntries(config.TargetPath).Any()))
            throw new IOException("目标非空或归属不明，不得覆盖或自动改用其他路径。");
        string parent = Path.GetDirectoryName(config.TargetPath) ?? throw new IOException("目标父路径缺失。");
        identity.EnsureOrdinaryDirectory(parent);
        var prepared = new MigrationTransactionRecord
        {
            Mode = mode, SourcePath = config.SourcePath, TargetPath = config.TargetPath,
            SourceIdentity = sourceIdentity, TargetParentIdentity = identity.ReadDirectory(parent, false),
            ReservedEmptyTargetIdentity = target, VerificationMode = config.VerificationMode
        };
        prepared = prepared with { ProbePath = prepared.SourcePath + "_lock_test_" + prepared.TransactionId.ToString("D") };
        return await store.CreateAsync(prepared, ct).ConfigureAwait(false);
    }

    public static async Task<DirectoryProbeResult> ProbeAsync(MigrationConfig config, MigrationMode mode,
        IProgress<string>? log, CancellationToken ct)
    {
        MigrationTransactionRecord? record = null;
        try
        {
            record = await PrepareTransactionAsync(config, mode, ct).ConfigureAwait(false);
            return mode == MigrationMode.Restore
                ? await SimpleFileLockDetector.CheckRestoreAsync(record, log, ct).ConfigureAwait(false)
                : await SimpleFileLockDetector.CheckMigrationAsync(record, log, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            string actual = mode == MigrationMode.Restore ? config.TargetPath : config.SourcePath;
            bool recovery = false;
            string error = ex.Message;
            if (record is not null)
            {
                try
                {
                    var identity = new PathIdentityService();
                    DirectoryIdentity? expected = mode == MigrationMode.Restore ? record.TargetIdentity : record.SourceIdentity;
                    if (record.ProbePath is not null && identity.ReadIfExists(record.ProbePath) == expected)
                    {
                        actual = record.ProbePath;
                        recovery = true;
                    }
                    else identity.RequireSame(actual, expected);
                }
                catch (Exception identityError)
                {
                    recovery = true;
                    error += $"；实际位置无法完全确认：{identityError.Message}";
                }
            }
            return new DirectoryProbeResult
            {
                CanProceed = false, RecoveryRequired = recovery, ActualDataPath = actual,
                Transaction = record, Error = error
            };
        }
    }

    public static void ApplyProbeResult(MigrationResult result, DirectoryProbeResult probe)
    {
        result.TransactionId = probe.Transaction?.TransactionId;
        result.Stage = probe.RecoveryRequired ? MigrationStage.RecoveryRequired
            : probe.CanProceed ? MigrationStage.Prepared
            : probe.Transaction?.Stage == MigrationStage.Cancelled ? MigrationStage.Cancelled : MigrationStage.Failed;
        result.BackupPath = probe.Transaction?.BackupPath;
        result.BackupState = probe.Transaction?.BackupState;
        result.RollbackOutcome = probe.RecoveryRequired ? RollbackOutcome.RecoveryRequired : RollbackOutcome.NotRequired;
        result.RecoveryPaths = probe.CanProceed ? [] : [probe.ActualDataPath];
        result.ErrorMessage = probe.Error;
    }
    public static void ApplyCopyResult(MigrationResult result, CopyExecutionResult copy)
    {
        result.TransactionId = copy.Transaction.TransactionId;
        result.Stage = copy.RecoveryRequired ? MigrationStage.RecoveryRequired : copy.Cancelled ? MigrationStage.Cancelled : copy.Transaction.Stage;
        result.VerificationSummary = copy.Verification;
        result.BackupPath = copy.Transaction.BackupPath;
        result.BackupState = copy.Transaction.BackupState;
        result.ErrorMessage = copy.Error;
        result.RollbackOutcome = copy.RecoveryRequired ? RollbackOutcome.RecoveryRequired : RollbackOutcome.NotRequired;
        result.RecoveryPaths = copy.Success || copy.ActualDataPath.Length == 0 ? [] : [copy.ActualDataPath];
    }


    /// <summary>
    /// 创建符号链接（包含备份源目录的操作）
    /// </summary>
    /// <param name="sourcePath">源路径</param>
    /// <param name="targetPath">目标路径</param>
    /// <param name="logProgress">日志进度回调</param>
    /// <returns>备份路径</returns>
    public static async Task<string> CreateSymbolicLinkWithBackupAsync(
        string sourcePath,
        string targetPath,
        IProgress<string>? logProgress = null)
    {
        return await Task.Run(() =>
        {
            // 创建备份
            string parent = Path.GetDirectoryName(sourcePath) ?? throw new InvalidOperationException("无法解析源目录父路径");
            string name = Path.GetFileName(sourcePath);
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string backupPath = Path.Combine(parent, $"{name}.bak_{timestamp}");

            logProgress?.Report($"备份源目录到: {backupPath}");
            Directory.Move(sourcePath, backupPath);

            // 创建符号链接
            logProgress?.Report($"创建符号链接: {sourcePath} -> {targetPath}");

            // 优先使用 P/Invoke 方法
            bool success = SymbolicLinkHelper.CreateDirectorySymbolicLink(sourcePath, targetPath);

            if (!success)
            {
                // 备选: 使用 cmd mklink
                logProgress?.Report("P/Invoke 失败，尝试使用 cmd mklink...");
                success = SymbolicLinkHelper.CreateSymbolicLinkViaCmdAsync(sourcePath, targetPath, out string error);

                if (!success)
                {
                    throw new InvalidOperationException($"创建符号链接失败: {error}");
                }
            }

            if (!Directory.Exists(sourcePath))
            {
                throw new InvalidOperationException("符号链接创建后无法访问");
            }

            return backupPath;
        });
    }

    /// <summary>
    /// 验证符号链接是否正确创建
    /// </summary>
    public static async Task VerifySymbolicLinkAsync(
        string symlinkPath,
        IProgress<string>? logProgress = null)
    {
        await Task.Run(() =>
        {
            logProgress?.Report("验证符号链接...");

            if (!SymbolicLinkHelper.IsSymbolicLink(symlinkPath))
            {
                throw new InvalidOperationException("创建的对象不是符号链接（重解析点）");
            }

            if (!Directory.Exists(symlinkPath))
            {
                throw new InvalidOperationException("符号链接无法访问");
            }

            logProgress?.Report("✅ 符号链接验证成功");
        });
    }

    /// <summary>
    /// 清理备份目录
    /// </summary>
    public static async Task CleanupBackupAsync(
        string? backupPath,
        IProgress<string>? logProgress = null)
    {
        if (string.IsNullOrEmpty(backupPath) || !Directory.Exists(backupPath))
            return;

        await Task.Run(() =>
        {
            try
            {
                logProgress?.Report($"清理备份目录: {backupPath}");
                Directory.Delete(backupPath, true);
                logProgress?.Report("✅ 备份已清理");
            }
            catch (Exception ex)
            {
                logProgress?.Report($"⚠️ 清理备份失败: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// 回滚迁移操作（删除符号链接，恢复备份）
    /// </summary>
    public static async Task RollbackMigrationAsync(
        string sourcePath,
        string targetPath,
        string? backupPath,
        IProgress<string>? logProgress = null)
    {
        if (string.IsNullOrEmpty(backupPath))
            return;

        await Task.Run(() =>
        {
            logProgress?.Report("开始回滚...");

            try
            {
                // 删除符号链接
                if (Directory.Exists(sourcePath))
                {
                    if (SymbolicLinkHelper.IsSymbolicLink(sourcePath))
                    {
                        Directory.Delete(sourcePath, false);
                    }
                }

                // 还原备份
                if (Directory.Exists(backupPath))
                {
                    Directory.Move(backupPath, sourcePath);
                    logProgress?.Report("✅ 已回滚至迁移前状态");
                }

                // 清理迁移标记
                MigrationStateDetector.DeleteMigrateMarkers(targetPath);
            }
            catch (Exception ex)
            {
                logProgress?.Report($"❌ 回滚失败: {ex.Message}");
                throw;
            }
        });
    }

    /// <summary>
    /// 扫描目录并获取统计信息
    /// </summary>
    public static async Task<FileStats> ScanDirectoryAsync(
        string directoryPath,
        long largeFileThresholdBytes,
        IProgress<string>? logProgress = null,
        CancellationToken cancellationToken = default)
    {
        logProgress?.Report($"正在扫描: {directoryPath}");

        var scanProgress = new Progress<string>(msg => logProgress?.Report(msg));
        var stats = await FileStatsService.ScanDirectoryAsync(directoryPath, largeFileThresholdBytes, scanProgress, cancellationToken);

        return stats;
    }

    /// <summary>
    /// 报告扫描结果
    /// </summary>
    public static void ReportScanResults(
        FileStats stats,
        int largeFileThresholdMB,
        IProgress<string>? logProgress = null)
    {
        logProgress?.Report($"总文件数: {stats.TotalFiles}");
        logProgress?.Report($"总大小: {FileStatsService.FormatBytes(stats.TotalBytes)}");
        logProgress?.Report($"大文件 (≥{largeFileThresholdMB}MB): {stats.LargeFiles} 个");
    }
}

