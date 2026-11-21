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
    private bool _isManualMode = true;

    [ObservableProperty]
    private bool _isQuickMigrateMode = false;

    [ObservableProperty]
    private object? _quickMigratePage;

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

    private string _sourcePath = string.Empty;
    public string SourcePath
    {
        get => _sourcePath;
        set
        {
            if (SetProperty(ref _sourcePath, value))
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
            if (SetProperty(ref _targetPath, value))
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
    public bool IsOperationBusy => IsMigrating || (QuickMigratePage is QuickMigratePage page && page.DataContext is QuickMigrateViewModel vm && vm.IsExecuting);

    public ObservableCollection<string> LogMessages { get; } = new();

    private FileStats? _scannedStats;
    private CancellationTokenSource? _cancellationTokenSource;

    public string VersionText { get; }

    public MainViewModel()
    {
        // 从 version.json 或程序集获取版本号
        VersionText = VersionService.GetVersion();
        
        // 初始化一键迁移页面
        QuickMigratePage = new QuickMigratePage();
        _ = RestoreLastTransactionAsync();
        
#if DEBUG
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] MainViewModel initialized");
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Version: {VersionText}");
#endif
    }

    [RelayCommand]
    private void ShowManualMode()
    {
        if (IsOperationBusy) return;
        IsManualMode = true;
        IsQuickMigrateMode = false;
#if DEBUG
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Switched to Manual Mode");
#endif
    }

    [RelayCommand]
    private void ShowQuickMigrate()
    {
        if (IsOperationBusy) return;
        IsManualMode = false;
        IsQuickMigrateMode = true;
#if DEBUG
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Switched to Quick Migrate Mode");
#endif
    }

    [RelayCommand]
    private void BrowseSource()
    {
#if DEBUG
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] BrowseSource command triggered - Using custom folder picker");
#endif
        
        // 使用自定义文件夹选择器，可以正确识别符号链接
        var picker = new Views.FolderPickerWindow
        {
            Owner = Application.Current.MainWindow
        };

        if (picker.ShowDialog() == true)
        {
            string? selectedPath = picker.SelectedPath;
            
            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
#if DEBUG
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] User selected path: {selectedPath}");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Checking if path is symlink...");
#endif
                
                // 检查是否为符号链接
                bool isSymlink = SymbolicLinkHelper.IsSymbolicLink(selectedPath);
                
#if DEBUG
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] IsSymbolicLink result: {isSymlink}");
#endif

                SourcePath = selectedPath;
                
