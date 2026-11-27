using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MigrationCore.Models;
using MigrationCore.Models.Safety;
using MigrationCore.Models.Scanning;
using MigrationCore.Services;
using MigrationCore.Services.Safety;
using MigrationCore.Services.Scanning;
using MoveWithSymlinkWPF.Helpers;
using MoveWithSymlinkWPF.Views;

namespace MoveWithSymlinkWPF.ViewModels;

public partial class DiskScanViewModel
{
    [RelayCommand]
    private void LoadVolumes()
    {
        string? prevRoot = SelectedVolumeCard?.DriveRoot;
        VolumeCards.Clear();
        foreach (ScanVolumeInfo v in _scanService.ListVolumes())
        {
            VolumeCards.Add(new VolumeCardItem(v));
        }

        SelectedVolumeCard = VolumeCards.FirstOrDefault(v => v.DriveRoot.Equals(prevRoot, StringComparison.OrdinalIgnoreCase))
            ?? VolumeCards.FirstOrDefault();

        // 若默认目标根目录盘符不存在，自动推荐一个非系统盘
        var preferredTarget = VolumeCards.FirstOrDefault(v => v.IsRecommendedTarget)
            ?? VolumeCards.FirstOrDefault(v => !v.DriveLetter.Equals("C:", StringComparison.OrdinalIgnoreCase));
        if (preferredTarget is not null && !Directory.Exists(Path.GetPathRoot(UnifiedTargetRoot) ?? string.Empty))
        {
            UnifiedTargetRoot = Path.Combine(preferredTarget.DriveRoot, "MigratedData");
        }
        UpdateTargetSpaceHint();
        RefreshVolumeCardCacheStates();
    }

    /// <summary>
    /// 供概览首页跳转到磁盘扫描页时预设选中盘符（仅切换选中卡片，不自动启动扫描）。
    /// </summary>
    public void SelectVolumeByRoot(string? driveRoot)
    {
        if (IsScanning || string.IsNullOrWhiteSpace(driveRoot))
        {
            return;
        }

        string normalized = VolumeSnapshotCache.NormalizeDriveRoot(driveRoot);
        VolumeCardItem? matched = VolumeCards.FirstOrDefault(v =>
            v.DriveRoot.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (matched is not null)
        {
            SelectedVolumeCard = matched;
            if (!HasSnapshot || ActiveSubView != DiskScanSubView.DirectoryTree)
            {
                ActiveSubView = DiskScanSubView.VolumeSelectAndScan;
            }
        }
    }

    /// <summary>
    /// 磁盘卡片主按钮：命中有效会话缓存（新鲜或陈旧但未硬过期）时直接秒开目录树，否则发起新扫描。
    /// </summary>
    [RelayCommand]
    private async Task OpenOrScanVolumeAsync(VolumeCardItem? card)
    {
        VolumeCardItem? target = card ?? SelectedVolumeCard;
        if (IsScanning || target is null) return;

        SelectedVolumeCard = target;
        VolumeSnapshotLookup? cached = _snapshotCache.TryGet(target.DriveRoot, runLiveProbe: true);
        if (cached is not null)
        {
            ApplySnapshotCore(cached.Entry.Snapshot, cached.Entry.VolumeTitle, cached.Entry.ScanElapsedSeconds, cached);
            ActiveSubView = DiskScanSubView.DirectoryTree;
            return;
        }

        await StartScanForVolumeAsync(target);
    }

    public void RefreshVolumeCardCacheStates()
    {
        foreach (VolumeCardItem card in VolumeCards)
        {
            VolumeSnapshotLookup? lookup = _snapshotCache.TryGet(card.DriveRoot, runLiveProbe: true, touchAccessTime: false);
            if (lookup is null)
            {
                card.HasCachedSnapshot = false;
                card.IsCacheStale = false;
                card.CacheStatusText = string.Empty;
                card.CacheStatusToolTip = string.Empty;
                continue;
            }

            card.HasCachedSnapshot = true;
            card.IsCacheStale = lookup.IsStale;
            string ageLabel = FormatCacheAge(lookup.Age);
            card.CacheStatusText = lookup.IsStale
                ? $"缓存已陈旧 · {ageLabel}"
                : $"已缓存 · {ageLabel}";
            card.CacheStatusToolTip = lookup.IsStale
                ? (lookup.StaleReason ?? "快照已超过新鲜期，点击「查看结果」可秒开浏览，迁移前仍会自动定向复核")
                : $"本会话临时缓存（{ageLabel}），点击「查看结果」零等待进入目录树；关闭软件后自动失效";
        }

        SyncRecentScansFromCache();
    }

    private void SyncRecentScansFromCache()
    {
        // 移除内存中已被硬过期/LRU 驱逐的条目（保留 #if DEBUG 开发磁盘缓存项）
        var activeIds = _snapshotCache.ListActive(runLiveProbe: false)
            .Select(x => x.Entry.Snapshot.ScanId)
            .ToHashSet();

        for (int i = RecentScans.Count - 1; i >= 0; i--)
        {
            RecentScanItem item = RecentScans[i];
            if (item.Snapshot is not null && string.IsNullOrEmpty(item.DevCacheFilePath) && !activeIds.Contains(item.ScanId))
            {
                RecentScans.RemoveAt(i);
            }
        }
    }

    private void SyncCurrentSnapshotFreshness()
    {
        if (_snapshot is null)
        {
            IsSnapshotStale = false;
            SnapshotStaleMessage = string.Empty;
            return;
        }

        VolumeSnapshotLookup? lookup = _snapshotCache.TryGetByScanId(_snapshot.ScanId, runLiveProbe: true);
        if (lookup is not null && lookup.IsStale)
        {
            IsSnapshotStale = true;
            SnapshotStaleMessage = lookup.StaleReason ?? "当前扫描快照已陈旧，建议点击「重新扫描」获取最新空间状态。";
        }
        else
        {
            IsSnapshotStale = false;
            SnapshotStaleMessage = string.Empty;
        }
    }

    private static string FormatCacheAge(TimeSpan age)
    {
        if (age.TotalSeconds < 60) return "刚刚";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes} 分钟前";
        return $"{(int)age.TotalHours} 小时前";
    }

