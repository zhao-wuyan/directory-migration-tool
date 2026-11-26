using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using MigrationCore.Models;
using MigrationCore.Models.Cleanup;

namespace MigrationCore.Services.Cleanup;

/// <summary>
/// 按卷受 ACL 保护的同卷隔离区管理器（&lt;Drive&gt;:\.MoveFolderQuarantine）。
/// 安全红线（对齐设计方案 9.4 与画板 08 / 11）：
/// 1. 仅允许同卷原子移动（MoveFileExW 不传 MOVEFILE_COPY_ALLOWED），严禁跨卷“复制后删源”；
/// 2. 物理落盘采用 \\?\ 扩展长路径 + 短 ID 目录（如 q-20261126-a1b2c3 / ai-20261126-a1b2c3）+ 原子 JSON 清单，防止突破 260 字符 MAX_PATH；
/// 3. 隔离根目录自动设置 Hidden 属性与严格 ACL（当前用户 + Administrators + SYSTEM），并已在 MigrationSafetyPolicy 永久排除；
/// 4. 恢复时绝不静默覆盖原路径已存在的新文件：无冲突直接移回，有冲突走「对比后恢复」（加 .restored_ 时间戳后缀保留双方）；
/// 5. 永久删除仅支持人工逐批二次确认，绝不按到期时间自动清空，并如实报告部分删除结果。
/// </summary>
public sealed class QuarantineStore
{
    public const string QuarantineFolderName = ".MoveFolderQuarantine";
    public const string ManifestFileName = "batch.json";
    public const string DeleteConfirmationPhrase = "永久删除";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly PathIdentityService _identity;
    private readonly string? _customQuarantineRootOverride;

    public QuarantineStore(PathIdentityService? identity = null, string? customQuarantineRootOverride = null)
    {
        _identity = identity ?? new PathIdentityService();
        _customQuarantineRootOverride = string.IsNullOrWhiteSpace(customQuarantineRootOverride)
            ? null
            : PathIdentityService.Normalize(customQuarantineRootOverride);
    }

    /// <summary>
    /// 解析指定源路径所属卷的隔离区根目录（生产环境恒为 &lt;Drive&gt;:\.MoveFolderQuarantine）。
    /// </summary>
    public string ResolveQuarantineRootForPath(string sourceFullPath)
    {
        if (_customQuarantineRootOverride is not null)
        {
            return _customQuarantineRootOverride;
        }

        string normalized = PathIdentityService.Normalize(sourceFullPath);
        string driveRoot = normalized[..3];
        return Path.Combine(driveRoot, QuarantineFolderName);
    }

    /// <summary>
    /// 列出本机全部固定卷（或测试注入根）中现有的隔离区批次。
    /// checkRestoreConflicts=false 时仅读取批次元数据（供主页面角标与按盘占用汇总使用，跳过逐文件原路径存在性探测，避免 UI/I/O 放大）；
    /// checkRestoreConflicts=true 时实时核验每条记录的原路径恢复冲突（供隔离区管理子视图使用）。
    /// </summary>
    public IReadOnlyList<QuarantineBatchInspection> ListAllInspections(
        bool checkRestoreConflicts = true,
        IReadOnlyList<string>? fixedDriveRoots = null,
        CancellationToken ct = default)
    {
        var results = new List<QuarantineBatchInspection>();
        foreach (string qRoot in EnumerateQuarantineRoots(fixedDriveRoots))
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(ToExtendedPath(qRoot)))
            {
                continue;
            }

            DirectoryIdentity? rootId = _identity.ReadIfExists(qRoot);
            if (rootId is null || !rootId.IsDirectory || rootId.IsReparsePoint)
            {
                continue;
            }

            string[] batchDirs;
            try
            {
                batchDirs = Directory.GetDirectories(ToExtendedPath(qRoot));
            }
            catch
            {
                continue;
            }

