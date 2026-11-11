using MigrationCore.Models;

namespace MigrationCore.Services;

/// <summary>
/// 共用事务准入、核对、目录切换与保留副本的恢复流程。
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
        if (mode == MigrationMode.Restore)
        {
            var sourceAddress = PathValidator.ValidateTargetPath(config.SourcePath);
            if (!sourceAddress.IsValid) throw new IOException(sourceAddress.Error);
        }
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
                    && (r.Stage == MigrationStage.AwaitingUserVerification
                        || r.Stage is MigrationStage.Cancelled or MigrationStage.Failed))
                ?? throw new IOException("没有身份匹配的可还原事务，不能按目录名认领。");
            identity.RequireSame(config.SourcePath, targetIdentity, true);
            if (record.BackupIdentity is not null && record.BackupState is not (BackupState.Deleted or BackupState.Recycled))
                identity.RequireSame(record.BackupPath ?? throw new IOException("备份路径缺失。"), record.BackupIdentity);
            return await store.SaveAsync(record with
            {
                Mode = MigrationMode.Restore, Stage = MigrationStage.Prepared,
                SourceParentIdentity = record.SourceParentIdentity ?? identity.ReadDirectory(Path.GetDirectoryName(config.SourcePath)!, false),
                ProbePath = config.TargetPath + "_lock_test_" + record.TransactionId.ToString("D"),
                StagingPath = null, StagingIdentity = null, StagingParentIdentity = null,
                VerificationMode = config.VerificationMode,
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
            SourceParentIdentity = identity.ReadDirectory(Path.GetDirectoryName(config.SourcePath)!, false),
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
        result.BackupState = probe.Transaction?.BackupPath is null ? null : probe.Transaction.BackupState;
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
        result.BackupState = copy.Transaction.BackupPath is null ? null : copy.Transaction.BackupState;
        result.ErrorMessage = copy.Error;
        result.RollbackOutcome = copy.RecoveryRequired ? RollbackOutcome.RecoveryRequired : RollbackOutcome.NotRequired;
        result.RecoveryPaths = copy.Success || copy.ActualDataPath.Length == 0 ? [] : [copy.ActualDataPath];
    }

    public static async Task<MigrationResult> ExecuteAsync(MigrationConfig config, MigrationMode mode,
        IProgress<MigrationProgress>? progress, IProgress<string>? log, CancellationToken ct)
    {
        var result = new MigrationResult { SourcePath = config.SourcePath, TargetPath = config.TargetPath };
        MigrationTransactionRecord? record = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
            FileManifest before;
            var verifier = new CopyVerificationService();
            if (mode == MigrationMode.Repair)
            {
                record = await PrepareRepairAsync(config, ct).ConfigureAwait(false);
                before = await verifier.CaptureAsync(record.TargetPath, record.VerificationMode, ct).ConfigureAwait(false);
                var identity = new PathIdentityService();
                DirectoryIdentity? source = identity.ReadIfExists(record.SourcePath);
                if (source is not null && !source.IsReparsePoint && Directory.EnumerateFileSystemEntries(record.SourcePath).Any())
                {
                    before = await verifier.CaptureAsync(record.SourcePath, record.VerificationMode, ct).ConfigureAwait(false);
                    RequireMatch(await verifier.VerifyAsync(before, record.SourcePath, record.TargetPath, record.VerificationMode, ct).ConfigureAwait(false));
                }
                record = await new MigrationTransactionStore().SaveAsync(record with
                {
                    Stage = MigrationStage.CopyVerified,
                    VerificationSummary = new VerificationSummary { Mode = record.VerificationMode, IsMatch = true }
                }, record.Revision, ct).ConfigureAwait(false);
                Publish(record, progress);
            }
            else
            {
                var probe = await ProbeAsync(config, mode, log, ct).ConfigureAwait(false);
                ApplyProbeResult(result, probe);
                if (!probe.CanProceed)
                {
                    if (ct.IsCancellationRequested && !probe.RecoveryRequired) result.Stage = MigrationStage.Cancelled;
                    return result;
                }
                record = probe.Transaction ?? throw new IOException("缺少已保存事务。");
                Publish(record, progress);
                string data = mode == MigrationMode.Restore ? record.TargetPath : record.SourcePath;
                result.Stats = await ScanDirectoryAsync(data, (long)config.LargeFileThresholdMB * 1024 * 1024, log, ct).ConfigureAwait(false);
                ReportScanResults(result.Stats, config.LargeFileThresholdMB, log);
                var copy = await new CopyOperationExecutor(record, result.Stats, config.RobocopyThreads,
                    config.SampleMilliseconds, mode == MigrationMode.Restore ? "还原" : "复制")
                    .ExecuteAsync(progress, log, ct).ConfigureAwait(false);
                record = copy.Transaction;
                ApplyCopyResult(result, copy);
                if (!copy.Success)
                {
                    result.RecoveryPaths = Locations(record);
                    return result;
                }
                before = copy.SourceManifest ?? throw new IOException("缺少独立复制前清单。");
                RequireMatch(await verifier.VerifyAsync(before, data,
                    mode == MigrationMode.Restore ? record.StagingPath! : record.TargetPath,
                    record.VerificationMode, ct).ConfigureAwait(false));
            }
            result.TransactionId = record.TransactionId;
            record = await new MigrationTransactionStore().SaveAsync(record with
            {
                SourceManifestDigest = CopyVerificationService.Fingerprint(before, includeIdentity: true),
                VerifiedManifestDigest = CopyVerificationService.Fingerprint(before, includeIdentity: false)
            }, record.Revision, ct).ConfigureAwait(false);
            Publish(record, progress);
            if (mode == MigrationMode.Restore)
                record = await CompleteRestoreAsync(record, before, progress, ct).ConfigureAwait(false);
            else
            {
                var identity = new PathIdentityService();
                DirectoryIdentity? source = identity.ReadIfExists(record.SourcePath);
                if (source is not null && !source.IsReparsePoint
                    && (mode == MigrationMode.Migrate || Directory.EnumerateFileSystemEntries(record.SourcePath).Any()))
                {
                    string backup = record.SourcePath + ".bak_" + record.TransactionId.ToString("D");
                    identity.RequireMissing(backup);
                    // 调用者在改名前即持有已持久化的准确路径，异常不能遗失备份位置。
                    record = await new MigrationTransactionStore().SaveAsync(record with { BackupPath = backup }, record.Revision, ct).ConfigureAwait(false);
                    Publish(record, progress);
                }
                record = await CreateSymbolicLinkWithBackupAsync(record, before, progress, ct).ConfigureAwait(false);
            }
            record = await new MigrationTransactionStore().SaveAsync(record with
            {
                Stage = MigrationStage.AwaitingUserVerification, LastError = null,
                UserVerifiedAtUtc = null, DeletionRequest = null
            }, record.Revision, CancellationToken.None).ConfigureAwait(false);
            ApplyRecord(result, record);
            result.Success = true;
            Publish(record, progress);
            log?.Report($"技术动作完成；{record.VerificationSummary?.Description}。所有数据副本保留，待用户验证。");
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            result.Stage = ex is OperationCanceledException ? MigrationStage.Cancelled : MigrationStage.Failed;
            if (record is not null)
            {
                result.TransactionId = record.TransactionId;
                try
                {
                    var store = new MigrationTransactionStore();
                    record = await store.LoadAsync(record.TransactionId).ConfigureAwait(false) ?? throw new IOException("异常后事务记录缺失。");
                    record = await store.SaveAsync(record with { Stage = MigrationStage.RecoveryRequired, LastError = ex.Message },
                        record.Revision, CancellationToken.None).ConfigureAwait(false);
                    Publish(record, progress);
                    result.RollbackOutcome = mode == MigrationMode.Restore
                        ? await RollbackRestoreAsync(record, CancellationToken.None).ConfigureAwait(false)
                        : await RollbackMigrationAsync(record, CancellationToken.None).ConfigureAwait(false);
                    record = await store.LoadAsync(record.TransactionId).ConfigureAwait(false) ?? throw new IOException("恢复后的事务记录缺失。");
                    record = await store.SaveAsync(record with
                    {
                        Stage = result.RollbackOutcome == RollbackOutcome.RecoveryRequired ? MigrationStage.RecoveryRequired : result.Stage,
                        LastError = record.LastError == ex.Message ? ex.Message : $"{ex.Message}；{record.LastError}"
                    }, record.Revision, CancellationToken.None).ConfigureAwait(false);
                    ApplyRecord(result, record);
                    result.ErrorMessage = record.LastError;
                    Publish(record, progress);
                }
                catch (Exception recoveryError)
                {
                    result.Stage = MigrationStage.RecoveryRequired;
                    result.RollbackOutcome = RollbackOutcome.RecoveryRequired;
                    result.ErrorMessage += $"；恢复或记录失败：{recoveryError.Message}";
                    result.BackupPath = record.BackupPath;
                    result.BackupState = record.BackupPath is null ? null : record.BackupState;
                }
                result.RecoveryPaths = Locations(record);
            }
            log?.Report($"操作未完成：{result.ErrorMessage}；恢复结果：{result.RollbackOutcome}；位置：{string.Join("；", result.RecoveryPaths)}");
        }
        return result;
    }

    private static async Task<MigrationTransactionRecord> PrepareRepairAsync(MigrationConfig config, CancellationToken ct)
    {
        config.SourcePath = PathIdentityService.Normalize(config.SourcePath);
        config.TargetPath = PathIdentityService.Normalize(config.TargetPath);
        MigrationRuntime.AssertDataPath(config.SourcePath, allowLink: true);
        MigrationRuntime.AssertDataPath(config.TargetPath);
        var relation = PathValidator.ValidatePathRelation(config.SourcePath, config.TargetPath);
        if (!relation.IsValid) throw new IOException(relation.Error);
        var sourceAddress = PathValidator.ValidateTargetPath(config.SourcePath);
        if (!sourceAddress.IsValid) throw new IOException(sourceAddress.Error);
        var admission = PathValidator.ValidateSourcePath(config.TargetPath);
        if (!admission.IsValid) throw new IOException(admission.Error);
        var identity = new PathIdentityService();
        DirectoryIdentity target = identity.ReadDirectory(config.TargetPath, false);
        if (target.IsReparsePoint) throw new IOException("修复目标必须是普通实体目录。");
        DirectoryIdentity? source = identity.ReadIfExists(config.SourcePath);
        var store = new MigrationTransactionStore();
        var known = (await store.ReadAllAsync(ct).ConfigureAwait(false)).Where(r =>
            r.SourcePath.Equals(config.SourcePath, StringComparison.OrdinalIgnoreCase)
            && r.TargetPath.Equals(config.TargetPath, StringComparison.OrdinalIgnoreCase) && r.TargetIdentity == target).ToArray();
        if (known.Length > 1) throw new IOException("存在多个匹配事务，不能猜测归属。");
        if (known.Length == 1)
        {
            var record = known[0];
            if (source is not null)
            {
                identity.RequireSame(config.SourcePath, source.IsReparsePoint ? record.LinkIdentity : record.SourceIdentity);
                if (source.IsReparsePoint) identity.RequireSame(config.SourcePath, target, true);
            }
            if (record.BackupIdentity is not null && record.BackupState is not (BackupState.Deleted or BackupState.Recycled))
                identity.RequireSame(record.BackupPath!, record.BackupIdentity);
            return await store.SaveAsync(record with
            {
                Mode = MigrationMode.Repair, Stage = MigrationStage.Prepared,
                SourceParentIdentity = record.SourceParentIdentity ?? identity.ReadDirectory(Path.GetDirectoryName(config.SourcePath)!, false),
                VerificationMode = config.VerificationMode, VerificationSummary = null, UserVerifiedAtUtc = null, DeletionRequest = null
            }, record.Revision, ct).ConfigureAwait(false);
        }
        if (source is null || source.IsReparsePoint) throw new IOException("缺少可确认的源实体或事务；不能认领未知链接/目录名。");
        return await store.CreateAsync(new MigrationTransactionRecord
        {
            Mode = MigrationMode.Repair, SourcePath = config.SourcePath, TargetPath = config.TargetPath,
            SourceIdentity = source, TargetIdentity = target, VerificationMode = config.VerificationMode,
            SourceParentIdentity = identity.ReadDirectory(Path.GetDirectoryName(config.SourcePath)!, false),
            TargetParentIdentity = identity.ReadDirectory(Path.GetDirectoryName(config.TargetPath)!, false)
        }, ct).ConfigureAwait(false);
    }

    public static async Task<MigrationTransactionRecord> CreateSymbolicLinkWithBackupAsync(MigrationTransactionRecord record,
        FileManifest before, IProgress<MigrationProgress>? progress, CancellationToken ct)
    {
        record = await RequireCurrentAsync(record, ct).ConfigureAwait(false);
        if (record.Stage != MigrationStage.CopyVerified || record.VerificationSummary?.IsMatch != true)
            throw new IOException("未完成核对，不能切换源目录。");
        var identity = new PathIdentityService();
        RequireParents(record, identity);
        identity.RequireSame(record.TargetPath, record.TargetIdentity);
        DirectoryIdentity? source = identity.ReadIfExists(record.SourcePath);
        if (source is { IsReparsePoint: true })
        {
            identity.RequireSame(record.SourcePath, record.LinkIdentity);
            identity.RequireSame(record.SourcePath, record.TargetIdentity, true);
            await RequireUnchangedAsync(before, record.TargetPath, record.TargetPath, ct).ConfigureAwait(false);
            return record;
        }
        ct.ThrowIfCancellationRequested();
        if (source is not null)
        {
            identity.RequireSame(record.SourcePath, record.SourceIdentity);
            if (record.BackupPath is not null)
            {
                if (record.BackupPath != record.SourcePath + ".bak_" + record.TransactionId.ToString("D"))
                    throw new IOException("备份意图路径不属于本事务。");
                identity.RequireMissing(record.BackupPath);
                Directory.Move(record.SourcePath, record.BackupPath);
                identity.RequireSame(record.BackupPath, record.SourceIdentity);
                record = await new MigrationTransactionStore().SaveAsync(record with
                {
                    Stage = MigrationStage.SourceRenamed, BackupIdentity = record.SourceIdentity,
                    BackupState = BackupState.RetainedUnverified
                }, record.Revision, CancellationToken.None).ConfigureAwait(false);
                Publish(record, progress);
                await RequireUnchangedAsync(before, record.BackupPath, record.TargetPath, ct).ConfigureAwait(false);
            }
            else
            {
                if (record.Mode != MigrationMode.Repair) throw new IOException("改名前缺少已持久化的备份意图。");
                await RequireUnchangedAsync(before, record.TargetPath, record.TargetPath, ct).ConfigureAwait(false);
                if (Directory.EnumerateFileSystemEntries(record.SourcePath).Any()) throw new IOException("源出现新内容，拒绝移除。");
                Directory.Delete(record.SourcePath, recursive: false);
                record = await SaveVacatedAsync(record, progress).ConfigureAwait(false);
            }
        }
        else
        {
            if (record.Mode != MigrationMode.Repair) throw new IOException("源实体缺失，不能跳过备份。");
            await RequireUnchangedAsync(before, record.TargetPath, record.TargetPath, ct).ConfigureAwait(false);
            record = await SaveVacatedAsync(record, progress).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        RequireParents(record, identity);
        identity.RequireMissing(record.SourcePath);
        identity.RequireSame(record.TargetPath, record.TargetIdentity);
        await CreateLinkAsync(record.SourcePath, record.TargetPath).ConfigureAwait(false);
        DirectoryIdentity link = identity.ReadDirectory(record.SourcePath, false);
        if (!link.IsReparsePoint) throw new IOException("新建对象不是链接。");
        identity.RequireSame(record.SourcePath, record.TargetIdentity, true);
        record = await new MigrationTransactionStore().SaveAsync(record with { LinkIdentity = link, Stage = MigrationStage.LinkCreated },
            record.Revision, CancellationToken.None).ConfigureAwait(false);
        Publish(record, progress);
        ct.ThrowIfCancellationRequested();
        identity.RequireSame(record.SourcePath, record.LinkIdentity);
        identity.RequireSame(record.SourcePath, record.TargetIdentity, true);
        return record;
    }

    private static async Task<MigrationTransactionRecord> CompleteRestoreAsync(MigrationTransactionRecord record,
        FileManifest before, IProgress<MigrationProgress>? progress, CancellationToken ct)
    {
        record = await RequireCurrentAsync(record, ct).ConfigureAwait(false);
        var identity = new PathIdentityService();
        RequireParents(record, identity);
        identity.RequireSame(record.SourcePath, record.LinkIdentity);
        identity.RequireSame(record.SourcePath, record.TargetIdentity, true);
        identity.RequireSame(record.TargetPath, record.TargetIdentity);
        identity.RequireSame(record.StagingPath!, record.StagingIdentity);
        await RequireUnchangedAsync(before, record.TargetPath, record.StagingPath!, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        Directory.Delete(record.SourcePath, recursive: false);
        record = await SaveVacatedAsync(record, progress).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        RequireParents(record, identity);
        identity.RequireSame(record.StagingPath!, record.StagingIdentity);
        identity.RequireMissing(record.SourcePath);
        await RequireUnchangedAsync(before, record.TargetPath, record.StagingPath!, ct).ConfigureAwait(false);
        Directory.Move(record.StagingPath!, record.SourcePath);
        identity.RequireSame(record.SourcePath, record.StagingIdentity);
        return await new MigrationTransactionStore().SaveAsync(record with { LinkIdentity = null }, record.Revision,
            CancellationToken.None).ConfigureAwait(false);
    }

    public static async Task<RollbackOutcome> RollbackMigrationAsync(MigrationTransactionRecord record, CancellationToken ct = default)
    {
        try
        {
            record = await RequireCurrentAsync(record, ct).ConfigureAwait(false);
            var identity = new PathIdentityService();
            RequireParents(record, identity);
            DirectoryIdentity? source = identity.ReadIfExists(record.SourcePath);
            if (source == record.SourceIdentity) return RollbackOutcome.NotRequired;
            if (record.BackupPath is null) return RollbackOutcome.RecoveryRequired;
            identity.RequireSame(record.BackupPath, record.BackupIdentity ?? record.SourceIdentity);
            identity.RequireSame(record.TargetPath, record.TargetIdentity);
            var verifier = new CopyVerificationService();
            FileManifest backup = await verifier.CaptureAsync(record.BackupPath, record.VerificationMode, ct).ConfigureAwait(false);
            RequireDigest(backup, record.SourceManifestDigest, includeIdentity: true);
            FileManifest target = await verifier.CaptureAsync(record.TargetPath, record.VerificationMode, ct).ConfigureAwait(false);
            RequireDigest(target, record.VerifiedManifestDigest, includeIdentity: false);
            if (source is not null)
            {
                identity.RequireSame(record.SourcePath, record.LinkIdentity);
                identity.RequireSame(record.SourcePath, record.TargetIdentity, true);
                Directory.Delete(record.SourcePath, recursive: false);
            }
            identity.RequireMissing(record.SourcePath);
            identity.RequireSame(record.BackupPath, record.BackupIdentity ?? record.SourceIdentity);
            Directory.Move(record.BackupPath, record.SourcePath);
            identity.RequireSame(record.SourcePath, record.SourceIdentity);
            await new MigrationTransactionStore().SaveAsync(record with
            {
                BackupPath = null, BackupIdentity = null, LinkIdentity = null,
                Stage = MigrationStage.Failed, LastError = "原实体已恢复到源路径，已复制目标仍保留。"
            }, record.Revision, CancellationToken.None).ConfigureAwait(false);
            return RollbackOutcome.Restored;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var store = new MigrationTransactionStore();
            record = await store.LoadAsync(record.TransactionId, CancellationToken.None).ConfigureAwait(false)
                ?? throw new IOException("回滚拒绝且事务记录缺失。", ex);
            await store.SaveAsync(record with
            {
                Stage = MigrationStage.RecoveryRequired, LastError = $"{record.LastError}；回滚拒绝：{ex.Message}"
            }, record.Revision, CancellationToken.None).ConfigureAwait(false);
            return RollbackOutcome.RecoveryRequired;
        }
    }

    private static async Task<RollbackOutcome> RollbackRestoreAsync(MigrationTransactionRecord record, CancellationToken ct)
    {
        var identity = new PathIdentityService();
        RequireParents(record, identity);
        identity.RequireSame(record.TargetPath, record.TargetIdentity);
        DirectoryIdentity? source = identity.ReadIfExists(record.SourcePath);
        if (source is not null)
        {
            if (source != record.LinkIdentity || !source.IsReparsePoint) return RollbackOutcome.RecoveryRequired;
            identity.RequireSame(record.SourcePath, record.TargetIdentity, true);
            return RollbackOutcome.NotRequired;
        }
        identity.RequireSame(record.StagingPath!, record.StagingIdentity);
        var verifier = new CopyVerificationService();
        FileManifest target = await verifier.CaptureAsync(record.TargetPath, record.VerificationMode, ct).ConfigureAwait(false);
        RequireDigest(target, record.SourceManifestDigest, includeIdentity: true);
        FileManifest staging = await verifier.CaptureAsync(record.StagingPath!, record.VerificationMode, ct).ConfigureAwait(false);
        RequireDigest(staging, record.VerifiedManifestDigest, includeIdentity: false);
        await CreateLinkAsync(record.SourcePath, record.TargetPath).ConfigureAwait(false);
        DirectoryIdentity link = identity.ReadDirectory(record.SourcePath, false);
        if (!link.IsReparsePoint) throw new IOException("恢复结果不是链接。");
        identity.RequireSame(record.SourcePath, record.TargetIdentity, true);
        await new MigrationTransactionStore().SaveAsync(record with { LinkIdentity = link, Stage = MigrationStage.Failed },
            record.Revision, CancellationToken.None).ConfigureAwait(false);
        return RollbackOutcome.Restored;
    }

    private static void RequireDigest(FileManifest manifest, string? expected, bool includeIdentity)
    {
        if (expected is null || CopyVerificationService.Fingerprint(manifest, includeIdentity) != expected)
            throw new IOException("集合与切换前持久化核对基线不同，或缺少可靠基线；保留所有实体。");
    }

    private static async Task CreateLinkAsync(string source, string target)
    {
        // 目录操作开始后先等待实际退出并登记身份，再接受取消，避免未登记的半完成链接。
        if (SymbolicLinkHelper.CreateDirectorySymbolicLink(source, target)) return;
        var created = await SymbolicLinkHelper.CreateSymbolicLinkViaCmdAsync(source, target).ConfigureAwait(false);
        if (!created.Success) throw new IOException($"创建符号链接失败：{created.Error}");
    }

    private static async Task<MigrationTransactionRecord> SaveVacatedAsync(MigrationTransactionRecord record, IProgress<MigrationProgress>? progress)
    {
        record = await new MigrationTransactionStore().SaveAsync(record with { Stage = MigrationStage.SourceRenamed },
            record.Revision, CancellationToken.None).ConfigureAwait(false);
        Publish(record, progress);
        return record;
    }

    private static async Task<MigrationTransactionRecord> RequireCurrentAsync(MigrationTransactionRecord record, CancellationToken ct)
    {
        var current = await new MigrationTransactionStore().LoadAsync(record.TransactionId, ct).ConfigureAwait(false)
            ?? throw new IOException("事务记录缺失。");
        if (current.Revision != record.Revision) throw new IOException("事务已变化，拒绝旧快照。");
        MigrationRuntime.AssertDataPath(current.SourcePath, allowLink: true);
        MigrationRuntime.AssertDataPath(current.TargetPath);
        if (current.BackupPath is not null) MigrationRuntime.AssertDataPath(current.BackupPath);
        if (current.StagingPath is not null) MigrationRuntime.AssertDataPath(current.StagingPath);
        return current;
    }

    private static void RequireParents(MigrationTransactionRecord record, PathIdentityService identity)
    {
        identity.RequireSame(Path.GetDirectoryName(record.SourcePath)!, record.SourceParentIdentity);
        identity.RequireSame(Path.GetDirectoryName(record.TargetPath)!, record.TargetParentIdentity);
    }

    private static async Task RequireUnchangedAsync(FileManifest before, string source, string target, CancellationToken ct) =>
        RequireMatch(await new CopyVerificationService().VerifyAsync(before, source, target, before.Mode, ct).ConfigureAwait(false));

    private static void RequireMatch(VerificationSummary summary)
    {
        if (!summary.IsMatch) throw new IOException(string.Join(Environment.NewLine, summary.Mismatches));
    }

    private static void ApplyRecord(MigrationResult result, MigrationTransactionRecord record)
    {
        result.TransactionId = record.TransactionId;
        result.SourcePath = record.SourcePath;
        result.TargetPath = record.TargetPath;
        result.Stage = record.Stage;
        result.BackupPath = record.BackupPath;
        result.BackupState = record.BackupPath is null ? null : record.BackupState;
        result.VerificationSummary = record.VerificationSummary;
    }

    private static string[] Locations(MigrationTransactionRecord record) =>
        new[] { record.SourcePath, record.TargetPath, record.BackupPath, record.StagingPath, record.ProbePath }
            .Where(p => p is not null).Select(p => p!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static void Publish(MigrationTransactionRecord record, IProgress<MigrationProgress>? progress)
    {
        string message = record.Stage == MigrationStage.AwaitingUserVerification
            ? "技术动作完成；保留数据副本，待用户验证" : $"事务阶段：{record.Stage}";
        progress?.Report(new MigrationProgress
        {
            Stage = record.Stage, CurrentPhase = record.Stage == MigrationStage.AwaitingUserVerification ? 6 : 4,
            PercentComplete = record.Stage == MigrationStage.AwaitingUserVerification ? 100 : 90,
            PhaseDescription = message, Message = message
        });
    }

    public static async Task<FileStats> ScanDirectoryAsync(string directoryPath, long largeFileThresholdBytes,
        IProgress<string>? logProgress = null, CancellationToken cancellationToken = default)
    {
        logProgress?.Report($"正在扫描: {directoryPath}");
        return await FileStatsService.ScanDirectoryAsync(directoryPath, largeFileThresholdBytes, logProgress, cancellationToken).ConfigureAwait(false);
    }

    public static void ReportScanResults(FileStats stats, int largeFileThresholdMB, IProgress<string>? logProgress = null)
    {
        logProgress?.Report($"总文件数: {stats.TotalFiles}");
        logProgress?.Report($"总大小: {FileStatsService.FormatBytes(stats.TotalBytes)}");
        logProgress?.Report($"大文件 (≥{largeFileThresholdMB}MB): {stats.LargeFiles} 个");
    }
}
