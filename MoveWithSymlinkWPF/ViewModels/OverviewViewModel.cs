using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MigrationCore.Models;
using MigrationCore.Models.Scanning;
using MigrationCore.Services;
using MigrationCore.Services.Scanning;

namespace MoveWithSymlinkWPF.ViewModels;

/// <summary>
/// 概览页单张磁盘状态卡片模型（全量使用预冻结画刷，零运行时画刷分配）。
/// </summary>
public sealed record OverviewDriveCardItem(
    string DriveRoot,
    string DriveLetter,
    string DisplayTitle,
    string RoleBadgeText,
    string FileSystem,
    double UsedPercent,
    string UsedPercentText,
    string FreeSpaceText,
    string CapacityRatioText,
    bool IsSystemDrive,
    bool IsWarning,
    bool IsCritical,
    Brush ProgressBrush,
    Brush RoleBadgeBackground,
    Brush RoleBadgeForeground,
    Brush CardBorderBrush);

/// <summary>
/// 概览页最近迁移/还原活动摘要条目。
/// </summary>
public sealed record OverviewRecentActivityItem(
    Guid TransactionId,
    string DisplayTitle,
    string SourcePath,
    string ModeBadgeText,
    string StageBadgeText,
    string UpdatedAtText,
    Brush StageBadgeBackground,
    Brush StageBadgeForeground,
    Brush StageBadgeBorderBrush);

/// <summary>
/// 玲珑星核一键迁移预设的轻量内存摘要（排除测试项，零额外磁盘/注册表扫盘）。
/// </summary>
public sealed record OverviewQuickMigrateSummary(
    bool IsReady,
    int PendingCount,
    int MigratedCount,
    IReadOnlyList<string> MatchedGroupNames)
{
    public bool HasMatchedPresets => PendingCount > 0 || MigratedCount > 0;
}

/// <summary>
/// 应用默认入口「概览」首页 ViewModel。
/// 严格遵守 UI 线程零阻塞与分层异步加载约束：仅复用共享态势缓存与事务元数据，绝不触发扫盘或逐文件核验。
/// </summary>
public partial class OverviewViewModel : ObservableObject
{
    private static readonly TimeSpan RefreshTtl = TimeSpan.FromSeconds(15);

    private static readonly Brush PrimaryBrush = CreateFrozenBrush("#0078D4");
    private static readonly Brush PrimarySoftBrush = CreateFrozenBrush("#EBF5FF");
    private static readonly Brush PrimaryBorderBrush = CreateFrozenBrush("#BFDBFE");

    private static readonly Brush GreenBrush = CreateFrozenBrush("#0F7B4B");
    private static readonly Brush GreenSoftBrush = CreateFrozenBrush("#ECFDF5");
    private static readonly Brush GreenBorderBrush = CreateFrozenBrush("#A7F3D0");

    private static readonly Brush AmberBrush = CreateFrozenBrush("#B45309");
    private static readonly Brush AmberAccentBrush = CreateFrozenBrush("#D97706");
    private static readonly Brush AmberSoftBrush = CreateFrozenBrush("#FFFBEB");
    private static readonly Brush AmberBorderBrush = CreateFrozenBrush("#FDE68A");

    private static readonly Brush RedBrush = CreateFrozenBrush("#C42B1C");
    private static readonly Brush RedSoftBrush = CreateFrozenBrush("#FEF2F2");
    private static readonly Brush RedBorderBrush = CreateFrozenBrush("#FECACA");

    private static readonly Brush NeutralSurfaceBrush = CreateFrozenBrush("#F8FAFC");
    private static readonly Brush NeutralBorderBrush = CreateFrozenBrush("#E2E8F0");
    private static readonly Brush NeutralMutedBrush = CreateFrozenBrush("#64748B");