#if DEBUG
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Final source path set to: {SourcePath}");
#endif
                
                // 检测源目录是否为符号链接，自动切换模式
                DetectAndSwitchMode();
            }
        }
        else
        {
#if DEBUG
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] User cancelled folder selection");
#endif
        }
    }
    
    /// <summary>
    /// 查找指向指定目标路径的符号链接
    /// </summary>
    private string? FindSymlinkPointingTo(string targetPath)
    {
        try
        {
            // 规范化目标路径
            string normalizedTarget = Path.GetFullPath(targetPath);
#if DEBUG
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Looking for symlinks pointing to: {normalizedTarget}");
#endif

            // 获取目标路径的父目录
            string? targetParent = Path.GetDirectoryName(normalizedTarget);
            if (string.IsNullOrEmpty(targetParent))
                return null;

            // 搜索常见的符号链接位置
            var searchPaths = new List<string>();
            
            // 1. 目标的父目录
            searchPaths.Add(targetParent);
            
            // 2. 常见的符号链接存放位置
            searchPaths.Add(@"C:\testMove");
            
            // 3. 用户配置文件目录
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            searchPaths.Add(userProfile);
            
            // 4. 桌面
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            searchPaths.Add(desktop);
            
            // 5. 我的文档
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            searchPaths.Add(documents);
            
            // 6. 所有固定磁盘的根目录
            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                    {
                        searchPaths.Add(drive.RootDirectory.FullName);
                    }
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error enumerating drives: {ex.Message}");
#else
                _ = ex; // 避免未使用变量警告
#endif
            }
            
            // 7. 从一键迁移配置文件中读取已知的符号链接位置
            try
            {
                var config = QuickMigrateConfigLoader.LoadConfig();
                if (config != null)
                {
                    // 从 Profiles 中获取符号链接位置
                    foreach (var profile in config.Profiles)
                    {
                        if (profile.Locator.Type == "absolutePath" && !string.IsNullOrEmpty(profile.Locator.Path))
                        {
                            var symlinkParent = Path.GetDirectoryName(profile.Locator.Path);
                            if (!string.IsNullOrEmpty(symlinkParent) && !searchPaths.Contains(symlinkParent, StringComparer.OrdinalIgnoreCase))
                            {
                                searchPaths.Add(symlinkParent);
                            }
                        }
                    }
                    
                    // 从 StandaloneSources 中获取符号链接位置
                    foreach (var source in config.StandaloneSources)
                    {
                        if (!string.IsNullOrEmpty(source.AbsolutePath))
                        {
                            var symlinkParent = Path.GetDirectoryName(source.AbsolutePath);
                            if (!string.IsNullOrEmpty(symlinkParent) && !searchPaths.Contains(symlinkParent, StringComparer.OrdinalIgnoreCase))
                            {
                                searchPaths.Add(symlinkParent);
                            }
                        }
                    }
                }
            }
            catch
            {
                // 忽略配置文件读取失败
            }

            foreach (string searchPath in searchPaths)
            {
                if (!Directory.Exists(searchPath))
                    continue;

#if DEBUG
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Searching in: {searchPath}");
#endif

                try
                {
                    // 限制搜索深度为1层，避免过度扫描
                    foreach (string dir in Directory.GetDirectories(searchPath))
                    {
                        if (SymbolicLinkHelper.IsSymbolicLink(dir))
                        {
                            var dirInfo = new DirectoryInfo(dir);
                            string? linkTarget = dirInfo.LinkTarget;
                            
                            if (!string.IsNullOrEmpty(linkTarget))
                            {
                                string normalizedLinkTarget = Path.GetFullPath(linkTarget);
#if DEBUG
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Found symlink: {dir} -> {normalizedLinkTarget}");
#endif
                                if (string.Equals(normalizedLinkTarget, normalizedTarget, StringComparison.OrdinalIgnoreCase))
                                {
#if DEBUG
                                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ Match found!");
#endif
                                    return dir;
                                }
                            }
                        }
                    }
                }
#if DEBUG
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error scanning {searchPath}: {ex.Message}");
#else
                catch
                {
#endif
                }
            }

            return null;
        }