            foreach (string rawBatchDir in batchDirs)
            {
                ct.ThrowIfCancellationRequested();
                string batchDir = StripExtendedPrefix(rawBatchDir);
                string manifestPath = Path.Combine(batchDir, ManifestFileName);
                if (!File.Exists(ToExtendedPath(manifestPath)))
                {
                    continue;
                }

                try
                {
                    string json = File.ReadAllText(ToExtendedPath(manifestPath));
                    QuarantineBatchRecord? record = JsonSerializer.Deserialize<QuarantineBatchRecord>(json, JsonOptions);
                    if (record is not null)
                    {
                        QuarantineBatchRecord normalizedRecord = record with
                        {
                            QuarantineDirectoryPath = batchDir
                        };
                        results.Add(checkRestoreConflicts
                            ? InspectBatch(normalizedRecord, ct)
                            : new QuarantineBatchInspection(
                                Batch: normalizedRecord,
                                HasRestoreConflict: false,
                                ConflictCount: 0,
                                ConflictSamplePaths: [],
                                IsQuarantineDirectoryMissing: false));
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // 跳过损坏清单但不静默删除，保障可人工检查
                }
            }
        }

        return results
            .OrderByDescending(i => i.Batch.CreatedAtUtc)
            .ToArray();
    }

    /// <summary>
    /// 后台异步列出本机全部固定卷中的隔离区批次，避免阻塞 WPF UI 线程。
    /// </summary>
    public Task<IReadOnlyList<QuarantineBatchInspection>> ListAllInspectionsAsync(
        bool checkRestoreConflicts = true,
        IReadOnlyList<string>? fixedDriveRoots = null,
        CancellationToken ct = default) =>
        Task.Run(() => ListAllInspections(checkRestoreConflicts, fixedDriveRoots, ct), ct);

    /// <summary>
    /// 实时核验单个隔离批次的恢复冲突状态（原路径是否已出现同名新文件/目录）。
    /// </summary>
    public QuarantineBatchInspection InspectBatch(QuarantineBatchRecord batch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

        bool batchMissing = !Directory.Exists(ToExtendedPath(batch.QuarantineDirectoryPath));
        int conflictCount = 0;
        var conflictSamples = new List<string>();

        QuarantineEntryRecord[] entries = batch.Entries;
        for (int i = 0; i < entries.Length; i++)
        {
            if ((i & 0xFF) == 0) ct.ThrowIfCancellationRequested();
            QuarantineEntryRecord entry = entries[i];
            if (ExistsOnDisk(entry.OriginalFullPath))
            {
                conflictCount++;
                if (conflictSamples.Count < 5)
                {
                    conflictSamples.Add(entry.OriginalFullPath);
                }
            }
        }

        return new QuarantineBatchInspection(
            Batch: batch,
            HasRestoreConflict: conflictCount > 0,
            ConflictCount: conflictCount,
            ConflictSamplePaths: conflictSamples.ToArray(),
            IsQuarantineDirectoryMissing: batchMissing);
    }

    /// <summary>
    /// 将一组位于同一卷上的文件或目录原子移入同卷隔离区（支持 \\?\ 长路径与短 ID 映射）。
    /// 跨卷对象、重解析点或被占用文件将记录跳过/失败并保留原位置，绝不跨卷复制删源。
    /// </summary>
    public Task<(QuarantineBatchRecord? Batch, int MovedCount, int FailedCount, long MovedBytes, string[] Details)> QuarantineItemsSameVolumeAsync(
        string displayTitle,
        string sourceDisplaySummary,
        string? ruleId,
        bool isFromAiSuspectedResidual,
        IReadOnlyList<(string FullPath, long SizeBytes, bool IsDirectory, ulong FileId)> items,
        CancellationToken ct = default,
        IProgress<(int Done, int Total, long MovedBytes)>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayTitle);
        ArgumentNullException.ThrowIfNull(items);

        return Task.Run(() =>
        {
            var details = new List<string>();
            if (items.Count == 0)
            {
                return ((QuarantineBatchRecord?)null, 0, 0, 0L, details.ToArray());
            }

            string firstNormalized = PathIdentityService.Normalize(items[0].FullPath);
            string driveRoot = firstNormalized[..3];
            string quarantineRoot = ResolveQuarantineRootForPath(firstNormalized);
            EnsureProtectedQuarantineRoot(quarantineRoot);

            DirectoryIdentity quarantineRootId = _identity.ReadDirectory(quarantineRoot, followLink: false);
            if (quarantineRootId.IsReparsePoint)
            {
                throw new IOException($"隔离区根目录异常为重解析点，已中止操作：{quarantineRoot}");
            }

            string prefix = isFromAiSuspectedResidual ? "ai" : "q";
            string batchId = $"{prefix}-{DateTime.Now:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6]}";
            string batchDir = Path.Combine(quarantineRoot, batchId);
            string payloadDir = Path.Combine(batchDir, "data");
            Directory.CreateDirectory(ToExtendedPath(payloadDir));

            var movedEntries = new List<QuarantineEntryRecord>(items.Count);
            int failed = 0;
            long movedBytes = 0;
            int seq = 0;
            int processed = 0;
            int totalItems = items.Count;
            long lastReportTick = Environment.TickCount64;

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                processed++;
                string src;
                try
                {
                    src = PathIdentityService.Normalize(item.FullPath);
                }
                catch (Exception ex)
                {
                    failed++;
                    details.Add($"路径无效跳过：{item.FullPath}（{ex.Message}）");
                    continue;
                }

                DirectoryIdentity? srcId = _identity.ReadIfExists(src);
                if (srcId is null)
                {
                    details.Add($"已消失安全跳过：{src}");
                    continue;
                }

                if (srcId.IsReparsePoint)
                {
                    failed++;
                    details.Add($"安全拦截（重解析点）：{src}");
                    continue;
                }

                // 强制同卷检查（VolumeGuid 必须一致，绝不跨卷复制删源）
                if (!string.Equals(srcId.VolumeGuid, quarantineRootId.VolumeGuid, StringComparison.OrdinalIgnoreCase))
                {
                    failed++;
                    details.Add($"安全拦截（非同卷对象，不执行跨卷复制删源）：{src}");
                    continue;
                }

                seq++;
                string shortId = $"e{seq:D6}";
                string relativePayload = Path.Combine("data", shortId);
                string destPath = Path.Combine(batchDir, relativePayload);

                // 使用 Win32 MoveFileExW（flags = 0，不设 MOVEFILE_COPY_ALLOWED）确保内核级同卷原子重命名
                if (!MoveFileExW(ToExtendedPath(src), ToExtendedPath(destPath), 0))
                {
                    int win32Err = Marshal.GetLastWin32Error();
                    failed++;
                    string reason = win32Err is 32 or 33
                        ? "文件正被其他程序占用"
                        : new Win32Exception(win32Err).Message;
                    details.Add($"保留原文件（未能移入隔离区）：{src}（{reason}）");
                    continue;
                }

                movedEntries.Add(new QuarantineEntryRecord(
                    ShortId: shortId,
                    OriginalFullPath: src,
                    QuarantinedRelativePath: relativePayload,
                    SizeBytes: item.SizeBytes,
                    IsDirectory: srcId.IsDirectory,
                    OriginalFileId: item.FileId != 0 ? item.FileId : srcId.FileId,
                    OriginalVolumeGuid: srcId.VolumeGuid));
                movedBytes += item.SizeBytes;

                if (progress is not null)
                {
                    long nowTick = Environment.TickCount64;
                    if (processed == totalItems || processed % 40 == 0 || nowTick - lastReportTick >= 80)
                    {
                        lastReportTick = nowTick;
                        progress.Report((processed, totalItems, movedBytes));
                    }
                }
            }

            if (movedEntries.Count == 0)
            {
                TryDeleteEmptyBatchDir(batchDir);
                return ((QuarantineBatchRecord?)null, 0, failed, 0L, details.ToArray());
            }

            var batch = new QuarantineBatchRecord(
                SchemaVersion: 1,
                BatchId: batchId,
                DriveRoot: driveRoot,
                QuarantineDirectoryPath: batchDir,
                DisplayTitle: displayTitle,
                SourceDisplaySummary: sourceDisplaySummary,
                RuleId: ruleId,
                IsFromAiSuspectedResidual: isFromAiSuspectedResidual,
                CreatedAtUtc: DateTimeOffset.UtcNow,
                TotalBytes: movedBytes,
                ItemCount: movedEntries.Count,
                Entries: movedEntries.ToArray());

            WriteManifestAtomically(batchDir, batch);
            return (batch, movedEntries.Count, failed, movedBytes, details.ToArray());
        }, ct);
    }

    /// <summary>
    /// 恢复隔离区批次到原路径。
    /// 当原路径已存在新文件且 allowConflictSuffix == false 时拒绝覆盖；
    /// 当 allowConflictSuffix == true（「对比后恢复」）时保留双方，恢复项自动加 .restored_ 时间戳后缀。
    /// </summary>
    public Task<QuarantineRestoreResult> RestoreBatchAsync(
        QuarantineBatchRecord batch,
        bool allowConflictSuffix = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

        return Task.Run(() =>
        {
            string batchDir = PathIdentityService.Normalize(batch.QuarantineDirectoryPath);
            AssertInsideQuarantineRoot(batchDir);

            DirectoryIdentity? batchId = _identity.ReadIfExists(batchDir);
            if (batchId is null || !batchId.IsDirectory || batchId.IsReparsePoint)
            {
                return new QuarantineRestoreResult(
                    batch.BatchId, false, 0, 0, batch.Entries.Length, false,
                    "隔离批次目录不存在或异常，无法恢复。",
                    [$"批次路径异常：{batchDir}"]);
            }

            // 若不允许后缀保留且检测到冲突，提前阻断并提示用户使用「对比后恢复」
            if (!allowConflictSuffix)
            {
                QuarantineBatchInspection inspection = InspectBatch(batch);
                if (inspection.HasRestoreConflict)
                {
                    return new QuarantineRestoreResult(
                        batch.BatchId, false, 0, 0, inspection.ConflictCount, false,
                        $"检测到原路径已有 {inspection.ConflictCount} 处同名新文件，为防止覆盖请使用「对比后恢复」（保留双方）。",
                        inspection.ConflictSamplePaths.Select(p => $"路径冲突：{p}").ToArray());
                }
            }

            int restored = 0;
            int restoredWithSuffix = 0;
            int skippedOrFailed = 0;
            var remainingEntries = new List<QuarantineEntryRecord>();
            var details = new List<string>();
            string suffixStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            foreach (QuarantineEntryRecord entry in batch.Entries)
            {
                ct.ThrowIfCancellationRequested();

                string quarantinedPath = Path.Combine(batchDir, entry.QuarantinedRelativePath);
                if (!ExistsOnDisk(quarantinedPath))
                {
                    skippedOrFailed++;
                    details.Add($"隔离文件缺失：{entry.OriginalFullPath}");
                    continue;
                }

                string targetPath = PathIdentityService.Normalize(entry.OriginalFullPath);
                string? targetParent = Path.GetDirectoryName(targetPath);
                if (string.IsNullOrWhiteSpace(targetParent))
                {
                    skippedOrFailed++;
                    remainingEntries.Add(entry);
                    details.Add($"目标父目录无效：{targetPath}");
                    continue;
                }

                // 防穿链：核验目标父目录链非重解析点
                if (HasReparsePointInExistingAncestors(targetParent))
                {
                    skippedOrFailed++;
                    remainingEntries.Add(entry);
                    details.Add($"安全拦截（恢复目标父目录含重解析点）：{targetParent}");
                    continue;
                }

                try
                {
                    Directory.CreateDirectory(ToExtendedPath(targetParent));
                }
                catch (Exception ex)
                {
                    skippedOrFailed++;
                    remainingEntries.Add(entry);
                    details.Add($"无法重建原父目录：{targetParent}（{ex.Message}）");
                    continue;
                }

                bool usedSuffix = false;
                if (ExistsOnDisk(targetPath))
                {
                    if (!allowConflictSuffix)
                    {
                        skippedOrFailed++;
                        remainingEntries.Add(entry);
                        details.Add($"原路径已存在文件（已拒绝覆盖）：{targetPath}");
                        continue;
                    }

                    targetPath = BuildNonCollidingRestoredPath(targetPath, entry.IsDirectory, suffixStamp);
                    usedSuffix = true;
                }

                if (!MoveFileExW(ToExtendedPath(quarantinedPath), ToExtendedPath(targetPath), 0))
                {
                    int err = Marshal.GetLastWin32Error();
                    skippedOrFailed++;
                    remainingEntries.Add(entry);
                    details.Add($"恢复失败：{entry.OriginalFullPath}（{new Win32Exception(err).Message}）");
                    continue;
                }

                if (usedSuffix)
                {
                    restoredWithSuffix++;
                    details.Add($"已保留双方并恢复至：{targetPath}");
                }
                else
                {
                    restored++;
                }
            }

            bool removedBatch = false;
            if (remainingEntries.Count == 0 && skippedOrFailed == 0)
            {
                TryDeleteEmptyBatchDir(batchDir);
                removedBatch = !Directory.Exists(ToExtendedPath(batchDir));
            }
            else if (remainingEntries.Count > 0)
            {
                QuarantineBatchRecord updated = batch with
                {
                    Entries = remainingEntries.ToArray(),
                    ItemCount = remainingEntries.Count,
                    TotalBytes = remainingEntries.Sum(e => e.SizeBytes)
                };
                WriteManifestAtomically(batchDir, updated);
            }

            bool succeeded = skippedOrFailed == 0 && (restored + restoredWithSuffix) > 0;
            string msg = succeeded
                ? restoredWithSuffix > 0
                    ? $"已安全恢复 {restored + restoredWithSuffix} 项（其中 {restoredWithSuffix} 项因原路径已有新文件已加后缀保留双方）。"
                    : $"已完整恢复全部 {restored} 项至原路径。"
                : $"部分恢复完成：成功 {restored + restoredWithSuffix} 项，未恢复 {skippedOrFailed} 项（保留在隔离区）。";

            return new QuarantineRestoreResult(
                batch.BatchId,
                succeeded,
                restored,
                restoredWithSuffix,
                skippedOrFailed,
                removedBatch,
                msg,
                details.ToArray());
        }, ct);
    }

    /// <summary>
    /// 人工逐批二次确认后永久删除隔离区批次（释放物理空间）。
    /// 绝不自动触发，绝不跟随重解析点，出现部分失败如实标记 PartiallyDeleted。
    /// </summary>
    public Task<QuarantineDeleteResult> PermanentlyDeleteBatchAsync(
        QuarantineBatchRecord batch,
        string enteredConfirmationPhrase,
        string expectedConfirmationPhrase = DeleteConfirmationPhrase,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (!string.Equals(enteredConfirmationPhrase?.Trim(), expectedConfirmationPhrase.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"确认短语不匹配：请输入「{expectedConfirmationPhrase}」以授权永久删除。");
        }

        return Task.Run(() =>
        {
            string batchDir = PathIdentityService.Normalize(batch.QuarantineDirectoryPath);
            AssertInsideQuarantineRoot(batchDir);

            DirectoryIdentity? batchId = _identity.ReadIfExists(batchDir);
            if (batchId is null)
            {
                return new QuarantineDeleteResult(
                    batch.BatchId, true, false, 0, 0, 0,
                    "该隔离批次目录已不存在。", []);
            }

            if (!batchId.IsDirectory || batchId.IsReparsePoint)
            {
                return new QuarantineDeleteResult(
                    batch.BatchId, false, false, 0, 0, batch.Entries.Length,
                    "隔离批次路径异常（非普通目录或为重解析点），已拒绝删除。",
                    [$"安全拦截：{batchDir}"]);
            }

            int deletedCount = 0;
            int failedCount = 0;
            long freedBytes = 0;
            var remainingEntries = new List<QuarantineEntryRecord>();
            var details = new List<string>();

            foreach (QuarantineEntryRecord entry in batch.Entries)
            {
                ct.ThrowIfCancellationRequested();
                string itemPath = Path.Combine(batchDir, entry.QuarantinedRelativePath);
                if (!ExistsOnDisk(itemPath))
                {
                    deletedCount++;
                    continue;
                }

                try
                {
                    DeleteEntrySafelyNoReparseFollow(itemPath);
                    deletedCount++;
                    freedBytes += entry.SizeBytes;
                }
                catch (Exception ex)
                {
                    failedCount++;
                    remainingEntries.Add(entry);
                    details.Add($"删除失败（已保留）：{entry.OriginalFullPath}（{ex.Message}）");
                }
            }

            if (failedCount == 0)
            {
                try
                {
                    DeleteEntrySafelyNoReparseFollow(batchDir);
                }
                catch (Exception ex)
                {
                    failedCount++;
                    details.Add($"清理批次空壳目录失败：{ex.Message}");
                }
            }
            else
            {
                QuarantineBatchRecord updated = batch with
                {
                    Entries = remainingEntries.ToArray(),
                    ItemCount = remainingEntries.Count,
                    TotalBytes = remainingEntries.Sum(e => e.SizeBytes)
                };
                WriteManifestAtomically(batchDir, updated);
            }

            bool fullyDeleted = failedCount == 0 && !Directory.Exists(ToExtendedPath(batchDir));
            bool partiallyDeleted = !fullyDeleted && deletedCount > 0;
            string message = fullyDeleted
                ? $"已永久删除隔离批次「{batch.DisplayTitle}」（预计释放 {FileStatsService.FormatBytes(freedBytes)}）。"
                : partiallyDeleted
                    ? $"部分删除：已删除 {deletedCount} 项，仍有 {failedCount} 项未能删除并保留在隔离区。"
                    : $"永久删除失败：共 {failedCount} 项未能删除。";

            return new QuarantineDeleteResult(
                batch.BatchId,
                fullyDeleted,
                partiallyDeleted,
                freedBytes,
                deletedCount,
                failedCount,
                message,
                details.ToArray());
        }, ct);
    }

    private IEnumerable<string> EnumerateQuarantineRoots(IReadOnlyList<string>? fixedDriveRoots = null)
    {
        if (_customQuarantineRootOverride is not null)
        {
            yield return _customQuarantineRootOverride;
            yield break;
        }

        if (fixedDriveRoots is not null)
        {
            foreach (string root in fixedDriveRoots)
            {
                if (!string.IsNullOrWhiteSpace(root))
                {
                    yield return Path.Combine(root, QuarantineFolderName);
                }
            }
            yield break;
        }

        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch
        {
            yield break;
        }

        foreach (DriveInfo drive in drives)
        {
            if (drive.DriveType == DriveType.Fixed && drive.IsReady)
            {
                yield return Path.Combine(drive.RootDirectory.FullName, QuarantineFolderName);
            }
        }
    }

    private void EnsureProtectedQuarantineRoot(string quarantineRoot)
    {
        string extPath = ToExtendedPath(quarantineRoot);
        if (!Directory.Exists(extPath))
        {
            Directory.CreateDirectory(extPath);
        }

        DirectoryIdentity id = _identity.ReadDirectory(quarantineRoot, followLink: false);
        if (id.IsReparsePoint)
        {
            throw new IOException($"隔离区根目录不允许为重解析点：{quarantineRoot}");
        }

        // 设置隐藏属性，避免普通用户在盘符根目录误删隔离数据
        try
        {
            FileAttributes attrs = File.GetAttributes(extPath);
            if ((attrs & FileAttributes.Hidden) == 0)
            {
                File.SetAttributes(extPath, attrs | FileAttributes.Hidden);
            }
        }
        catch
        {
        }

        // 收紧 ACL：仅当前用户 + SYSTEM + Builtin Administrators 拥有完全控制权，禁用低权限继承篡改
        try
        {
            var dirInfo = new DirectoryInfo(quarantineRoot);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            SecurityIdentifier? currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is not null)
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    currentUser,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }

            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            dirInfo.SetAccessControl(security);
        }
        catch
        {
            // 非管理员单元测试沙箱或受限卷上 ACL 收紧可能返回权限不足，不阻断功能测试
        }
    }

    private void AssertInsideQuarantineRoot(string batchDir)
    {
        string expectedRoot = ResolveQuarantineRootForPath(batchDir);
        if (!PathIdentityService.IsWithin(batchDir, expectedRoot)
            || batchDir.Equals(expectedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"拒绝操作非隔离区路径：{batchDir}");
        }
    }

    private static void WriteManifestAtomically(string batchDir, QuarantineBatchRecord record)
    {
        string manifestPath = Path.Combine(batchDir, ManifestFileName);
        string tempPath = Path.Combine(batchDir, ManifestFileName + ".tmp");
        string json = JsonSerializer.Serialize(record, JsonOptions);
        File.WriteAllText(ToExtendedPath(tempPath), json);
        File.Move(ToExtendedPath(tempPath), ToExtendedPath(manifestPath), overwrite: true);
    }

    private static void DeleteEntrySafelyNoReparseFollow(string path)
    {
        string ext = ToExtendedPath(path);
        FileAttributes attrs = File.GetAttributes(ext);
        if ((attrs & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"安全拦截：隔离区内意外出现重解析点，拒绝递归删除：{path}");
        }

        if ((attrs & FileAttributes.Directory) == 0)
        {
            if ((attrs & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(ext, attrs & ~FileAttributes.ReadOnly);
            }
            File.Delete(ext);
            return;
        }

        foreach (string file in Directory.GetFiles(ext))
        {
            DeleteEntrySafelyNoReparseFollow(StripExtendedPrefix(file));
        }

        foreach (string subDir in Directory.GetDirectories(ext))
        {
            DeleteEntrySafelyNoReparseFollow(StripExtendedPrefix(subDir));
        }

        if ((attrs & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(ext, attrs & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(ext, recursive: false);
    }

    private static void TryDeleteEmptyBatchDir(string batchDir)
    {
        try
        {
            string ext = ToExtendedPath(batchDir);
            if (!Directory.Exists(ext)) return;
            string dataDir = ToExtendedPath(Path.Combine(batchDir, "data"));
            if (Directory.Exists(dataDir) && !Directory.EnumerateFileSystemEntries(dataDir).Any())
            {
                Directory.Delete(dataDir, recursive: false);
            }
            string manifest = ToExtendedPath(Path.Combine(batchDir, ManifestFileName));
            if (File.Exists(manifest)) File.Delete(manifest);
            if (!Directory.EnumerateFileSystemEntries(ext).Any())
            {
                Directory.Delete(ext, recursive: false);
            }
        }
        catch
        {
        }
    }

    private bool HasReparsePointInExistingAncestors(string path)
    {
        string normalized = PathIdentityService.Normalize(path);
        for (int end = 3; end <= normalized.Length;)
        {
            string seg = normalized[..end];
            DirectoryIdentity? id = _identity.ReadIfExists(seg);
            if (id is { IsReparsePoint: true })
            {
                return true;
            }
            if (end == normalized.Length) break;
            int next = normalized.IndexOf('\\', end + 1);
            end = next < 0 ? normalized.Length : next;
        }
        return false;
    }

    private static string BuildNonCollidingRestoredPath(string originalPath, bool isDirectory, string stamp)
    {
        string parent = Path.GetDirectoryName(originalPath)!;
        string name = Path.GetFileName(originalPath);
        string baseName = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
        string ext = isDirectory ? string.Empty : Path.GetExtension(name);

        for (int i = 0; i < 1000; i++)
        {
            string suffix = i == 0 ? $".restored_{stamp}" : $".restored_{stamp}_{i}";
            string candidate = Path.Combine(parent, baseName + suffix + ext);
            if (!ExistsOnDisk(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(parent, $"{baseName}.restored_{Guid.NewGuid():N}{ext}");
    }

    private static bool ExistsOnDisk(string path)
    {
        string ext = ToExtendedPath(path);
        // .NET 10 Path.Exists 仅触发单次 Win32 属性查询，避免 File.Exists || Directory.Exists 对不存在路径双倍系统调用
        return Path.Exists(ext);
    }

    /// <summary>
    /// 转换为 Windows \\?\ 扩展长路径前缀，彻底规避 260 字符 MAX_PATH 限制。
    /// </summary>
    public static string ToExtendedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        string full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return @"\\?\UNC\" + full[2..];
        }
        return @"\\?\" + full;
    }

    private static string StripExtendedPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
        {
            return @"\\" + path[8..];
        }
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileExW(string lpExistingFileName, string lpNewFileName, uint dwFlags);
}