    private readonly SharedWorkspaceStateCache _workspaceCache;
    private readonly Func<Task<IReadOnlyList<MigrationTransactionRecord>>> _readTransactionsAsync;
    private readonly Func<OverviewQuickMigrateSummary> _readQuickMigrateSummary;
    private readonly Func<IReadOnlyList<VolumeSnapshotLookup>> _readScanSnapshots;
    private readonly Action<string?> _openDiskScanCallback;
    private readonly Action _openCleanupCallback;
    private readonly Action _openQuarantineCallback;
    private readonly Action _openManualModeCallback;
    private readonly Action _openQuickMigrateCallback;
    private readonly Action _openAiAssistantCallback;
    private readonly Action _openBackupCenterCallback;

    private CancellationTokenSource? _refreshCts;
    private long _refreshVersion;
    private DateTimeOffset _lastRefreshedUtc = DateTimeOffset.MinValue;
    private string _lastDrivesSignature = string.Empty;
    private string _lastActivitiesSignature = string.Empty;

    public ObservableCollection<OverviewDriveCardItem> VisibleDrives { get; } = [];
    public ObservableCollection<OverviewRecentActivityItem> RecentActivities { get; } = [];

    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    private bool _hasDrives;

    [ObservableProperty]
    private int _driveColumns = 2;

    [ObservableProperty]
    private bool _hasExtraDrives;

    [ObservableProperty]
    private string _extraDrivesHintText = string.Empty;

    [ObservableProperty]
    private string _lastRefreshedText = "正在读取本机空间态势…";

    // 环境状态胶囊
    [ObservableProperty]
    private string _adminBadgeText = "正在检测权限…";

    [ObservableProperty]
    private Brush _adminBadgeBackground = NeutralSurfaceBrush;

    [ObservableProperty]
    private Brush _adminBadgeForeground = NeutralMutedBrush;

    [ObservableProperty]
    private Brush _adminBadgeBorderBrush = NeutralBorderBrush;

    [ObservableProperty]
    private string _targetReadinessText = "检测可用 NTFS 目标盘…";

    // 三大通用入口副状态
    [ObservableProperty]
    private string _diskScanCardSubtitle = "只读高速扫描本机卷，定位最占空间的文件夹与大软件。";

    [ObservableProperty]
    private string _diskScanCardMeta = "按需扫描 · 不自动后台扫盘";

    [ObservableProperty]
    private string _cleanupCardSubtitle = "按 3 档安全分级清理系统缓存、应用缓存与疑似残留，先入隔离区可撤回。";

    [ObservableProperty]
    private string _cleanupCardMeta = "多盘规则就绪 · 隔离区安全兜底";

    [ObservableProperty]
    private string _manualCardSubtitle = "自由指定任意源目录与目标目录，支持符号链接迁移、原路还原与异常修复。";

    [ObservableProperty]
    private string _manualCardMeta = "源盘保留备份 · 失败自动回滚";

    // 玲珑星核一键迁移（自适应高亮/收束）
    [ObservableProperty]
    private bool _hasLingLongPresets;

    [ObservableProperty]
    private string _lingLongBadgeText = "专属预设";

    [ObservableProperty]
    private string _lingLongDescription = "针对玲珑星核与智玲同学等内置目录的预设迁移通道，支持批量迁移与一键还原。";

    [ObservableProperty]
    private string _lingLongActionText = "查看预设";

    [ObservableProperty]
    private Brush _lingLongCardBackground = NeutralSurfaceBrush;

    [ObservableProperty]
    private Brush _lingLongCardBorderBrush = NeutralBorderBrush;

    [ObservableProperty]
    private Brush _lingLongBadgeBackground = NeutralSurfaceBrush;

    [ObservableProperty]
    private Brush _lingLongBadgeForeground = NeutralMutedBrush;

    [ObservableProperty]
    private Brush _lingLongBadgeBorderBrush = NeutralBorderBrush;

    // 底部左栏：安全与待办
    [ObservableProperty]
    private int _managedMigrationCount;

    [ObservableProperty]
    private int _unverifiedBackupCount;

    [ObservableProperty]
    private bool _hasUnverifiedBackups;

