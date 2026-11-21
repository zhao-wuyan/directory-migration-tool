using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using MigrationCore.Models;

namespace MigrationCore.Services;

public sealed class MigrationTransactionStore
{
    private static readonly string[] RequiredFields =
    ["schemaVersion", "transactionId", "revision", "mode", "stage", "verificationMode", "sourcePath", "targetPath",
     "targetParentIdentity", "stagingPath", "stagingIdentity", "reservedEmptyTargetIdentity", "probePath", "backupPath",
     "sourceIdentity", "targetIdentity", "backupIdentity", "linkIdentity", "verificationSummary", "backupState",
     "userVerifiedAtUtc", "deletionRequest", "lastError", "updatedAtUtc"];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly PathIdentityService _identity = new();
    private readonly DirectoryIdentity _rootIdentity;
    public string DirectoryPath { get; }

    public MigrationTransactionStore(string? directory = null)
    {
        DirectoryPath = PathIdentityService.Normalize(directory ?? MigrationRuntime.TransactionDirectory);
        MigrationRuntime.AssertStatePath(DirectoryPath);
        _identity.EnsureOrdinaryDirectory(DirectoryPath);
        _rootIdentity = _identity.ReadDirectory(DirectoryPath, false);
    }

    public async Task<MigrationTransactionRecord> CreateAsync(MigrationTransactionRecord record, CancellationToken ct = default)
    {
        Validate(record);
        if (record.Revision != 0) throw new InvalidOperationException("新事务 revision 必须为 0。");
        using FileStream guard = Lock();
        _identity.RequireMissing(RecordPath(record.TransactionId));
        _identity.RequireSame(record.SourcePath, record.SourceIdentity);
        _identity.RequireSame(Path.GetDirectoryName(record.TargetPath)!, record.TargetParentIdentity);
        foreach (MigrationTransactionRecord other in await ReadAllUnlocked(ct).ConfigureAwait(false))
        {
            if (!other.IsActive) continue;
            if (PathsOverlap(record, other, _identity)) throw new IOException($"与活动事务重叠：{other.TransactionId}");
        }
        var saved = record with { Revision = 1, UpdatedAtUtc = DateTimeOffset.UtcNow };
        await WriteSnapshot(saved, false, ct).ConfigureAwait(false);
        return saved;
    }

    public async Task<MigrationTransactionRecord?> LoadAsync(Guid transactionId, CancellationToken ct = default)
    {
        using FileStream guard = Lock();
        return await ReadUnlocked(transactionId, ct).ConfigureAwait(false);
    }

    public async Task<MigrationTransactionRecord[]> ReadAllAsync(CancellationToken ct = default)
    {
        using FileStream guard = Lock();
        return await ReadAllUnlocked(ct).ConfigureAwait(false);
    }

    public async Task<MigrationTransactionRecord> SaveAsync(MigrationTransactionRecord record, long expectedRevision, CancellationToken ct = default)
    {
        Validate(record);
        using FileStream guard = Lock();
        MigrationTransactionRecord current = await ReadUnlocked(record.TransactionId, ct).ConfigureAwait(false)
            ?? throw new IOException("事务记录缺失，不能重建或认领备份。");
        if (current.Revision != expectedRevision || record.Revision != expectedRevision)
            throw new IOException($"事务 revision 冲突：期望 {expectedRevision}，实际 {current.Revision}");
        if (current.SourcePath != record.SourcePath || current.TargetPath != record.TargetPath
            || current.SourceIdentity != record.SourceIdentity || current.TargetParentIdentity != record.TargetParentIdentity
            || (current.SourceParentIdentity is not null && current.SourceParentIdentity != record.SourceParentIdentity))
            throw new IOException("不能改变已登记事务的源、目标或初始身份。");
        if (current.BackupDeletionInProgress && (record.Mode != current.Mode || record.Stage != current.Stage
            || record.TargetIdentity != current.TargetIdentity || record.LinkIdentity != current.LinkIdentity
            || record.StagingIdentity != current.StagingIdentity
            || record.DeletionRequest?.RequestId != current.DeletionRequest?.RequestId || record.DeletionRequest?.Consumed != true))
            throw new IOException("备份处理尚未结束，不能抢占或改写迁移阶段。");
        if (record.BackupDeletionInProgress && !current.BackupDeletionInProgress)
            foreach (var other in await ReadAllUnlocked(ct).ConfigureAwait(false))
                if (other.TransactionId != record.TransactionId && ReferencesBackup(record, other, _identity))
                    throw new IOException($"备份仍被其他事务引用：{FormatConflictTarget(other)}");
        var saved = record with { Revision = checked(expectedRevision + 1), UpdatedAtUtc = DateTimeOffset.UtcNow };
        await WriteSnapshot(saved, true, ct).ConfigureAwait(false);
        return saved;
    }

