using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MigrationCore.Services;
using MigrationCore.Services.Safety;
using MoveWithSymlinkWPF.Helpers;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;

namespace MoveWithSymlinkWPF.ViewModels;

public partial class FolderPickerViewModel : ObservableObject
{
    [ObservableProperty]
    private string _currentPath = string.Empty;

    [ObservableProperty]
    private string _selectedPath = string.Empty;

    [ObservableProperty]
    private ObservableCollection<FolderItem> _folders = new();

    [ObservableProperty]
    private FolderItem? _selectedFolder;

    [ObservableProperty]
    private ObservableCollection<QuickAccessItem> _quickAccessPaths = new();

    [ObservableProperty]
    private QuickAccessItem? _selectedQuickAccessPath;

    [ObservableProperty]
    private bool _canNavigateUp = true;

    [ObservableProperty]
    private bool _hasSelection = false;

    public bool DialogResult { get; private set; } = false;

    private Window? _window;

    public FolderPickerViewModel()
    {
        InitializeQuickAccess();
        
        // 默认导航到用户配置文件目录
        string defaultPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        NavigateToPath(defaultPath);
    }

    public void SetWindow(Window window)
    {
        _window = window;
    }

    private void InitializeQuickAccess()
    {
        QuickAccessPaths.Clear();

        // 添加常用位置
        QuickAccessPaths.Add(new QuickAccessItem
        {
            Icon = "🏠",
            DisplayName = "用户文件夹",
            Path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        });

        QuickAccessPaths.Add(new QuickAccessItem
        {
            Icon = "🖥️",
            DisplayName = "桌面",
            Path = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        });

        QuickAccessPaths.Add(new QuickAccessItem
        {
            Icon = "📄",
            DisplayName = "我的文档",
            Path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        });

        QuickAccessPaths.Add(new QuickAccessItem
        {
            Icon = "⬇️",
            DisplayName = "下载",
            Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        });

        // 添加所有固定磁盘
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                {
                    QuickAccessPaths.Add(new QuickAccessItem
                    {
                        Icon = "💾",
                        DisplayName = $"{drive.Name.TrimEnd('\\')} ({drive.VolumeLabel})",
                        Path = drive.RootDirectory.FullName
                    });
                }
            }
        }
        catch
        {
            // 忽略驱动器枚举错误
        }
    }

    partial void OnSelectedQuickAccessPathChanged(QuickAccessItem? value)
    {
        if (value != null && Directory.Exists(value.Path))
        {
            NavigateToPath(value.Path);
            // 同时设置选中路径，这样用户选择快速访问后可以直接确定
            SelectedPath = value.Path;
            HasSelection = true;
        }
    }

    partial void OnSelectedFolderChanged(FolderItem? value)
    {
        if (value != null)
        {
            SelectedPath = value.FullPath;
            HasSelection = true;
        }
        else
        {
            HasSelection = false;
        }
    }

    [RelayCommand]
    private void NavigateToPath()
    {
        if (!string.IsNullOrWhiteSpace(CurrentPath) && Directory.Exists(CurrentPath))
        {
            NavigateToPath(CurrentPath);
        }
        else
        {
            CustomMessageBox.ShowError("路径不存在！", "错误");
        }
    }

    [RelayCommand]
    private void SelectCurrentPath()
    {
        if (!string.IsNullOrWhiteSpace(CurrentPath) && Directory.Exists(CurrentPath))
        {
            SelectedPath = CurrentPath;
            HasSelection = true;
            
            // 检查是否为符号链接
            bool isSymlink = SymbolicLinkHelper.IsSymbolicLink(CurrentPath);
            string message = isSymlink
                ? $"已选择当前路径（符号链接）：\n\n{CurrentPath}\n\n点击'确定'按钮完成选择。"
                : $"已选择当前路径：\n\n{CurrentPath}\n\n点击'确定'按钮完成选择。";

            CustomMessageBox.ShowInformation(message, "选择当前路径");
        }
    }

    private void NavigateToPath(string path)
    {
        try
        {
            CurrentPath = Path.GetFullPath(path);
            LoadFolders();
            UpdateNavigationState();
        }
        catch (Exception ex)
        {
            CustomMessageBox.ShowError($"无法访问路径：{ex.Message}", "错误");
        }
    }

    [RelayCommand]
    private void NavigateUp()
    {
        if (CanNavigateUp)
        {
            var parent = Directory.GetParent(CurrentPath);
            if (parent != null)
            {
                NavigateToPath(parent.FullName);
            }
        }
    }

    [RelayCommand]
    private void NavigateToRoot()
    {
        try
        {
            var root = Path.GetPathRoot(CurrentPath);
            if (!string.IsNullOrEmpty(root))
            {
                NavigateToPath(root);
            }
        }
        catch (Exception ex)
        {
            CustomMessageBox.ShowError($"无法导航到根目录：{ex.Message}", "错误");
        }
    }

    [RelayCommand]
    private void ItemDoubleClick(FolderItem? item)
    {
        if (item == null) return;

        if (item.IsSymlink)
        {
            // 符号链接：直接选择
            SelectedPath = item.FullPath;
            SelectedFolder = item;
            HasSelection = true;
            
            // 显示提示
            CustomMessageBox.ShowInformation(
                $"已选择符号链接：\n\n{item.FullPath}\n\n点击'确定'按钮完成选择。",
                "符号链接");
        }
        else
        {
            // 普通文件夹：进入该目录
            NavigateToPath(item.FullPath);
        }
    }

    [RelayCommand]
    private void Confirm()
    {
        if (!string.IsNullOrWhiteSpace(SelectedPath))
        {
            DialogResult = true;
            if (_window != null)
            {
                _window.DialogResult = true;  // 设置窗口的 DialogResult
            }
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        DialogResult = false;
        if (_window != null)
        {
            _window.DialogResult = false;  // 设置窗口的 DialogResult
        }
    }

    private void LoadFolders()
    {
        Folders.Clear();

        try
        {
            var directoryInfo = new DirectoryInfo(CurrentPath);
            var directories = directoryInfo.GetDirectories();
            string? pathRoot = Path.GetPathRoot(CurrentPath);
            bool isVolumeRoot = !string.IsNullOrEmpty(pathRoot)
                && string.Equals(CurrentPath.TrimEnd('\\'), pathRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

            foreach (var dir in directories.OrderBy(d => d.Name))
            {
                if (isVolumeRoot && MigrationSafetyPolicy.IsHiddenSystemVolumeRootName(dir.Name))
                {
                    continue;
                }

                try
                {
                    bool isSymlink = SymbolicLinkHelper.IsSymbolicLink(dir.FullName);
                    string targetPath = string.Empty;

                    if (isSymlink)
                    {
                        targetPath = dir.LinkTarget ?? string.Empty;
                    }

                    Folders.Add(new FolderItem
                    {
                        Name = dir.Name,
                        FullPath = dir.FullName,
                        IsSymlink = isSymlink,
                        TargetPath = targetPath,
                        TypeDescription = isSymlink ? $"符号链接 → {targetPath}" : "文件夹"
                    });
                }
                catch
                {
                    // 跳过无法访问的目录
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            CustomMessageBox.ShowWarning("没有权限访问此目录！", "访问被拒绝");
        }
        catch (Exception ex)
        {
            CustomMessageBox.ShowError($"加载文件夹失败：{ex.Message}", "错误");
        }
    }

    private void UpdateNavigationState()
    {
        try
        {
            var parent = Directory.GetParent(CurrentPath);
            CanNavigateUp = parent != null;
        }
        catch
        {
            CanNavigateUp = false;
        }
    }
}

/// <summary>
/// 文件夹项目
/// </summary>
public class FolderItem
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public bool IsSymlink { get; set; }
    public string TargetPath { get; set; } = string.Empty;
    public string TypeDescription { get; set; } = string.Empty;
    public Visibility IsSymlinkVisibility => IsSymlink ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// 快速访问项目
/// </summary>
public class QuickAccessItem
{
    public string Icon { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}

