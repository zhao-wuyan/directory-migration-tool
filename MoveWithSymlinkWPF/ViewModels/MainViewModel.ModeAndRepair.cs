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

public partial class MainViewModel
{
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
                // 与后端 PrepareRepairAsync 准入一致：源不存在时必须存在匹配的历史事务，不能认领未知目录。
                if (HasMatchingRepairTransaction(SourcePath, TargetPath))
                {
                    CanRepair = true;
                    RepairHint = "已匹配到历史迁移事务，可直接重建符号链接";
                }
            }
            else
            {
                bool isSymlink = SymbolicLinkHelper.IsSymbolicLink(SourcePath);

                if (isSymlink)
                {
                    // 源是符号链接时，仅当存在匹配的历史事务且链接未指向预期目标才可修复。
                    var dirInfo = new DirectoryInfo(SourcePath);
                    string? currentTarget = dirInfo.LinkTarget;
                    bool pointsToExpected = !string.IsNullOrEmpty(currentTarget)
                        && string.Equals(Path.GetFullPath(currentTarget), Path.GetFullPath(TargetPath), StringComparison.OrdinalIgnoreCase);

                    if (!pointsToExpected && HasMatchingRepairTransaction(SourcePath, TargetPath))
                    {
                        CanRepair = true;
                        RepairHint = string.IsNullOrEmpty(currentTarget)
                            ? "已匹配到历史事务，符号链接目标异常，可修复"
                            : "已匹配到历史事务，符号链接指向不一致，可修复重定向";
                    }
                }
                else
                {
                    // 源是普通目录 - 可以修复（非空会备份，空目录将删除后建链）
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

    private static bool HasMatchingRepairTransaction(string sourcePath, string targetPath)
    {
        try
        {
            string normalizedSource = PathIdentityService.Normalize(sourcePath);
            string normalizedTarget = PathIdentityService.Normalize(targetPath);
            var identity = new PathIdentityService();
            DirectoryIdentity targetIdentity = identity.ReadDirectory(normalizedTarget, false);
            if (targetIdentity.IsReparsePoint) return false;
            var records = new MigrationTransactionStore().ReadAllAsync().GetAwaiter().GetResult();
            var matched = records.Where(r =>
                r.SourcePath.Equals(normalizedSource, StringComparison.OrdinalIgnoreCase)
                && r.TargetPath.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase)
                && r.TargetIdentity == targetIdentity).ToArray();
            if (matched.Any(r => r.IsActive || r.BackupDeletionInProgress)) return false;
            var known = matched.Where(r => !(r.Mode == MigrationMode.Restore && r.Stage is MigrationStage.Cancelled or MigrationStage.Failed)).ToArray();
            return known.Length == 1;
        }
        catch
        {
            return false;
        }
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

}