    private FileStream Lock()
    {
        MigrationRuntime.AssertStatePath(DirectoryPath);
        _identity.RequireSame(DirectoryPath, _rootIdentity);
        string path = Path.Combine(DirectoryPath, ".transactions.lock");
        DirectoryIdentity? existing = _identity.ReadIfExists(path);
        if (existing is { IsReparsePoint: true } or { IsDirectory: true }) throw new IOException("事务锁不是普通文件。");
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private string RecordPath(Guid id)
    {
        if (id == Guid.Empty) throw new InvalidDataException("事务 ID 不能为空。");
        return Path.Combine(DirectoryPath, id.ToString("D") + ".json");
    }

    private async Task<MigrationTransactionRecord?> ReadUnlocked(Guid id, CancellationToken ct)
    {
        string path = RecordPath(id);
        DirectoryIdentity? identity = _identity.ReadIfExists(path);
        if (identity is null) return null;
        if (identity.IsDirectory || identity.IsReparsePoint) throw new IOException("事务记录不是普通文件。");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > 4 * 1024 * 1024) throw new InvalidDataException("事务记录大小无效。");
        using JsonDocument json = await JsonDocument.ParseAsync(file, cancellationToken: ct).ConfigureAwait(false);
        RejectDuplicateProperties(json.RootElement);
        foreach (string field in RequiredFields)
            if (!json.RootElement.TryGetProperty(field, out _)) throw new InvalidDataException($"事务记录缺少字段：{field}");
        MigrationTransactionRecord record = json.RootElement.Deserialize<MigrationTransactionRecord>(JsonOptions)
            ?? throw new InvalidDataException("空事务记录。");
        Validate(record);
        if (record.TransactionId != id || record.Revision < 1) throw new InvalidDataException("事务 ID 或 revision 无效。");
        return record;
    }