#if DEBUG
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error in FindSymlinkPointingTo: {ex.Message}");
#else
        catch
        {
#endif
            return null;
        }
    }

    [RelayCommand]
    private void BrowseTarget()
    {
#if DEBUG
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] BrowseTarget command triggered - Using custom folder picker");
#endif
        
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
#if DEBUG
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] User selected target path: {selectedPath}");
#endif
                TargetPath = selectedPath;
            }
        }
        else
        {
#if DEBUG
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] User cancelled target folder selection");
#endif
        }
    }

    [RelayCommand]
    private async Task StartScanFromStep1Async()
    {
        // 先进行验证
        IsValidating = true;
        HasValidationError = false;
        ValidationMessage = string.Empty;

        try
        {
            await Task.Run(() =>
            {
                if (MigrationMode == MigrationMode.Restore)
                {
                    // ========== 还原模式验证 ==========
                    
                    // 验证源路径必须是符号链接
                    if (!Directory.Exists(SourcePath))
                    {
                        throw new InvalidOperationException("源路径不存在");
                    }

                    if (!SymbolicLinkHelper.IsSymbolicLink(SourcePath))
                    {
                        throw new InvalidOperationException("源路径不是符号链接，无法执行还原操作");
                    }

                    // 验证目标路径（符号链接指向的位置）必须存在
                    if (!Directory.Exists(TargetPath))
                    {
                        throw new InvalidOperationException($"符号链接指向的目标路径不存在：{TargetPath}");
                    }

                    // 检查磁盘空间（源磁盘需要有足够空间）
                    string? sourceDrive = Path.GetPathRoot(SourcePath);
                    if (!string.IsNullOrEmpty(sourceDrive))
                    {
                        var driveInfo = new DriveInfo(sourceDrive);
                        if (driveInfo.IsReady)
                        {
                            // 获取目标目录大小（近似）
                            long estimatedSize = 0;
                            try
                            {
                                var di = new DirectoryInfo(TargetPath);
                                estimatedSize = di.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                            }
                            catch
                            {
                                // 忽略
                            }

                            if (driveInfo.AvailableFreeSpace < estimatedSize * 1.1) // 需要1.1倍空间
                            {
                                ValidationMessage = $"⚠️ 警告：源磁盘可用空间可能不足\n" +
                                                   $"可用: {FileStatsService.FormatBytes(driveInfo.AvailableFreeSpace)}\n" +
                                                   $"预计需要: {FileStatsService.FormatBytes((long)(estimatedSize * 1.1))}";
                            }
                        }
                    }

                    ValidationMessage += (string.IsNullOrEmpty(ValidationMessage) ? "" : "\n\n") +
                                        "✅ 还原模式验证通过\n" +
                                        $"   将还原符号链接为真实目录\n" +
                                        $"   源（符号链接）: {SourcePath}\n" +
                                        $"   数据来源: {TargetPath}";
                }
                else
                {
                    // ========== 迁移模式验证 ==========
                    
                    // 验证源路径
                    var (isValidSource, sourceError, sourceWarning) = PathValidator.ValidateSourcePath(SourcePath);
                    if (!isValidSource)
                    {
                        throw new InvalidOperationException(sourceError);
                    }

                    if (sourceWarning != null)
                    {
                        ValidationMessage = sourceWarning;
                    }

                    // 获取源目录名称（用于可能的目标路径调整）
                    string sourceLeafForTarget = Path.GetFileName(SourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    
                    // 若目标路径是一个已存在的非空文件夹，且不以源目录名结尾，则自动拼接源目录名
                    if (Directory.Exists(TargetPath))
                    {
                        string targetLeafName = Path.GetFileName(TargetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                        if (string.IsNullOrEmpty(targetLeafName))
                        {
                            targetLeafName = new DirectoryInfo(TargetPath).Name;
                        }
                        
                        // 检查目标目录是否非空
                        bool isNonEmpty = false;
                        try
                        {
                            isNonEmpty = Directory.EnumerateFileSystemEntries(TargetPath).Any();
                        }
                        catch
                        {
                            // 忽略错误，继续处理
                        }
                        
                        // 如果目标目录非空，且目标目录名不等于源目录名，则自动拼接
                        if (isNonEmpty && !string.Equals(targetLeafName, sourceLeafForTarget, StringComparison.OrdinalIgnoreCase))
                        {
                            string newTargetPath = Path.Combine(TargetPath, sourceLeafForTarget);
                            ValidationMessage = $"目标目录非空，已自动调整为: {newTargetPath}";
                            TargetPath = newTargetPath;
                        }
                    }

                    // 验证目标路径
                    var (isValidTarget, targetError) = PathValidator.ValidateTargetPath(TargetPath);
                    if (!isValidTarget)
                    {
                        throw new InvalidOperationException(targetError);
                    }

                    // 检查最终目标目录是否为空（在路径调整之后）
                    var (isEmpty, emptyError) = PathValidator.IsTargetDirectoryEmpty(TargetPath);
                    if (!isEmpty)
                    {
                        throw new InvalidOperationException(emptyError);
                    }

                    // 验证路径关系
                    var (isValidRelation, relationError) = PathValidator.ValidatePathRelation(SourcePath, TargetPath);
                    if (!isValidRelation)
                    {
                        throw new InvalidOperationException(relationError);
                    }
                }

                // 权限检查（两种模式都需要）
                if (!PathValidator.IsAdministrator())
                {
                    if (!string.IsNullOrEmpty(ValidationMessage))
                    {
                        ValidationMessage += "\n";
                    }
                    
                    if (MigrationMode == MigrationMode.Restore)
                    {
                        ValidationMessage += "⚠️ 当前非管理员权限，若未启用开发者模式，还原操作可能失败";
                    }
                    else
                    {
                        ValidationMessage += "⚠️ 当前非管理员权限，若未启用开发者模式，创建符号链接可能失败";
                    }
                }
            });

            // 验证通过，切换到步骤2并开始扫描
            CurrentStep = 2;
            IsValidating = false;
            
            // 立即开始扫描
            await ScanAsync();
        }
        catch (Exception ex)
        {
            HasValidationError = true;
            ValidationMessage = ex.Message;
            IsValidating = false;
        }
    }

    private async Task ScanAsync()
    {
        IsScanning = true;
        StatsMessage = "正在扫描...";

        try
        {
            var progress = new Progress<string>(msg =>
            {
                Application.Current.Dispatcher.Invoke(() => StatsMessage = msg);
            });

            long thresholdBytes = (long)LargeFileThresholdMB * 1024 * 1024;
            _scannedStats = await FileStatsService.ScanDirectoryAsync(SourcePath, thresholdBytes, progress);

            StatsMessage = $"总文件: {_scannedStats.TotalFiles}\n" +
                          $"总大小: {FileStatsService.FormatBytes(_scannedStats.TotalBytes)}\n" +
                          $"大文件 (≥{LargeFileThresholdMB}MB): {_scannedStats.LargeFiles} 个";

            // 检查磁盘空间
            var (sufficient, available, required) = PathValidator.CheckDiskSpace(TargetPath, _scannedStats.TotalBytes);
            StatsMessage += $"\n\n目标磁盘可用: {FileStatsService.FormatBytes(available)}\n" +
                           $"所需空间: {FileStatsService.FormatBytes(required)}";

            if (!sufficient)
            {
                throw new InvalidOperationException("目标磁盘空间不足！");
            }
        }
        catch (Exception ex)
        {
            StatsMessage = $"扫描失败: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private async Task StartMigrationFromStep2Async()
    {
        // 改名快检仅由核心事务入口执行，界面不再先做一次未登记的目录切换。
        CurrentStep = 3;
        await StartMigrationAsync();
    }



    [RelayCommand]
    private async Task StartMigrationAsync()
    {
        if (IsOperationBusy) return;
        BackupTask = null;
        IsMigrating = true;
        MigrationCompleted = false;
        ProgressPercent = 0;
        Application.Current.Dispatcher.Invoke(() => LogMessages.Clear());

        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            var config = new MigrationConfig
            {
                SourcePath = SourcePath,
                TargetPath = TargetPath,
                LargeFileThresholdMB = LargeFileThresholdMB,
                RobocopyThreads = RobocopyThreads,
                VerificationMode = UseContentHash ? VerificationMode.ContentHash : VerificationMode.Basic,
                SampleMilliseconds = 1000
            };

            var progress = new Progress<MigrationProgress>(p =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ProgressPercent = p.PercentComplete;
                    ProgressMessage = p.Message;
                    PhaseDescription = $"[{p.CurrentPhase}/6] {p.PhaseDescription}";
                });
            });

            var logProgress = new Progress<string>(msg =>
            {
                AddLog(msg);
            });

            MigrationResult result;

            // 根据模式选择服务
            if (MigrationMode == MigrationMode.Restore)
            {
                // 还原保留旧目标与已登记备份，不在成功回调中清理数据。
                var restoreService = new ReversibleMigrationService(config, MigrationMode.Restore);
                result = await restoreService.ExecuteAsync(progress, logProgress, _cancellationTokenSource.Token);
            }
            else if (MigrationMode == MigrationMode.Repair)
            {
                // 修复模式 - 使用 RepairService，仅重建符号链接
                var repairService = new RepairService(config);
                result = await repairService.ExecuteAsync(progress, logProgress, _cancellationTokenSource.Token);
            }
            else
            {
                // 迁移模式 - 使用 MigrationService
                var service = new MigrationService(config);
                result = await service.ExecuteMigrationAsync(progress, logProgress, _cancellationTokenSource.Token);
            }

            MigrationSuccess = result.Success;
            MigrationCompleted = true;
            if (result.TransactionId is Guid transactionId) await RefreshBackupTaskAsync(transactionId);

            if (result.Success)
            {
                if (MigrationMode == MigrationMode.Repair)
                {
                    AddLog($"✅ 修复完成！源路径现为符号链接，指向目标位置");
                    AddLog($"   源: {SourcePath}");
                    AddLog($"   目标: {TargetPath}");
                    
                    // 设置修复结果消息
                    ResultMessage = $"✓ 修复成功！\n\n" +
                                   $"源路径(现为符号链接): {result.SourcePath}\n" +
                                   $"符号链接指向: {result.TargetPath}\n\n" +
                                   $"说明：修复模式不复制数据，仅重建符号链接。\n" +
                                   $"源路径现在指向目标位置的现有数据。";
                }
                else if (MigrationMode == MigrationMode.Restore)
                {
                    // 还原成功 - 显示还原结果
                    ResultMessage = $"✓ 还原成功！\n\n" +
                                   $"源路径(已还原): {result.SourcePath}\n" +
                                   $"还原前目标备份: {result.BackupPath ?? result.TargetPath}\n" +
                                   $"总文件: {result.Stats?.TotalFiles}\n" +
                                   $"总大小: {FileStatsService.FormatBytes(result.Stats?.TotalBytes ?? 0)}";

                    AddLog("还原前目标目录已转为本次还原备份保留，等待用户验证。");
                    ResultMessage += "\n\n原目标目录已改名为还原备份保留；验证可正常使用后再按需通过双确认处理备份。";
                }
                else
                {
                    // 迁移成功 - 显示迁移结果
                    ResultMessage = $"✓ 迁移成功！\n\n" +
                                   $"源路径(现为链接): {result.SourcePath}\n" +
                                   $"目标路径: {result.TargetPath}\n" +
                                   $"总文件: {result.Stats?.TotalFiles}\n" +
                                   $"总大小: {FileStatsService.FormatBytes(result.Stats?.TotalBytes ?? 0)}";
                }
            }
            else
            {
                ShowFailureResult(result);
            }

            CurrentStep = 4;
        }
        catch (Exception ex)
        {
            MigrationSuccess = false;
            MigrationCompleted = true;
            string operationType = MigrationMode == MigrationMode.Restore ? "还原" : 
                                  MigrationMode == MigrationMode.Repair ? "修复" : "迁移";
            ResultMessage = $"❌ 发生异常错误\n\n" +
                           $"错误信息: {ex.Message}\n\n" +
                           (ex.StackTrace != null ? $"堆栈跟踪:\n{ex.StackTrace}\n\n" : "") +
                           "请查看下方日志了解详细信息。";
            AddLog($"❌ {operationType}异常: {ex.Message}");
            if (ex.StackTrace != null)
            {
                AddLog($"堆栈: {ex.StackTrace}");
            }
            // 发生异常时不自动跳转，让用户查看日志
        }
        finally
        {
            IsMigrating = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    private void ShowFailureResult(MigrationResult result)
    {
        string operationType = MigrationMode == MigrationMode.Restore ? "还原" :
                              MigrationMode == MigrationMode.Repair ? "修复" : "迁移";
        string rollbackMessage = result.RollbackOutcome switch
        {
            RollbackOutcome.NotRequired => "未切换原路径，无需回滚。\n",
            RollbackOutcome.Restored => "✓ 已核对并恢复操作前状态。\n",
            _ => "需核对实际数据位置，不能认定已恢复。\n"
        };
        ResultMessage = $"❌ {operationType}失败\n\n" +
                        $"错误信息: {result.ErrorMessage}\n\n" +
                        rollbackMessage + "请查看下方日志了解详细信息。";
        if (result.RecoveryPaths.Length > 0) ResultMessage += "\n\n保留/待核对位置：\n" + string.Join("\n", result.RecoveryPaths);
    }

    [RelayCommand]
    private void CancelMigration()
    {
        _cancellationTokenSource?.Cancel();
        ProgressMessage = "正在取消，等待后台及本任务进程真实退出；请勿关闭应用。";
    }

    [RelayCommand]
    private void ViewResult()
    {
        if (MigrationCompleted)
        {
            CurrentStep = 4;
        }
    }

    [RelayCommand]
    private async Task BackToStep1Async()
    {
        if (IsOperationBusy) return;
        IsMigrating = true;
        try
        {
            if (BackupTask?.TransactionId is Guid id)
            {
                var record = await new MigrationTransactionStore().LoadAsync(id)
                    ?? throw new IOException("事务记录缺失，不能确认备份保留状态。");
                if (!UnverifiedExitConfirmation.Confirm([record], "完成并返回")) return;
            }
        }
        catch (Exception ex)
        {
            CustomMessageBox.ShowWarning(ex.Message, "不能完成");
            return;
        }
        finally { IsMigrating = false; }
        BackupTask = null;
        CurrentStep = 1;
        HasValidationError = false;
        ValidationMessage = string.Empty;
        StatsMessage = string.Empty;
        
        // 清空路径选择
        SourcePath = string.Empty;
        TargetPath = string.Empty;
        
        // 重置模式为迁移模式
        SwitchToMigrateMode();
        
        // 清空结果信息
        MigrationCompleted = false;
        MigrationSuccess = false;
        ResultMessage = string.Empty;
        ProgressPercent = 0;
        ProgressMessage = string.Empty;
        PhaseDescription = string.Empty;
        
        // 清空日志（可选，根据需求决定是否保留日志）
        Application.Current.Dispatcher.Invoke(() =>
        {
            LogMessages.Clear();
        });
        
#if DEBUG
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Back to Step 1, all paths and states cleared");
#endif
    }

    [RelayCommand]
    private void CloseApplication()
    {
        if (IsOperationBusy) { CancelCurrentOperations(); return; }
        Application.Current.MainWindow.Close();
    }

    /// <summary>
    /// 检测源目录是否为符号链接，并自动切换模式
    /// </summary>
    private void DetectAndSwitchMode()
    {
#if DEBUG
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] DetectAndSwitchMode called. SourcePath: '{SourcePath}'");
#endif

        if (string.IsNullOrWhiteSpace(SourcePath))
        {
            // 源路径为空，默认为迁移模式
#if DEBUG
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] SourcePath is empty, keeping Migrate mode");
#endif
            SwitchToMigrateMode();
            return;
        }

        if (!Directory.Exists(SourcePath))
        {
            // 源路径不存在，默认为迁移模式
#if DEBUG
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] SourcePath does not exist: {SourcePath}");
#endif
            SwitchToMigrateMode();
            return;
        }

        try
        {
#if DEBUG
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Checking if '{SourcePath}' is a symlink...");
#endif
            bool isSymlink = SymbolicLinkHelper.IsSymbolicLink(SourcePath);
#if DEBUG
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] IsSymbolicLink result: {isSymlink}");
#endif

            if (isSymlink)
            {
                // 源路径是符号链接，切换到还原模式
                var dirInfo = new DirectoryInfo(SourcePath);
                string? linkTarget = dirInfo.LinkTarget;

#if DEBUG
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Link target: '{linkTarget}'");
#endif

                if (!string.IsNullOrEmpty(linkTarget))
                {
                    TargetPath = linkTarget;
#if DEBUG
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Target path set to: {TargetPath}");
#endif
                }

                SwitchToRestoreMode();
                
                // 添加界面日志
                string logMessage = $"🔍 检测到符号链接，自动切换到还原模式";
                if (!string.IsNullOrEmpty(linkTarget))
                {
                    logMessage += $"\n   → 链接指向: {linkTarget}";
                }
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (LogMessages.Count == 0 || !LogMessages[^1].Contains("检测到符号链接"))
                    {
                        AddLog(logMessage);
                    }
                });

