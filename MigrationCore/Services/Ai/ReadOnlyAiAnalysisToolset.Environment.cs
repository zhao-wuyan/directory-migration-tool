using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using MigrationCore.Models.Ai;
using MigrationCore.Models.Safety;
using MigrationCore.Models.Scanning;
using MigrationCore.Services.Safety;
using MigrationCore.Services.Scanning;

namespace MigrationCore.Services.Ai;

public sealed partial class ReadOnlyAiAnalysisToolset
{
    private ScanSnapshot RequireAuthorizedSnapshot(Guid scanId)
    {
        if (!TryGetAuthorizedSnapshot(scanId, out ScanSnapshot? snapshot) || snapshot is null)
        {
            throw new UnauthorizedAccessException($"扫描快照 {scanId} 未在当前会话授权范围内或已撤销。");
        }
        return snapshot;
    }

    private SafetyPolicyDecision EvaluateSnapshotPath(string fullPath) =>
        MigrationSafetyPolicy.EvaluateSource(
            fullPath,
            _catalog,
            SafetyEvaluationContext.ScanPreview,
            checkFilesystem: false,
            environment: _policyEnvironment);

    private AiTargetCandidateDto[] GetTargetCandidatesForSourceDrive(string sourceDriveRoot)
    {
        IReadOnlyList<AiTargetCandidateDto> all = _targetCandidatesProvider();
        string normSource = sourceDriveRoot.TrimEnd('\\');
        return all
            .Where(t => !t.DriveLetter.TrimEnd('\\').Equals(normSource, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static IReadOnlyList<AiTargetCandidateDto> DefaultDiscoverTargetCandidates()
    {
        WorkspaceStateSnapshot state = SharedWorkspaceStateCache.Shared.GetOrRefreshSync();
        var list = new List<AiTargetCandidateDto>();
        foreach (SharedDriveTelemetry drive in state.Drives)
        {
            if (!drive.IsFixedDrive || !drive.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) continue;

            string letter = drive.DriveLetter;
            string driveKey = letter.TrimEnd(':').ToLowerInvariant();
            bool recommended = !drive.IsSystemDrive && drive.FreeBytes >= 20L * 1024 * 1024 * 1024;

            list.Add(new AiTargetCandidateDto(
                TargetCandidateId: $"target-vol-{driveKey}",
                DriveLetter: letter,
                SuggestedRoot: Path.Combine(drive.DriveRoot, "MigratedData"),
                AvailableBytes: drive.FreeBytes,
                IsRecommended: recommended));
        }

        return list.OrderByDescending(x => x.IsRecommended).ThenByDescending(x => x.AvailableBytes).ToArray();
    }

    private AiSystemEnvironmentResponse CollectSystemEnvironment(int authorizedScanCount)
    {
        WorkspaceStateSnapshot shared = SharedWorkspaceStateCache.Shared.GetOrRefreshSync();
        string systemDrive = shared.SystemDrive;
        bool isAdmin = shared.IsAdministrator;
        bool isDevMode = shared.IsDeveloperModeEnabled;
        bool canCreateSymlinks = isAdmin || isDevMode;

        IReadOnlyList<AiTargetCandidateDto> targetCandidates = _targetCandidatesProvider();
        var targetMap = targetCandidates
            .GroupBy(t => t.DriveLetter.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var drives = new List<AiDiskVolumeInfoDto>(shared.Drives.Length);
        foreach (SharedDriveTelemetry drive in shared.Drives)
        {
            string letter = drive.DriveLetter;
            bool isSys = drive.IsSystemDrive;
            bool isFixed = drive.IsFixedDrive;
            bool isNtfs = drive.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase);

            targetMap.TryGetValue(letter, out AiTargetCandidateDto? candidate);
            bool supportsTarget = drive.SupportsSymlinkMigrationTarget;
            string? targetNote = isSys
                ? "系统盘（通常作为迁出源盘，不建议作为迁移目标）"
                : !isFixed
                    ? $"驱动器类型为 {drive.DriveType}（非本地固定磁盘，不适合作为符号链接目标）"
                    : !isNtfs
                        ? $"文件系统为 {drive.FileSystem}（仅固定 NTFS 卷支持安全符号链接迁移）"
                        : drive.FreeBytes < 20L * 1024 * 1024 * 1024
                            ? "剩余空间低于 20 GB，迁入大目录前请确认余量"
                            : "本地固定 NTFS 卷，适合作为安全迁移目标";

            string safeLabel = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                ? string.Empty
                : AiPathSanitizer.SanitizeUntrustedText(drive.VolumeLabel, 48, preserveNewLines: false);

            drives.Add(new AiDiskVolumeInfoDto(
                DriveLetter: letter,
                VolumeLabel: safeLabel,
                DriveType: drive.DriveType,
                FileSystem: drive.FileSystem,
                TotalBytes: drive.TotalBytes,
                UsedBytes: drive.UsedBytes,
                FreeBytes: drive.FreeBytes,
                UsedPercent: drive.UsedPercent,
                IsSystemDrive: isSys,
                SupportsSymlinkMigrationTarget: supportsTarget,
                TargetCandidateId: candidate?.TargetCandidateId,
                TargetNote: targetNote));
        }

        var specialFolders = new List<AiSpecialFolderLocationDto>();
        AddSanitizedSpecialFolder(specialFolders, "Desktop", "桌面", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), systemDrive);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            string downloads = Path.Combine(userProfile, "Downloads");
            if (Directory.Exists(downloads))
            {
                AddSanitizedSpecialFolder(specialFolders, "Downloads", "下载", downloads, systemDrive);
            }
        }
        AddSanitizedSpecialFolder(specialFolders, "Documents", "文档", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), systemDrive);
        AddSanitizedSpecialFolder(specialFolders, "LocalAppData", "本地应用数据 (AppData\\Local)", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), systemDrive);
        AddSanitizedSpecialFolder(specialFolders, "RoamingAppData", "漫游应用数据 (AppData\\Roaming)", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), systemDrive);
        AddSanitizedSpecialFolder(specialFolders, "Temp", "系统临时目录 (Temp)", Path.GetTempPath(), systemDrive);

        string[] authorizedScanDrives = GetAuthorizedSnapshots()
            .Select(s => s.Volume.DriveRoot.TrimEnd('\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        bool hasCleanupScan = GetLatestAuthorizedCleanupSnapshot() is not null;
        AiBackupAndQuarantineSummaryDto reclaimSummary = BuildStorageReclaimSummary(shared);

        return new AiSystemEnvironmentResponse(
            OsDescription: RuntimeInformation.OSDescription,
            OsVersion: Environment.OSVersion.Version.ToString(),
            OsArchitecture: RuntimeInformation.OSArchitecture.ToString(),
            SystemDrive: systemDrive,
            IsAdministrator: isAdmin,
            IsDeveloperModeEnabled: isDevMode,
            CanCreateSymlinks: canCreateSymlinks,
            CanUseRawNtfsMftScan: isAdmin,
            AuthorizedScanCount: authorizedScanCount,
            Drives: drives.OrderBy(d => d.DriveLetter, StringComparer.OrdinalIgnoreCase).ToArray(),
            SpecialFolders: specialFolders.ToArray(),
            PrivacyNotice: "已严格脱敏：仅包含磁盘卷容量/文件系统、OS 版本、权限前提、备份/隔离区统计与 <USER> 脱敏目录位置，绝不含真实用户名、计算机名、网络地址、环境变量或任何文件内容。",
            StorageReclaimSummary: reclaimSummary,
            AuthorizedScanDrives: authorizedScanDrives,
            HasCleanupScanSnapshot: hasCleanupScan);
    }

    private static AiBackupAndQuarantineSummaryDto BuildStorageReclaimSummary(WorkspaceStateSnapshot shared)
    {
        var parts = new List<string>();
        if (shared.Backups.UnverifiedBackupCount > 0)
        {
            string sizeHint = shared.Backups.HasEstimatedBytes
                ? $"（已估算约 {FileStatsService.FormatBytes(shared.Backups.EstimatedBackupBytes)}）"
                : string.Empty;
            parts.Add($"待确认释放的迁移备份 {shared.Backups.UnverifiedBackupCount} 项{sizeHint}");
        }
        if (shared.Backups.RecoveryRequiredCount > 0)
        {
            parts.Add($"需先核验的中断迁移事务 {shared.Backups.RecoveryRequiredCount} 项");
        }
        if (shared.Quarantine.TotalBatchCount > 0)
        {
            parts.Add($"清理隔离区待释放 {shared.Quarantine.TotalBatchCount} 批（共 {FileStatsService.FormatBytes(shared.Quarantine.TotalBytes)}）");
        }

        string note = parts.Count == 0
            ? "当前无待确认释放的迁移备份或清理隔离区占用。"
            : $"检测到可直接通过工具安全释放的已有留存空间：{string.Join("；", parts)}。请提醒用户在「备份管理」或「清理隔离区」确认无误后手动释放。";

        return new AiBackupAndQuarantineSummaryDto(
            UnverifiedBackupCount: shared.Backups.UnverifiedBackupCount,
            EstimatedBackupBytes: shared.Backups.EstimatedBackupBytes,
            HasEstimatedBackupBytes: shared.Backups.HasEstimatedBytes,
            RecoveryRequiredCount: shared.Backups.RecoveryRequiredCount,
            QuarantineBatchCount: shared.Quarantine.TotalBatchCount,
            QuarantineBytes: shared.Quarantine.TotalBytes,
            SummaryNote: note);
    }

    private static void AddSanitizedSpecialFolder(
        List<AiSpecialFolderLocationDto> list,
        string key,
        string displayName,
        string? rawPath,
        string systemDrive)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return;
        string trimmed = rawPath.TrimEnd('\\');
        string driveLetter = (Path.GetPathRoot(trimmed) ?? string.Empty).TrimEnd('\\');
        if (string.IsNullOrWhiteSpace(driveLetter)) return;

        string sanitized = AiPathSanitizer.SanitizePath(trimmed);
        bool onSys = driveLetter.Equals(systemDrive, StringComparison.OrdinalIgnoreCase);
        list.Add(new AiSpecialFolderLocationDto(key, displayName, driveLetter, sanitized, onSys));
    }

    private static bool IsWindowsDeveloperModeEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock",
                writable: false);
            object? val = key?.GetValue("AllowDevelopmentWithoutDevLicense");
            return val is int intVal && intVal == 1;
        }
        catch
        {
            return false;
        }
    }

    private static AiScanSummaryItem BuildScanSummaryItem(ScanSnapshot snapshot)
    {
        string letter = snapshot.Volume.DriveRoot.TrimEnd('\\');
        string label = string.IsNullOrWhiteSpace(snapshot.Volume.VolumeLabel)
            ? $"本地磁盘 ({letter})"
            : $"{snapshot.Volume.VolumeLabel} ({letter})";

        long ageSec = Math.Max(0, (long)(DateTimeOffset.UtcNow - snapshot.CompletedAtUtc).TotalSeconds);

        return new AiScanSummaryItem(
            ScanId: snapshot.ScanId,
            VolumeDisplay: label,
            DriveLetter: letter,
            Backend: snapshot.Backend,
            Coverage: snapshot.Coverage,
            CanUseForMigrationPlanning: snapshot.CanUseForMigrationPlanning,
            TotalBytes: snapshot.Volume.TotalBytes,
            UsedBytes: snapshot.Volume.UsedBytes,
            FreeBytes: snapshot.Volume.FreeBytes,
            PolicyVersion: snapshot.PolicyVersion,
            CompletedAtUtc: snapshot.CompletedAtUtc,
            ScanAgeSeconds: ageSec,
            NodeCount: snapshot.Nodes.Length,
            IncompleteNodeCount: snapshot.IncompleteNodeCount,
            RequiresLiveReverification: snapshot.RequiresLiveReverification);
    }

    private static bool HasChildDirectories(ScanSnapshot snapshot, int nodeId)
    {
        ReadOnlySpan<int> children = snapshot.GetChildrenSpan(nodeId);
        for (int i = 0; i < children.Length; i++)
        {
            int cid = children[i];
            ref readonly ScanNodeRecord c = ref snapshot.Nodes[cid];
            if (c.IsDirectory && !c.IsOrphaned && !MigrationSafetyPolicy.IsHiddenSystemVolumeNode(snapshot, cid))
            {
                return true;
            }
        }
        return false;
    }

    private int[] CollectDisjointHotspots(ScanSnapshot snapshot, long minAllocatedBytes, int maxCount, int? startNodeId = null) =>
        ScanHotspotAnalyzer.CollectDisjointHotspots(
            snapshot,
            _catalog,
            minAllocatedBytes,
            maxCount,
            HotspotTraversalOptions.ForAiToolset(startNodeId, _policyEnvironment));

    private static bool IsContainerToDescend(string fullPath, SafetyPolicyDecision decision) =>
        ScanHotspotAnalyzer.IsStrictDescendableL0Container(fullPath, decision);

    private static string BuildDisplayName(ScanSnapshot snapshot, int nodeId, string? matchedAppName)
    {
        string leaf = AiPathSanitizer.SanitizeUntrustedText(snapshot.GetNodeName(nodeId), 80, false);
        if (!string.IsNullOrWhiteSpace(matchedAppName)
            && !leaf.Equals(matchedAppName, StringComparison.OrdinalIgnoreCase))
        {
            return $"{matchedAppName} / {leaf}";
        }

        int parentId = snapshot.Nodes[nodeId].ParentNodeId;
        if (parentId >= 0 && parentId != snapshot.RootNodeId && parentId != nodeId)
        {
            string parentName = AiPathSanitizer.SanitizeUntrustedText(snapshot.GetNodeName(parentId), 60, false);
            if (!string.IsNullOrWhiteSpace(parentName)
                && !parentName.Equals("Users", StringComparison.OrdinalIgnoreCase)
                && !parentName.Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase))
            {
                return $"{parentName} / {leaf}";
            }
        }

        return leaf;
    }

    private static string NormalizeCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return "Migration";
        string c = category.Trim();
        if (c.Equals("SuspectedCacheAdvisory", StringComparison.OrdinalIgnoreCase)
            || c.Equals("SuspectedResidual", StringComparison.OrdinalIgnoreCase)
            || c.Equals("CleanupAdvisory", StringComparison.OrdinalIgnoreCase))
        {
            return "SuspectedCacheAdvisory";
        }
        if (c.Equals("NativeGuideAdvisory", StringComparison.OrdinalIgnoreCase)
            || c.Equals("NativeGuide", StringComparison.OrdinalIgnoreCase)
            || c.Equals("VendorNative", StringComparison.OrdinalIgnoreCase)
            || c.Equals("VendorNativeRecommended", StringComparison.OrdinalIgnoreCase)
            || c.Equals("L2NativeGuide", StringComparison.OrdinalIgnoreCase))
        {
            return "NativeGuideAdvisory";
        }
        return "Migration";
    }

    private static string? TryBuildTargetPath(string sourcePath, string targetRoot, string? appId)
    {
        try
        {
            return MigrationPlanService.BuildDefaultTargetPath(sourcePath, targetRoot, appId);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSameOrAncestorOrDescendant(string pathA, string pathB)
    {
        string a = pathA.TrimEnd('\\');
        string b = pathB.TrimEnd('\\');
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return true;
        return a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase)
            || b.StartsWith(a + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static int ParseCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return 0;
        return int.TryParse(cursor.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed >= 0
            ? parsed
            : 0;
    }

    private static AiMigrationProposalReceipt BuildRejectedEnvelope(string clientRequestId, Guid scanId, string message) =>
        new(
            ProposalId: Guid.NewGuid(),
            ClientRequestId: clientRequestId,
            ScanId: scanId,
            PolicyVersion: MigrationSafetyPolicy.CurrentPolicyVersion,
            ReceivedAtUtc: DateTimeOffset.UtcNow,
            Status: AiProposalStatus.Invalidated,
            IsIdempotentReplay: false,
            AcceptedCount: 0,
            BlockedByPolicyCount: 0,
            RejectedCount: 0,
            Items: [],
            Disclaimer: MandatoryDisclaimer,
            RejectionMessage: message);
}
