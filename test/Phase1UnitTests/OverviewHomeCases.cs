using System.Windows;
using MigrationCore.Models;
using MigrationCore.Services;
using MoveWithSymlinkWPF;
using MoveWithSymlinkWPF.ViewModels;
using MoveWithSymlinkWPF.Views;

internal static class OverviewHomeCases
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("OverviewViewModel-drives-cap-adaptive-linglong-and-onboarding", async () =>
        {
            const long GiB = 1024L * 1024L * 1024L;
            var drives = new[]
            {
                new SharedDriveTelemetry(@"D:\", "D:", "Data", "Fixed", "NTFS", 1000 * GiB, 600 * GiB, 400 * GiB, 40.0, false, true, true, true),
                new SharedDriveTelemetry(@"C:\", "C:", "Windows", "Fixed", "NTFS", 256 * GiB, 12 * GiB, 244 * GiB, 95.3, true, true, true, false),
                new SharedDriveTelemetry(@"E:\", "E:", "Games", "Fixed", "NTFS", 500 * GiB, 250 * GiB, 250 * GiB, 50.0, false, true, true, true),
                new SharedDriveTelemetry(@"F:\", "F:", "Archive", "Fixed", "NTFS", 500 * GiB, 100 * GiB, 400 * GiB, 80.0, false, true, true, true),
                new SharedDriveTelemetry(@"G:\", "G:", "Extra", "Fixed", "NTFS", 500 * GiB, 400 * GiB, 100 * GiB, 20.0, false, true, true, true)
            };

            var snapshot = new WorkspaceStateSnapshot(
                RefreshedAtUtc: DateTimeOffset.UtcNow,
                SystemDrive: "C:",
                IsAdministrator: true,
                IsDeveloperModeEnabled: false,
                Drives: drives,
                Backups: new SharedBackupSummary(2, 0, 14L * GiB, true, new Dictionary<string, int> { ["C:"] = 2 }),
                Quarantine: new SharedQuarantineSummary(3, 256L * 1024 * 1024, new Dictionary<string, (int, long)>()));

            var cache = new SharedWorkspaceStateCache(TimeSpan.FromMinutes(5), _ => snapshot);
            OverviewQuickMigrateSummary currentPreset = new(IsReady: true, PendingCount: 0, MigratedCount: 0, MatchedGroupNames: []);
            IReadOnlyList<MigrationTransactionRecord> currentRecords = [];
            string? scannedDriveRoot = null;

            var vm = new OverviewViewModel(
                openDiskScanCallback: root => scannedDriveRoot = root,
                workspaceCache: cache,
                readTransactionsAsync: () => Task.FromResult(currentRecords),
                readQuickMigrateSummary: () => currentPreset,
                readScanSnapshots: () => []);

            await vm.OnEnteredOverviewPageAsync(force: true);

            // 1. 磁盘卡片上限 4 张、系统盘 C: 置顶且告警、多出 1 块盘提示
            Require(vm.HasDrives && vm.VisibleDrives.Count == 4 && vm.DriveColumns == 4,
                $"Expected 4 capped drive cards, got {vm.VisibleDrives.Count}");
            Require(vm.VisibleDrives[0].DriveLetter == "C:" && vm.VisibleDrives[0].IsCritical,
                $"System drive C: should be first and critical, got {vm.VisibleDrives[0].DriveLetter} critical={vm.VisibleDrives[0].IsCritical}");
            Require(vm.HasExtraDrives && vm.ExtraDrivesHintText.Contains("1"),
                $"Expected extra drive hint for 1 extra drive, got '{vm.ExtraDrivesHintText}'");
            Require(vm.TargetReadinessText.Contains("4"), $"Expected 4 target NTFS drives, got '{vm.TargetReadinessText}'");

            // 2. 备份与隔离区待办摘要
            Require(vm.HasUnverifiedBackups && vm.UnverifiedBackupCount == 2 && vm.BackupSummaryTitle.Contains("2 份"),
                $"Unexpected backup summary: '{vm.BackupSummaryTitle}'");
            Require(vm.HasQuarantineItems && vm.QuarantineBatchCount == 3 && vm.QuarantineSummaryTitle.Contains("3 个批次"),
                $"Unexpected quarantine summary: '{vm.QuarantineSummaryTitle}'");

            // 3. 普通电脑未命中玲珑星核预设 -> 低调收敛
            Require(!vm.HasLingLongPresets && vm.LingLongActionText == "查看配置",
                $"Unmatched LingLong preset should stay subtle, got has={vm.HasLingLongPresets} action='{vm.LingLongActionText}'");

            // 4. 命中玲珑星核预设 -> 高亮突出
            currentPreset = new OverviewQuickMigrateSummary(IsReady: true, PendingCount: 2, MigratedCount: 1, MatchedGroupNames: ["智玲同学", "模型管理"]);
            vm.RefreshQuickMigrateSummary();
            Require(vm.HasLingLongPresets && vm.LingLongActionText == "立即迁移" && vm.LingLongDescription.Contains("智玲同学"),
                $"Matched LingLong preset should highlight, got action='{vm.LingLongActionText}' desc='{vm.LingLongDescription}'");

            // 5. 无事务记录 -> 显示新手 3 步安全指引
            Require(!vm.HasRecentActivities && vm.ShowOnboardingGuide,
                "Empty transaction history must show 3-step onboarding guide");

            // 6. 有 5 条事务记录 -> 仅展示最近 4 条并自动隐藏新手指引
            DateTimeOffset now = DateTimeOffset.UtcNow;
            currentRecords = Enumerable.Range(1, 5).Select(i => new MigrationTransactionRecord
            {
                TransactionId = Guid.NewGuid(),
                Mode = i == 5 ? MigrationMode.Restore : MigrationMode.Migrate,
                Stage = MigrationStage.AwaitingUserVerification,
                SourcePath = $@"C:\Users\Demo\Folder{i}",
                TargetPath = $@"D:\Migrated\Folder{i}",
                BackupPath = $@"C:\Users\Demo\Folder{i}.bak",
                VerificationMode = VerificationMode.Basic,
                BackupState = i == 4 ? BackupState.Deleted : BackupState.RetainedUnverified,
                UserVerifiedAtUtc = i == 4 ? now : null,
                UpdatedAtUtc = now.AddMinutes(-i)
            }).ToArray();

            await vm.OnEnteredOverviewPageAsync(force: true);
            Require(vm.HasRecentActivities && !vm.ShowOnboardingGuide && vm.RecentActivities.Count == 4,
                $"Expected 4 recent activities, got {vm.RecentActivities.Count}");
            Require(vm.RecentActivities[0].DisplayTitle.Contains("Folder1"),
                $"Most recent activity should be Folder1, got '{vm.RecentActivities[0].DisplayTitle}'");

            // 7. 点击磁盘卡片去扫描透传 driveRoot
            vm.OpenDiskScanCommand.Execute(@"C:\");
            Require(string.Equals(scannedDriveRoot, @"C:\", StringComparison.OrdinalIgnoreCase),
                $"Expected OpenDiskScan callback to receive C:\\, got '{scannedDriveRoot}'");
        });

        await test("OverviewHome-wpf-ui-smoke-and-default-landing", () =>
        {
            Exception? staError = null;
            var thread = new Thread(() =>
            {
                try
                {
                    if (Application.Current is null)
                    {
                        var app = new Application();
                        app.Resources.MergedDictionaries.Add(new ResourceDictionary
                        {
                            Source = new Uri("pack://application:,,,/MoveWithSymlinkWPF;component/Resources/FluentTheme.xaml")
                        });
                        app.Resources.MergedDictionaries.Add(new ResourceDictionary
                        {
                            Source = new Uri("pack://application:,,,/MoveWithSymlinkWPF;component/Resources/AppIcon.xaml")
                        });
                    }

                    // 1. 验证非 TestLab 默认落到「概览」首页
                    var mainVm = new MainViewModel();
                    mainVm.IsMigrating = false;
                    Require(mainVm.IsOverviewMode && mainVm.OverviewNavTag == "Active" && !mainVm.IsManualMode,
                        $"App should land on Overview page by default (IsOverview={mainVm.IsOverviewMode}, IsManual={mainVm.IsManualMode})");
                    Require(mainVm.OverviewPage is OverviewPage,
                        "MainViewModel.OverviewPage must instantiate OverviewPage");

                    // 2. 验证 OverviewPage 在默认窗口视口(1100x636)下无垂直滚动条，且全屏宽屏(1920x980)下画布限宽居中(<=1360)
                    var overviewPage = (OverviewPage)mainVm.OverviewPage!;
                    overviewPage.Measure(new Size(1100, 636));
                    overviewPage.Arrange(new Rect(0, 0, 1100, 636));
                    overviewPage.UpdateLayout();
                    var scrollViewer = (System.Windows.Controls.ScrollViewer)overviewPage.FindName("OverviewScrollViewer")!;
                    var contentCanvas = (System.Windows.FrameworkElement)overviewPage.FindName("OverviewContentCanvas")!;
                    Require(scrollViewer.ExtentHeight <= scrollViewer.ViewportHeight + 1.0,
                        $"OverviewPage should fit default window viewport without vertical scrollbar (Extent={scrollViewer.ExtentHeight:F1}, Viewport={scrollViewer.ViewportHeight:F1})");

                    overviewPage.Measure(new Size(1920, 980));
                    overviewPage.Arrange(new Rect(0, 0, 1920, 980));
                    overviewPage.UpdateLayout();
                    Require(contentCanvas.ActualWidth <= 1360.1,
                        $"OverviewPage content canvas must clamp width on ultrawide/fullscreen (ActualWidth={contentCanvas.ActualWidth:F1})");

                    // 3. 验证通过概览页快捷命令跳转各模块，再返回概览页
                    mainVm.OverviewViewModel.OpenDiskScanCommand.Execute(@"C:\");
                    Require(mainVm.IsDiskScanMode && mainVm.DiskScanNavTag == "Active" && !mainVm.IsOverviewMode,
                        "Overview OpenDiskScanCommand must switch to DiskScan mode");

                    mainVm.ShowOverviewCommand.Execute(null);
                    Require(mainVm.IsOverviewMode && mainVm.OverviewNavTag == "Active",
                        "ShowOverviewCommand must switch back to Overview mode");

                    mainVm.OverviewViewModel.OpenManualModeCommand.Execute(null);
                    Require(mainVm.IsManualMode && mainVm.ManualNavTag == "Active",
                        "Overview OpenManualModeCommand must switch to Manual mode");

                    mainVm.OverviewViewModel.OpenQuickMigrateCommand.Execute(null);
                    Require(mainVm.IsQuickMigrateMode && mainVm.QuickMigrateNavTag == "Active",
                        "Overview OpenQuickMigrateCommand must switch to QuickMigrate mode");

                    mainVm.OverviewViewModel.OpenAiAssistantCommand.Execute(null);
                    Require(mainVm.IsAiAssistantMode && mainVm.AiAssistantNavTag == "Active",
                        "Overview OpenAiAssistantCommand must switch to AiAssistant mode");

                    mainVm.OverviewViewModel.OpenBackupCenterCommand.Execute(null);
                    Require(mainVm.IsBackupCenterMode && mainVm.BackupCenterNavTag == "Active",
                        "Overview OpenBackupCenterCommand must switch to BackupCenter mode");

                    mainVm.OverviewViewModel.OpenQuarantineCommand.Execute(null);
                    Require(mainVm.IsCleanupMode && mainVm.CleanupNavTag == "Active",
                        "Overview OpenQuarantineCommand must switch to Cleanup Quarantine view");
                    if (mainVm.CleanupPage is CleanupPage cp)
                    {
                        Require(cp.ViewModel.IsQuarantineView,
                            "OpenQuarantineCommand must open Quarantine subview inside CleanupPage");
                    }
                }
                catch (Exception ex)
                {
                    staError = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (staError is not null)
            {
                throw new InvalidOperationException($"Overview WPF UI smoke failed: {staError.Message}", staError);
            }

            return Task.CompletedTask;
        });
    }
}