    /// <summary>
    /// 供 AI 交互式扫描卡片在用户手动勾选确认后执行的单盘只读扫描：
    /// 1. 与磁盘扫描页共享单一扫描任务、磁盘卡片扫描态、阶段 00/01 实时进度与双向取消；
    /// 2. 同盘已在扫描时直接挂载复用进行中的任务，杜绝重复扫盘；
    /// 3. 保留克制体验：扫描完成写入共享缓存与最近记录，若当前已在浏览目录树绝不强制跳页打断对话。
    /// </summary>
    public async Task<ScanSnapshot?> ScanDriveForAiCardAsync(
        string driveRoot,
        IProgress<(double Percent, string Message)>? progress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string normRoot = VolumeSnapshotCache.NormalizeDriveRoot(driveRoot);

        while (true)
        {
            Task<ScanSnapshot> scanTask;
            CancellationTokenSource activeCts;
            TaskCompletionSource<ScanSnapshot>? tcsToRun = null;
            Task<ScanSnapshot>? waitOtherDriveTask = null;
            Action<(double Percent, string Message)>? listener = progress is null ? null : p => progress.Report(p);
            (double Percent, string Message)? initialBroadcast = null;

            lock (_diskScanSync)
            {
                if (_activeDiskScanTask is not null && _scanCts is not null && !_activeDiskScanTask.IsCompleted)
                {
                    if (string.Equals(_activeDiskScanDriveRoot, normRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        if (listener is not null)
                        {
                            _externalDiskScanProgressListeners.Add(listener);
                        }
                        scanTask = _activeDiskScanTask;
                        activeCts = _scanCts;
                        initialBroadcast = _lastDiskScanProgressBroadcast;
                    }
                    else
                    {
                        waitOtherDriveTask = _activeDiskScanTask;
                        scanTask = null!;
                        activeCts = null!;
                    }
                }
                else
                {
                    if (listener is not null)
                    {
                        _externalDiskScanProgressListeners.Add(listener);
                    }
                    activeCts = new CancellationTokenSource();
                    _scanCts = activeCts;
                    _activeDiskScanDriveRoot = normRoot;
                    _lastDiskScanProgressBroadcast = (6, "准备读取卷信息…");
                    tcsToRun = new TaskCompletionSource<ScanSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
                    scanTask = tcsToRun.Task;
                    _activeDiskScanTask = scanTask;
                }
            }

            if (waitOtherDriveTask is not null)
            {
                try
                {
                    await waitOtherDriveTask.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // 前一块盘的取消或异常不影响继续扫描当前目标盘
                }
                continue;
            }

            if (tcsToRun is not null)
            {
                VolumeCardItem? matchedCard = FindVolumeCardByRoot(normRoot);
                _ = RunUnifiedVolumeScanCoreAsync(normRoot, matchedCard, switchToTreeOnComplete: false, activeCts, tcsToRun);
            }
            else if (initialBroadcast is { } initProg)
            {
                progress?.Report(initProg);
            }

            using CancellationTokenRegistration cancelReg = ct.Register(() =>
            {
                try { activeCts.Cancel(); } catch (ObjectDisposedException) { }
            });

            try
            {
                return await scanTask.WaitAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                if (listener is not null)
                {
                    lock (_diskScanSync)
                    {
                        _externalDiskScanProgressListeners.Remove(listener);
                    }
                }
            }
        }
    }

    [RelayCommand]
    private async Task StartScanForVolumeAsync(VolumeCardItem? card)
    {
        VolumeCardItem? target = card ?? SelectedVolumeCard;
        if (target is null) return;

        string normRoot = VolumeSnapshotCache.NormalizeDriveRoot(target.DriveRoot);
        CancellationTokenSource cts;
        TaskCompletionSource<ScanSnapshot> tcs;

        lock (_diskScanSync)
        {
            if (_activeDiskScanTask is not null && !_activeDiskScanTask.IsCompleted)
            {
                return;
            }

            cts = new CancellationTokenSource();
            _scanCts = cts;
            _activeDiskScanDriveRoot = normRoot;
            _lastDiskScanProgressBroadcast = (6, "准备读取卷信息…");
            tcs = new TaskCompletionSource<ScanSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeDiskScanTask = tcs.Task;
        }

        _ = RunUnifiedVolumeScanCoreAsync(normRoot, target, switchToTreeOnComplete: true, cts, tcs);

        try
        {
            await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            // UI 取消态已由 RunUnifiedVolumeScanCoreAsync 统一更新
        }
        catch
        {
            // UI 错误态与提示框已由 RunUnifiedVolumeScanCoreAsync 统一处理
        }
    }

    private VolumeCardItem? FindVolumeCardByRoot(string normRoot)
    {
        VolumeCardItem? found = null;
        DispatchToUi(() =>
        {
            found = VolumeCards.FirstOrDefault(v =>
                VolumeSnapshotCache.NormalizeDriveRoot(v.DriveRoot).Equals(normRoot, StringComparison.OrdinalIgnoreCase));
        });
        return found;
    }

    private async Task RunUnifiedVolumeScanCoreAsync(
        string normRoot,
        VolumeCardItem? target,
        bool switchToTreeOnComplete,
        CancellationTokenSource scanCts,
        TaskCompletionSource<ScanSnapshot> tcs)
    {
        string driveLetter = normRoot.TrimEnd('\\');
        bool supportsRawMft = target?.Info.SupportsRawMft ?? true;

        DispatchToUi(() =>
        {
            if (switchToTreeOnComplete)
            {
                if (target is not null) SelectedVolumeCard = target;
                ActiveSubView = DiskScanSubView.VolumeSelectAndScan;
            }

            IsScanning = true;
            if (target is not null) target.IsCurrentlyScanning = true;
            ScanProgressPercent = 6;
            ScanStageBadgeText = $"正在扫描 · {driveLetter}";
            ScanStageHeading = supportsRawMft
                ? "阶段 1 / 3：读取原始 NTFS MFT 主表"
                : "阶段 1 / 3：普通目录遍历（降级只读模式）";
            ScanProcessedRecordsText = "0 项";
            ScanLargeDirCountText = "统计中…";
            ScanL1SummaryText = "统计中…";
            ScanStep1State = 1; ScanStep1Status = "进行中…";
            ScanStep2State = 0; ScanStep2Status = "等待中";
            ScanStep3State = 0; ScanStep3Status = "等待中";

            _scanStopwatch.Restart();
            _scanElapsedTimer.Start();
            AiAssistant?.NotifyExternalDiskScanState(normRoot, isScanning: true, percent: 6, message: "读取卷信息中…");
        });

        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                if (scanCts.IsCancellationRequested)
                {
                    return;
                }

                double aiPct = p.Phase switch
                {
                    ScanPhase.ProbingVolume or ScanPhase.ReadingMft => p.Percent is double p1 ? Math.Clamp(p1 * 0.55, 5, 55) : 30,
                    ScanPhase.BuildingTree or ScanPhase.AggregatingSizes => 72,
                    ScanPhase.IdentifyingApplications => 90,
                    _ => p.Percent ?? 50
                };
                string aiMsg = p.Phase switch
                {
                    ScanPhase.ProbingVolume or ScanPhase.ReadingMft => p.Backend == ScanBackendKind.RawNtfsMft ? "读取 NTFS MFT 主表…" : "只读目录遍历中…",
                    ScanPhase.BuildingTree or ScanPhase.AggregatingSizes => "构建目录树与聚合体积…",
                    ScanPhase.IdentifyingApplications => "匹配软件归属与安全等级…",
                    _ => p.Message ?? "扫描中…"
                };

                Action<(double Percent, string Message)>[] listeners;
                lock (_diskScanSync)
                {
                    _lastDiskScanProgressBroadcast = (aiPct, aiMsg);
                    listeners = _externalDiskScanProgressListeners.Count > 0
                        ? _externalDiskScanProgressListeners.ToArray()
                        : [];
                }

                foreach (Action<(double Percent, string Message)> cb in listeners)
                {
                    cb((aiPct, aiMsg));
                }

                DispatchToUi(() =>
                {
                    if (scanCts.IsCancellationRequested || !IsScanning)
                    {
                        return;
                    }

                    if (p.ProcessedRecords > 0)
                    {
                        ScanProcessedRecordsText = $"{p.ProcessedRecords:N0} 项";
                    }

                    switch (p.Phase)
                    {
                        case ScanPhase.ProbingVolume:
                        case ScanPhase.ReadingMft:
                            ScanStageHeading = p.Backend == ScanBackendKind.RawNtfsMft
                                ? "阶段 1 / 3：读取原始 MFT 主表"
                                : "阶段 1 / 3：只读目录遍历（降级）";
                            ScanStep1State = 1;
                            ScanStep1Status = p.Percent is double pct1 ? $"进行中 · {pct1:F0}%" : "读取中…";
                            ScanProgressPercent = p.Percent is double p1 ? Math.Clamp(p1 * 0.55, 5, 55) : Math.Min(50, ScanProgressPercent + 1);
                            break;

                        case ScanPhase.BuildingTree:
                        case ScanPhase.AggregatingSizes:
                            ScanStageHeading = "阶段 2 / 3：构建目录邻接树与汇总空间";
                            ScanStep1State = 2;
                            if (ScanStep1Status.StartsWith("进行中") || ScanStep1Status == "读取中…")
                                ScanStep1Status = $"已完成 · {_scanStopwatch.Elapsed.TotalSeconds:F1} s";
                            ScanStep2State = 1;
                            ScanStep2Status = "聚合计算中…";
                            ScanProgressPercent = 72;
                            break;

                        case ScanPhase.IdentifyingApplications:
                            ScanStageHeading = "阶段 3 / 3：匹配软件归属与 4 级准入";
                            ScanStep1State = 2;
                            ScanStep2State = 2; ScanStep2Status = "已完成";
                            ScanStep3State = 1; ScanStep3Status = "匹配规则中…";
                            ScanProgressPercent = 90;
                            break;
                    }

                    AiAssistant?.NotifyExternalDiskScanState(normRoot, isScanning: true, percent: aiPct, message: aiMsg);
                });
            });

