using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MigrationCore.Models;
using MigrationCore.Services;
using Microsoft.Win32;
using MoveWithSymlinkWPF.Helpers;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using MoveWithSymlinkWPF.Views;

namespace MoveWithSymlinkWPF.ViewModels;

/// <summary>
/// 一键迁移页面 ViewModel
/// </summary>
public partial class QuickMigrateViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isLoading = false;

    [ObservableProperty]
    private bool _hasConfig = false;

    [ObservableProperty]
    private bool _hasValidTasks = false;

    [ObservableProperty]
    private bool _hasPendingTasks = false;

    [ObservableProperty]
    private bool _hasSelectedTasks = false;

    private bool _isAllSelected = false;
    private bool _isUpdatingFromCode = false;
    
    public bool IsAllSelected
    {
        get => _isAllSelected;
        set
        {
            if (_isUpdatingFromCode)
                return;
                
            if (SetProperty(ref _isAllSelected, value))
            {
                // 当全选状态改变时，更新所有任务的选中状态
                _isUpdatingFromCode = true;
                foreach (var group in PendingTaskGroups)
                {
                    foreach (var task in group.Tasks)
                    {
                        task.IsSelected = value;
                    }
                }
                _isUpdatingFromCode = false;
                
                // 更新按钮状态
                SelectedTaskCount = value ? PendingTaskGroups.SelectMany(g => g.Tasks).Count() : 0;
                HasSelectedTasks = SelectedTaskCount > 0;
            }
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionSummaryText))]
    [NotifyPropertyChangedFor(nameof(AllTasksMigrated))]
    private int _pendingTaskCount = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMigratedTasks))]
    [NotifyPropertyChangedFor(nameof(ShowMigratedFilterEmptyState))]
    [NotifyPropertyChangedFor(nameof(MigratedFilterSummaryText))]
    private int _migratedTaskCount = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionSummaryText))]
    private int _selectedTaskCount = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnverifiedPendingBadgeText))]
    private int _unverifiedPendingCount = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnverifiedMigratedBadgeText))]
    private int _unverifiedMigratedCount = 0;

    [ObservableProperty]
    private int _retainedBackupMigratedCount = 0;

    [ObservableProperty]
    private int _abnormalMigratedCount = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMigratedFilterEmptyState))]
    [NotifyPropertyChangedFor(nameof(MigratedFilterSummaryText))]
    private int _filteredMigratedTaskCount = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMigratedFilterAll))]
    [NotifyPropertyChangedFor(nameof(IsMigratedFilterUnverified))]
    [NotifyPropertyChangedFor(nameof(IsMigratedFilterRetainedBackup))]
    [NotifyPropertyChangedFor(nameof(IsMigratedFilterAbnormal))]
    [NotifyPropertyChangedFor(nameof(ShowMigratedFilterEmptyState))]
    [NotifyPropertyChangedFor(nameof(MigratedFilterSummaryText))]
    private MigratedTaskFilter _migratedFilter = MigratedTaskFilter.All;

    partial void OnMigratedFilterChanged(MigratedTaskFilter value)
    {
        RebuildMigratedTaskGroups();
    }

    public bool HasMigratedTasks => MigratedTaskCount > 0;
    public bool ShowMigratedFilterEmptyState => HasMigratedTasks && FilteredMigratedTaskCount == 0;
    public bool IsMigratedFilterAll => MigratedFilter == MigratedTaskFilter.All;
    public bool IsMigratedFilterUnverified => MigratedFilter == MigratedTaskFilter.Unverified;
    public bool IsMigratedFilterRetainedBackup => MigratedFilter == MigratedTaskFilter.RetainedBackup;
    public bool IsMigratedFilterAbnormal => MigratedFilter == MigratedTaskFilter.Abnormal;
    public string MigratedFilterSummaryText => MigratedFilter == MigratedTaskFilter.All
        ? $"共 {MigratedTaskCount} 项已迁移"
        : $"已筛选 {FilteredMigratedTaskCount} / {MigratedTaskCount} 项";

    public string UnverifiedPendingBadgeText => UnverifiedPendingCount > 0 ? $"{UnverifiedPendingCount} 待验证" : string.Empty;
    public string UnverifiedMigratedBadgeText => UnverifiedMigratedCount > 0 ? $"{UnverifiedMigratedCount} 待验证" : string.Empty;
    public string SelectionSummaryText => SelectedTaskCount > 0 ? $"已选 {SelectedTaskCount} / {PendingTaskCount} 项" : $"共 {PendingTaskCount} 项待迁移";
    public bool AllTasksMigrated => HasValidTasks && !HasPendingTasks;

    [ObservableProperty]
    private string _statusMessage = "正在加载配置...";

    [ObservableProperty]
    private bool _useUnifiedTarget = true;

    [ObservableProperty]
    private string _unifiedTargetRoot = string.Empty;

    partial void OnUseUnifiedTargetChanged(bool value)
    {
        if (IsLoading || IsExecuting) return;
        UpdatePendingTasksTargetPaths();
    }

    partial void OnUnifiedTargetRootChanged(string value)
    {
        if (IsLoading || IsExecuting) return;
        if (UseUnifiedTarget)
        {
            UpdatePendingTasksTargetPaths();
        }
    }

    [ObservableProperty]
    private int _selectedTabIndex = 0; // 0 = 未迁移, 1 = 已迁移

    [ObservableProperty]
    private bool _isExecuting = false;

    [ObservableProperty]
    private int _completedCount = 0;

    [ObservableProperty]
    private int _failedCount = 0;

    [ObservableProperty]
    private int _totalCount = 0;

    [ObservableProperty]
    private bool _hasErrors = false;

    [ObservableProperty]
    private bool _isLogExpanded = false;

    public ObservableCollection<QuickMigrateTaskGroup> PendingTaskGroups { get; } = new();
    public ObservableCollection<QuickMigrateTaskGroup> MigratedTaskGroups { get; } = new();
    public ObservableCollection<string> LogMessages { get; } = new();

    // 保留最近一次未入事务（如目标盘不存在、路径准入拒绝）的执行失败信息，避免刷新列表时被洗白
    private readonly Dictionary<string, (string ErrorMessage, ErrorType ErrorType)> _lastTaskErrors = new(StringComparer.OrdinalIgnoreCase);
    // 完整已迁移任务快照，供「已迁移」页签按状态实时筛选而不必重新扫描磁盘
    private List<QuickMigrateTask> _allMigratedTasks = new();

    private QuickMigrateConfig? _config;
    private CancellationTokenSource? _cancellationTokenSource;
    [ObservableProperty]
    private bool _useContentHash;
    public Task Initialization { get; }
    private bool OtherEntryBusy => Application.Current.MainWindow?.DataContext is MainViewModel main && main.IsMigrating;

    public QuickMigrateViewModel()
    {
        Initialization = LoadConfigAsync();
    }

    [RelayCommand]
    private async Task LoadConfigAsync()
    {
        if (IsExecuting || IsLoading) return;
        IsLoading = true;
        _lastTaskErrors.Clear();
        StatusMessage = "正在加载配置...";
        try
        {
            _config = await Task.Run(() => QuickMigrateConfigLoader.LoadConfig());
            HasConfig = _config is not null;
            if (_config is null) { StatusMessage = "未找到配置文件 quick-migrate.json"; return; }
            UseUnifiedTarget = _config.Defaults.TargetStrategy == "unified";
            UnifiedTargetRoot = _config.Defaults.UnifiedTargetRoot;
            ScanAndBuildTasks();
        }
        catch (Exception ex)
        {
            HasConfig = false;
            StatusMessage = $"配置或事务无法安全读取：{ex.Message}";
            AddLog(StatusMessage);
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private void RefreshScan()
    {
        if (IsExecuting || OtherEntryBusy) return;
        _ = LoadConfigAsync();
    }

    [RelayCommand]
    private void ExportExampleConfig()
    {
#if DEBUG
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ExportExampleConfig command triggered");
#endif
        try
        {
            var exampleConfig = QuickMigrateConfigLoader.CreateExampleConfig();
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string configPath = Path.Combine(exeDir, "quick-migrate-example.json");
            
#if DEBUG
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Export path: {configPath}");
#endif
            
            if (QuickMigrateConfigLoader.SaveConfig(exampleConfig, configPath))
            {
#if DEBUG
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Example config exported successfully");
#endif
                CustomMessageBox.ShowInformation(
                    $"示例配置已导出到：\n{configPath}\n\n请根据实际需求修改后，重命名为 quick-migrate.json",
                    "导出成功");
            }
            else
            {
#if DEBUG
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Failed to export example config");
#endif
                CustomMessageBox.ShowError("导出失败", "错误");
            }
        }
        catch (Exception ex)
        {
#if DEBUG
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Exception in ExportExampleConfig: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
#endif
            CustomMessageBox.ShowError($"导出失败: {ex.Message}", "错误");
        }
    }

    private void ScanAndBuildTasks()
    {
        if (_config == null)
            return;


        PendingTaskGroups.Clear();
        MigratedTaskGroups.Clear();

        var allTasks = new List<QuickMigrateTask>();

        // 处理 Profiles
        foreach (var profile in _config.Profiles)
        {
            var installRoot = RegistryLocatorService.LocateInstallRoot(profile.Locator);
            if (installRoot != null)
            {
                var tasks = RegistryLocatorService.ExpandProfileToTasks(profile, installRoot);
                allTasks.AddRange(tasks);
            }
        }

        // 处理独立源
        foreach (var standalone in _config.StandaloneSources)
        {
            var tasks = RegistryLocatorService.ExpandStandaloneSourceToTasks(standalone);
            allTasks.AddRange(tasks);
        }

        // 去重
        allTasks = RegistryLocatorService.DeduplicateTasks(allTasks);

        // 读取已登记事务（供已迁移任务与未收尾还原任务保留真实目标路径，避免被顶部“统一目标目录”覆盖）
        var savedTransactions = TryLoadTransactions();
        var pathIdentity = new PathIdentityService();

        // 设置目标路径：已核实身份的已迁移/待收尾还原任务沿用事务登记目标，未迁移任务使用统一/默认目标
        foreach (var task in allTasks)
        {
            if (TryResolveTrackedTargetPath(task, savedTransactions, pathIdentity) is string trackedTarget)
            {
                task.TargetPath = trackedTarget;
                continue;
            }

            if (UseUnifiedTarget)
            {
                task.TargetPath = string.IsNullOrWhiteSpace(UnifiedTargetRoot)
                    ? string.Empty
                    : Path.Combine(UnifiedTargetRoot.Trim(), ResolveTaskSubPath(task));
            }
            else if (!string.IsNullOrEmpty(_config.Defaults.UnifiedTargetRoot))
            {
                // 非统一模式但配置文件中有默认目标根目录：使用配置文件中的路径作为默认值
                task.TargetPath = Path.Combine(_config.Defaults.UnifiedTargetRoot, ResolveTaskSubPath(task));
            }
            // 如果两者都为空，TargetPath 保持为空，UI 会提示用户设置
        }

        // 检测迁移状态，并恢复错误分类及未入事务的最近一次执行错误
        foreach (var task in allTasks)
        {
            MigrationStateDetector.DetectMigrationState(task);
            if (task.Status == QuickMigrateTaskStatus.Failed)
            {
                if (string.IsNullOrWhiteSpace(task.ErrorMessage) && _lastTaskErrors.TryGetValue(task.SourcePath, out var cached))
                {
                    task.ErrorMessage = cached.ErrorMessage;
                    task.ErrorType = cached.ErrorType;
                }
                else if (!string.IsNullOrWhiteSpace(task.ErrorMessage))
                {
                    task.ErrorType = ClassifyError(task.ErrorMessage);
                }
            }
            else if (_lastTaskErrors.TryGetValue(task.SourcePath, out var cachedError))
            {
                task.Status = QuickMigrateTaskStatus.Failed;
                task.ErrorMessage = cachedError.ErrorMessage;
                task.ErrorType = cachedError.ErrorType;
            }
        }

        // 分组：未迁移 vs 已迁移
        var pendingTasks = allTasks.Where(t => 
            t.MigrationState == MigrationState.Pending || 
            t.MigrationState == MigrationState.NeedsCompletion).ToList();
        
        var migratedTasks = allTasks.Where(t => 
            t.MigrationState == MigrationState.Migrated || 
            t.MigrationState == MigrationState.Inconsistent ||
            t.MigrationState == MigrationState.NeedsCleanup).ToList();

        // 按 ProfileName 分组（未迁移）
        var pendingGroups = pendingTasks
            .GroupBy(t => t.ProfileName ?? "自定义目录")
            .Select(g => new QuickMigrateTaskGroup
            {
                GroupName = g.Key,
                Tasks = new ObservableCollection<QuickMigrateTask>(g)
            });

        foreach (var group in pendingGroups)
        {
            PendingTaskGroups.Add(group);
            // 为每个任务订阅 PropertyChanged 事件
            foreach (var task in group.Tasks)
            {

                task.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(QuickMigrateTask.IsSelected))
                    {
                        UpdateSelectionState();
                    }
                };
            }
        }

        // 保存完整已迁移任务列表，并按当前筛选条件填充展示分组
        _allMigratedTasks = migratedTasks;
        MigratedTaskCount = migratedTasks.Count;
        UnverifiedMigratedCount = migratedTasks.Count(t => t.IsAwaitingUserVerification);
        RetainedBackupMigratedCount = migratedTasks.Count(t => t.HasRetainedBackup);
        AbnormalMigratedCount = migratedTasks.Count(IsAbnormalMigratedTask);
        RebuildMigratedTaskGroups();

        // 更新错误状态（基于全量恢复后的任务状态，不受筛选影响）
        var currentFailedCount = PendingTaskGroups
            .SelectMany(g => g.Tasks)
            .Concat(_allMigratedTasks)
            .Count(t => t.Status == QuickMigrateTaskStatus.Failed);

        HasErrors = currentFailedCount > 0;

        PendingTaskCount = pendingTasks.Count;
        UnverifiedPendingCount = pendingTasks.Count(t => t.IsAwaitingUserVerification);

        HasValidTasks = PendingTaskGroups.Any() || _allMigratedTasks.Any();
        HasPendingTasks = PendingTaskGroups.Any();
        OnPropertyChanged(nameof(AllTasksMigrated));

        if (!HasValidTasks)
        {
            StatusMessage = "无可一键迁移目录";
        }
        else
        {
            int totalUnverified = UnverifiedPendingCount + UnverifiedMigratedCount;
            StatusMessage = totalUnverified > 0
                ? $"共 {pendingTasks.Count} 个未迁移，{migratedTasks.Count} 个已迁移（{totalUnverified} 项待验证）"
                : $"共 {pendingTasks.Count} 个未迁移，{migratedTasks.Count} 个已迁移";
        }

        // 初始化选择状态
        UpdateSelectionState();
    }

    [RelayCommand]
    private void SetMigratedFilter(MigratedTaskFilter filter)
    {
        MigratedFilter = filter;
    }

    private void RebuildMigratedTaskGroups()
    {
        MigratedTaskGroups.Clear();

        IEnumerable<QuickMigrateTask> filtered = MigratedFilter switch
        {
            MigratedTaskFilter.Unverified => _allMigratedTasks.Where(t => t.IsAwaitingUserVerification),
            MigratedTaskFilter.RetainedBackup => _allMigratedTasks.Where(t => t.HasRetainedBackup),
            MigratedTaskFilter.Abnormal => _allMigratedTasks.Where(IsAbnormalMigratedTask),
            _ => _allMigratedTasks
        };

        var filteredList = filtered.ToList();
        FilteredMigratedTaskCount = filteredList.Count;

        foreach (var group in filteredList
            .GroupBy(t => t.ProfileName ?? "自定义目录")
            .Select(g => new QuickMigrateTaskGroup
            {
                GroupName = g.Key,
                Tasks = new ObservableCollection<QuickMigrateTask>(g)
            }))
        {
            MigratedTaskGroups.Add(group);
        }
    }

    private static bool IsAbnormalMigratedTask(QuickMigrateTask task) =>
        task.IsTransactionAbnormal
        || task.MigrationState == MigrationState.Inconsistent
        || task.Status == QuickMigrateTaskStatus.Failed;

    [RelayCommand]
    private void BrowseUnifiedTarget()
    {
        if (IsExecuting || OtherEntryBusy) return;
        // 使用自定义文件夹选择器
        var picker = new Views.FolderPickerWindow
        {
            Owner = Application.Current.MainWindow
        };

        if (picker.ShowDialog() == true)
        {
            string? selectedPath = picker.SelectedPath;
            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                // OnUnifiedTargetRootChanged 会自动同步更新所有未迁移任务的目标路径
                UnifiedTargetRoot = selectedPath;
                UpdatePendingTasksTargetPaths();
            }
        }
    }

    private void UpdatePendingTasksTargetPaths()
    {
        string? root = UseUnifiedTarget
            ? UnifiedTargetRoot.Trim()
            : _config?.Defaults.UnifiedTargetRoot;

        foreach (var group in PendingTaskGroups)
        {
            foreach (var task in group.Tasks)
            {
                task.TargetPath = string.IsNullOrWhiteSpace(root)
                    ? string.Empty
                    : Path.Combine(root, ResolveTaskSubPath(task));
            }
        }
    }

    private static string ResolveTaskSubPath(QuickMigrateTask task) =>
        string.IsNullOrEmpty(task.RelativePath)
            ? Path.GetFileName(task.SourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : task.RelativePath;

    private static IReadOnlyList<MigrationTransactionRecord> TryLoadTransactions()
    {
        try
        {
            return new MigrationTransactionStore().ReadAllAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // 事务读取异常时交由 MigrationStateDetector 逐项按严格安全策略判定为 Inconsistent
            return Array.Empty<MigrationTransactionRecord>();
        }
    }

    /// <summary>
    /// 仅在底层卷/文件身份完全匹配已登记事务时，保留该任务的真实目标路径，防止被顶部“统一目标目录”误覆盖。
    /// 不按目录名认领，任何身份不符都交回 MigrationStateDetector 严格判定。
    /// </summary>
    private static string? TryResolveTrackedTargetPath(
        QuickMigrateTask task,
        IReadOnlyList<MigrationTransactionRecord> records,
        PathIdentityService identity)
    {
        if (records.Count == 0 || string.IsNullOrWhiteSpace(task.SourcePath))
            return null;

        try
        {
            var latest = records
                .Where(r => r.SourcePath.Equals(task.SourcePath, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.UpdatedAtUtc)
                .FirstOrDefault();
            if (latest is null)
                return null;

            // 1. 已迁移链接：核实源链接身份、目标实目录身份、以及链接真实解析目标全部一致
            if (latest.Mode != MigrationMode.Restore
                && latest.LinkIdentity is not null
                && latest.TargetIdentity is not null)
            {
                var liveLink = identity.ReadIfExists(task.SourcePath);
                var liveTarget = identity.ReadIfExists(latest.TargetPath);
                if (liveLink is { IsReparsePoint: true }
                    && liveLink == latest.LinkIdentity
                    && liveTarget == latest.TargetIdentity
                    && identity.ReadDirectory(task.SourcePath, followLink: true) == latest.TargetIdentity)
                {
                    return latest.TargetPath;
                }
            }

            // 2. 刚还原但待用户验证或还原备份仍保留的目录：保留原事务目标以维持验证与备份管理入口
            if (latest.Mode == MigrationMode.Restore
                && latest.Stage == MigrationStage.AwaitingUserVerification
                && latest.StagingIdentity is not null)
            {
                bool needsVerificationOrBackup = latest.UserVerifiedAtUtc is null
                    || latest.BackupDeletionInProgress
                    || (latest.BackupPath is not null
                        && latest.BackupState is BackupState.RetainedUnverified or BackupState.UserVerified
                            or BackupState.DeleteRequested or BackupState.DeleteFailed);
                if (needsVerificationOrBackup)
                {
                    var liveSource = identity.ReadIfExists(task.SourcePath);
                    if (liveSource is { IsDirectory: true, IsReparsePoint: false }
                        && liveSource == latest.StagingIdentity)
                    {
                        return latest.TargetPath;
                    }
                }
            }
        }
        catch
        {
            // 任何探测异常都不认领目标，交回 MigrationStateDetector 处理
        }

        return null;
    }

    private void UpdateSelectionState()
    {
        if (_isUpdatingFromCode)
            return;
            
        var allTasks = PendingTaskGroups.SelectMany(g => g.Tasks).ToList();
        var totalCount = allTasks.Count;
        var selectedCount = allTasks.Count(t => t.IsSelected);

        SelectedTaskCount = selectedCount;
        HasSelectedTasks = selectedCount > 0;

        // 更新全选状态以反映子项的选择状态
        bool shouldBeChecked = totalCount > 0 && selectedCount == totalCount;
        if (_isAllSelected != shouldBeChecked)
        {
            _isUpdatingFromCode = true;
            _isAllSelected = shouldBeChecked;
            OnPropertyChanged(nameof(IsAllSelected));
            _isUpdatingFromCode = false;
        }
    }

    [RelayCommand]
    private void BrowseTaskTarget(QuickMigrateTask task)
    {
        if (IsExecuting || OtherEntryBusy) return;
        // 使用自定义文件夹选择器
        var picker = new Views.FolderPickerWindow
        {
            Owner = Application.Current.MainWindow
        };

        if (picker.ShowDialog() == true)
        {
            string? selectedPath = picker.SelectedPath;
            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                task.TargetPath = selectedPath;
            }
        }
    }

    [RelayCommand]
    private async Task StartQuickMigrationAsync()
    {
        if (!HasValidTasks || IsExecuting || OtherEntryBusy)
            return;

        // 收集所有已勾选的未迁移任务
        var tasks = PendingTaskGroups.SelectMany(g => g.Tasks).Where(t => t.IsSelected).ToList();

        if (!tasks.Any())
        {
            AddLog("没有可执行的迁移任务（请先勾选要迁移的任务）");
            return;
        }

        // 验证目标路径
        if (UseUnifiedTarget && string.IsNullOrWhiteSpace(UnifiedTargetRoot))
        {
            CustomMessageBox.ShowWarning("请先选择统一目标根目录", "提示");
            return;
        }

        IsExecuting = true;
        CompletedCount = 0;
        FailedCount = 0;
        TotalCount = tasks.Count;
        LogMessages.Clear();

        _cancellationTokenSource = new CancellationTokenSource();

        AddLog($"开始一键迁移，共 {TotalCount} 个任务");

        // 预扫描所有任务并检查磁盘空间
        AddLog("正在扫描所有任务并检查磁盘空间...");

        // 按目标磁盘分组统计所需空间
        var diskSpaceRequirements = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var task in tasks)
        {
            if (_cancellationTokenSource.Token.IsCancellationRequested) break;
            try
            {
                // 在后台线程统计源目录大小，避免大目录阻塞 UI 线程
                long sourceSize = await Task.Run(() => FileStatsService.GetDirectorySize(task.SourcePath),
                    _cancellationTokenSource.Token);
                
                // 获取目标磁盘根路径
                string? targetRoot = Path.GetPathRoot(task.TargetPath);
                if (string.IsNullOrEmpty(targetRoot))
                {
                    AddLog($"⚠️ [{task.DisplayName}] 无法确定目标磁盘，跳过空间检查");
                    continue;
                }
                
                // 累加该磁盘所需空间
                if (diskSpaceRequirements.ContainsKey(targetRoot))
                {
                    diskSpaceRequirements[targetRoot] += sourceSize;
                }
                else
                {
                    diskSpaceRequirements[targetRoot] = sourceSize;
                }
                
                AddLog($"  [{task.DisplayName}] 大小: {FileStatsService.FormatBytes(sourceSize)}");
            }
            catch (Exception ex)
            {
                AddLog($"⚠️ [{task.DisplayName}] 扫描失败: {ex.Message}");
                var result = CustomMessageBox.ShowQuestion(
                    $"任务 \"{task.DisplayName}\" 扫描失败:\n{ex.Message}\n\n是否继续？",
                    "扫描警告");

                if (!result)
                {
                    IsExecuting = false;
                    _cancellationTokenSource?.Dispose();
                    _cancellationTokenSource = null;
                    return;
                }
            }
        }

        // 检查每个目标磁盘的可用性与剩余空间
        bool spaceCheckFailed = false;
        var unavailableDrives = new List<string>();
        foreach (var kvp in diskSpaceRequirements)
        {
            string diskRoot = kvp.Key;
            long requiredBytes = kvp.Value;

            bool driveReady = false;
            try
            {
                driveReady = new DriveInfo(diskRoot).IsReady;
            }
            catch
            {
                driveReady = false;
            }

            if (!driveReady)
            {
                AddLog($"❌ 目标驱动器 {diskRoot} 不存在或不可用！");
                unavailableDrives.Add(diskRoot);
                continue;
            }
            
            var (sufficient, available, required) = PathValidator.CheckDiskSpace(diskRoot, requiredBytes);
            
            AddLog($"磁盘 {diskRoot}:");
            AddLog($"  可用空间: {FileStatsService.FormatBytes(available)}");
            AddLog($"  所需空间(含10%余量): {FileStatsService.FormatBytes(required)}");
            
            if (!sufficient)
            {
                AddLog($"❌ 磁盘 {diskRoot} 空间不足！");
                spaceCheckFailed = true;
            }
            else
            {
                AddLog($"✅ 磁盘 {diskRoot} 空间充足");
            }
        }

        if (unavailableDrives.Count > 0)
        {
            IsLogExpanded = true;
            IsExecuting = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            CustomMessageBox.ShowError(
                $"目标驱动器不可用：{string.Join("、", unavailableDrives)}\n\n请检查磁盘连接或修改目标路径后再试。",
                "目标驱动器不可用");
            return;
        }

        if (spaceCheckFailed)
        {
            IsLogExpanded = true;
            IsExecuting = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            CustomMessageBox.ShowError(
                "目标磁盘空间不足，无法继续迁移！\n\n请查看日志了解详细信息。",
                "空间不足");
            return;
        }

        AddLog("磁盘空间检查通过，开始迁移任务...");

        // 直接开始迁移，每个任务会在执行时自动进行占用检测（ReversibleMigrationService内部）
        foreach (var task in tasks)
        {
            if (_cancellationTokenSource.Token.IsCancellationRequested)
            {
                AddLog("用户取消操作");
                // 标记未执行的任务为跳过
                foreach (var remainingTask in tasks.Where(t => t.Status == QuickMigrateTaskStatus.Pending))
                {
                    remainingTask.Status = QuickMigrateTaskStatus.Skipped;
                    remainingTask.StatusMessage = "已取消";
                    remainingTask.ShowProgress = false;
                }
                break;
            }

            await ExecuteSingleMigrationTaskAsync(task);
        }

        bool wasCancelled = _cancellationTokenSource.Token.IsCancellationRequested;
        if (wasCancelled)
        {
            AddLog($"操作已取消。成功: {CompletedCount}, 失败: {FailedCount}, 跳过: {tasks.Count - CompletedCount - FailedCount}");
        }
        else
        {
            AddLog($"一键迁移完成！成功: {CompletedCount}, 失败: {FailedCount}");
        }

        IsExecuting = false;
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;

        // 刷新任务列表（_lastTaskErrors 会保留未入事务的失败状态供卡片展示）
        ScanAndBuildTasks();

        if (!wasCancelled && FailedCount > 0)
        {
            IsLogExpanded = true;
            string title = CompletedCount > 0 ? "迁移部分失败" : "迁移失败";
            CustomMessageBox.ShowError(
                $"一键迁移结束：成功 {CompletedCount} 项，失败 {FailedCount} 项。\n\n请查看失败任务卡片或下方执行日志了解详情。",
                title);
        }
    }

    [RelayCommand]
    private async Task MigrateSingleTaskAsync(QuickMigrateTask task)
    {
        if (IsExecuting || OtherEntryBusy)
            return;

        // 验证目标路径
        if (UseUnifiedTarget && string.IsNullOrWhiteSpace(UnifiedTargetRoot))
        {
            CustomMessageBox.ShowWarning("请先选择统一目标根目录", "提示");
            return;
        }

        if (string.IsNullOrWhiteSpace(task.TargetPath))
        {
            CustomMessageBox.ShowWarning(
                "目标路径未设置。\n\n请点击\"浏览...\"按钮选择目标目录，或在配置文件中指定 unifiedTargetRoot。",
                "目标路径无效");
            return;
        }

        var result = CustomMessageBox.ShowQuestion(
            $"确定要迁移以下任务吗？\n\n源: {task.SourcePath}\n目标: {task.TargetPath}\n\n迁移后，源位置将创建符号链接指向目标位置。",
            "确认迁移");

        if (!result)
            return;

        IsExecuting = true;
        CompletedCount = 0;
        FailedCount = 0;
        TotalCount = 1;
        LogMessages.Clear();
        AddLog($"开始迁移: {task.DisplayName}");

        _cancellationTokenSource = new CancellationTokenSource();
        string? failureDetail = null;
        bool wasCancelled = false;

        try
        {
            await ExecuteSingleMigrationTaskAsync(task);
            wasCancelled = task.Status == QuickMigrateTaskStatus.Skipped;
            if (task.Status == QuickMigrateTaskStatus.Completed)
            {
                AddLog("迁移操作完成");
            }
            else if (!wasCancelled)
            {
                failureDetail = task.ErrorMessage;
                AddLog("迁移操作失败");
            }
        }
        finally
        {
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;
            IsExecuting = false;
            ScanAndBuildTasks();
        }

        if (!wasCancelled && failureDetail is not null)
        {
            IsLogExpanded = true;
            CustomMessageBox.ShowError(
                $"迁移任务 \"{task.DisplayName}\" 失败：\n\n{failureDetail}\n\n请查看下方执行日志了解详细信息。",
                "迁移失败");
        }
    }

    [RelayCommand]
    private async Task RestoreTaskAsync(QuickMigrateTask task)
    {
        if (IsExecuting || OtherEntryBusy)
            return;

        var result = CustomMessageBox.ShowQuestion(
            $"确定要还原以下任务吗？\n\n源: {task.SourcePath}\n目标: {task.TargetPath}\n\n还原后，数据将从目标位置复制回源位置，符号链接将被删除。",
            "确认还原");

        if (!result)
            return;

        IsExecuting = true;
        CompletedCount = 0;
        FailedCount = 0;
        TotalCount = 1;
        LogMessages.Clear();
        AddLog($"开始还原: {task.DisplayName}");
        _cancellationTokenSource = new CancellationTokenSource();
        string? failureDetail = null;
        bool succeeded = false;
        bool wasCancelled = false;
        try
        {
            await ExecuteSingleRestoreTaskAsync(task);
            succeeded = task.Status == QuickMigrateTaskStatus.Completed;
            wasCancelled = task.Status == QuickMigrateTaskStatus.Skipped;
            if (!succeeded && !wasCancelled)
            {
                failureDetail = task.ErrorMessage;
            }
        }
        finally
        {
            _cancellationTokenSource.Dispose(); _cancellationTokenSource = null;
            IsExecuting = false;
            ScanAndBuildTasks();
        }

        if (succeeded)
        {
            CustomMessageBox.ShowSuccess(
                $"任务 \"{task.DisplayName}\" 已完成还原！\n\n原目标目录已转为还原备份保留，可在「未迁移」页签完成使用验证。",
                "还原完成");
        }
        else if (!wasCancelled && failureDetail is not null)
        {
            IsLogExpanded = true;
            CustomMessageBox.ShowError(
                $"还原任务 \"{task.DisplayName}\" 失败：\n\n{failureDetail}\n\n请查看下方执行日志了解详细信息。",
                "还原失败");
        }
    }

    [RelayCommand]
    private async Task VerifyTaskDataAsync(QuickMigrateTask task)
    {
        if (IsExecuting || OtherEntryBusy || task.TransactionId is not Guid id) return;
        IsExecuting = true;
        try
        {
            await MigrationVerificationWindow.ShowForTransactionAsync(id);
            if (Application.Current.MainWindow?.DataContext is MainViewModel main)
                await main.RefreshDisplayedTransactionAsync(id);
        }
        catch (Exception ex) { CustomMessageBox.ShowWarning(ex.Message, "无法打开迁移验证"); }
        finally
        {
            IsExecuting = false;
            ScanAndBuildTasks();
        }
    }

    [RelayCommand]
    private void ViewDeletionDetails(QuickMigrateTask task)
    {
        if (task.Transaction?.LastDeletionResult is not { } result)
            return;
        BackupDeletionDetailsWindow.ShowDetails(task.DisplayName, task.BackupPath, result);
    }

    public void RefreshTransactionState() => ScanAndBuildTasks();

    [RelayCommand]
    private void CancelExecution()
    {
        _cancellationTokenSource?.Cancel();
        AddLog("正在取消...");
    }

    [RelayCommand]
    private async Task CopyLogsAsync()
    {
        try
        {
            if (LogMessages.Count == 0)
            {
                CustomMessageBox.ShowInformation("暂无日志可复制", "提示");
                return;
            }

            string allLogs = string.Join(Environment.NewLine, LogMessages);

            if (await TrySetClipboardAsync(allLogs))
            {
                AddLog("✅ 日志已复制到剪贴板");
            }
            else
            {
                AddLog("❌ 复制失败：剪贴板被占用");
            }
        }
        catch (Exception ex)
        {
            AddLog($"❌ 复制日志失败: {ex.Message}");
        }
    }

    private async Task ExecuteSingleMigrationTaskAsync(QuickMigrateTask task)
    {
        _lastTaskErrors.Remove(task.SourcePath);
        task.Status = QuickMigrateTaskStatus.InProgress;
        task.ShowProgress = true;
        AddLog($"[{task.DisplayName}] 开始迁移");

        try
        {
            var config = new MigrationConfig
            {
                SourcePath = task.SourcePath,
                TargetPath = task.TargetPath,
                LargeFileThresholdMB = _config?.Defaults.LargeFileThresholdMB ?? 1024,
                RobocopyThreads = _config?.Defaults.RobocopyThreads ?? 8,
                SampleMilliseconds = 2000,
                VerificationMode = UseContentHash ? VerificationMode.ContentHash : VerificationMode.Basic,
            };

            var service = new ReversibleMigrationService(config, MigrationMode.Migrate);

            var progress = new Progress<MigrationProgress>(p =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    task.CurrentPhase = p.CurrentPhase;
                    task.PhaseDescription = p.PhaseDescription;
                    task.ProgressPercent = p.PercentComplete;
                    task.StatusMessage = p.Message;
                });
            });

            var logProgress = new Progress<string>(msg =>
            {
                AddLog($"  {msg}");
            });

            var result = await service.ExecuteAsync(progress, logProgress, _cancellationTokenSource?.Token ?? default);
            await ApplyTaskResultAsync(task, result);
            if (result.Stage == MigrationStage.Cancelled)
            {
                task.Status = QuickMigrateTaskStatus.Skipped; task.StatusMessage = "已取消，副本保留"; task.ShowProgress = false;
                return;
            }

            if (result.Success)
            {
                task.Status = QuickMigrateTaskStatus.Completed;
                task.MigrationState = MigrationState.Migrated;
                task.MigratedAt = DateTime.Now;
                task.ShowProgress = false;
                CompletedCount++;
                AddLog($"[{task.DisplayName}] ✅ 迁移成功");
            }
            else
            {
                string errorText = FormatFailureMessage(result.ErrorMessage, result.RecoveryPaths);
                task.Status = QuickMigrateTaskStatus.Failed;
                task.ErrorMessage = errorText;
                task.ShowProgress = false;
                task.ErrorType = ClassifyError(result.ErrorMessage);
                _lastTaskErrors[task.SourcePath] = (errorText, task.ErrorType);
                FailedCount++;
                HasErrors = true;
                AddLog($"[{task.DisplayName}] ❌ 迁移失败: {result.ErrorMessage}");
            }
        }
        catch (OperationCanceledException)
        {
            task.Status = QuickMigrateTaskStatus.Skipped;
            task.StatusMessage = "用户取消";
            task.ShowProgress = false;
            AddLog($"[{task.DisplayName}] ⚠️ 已取消");
        }
        catch (Exception ex)
        {
            task.Status = QuickMigrateTaskStatus.Failed;
            task.ErrorMessage = ex.Message;
            task.ErrorType = ClassifyError(ex.Message);
            task.ShowProgress = false;
            _lastTaskErrors[task.SourcePath] = (ex.Message, task.ErrorType);
            FailedCount++;
            HasErrors = true;
            AddLog($"[{task.DisplayName}] ❌ 异常: {ex.Message}");
        }
    }

    private async Task ExecuteSingleRestoreTaskAsync(QuickMigrateTask task)
    {
        _lastTaskErrors.Remove(task.SourcePath);
        task.Status = QuickMigrateTaskStatus.InProgress;
        task.ShowProgress = true;
        AddLog($"[{task.DisplayName}] 开始还原");

        try
        {
            var config = new MigrationConfig
            {
                SourcePath = task.SourcePath,
                TargetPath = task.TargetPath,
                LargeFileThresholdMB = _config?.Defaults.LargeFileThresholdMB ?? 1024,
                RobocopyThreads = _config?.Defaults.RobocopyThreads ?? 8,
                SampleMilliseconds = 2000,
                VerificationMode = UseContentHash ? VerificationMode.ContentHash : VerificationMode.Basic,
            };

            var service = new ReversibleMigrationService(config, MigrationMode.Restore);

            var progress = new Progress<MigrationProgress>(p =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    task.CurrentPhase = p.CurrentPhase;
                    task.PhaseDescription = p.PhaseDescription;
                    task.ProgressPercent = p.PercentComplete;
                    task.StatusMessage = p.Message;
                });
            });

            var logProgress = new Progress<string>(msg =>
            {
                AddLog($"  {msg}");
            });

            var result = await service.ExecuteAsync(progress, logProgress, _cancellationTokenSource?.Token ?? default);
            await ApplyTaskResultAsync(task, result);
            if (result.Stage == MigrationStage.Cancelled)
            {
                task.Status = QuickMigrateTaskStatus.Skipped; task.StatusMessage = "已取消，副本保留"; task.ShowProgress = false;
                return;
            }

            if (result.Success)
            {
                task.Status = QuickMigrateTaskStatus.Completed;
                task.MigrationState = MigrationState.Pending;
                task.MigratedAt = null;
                task.ShowProgress = false;
                CompletedCount++;
                AddLog($"[{task.DisplayName}] ✅ 还原成功");
            }
            else
            {
                string errorText = FormatFailureMessage(result.ErrorMessage, result.RecoveryPaths);
                task.Status = QuickMigrateTaskStatus.Failed;
                task.ErrorMessage = errorText;
                task.ShowProgress = false;
                task.ErrorType = ClassifyError(result.ErrorMessage);
                _lastTaskErrors[task.SourcePath] = (errorText, task.ErrorType);
                FailedCount++;
                HasErrors = true;
                AddLog($"[{task.DisplayName}] ❌ 还原失败: {result.ErrorMessage}");
            }
        }
        catch (OperationCanceledException)
        {
            task.Status = QuickMigrateTaskStatus.Skipped;
            task.StatusMessage = "用户取消";
            task.ShowProgress = false;
            AddLog($"[{task.DisplayName}] ⚠️ 已取消");
        }
        catch (Exception ex)
        {
            task.Status = QuickMigrateTaskStatus.Failed;
            task.ErrorMessage = ex.Message;
            task.ErrorType = ClassifyError(ex.Message);
            task.ShowProgress = false;
            _lastTaskErrors[task.SourcePath] = (ex.Message, task.ErrorType);
            FailedCount++;
            HasErrors = true;
            AddLog($"[{task.DisplayName}] ❌ 异常: {ex.Message}");
        }
    }

    private static string FormatFailureMessage(string? errorMessage, string[]? recoveryPaths)
    {
        string primary = string.IsNullOrWhiteSpace(errorMessage) ? "操作未完成" : errorMessage.Trim();
        var paths = recoveryPaths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        if (paths is null || paths.Length == 0)
            return primary;
        return primary + "\n相关/保留位置：\n" + string.Join("\n", paths);
    }

    private static async Task ApplyTaskResultAsync(QuickMigrateTask task, MigrationResult result)
    {
        if (result.TransactionId is Guid id)
        {
            task.Transaction = await new MigrationTransactionStore().LoadAsync(id);
            task.BackupPath = task.Transaction?.BackupPath;
        }
        task.StatusMessage = task.TransactionStatusText;
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

    /// <summary>
    /// 分类错误类型（委托至统一分类器 ErrorTypeClassifier）
    /// </summary>
    private static ErrorType ClassifyError(string? errorMessage) => ErrorTypeClassifier.Classify(errorMessage);

    /// <summary>
    /// 复制错误信息到剪贴板（public 方法，因为源生成器未生成 Command 属性）
    /// </summary>
    [RelayCommand]
    private async Task CopyErrorInfoAsync(QuickMigrateTask task)
    {
        try
        {
            if (task.Status != QuickMigrateTaskStatus.Failed || string.IsNullOrEmpty(task.ErrorMessage))
                return;

            string errorInfo = $"任务: {task.DisplayName}\n" +
                              $"源路径: {task.SourcePath}\n" +
                              $"目标路径: {task.TargetPath}\n" +
                              $"错误类型: {task.ErrorType}\n" +
                              $"错误消息: {task.ErrorMessage}\n" +
                              $"发生时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";

            if (await TrySetClipboardAsync(errorInfo))
            {
                AddLog("✅ 错误信息已复制到剪贴板");
            }
            else
            {
                AddLog("❌ 复制失败：剪贴板被占用");
            }
        }
        catch (Exception ex)
        {
            AddLog($"❌ 复制错误信息失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ViewTaskLogAsync(QuickMigrateTask task)
    {
        try
        {
            // 查找与该任务相关的日志条目
            var taskLogs = LogMessages.Where(msg => msg.Contains(task.DisplayName)).ToList();

            if (taskLogs.Any())
            {
                string allTaskLogs = string.Join(Environment.NewLine, taskLogs);

                if (await TrySetClipboardAsync(allTaskLogs))
                {
                    AddLog($"✅ 任务 [{task.DisplayName}] 的日志已复制到剪贴板");
                }
                else
                {
                    AddLog("❌ 复制失败：剪贴板被占用");
                }
            }
            else
            {
                AddLog($"⚠️ 未找到任务 [{task.DisplayName}] 的相关日志");
            }
        }
        catch (Exception ex)
        {
            AddLog($"❌ 查看任务日志失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 在 WPF STA 线程设置剪贴板内容（含短暂重试避免剪贴板瞬时占用）
    /// </summary>
    private static async Task<bool> TrySetClipboardAsync(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            bool copied = await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    Clipboard.SetDataObject(text, true);
                    return true;
                }
                catch
                {
                    try
                    {
                        Clipboard.SetText(text);
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                }
            });
            if (copied) return true;
            await Task.Delay(50);
        }
        return false;
    }
}

/// <summary>
/// 「已迁移」页签筛选条件
/// </summary>
public enum MigratedTaskFilter
{
    All,
    Unverified,
    RetainedBackup,
    Abnormal
}

/// <summary>
/// 任务分组
/// </summary>
public class QuickMigrateTaskGroup
{
    public string GroupName { get; set; } = string.Empty;
    public ObservableCollection<QuickMigrateTask> Tasks { get; set; } = new();
}
