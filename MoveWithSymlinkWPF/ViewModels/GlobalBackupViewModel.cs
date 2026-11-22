using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MigrationCore.Models;
using MigrationCore.Services;
using MoveWithSymlinkWPF.Helpers;
using MoveWithSymlinkWPF.Views;

namespace MoveWithSymlinkWPF.ViewModels;

public enum GlobalBackupFilter
{
    All,
    Unverified,
    VerifiedRetained,
    RestoreBackups,
    Cleaned,
    Abnormal
}

public sealed record GlobalBackupItemView(
    Guid TransactionId,
    MigrationTransactionRecord Record,
    string DisplayTitle,
    string ModeLabel,
    string StageLabel,
    string BackupStateLabel,
    string VerificationModeLabel,
    string SourcePath,
    string TargetPath,
    string BackupPathDisplay,
    string UpdatedAtDisplay,
    string LastErrorDisplay,
    bool HasLiveBackup,
    bool CanOpenVerifyDialog,
    bool CanJumpToRestore,
    bool HasDeletionDetails,
    bool IsRecoveryRequired,
    bool IsPendingUnverified)
{
    public string BadgeBackground => IsRecoveryRequired ? "#FEF2F2"
        : IsPendingUnverified ? "#FFFBEB"
        : Record.BackupState is BackupState.Deleted or BackupState.Recycled ? "#ECFDF5"
        : "#EBF5FF";

    public string BadgeBorder => IsRecoveryRequired ? "#FECACA"
        : IsPendingUnverified ? "#FDE68A"
        : Record.BackupState is BackupState.Deleted or BackupState.Recycled ? "#A7F3D0"
        : "#BFDBFE";

    public string BadgeForeground => IsRecoveryRequired ? "#C42B1C"
        : IsPendingUnverified ? "#B45309"
        : Record.BackupState is BackupState.Deleted or BackupState.Recycled ? "#0F7B4B"
        : "#0078D4";
}

/// <summary>
/// 全局备份管理中心 ViewModel（对齐 docs/untitled.pen Artboard 05）：
/// 统一收口手动、一键、磁盘扫描与还原的全部持久化事务与中断待恢复项。
/// </summary>
public partial class GlobalBackupViewModel : ObservableObject
{
    private List<GlobalBackupItemView> _allItems = [];

