using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MigrationCore.Models.Cleanup;
using MigrationCore.Services;
using MigrationCore.Services.Cleanup;
using MoveWithSymlinkWPF.Helpers;
using MoveWithSymlinkWPF.Views;

namespace MoveWithSymlinkWPF.ViewModels;

public partial class CleanupViewModel
{
    private sealed record FixedDriveSnapshot(
        string DriveRoot,
        string DriveLabel,
        string VolumeLabel,
        long TotalBytes,
        long FreeBytes);

    /// <summary>
    /// 进入垃圾清理页时的非阻塞入口（兼容同步调用方，内部统一转交后台异步刷新，绝不卡死切页渲染）。
    /// </summary>
    public void OnEnteredCleanupPage() => _ = OnEnteredCleanupPageAsync();

    /// <summary>
    /// 进入垃圾清理页时在后台合并刷新隔离区摘要（跳过逐文件冲突遍历）、跨盘源探测与全盘空间态势；
    /// MFT 提示走纯内存读取即刻更新，绝不自动启动磁盘扫描，并在短 TTL 内复用缓存防止来回切页重复扫盘。
    /// </summary>
    public async Task OnEnteredCleanupPageAsync(bool force = false)
    {
        RefreshMftCacheHint();
        if (!HasScanSnapshot && !IsScanning && VisibleIdleScopePreviews.Count == 0)
        {
            SetIdleCategorySubtitles();
            RebuildIdleScopePreviews();
        }

        if (!force
            && !_overviewDirty
            && (DateTimeOffset.UtcNow - _lastOverviewRefreshedUtc) < OverviewRefreshTtl)
        {
            return;
        }

        CancellationTokenSource? oldCts = Interlocked.Exchange(ref _overviewRefreshCts, new CancellationTokenSource());
        try { oldCts?.Cancel(); } catch { }
        oldCts?.Dispose();

        CancellationToken ct = _overviewRefreshCts!.Token;
        long version = Interlocked.Increment(ref _overviewRefreshVersion);
        DispatchToUi(() =>
        {
            if (version == Volatile.Read(ref _overviewRefreshVersion))
            {
                IsRefreshingOverview = true;
            }
        });

        try
        {
            var payload = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                FixedDriveSnapshot[] drives = CollectFixedDriveSnapshots(ct);
                string[] driveRoots = drives.Select(d => d.DriveRoot).ToArray();

                // 主视图仅需角标批次数与按盘总占用，传 checkRestoreConflicts: false 跳过逐文件 ExistsOnDisk 核验
                IReadOnlyList<QuarantineBatchInspection> quarantineLite;
                try
                {
                    quarantineLite = _quarantineStore.ListAllInspections(
                        checkRestoreConflicts: false,
                        fixedDriveRoots: driveRoots,
                        ct: ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    quarantineLite = [];
                }

                IReadOnlyList<CrossDriveDiscoveryItem> crossDrives;
                try
                {
                    crossDrives = _cleanupService.DiscoverCrossDriveTargets(ct: ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    crossDrives = [];
                }

                List<CleanupDiskOverviewItem> disks = BuildDiskOverviewsFromSnapshots(drives, quarantineLite, crossDrives);
                return (quarantineLite, crossDrives, disks);
            }, ct).ConfigureAwait(false);

            await DispatchToUiAsync(() =>
            {
                if (ct.IsCancellationRequested || version != Volatile.Read(ref _overviewRefreshVersion))
                {
                    return;
                }

                ApplyQuarantineSummaryFromOverview(payload.quarantineLite);
                bool crossChanged = ApplyCrossDriveDiscoveries(payload.crossDrives);
                ApplyDiskOverviews(payload.disks);
                RefreshMftCacheHint();

                if (!HasScanSnapshot && !IsScanning && (crossChanged || VisibleIdleScopePreviews.Count == 0))
                {
                    SetIdleCategorySubtitles();
                    RebuildIdleScopePreviews();
                }

                _overviewDirty = false;
                _lastOverviewRefreshedUtc = DateTimeOffset.UtcNow;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await DispatchToUiAsync(() =>
            {
                if (version == Volatile.Read(ref _overviewRefreshVersion))
                {
                    IsRefreshingOverview = false;
                }
            }).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 扫描前快速探测跨盘缓存源（兼容同步调用方；UI 切页路径已改为 OnEnteredCleanupPageAsync 后台执行）。
    /// </summary>
    public void RefreshCrossDriveDiscovery()
    {
        IReadOnlyList<CrossDriveDiscoveryItem> discovered;
        try
        {
            discovered = _cleanupService.DiscoverCrossDriveTargets();
        }
        catch
        {
            discovered = [];
        }

        ApplyCrossDriveDiscoveries(discovered);
    }

    private bool ApplyCrossDriveDiscoveries(IReadOnlyList<CrossDriveDiscoveryItem> discovered)
    {
        string signature = BuildCrossDriveSignature(discovered);
        bool changed = !string.Equals(signature, _lastCrossDriveSignature, StringComparison.Ordinal);

        if (changed)
        {
            CrossDriveDiscoveries.Clear();
            foreach (CrossDriveDiscoveryItem item in discovered)
            {
                CrossDriveDiscoveries.Add(item);
            }
            _lastCrossDriveSignature = signature;
        }

        CrossDriveDiscoveryCount = CrossDriveDiscoveries.Count;
        if (CrossDriveDiscoveryCount > 0)
        {
            string[] drives = CrossDriveDiscoveries
                .Select(d => d.DriveLabel)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            CrossDriveBadgeText = $"已探测 {CrossDriveDiscoveryCount} 处跨盘源";
            CrossDriveSummaryText = $"已在 {string.Join(" / ", drives)} 自动探测到 {CrossDriveDiscoveryCount} 处非系统盘缓存源（环境变量、跨盘软件配置或已登记迁移符号链接），全盘回收站也将同步检测。";
        }
        else
        {
            CrossDriveBadgeText = "多盘规则就绪";
            CrossDriveSummaryText = "已启用多盘规则支持：自动识别环境变量重定向、Steam/IDE 跨盘配置、全盘回收站及本工具已登记的迁移符号链接。";
        }

        return changed;
    }

    private static string BuildCrossDriveSignature(IReadOnlyList<CrossDriveDiscoveryItem> items)
    {
        if (items.Count == 0) return string.Empty;
        return string.Join("\n", items
            .OrderBy(i => i.RuleId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.DriveLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.DisplayPath, StringComparer.OrdinalIgnoreCase)
            .Select(i => $"{i.RuleId}|{i.DriveLabel}|{i.DisplayPath}"));
    }

    /// <summary>
    /// 实时读取本机所有就绪固定磁盘容量、MFT 缓存、隔离区占用与跨盘缓存源数，填充右栏 Top 3 与「查看全部」集合。
    /// </summary>
    public void RefreshDiskOverviews()
    {
        FixedDriveSnapshot[] drives = CollectFixedDriveSnapshots(CancellationToken.None);
        IReadOnlyList<QuarantineBatchInspection> currentQuarantine = QuarantineBatches.Select(b => b.Inspection).ToArray();
        List<CleanupDiskOverviewItem> list = BuildDiskOverviewsFromSnapshots(drives, currentQuarantine, CrossDriveDiscoveries);
        ApplyDiskOverviews(list);
    }

    private static FixedDriveSnapshot[] CollectFixedDriveSnapshots(CancellationToken ct)
    {
        WorkspaceStateSnapshot shared = SharedWorkspaceStateCache.Shared.GetOrRefreshSync(force: false, ct: ct);
        return shared.Drives
            .Where(d => d.IsFixedDrive)
            .Select(d => new FixedDriveSnapshot(d.DriveRoot, d.DriveLetter, d.VolumeLabel, d.TotalBytes, d.FreeBytes))
            .ToArray();
    }

    private static List<CleanupDiskOverviewItem> BuildDiskOverviewsFromSnapshots(
        IReadOnlyList<FixedDriveSnapshot> drives,
        IReadOnlyList<QuarantineBatchInspection> quarantineInspections,
        IReadOnlyList<CrossDriveDiscoveryItem> crossDriveDiscoveries)
    {
        string sysDrive = MigrationCore.Services.Scanning.VolumeSnapshotCache.NormalizeDriveRoot(
            Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\");

        var quarantineByDrive = quarantineInspections
            .GroupBy(b => MigrationCore.Services.Scanning.VolumeSnapshotCache.NormalizeDriveRoot(b.Batch.DriveRoot), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (Bytes: g.Sum(x => x.Batch.TotalBytes), Count: g.Count()), StringComparer.OrdinalIgnoreCase);

        var crossByDrive = crossDriveDiscoveries
            .GroupBy(d => d.DriveLabel, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var list = new List<CleanupDiskOverviewItem>(drives.Count);
        foreach (FixedDriveSnapshot d in drives)
        {
            bool hasFreshMft = false;
            int mftMins = 0;
            var mftLookup = MigrationCore.Services.Scanning.VolumeSnapshotCache.Shared.TryGet(d.DriveRoot, runLiveProbe: false, touchAccessTime: false);
            if (mftLookup is { IsFresh: true } && mftLookup.Entry.Snapshot.Backend == MigrationCore.Models.Scanning.ScanBackendKind.RawNtfsMft)
            {
                hasFreshMft = true;
                mftMins = Math.Max(0, (int)Math.Round(mftLookup.Age.TotalMinutes));
            }

            quarantineByDrive.TryGetValue(d.DriveRoot, out var qInfo);
            crossByDrive.TryGetValue(d.DriveLabel, out int crossCount);

            list.Add(new CleanupDiskOverviewItem
            {
                DriveRoot = d.DriveRoot,
                DriveLabel = d.DriveLabel,
                VolumeLabel = d.VolumeLabel,
                IsSystemDrive = d.DriveRoot.Equals(sysDrive, StringComparison.OrdinalIgnoreCase),
                TotalBytes = d.TotalBytes,
                FreeBytes = d.FreeBytes,
                HasFreshMftCache = hasFreshMft,
                MftAgeMinutes = mftMins,
                QuarantineBytes = qInfo.Bytes,
                QuarantineBatchCount = qInfo.Count,
                CrossDriveSourceCount = crossCount
            });
        }

        return list;
    }

    /// <summary>
    /// 填充全盘空间概览（智能排序：系统盘置顶 → 空间紧张(≥85%) → 含隔离区 → 已用占比降序），
    /// 右栏仅展示前 3 块防止多盘撑爆布局，超过 3 块自动展示汇总与「查看全部」弹框入口。
    /// </summary>
    public void ApplyDiskOverviews(IEnumerable<CleanupDiskOverviewItem> disks)
    {
        CleanupDiskOverviewItem[] sorted = CleanupDiskOverviewWindow.FilterAndSort(
            disks.ToArray(),
            query: null,
            CleanupDiskSortMode.Urgency);

        string signature = string.Join("\n", sorted.Select(d =>
            $"{d.DriveRoot}|{d.VolumeLabel}|{d.TotalBytes}|{d.FreeBytes}|{d.HasFreshMftCache}|{d.MftAgeMinutes}|{d.QuarantineBytes}|{d.QuarantineBatchCount}|{d.CrossDriveSourceCount}"));
        if (AllDiskOverviews.Count == sorted.Length
            && string.Equals(signature, _lastDiskOverviewSignature, StringComparison.Ordinal))
        {
            return;
        }
        _lastDiskOverviewSignature = signature;

        AllDiskOverviews.Clear();
        TopDiskOverviews.Clear();

        foreach (CleanupDiskOverviewItem d in sorted)
        {
            AllDiskOverviews.Add(d);
        }

        foreach (CleanupDiskOverviewItem d in sorted.Take(MaxInlineDiskOverviewCount))
        {
            TopDiskOverviews.Add(d);
        }

        TotalDiskCount = AllDiskOverviews.Count;
        int tightTotal = AllDiskOverviews.Count(d => d.UsedPercent >= 85d);

        DiskOverviewHeaderSummary = TotalDiskCount == 0
            ? "本机磁盘空间"
            : tightTotal > 0
                ? $"本机磁盘（共 {TotalDiskCount} 块 · {tightTotal} 块紧张）"
                : $"本机磁盘（共 {TotalDiskCount} 块）";

        if (HasOverflowDisks)
        {
            CleanupDiskOverviewItem[] hidden = AllDiskOverviews.Skip(MaxInlineDiskOverviewCount).ToArray();
            int hiddenTight = hidden.Count(d => d.UsedPercent >= 85d);
            long hiddenFree = hidden.Sum(d => d.FreeBytes);
            long hiddenQuarantine = hidden.Sum(d => d.QuarantineBytes);

            var parts = new List<string> { $"其余 {hidden.Length} 块盘" };
            if (hiddenTight > 0) parts.Add($"{hiddenTight} 块紧张");
            if (hiddenQuarantine > 0) parts.Add($"隔离区 {FileStatsService.FormatBytes(hiddenQuarantine)}");
            parts.Add($"合计可用 {FileStatsService.FormatBytes(hiddenFree)}");
            OverflowDisksSummaryText = string.Join(" · ", parts);
        }
        else
        {
            OverflowDisksSummaryText = string.Empty;
        }
    }

    [RelayCommand]
    private void OpenAllDisksWindow()
    {
        if (AllDiskOverviews.Count == 0) RefreshDiskOverviews();
        var win = new CleanupDiskOverviewWindow(AllDiskOverviews, OpenQuarantineView);
        if (Application.Current?.MainWindow is { IsVisible: true } owner)
        {
            win.Owner = owner;
            win.Icon = owner.Icon;
        }
        win.ShowDialog();
    }

    private void SetIdleCategorySubtitles()
    {
        IReadOnlyList<CleanupRuleDefinition> allRules = BuiltInCleanupRules.All;
        CategoryHeaderText = $"清理分类（内置 {allRules.Count} 项规则）";

        foreach (CleanupCategoryNavItem cat in CategoryNavItems)
        {
            int count = allRules.Count(r => r.Category == cat.Category);
            cat.Subtitle = cat.Category switch
            {
                CleanupCategory.SystemTempAndShader => $"内置 {count} 项 · 建议清理",
                CleanupCategory.SystemUpdateAndComponent => $"内置 {count} 项 · 官方接口",
                CleanupCategory.SoftwareAndDeveloperCache => $"内置 {count} 款 · 支持跨盘",
                CleanupCategory.CautionItems => $"内置 {count} 项 · 含全盘回收站",
                _ => $"内置 {count} 项 · 待扫描"
            };
        }
    }

    private void RebuildIdleScopePreviews()
    {
        string previewSignature = $"{SelectedCategoryFilter?.ToString() ?? "*"}|{_lastCrossDriveSignature}";
        if (VisibleIdleScopePreviews.Count > 0
            && string.Equals(previewSignature, _lastIdlePreviewSignature, StringComparison.Ordinal))
        {
            return;
        }
        _lastIdlePreviewSignature = previewSignature;

        VisibleIdleScopePreviews.Clear();

        IReadOnlyList<CleanupRuleDefinition> allRules = BuiltInCleanupRules.All;
        IEnumerable<CleanupRuleDefinition> scoped = SelectedCategoryFilter is CleanupCategory filter
            ? allRules.Where(r => r.Category == filter)
            : allRules;

        var crossByRule = CrossDriveDiscoveries
            .GroupBy(c => c.RuleId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(x => x.DriveLabel).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);

        if (SelectedCategoryFilter is CleanupCategory activeCat)
        {
            string? title = CategoryNavItems.FirstOrDefault(c => c.Category == activeCat)?.Title;
            IdleScopeHeaderText = $"分类覆盖预览：{title}（{scoped.Count()} 项规则）";
        }
        else
        {
            IdleScopeHeaderText = $"本次扫描覆盖范围（内置 {allRules.Count} 项规则 · 覆盖系统与 6 大软件生态）";
        }

        // 1. 系统临时与着色器
        var sysTemp = scoped.Where(r => r.Category == CleanupCategory.SystemTempAndShader).ToArray();
        if (sysTemp.Length > 0)
        {
            VisibleIdleScopePreviews.Add(BuildIdlePreviewCard(
                CleanupCategory.SystemTempAndShader,
                "系统临时与显卡着色器缓存",
                sysTemp,
                "建议清理", "#ECFDF5", "#A7F3D0", "#0F7B4B",
                crossByRule));
        }

        // 2. 系统更新与组件回收
        var sysUpd = scoped.Where(r => r.Category == CleanupCategory.SystemUpdateAndComponent).ToArray();
        if (sysUpd.Length > 0)
        {
            VisibleIdleScopePreviews.Add(BuildIdlePreviewCard(
                CleanupCategory.SystemUpdateAndComponent,
                "系统更新与官方组件回收",
                sysUpd,
                "官方接口", "#EBF5FF", "#BFDBFE", "#0078D4",
                crossByRule));
        }

        // 3. 软件与开发者缓存按 6 大业务分组展示
        var swGroups = scoped
            .Where(r => r.Category == CleanupCategory.SoftwareAndDeveloperCache)
            .GroupBy(r => r.GroupId ?? "other", StringComparer.OrdinalIgnoreCase);

        foreach (var grp in swGroups)
        {
            CleanupRuleDefinition[] items = grp.ToArray();
            string gTitle = items[0].GroupDisplayName ?? grp.Key;
            bool isOnDemand = items.All(i => i.Tier == CleanupRecommendationTier.OnDemand);
            VisibleIdleScopePreviews.Add(BuildIdlePreviewCard(
                CleanupCategory.SoftwareAndDeveloperCache,
                gTitle,
                items,
                isOnDemand ? "按需清理" : "建议 / 按需",
                isOnDemand ? "#EBF5FF" : "#ECFDF5",
                isOnDemand ? "#BFDBFE" : "#A7F3D0",
                isOnDemand ? "#0078D4" : "#0F7B4B",
                crossByRule));
        }

        // 4. 谨慎清理项（含全盘回收站）
        var caution = scoped.Where(r => r.Category == CleanupCategory.CautionItems).ToArray();
        if (caution.Length > 0)
        {
            VisibleIdleScopePreviews.Add(BuildIdlePreviewCard(
                CleanupCategory.CautionItems,
                "谨慎清理项（默认折叠 · 含全盘回收站）",
                caution,
                "谨慎清理", "#FFFBEB", "#FDE68A", "#B45309",
                crossByRule));
        }
    }

    private static CleanupIdleScopePreviewItem BuildIdlePreviewCard(
        CleanupCategory category,
        string title,
        CleanupRuleDefinition[] rules,
        string tierText, string tierBg, string tierBorder, string tierFg,
        Dictionary<string, string[]> crossByRule)
    {
        var sampleNames = rules.Take(5).Select(r => ShortRulePreviewName(r.DisplayName)).ToList();
        string summary = string.Join(" · ", sampleNames);
        if (rules.Length > sampleNames.Count)
        {
            summary += $" 等 {rules.Length} 项";
        }

        string[] crossDrives = rules
            .Where(r => crossByRule.ContainsKey(r.RuleId))
            .SelectMany(r => crossByRule[r.RuleId])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string crossHint = crossDrives.Length > 0
            ? $"已探测到 {string.Join("/", crossDrives)} 跨盘缓存"
            : (category == CleanupCategory.CautionItems ? "支持 C/D/E… 全盘回收站" : string.Empty);

        return new CleanupIdleScopePreviewItem
        {
            Category = category,
            Title = title,
            RuleCountText = $"{rules.Length} 项",
            SampleSummary = summary,
            TierBadgeText = tierText,
            TierBadgeBg = tierBg,
            TierBadgeBorder = tierBorder,
            TierBadgeFg = tierFg,
            CrossDriveHint = crossHint
        };
    }

    private static string ShortRulePreviewName(string displayName)
    {
        int idx = displayName.IndexOfAny(['（', '(', '与']);
        if (idx >= 2 && idx <= 16) return displayName[..idx].Trim();
        return displayName.Length > 16 ? displayName[..16] + "…" : displayName;
    }

    public void RefreshMftCacheHint()
    {
        string sysDrive = MigrationCore.Services.Scanning.VolumeSnapshotCache.NormalizeDriveRoot(
            Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\");
        var lookup = MigrationCore.Services.Scanning.VolumeSnapshotCache.Shared.TryGet(sysDrive, runLiveProbe: false, touchAccessTime: false);
        if (lookup is { IsFresh: true } && lookup.Entry.Snapshot.Backend == MigrationCore.Models.Scanning.ScanBackendKind.RawNtfsMft)
        {
            int mins = Math.Max(0, (int)Math.Round(lookup.Age.TotalMinutes));
            MftCacheHintText = $"已检测到新鲜 MFT 缓存（{sysDrive.TrimEnd('\\')} · {mins} 分钟前）：点击「开始扫描」将优先从内存秒级复用，并在执行清理前实时复核 24 小时修改时间与 FileId。";
        }
        else
        {
            MftCacheHintText = "未命中新鲜 MFT 缓存：点击「开始扫描」将使用零句柄高速定向遍历（仅扫内置规则已知目录，不扫普通数据盘），所有项目默认不勾选。";
        }
    }

    [RelayCommand]
    private void CancelScan()
    {
        _scanCts?.Cancel();
    }

    /// <summary>
    /// 供 AI 交互式扫描卡片触发的只读垃圾清理扫描（用户手动点击确认后调用；完成后同步回填垃圾清理页三档列表）。
    /// </summary>
    public async Task<CleanupScanSnapshot?> ScanForAiCardAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (IsExecuting)
        {
            throw new InvalidOperationException("当前正在执行清理任务，请等待完成后再扫描。");
        }

        var detailedProgress = new Progress<CleanupScanProgress>(p =>
        {
            string label = p.TotalSteps > 0
                ? $"清理扫描 ({p.CompletedSteps}/{p.TotalSteps})：{p.Message}"
                : p.Message;
            progress?.Report(label);
        });

        CleanupScanSnapshot snapshot = await _cleanupService.ScanAsync(detailedProgress: detailedProgress, ct: ct).ConfigureAwait(false);
        DispatchToUi(() =>
        {
            ApplyScanSnapshot(snapshot);
            StatusMessage = $"只读扫描完成：共识别 {FileStatsService.FormatBytes(snapshot.TotalReclaimableBytes)}（所有项目默认未勾选）。";
            _overviewDirty = true;
            _ = OnEnteredCleanupPageAsync(force: true);
        });
        return snapshot;
    }

    [RelayCommand]
    public async Task ScanAsync()
    {
        if (IsScanning || IsExecuting) return;

        IsScanning = true;
        HasExecutionReceipt = false;
        SelectedRule = null;
        ResetLiveScanFeed();
        ScanProgressPercent = 0;
        ScanProgressBadgeText = "0%";
        StatusMessage = "正在只读扫描可清理项（不修改任何文件）…";

        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();

        try
        {
            var detailedProgress = new Progress<CleanupScanProgress>(p => DispatchToUi(() =>
            {
                ScanProgressPercent = p.Percent;
                ScanProgressBadgeText = p.TotalSteps > 0
                    ? $"{p.CompletedSteps} / {p.TotalSteps} · {Math.Round(p.Percent):0}%"
                    : $"{Math.Round(p.Percent):0}%";
                StatusMessage = p.Message;

                if (p.CompletedRuleHit is not null)
                {
                    ApplyLiveScanHit(p.CompletedRuleHit);
                }
            }));
            CleanupScanSnapshot snapshot = await _cleanupService.ScanAsync(detailedProgress: detailedProgress, ct: _scanCts.Token);

            DispatchToUi(() =>
            {
                ScanProgressPercent = 100;
                ScanProgressBadgeText = "100%";
                ApplyScanSnapshot(snapshot);
                _aiAssistant?.SyncCleanupScanSnapshot(snapshot);
                StatusMessage = $"只读扫描完成：共识别 {FileStatsService.FormatBytes(snapshot.TotalReclaimableBytes)}（所有项目默认未勾选）。";
                _overviewDirty = true;
                _ = OnEnteredCleanupPageAsync(force: true);
            });
        }
        catch (OperationCanceledException)
        {
            DispatchToUi(() =>
            {
                StatusMessage = "已取消垃圾扫描，可随时点击右上角「重新扫描」。";
                FooterSelectionSummary = StatusMessage;
                if (!HasScanSnapshot)
                {
                    SetIdleCategorySubtitles();
                    RebuildIdleScopePreviews();
                }
            });
        }
        catch (Exception ex)
        {
            DispatchToUi(() =>
            {
                StatusMessage = $"垃圾扫描失败：{ex.Message}";
                FooterSelectionSummary = StatusMessage;
            });
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void ResetLiveScanFeed()
    {
        _allLiveHits.Clear();
        _liveCategoryBytes.Clear();
        _liveDiscoveredDrives.Clear();
        LiveScanHits.Clear();
        LiveDiscoveredBytes = 0;
        LiveHitRuleCount = 0;
        LiveSkippedCount = 0;
        LiveHitDrivesText = string.Empty;

        CategoryHeaderText = "清理分类（正在扫描…）";
        foreach (CleanupCategoryNavItem cat in CategoryNavItems)
        {
            cat.Subtitle = "扫描中…";
        }
    }

    /// <summary>
    /// 扫描进行中逐条接收真实命中结果：累加总体积、刷新左栏分类实时发现、并维护按体积降序且高亮最新项的实时榜单。
    /// </summary>
    public void ApplyLiveScanHit(CleanupLiveRuleHit hit)
    {
        LiveSkippedCount += hit.SkippedRecentOrLockedCount;

        if (hit.TotalBytes <= 0 && !hit.IsAvailable)
        {
            return;
        }

        LiveDiscoveredBytes += hit.TotalBytes;
        LiveHitRuleCount++;

        foreach (string d in hit.Drives)
        {
            _liveDiscoveredDrives.Add(d);
        }

        LiveHitDrivesText = _liveDiscoveredDrives.Count > 0
            ? string.Join(" · ", _liveDiscoveredDrives.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            : string.Empty;

        _liveCategoryBytes[hit.Category] = _liveCategoryBytes.GetValueOrDefault(hit.Category) + hit.TotalBytes;
        CleanupCategoryNavItem? nav = CategoryNavItems.FirstOrDefault(c => c.Category == hit.Category);
        if (nav is not null)
        {
            long catBytes = _liveCategoryBytes[hit.Category];
            nav.Subtitle = catBytes > 0
                ? $"已发现 {FileStatsService.FormatBytes(catBytes)}"
                : "已发现官方可清理项";
        }
        CategoryHeaderText = $"清理分类（已发现 {FileStatsService.FormatBytes(LiveDiscoveredBytes)}）";

        foreach (CleanupLiveHitRowViewModel prev in _allLiveHits)
        {
            prev.IsLatestHighlight = false;
        }

        string sysDrive = (Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\")[..2];
        var row = new CleanupLiveHitRowViewModel
        {
            RuleId = hit.RuleId,
            DisplayName = hit.DisplayName,
            Category = hit.Category,
            Tier = hit.Tier,
            TotalBytes = hit.TotalBytes,
            MatchedCount = hit.MatchedCount,
            HasBlockingProcesses = hit.HasBlockingProcesses,
            Drives = hit.Drives,
            HasNonSystemDrive = hit.Drives.Any(d => !d.Equals(sysDrive, StringComparison.OrdinalIgnoreCase)),
            IsLatestHighlight = true
        };
        _allLiveHits.Add(row);

        // 按体积降序呈现前 24 个大项（避免列表过长），保证大头始终置顶、最新项带高亮标签
        CleanupLiveHitRowViewModel[] topRows = _allLiveHits
            .OrderByDescending(r => r.TotalBytes)
            .ThenByDescending(r => r.IsLatestHighlight)
            .Take(24)
            .ToArray();

        LiveScanHits.Clear();
        foreach (CleanupLiveHitRowViewModel r in topRows)
        {
            LiveScanHits.Add(r);
        }
    }

    public void ApplyScanSnapshot(CleanupScanSnapshot snapshot)
    {
        CurrentSnapshot = snapshot;
        SelectedDriveFilter = null;
        AllRuleItems.Clear();

        foreach (CleanupRuleScanResult ruleScan in snapshot.Rules)
        {
            AllRuleItems.Add(new CleanupRuleItemViewModel(ruleScan, RecalculateSelectionSummary));
        }

        CategoryHeaderText = $"清理分类（共 {FileStatsService.FormatBytes(snapshot.TotalReclaimableBytes)}）";
        UpdateCategorySubtitles(snapshot);
        RebuildDriveFilterChips();
        RebuildVisibleRules();
        RecalculateSelectionSummary();
    }

    private void RebuildDriveFilterChips()
    {
        DriveFilterChips.Clear();

        var driveTotals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (CleanupRuleItemViewModel item in AllRuleItems.Where(r => r.HasReclaimableContent))
        {
            if (item.AffectedDrives.Length == 1)
            {
                string d = item.AffectedDrives[0];
                driveTotals[d] = driveTotals.GetValueOrDefault(d) + item.TotalBytes;
            }
            else if (item.AffectedDrives.Length > 1)
            {
                // 跨多盘规则按明细或均摊归属显示盘符存在（保证每个涉及盘符都出现在筛选胶囊里）
                foreach (string d in item.AffectedDrives)
                {
                    long perDrive = item.ScanResult.PreviewItems
                        .Where(p => p.FullPath.StartsWith(d, StringComparison.OrdinalIgnoreCase))
                        .Sum(p => p.SizeBytes);
                    driveTotals[d] = driveTotals.GetValueOrDefault(d) + perDrive;
                }
            }
        }

        HasMultipleDrivesInResults = driveTotals.Count >= 2;
        if (!HasMultipleDrivesInResults)
        {
            SelectedDriveFilter = null;
            return;
        }

        long grandTotal = AllRuleItems.Where(r => r.HasReclaimableContent).Sum(r => r.TotalBytes);
        DriveFilterChips.Add(new CleanupDriveFilterChip
        {
            DriveLabel = null,
            DisplayText = $"全部磁盘 ({FileStatsService.FormatBytes(grandTotal)})",
            IsSelected = SelectedDriveFilter is null
        });

        foreach (var kv in driveTotals.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            DriveFilterChips.Add(new CleanupDriveFilterChip
            {
                DriveLabel = kv.Key,
                DisplayText = kv.Value > 0 ? $"{kv.Key} · {FileStatsService.FormatBytes(kv.Value)}" : kv.Key,
                IsSelected = string.Equals(SelectedDriveFilter, kv.Key, StringComparison.OrdinalIgnoreCase)
            });
        }
    }

    [RelayCommand]
    private void SelectDriveFilter(CleanupDriveFilterChip? chip)
    {
        string? target = chip?.DriveLabel;
        if (target is not null && string.Equals(SelectedDriveFilter, target, StringComparison.OrdinalIgnoreCase))
        {
            SelectedDriveFilter = null;
        }
        else
        {
            SelectedDriveFilter = target;
        }

        foreach (CleanupDriveFilterChip c in DriveFilterChips)
        {
            c.IsSelected = string.Equals(c.DriveLabel, SelectedDriveFilter, StringComparison.OrdinalIgnoreCase);
        }

        RebuildVisibleRules();
    }
}