    [ObservableProperty]
    private string _backupSummaryTitle = "迁移备份状态正常";

    [ObservableProperty]
    private string _backupSummaryDetail = "暂无待核验备份；已完成的迁移可在备份管理中随时查看或还原。";

    [ObservableProperty]
    private int _quarantineBatchCount;

    [ObservableProperty]
    private bool _hasQuarantineItems;

    [ObservableProperty]
    private string _quarantineSummaryTitle = "隔离区暂无暂存";

    [ObservableProperty]
    private string _quarantineSummaryDetail = "垃圾清理默认先移入同卷隔离区，确认软件正常后再永久释放空间。";

    // 底部右栏：最近活动 / 新手 3 步指引
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOnboardingGuide))]
    private bool _hasRecentActivities;

    public bool ShowOnboardingGuide => !HasRecentActivities;

    public OverviewViewModel(
        Action<string?>? openDiskScanCallback = null,
        Action? openCleanupCallback = null,
        Action? openQuarantineCallback = null,
        Action? openManualModeCallback = null,
        Action? openQuickMigrateCallback = null,
        Action? openAiAssistantCallback = null,
        Action? openBackupCenterCallback = null,
        SharedWorkspaceStateCache? workspaceCache = null,
        Func<Task<IReadOnlyList<MigrationTransactionRecord>>>? readTransactionsAsync = null,
        Func<OverviewQuickMigrateSummary>? readQuickMigrateSummary = null,
        Func<IReadOnlyList<VolumeSnapshotLookup>>? readScanSnapshots = null)
    {
        _openDiskScanCallback = openDiskScanCallback ?? (_ => { });
        _openCleanupCallback = openCleanupCallback ?? (() => { });
        _openQuarantineCallback = openQuarantineCallback ?? (() => { });
        _openManualModeCallback = openManualModeCallback ?? (() => { });
        _openQuickMigrateCallback = openQuickMigrateCallback ?? (() => { });
        _openAiAssistantCallback = openAiAssistantCallback ?? (() => { });
        _openBackupCenterCallback = openBackupCenterCallback ?? (() => { });
        _workspaceCache = workspaceCache ?? SharedWorkspaceStateCache.Shared;
        _readTransactionsAsync = readTransactionsAsync ?? DefaultReadTransactionsAsync;
        _readQuickMigrateSummary = readQuickMigrateSummary ?? (() => new OverviewQuickMigrateSummary(false, 0, 0, []));
        _readScanSnapshots = readScanSnapshots ?? (() => VolumeSnapshotCache.Shared.ListActive(runLiveProbe: false));

        // 零阻塞首帧：若进程共享缓存已有快照则立即同步渲染，绝不在此处发起磁盘 I/O
        WorkspaceStateSnapshot? cached = _workspaceCache.GetCachedOrNull();
        if (cached is not null)
        {
            ApplyWorkspaceSnapshot(cached);
        }

        RefreshScanSnapshotHint();
        RefreshQuickMigrateSummary();
    }

    private static async Task<IReadOnlyList<MigrationTransactionRecord>> DefaultReadTransactionsAsync()
    {
        return await new MigrationTransactionStore().ReadAllAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 进入概览首页时的非阻塞异步刷新入口（短 TTL 节流，支持并发取消与版本防竞态）。
    /// </summary>
    public async Task OnEnteredOverviewPageAsync(bool force = false)
    {
        RefreshScanSnapshotHint();
        RefreshQuickMigrateSummary();

        if (!force && (DateTimeOffset.UtcNow - _lastRefreshedUtc) < RefreshTtl && HasDrives)
        {
            return;
        }

        CancellationTokenSource? previous = Interlocked.Exchange(ref _refreshCts, new CancellationTokenSource());
        try { previous?.Cancel(); } catch { }
        previous?.Dispose();

        CancellationToken ct = _refreshCts!.Token;
        long version = Interlocked.Increment(ref _refreshVersion);

        DispatchToUi(() =>
        {
            if (version == Volatile.Read(ref _refreshVersion))
            {
                IsRefreshing = true;
            }
        });

        try
        {
            Task<WorkspaceStateSnapshot> workspaceTask = _workspaceCache.GetOrRefreshAsync(force: force, ct: ct);
            Task<IReadOnlyList<MigrationTransactionRecord>> recordsTask = SafeReadTransactionsAsync(ct);

            await Task.WhenAll(workspaceTask, recordsTask).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            WorkspaceStateSnapshot snapshot = await workspaceTask.ConfigureAwait(false);
            IReadOnlyList<MigrationTransactionRecord> records = await recordsTask.ConfigureAwait(false);

            OverviewRecentActivityItem[] recentItems = BuildRecentActivityItems(records);
            int managedCount = records
                .DistinctBy(r => r.TransactionId)
                .Count(r => r.Mode == MigrationMode.Migrate && r.Stage != MigrationStage.Failed && r.Stage != MigrationStage.Cancelled);

            await DispatchToUiAsync(() =>
            {
                if (ct.IsCancellationRequested || version != Volatile.Read(ref _refreshVersion))
                {
                    return;
                }

                ApplyWorkspaceSnapshot(snapshot);
                ApplyTransactionSummary(managedCount, recentItems);
                RefreshScanSnapshotHint();
                RefreshQuickMigrateSummary();
                _lastRefreshedUtc = DateTimeOffset.UtcNow;
                LastRefreshedText = $"已更新 · {DateTime.Now:HH:mm:ss}";
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            await DispatchToUiAsync(() =>
            {
                if (version == Volatile.Read(ref _refreshVersion))
                {
                    LastRefreshedText = "状态刷新受限，可稍后重试";
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            await DispatchToUiAsync(() =>
            {
                if (version == Volatile.Read(ref _refreshVersion))
                {
                    IsRefreshing = false;
                }
            }).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<MigrationTransactionRecord>> SafeReadTransactionsAsync(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            return await _readTransactionsAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// 纯内存刷新最近磁盘扫描缓存提示（0ms，不触发扫盘）。
    /// </summary>
    public void RefreshScanSnapshotHint()
    {
        IReadOnlyList<VolumeSnapshotLookup> active;
        try
        {
            active = _readScanSnapshots();
        }
        catch
        {
            active = [];
        }

        if (active.Count == 0)
        {
            DiskScanCardSubtitle = "只读高速扫描本机卷，定位最占空间的文件夹与大软件。";
            DiskScanCardMeta = "按需扫描 · 不自动后台扫盘";
            return;
        }

        VolumeSnapshotLookup latest = active.OrderByDescending(x => x.Entry.CachedAtUtc).First();
        string driveLabel = latest.Entry.Snapshot.Volume.DriveRoot.TrimEnd('\\');
        int hotspotCount = latest.Entry.Snapshot.GetHotspotDirectories().Count();
        string ageText = FormatAge(latest.Age);
        DiskScanCardSubtitle = $"上次扫描 {driveLabel}（{ageText}），已用 {FileStatsService.FormatBytes(latest.Entry.Snapshot.Volume.UsedBytes)}。";
        DiskScanCardMeta = hotspotCount > 0
            ? $"发现 {hotspotCount} 个热点大目录 · 点击直接查看"
            : "已有本会话缓存 · 点击零等待浏览";
    }

    /// <summary>
    /// 纯内存刷新玲珑星核一键迁移自适应状态（复用 QuickMigrateViewModel 已加载结果，排除测试配置项）。
    /// </summary>
    public void RefreshQuickMigrateSummary()
    {
        OverviewQuickMigrateSummary summary;
        try
        {
            summary = _readQuickMigrateSummary();
        }
        catch
        {
            summary = new OverviewQuickMigrateSummary(false, 0, 0, []);
        }

        HasLingLongPresets = summary.HasMatchedPresets;

        if (!summary.IsReady)
        {
            LingLongBadgeText = "正在检测预设";
            LingLongDescription = "正在检测本机是否安装玲珑星核 / 智玲同学预设目录…";
            LingLongActionText = "查看预设";
            ApplyLingLongCardPalette(highlighted: false);
            return;
        }

        if (summary.PendingCount > 0)
        {
            string names = summary.MatchedGroupNames.Count > 0
                ? string.Join("、", summary.MatchedGroupNames.Take(2))
                : "玲珑星核预设";
            LingLongBadgeText = $"发现 {summary.PendingCount} 项待迁移";
            LingLongDescription = $"检测到 {names} 等 {summary.PendingCount} 项预设目录可一键迁移"
                + (summary.MigratedCount > 0 ? $"（另有 {summary.MigratedCount} 项已托管）" : "，支持一键还原。");
            LingLongActionText = "立即迁移";
            ApplyLingLongCardPalette(highlighted: true);
        }
        else if (summary.MigratedCount > 0)
        {
            LingLongBadgeText = $"已托管 {summary.MigratedCount} 项";
            LingLongDescription = $"本机 {summary.MigratedCount} 项玲珑星核预设目录已完成迁移，可随时查看状态或一键还原。";
            LingLongActionText = "管理预设";
            ApplyLingLongCardPalette(highlighted: true);
        }
        else
        {
            LingLongBadgeText = "玲珑星核专属";
            LingLongDescription = "适配玲珑星核 / 智玲同学等内置目录（本机未检测到匹配项，普通电脑请优先使用上方通用入口）。";
            LingLongActionText = "查看配置";
            ApplyLingLongCardPalette(highlighted: false);
        }
    }

    private void ApplyLingLongCardPalette(bool highlighted)
    {
        if (highlighted)
        {
            LingLongCardBackground = PrimarySoftBrush;
            LingLongCardBorderBrush = PrimaryBorderBrush;
            LingLongBadgeBackground = Brushes.White;
            LingLongBadgeForeground = PrimaryBrush;
            LingLongBadgeBorderBrush = PrimaryBorderBrush;
        }
        else
        {
            LingLongCardBackground = NeutralSurfaceBrush;
            LingLongCardBorderBrush = NeutralBorderBrush;
            LingLongBadgeBackground = Brushes.White;
            LingLongBadgeForeground = NeutralMutedBrush;
            LingLongBadgeBorderBrush = NeutralBorderBrush;
        }
    }

    private void ApplyWorkspaceSnapshot(WorkspaceStateSnapshot snapshot)
    {
        // 1. 环境权限与可用目标盘胶囊
        if (snapshot.IsAdministrator)
        {
            AdminBadgeText = "管理员权限就绪";
            AdminBadgeBackground = GreenSoftBrush;
            AdminBadgeForeground = GreenBrush;
            AdminBadgeBorderBrush = GreenBorderBrush;
        }
        else if (snapshot.IsDeveloperModeEnabled)
        {
            AdminBadgeText = "开发者模式（软链可用）";
            AdminBadgeBackground = PrimarySoftBrush;
            AdminBadgeForeground = PrimaryBrush;
            AdminBadgeBorderBrush = PrimaryBorderBrush;
        }
        else
        {
            AdminBadgeText = "标准权限 · 建议管理员运行";
            AdminBadgeBackground = AmberSoftBrush;
            AdminBadgeForeground = AmberBrush;
            AdminBadgeBorderBrush = AmberBorderBrush;
        }

        SharedDriveTelemetry[] fixedDrives = snapshot.Drives
            .Where(d => d.IsFixedDrive && d.TotalBytes > 0)
            .OrderByDescending(d => d.IsSystemDrive)
            .ThenBy(d => d.DriveLetter, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        int targetCount = fixedDrives.Count(d => d.SupportsSymlinkMigrationTarget);
        TargetReadinessText = targetCount > 0
            ? $"{targetCount} 个非系统 NTFS 目标盘可用"
            : "未检测到非系统 NTFS 目标盘";

        // 2. 磁盘卡片（最多平铺 4 张，超出的以摘要提示防撑爆 1000x700 首屏）
        const int MaxVisibleDrives = 4;
        OverviewDriveCardItem[] cards = fixedDrives
            .Take(MaxVisibleDrives)
            .Select(BuildDriveCard)
            .ToArray();

        string drivesSig = string.Join("|", cards.Select(c => $"{c.DriveRoot}:{c.UsedPercentText}:{c.FreeSpaceText}"));
        if (!string.Equals(drivesSig, _lastDrivesSignature, StringComparison.Ordinal))
        {
            VisibleDrives.Clear();
            foreach (OverviewDriveCardItem card in cards)
            {
                VisibleDrives.Add(card);
            }
            _lastDrivesSignature = drivesSig;
        }

        HasDrives = VisibleDrives.Count > 0;
        DriveColumns = Math.Clamp(VisibleDrives.Count, 1, MaxVisibleDrives);

        int extraCount = Math.Max(0, fixedDrives.Length - MaxVisibleDrives);
        HasExtraDrives = extraCount > 0;
        ExtraDrivesHintText = extraCount > 0
            ? $"另有 {extraCount} 个本地卷，可在「磁盘扫描」中查看全部"
            : string.Empty;

        // 3. 备份汇总与隔离区汇总（纯元数据）
        UnverifiedBackupCount = snapshot.Backups.UnverifiedBackupCount;
        HasUnverifiedBackups = UnverifiedBackupCount > 0;
        if (HasUnverifiedBackups)
        {
            string sizeSuffix = snapshot.Backups.HasEstimatedBytes && snapshot.Backups.EstimatedBackupBytes > 0
                ? $"（约 {FileStatsService.FormatBytes(snapshot.Backups.EstimatedBackupBytes)} 可释放）"
                : string.Empty;
            BackupSummaryTitle = $"{UnverifiedBackupCount} 份迁移原备份待核验{sizeSuffix}";
            BackupSummaryDetail = "原目录备份仍保留在源盘；确认软件运行正常后，前往备份管理手动删除即可释放空间。";
        }
        else
        {
            BackupSummaryTitle = ManagedMigrationCount > 0
                ? $"已托管 {ManagedMigrationCount} 项迁移 · 无待核验备份"
                : "暂无待核验迁移备份";
            BackupSummaryDetail = "迁移产生的原目录备份绝不会自动删除，可在备份管理中随时核验或还原。";
        }

        QuarantineBatchCount = snapshot.Quarantine.TotalBatchCount;
        HasQuarantineItems = QuarantineBatchCount > 0;
        if (HasQuarantineItems)
        {
            string bytesText = FileStatsService.FormatBytes(snapshot.Quarantine.TotalBytes);
            QuarantineSummaryTitle = $"隔离区暂存 {QuarantineBatchCount} 个批次（共 {bytesText}）";
            QuarantineSummaryDetail = "清理的文件已安全隔离在原卷，确认软件无异常后可一键释放空间，或随时原路恢复。";
            CleanupCardMeta = $"隔离区暂存 {QuarantineBatchCount} 批（{bytesText}）· 可释放或恢复";
        }
        else
        {
            QuarantineSummaryTitle = "隔离区暂无暂存批次";
            QuarantineSummaryDetail = "垃圾清理默认先移入同卷隔离区，确认无异常后再永久释放空间。";
            CleanupCardMeta = "多盘规则就绪 · 隔离区安全兜底";
        }
    }

    private void ApplyTransactionSummary(int managedCount, OverviewRecentActivityItem[] recentItems)
    {
        ManagedMigrationCount = managedCount;
        if (!HasUnverifiedBackups)
        {
            BackupSummaryTitle = managedCount > 0
                ? $"已托管 {managedCount} 项迁移 · 无待核验备份"
                : "暂无待核验迁移备份";
        }

        string sig = string.Join("|", recentItems.Select(i => $"{i.TransactionId}:{i.StageBadgeText}:{i.UpdatedAtText}"));
        if (!string.Equals(sig, _lastActivitiesSignature, StringComparison.Ordinal))
        {
            RecentActivities.Clear();
            foreach (OverviewRecentActivityItem item in recentItems)
            {
                RecentActivities.Add(item);
            }
            _lastActivitiesSignature = sig;
        }

        HasRecentActivities = RecentActivities.Count > 0;
    }

    private static OverviewDriveCardItem BuildDriveCard(SharedDriveTelemetry d)
    {
        bool isCritical = d.UsedPercent >= 90.0 || (d.IsSystemDrive && d.FreeBytes < 15L * 1024 * 1024 * 1024);
        bool isWarning = !isCritical && (d.UsedPercent >= 78.0 || (d.IsSystemDrive && d.FreeBytes < 35L * 1024 * 1024 * 1024));

        string volumeName = string.IsNullOrWhiteSpace(d.VolumeLabel)
            ? (d.IsSystemDrive ? "系统盘" : "本地磁盘")
            : d.VolumeLabel.Trim();
        string displayTitle = $"{d.DriveLetter} {volumeName}";

        string roleBadge = d.IsSystemDrive
            ? (isCritical ? "系统盘 · 空间告急" : isWarning ? "系统盘 · 空间偏紧" : "系统盘")
            : d.SupportsSymlinkMigrationTarget
                ? (isCritical ? "NTFS · 空间告急" : isWarning ? "NTFS · 空间偏紧" : "推荐目标盘")
                : d.FileSystem;

        Brush progressBrush = isCritical ? RedBrush : isWarning ? AmberAccentBrush : PrimaryBrush;
        Brush badgeBg = isCritical ? RedSoftBrush
            : isWarning ? AmberSoftBrush
            : d.SupportsSymlinkMigrationTarget ? GreenSoftBrush
            : PrimarySoftBrush;
        Brush badgeFg = isCritical ? RedBrush
            : isWarning ? AmberBrush
            : d.SupportsSymlinkMigrationTarget ? GreenBrush
            : PrimaryBrush;
        Brush borderBrush = isCritical ? RedBorderBrush
            : isWarning ? AmberBorderBrush
            : NeutralBorderBrush;

        return new OverviewDriveCardItem(
            DriveRoot: d.DriveRoot,
            DriveLetter: d.DriveLetter,
            DisplayTitle: displayTitle,
            RoleBadgeText: roleBadge,
            FileSystem: d.FileSystem,
            UsedPercent: Math.Clamp(d.UsedPercent, 0, 100),
            UsedPercentText: $"{d.UsedPercent:F0}%",
            FreeSpaceText: $"剩余 {FileStatsService.FormatBytes(d.FreeBytes)}",
            CapacityRatioText: $"已用 {FileStatsService.FormatBytes(d.UsedBytes)} / {FileStatsService.FormatBytes(d.TotalBytes)}",
            IsSystemDrive: d.IsSystemDrive,
            IsWarning: isWarning,
            IsCritical: isCritical,
            ProgressBrush: progressBrush,
            RoleBadgeBackground: badgeBg,
            RoleBadgeForeground: badgeFg,
            CardBorderBrush: borderBrush);
    }

    private static OverviewRecentActivityItem[] BuildRecentActivityItems(IReadOnlyList<MigrationTransactionRecord> records)
    {
        if (records.Count == 0)
        {
            return [];
        }

        return records
            .DistinctBy(r => r.TransactionId)
            .OrderByDescending(r => r.UpdatedAtUtc)
            .Take(4)
            .Select(MapRecentActivity)
            .ToArray();
    }

    private static OverviewRecentActivityItem MapRecentActivity(MigrationTransactionRecord r)
    {
        string trimmed = r.SourcePath.TrimEnd('\\');
        string leaf = Path.GetFileName(trimmed);
        string parent = Path.GetFileName(Path.GetDirectoryName(trimmed) ?? string.Empty);
        string title = string.IsNullOrWhiteSpace(parent)
            ? (string.IsNullOrWhiteSpace(leaf) ? r.SourcePath : leaf)
            : $"{parent} / {leaf}";

        string modeBadge = r.Mode switch
        {
            MigrationMode.Restore => "还原",
            MigrationMode.Repair => "修复",
            _ => "迁移"
        };

        bool isAbnormal = r.Stage is MigrationStage.RecoveryRequired or MigrationStage.Failed
            || r.BackupState is BackupState.DeleteFailed or BackupState.PartiallyDeleted or BackupState.IdentityChanged;
        bool isCleaned = r.BackupState is BackupState.Deleted or BackupState.Recycled;
        bool isUnverified = r.Stage == MigrationStage.AwaitingUserVerification
            && r.UserVerifiedAtUtc is null
            && !isCleaned;

        string stageBadge = r.Stage switch
        {
            MigrationStage.RecoveryRequired => "中断待恢复",
            MigrationStage.Failed => "执行失败",
            MigrationStage.Cancelled => "已取消",
            MigrationStage.AwaitingUserVerification when isCleaned => "已完成并释放",
            MigrationStage.AwaitingUserVerification when r.UserVerifiedAtUtc is not null => "已验证",
            MigrationStage.AwaitingUserVerification => "待验证备份",
            _ => r.Stage.ToString()
        };

        Brush bg = isAbnormal ? RedSoftBrush
            : isUnverified ? AmberSoftBrush
            : isCleaned ? GreenSoftBrush
            : PrimarySoftBrush;
        Brush fg = isAbnormal ? RedBrush
            : isUnverified ? AmberBrush
            : isCleaned ? GreenBrush
            : PrimaryBrush;
        Brush border = isAbnormal ? RedBorderBrush
            : isUnverified ? AmberBorderBrush
            : isCleaned ? GreenBorderBrush
            : PrimaryBorderBrush;

        return new OverviewRecentActivityItem(
            TransactionId: r.TransactionId,
            DisplayTitle: title,
            SourcePath: r.SourcePath,
            ModeBadgeText: modeBadge,
            StageBadgeText: stageBadge,
            UpdatedAtText: r.UpdatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm"),
            StageBadgeBackground: bg,
            StageBadgeForeground: fg,
            StageBadgeBorderBrush: border);
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalSeconds < 60) return "刚刚";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes} 分钟前";
        return $"{(int)age.TotalHours} 小时前";
    }

    private static Brush CreateFrozenBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static void DispatchToUi(Action action)
    {
        if (Application.Current?.Dispatcher is { } dispatcher
            && !dispatcher.HasShutdownStarted
            && !dispatcher.HasShutdownFinished
            && dispatcher.Thread.IsAlive)
        {
            if (dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                dispatcher.BeginInvoke(action);
            }
            return;
        }

        action();
    }

    private static Task DispatchToUiAsync(Action action)
    {
        if (Application.Current?.Dispatcher is { } dispatcher
            && !dispatcher.HasShutdownStarted
            && !dispatcher.HasShutdownFinished
            && dispatcher.Thread.IsAlive)
        {
            if (dispatcher.CheckAccess())
            {
                action();
                return Task.CompletedTask;
            }

            return dispatcher.InvokeAsync(action).Task;
        }

        action();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task RefreshAsync() => OnEnteredOverviewPageAsync(force: true);

    [RelayCommand]
    private void OpenDiskScan(string? driveRoot) => _openDiskScanCallback(driveRoot);

    [RelayCommand]
    private void OpenCleanup() => _openCleanupCallback();

    [RelayCommand]
    private void OpenQuarantine() => _openQuarantineCallback();

    [RelayCommand]
    private void OpenManualMode() => _openManualModeCallback();

    [RelayCommand]
    private void OpenQuickMigrate() => _openQuickMigrateCallback();

    [RelayCommand]
    private void OpenAiAssistant() => _openAiAssistantCallback();

    [RelayCommand]
    private void OpenBackupCenter() => _openBackupCenterCallback();
}