            ScanSnapshot snapshot = await _scanService.ScanVolumeAsync(
                normRoot,
                allowFallbackToDirectoryWalk: true,
                progress,
                scanCts.Token).ConfigureAwait(false);

            // 扫描器在扫描中途收到取消信号时会返回 Coverage=Cancelled 的残缺快照；
            // 此处统一按取消流程收口，禁止将其作为完整结果切入目录树或写入缓存。
            if (snapshot.Coverage == ScanCoverageStatus.Cancelled || scanCts.IsCancellationRequested)
            {
                throw new OperationCanceledException(scanCts.Token);
            }

            double elapsedSec = Math.Max(0.1, (snapshot.CompletedAtUtc - snapshot.StartedAtUtc).TotalSeconds);

            // 后台汇总 >1GB 大目录与 L1 软件统计
            var (largeCount, l1Count, l1Bytes) = await Task.Run(() => SummarizeSnapshotHighlights(snapshot), scanCts.Token).ConfigureAwait(false);
            string displayTitle = target?.DisplayTitle
                ?? (string.IsNullOrWhiteSpace(snapshot.Volume.VolumeLabel)
                    ? $"本地磁盘 ({driveLetter})"
                    : $"{snapshot.Volume.VolumeLabel} ({driveLetter})");

            // 写入进程级共享快照缓存（供磁盘扫描卡片秒开、AI 助手与后续垃圾清理共享）
            _snapshotCache.Put(snapshot, displayTitle, elapsedSec, largeCount, l1Count, l1Bytes);