    public ObservableCollection<GlobalBackupItemView> RecoveryItems { get; } = [];
    public ObservableCollection<GlobalBackupItemView> FilteredItems { get; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasRecoveryItems;
    [ObservableProperty] private bool _hasUnverifiedBanner;
    [ObservableProperty] private string _unverifiedBannerTitle = string.Empty;
    [ObservableProperty] private string _summaryText = "正在加载全模式事务与备份记录…";

    [ObservableProperty] private string _tabAllText = "全部";
    [ObservableProperty] private string _tabUnverifiedText = "待验证 (0)";
    [ObservableProperty] private string _tabVerifiedText = "已验证可清理 (0)";
    [ObservableProperty] private string _tabRestoreText = "还原副本 (0)";
    [ObservableProperty] private string _tabCleanedText = "历史已处理 (0)";
    [ObservableProperty] private string _tabAbnormalText = "异常 (0)";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterAllTag))]
    [NotifyPropertyChangedFor(nameof(FilterUnverifiedTag))]
    [NotifyPropertyChangedFor(nameof(FilterVerifiedTag))]
    [NotifyPropertyChangedFor(nameof(FilterRestoreTag))]
    [NotifyPropertyChangedFor(nameof(FilterCleanedTag))]
    [NotifyPropertyChangedFor(nameof(FilterAbnormalTag))]
    private GlobalBackupFilter _activeFilter = GlobalBackupFilter.All;

    public string FilterAllTag => ActiveFilter == GlobalBackupFilter.All ? "Active" : string.Empty;
    public string FilterUnverifiedTag => ActiveFilter == GlobalBackupFilter.Unverified ? "Active" : string.Empty;
    public string FilterVerifiedTag => ActiveFilter == GlobalBackupFilter.VerifiedRetained ? "Active" : string.Empty;
    public string FilterRestoreTag => ActiveFilter == GlobalBackupFilter.RestoreBackups ? "Active" : string.Empty;
    public string FilterCleanedTag => ActiveFilter == GlobalBackupFilter.Cleaned ? "Active" : string.Empty;
    public string FilterAbnormalTag => ActiveFilter == GlobalBackupFilter.Abnormal ? "Active" : string.Empty;

    public GlobalBackupViewModel()
    {
        _ = RefreshAsync();
    }

    partial void OnActiveFilterChanged(GlobalBackupFilter value) => ApplyFilter();

    [RelayCommand]
    private void SetFilter(string? filterName)
    {
        if (Enum.TryParse<GlobalBackupFilter>(filterName, out var parsed))
        {
            ActiveFilter = parsed;
        }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            var records = await new MigrationTransactionStore().ReadAllAsync();
            var pendingIds = await Task.Run(() => UnverifiedExitConfirmation.SelectPendingUnverified(records)
                .Select(r => r.TransactionId).ToHashSet());
            var ordered = await Task.Run(() => records
                .DistinctBy(r => r.TransactionId)
                .OrderByDescending(r => r.UpdatedAtUtc)
                .Select(r => MapRecord(r, pendingIds.Contains(r.TransactionId)))
                .ToList());

            _allItems = ordered;
            RecoveryItems.Clear();
            foreach (var rec in ordered.Where(i => i.IsRecoveryRequired))
            {
                RecoveryItems.Add(rec);
            }
            HasRecoveryItems = RecoveryItems.Count > 0;

            int unverified = pendingIds.Count;
            int verifiedRetained = ordered.Count(i => i.Record.UserVerifiedAtUtc is not null && i.HasLiveBackup);
            int restoreCount = ordered.Count(i => i.Record.Mode == MigrationMode.Restore);
            int cleanedCount = ordered.Count(i => i.Record.BackupState is BackupState.Deleted or BackupState.Recycled);
            int abnormalCount = ordered.Count(IsAbnormal);

            HasUnverifiedBanner = unverified > 0;
            UnverifiedBannerTitle = $"当前有 {unverified} 份待验证原始备份仍在源盘保留";
            TabAllText = $"全部 ({ordered.Count})";
            TabUnverifiedText = $"待验证 ({unverified})";
            TabVerifiedText = $"已验证可清理 ({verifiedRetained})";
            TabRestoreText = $"还原副本 ({restoreCount})";
            TabCleanedText = $"历史已处理 ({cleanedCount})";
            TabAbnormalText = $"异常 ({abnormalCount})";

            SummaryText = "统一管理手动模式、一键迁移、磁盘扫描与还原产生的全部备份；验证软件正常后再手动删除释放空间。";

            ApplyFilter();

            if (Application.Current?.MainWindow?.DataContext is MainViewModel main)
            {
                await main.RefreshGlobalStatusAsync();
            }
        }
        catch (Exception ex)
        {
            SummaryText = $"读取全局事务库失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task VerifyAndManageBackupAsync(GlobalBackupItemView? item)
    {
        if (item is null || !item.CanOpenVerifyDialog) return;
        if (OperationExecutionGate.Shared.IsBusy)
        {
            CustomMessageBox.ShowWarning("当前有其他迁移或备份操作正在运行，请等待完成后再管理备份。", "操作互斥");
            return;
        }

        using var lease = OperationExecutionGate.Shared.TryAcquire("BackupVerification", $"验证并管理备份：{item.SourcePath}", [item.SourcePath, item.TargetPath]);
        if (lease is null) return;

        try
        {
            await MigrationVerificationWindow.ShowForTransactionAsync(item.TransactionId);
        }
        catch (Exception ex)
        {
            CustomMessageBox.ShowWarning($"无法打开验证与备份管理窗口：{ex.Message}", "提示");
        }
        finally
        {
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private void ViewDeletionDetails(GlobalBackupItemView? item)
    {
        if (item?.Record.LastDeletionResult is not { } result) return;
        BackupDeletionDetailsWindow.ShowDetails(item.SourcePath, item.Record.BackupPath, result);
    }

    [RelayCommand]
    private void JumpToRestore(GlobalBackupItemView? item)
    {
        if (item is null || !item.CanJumpToRestore) return;
        if (Application.Current?.MainWindow?.DataContext is MainViewModel main)
        {
            main.OpenRestoreInManualMode(item.SourcePath);
        }
    }

    [RelayCommand]
    private void OpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            CustomMessageBox.ShowInformation("该路径当前不存在于磁盘。", "提示");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    private void ApplyFilter()
    {
        FilteredItems.Clear();
        IEnumerable<GlobalBackupItemView> query = ActiveFilter switch
        {
            GlobalBackupFilter.Unverified => _allItems.Where(i => i.IsPendingUnverified),
            GlobalBackupFilter.VerifiedRetained => _allItems.Where(i => i.Record.UserVerifiedAtUtc is not null && i.HasLiveBackup),
            GlobalBackupFilter.RestoreBackups => _allItems.Where(i => i.Record.Mode == MigrationMode.Restore),
            GlobalBackupFilter.Cleaned => _allItems.Where(i => i.Record.BackupState is BackupState.Deleted or BackupState.Recycled),
            GlobalBackupFilter.Abnormal => _allItems.Where(IsAbnormal),
            _ => _allItems
        };

        foreach (var item in query)
        {
            FilteredItems.Add(item);
        }
    }

    private static bool IsAbnormal(GlobalBackupItemView i) =>
        i.IsRecoveryRequired
        || i.Record.Stage == MigrationStage.Failed
        || i.Record.BackupState is BackupState.Missing or BackupState.IdentityChanged or BackupState.DeleteFailed or BackupState.PartiallyDeleted;

    private static GlobalBackupItemView MapRecord(MigrationTransactionRecord r, bool isPendingUnverified)
    {
        bool liveBackup = r.BackupPath is not null
            && r.BackupState is not (BackupState.Deleted or BackupState.Recycled)
            && Directory.Exists(r.BackupPath);

        bool canRestore = r.Mode == MigrationMode.Migrate
            && r.Stage == MigrationStage.AwaitingUserVerification
            && SymbolicLinkHelper.IsSymbolicLink(r.SourcePath);

        string leaf = Path.GetFileName(r.SourcePath.TrimEnd('\\'));
        string parent = Path.GetFileName(Path.GetDirectoryName(r.SourcePath.TrimEnd('\\')) ?? string.Empty);
        string title = string.IsNullOrWhiteSpace(parent) ? (string.IsNullOrWhiteSpace(leaf) ? r.SourcePath : leaf) : $"{parent} / {leaf}";

        string modeLabel = r.Mode switch
        {
            MigrationMode.Restore => "还原",
            MigrationMode.Repair => "修复",
            _ => "迁移"
        };

        string stageLabel = r.Stage switch
        {
            MigrationStage.AwaitingUserVerification => r.UserVerifiedAtUtc is null ? "待用户验证" : "用户已验证",
            MigrationStage.RecoveryRequired => "中断待恢复 (RecoveryRequired)",
            MigrationStage.Failed => "执行失败",
            MigrationStage.Cancelled => "已取消",
            _ => r.Stage.ToString()
        };

        string backupLabel = r.BackupPath is null ? "无备份" : r.BackupState switch
        {
            BackupState.RetainedUnverified => liveBackup ? "源盘备份保留" : "记录缺失/已移走",
            BackupState.UserVerified => liveBackup ? "已验证可清理" : "记录缺失",
            BackupState.DeleteRequested => "待双确认处理",
            BackupState.Recycled => "已移入回收站",
            BackupState.Deleted => "已永久删除",
            BackupState.PartiallyDeleted => "部分删除",
            BackupState.DeleteFailed => "删除失败（已保留）",
            _ => r.BackupState.ToString()
        };

        string verLabel = r.VerificationMode == VerificationMode.ContentHash ? "SHA-256 核对通过" : "快速核对通过";

        return new GlobalBackupItemView(
            TransactionId: r.TransactionId,
            Record: r,
            DisplayTitle: title,
            ModeLabel: modeLabel,
            StageLabel: stageLabel,
            BackupStateLabel: backupLabel,
            VerificationModeLabel: verLabel,
            SourcePath: r.SourcePath,
            TargetPath: r.TargetPath,
            BackupPathDisplay: r.BackupPath ?? "（无备份路径）",
            UpdatedAtDisplay: r.UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            LastErrorDisplay: r.LastError ?? string.Empty,
            HasLiveBackup: liveBackup,
            CanOpenVerifyDialog: r.Stage == MigrationStage.AwaitingUserVerification,
            CanJumpToRestore: canRestore,
            HasDeletionDetails: r.LastDeletionResult is not null,
            IsRecoveryRequired: r.Stage == MigrationStage.RecoveryRequired,
            IsPendingUnverified: isPendingUnverified);
    }
}
