namespace MigrationCore.Models;

public enum BackupDeletionStatus { Deleted, Recycled, Failed, Partial, Unsupported }
public enum BackupItemOutcome { Deleted, Recycled, Failed, Retained, Unsupported }

public sealed record BackupDeletionItem(string Path, BackupItemOutcome Outcome, string? Error = null);

public sealed record BackupDeletionResult
{
    public Guid RequestId { get; init; }
    public Guid TransactionId { get; init; }
    public BackupDeletionStatus Status { get; init; }
    public BackupDeletionItem[] Items { get; init; } = [];
    public string? Error { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
}
