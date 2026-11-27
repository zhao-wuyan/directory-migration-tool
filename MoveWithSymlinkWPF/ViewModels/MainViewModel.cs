using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MigrationCore.Models;
using MigrationCore.Services;
using MoveWithSymlinkWPF.Helpers;
using MoveWithSymlinkWPF.Services;
using MoveWithSymlinkWPF.Views;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace MoveWithSymlinkWPF.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverviewNavTag))]
    private bool _isOverviewMode = !MigrationRuntime.IsTestLab;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ManualNavTag))]
    private bool _isManualMode = MigrationRuntime.IsTestLab;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QuickMigrateNavTag))]
    private bool _isQuickMigrateMode = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiskScanNavTag))]
    private bool _isDiskScanMode = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackupCenterNavTag))]
    private bool _isBackupCenterMode = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AiAssistantNavTag))]
    private bool _isAiAssistantMode = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CleanupNavTag))]
    private bool _isCleanupMode = false;

    [ObservableProperty]
    private object? _overviewPage;

    [ObservableProperty]
    private object? _quickMigratePage;

    [ObservableProperty]
    private object? _diskScanPage;

    [ObservableProperty]
    private object? _globalBackupPage;

    [ObservableProperty]
    private object? _aiAssistantPage;

    [ObservableProperty]
    private object? _cleanupPage;

    public OverviewViewModel OverviewViewModel { get; }
    public AiAssistantViewModel AiAssistantViewModel { get; }

    public string OverviewNavTag => IsOverviewMode ? "Active" : string.Empty;
    public string ManualNavTag => IsManualMode ? "Active" : string.Empty;
    public string QuickMigrateNavTag => IsQuickMigrateMode ? "Active" : string.Empty;
    public string DiskScanNavTag => IsDiskScanMode ? "Active" : string.Empty;
    public string BackupCenterNavTag => IsBackupCenterMode ? "Active" : string.Empty;
    public string AiAssistantNavTag => IsAiAssistantMode ? "Active" : string.Empty;
    public string CleanupNavTag => IsCleanupMode ? "Active" : string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnverifiedBackups))]
    [NotifyPropertyChangedFor(nameof(UnverifiedBadgeText))]
    private int _unverifiedBackupCount = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecoveryRequired))]
    private int _recoveryRequiredCount = 0;

    [ObservableProperty]
    private string _recoveryBannerMessage = string.Empty;

    public bool HasUnverifiedBackups => UnverifiedBackupCount > 0;
    public string UnverifiedBadgeText => UnverifiedBackupCount > 0 ? UnverifiedBackupCount.ToString() : string.Empty;
    public bool HasRecoveryRequired => RecoveryRequiredCount > 0;

    [ObservableProperty]
    private MigrationMode _migrationMode = MigrationMode.Migrate;

    [ObservableProperty]
    private string _currentModeDisplay = "迁移模式";

    [ObservableProperty]
    private bool _isRestoreMode = false;

    [ObservableProperty]
    private bool _isTargetPathReadOnly = false;

    [ObservableProperty]
    private bool _canRepair = false;

    [ObservableProperty]
    private string _repairHint = string.Empty;

    private bool _suppressPathDetection;

    private string _sourcePath = string.Empty;
    public string SourcePath
    {
        get => _sourcePath;
        set
        {
            if (SetProperty(ref _sourcePath, value) && !_suppressPathDetection)
            {
                // 当源路径变更时，自动检测模式
                DetectAndSwitchMode();
                // 检测是否可以修复
                CheckRepairCondition();
            }
        }
    }

    private string _targetPath = string.Empty;
    public string TargetPath
    {
        get => _targetPath;
        set
        {
            if (SetProperty(ref _targetPath, value) && !_suppressPathDetection)
            {
                // 当目标路径变更时，检测是否可以修复
                CheckRepairCondition();
            }
        }
    }

    [ObservableProperty]
    private int _largeFileThresholdMB = 1024;

    [ObservableProperty]
    private int _robocopyThreads = 8;

    [ObservableProperty]
    private int _currentStep = 1;

    [ObservableProperty]
    private bool _isValidating = false;

    [ObservableProperty]
    private bool _isScanning = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOperationBusy))]
    private bool _isMigrating = false;

    [ObservableProperty]
    private bool _migrationCompleted = false;

    [ObservableProperty]
    private string _validationMessage = string.Empty;

    [ObservableProperty]
    private bool _hasValidationError = false;

    [ObservableProperty]
    private string _statsMessage = string.Empty;

    [ObservableProperty]
    private double _progressPercent = 0;

    [ObservableProperty]
    private string _progressMessage = string.Empty;

    [ObservableProperty]
    private string _phaseDescription = string.Empty;

    [ObservableProperty]
    private bool _migrationSuccess = false;

    [ObservableProperty]
    private string _resultMessage = string.Empty;

    [ObservableProperty]
    private QuickMigrateTask? _backupTask;
    [ObservableProperty]
    private bool _useContentHash;
    public bool IsOperationBusy => IsMigrating
        || OperationExecutionGate.Shared.IsBusy
        || (QuickMigratePage is QuickMigratePage page && page.DataContext is QuickMigrateViewModel vm && vm.IsExecuting)
        || (CleanupPage is CleanupPage cp && cp.DataContext is CleanupViewModel cvm && cvm.IsExecuting);

    public ObservableCollection<string> LogMessages { get; } = new();

    private FileStats? _scannedStats;
    private CancellationTokenSource? _cancellationTokenSource;

    public string VersionText { get; }

    public MainViewModel()
    {
        // 从 version.json 或程序集获取版本号
        VersionText = VersionService.GetVersion();

        OperationExecutionGate.Shared.StateChanged += (_, _) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(IsOperationBusy)));
        };

        // 初始化一键迁移、磁盘扫描与全局备份管理页面（W4 视图落地后自动激活）
        QuickMigratePage = new QuickMigratePage();
        if (QuickMigratePage is QuickMigratePage qp && qp.DataContext is QuickMigrateViewModel qvm)
        {
            qvm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(QuickMigrateViewModel.IsExecuting))
                    OnPropertyChanged(nameof(IsOperationBusy));
            };
        }
        DiskScanPage = new DiskScanPage();
        GlobalBackupPage = new GlobalBackupPage();
        CleanupPage = new CleanupPage();

        QuickMigrateViewModel? quickMigrateVm = (QuickMigratePage as QuickMigratePage)?.DataContext as QuickMigrateViewModel;
        DiskScanViewModel? diskScanVm = (DiskScanPage as DiskScanPage)?.DataContext as DiskScanViewModel;
        CleanupViewModel? cleanupVm = (CleanupPage as CleanupPage)?.DataContext as CleanupViewModel;
        if (cleanupVm is not null)
        {
            cleanupVm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CleanupViewModel.IsExecuting))
                    OnPropertyChanged(nameof(IsOperationBusy));
            };
        }

        OverviewViewModel = new OverviewViewModel(
            openDiskScanCallback: driveRoot =>
            {
                if (IsOperationBusy) return;
                if (!string.IsNullOrWhiteSpace(driveRoot))
                {
                    diskScanVm?.SelectVolumeByRoot(driveRoot);
                }
                SetActiveMode(diskScan: true);
            },
            openCleanupCallback: () =>
            {
                if (IsOperationBusy) return;
                ShowCleanup();
            },
            openQuarantineCallback: () =>
            {
                if (IsOperationBusy) return;
                SetActiveMode(cleanup: true);
                if (cleanupVm is not null)
                {
                    cleanupVm.OpenQuarantineViewCommand.Execute(null);
                    _ = cleanupVm.OnEnteredCleanupPageAsync();
                }
            },
            openManualModeCallback: () =>
            {
                if (!IsOperationBusy) ShowManualMode();
            },
            openQuickMigrateCallback: () =>
            {
                if (!IsOperationBusy) ShowQuickMigrate();
            },
            openAiAssistantCallback: () =>
            {
                if (!IsOperationBusy) ShowAiAssistant();
            },
            openBackupCenterCallback: () =>
            {
                if (!IsOperationBusy) ShowBackupCenter();
            },
            readQuickMigrateSummary: () => BuildQuickMigrateOverviewSummary(quickMigrateVm));
        OverviewPage = new OverviewPage { DataContext = OverviewViewModel };

        if (quickMigrateVm is not null)
        {
            quickMigrateVm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(QuickMigrateViewModel.IsLoading)
                    or nameof(QuickMigrateViewModel.PendingTaskCount)
                    or nameof(QuickMigrateViewModel.MigratedTaskCount))
                {
                    OverviewViewModel.RefreshQuickMigrateSummary();
                }
            };
        }

        AiAssistantViewModel = new AiAssistantViewModel(
            adoptToPlanCallback: (scanId, nodeId, suggestedTarget) =>
                diskScanVm is null
                    ? (false, "磁盘扫描模块尚未就绪。")
                    : diskScanVm.TryAddAiProposalToPlan(scanId, nodeId, suggestedTarget),
            locateNodeInTreeCallback: (scanId, nodeId) =>
            {
                if (diskScanVm is null || !diskScanVm.TryLocateNodeInTree(scanId, nodeId))
                    return false;
                SetActiveMode(diskScan: true);
                return true;
            },
            openDiskScanCallback: () =>
            {
                if (!IsOperationBusy) SetActiveMode(diskScan: true);
            },
            openAiSettingsCallback: () =>
            {
                if (!IsOperationBusy) SetActiveMode(aiAssistant: true);
            },
            closeDrawerCallback: () =>
            {
                if (diskScanVm is not null) diskScanVm.IsAiDrawerOpen = false;
                if (cleanupVm is not null) cleanupVm.IsAiDrawerOpen = false;
            },
            openCleanupResidualCallback: () =>
            {
                if (IsOperationBusy) return;
                SetActiveMode(cleanup: true);
                if (cleanupVm is not null)
                {
                    cleanupVm.OpenAiResidualViewCommand.Execute(null);
                    _ = cleanupVm.OnEnteredCleanupPageAsync();
                }
            },
            checkCleanupRuleCallback: ruleId =>
                cleanupVm is null
                    ? (false, "垃圾清理模块尚未就绪。")
                    : cleanupVm.TryCheckRuleFromAiAdvice(ruleId),
            locateCleanupRuleCallback: ruleId =>
            {
                if (IsOperationBusy || cleanupVm is null) return false;
                if (!cleanupVm.TryLocateCleanupRule(ruleId)) return false;
                SetActiveMode(cleanup: true);
                return true;
            },
            openCleanupCallback: () =>
            {
                if (IsOperationBusy) return;
                SetActiveMode(cleanup: true);
                if (cleanupVm is not null)
                {
                    _ = cleanupVm.OnEnteredCleanupPageAsync();
                }
            },
            openBackupCenterCallback: () =>
            {
                if (!IsOperationBusy) ShowBackupCenter();
            },
            openQuarantineCallback: () =>
            {
                if (IsOperationBusy) return;
                SetActiveMode(cleanup: true);
                if (cleanupVm is not null)
                {
                    cleanupVm.OpenQuarantineViewCommand.Execute(null);
                    _ = cleanupVm.OnEnteredCleanupPageAsync();
                }
            },
            scanDriveCallback: (driveRoot, progress, ct) =>
                diskScanVm is null
                    ? Task.FromResult<MigrationCore.Models.Scanning.ScanSnapshot?>(null)
                    : diskScanVm.ScanDriveForAiCardAsync(driveRoot, progress, ct),
            scanCleanupCallback: (progress, ct) =>
                cleanupVm is null
                    ? Task.FromResult<MigrationCore.Models.Cleanup.CleanupScanSnapshot?>(null)
                    : cleanupVm.ScanForAiCardAsync(progress, ct),
            cancelDiskScanCallback: () => diskScanVm?.CancelScanCommand.Execute(null),
            cancelCleanupScanCallback: () => cleanupVm?.CancelScanCommand.Execute(null));

        if (diskScanVm is not null)
        {
            diskScanVm.AiAssistant = AiAssistantViewModel;
            diskScanVm.SnapshotChanged += (_, snap) =>
            {
                AiAssistantViewModel.SyncScanSnapshot(snap);
                OverviewViewModel.RefreshScanSnapshotHint();
            };
            if (diskScanVm.CurrentSnapshot is not null)
            {
                AiAssistantViewModel.SyncScanSnapshot(diskScanVm.CurrentSnapshot);
            }
        }

        if (cleanupVm is not null)
        {
            cleanupVm.AiAssistant = AiAssistantViewModel;
        }

        AiAssistantPage = new AiAssistantPage { DataContext = AiAssistantViewModel };

        _ = RestoreLastTransactionAsync();
        if (IsOverviewMode)
        {
            _ = OverviewViewModel.OnEnteredOverviewPageAsync();
        }
        
