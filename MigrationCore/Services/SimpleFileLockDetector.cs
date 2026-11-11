using MigrationCore.Models;

namespace MigrationCore.Services;

public sealed record DirectoryProbeResult
{
    public bool CanProceed { get; init; }
    public bool RecoveryRequired { get; init; }
    public string ActualDataPath { get; init; } = "";
    public string? Error { get; init; }
    public MigrationTransactionRecord? Transaction { get; init; }
    public IReadOnlyList<HandleDiagnosticResult> Diagnostics { get; init; } = [];
}

public static class SimpleFileLockDetector
{
    public static Task<DirectoryProbeResult> CheckMigrationAsync(MigrationTransactionRecord transaction,
        IProgress<string>? log, CancellationToken ct) => CheckAsync(transaction, false, log, ct);

    public static Task<DirectoryProbeResult> CheckRestoreAsync(MigrationTransactionRecord transaction,
        IProgress<string>? log, CancellationToken ct) => CheckAsync(transaction, true, log, ct);

    private static async Task<DirectoryProbeResult> CheckAsync(MigrationTransactionRecord transaction, bool restore,
        IProgress<string>? log, CancellationToken ct)
    {
        var identity = new PathIdentityService();
        var store = new MigrationTransactionStore();
        MigrationTransactionRecord record = transaction;
        string original = restore ? record.TargetPath : record.SourcePath;
        string actual = original;
        string probe = original + "_lock_test_" + record.TransactionId.ToString("D");
        DirectoryIdentity? expected = restore ? record.TargetIdentity : record.SourceIdentity;
        bool attemptedRename = false;
        bool moved = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            MigrationRuntime.AssertDataPath(record.SourcePath, allowLink: restore);
            MigrationRuntime.AssertDataPath(record.TargetPath);
            MigrationRuntime.AssertDataPath(probe);
            MigrationTransactionRecord saved = await store.LoadAsync(record.TransactionId, ct).ConfigureAwait(false)
                ?? throw new IOException("快检必须绑定已保存的事务。");
            if (saved.Revision != record.Revision || saved.SourcePath != record.SourcePath || saved.TargetPath != record.TargetPath
                || saved.SourceIdentity != record.SourceIdentity || saved.TargetIdentity != record.TargetIdentity
                || saved.Stage != MigrationStage.Prepared)
                throw new IOException("快检事务的身份、阶段或 revision 已变化。");
            record = saved;
            if (expected is null || expected.IsReparsePoint || !expected.IsDirectory)
                throw new IOException("缺少普通实体目录的可靠身份。");
            identity.RequireSame(original, expected);
            identity.RequireSame(Path.GetDirectoryName(record.TargetPath)!, record.TargetParentIdentity);
            var admission = PathValidator.ValidateSourcePath(original);
            if (!admission.IsValid) throw new IOException(admission.Error);
            var targetAdmission = PathValidator.ValidateTargetPath(record.TargetPath);
            if (!targetAdmission.IsValid) throw new IOException(targetAdmission.Error);
            if (restore)
            {
                identity.RequireSame(record.SourcePath, record.LinkIdentity);
                identity.RequireSame(record.SourcePath, record.TargetIdentity, true);
            }
            if (record.ProbePath is not null && !record.ProbePath.Equals(probe, StringComparison.OrdinalIgnoreCase))
                throw new IOException("试改名路径不属于此事务。");
            identity.RequireMissing(probe);
            if (record.ProbePath is null)
                record = await store.SaveAsync(record with { ProbePath = probe }, record.Revision, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            identity.RequireSame(original, expected);
            attemptedRename = true;
            Directory.Move(original, probe);
            moved = true;
            actual = probe;
            identity.RequireSame(probe, expected);
            record = await store.SaveAsync(record with { Stage = MigrationStage.ProbeRenamed }, record.Revision,
                CancellationToken.None).ConfigureAwait(false);
            // 同步报告发生在快照保存之后；观察者可据真实记录判断阶段，无须猜测时间窗口。
            log?.Report($"快检阶段已保存：{record.Stage}；实际数据：{actual}");
            identity.RequireMissing(original);
            identity.RequireSame(probe, expected);
            Directory.Move(probe, original);
            actual = original;
            identity.RequireSame(original, expected);
            MigrationStage finalStage = ct.IsCancellationRequested ? MigrationStage.Cancelled : MigrationStage.Prepared;
            record = await store.SaveAsync(record with { Stage = finalStage }, record.Revision,
                CancellationToken.None).ConfigureAwait(false);
            log?.Report($"快检完成，原名及身份已恢复：{actual}");
            return new DirectoryProbeResult
            {
                CanProceed = !ct.IsCancellationRequested, ActualDataPath = actual, Transaction = record,
                Error = ct.IsCancellationRequested ? "快检已取消，原数据已恢复原名。" : null
            };
        }
        catch (Exception ex)
        {
            string error = ex.Message;
            bool originalVerified = false;
            try
            {
                identity.RequireSame(original, expected);
                identity.RequireMissing(probe);
                originalVerified = true;
                actual = original;
            }
            catch (Exception verificationError)
            {
                error += $"；不能确认原位实体：{verificationError.Message}";
            }
            bool recovery = !originalVerified || (moved && record.Stage != MigrationStage.Prepared
                && record.Stage != MigrationStage.Cancelled);
            var diagnostics = new List<HandleDiagnosticResult>();
            if (attemptedRename)
            {
                diagnostics.Add(await HandleHelper.GetProcessesLockingPathAsync(actual, ct).ConfigureAwait(false));
                if (!actual.Equals(original, StringComparison.OrdinalIgnoreCase))
                    diagnostics.Add(await HandleHelper.GetProcessesLockingPathAsync(original, ct).ConfigureAwait(false));
                if (diagnostics.Any(d => !d.ProcessExited)) recovery = true;
            }
            MigrationStage stage = recovery ? MigrationStage.RecoveryRequired
                : ct.IsCancellationRequested ? MigrationStage.Cancelled : MigrationStage.Failed;
            try
            {
                record = await store.SaveAsync(record with { Stage = stage, LastError = error }, record.Revision,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception saveError)
            {
                error += $"；记录失败：{saveError.Message}";
                recovery |= moved;
            }
            if (diagnostics.Count != 0)
                error += Environment.NewLine + string.Join(Environment.NewLine,
                    diagnostics.Select(d => $"Handle 诊断：{d.Status}；{d.Error}"));
            return new DirectoryProbeResult
            {
                CanProceed = false, RecoveryRequired = recovery, ActualDataPath = actual,
                Error = error, Transaction = record, Diagnostics = diagnostics
            };
        }
    }
}
