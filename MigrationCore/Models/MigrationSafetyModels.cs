namespace MigrationCore.Models;

public enum VerificationMode { Basic, ContentHash }
public enum MigrationStage
{
    Prepared, ProbeRenamed, Copying, CopyVerified, SourceRenamed, LinkCreated,
    AwaitingUserVerification, Cancelled, Failed, RecoveryRequired
}
public enum BackupState
{
    RetainedUnverified, UserVerified, DeleteRequested, Recycled, Deleted,
    Missing, IdentityChanged, DeleteFailed, PartiallyDeleted
}
public enum RollbackOutcome { NotRequired, Restored, RecoveryRequired }
public enum BackupDeletionMode { Recycle, Permanent }
public enum ConfirmationStep { First, Second }

// 身份不包含可变化的路径、时间或大小；重命名不改变对象身份。
public sealed record DirectoryIdentity(string VolumeGuid, uint VolumeSerial, ulong FileId,
    bool IsReparsePoint, bool IsDirectory = true);

public sealed record VerificationSummary
{
    public VerificationMode Mode { get; init; }
    public bool IsMatch { get; init; }
    public string[] Mismatches { get; init; } = [];
    public string Description => Mode == VerificationMode.Basic
        ? "基础项核对（未做内容哈希，不证明逐字节一致）" : "完整内容 SHA-256 核对";
}

public sealed record BackupDeletionRequest
{
    public Guid RequestId { get; init; }
    public Guid TransactionId { get; init; }
    public long TransactionRevision { get; init; }
    public BackupDeletionMode Mode { get; init; }
    public DirectoryIdentity? BackupIdentity { get; init; }
    public DirectoryIdentity? TargetIdentity { get; init; }
    public string ManifestDigest { get; init; } = "";
    public DateTimeOffset? FirstConfirmedAtUtc { get; init; }
    public DateTimeOffset? SecondConfirmedAtUtc { get; init; }
    public bool Consumed { get; init; }
}

public sealed record MigrationTransactionRecord
{
    public int SchemaVersion { get; init; } = 1;
    public Guid TransactionId { get; init; } = Guid.NewGuid();
    public long Revision { get; init; }
    public MigrationMode Mode { get; init; }
    public MigrationStage Stage { get; init; } = MigrationStage.Prepared;
    public VerificationMode VerificationMode { get; init; }
    public string SourcePath { get; init; } = "";
    public string TargetPath { get; init; } = "";
    public DirectoryIdentity? TargetParentIdentity { get; init; }
    public string? StagingPath { get; init; }
    public DirectoryIdentity? StagingIdentity { get; init; }
    public DirectoryIdentity? ReservedEmptyTargetIdentity { get; init; }
    public string? ProbePath { get; init; }
    public string? BackupPath { get; init; }
    public DirectoryIdentity? SourceIdentity { get; init; }
    public DirectoryIdentity? TargetIdentity { get; init; }
    public DirectoryIdentity? BackupIdentity { get; init; }
    public DirectoryIdentity? LinkIdentity { get; init; }
    public VerificationSummary? VerificationSummary { get; init; }
    public BackupState BackupState { get; init; } = BackupState.RetainedUnverified;
    public DateTimeOffset? UserVerifiedAtUtc { get; init; }
    public BackupDeletionRequest? DeletionRequest { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public bool IsActive => Stage is MigrationStage.Prepared or MigrationStage.ProbeRenamed
        or MigrationStage.Copying or MigrationStage.CopyVerified or MigrationStage.SourceRenamed
        or MigrationStage.LinkCreated or MigrationStage.RecoveryRequired;
}