            DispatchToUi(() =>
            {
                _scanStopwatch.Stop();
                _scanElapsedTimer.Stop();

                ScanProgressPercent = 100;
                ScanStep1State = 2; ScanStep1Status = "已完成";
                ScanStep2State = 2; ScanStep2Status = "已完成";
                ScanStep3State = 2; ScanStep3Status = $"已完成 · {elapsedSec:F1} s";
                ScanProcessedRecordsText = $"{snapshot.Nodes.Length:N0} 项";
                ScanLargeDirCountText = $"{largeCount:N0} 个";
                ScanL1SummaryText = l1Count > 0 ? $"{l1Count} 项 · {FileStatsService.FormatBytes(l1Bytes)}" : "0 项";
                ScanElapsedText = $"耗时 {elapsedSec:F1} s";
                ScanStageBadgeText = $"扫描完成 · {driveLetter}";
                ScanStageHeading = $"扫描完成，共解析 {snapshot.Nodes.Length:N0} 个对象";

                if (switchToTreeOnComplete || _snapshot is null)
                {
                    ApplySnapshot(snapshot, displayTitle, elapsedSec);
                }
                else
                {
                    AiAssistant?.SyncScanSnapshot(snapshot);
                }

                // 记录到最近扫描历史
                var existingIdx = RecentScans.ToList().FindIndex(r => r.VolumeTitle == displayTitle);
                if (existingIdx >= 0) RecentScans.RemoveAt(existingIdx);
                RecentScans.Insert(0, new RecentScanItem(
                    snapshot.ScanId,
                    displayTitle,
                    DateTime.Now.ToString("HH:mm"),
                    $"已用 {FileStatsService.FormatBytes(snapshot.Volume.UsedBytes)} · 发现 {largeCount} 个 >1 GB 大目录",
                    snapshot));
                if (RecentScans.Count > 5) RecentScans.RemoveAt(RecentScans.Count - 1);
                RefreshVolumeCardCacheStates();

                AiAssistant?.NotifyExternalDiskScanState(normRoot, isScanning: false, percent: 100, message: string.Empty);

                if (switchToTreeOnComplete)
                {
                    // 扫描完成后自动进入阶段 01 目录树（严格对齐 docs/untitled.pen 00 脚栏说明）
                    ActiveSubView = DiskScanSubView.DirectoryTree;
                }
            });

#if DEBUG
            if (!MigrationRuntime.IsTestLab && snapshot.CanUseForMigrationPlanning)
            {
                string volTitleForCache = displayTitle;
                _ = Task.Run(() =>
                {
                    try
                    {
                        ScanSnapshotDevCache.Save(snapshot, volTitleForCache, largeCount, l1Count, l1Bytes);
                        Application.Current?.Dispatcher.BeginInvoke(() => HasDevCache = true);
                    }
                    catch { }
                });
            }
#endif

