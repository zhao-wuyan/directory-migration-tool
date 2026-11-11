namespace MigrationCore.Models;

public enum CopyPhase { Preparing, ProcessStarted, ReadingOutput, Sampling, WaitingForExit, Verifying, Promoting, Completed, Stopping }

public sealed record AlternateStreamManifest(string Name, long Length, string? Hash);

public sealed record FileManifestEntry(string RelativePath, bool IsDirectory, long Length,
    DateTime LastWriteTimeUtc, FileAttributes Attributes, string AclSddl,
    AlternateStreamManifest[] AlternateStreams, string? Hash, DirectoryIdentity Identity);

public sealed record FileManifest(string RootPath, DirectoryIdentity RootIdentity, VerificationMode Mode,
    IReadOnlyDictionary<string, FileManifestEntry> Entries);

public sealed record CopyExecutionResult
{
    public int? ExitCode { get; init; }
    public string StagingPath { get; init; } = "";
    public string ActualDataPath { get; init; } = "";
    public string? Error { get; init; }
    public bool ProcessExited { get; init; } = true;
    public bool Cancelled { get; init; }
    public bool RecoveryRequired { get; init; }
    public int? ProcessId { get; init; }
    public required MigrationTransactionRecord Transaction { get; init; }
    public FileManifest? SourceManifest { get; init; }
    public VerificationSummary? Verification { get; init; }
    public bool Success => Error is null && !Cancelled && !RecoveryRequired && ProcessExited && Verification?.IsMatch == true;
}