#if DEBUG
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ Switched to Restore mode. Target: {TargetPath}");
#endif
            }
            else
            {
                // 源路径不是符号链接，切换到迁移模式
                SwitchToMigrateMode();
                
                // 添加界面日志
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (LogMessages.Count == 0 || !LogMessages[^1].Contains("普通目录"))
                    {
                        AddLog($"🔍 检测到普通目录，使用迁移模式");
                    }
                });

#if DEBUG
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ Normal directory, using Migrate mode");
#endif
            }
        }
        catch (Exception ex)
        {
#if DEBUG
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ Error detecting symlink: {ex.Message}");
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Stack trace: {ex.StackTrace}");
#endif
            // 发生错误时，默认为迁移模式
            SwitchToMigrateMode();
            
            // 添加界面日志
            Application.Current.Dispatcher.Invoke(() =>
            {
                AddLog($"⚠️ 检测模式时出错，默认使用迁移模式: {ex.Message}");
            });
        }
    }

    /// <summary>
    /// 切换到迁移模式
    /// </summary>
    private void SwitchToMigrateMode()
    {
        MigrationMode = MigrationMode.Migrate;
        CurrentModeDisplay = "迁移模式";
        IsRestoreMode = false;
        IsTargetPathReadOnly = false;
        
#if DEBUG
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Mode switched to: Migrate");
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}]   - CurrentModeDisplay: {CurrentModeDisplay}");
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}]   - IsRestoreMode: {IsRestoreMode}");
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}]   - IsTargetPathReadOnly: {IsTargetPathReadOnly}");
#endif
    }

    /// <summary>
    /// 切换到还原模式
    /// </summary>
    private void SwitchToRestoreMode()
    {
        MigrationMode = MigrationMode.Restore;
        CurrentModeDisplay = "还原模式";
        IsRestoreMode = true;
        IsTargetPathReadOnly = true;
        
#if DEBUG
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Mode switched to: Restore");
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}]   - CurrentModeDisplay: {CurrentModeDisplay}");
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}]   - IsRestoreMode: {IsRestoreMode}");
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}]   - IsTargetPathReadOnly: {IsTargetPathReadOnly}");
#endif
    }

    /// <summary>
    /// 切换到修复模式
    /// </summary>
    private void SwitchToRepairMode()
    {
        MigrationMode = MigrationMode.Repair;
        CurrentModeDisplay = "修复模式";
        IsRestoreMode = false;
        IsTargetPathReadOnly = false;
        
#if DEBUG
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Mode switched to: Repair");
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}]   - CurrentModeDisplay: {CurrentModeDisplay}");
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}]   - IsRestoreMode: {IsRestoreMode}");
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}]   - IsTargetPathReadOnly: {IsTargetPathReadOnly}");
#endif
    }

    /// <summary>
    /// 检测是否满足修复条件
    /// </summary>
    private void CheckRepairCondition()
    {
        CanRepair = false;
        RepairHint = string.Empty;

        // 必须有源路径和目标路径
        if (string.IsNullOrWhiteSpace(SourcePath) || string.IsNullOrWhiteSpace(TargetPath))
            return;

        // 新增：如果源目录和目标目录相同，不触发修复模式
        if (string.Equals(Path.GetFullPath(SourcePath), Path.GetFullPath(TargetPath), StringComparison.OrdinalIgnoreCase))
            return;

        // 目标目录必须存在
        if (!Directory.Exists(TargetPath))
            return;

        // 新增：如果目标文件夹为空，不触发修复模式
        bool targetIsEmpty = !PathValidator.HasUserContent(TargetPath);
        if (targetIsEmpty)
            return;

        try
        {
            bool sourceExists = Directory.Exists(SourcePath);
            
            if (!sourceExists)
            {
                // 源不存在 - 可以修复
                CanRepair = true;
                RepairHint = "源路径不存在，可以直接创建符号链接";
            }
            else
            {
                bool isSymlink = SymbolicLinkHelper.IsSymbolicLink(SourcePath);
                
                if (isSymlink)
                {
                    // 源是符号链接 - 检查是否指向正确目标
                    var dirInfo = new DirectoryInfo(SourcePath);
                    string? currentTarget = dirInfo.LinkTarget;
                    
                    if (!string.IsNullOrEmpty(currentTarget))
                    {
                        string currentTargetFull = Path.GetFullPath(currentTarget);
                        string expectedTarget = Path.GetFullPath(TargetPath);
                        
                        if (!string.Equals(currentTargetFull, expectedTarget, StringComparison.OrdinalIgnoreCase))
                        {
                            CanRepair = true;
                            RepairHint = $"符号链接指向错误目标，可以修复重定向到正确位置";
                        }
                    }
                    else
                    {
                        CanRepair = true;
                        RepairHint = "符号链接无法读取目标，可以修复";
                    }
                }
                else
                {
                    // 源是普通目录 - 可以修复（会备份）
                    bool hasContent = PathValidator.HasUserContent(SourcePath);
                    if (hasContent)
                    {
                        CanRepair = true;
                        RepairHint = "源是普通目录（非空），可修复（将备份后创建符号链接）";
                    }
                    else
                    {
                        CanRepair = true;
                        RepairHint = "源是空目录，可修复（将删除后创建符号链接）";
                    }
                }
            }
        }
        catch
        {
            CanRepair = false;
            RepairHint = string.Empty;
        }

#if DEBUG
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] CheckRepairCondition: CanRepair={CanRepair}, Hint={RepairHint}");
#endif
    }

    /// <summary>
    /// 执行修复操作
    /// </summary>
    [RelayCommand]
    private async Task StartRepairAsync()
    {
        if (!CanRepair)
        {
            CustomMessageBox.ShowInformation("当前条件不满足修复要求", "提示");
            return;
        }

        var result = CustomMessageBox.ShowQuestion(
            $"确定要修复符号链接吗？\n\n源: {SourcePath}\n目标: {TargetPath}\n\n{RepairHint}",
            "确认修复");

        if (!result)
            return;

        // 切换到修复模式并执行
        SwitchToRepairMode();
        CurrentStep = 3;
        await StartMigrationAsync();
        
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
            var record = records.OrderByDescending(r => r.UpdatedAtUtc).FirstOrDefault();
            if (record is null) return;
            SourcePath = record.SourcePath;
            TargetPath = record.TargetPath;
            if (record.Mode == MigrationMode.Restore) SwitchToRestoreMode();
            else if (record.Mode == MigrationMode.Repair) SwitchToRepairMode();
            else SwitchToMigrateMode();
            UseContentHash = record.VerificationMode == VerificationMode.ContentHash;
            await RefreshBackupTaskAsync(record.TransactionId);
            MigrationCompleted = true;
            MigrationSuccess = record.Stage == MigrationStage.AwaitingUserVerification;
            ResultMessage = "已读取持久化事务；技术完成不代表用户验收。\n\n"
                + $"源：{record.SourcePath}\n目标：{record.TargetPath}\n备份：{record.BackupPath ?? "未创建"}\n"
                + $"staging：{record.StagingPath ?? "未创建"}\n试改名：{record.ProbePath ?? "未创建"}\n{record.LastError}";
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
        IsMigrating = true;
        try
        {
            await MigrationVerificationWindow.ShowForTransactionAsync(id);
            await RefreshBackupTaskAsync(id);
            if (QuickMigratePage is QuickMigratePage page && page.DataContext is QuickMigrateViewModel vm)
                vm.RefreshTransactionState();
        }
        catch (Exception ex) { CustomMessageBox.ShowWarning(ex.Message, "无法打开迁移验证"); }
        finally { IsMigrating = false; }
    }

    public async Task RefreshDisplayedTransactionAsync(Guid transactionId)
    {
        if (BackupTask?.TransactionId == transactionId) await RefreshBackupTaskAsync(transactionId);
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