            tcs.TrySetResult(snapshot);
        }
        catch (OperationCanceledException)
        {
            DispatchToUi(() =>
            {
                _scanStopwatch.Stop();
                _scanElapsedTimer.Stop();
                ScanStageBadgeText = "已取消";
                ScanStageHeading = "扫描已安全取消，未修改任何卷数据";
                if (ScanStep1State != 2) { ScanStep1State = 0; ScanStep1Status = "已取消"; }
                if (ScanStep2State != 2) { ScanStep2State = 0; ScanStep2Status = "已取消"; }
                if (ScanStep3State != 2) { ScanStep3State = 0; ScanStep3Status = "已取消"; }
                AiAssistant?.NotifyExternalDiskScanState(normRoot, isScanning: false, percent: 0, message: string.Empty);
            });
            tcs.TrySetCanceled(scanCts.Token);
        }
        catch (Exception ex)
        {
            DispatchToUi(() =>
            {
                _scanStopwatch.Stop();
                _scanElapsedTimer.Stop();
                ScanStageBadgeText = "扫描失败";
                ScanStageHeading = ex.Message;
                AiAssistant?.NotifyExternalDiskScanState(normRoot, isScanning: false, percent: 0, message: string.Empty);
                if (switchToTreeOnComplete)
                {
                    CustomMessageBox.ShowError($"磁盘扫描失败：\n{ex.Message}", "扫描错误");
                }
            });
            tcs.TrySetException(ex);
        }
        finally
        {
            lock (_diskScanSync)
            {
                if (ReferenceEquals(_scanCts, scanCts))
                {
                    _scanCts = null;
                    _activeDiskScanTask = null;
                    _activeDiskScanDriveRoot = null;
                    _lastDiskScanProgressBroadcast = null;
                }
            }

            DispatchToUi(() =>
            {
                IsScanning = false;
                if (target is not null) target.IsCurrentlyScanning = false;
            });
        }
    }

    [RelayCommand]
    private Task StartScanAsync() => StartScanForVolumeAsync(SelectedVolumeCard);

    [RelayCommand]
    private void CancelScan()
    {
        CancellationTokenSource? cts;
        lock (_diskScanSync)
        {
            cts = _scanCts;
        }
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        DispatchToUi(() => ScanStageHeading = "正在安全取消扫描…");
    }

    private static void DispatchToUi(Action action)
    {
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            if (dispatcher.CheckAccess())
            {
                action();
                return;
            }

            if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
            {
                try
                {
                    dispatcher.Invoke(action);
                }
                catch (TaskCanceledException)
                {
                }
                return;
            }
        }

        action();
    }

    [RelayCommand]
    private async Task OpenRecentScanAsync(RecentScanItem? recent)
    {
        if (recent is null || IsScanning) return;

        if (recent.Snapshot is not null)
        {
            double sec = Math.Max(0.1, (recent.Snapshot.CompletedAtUtc - recent.Snapshot.StartedAtUtc).TotalSeconds);
            VolumeSnapshotLookup? lookup = _snapshotCache.TryGetByScanId(recent.Snapshot.ScanId, runLiveProbe: true);
            ApplySnapshotCore(recent.Snapshot, recent.VolumeTitle, sec, lookup);
            ActiveSubView = DiskScanSubView.DirectoryTree;
            return;
        }

        if (!string.IsNullOrEmpty(recent.DevCacheFilePath))
        {
            IsScanning = true;
            ScanProgressPercent = 45;
            ScanStageBadgeText = "开发缓存加载";
            ScanStageHeading = $"正在从本地开发缓存秒级恢复 {recent.VolumeTitle} 快照…";
            var sw = Stopwatch.StartNew();
            try
            {
                var (loaded, header) = await Task.Run(() =>
                {
                    ScanSnapshot snap = ScanSnapshotDevCache.Load(recent.DevCacheFilePath, out DevScanCacheHeader h);
                    return (snap, h);
                });
                sw.Stop();

                // 用内存中的已加载快照替换条目，本会话再次切回零等待
                int idx = RecentScans.IndexOf(recent);
                if (idx >= 0)
                {
                    RecentScans[idx] = recent with { Snapshot = loaded };
                }

                ScanProgressPercent = 100;
                ScanElapsedText = $"缓存加载 {sw.Elapsed.TotalSeconds:F1} s";
                ScanProcessedRecordsText = $"{loaded.Nodes.Length:N0} 项";
                ScanLargeDirCountText = $"{header.LargeDirCount:N0} 个";
                ScanL1SummaryText = header.L1Count > 0 ? $"{header.L1Count} 项 · {FileStatsService.FormatBytes(header.L1Bytes)}" : "0 项";
                ScanStageBadgeText = "已加载开发缓存";
                ScanStageHeading = $"已从开发缓存恢复 {loaded.Nodes.Length:N0} 个对象（执行迁移前仍实时复核）";
                ScanStep1State = 2; ScanStep1Status = "缓存命中";
                ScanStep2State = 2; ScanStep2Status = "缓存命中";
                ScanStep3State = 2; ScanStep3Status = $"耗时 {sw.Elapsed.TotalSeconds:F1} s";

                ApplySnapshot(loaded, recent.VolumeTitle, sw.Elapsed.TotalSeconds);
                SnapshotModeBadge = $"开发缓存 · {sw.Elapsed.TotalSeconds:F1} s";
                UsnStatusNote = "当前展示为开发模式临时缓存快照；点击「重新扫描」可实时重读 MFT，执行迁移前仍强制逐项实时核验。";
                ActiveSubView = DiskScanSubView.DirectoryTree;
            }
            catch (Exception ex)
            {
                ScanStageBadgeText = "缓存失效";
                ScanStageHeading = $"开发缓存读取失败（已忽略，可重新扫描）：{ex.Message}";
            }
            finally
            {
                IsScanning = false;
            }
        }
    }

    [RelayCommand]
    private void ClearDevCache()
    {
        ScanSnapshotDevCache.ClearAll();
        HasDevCache = false;
        for (int i = RecentScans.Count - 1; i >= 0; i--)
        {
            if (RecentScans[i].IsDevCached)
            {
                RecentScans.RemoveAt(i);
            }
        }
    }

