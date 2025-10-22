using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MigrationCore.Models;
using MigrationCore.Services;
using System.Collections.ObjectModel;
using Windows.Storage.Pickers;

namespace MoveWithSymlinkGUI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private string _sourcePath = string.Empty;

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

    [RelayCommand]
    private async Task BrowseSourceAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            SourcePath = folder.Path;
        }
    }

    [RelayCommand]
    private async Task BrowseTargetAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            TargetPath = folder.Path;
        }
    }

    [RelayCommand]
    private async Task ValidateAndProceedAsync()
    {
        IsValidating = true;
        HasValidationError = false;
        ValidationMessage = string.Empty;

        try
        {
            await Task.Run(() =>
            {
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

                // 验证目标路径
                var (isValidTarget, targetError) = PathValidator.ValidateTargetPath(TargetPath);
                if (!isValidTarget)
                {
                    throw new InvalidOperationException(targetError);
                }

                // 验证路径关系
                var (isValidRelation, relationError) = PathValidator.ValidatePathRelation(SourcePath, TargetPath);
                if (!isValidRelation)
                {
                    throw new InvalidOperationException(relationError);
                }

                // 权限检查
                if (!PathValidator.IsAdministrator())
                {
                    ValidationMessage = "当前非管理员权限，若未启用开发者模式，创建符号链接可能失败";
                }
            });

            // 验证通过，进入下一步
            CurrentStep = 2;
        }
        catch (Exception ex)
        {
            HasValidationError = true;
            ValidationMessage = ex.Message;
        }
        finally
        {
            IsValidating = false;
        }
    }

    [ObservableProperty]
    private bool _scanCompleted = false;

    [RelayCommand]
    private async Task ScanAsync()
    {
        IsScanning = true;
        ScanCompleted = false;
        StatsMessage = "正在扫描...";

        try
        {
            var progress = new Progress<string>(msg =>
            {
                StatsMessage = msg;
            });

            long thresholdBytes = (long)LargeFileThresholdMB * 1024 * 1024;
            _scannedStats = await FileStatsService.ScanDirectoryAsync(SourcePath, thresholdBytes, progress);

            StatsMessage = $"✓ 扫描完成！\n\n" +
                          $"总文件: {_scannedStats.TotalFiles}\n" +
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

            ScanCompleted = true;
        }
        catch (Exception ex)
        {
            StatsMessage = $"❌ 扫描失败: {ex.Message}";
            ScanCompleted = false;
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void ProceedToMigration()
    {
        if (ScanCompleted && _scannedStats != null)
        {
            CurrentStep = 3;
        }
    }

    [RelayCommand]
    private async Task StartMigrationAsync()
    {
        IsMigrating = true;
        MigrationCompleted = false;
        ProgressPercent = 0;
        LogMessages.Clear();

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

            var service = new MigrationService(config);

            var progress = new Progress<MigrationProgress>(p =>
            {
                ProgressPercent = p.PercentComplete;
                ProgressMessage = p.Message;
                PhaseDescription = $"[{p.CurrentPhase}/6] {p.PhaseDescription}";
            });

            var logProgress = new Progress<string>(msg =>
            {
                LogMessages.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
            });

            var result = await service.ExecuteMigrationAsync(progress, logProgress, _cancellationTokenSource.Token);

            MigrationSuccess = result.Success;
            MigrationCompleted = true;

            if (result.Success)
            {
                ResultMessage = $"✓ 迁移成功！\n\n" +
                               $"源路径(现为链接): {result.SourcePath}\n" +
                               $"目标路径: {result.TargetPath}\n" +
                               $"总文件: {result.Stats?.TotalFiles}\n" +
                               $"总大小: {FileStatsService.FormatBytes(result.Stats?.TotalBytes ?? 0)}";
            }
            else
            {
                ResultMessage = $"❌ 迁移失败: {result.ErrorMessage}\n" +
                               (result.WasRolledBack ? "已回滚至原始状态" : "");
            }

            // 不自动跳转，让用户查看日志
        }
        catch (Exception ex)
        {
            MigrationSuccess = false;
            MigrationCompleted = true;
            ResultMessage = $"❌ 发生错误: {ex.Message}";
            LogMessages.Add($"[{DateTime.Now:HH:mm:ss}] ❌ {ex.Message}");
        }
        finally
        {
            IsMigrating = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
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
    private void CancelMigration()
    {
        _cancellationTokenSource?.Cancel();
    }

    [RelayCommand]
    private void BackToStep1()
    {
        CurrentStep = 1;
        HasValidationError = false;
        ValidationMessage = string.Empty;
        ScanCompleted = false;
        StatsMessage = string.Empty;
        _scannedStats = null;
    }

    [RelayCommand]
    private void BackToStep2()
    {
        CurrentStep = 2;
        MigrationCompleted = false;
        ProgressPercent = 0;
        ProgressMessage = string.Empty;
        PhaseDescription = string.Empty;
        LogMessages.Clear();
    }

    [RelayCommand]
    private void Reset()
    {
        CurrentStep = 1;
        SourcePath = string.Empty;
        TargetPath = string.Empty;
        LargeFileThresholdMB = 1024;
        RobocopyThreads = 8;
        HasValidationError = false;
        ValidationMessage = string.Empty;
        StatsMessage = string.Empty;
        ScanCompleted = false;
        ProgressPercent = 0;
        ProgressMessage = string.Empty;
        PhaseDescription = string.Empty;
        MigrationCompleted = false;
        MigrationSuccess = false;
        ResultMessage = string.Empty;
        LogMessages.Clear();
        _scannedStats = null;
    }
}

