using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MigrationCore.Models;
using MigrationCore.Services;
using MoveWithSymlinkWPF.Services;
using MoveWithSymlinkWPF.Views;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using System.Diagnostics;

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
            }
        }
    }

    [ObservableProperty]
    private string _targetPath = string.Empty;

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
        
#if DEBUG
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] MainViewModel initialized");
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Version: {VersionText}");
#endif
    }

    [RelayCommand]
    private void ShowManualMode()
    {
        IsManualMode = true;
        IsQuickMigrateMode = false;
#if DEBUG
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Switched to Manual Mode");
#endif
    }

    [RelayCommand]
    private void ShowQuickMigrate()
    {
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
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] BrowseSource command triggered");
#endif
        
        // 使用 FolderBrowserDialog 需要手动输入，或者使用自定义逻辑
        // OpenFolderDialog 会自动解析符号链接，所以我们需要特殊处理
        
        var dialog = new OpenFolderDialog
        {
            Title = "选择源目录（如果是符号链接，请手动输入路径）"
        };

        if (dialog.ShowDialog() == true)
        {
            string selectedPath = dialog.FolderName;
#if DEBUG
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Dialog returned path: {selectedPath}");
#endif

            // 检查对话框返回的路径是否为符号链接
            // 注意：OpenFolderDialog 可能会解析符号链接返回目标路径
            bool isSymlink = SymbolicLinkHelper.IsSymbolicLink(selectedPath);
            
#if DEBUG
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Dialog path is symlink: {isSymlink}");
#endif

            // 如果不是符号链接，尝试查找父目录中是否有指向这个路径的符号链接
            if (!isSymlink)
            {
                string? symlinkPath = FindSymlinkPointingTo(selectedPath);
                if (symlinkPath != null)
                {
#if DEBUG
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Found symlink pointing to this path: {symlinkPath}");
#endif
                    // 询问用户是否要使用符号链接路径
                    var result = MessageBox.Show(
                        $"检测到符号链接指向此目录：\n\n" +
                        $"符号链接：{symlinkPath}\n" +
                        $"目标目录：{selectedPath}\n\n" +
                        $"您想要使用符号链接路径吗？\n" +
                        $"• 选择'是'将进入还原模式\n" +
                        $"• 选择'否'将使用目标路径进入迁移模式（警告：重复迁移会造成多层嵌套，可能有未知问题）",
                        "检测到符号链接",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    
                    if (result == MessageBoxResult.Yes)
                    {
                        selectedPath = symlinkPath;
#if DEBUG
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] User chose to use symlink path");
#endif
                    }
                }
            }

            SourcePath = selectedPath;
#if DEBUG
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Final source path set to: {SourcePath}");
#endif
            // 检测源目录是否为符号链接，自动切换模式
            DetectAndSwitchMode();
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

            foreach (string searchPath in searchPaths)
            {
                if (!Directory.Exists(searchPath))
                    continue;

#if DEBUG
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Searching in: {searchPath}");
#endif

                try
                {
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
        var dialog = new OpenFolderDialog
        {
            Title = "选择目标目录"
        };

        if (dialog.ShowDialog() == true)
        {
            TargetPath = dialog.FolderName;
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
        // 切换到步骤3并开始迁移
        CurrentStep = 3;
        await StartMigrationAsync();
    }

    [RelayCommand]
    private async Task StartMigrationAsync()
    {
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
                // 还原模式 - 使用 ReversibleMigrationService，初始不删除目标数据
                var restoreService = new ReversibleMigrationService(config, MigrationMode.Restore, keepTargetOnRestore: true);
                result = await restoreService.ExecuteAsync(progress, logProgress, _cancellationTokenSource.Token);
            }
            else
            {
                // 迁移模式 - 使用 MigrationService
                var service = new MigrationService(config);
                result = await service.ExecuteMigrationAsync(progress, logProgress, _cancellationTokenSource.Token);
            }

            MigrationSuccess = result.Success;
            MigrationCompleted = true;

            if (result.Success)
            {
                if (MigrationMode == MigrationMode.Restore)
                {
                    // 还原成功 - 显示还原结果
                    ResultMessage = $"✓ 还原成功！\n\n" +
                                   $"源路径(已还原): {result.SourcePath}\n" +
                                   $"原目标路径: {result.TargetPath}\n" +
                                   $"总文件: {result.Stats?.TotalFiles}\n" +
                                   $"总大小: {FileStatsService.FormatBytes(result.Stats?.TotalBytes ?? 0)}";

                    // 还原成功后，询问用户是否删除目标目录数据
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        var cleanupResult = MessageBox.Show(
                            $"还原完成！\n\n是否删除目标目录的数据？\n\n目标目录：{TargetPath}\n\n提示：删除后无法恢复。",
                            "还原完成",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Question);

                        if (cleanupResult == MessageBoxResult.Yes)
                        {
                            // 用户选择删除目标目录
                            Task.Run(async () =>
                            {
                                try
                                {
                                    AddLog("正在删除目标目录数据...");
                                    await Task.Run(() =>
                                    {
                                        if (Directory.Exists(TargetPath))
                                        {
                                            Directory.Delete(TargetPath, true);
                                        }
                                    });
                                    AddLog($"✅ 已删除目标目录: {TargetPath}");
                                    Application.Current.Dispatcher.Invoke(() =>
                                    {
                                        ResultMessage += "\n\n✅ 目标目录数据已删除";
                                    });
                                }
                                catch (Exception ex)
                                {
                                    AddLog($"❌ 删除目标目录失败: {ex.Message}");
                                    Application.Current.Dispatcher.Invoke(() =>
                                    {
                                        MessageBox.Show(
                                            $"删除目标目录失败：\n{ex.Message}\n\n您可以稍后手动删除。",
                                            "删除失败",
                                            MessageBoxButton.OK,
                                            MessageBoxImage.Warning);
                                    });
                                }
                            });
                        }
                        else
                        {
                            AddLog("用户选择保留目标目录数据");
                            ResultMessage += "\n\n📁 已保留目标目录数据";
                        }
                    });
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
                // 失败情况
                string operationType = MigrationMode == MigrationMode.Restore ? "还原" : "迁移";
                ResultMessage = $"❌ {operationType}失败\n\n" +
                               $"错误信息: {result.ErrorMessage}\n\n" +
                               (result.WasRolledBack ? "✓ 已回滚至原始状态\n" : "") +
                               "请查看下方日志了解详细信息。";
            }

            // 如果失败，不自动跳转，让用户查看日志；如果成功，自动跳转到结果页面
            if (result.Success)
            {
                CurrentStep = 4;
            }
        }
        catch (Exception ex)
        {
            MigrationSuccess = false;
            MigrationCompleted = true;
            string operationType = MigrationMode == MigrationMode.Restore ? "还原" : "迁移";
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

    [RelayCommand]
    private void CancelMigration()
    {
        _cancellationTokenSource?.Cancel();
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
    private void BackToStep1()
    {
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
        Application.Current.Shutdown();
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