#if DEBUG
    private void LoadDevCachedRecents()
    {
        if (MigrationRuntime.IsTestLab) return;
        try
        {
            var headers = ScanSnapshotDevCache.ListValidHeaders(MigrationSafetyPolicy.CurrentPolicyVersion, TimeSpan.FromHours(24));
            foreach (DevScanCacheHeader h in headers)
            {
                RecentScans.Add(new RecentScanItem(
                    h.ScanId,
                    h.VolumeTitle,
                    $"开发缓存 · {h.CompletedAtUtc.ToLocalTime():HH:mm}",
                    $"{h.NodeCount:N0} 项 · 发现 {h.LargeDirCount} 个 >1 GB 大目录（点击秒开）",
                    Snapshot: null,
                    DevCacheFilePath: h.FilePath));
            }
            HasDevCache = headers.Count > 0;
        }
        catch { }
    }
#endif

    [RelayCommand]
    private void GoToVolumeSelect()
    {
        if (IsBatchExecuting) return;
        ActiveSubView = DiskScanSubView.VolumeSelectAndScan;
    }

    [RelayCommand]
    private void GoToDirectoryTree()
    {
        if (!HasSnapshot || IsBatchExecuting) return;
        ActiveSubView = DiskScanSubView.DirectoryTree;
    }

    [RelayCommand]
    private async Task GoToMigrationQueueAsync()
    {
        if (IsBatchExecuting) return;
        ActiveSubView = DiskScanSubView.MigrationQueue;
        if (PlanItems.Count > 0)
        {
            await FreezeAndPreflightAsync();
        }
    }

    [RelayCommand]
    private void OpenBackupCenter()
    {
        if (Application.Current?.MainWindow?.DataContext is MainViewModel main)
        {
            main.ShowBackupCenterCommand.Execute(null);
        }
    }

    [RelayCommand]
    private void SwitchToTreeView() => BrowseMode = ScanBrowseMode.DirectoryTree;

    [RelayCommand]
    private void SwitchToHotspotView() => BrowseMode = _lastHotspotMode;

    [RelayCommand]
    private void SwitchToHotspotFlat()
    {
        _lastHotspotMode = ScanBrowseMode.HotspotFlatList;
        BrowseMode = ScanBrowseMode.HotspotFlatList;
    }

    [RelayCommand]
    private void SwitchToHotspotPathTree()
    {
        if (BrowseMode == ScanBrowseMode.HotspotPathTree)
        {
            // 已在“按路径”模式下再次点击：切换一键展开 / 收起
            IsHotspotPathTreeExpanded = !IsHotspotPathTreeExpanded;
            RebuildVisibleRows();
            return;
        }

        _lastHotspotMode = ScanBrowseMode.HotspotPathTree;
        BrowseMode = ScanBrowseMode.HotspotPathTree;
    }

    [RelayCommand]
    private void SetFastVerification() => UseContentHash = false;

    [RelayCommand]
    private void SetHashVerification() => UseContentHash = true;

    [RelayCommand]
    private void ToggleDetailedLogs() => ShowDetailedLogs = !ShowDetailedLogs;
}
