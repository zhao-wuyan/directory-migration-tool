using MigrationCore.Models;

namespace MigrationCore.Services;

/// <summary>不能证明只回收且失败不永久降级时，明确拒绝而不尝试有风险的 Shell 删除。</summary>
public static class RecycleBinHelper
{
    public const string UnsupportedReason = "Unsupported：尚无可证明失败时绝不永久降级的 Shell 只回收保证；未调用删除 API，备份保留。";

    public static BackupDeletionResult MoveDirectoryToRecycleBin(Guid requestId, Guid transactionId,
        string directoryPath, FileManifest manifest)
    {
        MigrationRuntime.AssertDataPath(directoryPath);
        new PathIdentityService().RequireSame(directoryPath, manifest.RootIdentity);
        return new BackupDeletionResult
        {
            RequestId = requestId, TransactionId = transactionId, Status = BackupDeletionStatus.Unsupported,
            Items = manifest.Entries.Select(p => new BackupDeletionItem(
                p.Key.Length == 0 ? directoryPath : Path.Combine(directoryPath, p.Key),
                BackupItemOutcome.Unsupported, UnsupportedReason)).ToArray(),
            Error = UnsupportedReason, CompletedAtUtc = DateTimeOffset.UtcNow
        };
    }
}