#if DEBUG
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] MainViewModel initialized");
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Version: {VersionText}");
#endif
    }

    [RelayCommand]
    private void ShowOverview()
    {
        if (IsOperationBusy) return;
        SetActiveMode(overview: true);
        _ = OverviewViewModel.OnEnteredOverviewPageAsync();
    }

    [RelayCommand]
    private void ShowManualMode()
    {
        if (IsOperationBusy) return;
        SetActiveMode(manual: true);
    }

    [RelayCommand]
    private void ShowQuickMigrate()
    {
        if (IsOperationBusy) return;
        SetActiveMode(quick: true);
    }

    [RelayCommand]
    private void ShowDiskScan()
    {
        if (IsOperationBusy) return;
        SetActiveMode(diskScan: true);
    }

    [RelayCommand]
    private void ShowAiAssistant()
    {
        if (IsOperationBusy) return;
        SetActiveMode(aiAssistant: true);
        _ = AiAssistantViewModel.OnEnteredAiPageAsync();
    }

    [RelayCommand]
    private void ShowCleanup()
    {
        if (IsOperationBusy) return;
        SetActiveMode(cleanup: true);
        if (CleanupPage is CleanupPage cp && cp.DataContext is CleanupViewModel cvm)
        {
            _ = cvm.OnEnteredCleanupPageAsync();
        }
    }

    [RelayCommand]
    private void ShowBackupCenter()
    {
        if (IsOperationBusy) return;
        SetActiveMode(backupCenter: true);
        _ = RefreshGlobalStatusAsync();
        if (GlobalBackupPage is GlobalBackupPage bp && bp.DataContext is GlobalBackupViewModel bvm)
            _ = bvm.RefreshAsync();
    }

    [RelayCommand]
    private void OpenRecoveryCenter()
    {
        if (IsOperationBusy) return;
        SetActiveMode(backupCenter: true);
        _ = RefreshGlobalStatusAsync();
        if (GlobalBackupPage is GlobalBackupPage bp && bp.DataContext is GlobalBackupViewModel bvm)
            _ = bvm.RefreshAsync();
    }

    /// <summary>
    /// 从全局备份管理页快捷跳转到手动模式并自动填入源链接路径（触发还原检测）。
    /// </summary>
    public void OpenRestoreInManualMode(string sourcePath)
    {
        if (IsOperationBusy || string.IsNullOrWhiteSpace(sourcePath)) return;
        SetActiveMode(manual: true);
        CurrentStep = 1;
        SourcePath = sourcePath;
    }

    private void SetActiveMode(
        bool overview = false,
        bool manual = false,
        bool quick = false,
        bool diskScan = false,
        bool backupCenter = false,
        bool aiAssistant = false,
        bool cleanup = false)
    {
        IsOverviewMode = overview;
        IsManualMode = manual;
        IsQuickMigrateMode = quick;
        IsDiskScanMode = diskScan;
        IsBackupCenterMode = backupCenter;
        IsAiAssistantMode = aiAssistant;
        IsCleanupMode = cleanup;
        if (diskScan && DiskScanPage is DiskScanPage dp && dp.DataContext is DiskScanViewModel dvm)
        {
            dvm.SyncUnverifiedBackupCount(UnverifiedBackupCount);
        }
    }

    private static OverviewQuickMigrateSummary BuildQuickMigrateOverviewSummary(QuickMigrateViewModel? qvm)
    {
        if (qvm is null || (qvm.IsLoading && !qvm.HasConfig))
        {
            return new OverviewQuickMigrateSummary(IsReady: false, PendingCount: 0, MigratedCount: 0, MatchedGroupNames: []);
        }

        static bool IsTestPresetTask(QuickMigrateTask task)
        {
            if (string.Equals(task.ProfileName, "测试迁移", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string src = task.SourcePath ?? string.Empty;
            return src.StartsWith(@"C:\testMove", StringComparison.OrdinalIgnoreCase);
        }

        var pending = qvm.PendingTaskGroups
            .SelectMany(g => g.Tasks)
            .Where(t => !IsTestPresetTask(t))
            .ToArray();
        var migrated = qvm.MigratedTaskGroups
            .SelectMany(g => g.Tasks)
            .Where(t => !IsTestPresetTask(t))
            .ToArray();

        string[] groupNames = pending
            .Concat(migrated)
            .Select(t => !string.IsNullOrWhiteSpace(t.ProfileName) ? t.ProfileName! : t.DisplayName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new OverviewQuickMigrateSummary(
            IsReady: !qvm.IsLoading,
            PendingCount: pending.Length,
            MigratedCount: migrated.Length,
            MatchedGroupNames: groupNames);
    }

    /// <summary>
    /// 显示使用说明窗口
    /// </summary>
    [RelayCommand]
    private void ShowUserGuide()
    {
        var guideWindow = new Views.UserGuideWindow
        {
            Owner = Application.Current.MainWindow
        };
        guideWindow.ShowDialog();
    }

    private async Task RestoreLastTransactionAsync()
    {
        IsMigrating = true;
        try
        {
            if (QuickMigratePage is QuickMigratePage page && page.DataContext is QuickMigrateViewModel vm) await vm.Initialization;
            var records = await new MigrationTransactionStore().ReadAllAsync();
            UpdateGlobalStatusFromRecords(records);

            // 正常使用时历史/待验证/异常事务统一由顶部「备份管理」中心与全局徽章承接，
            // 重启软件后手动模式保持在「步骤 1. 选择路径」，不再每次强行跳到「步骤 4. 完成」。
            // 仅在自动化安全回归实验室（IsTestLab）下保留旧版直达步骤 4 恢复，兼容既有 UI 自动化用例。
            if (!MigrationRuntime.IsTestLab)
            {
                return;
            }

            var record = records.OrderByDescending(r => r.UpdatedAtUtc).FirstOrDefault();
            if (record is null) return;
            _suppressPathDetection = true;
            try
            {
                SourcePath = record.SourcePath;
                TargetPath = record.TargetPath;
            }
            finally { _suppressPathDetection = false; }
            if (record.Mode == MigrationMode.Restore) SwitchToRestoreMode();
            else if (record.Mode == MigrationMode.Repair) SwitchToRepairMode();
            else SwitchToMigrateMode();
            UseContentHash = record.VerificationMode == VerificationMode.ContentHash;
            await RefreshBackupTaskAsync(record.TransactionId);
            MigrationCompleted = true;
            MigrationSuccess = record.Stage == MigrationStage.AwaitingUserVerification;
            string modeLabel = record.Mode switch { MigrationMode.Restore => "还原", MigrationMode.Repair => "修复", _ => "迁移" };
            ResultMessage = $"已读取持久化{modeLabel}事务；技术完成不代表用户验收。\n\n"
                + $"源：{record.SourcePath}\n目标：{record.TargetPath}\n备份：{record.BackupPath ?? "未创建"}\n"
                + $"staging：{record.StagingPath ?? "未创建"}\n试改名：{record.ProbePath ?? "未创建"}"
                + (string.IsNullOrWhiteSpace(record.LastError) ? "" : $"\n{record.LastError}");
            CurrentStep = 4;
        }
        catch (Exception ex) { AddLog($"读取事务失败，未认领任何目录：{ex.Message}"); }
        finally { IsMigrating = false; }
    }

    private async Task RefreshBackupTaskAsync(Guid transactionId)
    {
        var record = await new MigrationTransactionStore().LoadAsync(transactionId) ?? throw new IOException("结果事务记录缺失。");
        var task = new QuickMigrateTask { SourcePath = record.SourcePath, TargetPath = record.TargetPath,
            Transaction = record, BackupPath = record.BackupPath };
        MigrationStateDetector.DetectMigrationState(task);
        BackupTask = task;
    }

    [RelayCommand]
    private async Task VerifyDataAsync()
    {
        if (IsOperationBusy || BackupTask?.TransactionId is not Guid id) return;
        using var gateLease = OperationExecutionGate.Shared.TryAcquire("ManualBackupVerify", $"手动验证并处理备份：{BackupTask.SourcePath}", [BackupTask.SourcePath, BackupTask.TargetPath]);
        if (gateLease is null) return;
        IsMigrating = true;
        try
        {
            await MigrationVerificationWindow.ShowForTransactionAsync(id);
            await RefreshBackupTaskAsync(id);
            await RefreshGlobalStatusAsync();
            if (QuickMigratePage is QuickMigratePage page && page.DataContext is QuickMigrateViewModel vm)
                vm.RefreshTransactionState();
        }
        catch (Exception ex) { CustomMessageBox.ShowWarning(ex.Message, "无法打开迁移验证"); }
        finally { IsMigrating = false; }
    }

    public async Task RefreshGlobalStatusAsync()
    {
        try
        {
            var records = await new MigrationTransactionStore().ReadAllAsync().ConfigureAwait(false);
            int unverifiedCount = await Task.Run(() => UnverifiedExitConfirmation.SelectPendingUnverified(records).Length).ConfigureAwait(false);
            var recoveryCount = records.DistinctBy(r => r.TransactionId).Count(r => r.Stage == MigrationStage.RecoveryRequired);
            await Application.Current.Dispatcher.InvokeAsync(() => ApplyGlobalStatusCounts(unverifiedCount, recoveryCount));
        }
        catch
        {
            // 事务库不可读时保持现有状态，不静默清空警示
        }
    }

    private void UpdateGlobalStatusFromRecords(IEnumerable<MigrationTransactionRecord> records)
    {
        var distinct = records.DistinctBy(r => r.TransactionId).ToArray();
        int unverified = UnverifiedExitConfirmation.SelectPendingUnverified(distinct).Length;
        int recoveryCount = distinct.Count(r => r.Stage == MigrationStage.RecoveryRequired);
        ApplyGlobalStatusCounts(unverified, recoveryCount);
    }

    private void ApplyGlobalStatusCounts(int unverified, int recoveryCount)
    {
        UnverifiedBackupCount = unverified;
        RecoveryRequiredCount = recoveryCount;
        RecoveryBannerMessage = recoveryCount == 0
            ? string.Empty
            : $"检测到 {recoveryCount} 个上次异常中断的迁移事务（RecoveryRequired）";
        if (DiskScanPage is DiskScanPage dp && dp.DataContext is DiskScanViewModel dvm)
        {
            dvm.SyncUnverifiedBackupCount(unverified);
        }
    }

    public async Task RefreshDisplayedTransactionAsync(Guid transactionId)
    {
        if (BackupTask?.TransactionId == transactionId) await RefreshBackupTaskAsync(transactionId);
        await RefreshGlobalStatusAsync();
    }

    public async Task<bool> ConfirmCloseAsync()
    {
        if (IsOperationBusy) return false;
        IsMigrating = true;
        try
        {
            var records = await new MigrationTransactionStore().ReadAllAsync();
            return UnverifiedExitConfirmation.Confirm(records, "关闭应用");
        }
        catch (Exception ex)
        {
            CustomMessageBox.ShowWarning($"无法核对待验证记录：{ex.Message}\n请先确认备份状态。", "暂不关闭");
            return false;
        }
        finally { IsMigrating = false; }
    }

    public void CancelCurrentOperations()
    {
        if (IsMigrating) CancelMigration();
        if (QuickMigratePage is QuickMigratePage page && page.DataContext is QuickMigrateViewModel vm && vm.IsExecuting)
            vm.CancelExecutionCommand.Execute(null);
        if (DiskScanPage is DiskScanPage dsp && dsp.DataContext is DiskScanViewModel dvm)
        {
            if (dvm.IsBatchExecuting) dvm.CancelBatchCommand.Execute(null);
            if (dvm.IsScanning) dvm.CancelScanCommand.Execute(null);
        }
        if (AiAssistantViewModel.IsAnySessionAnalyzing)
        {
            AiAssistantViewModel.StopAnalysisCommand.Execute(null);
        }
    }

    private void AddLog(string message)
    {
        var formattedMessage = $"[{DateTime.Now:HH:mm:ss}] {message}";

        try
        {
            Console.WriteLine(formattedMessage);
        }
        catch
        {
            // 忽略在无控制台环境下写入失败的情况
        }

        Application.Current.Dispatcher.Invoke(() =>
        {
            LogMessages.Add(formattedMessage);
        });
    }
}

