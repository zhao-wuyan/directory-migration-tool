using System.Runtime.InteropServices;
using MigrationCore.Models;
using MigrationCore.Models.Cleanup;
using MigrationCore.Models.Scanning;
using MigrationCore.Services.Safety;

namespace MigrationCore.Services.Cleanup;

/// <summary>
/// 通道 B（Windows 官方系统接口）与特殊只读核对处理器：
/// 涵盖 DISM、pnputil、Delivery Optimization、Windows Update Download、Orphaned MSI、Windows.old 与全盘回收站。
/// </summary>
internal sealed class CleanupOfficialApiHandlers
{
    private const uint SherbNoConfirmation = 0x00000001;
    private const uint SherbNoProgressUi = 0x00000002;
    private const uint SherbNoSound = 0x00000004;

    private readonly PathIdentityService _identity;
    private readonly SafetyPolicyEnvironment? _policyEnvironment;
    private readonly CleanupRuleScanner _channelAScanner;
    private readonly Func<CleanupOfficialApiKind, CleanupRuleScanResult, CancellationToken, Task<(bool Succeeded, long ReclaimedBytes, string Message, string[] Details)>>? _officialApiExecutorOverride;

    public CleanupOfficialApiHandlers(
        PathIdentityService identity,
        SafetyPolicyEnvironment? policyEnvironment,
        CleanupRuleScanner channelAScanner,
        Func<CleanupOfficialApiKind, CleanupRuleScanResult, CancellationToken, Task<(bool Succeeded, long ReclaimedBytes, string Message, string[] Details)>>? officialApiExecutorOverride = null)
    {
        _identity = identity;
        _policyEnvironment = policyEnvironment;
        _channelAScanner = channelAScanner;
        _officialApiExecutorOverride = officialApiExecutorOverride;
    }

    public CleanupRuleScanResult ScanOfficialRule(
        CleanupRuleDefinition rule,
        IReadOnlyList<string> protectedTxPaths,
        string[] blockingProcesses,
        DateTimeOffset scannedAt,
        int stepIndex,
        int totalRules,
        CleanupRootResolutionCache? rootCache,
        IProgress<string>? progress,
        CancellationToken ct) =>
        rule.OfficialApiKind switch
        {
            CleanupOfficialApiKind.PnpUtilSupersededDrivers => ScanSupersededDrivers(rule, blockingProcesses, scannedAt, ct),
            CleanupOfficialApiKind.OrphanedInstallerMsi => ScanOrphanedInstallerPackages(rule, protectedTxPaths, blockingProcesses, scannedAt, ct),
            CleanupOfficialApiKind.DismStartComponentCleanup or CleanupOfficialApiKind.DismResetBase => ScanWinSxSComponentStore(rule, blockingProcesses, scannedAt),
            CleanupOfficialApiKind.WindowsOldOfficial => ScanWindowsOldOfficial(rule, blockingProcesses, scannedAt, ct),
            CleanupOfficialApiKind.DeliveryOptimization or CleanupOfficialApiKind.WindowsUpdateDownload => ScanOfficialDirectoryEstimate(rule, protectedTxPaths, blockingProcesses, scannedAt, stepIndex, totalRules, rootCache, progress, ct),
            CleanupOfficialApiKind.RecycleBinAllDrives => ScanRecycleBinAllDrives(rule, blockingProcesses, scannedAt, ct),
            _ => CleanupRuleScanner.EmptyRuleResult(rule, blockingProcesses, scannedAt, "未注册的官方清理接口。")
        };