    private async Task<MigrationTransactionRecord[]> ReadAllUnlocked(CancellationToken ct)
    {
        var records = new List<MigrationTransactionRecord>();
        foreach (string path in Directory.EnumerateFiles(DirectoryPath, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "D", out Guid id))
                throw new InvalidDataException($"未知事务文件：{path}");
            records.Add(await ReadUnlocked(id, ct).ConfigureAwait(false) ?? throw new IOException("枚举后的事务记录丢失。"));
        }
        return records.ToArray();
    }

    private async Task WriteSnapshot(MigrationTransactionRecord record, bool replace, CancellationToken ct)
    {
        string destination = RecordPath(record.TransactionId);
        string temporary = Path.Combine(DirectoryPath, $".{record.TransactionId:D}.{Guid.NewGuid():N}.tmp");
        _identity.RequireMissing(temporary);
        // 临时快照失败时保留，不依据文件名恢复或认领。
        await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(file, record, JsonOptions, ct).ConfigureAwait(false);
            if (file.Length > 4 * 1024 * 1024) throw new IOException("事务快照超过可安全读取的容量，保留原记录和临时快照。");
            await file.FlushAsync(ct).ConfigureAwait(false);
            file.Flush(true);
        }
        ct.ThrowIfCancellationRequested();
        _identity.RequireSame(DirectoryPath, _rootIdentity);
        if (replace) File.Replace(temporary, destination, null);
        else File.Move(temporary, destination, false);
    }

    private static bool PathsOverlap(MigrationTransactionRecord a, MigrationTransactionRecord b, PathIdentityService identity)
    {
        return Overlap(a.SourcePath, b.SourcePath) || Overlap(a.SourcePath, b.TargetPath)
            || Overlap(a.TargetPath, b.SourcePath) || Overlap(a.TargetPath, b.TargetPath)
            || (a.BackupDeletionInProgress && ReferencesBackup(a, b, identity))
            || (b.BackupDeletionInProgress && ReferencesBackup(b, a, identity));

        static bool Overlap(string x, string y) => PathIdentityService.IsWithin(x, y) || PathIdentityService.IsWithin(y, x);
    }

    internal static bool ReferencesBackup(MigrationTransactionRecord owner, MigrationTransactionRecord other,
        PathIdentityService? identity = null)
    {
        if (owner.TransactionId == other.TransactionId || owner.BackupPath is null) return false;
        bool Touches(string? path) => path is not null && (PathIdentityService.IsWithin(path, owner.BackupPath)
            || PathIdentityService.IsWithin(owner.BackupPath, path));
        if (Touches(other.SourcePath) || Touches(other.TargetPath) || Touches(other.BackupPath) || Touches(other.StagingPath))
            return true;
        if (owner.BackupIdentity is null) return false;

        if (other.IsActive)
            return other.SourceIdentity == owner.BackupIdentity
                || other.TargetIdentity == owner.BackupIdentity
                || other.BackupIdentity == owner.BackupIdentity;

        // 非活动历史事务可能因同卷移动或二次链式迁移（A→B 后再 B→C）与当前备份共享过历史 FileId；
        // 仅当该历史事务在磁盘上的实际路径仍解析到当前备份实体（或无法核实磁盘状态）时才视为占用。
        var checker = identity ?? new PathIdentityService();
        if (other.TargetIdentity == owner.BackupIdentity
            && (PathStillMatchesIdentity(checker, other.TargetPath, owner.BackupIdentity, followLink: false)
                || PathStillMatchesIdentity(checker, other.TargetPath, owner.BackupIdentity, followLink: true)
                || PathStillMatchesIdentity(checker, other.SourcePath, owner.BackupIdentity, followLink: true)))
            return true;

        if (other.StagingIdentity == owner.BackupIdentity
            && PathStillMatchesIdentity(checker, other.SourcePath, owner.BackupIdentity, followLink: false))
            return true;

        if (other.BackupIdentity == owner.BackupIdentity
            && other.BackupState is not (BackupState.Deleted or BackupState.Recycled)
            && PathStillMatchesIdentity(checker, other.BackupPath, owner.BackupIdentity, followLink: false))
            return true;

        return false;
    }

    internal static string FormatConflictTarget(MigrationTransactionRecord other) =>
        $"{other.TransactionId}（原路径：{other.SourcePath}，目标：{other.TargetPath}）";

    private static bool PathStillMatchesIdentity(PathIdentityService identity, string? path, DirectoryIdentity expected, bool followLink)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            return identity.ReadEntry(path, followLink) == expected;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 2 or 3)
        {
            return false;
        }
        catch
        {
            // 无法核实磁盘实际指向（例如权限拒绝或未知重解析祖先）时保守按仍被引用处理。
            return true;
        }
    }

    private static void Validate(MigrationTransactionRecord record)
    {
        if (record.SchemaVersion != 1 || record.TransactionId == Guid.Empty || record.Revision < 0
            || !Enum.IsDefined(record.Stage) || !Enum.IsDefined(record.Mode) || !Enum.IsDefined(record.BackupState)
            || !Enum.IsDefined(record.VerificationMode) || record.SourceIdentity is null || record.TargetParentIdentity is null)
            throw new InvalidDataException("不完整或不支持的事务记录。");
        if (PathIdentityService.Normalize(record.SourcePath) != record.SourcePath
            || PathIdentityService.Normalize(record.TargetPath) != record.TargetPath)
            throw new InvalidDataException("事务路径不是规范绝对路径。");
        if (PathIdentityService.IsWithin(record.SourcePath, record.TargetPath)
            || PathIdentityService.IsWithin(record.TargetPath, record.SourcePath))
            throw new InvalidDataException("源和目标不能相同或互相包含。");
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("事务记录包含重复字段。");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (JsonElement child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }
}
