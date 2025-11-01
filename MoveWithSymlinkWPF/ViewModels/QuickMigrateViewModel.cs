using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MigrationCore.Models;
using MigrationCore.Services;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

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
                HasSelectedTasks = value && PendingTaskGroups.SelectMany(g => g.Tasks).Any();
            }
        }
    }

    [ObservableProperty]
    private string _statusMessage = "正在加载配置...";

    [ObservableProperty]
    private bool _useUnifiedTarget = true;

    [ObservableProperty]
    private string _unifiedTargetRoot = string.Empty;

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

    public ObservableCollection<QuickMigrateTaskGroup> PendingTaskGroups { get; } = new();
    public ObservableCollection<QuickMigrateTaskGroup> MigratedTaskGroups { get; } = new();
    public ObservableCollection<string> LogMessages { get; } = new();

    private QuickMigrateConfig? _config;
    private CancellationTokenSource? _cancellationTokenSource;

    public QuickMigrateViewModel()
    {
        _ = LoadConfigAsync();
    }

    [RelayCommand]
    private async Task LoadConfigAsync()
    {
        IsLoading = true;
        StatusMessage = "正在加载配置...";
        
#if DEBUG
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] QuickMigrateViewModel: LoadConfigAsync started");
#endif

        await Task.Run(() =>
        {
            _config = QuickMigrateConfigLoader.LoadConfig();
#if DEBUG
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Config loaded: {(_config != null ? "Success" : "Failed")}");
#endif

            Application.Current.Dispatcher.Invoke(() =>
            {
                if (_config == null)
                {
                    HasConfig = false;
                    StatusMessage = "未找到配置文件 quick-migrate.json";
                    IsLoading = false;
#if DEBUG
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Config file not found");
#endif
                    return;
                }

                HasConfig = true;
                UseUnifiedTarget = _config.Defaults.TargetStrategy == "unified";
                UnifiedTargetRoot = _config.Defaults.UnifiedTargetRoot;

#if DEBUG
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Config loaded successfully, scanning tasks...");
#endif
                // 扫描并构建任务列表
                ScanAndBuildTasks();

                IsLoading = false;
            });
        });
    }

    [RelayCommand]
    private void RefreshScan()
    {
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
                MessageBox.Show(
                    $"示例配置已导出到：\n{configPath}\n\n请根据实际需求修改后，重命名为 quick-migrate.json",
                    "导出成功",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
#if DEBUG
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Failed to export example config");
#endif
                MessageBox.Show("导出失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
#if DEBUG
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Exception in ExportExampleConfig: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
#endif
            MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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

        // 设置目标路径
        foreach (var task in allTasks)
        {
            if (UseUnifiedTarget && !string.IsNullOrEmpty(UnifiedTargetRoot))
            {
                // 统一目标模式：使用用户选择的统一目标根目录
                string sourceName = string.IsNullOrEmpty(task.RelativePath) 
                    ? Path.GetFileName(task.SourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) 
                    : task.RelativePath;
                task.TargetPath = Path.Combine(UnifiedTargetRoot, sourceName);
            }
            else if (!string.IsNullOrEmpty(_config.Defaults.UnifiedTargetRoot))
            {
                // 非统一模式但配置文件中有默认目标根目录：使用配置文件中的路径作为默认值
                string sourceName = string.IsNullOrEmpty(task.RelativePath) 
                    ? Path.GetFileName(task.SourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) 
                    : task.RelativePath;
                task.TargetPath = Path.Combine(_config.Defaults.UnifiedTargetRoot, sourceName);
            }
            // 如果两者都为空，TargetPath 保持为空，UI 会提示用户设置
        }

        // 检测迁移状态
        foreach (var task in allTasks)
        {
            MigrationStateDetector.DetectMigrationState(task);
        }

        // 分组：未迁移 vs 已迁移
        var pendingTasks = allTasks.Where(t => 
            t.MigrationState == MigrationState.Pending || 
            t.MigrationState == MigrationState.NeedsCompletion).ToList();
        
        var migratedTasks = allTasks.Where(t => 
            t.MigrationState == MigrationState.Migrated || 
            t.MigrationState == MigrationState.Inconsistent ||
            t.MigrationState == MigrationState.NeedsCleanup).ToList();

        // 按 ProfileName 分组
        var pendingGroups = pendingTasks
            .GroupBy(t => t.ProfileName ?? "自定义目录")
            .Select(g => new QuickMigrateTaskGroup
            {
                GroupName = g.Key,
                Tasks = new ObservableCollection<QuickMigrateTask>(g)
            });

        var migratedGroups = migratedTasks
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

        foreach (var group in migratedGroups)
        {
            MigratedTaskGroups.Add(group);
        }

        HasValidTasks = PendingTaskGroups.Any() || MigratedTaskGroups.Any();
        HasPendingTasks = PendingTaskGroups.Any();

        if (!HasValidTasks)
        {
            StatusMessage = "无可一键迁移目录";
        }
        else
        {
            StatusMessage = $"找到 {pendingTasks.Count} 个未迁移任务，{migratedTasks.Count} 个已迁移任务";
        }

        // 初始化选择状态
        UpdateSelectionState();
    }

    [RelayCommand]
    private void BrowseUnifiedTarget()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择统一目标根目录"
        };

        if (dialog.ShowDialog() == true)
        {
            UnifiedTargetRoot = dialog.FolderName;
            
            // 更新所有未迁移任务的目标路径
            foreach (var group in PendingTaskGroups)
            {
                foreach (var task in group.Tasks)
                {
                    string sourceName = Path.GetFileName(task.SourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    task.TargetPath = Path.Combine(UnifiedTargetRoot, sourceName);
                }
            }
        }
    }

    private void UpdateSelectionState()
    {
        if (_isUpdatingFromCode)
            return;
            
        var allTasks = PendingTaskGroups.SelectMany(g => g.Tasks).ToList();
        var totalCount = allTasks.Count;
        var selectedCount = allTasks.Count(t => t.IsSelected);

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
        var dialog = new OpenFolderDialog
        {
            Title = $"选择 \"{task.DisplayName}\" 的目标目录"
        };
        
        // 如果已有目标路径，使用其父目录作为初始位置
        if (!string.IsNullOrEmpty(task.TargetPath))
        {
            try
            {
                string? parentDir = Path.GetDirectoryName(task.TargetPath);
                if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
                {
                    dialog.InitialDirectory = parentDir;
                }
            }
            catch { }
        }

        if (dialog.ShowDialog() == true)
        {
            task.TargetPath = dialog.FolderName;
        }
    }

    [RelayCommand]
    private async Task StartQuickMigrationAsync()
    {
        if (!HasValidTasks || IsExecuting)
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
            MessageBox.Show("请先选择统一目标根目录", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            try
            {
                // 获取源目录大小
                long sourceSize = FileStatsService.GetDirectorySize(task.SourcePath);
                
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
                var result = MessageBox.Show(
                    $"任务 \"{task.DisplayName}\" 扫描失败:\n{ex.Message}\n\n是否继续？", 
                    "扫描警告", 
                    MessageBoxButton.YesNo, 
                    MessageBoxImage.Warning);
                
                if (result != MessageBoxResult.Yes)
                {
                    IsExecuting = false;
                    _cancellationTokenSource?.Dispose();
                    _cancellationTokenSource = null;
                    return;
                }
            }
        }

        // 检查每个目标磁盘的可用空间
        bool spaceCheckFailed = false;
        foreach (var kvp in diskSpaceRequirements)
        {
            string diskRoot = kvp.Key;
            long requiredBytes = kvp.Value;
            
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

        if (spaceCheckFailed)
        {
            MessageBox.Show(
                "目标磁盘空间不足，无法继续迁移！\n\n请查看日志了解详细信息。", 
                "空间不足", 
                MessageBoxButton.OK, 
                MessageBoxImage.Error);
            IsExecuting = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            return;
        }

        AddLog("磁盘空间检查通过，开始迁移任务...");

        foreach (var task in tasks)
        {
            if (_cancellationTokenSource.Token.IsCancellationRequested)
            {
                AddLog("用户取消操作");
                break;
            }

            await ExecuteSingleMigrationTaskAsync(task);
        }

        AddLog($"一键迁移完成！成功: {CompletedCount}, 失败: {FailedCount}");

        IsExecuting = false;
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;

        // 刷新任务列表
        ScanAndBuildTasks();
    }

    [RelayCommand]
    private async Task MigrateSingleTaskAsync(QuickMigrateTask task)
    {
        if (IsExecuting)
            return;

        // 验证目标路径
        if (UseUnifiedTarget && string.IsNullOrWhiteSpace(UnifiedTargetRoot))
        {
            MessageBox.Show("请先选择统一目标根目录", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(task.TargetPath))
        {
            MessageBox.Show(
                "目标路径未设置。\n\n请点击\"浏览...\"按钮选择目标目录，或在配置文件中指定 unifiedTargetRoot。",
                "目标路径无效",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show(
            $"确定要迁移以下任务吗？\n\n源: {task.SourcePath}\n目标: {task.TargetPath}\n\n迁移后，源位置将创建符号链接指向目标位置。",
            "确认迁移",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        IsExecuting = true;
        CompletedCount = 0;
        FailedCount = 0;
        TotalCount = 1;
        LogMessages.Clear();
        AddLog($"开始迁移: {task.DisplayName}");

        _cancellationTokenSource = new CancellationTokenSource();

        await ExecuteSingleMigrationTaskAsync(task);

        if (task.Status == QuickMigrateTaskStatus.Completed)
        {
            AddLog("迁移操作完成");
        }
        else
        {
            AddLog("迁移操作失败");
        }

        IsExecuting = false;
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;

        // 刷新任务列表
        ScanAndBuildTasks();
    }

    [RelayCommand]
    private async Task RestoreTaskAsync(QuickMigrateTask task)
    {
        if (IsExecuting)
            return;

        var result = MessageBox.Show(
            $"确定要还原以下任务吗？\n\n源: {task.SourcePath}\n目标: {task.TargetPath}\n\n还原后，数据将从目标位置复制回源位置，符号链接将被删除。",
            "确认还原",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        IsExecuting = true;
        LogMessages.Clear();
        AddLog($"开始还原: {task.DisplayName}");

        await ExecuteSingleRestoreTaskAsync(task);

        AddLog("还原操作完成");
        IsExecuting = false;

        // 刷新任务列表
        ScanAndBuildTasks();
    }

    [RelayCommand]
    private async Task CleanupBackupAsync(QuickMigrateTask task)
    {
        if (string.IsNullOrEmpty(task.BackupPath) || !Directory.Exists(task.BackupPath))
        {
            MessageBox.Show("备份目录不存在", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"确定要删除备份目录吗？\n\n{task.BackupPath}\n\n此操作不可恢复！",
            "确认删除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        IsExecuting = true;
        AddLog($"清理备份: {task.BackupPath}");

        await Task.Run(() =>
        {
            try
            {
                Directory.Delete(task.BackupPath, true);
                AddLog("✅ 备份已清理");
                task.BackupPath = null;
                task.MigrationState = MigrationState.Migrated;
                task.StatusMessage = "已迁移";
            }
            catch (Exception ex)
            {
                AddLog($"❌ 清理失败: {ex.Message}");
            }
        });

        IsExecuting = false;
        ScanAndBuildTasks();
    }

    [RelayCommand]
    private void CancelExecution()
    {
        _cancellationTokenSource?.Cancel();
        AddLog("正在取消...");
    }

    private async Task ExecuteSingleMigrationTaskAsync(QuickMigrateTask task)
    {
        task.Status = QuickMigrateTaskStatus.InProgress;
        AddLog($"[{task.DisplayName}] 开始迁移");

        try
        {
            var config = new MigrationConfig
            {
                SourcePath = task.SourcePath,
                TargetPath = task.TargetPath,
                LargeFileThresholdMB = _config?.Defaults.LargeFileThresholdMB ?? 1024,
                RobocopyThreads = _config?.Defaults.RobocopyThreads ?? 8,
                SampleMilliseconds = 2000
            };

            var service = new ReversibleMigrationService(config, MigrationMode.Migrate);

            var progress = new Progress<MigrationProgress>(p =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    task.CurrentPhase = p.CurrentPhase;
                    task.ProgressPercent = p.PercentComplete;
                    task.StatusMessage = p.Message;
                });
            });

            var logProgress = new Progress<string>(msg =>
            {
                AddLog($"  {msg}");
            });

            var result = await service.ExecuteAsync(progress, logProgress, _cancellationTokenSource?.Token ?? default);

            if (result.Success)
            {
                task.Status = QuickMigrateTaskStatus.Completed;
                task.MigrationState = MigrationState.Migrated;
                task.MigratedAt = DateTime.Now;
                CompletedCount++;
                AddLog($"[{task.DisplayName}] ✅ 迁移成功");
            }
            else
            {
                task.Status = QuickMigrateTaskStatus.Failed;
                task.ErrorMessage = result.ErrorMessage;
                FailedCount++;
                AddLog($"[{task.DisplayName}] ❌ 迁移失败: {result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            task.Status = QuickMigrateTaskStatus.Failed;
            task.ErrorMessage = ex.Message;
            FailedCount++;
            AddLog($"[{task.DisplayName}] ❌ 异常: {ex.Message}");
        }
    }

    private async Task ExecuteSingleRestoreTaskAsync(QuickMigrateTask task)
    {
        task.Status = QuickMigrateTaskStatus.InProgress;
        AddLog($"[{task.DisplayName}] 开始还原");

        try
        {
            var config = new MigrationConfig
            {
                SourcePath = task.SourcePath,
                TargetPath = task.TargetPath,
                LargeFileThresholdMB = _config?.Defaults.LargeFileThresholdMB ?? 1024,
                RobocopyThreads = _config?.Defaults.RobocopyThreads ?? 8,
                SampleMilliseconds = 2000
            };

            bool keepTarget = _config?.Defaults.RestoreKeepTarget ?? false;
            var service = new ReversibleMigrationService(config, MigrationMode.Restore, keepTarget);

            var progress = new Progress<MigrationProgress>(p =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    task.CurrentPhase = p.CurrentPhase;
                    task.ProgressPercent = p.PercentComplete;
                    task.StatusMessage = p.Message;
                });
            });

            var logProgress = new Progress<string>(msg =>
            {
                AddLog($"  {msg}");
            });

            var result = await service.ExecuteAsync(progress, logProgress, _cancellationTokenSource?.Token ?? default);

            if (result.Success)
            {
                task.Status = QuickMigrateTaskStatus.Completed;
                task.MigrationState = MigrationState.Pending;
                task.MigratedAt = null;
                AddLog($"[{task.DisplayName}] ✅ 还原成功");
            }
            else
            {
                task.Status = QuickMigrateTaskStatus.Failed;
                task.ErrorMessage = result.ErrorMessage;
                AddLog($"[{task.DisplayName}] ❌ 还原失败: {result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            task.Status = QuickMigrateTaskStatus.Failed;
            task.ErrorMessage = ex.Message;
            AddLog($"[{task.DisplayName}] ❌ 异常: {ex.Message}");
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

/// <summary>
/// 任务分组
/// </summary>
public class QuickMigrateTaskGroup
{
    public string GroupName { get; set; } = string.Empty;
    public ObservableCollection<QuickMigrateTask> Tasks { get; set; } = new();
}