    public async Task<CleanupExecutionItemResult> ExecuteChannelBRuleAsync(
        CleanupRuleScanResult ruleScan,
        IProgress<string>? progress,
        IProgress<CleanupExecutionProgress>? detailedProgress,
        int ruleIndex,
        int totalRules,
        CancellationToken ct)
    {
        CleanupRuleDefinition rule = ruleScan.Rule;
        detailedProgress?.Report(new CleanupExecutionProgress(
            CompletedRules: ruleIndex,
            TotalRules: totalRules,
            CurrentRuleName: rule.DisplayName,
            PhaseText: "调用 Windows 官方接口",
            ProcessedFiles: 0,
            TotalFiles: ruleScan.MatchedCount,
            ProcessedBytes: 0,
            OverallPercent: (ruleIndex + 0.2) * 100.0 / Math.Max(1, totalRules),
            IsIndeterminate: true,
            Message: $"正在调用 Windows 官方接口：{rule.DisplayName}…"));

        if (_officialApiExecutorOverride is not null)
        {
            var (ok, bytes, msg, det) = await _officialApiExecutorOverride(rule.OfficialApiKind, ruleScan, ct).ConfigureAwait(false);
            return new CleanupExecutionItemResult(
                rule.RuleId,
                rule.DisplayName,
                rule.Channel,
                ok ? CleanupItemExecutionStatus.ReclaimedByOfficialApi : CleanupItemExecutionStatus.Failed,
                bytes,
                ok ? Math.Max(1, ruleScan.MatchedCount) : 0,
                0,
                ok ? 0 : 1,
                null,
                msg,
                det);
        }

        string sys32 = Environment.SystemDirectory;
        switch (rule.OfficialApiKind)
        {
            case CleanupOfficialApiKind.DismStartComponentCleanup:
            case CleanupOfficialApiKind.DismResetBase:
            {
                string dismExe = Path.Combine(sys32, "dism.exe");
                string[] args = rule.OfficialApiKind == CleanupOfficialApiKind.DismResetBase
                    ? ["/Online", "/Cleanup-Image", "/StartComponentCleanup", "/ResetBase"]
                    : ["/Online", "/Cleanup-Image", "/StartComponentCleanup"];

                progress?.Report($"正在调用官方 DISM（可能需要 2～5 分钟）：{rule.DisplayName}…");
                var (exitCode, output) = await SystemToolRunner.RunManagedSystemCommandAsync(dismExe, args, ct).ConfigureAwait(false);
                bool ok = exitCode == 0;
                return new CleanupExecutionItemResult(
                    rule.RuleId,
                    rule.DisplayName,
                    rule.Channel,
                    ok ? CleanupItemExecutionStatus.ReclaimedByOfficialApi : CleanupItemExecutionStatus.Failed,
                    0,
                    ok ? 1 : 0,
                    0,
                    ok ? 0 : 1,
                    null,
                    ok ? "Windows 官方 DISM 组件存储清理已完成（不进入隔离区）。" : $"DISM 返回退出码 {exitCode}。",
                    SystemToolRunner.SplitLines(output, 15));
            }

            case CleanupOfficialApiKind.DeliveryOptimization:
            {
                string psExe = Path.Combine(sys32, @"WindowsPowerShell\v1.0\powershell.exe");
                progress?.Report("正在调用 Windows 官方传递优化清理接口…");
                var (exitCode, output) = await SystemToolRunner.RunManagedSystemCommandAsync(
                    psExe,
                    ["-NoProfile", "-NonInteractive", "-Command", "Delete-DeliveryOptimizationCache -Force"],
                    ct).ConfigureAwait(false);
                bool ok = exitCode == 0;
                return new CleanupExecutionItemResult(
                    rule.RuleId,
                    rule.DisplayName,
                    rule.Channel,
                    ok ? CleanupItemExecutionStatus.ReclaimedByOfficialApi : CleanupItemExecutionStatus.Failed,
                    ok ? ruleScan.TotalBytes : 0,
                    ok ? Math.Max(1, ruleScan.MatchedCount) : 0,
                    0,
                    ok ? 0 : 1,
                    null,
                    ok ? "已通过 Windows 官方传递优化接口完成清理。" : $"传递优化官方接口返回退出码 {exitCode}。",
                    SystemToolRunner.SplitLines(output, 10));
            }

            case CleanupOfficialApiKind.WindowsUpdateDownload:
            {
                if (SystemToolRunner.IsWindowsUpdateServiceRunning())
                {
                    return new CleanupExecutionItemResult(
                        rule.RuleId,
                        rule.DisplayName,
                        rule.Channel,
                        CleanupItemExecutionStatus.Blocked,
                        0, 0, ruleScan.MatchedCount, 0, null,
                        "检测到 Windows Update 服务（wuauserv）正在运行，为避免中断系统更新已自动跳过保护（工具不强停系统服务）。",
                        ["建议等待 Windows 更新完成后重试。"]);
                }

                int deleted = 0;
                int skipped = 0;
                long reclaimed = 0;
                foreach (CleanupDirectoryExpectation dirExp in ruleScan.VerificationExpectations)
                {
                    if (!dirExp.RootDirectoryPath.EndsWith(@"SoftwareDistribution\Download", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    foreach (CleanupFileExpectation f in dirExp.ExpectedFiles)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            DirectoryIdentity? id = _identity.ReadIfExists(f.FullPath);
                            if (id is null || id.IsDirectory || id.IsReparsePoint) { skipped++; continue; }
                            File.Delete(QuarantineStore.ToExtendedPath(f.FullPath));
                            deleted++;
                            reclaimed += f.ExpectedLogicalBytes;
                        }
                        catch
                        {
                            skipped++;
                        }
                    }
                }

                return new CleanupExecutionItemResult(
                    rule.RuleId,
                    rule.DisplayName,
                    rule.Channel,
                    deleted > 0 ? CleanupItemExecutionStatus.ReclaimedByOfficialApi : CleanupItemExecutionStatus.Skipped,
                    reclaimed,
                    deleted,
                    skipped,
                    0,
                    null,
                    deleted > 0
                        ? $"已安全清理 {deleted:N0} 个过期更新下载缓存（{FileStatsService.FormatBytes(reclaimed)}），完整保留 DataStore 更新历史库。"
                        : "无可清理的过期更新下载缓存（可能正被占用或已清理）。",
                    []);
            }

            case CleanupOfficialApiKind.PnpUtilSupersededDrivers:
            {
                string pnpUtilExe = Path.Combine(sys32, "pnputil.exe");
                int removed = 0;
                int failed = 0;
                long reclaimed = 0;
                var details = new List<string>();

                foreach (CleanupPreviewItem item in ruleScan.PreviewItems)
                {
                    ct.ThrowIfCancellationRequested();
                    string oemInf = item.ExtraMetadata ?? item.ItemId;
                    if (!SystemToolRunner.OemInfRegex().IsMatch(oemInf)) continue;

                    progress?.Report($"正在通过 pnputil 移除旧驱动：{oemInf}…");
                    var (code, _) = await SystemToolRunner.RunManagedSystemCommandAsync(pnpUtilExe, ["/delete-driver", oemInf], ct).ConfigureAwait(false);
                    if (code == 0)
                    {
                        removed++;
                        reclaimed += item.SizeBytes;
                        details.Add($"已移除旧驱动：{item.DisplayName}");
                    }
                    else
                    {
                        failed++;
                        details.Add($"已跳过/未移除 {oemInf}（退出码 {code}，可能仍被设备关联）");
                    }
                }

                return new CleanupExecutionItemResult(
                    rule.RuleId,
                    rule.DisplayName,
                    rule.Channel,
                    removed > 0 ? CleanupItemExecutionStatus.ReclaimedByOfficialApi : CleanupItemExecutionStatus.Skipped,
                    reclaimed,
                    removed,
                    failed,
                    0,
                    null,
                    removed > 0
                        ? $"已通过官方 pnputil 移除 {removed} 个已取代旧驱动包（约 {FileStatsService.FormatBytes(reclaimed)}）。"
                        : "未能移除旧驱动包（均被系统判定为仍有关联设备）。",
                    details.ToArray());
            }

            case CleanupOfficialApiKind.WindowsOldOfficial:
            {
                string cleanmgr = Path.Combine(sys32, "cleanmgr.exe");
                if (!File.Exists(cleanmgr))
                {
                    return new CleanupExecutionItemResult(
                        rule.RuleId, rule.DisplayName, rule.Channel,
                        CleanupItemExecutionStatus.Blocked, 0, 0, 1, 0, null,
                        "当前系统缺少官方 cleanmgr.exe，拒绝暴力手删 Windows.old。", []);
                }

                string sysDrive = (Path.GetPathRoot(sys32) ?? @"C:\")[..2];
                progress?.Report("正在拉起 Windows 官方磁盘清理接口处理旧版系统安装…");
                var (code, _) = await SystemToolRunner.RunManagedSystemCommandAsync(cleanmgr, ["/d", sysDrive, "/verylowdisk"], ct).ConfigureAwait(false);
                bool ok = code == 0;
                return new CleanupExecutionItemResult(
                    rule.RuleId,
                    rule.DisplayName,
                    rule.Channel,
                    ok ? CleanupItemExecutionStatus.ReclaimedByOfficialApi : CleanupItemExecutionStatus.Failed,
                    ok ? ruleScan.TotalBytes : 0,
                    ok ? 1 : 0,
                    0,
                    ok ? 0 : 1,
                    null,
                    ok ? "已通过 Windows 官方磁盘清理接口完成旧版 Windows 回收。" : $"官方磁盘清理接口退出码 {code}。",
                    []);
            }

            case CleanupOfficialApiKind.RecycleBinAllDrives:
            {
                string[] targetDrives = ruleScan.PreviewItems
                    .Select(p => p.ExtraMetadata)
                    .Where(d => !string.IsNullOrWhiteSpace(d))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Cast<string>()
                    .ToArray();

                if (targetDrives.Length == 0)
                {
                    return new CleanupExecutionItemResult(
                        rule.RuleId, rule.DisplayName, rule.Channel,
                        CleanupItemExecutionStatus.Skipped, 0, 0, 0, 0, null,
                        "所有固定磁盘的回收站当前均为空。", []);
                }

                long reclaimed = 0;
                int emptiedDrives = 0;
                int failedDrives = 0;
                var details = new List<string>();

                foreach (string driveRoot in targetDrives)
                {
                    ct.ThrowIfCancellationRequested();
                    string label = driveRoot.TrimEnd('\\');
                    progress?.Report($"正在通过官方接口清空 {label} 回收站…");

                    (long beforeBytes, long beforeItems, bool okBefore) = QueryDriveRecycleBin(driveRoot);
                    if (!okBefore || (beforeBytes <= 0 && beforeItems <= 0))
                    {
                        details.Add($"{label} 回收站已为空，已跳过。");
                        continue;
                    }

                    int hr = SHEmptyRecycleBinW(IntPtr.Zero, driveRoot, SherbNoConfirmation | SherbNoProgressUi | SherbNoSound);
                    (long afterBytes, _, bool okAfter) = QueryDriveRecycleBin(driveRoot);
                    if (hr == 0 || (okAfter && afterBytes < beforeBytes))
                    {
                        long delta = okAfter ? Math.Max(0, beforeBytes - afterBytes) : beforeBytes;
                        reclaimed += delta;
                        emptiedDrives++;
                        details.Add($"{label} 回收站已清空（释放 {FileStatsService.FormatBytes(delta)}）");
                    }
                    else
                    {
                        failedDrives++;
                        details.Add($"{label} 回收站清空失败（HRESULT 0x{hr:X8}）");
                    }
                }

                return new CleanupExecutionItemResult(
                    rule.RuleId,
                    rule.DisplayName,
                    rule.Channel,
                    emptiedDrives > 0 ? CleanupItemExecutionStatus.ReclaimedByOfficialApi : (failedDrives > 0 ? CleanupItemExecutionStatus.Failed : CleanupItemExecutionStatus.Skipped),
                    reclaimed,
                    emptiedDrives,
                    0,
                    failedDrives,
                    null,
                    emptiedDrives > 0
                        ? $"已通过 Windows 官方接口清空 {emptiedDrives} 个磁盘回收站（共释放 {FileStatsService.FormatBytes(reclaimed)}）。"
                        : (failedDrives > 0 ? "部分或全部磁盘回收站清空失败，请检查权限或文件占用。" : "各磁盘回收站均为空。"),
                    details.ToArray());
            }

            default:
                return new CleanupExecutionItemResult(
                    rule.RuleId, rule.DisplayName, rule.Channel,
                    CleanupItemExecutionStatus.Blocked, 0, 0, 0, 1, null,
                    "不支持的官方清理接口类型。", []);
        }
    }

    private CleanupRuleScanResult ScanOrphanedInstallerPackages(
        CleanupRuleDefinition rule,
        IReadOnlyList<string> protectedTxPaths,
        string[] blockingProcesses,
        DateTimeOffset scannedAt,
        CancellationToken ct)
    {
        string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string installerDir = Path.Combine(winDir, "Installer");
        if (!Directory.Exists(installerDir))
        {
            return CleanupRuleScanner.EmptyRuleResult(rule, blockingProcesses, scannedAt, "未检测到 Windows\\Installer 目录。");
        }

        HashSet<string> referencedFiles;
        try
        {
            referencedFiles = SystemToolRunner.CollectReferencedWindowsInstallerFiles();
        }
        catch (Exception ex)
        {
            return CleanupRuleScanner.EmptyRuleResult(rule, blockingProcesses, scannedAt, $"无法完整读取 Windows Installer 注册表引用（失败封闭，跳过）：{ex.Message}");
        }

        if (referencedFiles.Count == 0)
        {
            return CleanupRuleScanner.EmptyRuleResult(rule, blockingProcesses, scannedAt, "未读取到任何 Windows Installer 产品引用，按失败封闭原则跳过孤立判定。");
        }

        var previews = new List<CleanupPreviewItem>();
        var expectations = new List<CleanupFileExpectation>();
        long totalBytes = 0;
        int matched = 0;
        int skipped = 0;
        DateTimeOffset cutoff = scannedAt.AddHours(-Math.Max(24, rule.MinAgeHours));

        string[] files;
        try
        {
            files = Directory.GetFiles(installerDir, "*.ms*");
        }
        catch (Exception ex)
        {
            return CleanupRuleScanner.EmptyRuleResult(rule, blockingProcesses, scannedAt, $"无法读取 Windows\\Installer 目录：{ex.Message}");
        }

        foreach (string file in files)
        {
            ct.ThrowIfCancellationRequested();
            string ext = Path.GetExtension(file);
            if (!ext.Equals(".msi", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".msp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string normalized = PathIdentityService.Normalize(file);
            string fileName = Path.GetFileName(normalized);
            if (referencedFiles.Contains(normalized) || referencedFiles.Contains(fileName))
            {
                continue;
            }

            if (CleanupExclusionPolicy.EvaluateFileForRuleCleanup(normalized, protectedTxPaths, _policyEnvironment).IsExcluded)
            {
                continue;
            }

            try
            {
                var fi = new FileInfo(normalized);
                if (!fi.Exists || (fi.Attributes & FileAttributes.ReparsePoint) != 0 || fi.LastWriteTimeUtc > cutoff.UtcDateTime)
                {
                    skipped++;
                    continue;
                }

                DirectoryIdentity? fid = _identity.ReadIfExists(normalized);
                if (fid is null || fid.IsDirectory || fid.IsReparsePoint)
                {
                    continue;
                }

                totalBytes += fi.Length;
                matched++;
                expectations.Add(new CleanupFileExpectation(
                    FullPath: normalized,
                    ExpectedFileId: fid.FileId,
                    ExpectedLogicalBytes: fi.Length,
                    ExpectedLastWriteUtcTicks: fi.LastWriteTimeUtc.Ticks));

                string label = ext.Equals(".msp", StringComparison.OrdinalIgnoreCase)
                    ? $"{fileName} (无产品引用孤立补丁)"
                    : $"{fileName} (无产品引用孤立安装包)";
                previews.Add(new CleanupPreviewItem(
                    ItemId: $"{rule.RuleId}:{fileName}",
                    DisplayName: label,
                    FullPath: normalized,
                    SizeBytes: fi.Length,
                    LastWriteUtcTicks: fi.LastWriteTimeUtc.Ticks,
                    FileId: fid.FileId,
                    IsDirectory: false,
                    KindTag: "隔离区"));
            }
            catch
            {
                skipped++;
            }
        }

        DirectoryIdentity? rootId = _identity.ReadIfExists(installerDir);
        CleanupDirectoryExpectation[] expArr = expectations.Count > 0 && rootId is not null
            ? [new CleanupDirectoryExpectation(installerDir, rootId, scannedAt, expectations.ToArray())]
            : [];

        return new CleanupRuleScanResult(
            Rule: rule,
            PreviewItems: previews.OrderByDescending(p => p.SizeBytes).Take(CleanupService.MaxPreviewItemsPerRule).ToArray(),
            VerificationExpectations: expArr,
            TotalBytes: totalBytes,
            MatchedCount: matched,
            SkippedRecentOrLockedCount: skipped,
            ExcludedProtectedCount: referencedFiles.Count,
            RunningBlockingProcesses: blockingProcesses,
            IsAvailable: matched > 0,
            StatusNote: matched > 0
                ? $"交叉核对 {referencedFiles.Count} 条已安装引用后，识别到 {matched} 个无引用孤立 MSI/MSP（强制先入同卷隔离区）。"
                : $"已核对 {referencedFiles.Count} 条已安装产品引用，未发现孤立 MSI/MSP 补丁。",
            ScannedAtUtc: scannedAt);
    }

    private CleanupRuleScanResult ScanSupersededDrivers(
        CleanupRuleDefinition rule,
        string[] blockingProcesses,
        DateTimeOffset scannedAt,
        CancellationToken ct)
    {
        try
        {
            string pnpUtilExe = Path.Combine(Environment.SystemDirectory, "pnputil.exe");
            if (!File.Exists(pnpUtilExe))
            {
                return CleanupRuleScanner.EmptyRuleResult(rule, blockingProcesses, scannedAt, "系统未提供 pnputil.exe，跳过旧驱动检测。");
            }

            string output = SystemToolRunner.RunReadOnlySystemTool(pnpUtilExe, ["/enum-drivers"], TimeSpan.FromSeconds(15), ct);
            List<SystemToolRunner.ParsedDriverPackage> drivers = SystemToolRunner.ParsePnpUtilDrivers(output);
            if (drivers.Count == 0)
            {
                return CleanupRuleScanner.EmptyRuleResult(rule, blockingProcesses, scannedAt, "未发现可安全识别的已取代第三方旧驱动。");
            }

            var superseded = new List<CleanupPreviewItem>();
            long totalBytes = 0;

            foreach (var group in drivers.GroupBy(d => $"{d.OriginalName}|{d.ProviderName}|{d.ClassName}", StringComparer.OrdinalIgnoreCase))
            {
                List<SystemToolRunner.ParsedDriverPackage> ordered = group
                    .OrderByDescending(d => d.ParsedVersion)
                    .ThenByDescending(d => d.DateStamp)
                    .ToList();
                if (ordered.Count <= 1) continue;

                foreach (SystemToolRunner.ParsedDriverPackage oldPkg in ordered.Skip(1))
                {
                    if (!SystemToolRunner.OemInfRegex().IsMatch(oldPkg.PublishedName)) continue;
                    long estSize = SystemToolRunner.EstimateDriverRepositorySize(oldPkg.OriginalName);
                    totalBytes += estSize;
                    string label = $"{oldPkg.PublishedName} ({oldPkg.ProviderName} {oldPkg.VersionText} 旧驱动)";
                    superseded.Add(new CleanupPreviewItem(
                        ItemId: oldPkg.PublishedName,
                        DisplayName: label,
                        FullPath: oldPkg.PublishedName,
                        SizeBytes: estSize,
                        LastWriteUtcTicks: 0,
                        FileId: 0,
                        IsDirectory: false,
                        KindTag: "pnputil",
                        ExtraMetadata: oldPkg.PublishedName));
                }
            }

            return new CleanupRuleScanResult(
                Rule: rule,
                PreviewItems: superseded.OrderByDescending(p => p.SizeBytes).Take(CleanupService.MaxPreviewItemsPerRule).ToArray(),
                VerificationExpectations: [],
                TotalBytes: totalBytes,
                MatchedCount: superseded.Count,
                SkippedRecentOrLockedCount: 0,
                ExcludedProtectedCount: drivers.Count - superseded.Count,
                RunningBlockingProcesses: blockingProcesses,
                IsAvailable: superseded.Count > 0,
                StatusNote: superseded.Count > 0
                    ? $"通过官方 pnputil 识别到 {superseded.Count} 个已被新版取代的旧驱动包（活跃驱动已自动保护）。"
                    : "当前系统未检测到已被新版取代的冗余第三方驱动。",
                ScannedAtUtc: scannedAt);
        }
        catch (Exception ex)
        {
            return CleanupRuleScanner.EmptyRuleResult(rule, blockingProcesses, scannedAt, $"pnputil 只读枚举失败：{ex.Message}");
        }
    }

    private static CleanupRuleScanResult ScanWinSxSComponentStore(
        CleanupRuleDefinition rule,
        string[] blockingProcesses,
        DateTimeOffset scannedAt)
    {
        string dismExe = Path.Combine(Environment.SystemDirectory, "dism.exe");
        bool available = File.Exists(dismExe) && PathValidator.IsAdministrator();
        string winSxSDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "WinSxS");
        bool hasWinSxS = Directory.Exists(winSxSDir);

        var preview = new CleanupPreviewItem(
            ItemId: rule.RuleId,
            DisplayName: rule.OfficialApiKind == CleanupOfficialApiKind.DismResetBase
                ? "Dism.exe /Online /Cleanup-Image /StartComponentCleanup /ResetBase"
                : "Dism.exe /Online /Cleanup-Image /StartComponentCleanup",
            FullPath: winSxSDir,
            SizeBytes: 0,
            LastWriteUtcTicks: 0,
            FileId: 0,
            IsDirectory: true,
            KindTag: "DISM 官方接口");

        return new CleanupRuleScanResult(
            Rule: rule,
            PreviewItems: available && hasWinSxS ? [preview] : [],
            VerificationExpectations: [],
            TotalBytes: 0,
            MatchedCount: available && hasWinSxS ? 1 : 0,
            SkippedRecentOrLockedCount: 0,
            ExcludedProtectedCount: 0,
            RunningBlockingProcesses: blockingProcesses,
            IsAvailable: available && hasWinSxS,
            StatusNote: available
                ? "官方 DISM 接口已就绪（具体可回收体积由 Windows 组件存储执行时实时计算，不虚报估算值）。"
                : "需要管理员权限与系统 Dism.exe 支持。",
            ScannedAtUtc: scannedAt);
    }

    private static CleanupRuleScanResult ScanWindowsOldOfficial(
        CleanupRuleDefinition rule,
        string[] blockingProcesses,
        DateTimeOffset scannedAt,
        CancellationToken ct)
    {
        string sysRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        string windowsOld = Path.Combine(sysRoot, "Windows.old");
        if (!Directory.Exists(windowsOld))
        {
            return CleanupRuleScanner.EmptyRuleResult(rule, blockingProcesses, scannedAt, "当前系统盘未检测到 Windows.old 旧版系统目录。");
        }

        long estimated = SystemToolRunner.EstimateDirectoryBytesBounded(windowsOld, maxFiles: 10_000, ct);
        var preview = new CleanupPreviewItem(
            ItemId: rule.RuleId,
            DisplayName: "Windows.old (上一版本 Windows 系统备份)",
            FullPath: windowsOld,
            SizeBytes: estimated,
            LastWriteUtcTicks: 0,
            FileId: 0,
            IsDirectory: true,
            KindTag: "官方接口");

        return new CleanupRuleScanResult(
            Rule: rule,
            PreviewItems: [preview],
            VerificationExpectations: [],
            TotalBytes: estimated,
            MatchedCount: 1,
            SkippedRecentOrLockedCount: 0,
            ExcludedProtectedCount: 0,
            RunningBlockingProcesses: blockingProcesses,
            IsAvailable: true,
            StatusNote: $"检测到旧版 Windows.old（约 {FileStatsService.FormatBytes(estimated)}），清理后无法回退旧版系统。",
            ScannedAtUtc: scannedAt);
    }

    private CleanupRuleScanResult ScanOfficialDirectoryEstimate(
        CleanupRuleDefinition rule,
        IReadOnlyList<string> protectedTxPaths,
        string[] blockingProcesses,
        DateTimeOffset scannedAt,
        int stepIndex,
        int totalRules,
        CleanupRootResolutionCache? rootCache,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        CleanupRuleScanResult baseScan = _channelAScanner.ScanChannelAFiles(
            rule, protectedTxPaths, [], blockingProcesses, scannedAt, stepIndex, totalRules, rootCache, progress, ct);
        CleanupPreviewItem[] taggedPreviews = baseScan.PreviewItems
            .Select(p => p with { KindTag = "官方接口" })
            .ToArray();

        return baseScan with
        {
            PreviewItems = taggedPreviews,
            StatusNote = baseScan.MatchedCount > 0
                ? $"检测到约 {FileStatsService.FormatBytes(baseScan.TotalBytes)}（{baseScan.MatchedCount:N0} 项），将通过 Windows 官方接口安全回收。"
                : "当前无待回收的系统更新/传递优化缓存。"
        };
    }

    private static CleanupRuleScanResult ScanRecycleBinAllDrives(
        CleanupRuleDefinition rule,
        string[] blockingProcesses,
        DateTimeOffset scannedAt,
        CancellationToken ct)
    {
        var previews = new List<CleanupPreviewItem>();
        long totalBytes = 0;
        long totalItems = 0;

        DriveInfo[] fixedDrives;
        try
        {
            fixedDrives = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return CleanupRuleScanner.EmptyRuleResult(rule, blockingProcesses, scannedAt, "无法枚举本地固定磁盘。");
        }

        foreach (DriveInfo drive in fixedDrives)
        {
            ct.ThrowIfCancellationRequested();
            (long bytes, long items, bool ok) = QueryDriveRecycleBin(drive.Name);
            if (!ok || (bytes <= 0 && items <= 0)) continue;

            string driveLabel = drive.Name.TrimEnd('\\');
            totalBytes += bytes;
            totalItems += items;
            previews.Add(new CleanupPreviewItem(
                ItemId: $"recyclebin:{driveLabel}",
                DisplayName: $"{driveLabel} 回收站（{items:N0} 个已删除项目）",
                FullPath: Path.Combine(drive.Name, "$Recycle.Bin"),
                SizeBytes: bytes,
                LastWriteUtcTicks: scannedAt.UtcTicks,
                FileId: 0,
                IsDirectory: true,
                KindTag: "官方回收站",
                ExtraMetadata: drive.Name));
        }

        if (previews.Count == 0)
        {
            return CleanupRuleScanner.EmptyRuleResult(rule, blockingProcesses, scannedAt, $"已检测 {fixedDrives.Length} 个固定磁盘，各卷回收站当前均为空。");
        }

        string driveList = string.Join("、", previews.Select(p => p.ExtraMetadata?.TrimEnd('\\')));
        return new CleanupRuleScanResult(
            Rule: rule,
            PreviewItems: previews.OrderByDescending(p => p.SizeBytes).ToArray(),
            VerificationExpectations: [],
            TotalBytes: totalBytes,
            MatchedCount: (int)Math.Min(int.MaxValue, totalItems),
            SkippedRecentOrLockedCount: 0,
            ExcludedProtectedCount: 0,
            RunningBlockingProcesses: blockingProcesses,
            IsAvailable: true,
            StatusNote: $"共在 {previews.Count} 个磁盘（{driveList}）发现 {totalItems:N0} 个回收站项目，总计 {FileStatsService.FormatBytes(totalBytes)}。",
            ScannedAtUtc: scannedAt);
    }

    private static (long Bytes, long Items, bool Succeeded) QueryDriveRecycleBin(string driveRoot)
    {
        try
        {
            var info = new ShQueryRbInfo { cbSize = (uint)Marshal.SizeOf<ShQueryRbInfo>() };
            int hr = SHQueryRecycleBinW(driveRoot, ref info);
            if (hr == 0)
            {
                return (Math.Max(0, info.i64Size), Math.Max(0, info.i64NumItems), true);
            }
        }
        catch
        {
        }
        return (0, 0, false);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct ShQueryRbInfo
    {
        public uint cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string? pszRootPath, ref ShQueryRbInfo pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? pszRootPath, uint dwFlags);
}
