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
                string sourceName = string.IsNullOrEmpty(task.RelativePath) ? Path.GetFileName(task.SourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) : task.RelativePath;
                task.TargetPath = Path.Combine(UnifiedTargetRoot, sourceName);
            }
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
        }

        foreach (var group in migratedGroups)
        {
            MigratedTaskGroups.Add(group);
        }

        HasValidTasks = PendingTaskGroups.Any() || MigratedTaskGroups.Any();

        if (!HasValidTasks)
        {
            StatusMessage = "无可一键迁移目录";
        }
        else
        {
            StatusMessage = $"找到 {pendingTasks.Count} 个未迁移任务，{migratedTasks.Count} 个已迁移任务";
        }
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

    [RelayCommand]
    private async Task StartQuickMigrationAsync()
    {
        if (!HasValidTasks || IsExecuting)
            return;

        // 收集所有未迁移任务
        var tasks = PendingTaskGroups.SelectMany(g => g.Tasks).ToList();

        if (!tasks.Any())
        {
            AddLog("没有可执行的迁移任务");
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

