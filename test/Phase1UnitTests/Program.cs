using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MigrationCore.Models;
using MigrationCore.Models.Safety;
using MigrationCore.Models.Scanning;
using MigrationCore.Services;
using MigrationCore.Services.Safety;
using MigrationCore.Services.Scanning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MoveWithSymlinkWPF;
using MoveWithSymlinkWPF.Helpers;
using MoveWithSymlinkWPF.ViewModels;
using MoveWithSymlinkWPF.Views;

internal static class Program
{
    private sealed record GroupResult(string Group, int Passed, int Failed, string[] Failures);

    private static async Task<int> Main()
    {
        var results = new List<GroupResult>
        {
            RunGroup("PolicyCases", RunPolicyCases),
            RunGroup("MftParserCases", RunMftParserCases),
            await RunGroupAsync("DirectoryWalkCases", RunDirectoryWalkCasesAsync),
            RunGroup("CatalogCases", RunCatalogCases),
            RunGroup("BatchPlanCases", RunBatchPlanCases),
            await RunGroupAsync("ConcurrencyCases", RunConcurrencyCasesAsync),
            RunGroup("ExitConfirmCases", RunExitConfirmCases),
            RunGroup("WpfUiSmokeCases", RunWpfUiSmokeCases),
            await RunGroupAsync("Phase2AiCases", Phase2AiCases.RunAsync)
        };

        int totalPassed = results.Sum(r => r.Passed);
        int totalFailed = results.Sum(r => r.Failed);

        foreach (GroupResult g in results)
        {
            string tag = g.Failed == 0 ? "PASSED" : "FAILED";
            Console.WriteLine($"[{tag}] {g.Group}: {g.Passed} passed, {g.Failed} failed");
            foreach (string err in g.Failures)
            {
                Console.WriteLine($"  - {err}");
            }
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            status = totalFailed == 0 ? "Passed" : "Failed",
            totalPassed,
            totalFailed,
            groups = results
        }, new JsonSerializerOptions { WriteIndented = false }));

        return totalFailed == 0 ? 0 : 1;
    }

    private static GroupResult RunGroup(string name, Action<Action<string, Action>> body)
    {
        int passed = 0;
        var failures = new List<string>();
        body((caseName, test) =>
        {
            try { test(); passed++; }
            catch (Exception ex) { failures.Add($"{caseName}: {ex.Message}"); }
        });
        return new GroupResult(name, passed, failures.Count, failures.ToArray());
    }

    private static async Task<GroupResult> RunGroupAsync(string name, Func<Func<string, Func<Task>, Task>, Task> body)
    {
        int passed = 0;
        var failures = new List<string>();
        await body(async (caseName, test) =>
        {
            try { await test(); passed++; }
            catch (Exception ex) { failures.Add($"{caseName}: {ex.Message}"); }
        });
        return new GroupResult(name, passed, failures.Count, failures.ToArray());
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // ==================== 1. PolicyCases ====================
    private static void RunPolicyCases(Action<string, Action> test)
    {
        var nonCEnv = new SafetyPolicyEnvironment(
            WindowsDirectories: [@"E:\Windows"],
            ProgramFilesDirectories: [@"E:\Program Files", @"E:\Program Files (x86)"],
            ProgramDataDirectories: [@"E:\ProgramData"],
            UsersRoots: [@"E:\Users"],
            UserProfileRoots: [@"E:\Users\Alice"],
            AppDataRoots: [@"E:\Users\Alice\AppData", @"E:\Users\Alice\AppData\Local", @"E:\Users\Alice\AppData\Roaming", @"E:\Users\Alice\AppData\LocalLow"],
            ToolAndStateDirectories: [@"D:\Tools\MoveFolder", @"E:\Users\Alice\AppData\Local\MoveWithSymlinkWPF\State\Transactions"],
            IsFixedNtfsVolume: _ => true);

        test("L0-non-C-windows-and-reserved-blocked", () =>
        {
            string[] blocked =
            [
                @"E:\",
                @"E:\Windows",
                @"E:\Windows\System32",
                @"E:\Windows.old\Users",
                @"D:\Boot\BCD",
                @"D:\Recovery\WindowsRE",
                @"D:\$Recycle.Bin\S-1-5-21",
                @"D:\System Volume Information",
                @"D:\.MoveFolderQuarantine\item1",
                @"E:\Program Files",
                @"E:\Program Files (x86)",
                @"E:\Program Files\WindowsApps\App1",
                @"E:\Program Files\Common Files\Microsoft Shared",
                @"E:\ProgramData",
                @"E:\ProgramData\Microsoft\Crypto",
                @"E:\Users",
                @"E:\Users\Alice",
                @"E:\Users\Alice\AppData",
                @"E:\Users\Alice\AppData\Local\Programs",
                @"E:\Users\Alice\AppData\Local\Programs\Common",
                @"E:\Users\Alice\AppData\Local\Programs\Common\Sub",
                @"E:\Users\Alice\AppData\Local\VirtualStore\App",
                @"E:\Users\Alice\AppData\Local\D3DSCache",
                @"E:\ProgramData\USOShared\Update",
                @"E:\Users\Alice\AppData\Local\Microsoft\Edge",
                @"E:\Users\Alice\AppData\Local\Packages\StoreApp",
                @"D:\Tools\MoveFolder\sub",
                @"D:\Tools", // 工具本体父目录（FIND-03 双向重叠拦截）
                @"E:\Users\OtherUser", // 多用户 Profile 根（FIND-03）
                @"E:\Users\Default\AppData\Local\Microsoft\Windows", // Default 用户受保护子树（FIND-03）
                @"D:\Games\MyApp.bak_11111111-2222-3333-4444-555555555555",
                @"D:\Security\CrowdStrike\Logs"
            ];

            foreach (string p in blocked)
            {
                SafetyPolicyDecision d = MigrationSafetyPolicy.EvaluateL0HardDeny(p, checkFilesystem: false, environment: nonCEnv);
                Require(d.IsHardDenied, $"Expected L0 HardDenied for {p}, got {d.Level}");
            }
        });

        test("similar-prefix-WindowsTools-not-misblocked", () =>
        {
            SafetyPolicyDecision d = MigrationSafetyPolicy.EvaluateSource(@"E:\WindowsTools\MyAssets", matcher: null, context: SafetyEvaluationContext.ScanPreview, checkFilesystem: false, environment: nonCEnv);
            Require(!d.IsHardDenied && d.Level == MigrationAdmissionLevel.ManualRiskConfirm,
                $"E:\\WindowsTools should not be misclassified as E:\\Windows (got {d.Level}: {d.Summary})");
        });

        test("ProgramFiles-root-denied-but-L1-subdir-allowed", () =>
        {
            var catalog = new ApplicationCatalog(
                BuiltInApplicationRules.All,
                [new InstalledSoftwareEvidence("NovaStudio", "2.0", "Nova", @"E:\Program Files\NovaStudio", ApplicationEvidenceSource.RegistryUninstall64)]);

            SafetyPolicyDecision rootDecision = MigrationSafetyPolicy.EvaluateSource(@"E:\Program Files", catalog, SafetyEvaluationContext.ScanPreview, checkFilesystem: false, environment: nonCEnv);
            Require(rootDecision.IsHardDenied, "Program Files container root must be L0 HardDenied");

            SafetyPolicyDecision dataDecision = MigrationSafetyPolicy.EvaluateSource(@"E:\Program Files\NovaStudio\comfyui\models", catalog, SafetyEvaluationContext.ScanPreview, checkFilesystem: false, environment: nonCEnv);
            Require(dataDecision.Level == MigrationAdmissionLevel.VerifiedEligible && dataDecision.IsAllowedForNewMigration,
                $"Expected L1 VerifiedEligible for approved NovaStudio models subdir, got {dataDecision.Level}: {dataDecision.Summary}");

            SafetyPolicyDecision appRootDecision = MigrationSafetyPolicy.EvaluateSource(@"E:\Program Files\NovaStudio", catalog, SafetyEvaluationContext.ScanPreview, checkFilesystem: false, environment: nonCEnv);
            Require(appRootDecision.Level == MigrationAdmissionLevel.ManualRiskConfirm,
                $"NovaStudio install root itself must not receive L1 (got {appRootDecision.Level})");
        });

        test("RestoreOrRepair-only-enforces-L0", () =>
        {
            SafetyPolicyDecision restorePersonal = MigrationSafetyPolicy.EvaluateSource(@"D:\MyData\Video", matcher: null, context: SafetyEvaluationContext.RestoreOrRepair, checkFilesystem: false, environment: nonCEnv);
            Require(restorePersonal.IsAllowedForRestoreOrRepair && !restorePersonal.IsHardDenied, "Restore/Repair on non-L0 path must be allowed");

            SafetyPolicyDecision restoreSystem = MigrationSafetyPolicy.EvaluateSource(@"E:\Windows\System32", matcher: null, context: SafetyEvaluationContext.RestoreOrRepair, checkFilesystem: false, environment: nonCEnv);
            Require(restoreSystem.IsHardDenied && !restoreSystem.IsAllowedForRestoreOrRepair, "Restore/Repair must still reject L0 system paths");
        });

        test("HasUserContent-and-IsTargetDirectoryEmpty-fail-closed-on-ACL-deny", () =>
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "MoveFolder_P1Policy_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string originalSddl = new DirectoryInfo(tempDir).GetAccessControl(AccessControlSections.Access)
                .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            try
            {
                var denied = new DirectoryInfo(tempDir).GetAccessControl(AccessControlSections.Access);
                denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
                new DirectoryInfo(tempDir).SetAccessControl(denied);

                Require(PathValidator.HasUserContent(tempDir), "HasUserContent must fail closed (return true) when directory cannot be enumerated.");
                var (isEmpty, err) = PathValidator.IsTargetDirectoryEmpty(tempDir);
                Require(!isEmpty && !string.IsNullOrWhiteSpace(err), "IsTargetDirectoryEmpty must return false when directory cannot be enumerated.");

                // FIND-10: 目标路径为普通文件时不得误判为空目录
                string fileTarget = Path.Combine(Path.GetTempPath(), "MoveFolder_P1File_" + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(fileTarget, "occupied");
                try
                {
                    var fileCheck = PathValidator.IsTargetDirectoryEmpty(fileTarget);
                    Require(!fileCheck.IsEmpty, "IsTargetDirectoryEmpty must reject an existing regular file.");
                }
                finally { File.Delete(fileTarget); }
            }
            finally
            {
                var restored = new DirectorySecurity();
                restored.SetSecurityDescriptorSddlForm(originalSddl, AccessControlSections.Access);
                new DirectoryInfo(tempDir).SetAccessControl(restored);
                Directory.Delete(tempDir, recursive: true);
            }
        });

        test("known-directory-explainer-covers-personal-system-and-dev-folders", () =>
        {
            var catalog = new ApplicationCatalog(BuiltInApplicationRules.All, []);

            // 1. Windows 个人 Known Folders（Desktop/Documents/Downloads/Videos/Pictures/Music）
            var desktop = KnownDirectoryExplainer.Explain(@"C:\Users\alice\Desktop", catalog);
            var documents = KnownDirectoryExplainer.Explain(@"C:\Users\alice\Documents", catalog);
            var downloads = KnownDirectoryExplainer.Explain(@"C:\Users\alice\Downloads", catalog);
            var videos = KnownDirectoryExplainer.Explain(@"C:\Users\alice\Videos", catalog);
            var pictures = KnownDirectoryExplainer.Explain(@"C:\Users\alice\Pictures", catalog);
            var music = KnownDirectoryExplainer.Explain(@"C:\Users\alice\Music", catalog);

            Require(desktop is not null && desktop.RoleTitle.Contains("桌面") && desktop.MigrationAdvice.Contains("OneDrive") && desktop.HintLevel == DirectoryHintLevel.Warning, "Desktop explanation/hint mismatch");
            Require(documents is not null && documents.RoleTitle.Contains("文档") && documents.MigrationAdvice.Contains("位置") && documents.HintLevel == DirectoryHintLevel.Info, "Documents explanation/hint mismatch");
            Require(downloads is not null && downloads.RoleTitle.Contains("下载") && downloads.Purpose.Contains("安装包") && downloads.HintLevel == DirectoryHintLevel.Info, "Downloads explanation/hint mismatch");
            Require(videos is not null && videos.RoleTitle.Contains("视频") && videos.HintLevel == DirectoryHintLevel.Info, "Videos explanation/hint mismatch");
            Require(pictures is not null && pictures.RoleTitle.Contains("图片") && pictures.HintLevel == DirectoryHintLevel.Info, "Pictures explanation/hint mismatch");
            Require(music is not null && music.RoleTitle.Contains("音乐") && music.HintLevel == DirectoryHintLevel.Info, "Music explanation/hint mismatch");

            // 2. 系统与 AppData 容器（含 Local\Programs、Temp、Packages）及工具自身保护说明
            var programs = KnownDirectoryExplainer.Explain(@"C:\Users\alice\AppData\Local\Programs", catalog);
            var temp = KnownDirectoryExplainer.Explain(@"C:\Users\alice\AppData\Local\Temp", catalog);
            var packages = KnownDirectoryExplainer.Explain(@"C:\Users\alice\AppData\Local\Packages", catalog);
            var usersRoot = KnownDirectoryExplainer.Explain(@"C:\Users", catalog);
            var selfProtectDecision = new SafetyPolicyDecision(
                MigrationAdmissionLevel.HardDenied, false, false, [SafetyReasonCodes.L0SelfOrActiveTransaction], MigrationSafetyPolicy.CurrentPolicyVersion, "self");
            var selfProtectExplain = KnownDirectoryExplainer.Explain(@"C:\ProjectDev", catalog, selfProtectDecision);

            Require(programs is not null && programs.RoleTitle.Contains("Programs") && programs.HintLevel == DirectoryHintLevel.Warning, "Local\\Programs explanation/hint mismatch");
            Require(temp is not null && temp.RoleTitle.Contains("临时文件") && temp.HintLevel == DirectoryHintLevel.Info, "Local\\Temp explanation/hint mismatch");
            Require(packages is not null && packages.RoleTitle.Contains("UWP") && packages.HintLevel == DirectoryHintLevel.None, "Local\\Packages explanation/hint mismatch");
            Require(usersRoot is not null && usersRoot.HintLevel == DirectoryHintLevel.None, "Users container should stay clean without extra row icon");
            Require(selfProtectExplain is not null && selfProtectExplain.RoleTitle.Contains("运行中的迁移工具") && selfProtectExplain.HintLevel == DirectoryHintLevel.Warning,
                "L0SelfOrActiveTransaction must produce Warning explanation");

            // 2b. 系统保护优先于工具保护：Users / Profile / AppData 即使包含工具事务状态目录，也必须归为系统保护而非工具保护
            foreach (string sysContainer in new[]
            {
                @"E:\Users",
                @"E:\Users\Alice",
                @"E:\Users\Alice\AppData",
                @"E:\Users\Alice\AppData\Local"
            })
            {
                var sysDecision = MigrationSafetyPolicy.EvaluateL0HardDeny(sysContainer, checkFilesystem: false, environment: nonCEnv);
                Require(sysDecision.IsHardDenied
                    && !sysDecision.ReasonCodes.Contains(SafetyReasonCodes.L0SelfOrActiveTransaction, StringComparer.OrdinalIgnoreCase),
                    $"System container '{sysContainer}' must prioritize system protection over tool protection, got [{string.Join(',', sysDecision.ReasonCodes)}]");
            }
            var toolDirDecision = MigrationSafetyPolicy.EvaluateL0HardDeny(@"E:\Users\Alice\AppData\Local\MoveWithSymlinkWPF", checkFilesystem: false, environment: nonCEnv);
            Require(toolDirDecision.IsHardDenied
                && toolDirDecision.ReasonCodes.Contains(SafetyReasonCodes.L0SelfOrActiveTransaction, StringComparer.OrdinalIgnoreCase),
                "Non-system tool directory must still be classified as tool protection (L0SelfOrActiveTransaction)");
            var toolParentDecision = MigrationSafetyPolicy.EvaluateL0HardDeny(@"D:\Tools", checkFilesystem: false, environment: nonCEnv);
            Require(toolParentDecision.IsHardDenied
                && toolParentDecision.ReasonCodes.Contains(SafetyReasonCodes.L0SelfOrActiveTransaction, StringComparer.OrdinalIgnoreCase),
                "Non-system parent directory containing tool binary must be classified as tool protection (L0SelfOrActiveTransaction)");

            // 3. 高频软件/AI/开发/聊天/虚拟磁盘目录，且普通项目名为 Docker 的目录不被误判为 Docker Desktop
            var jetbrainsLocal = KnownDirectoryExplainer.Explain(@"C:\Users\alice\AppData\Local\JetBrains", catalog);
            var jetbrainsRoaming = KnownDirectoryExplainer.Explain(@"C:\Users\alice\AppData\Roaming\JetBrains", catalog);
            var npmCache = KnownDirectoryExplainer.Explain(@"C:\Users\alice\AppData\Local\npm-cache", catalog);
            var lmStudio = KnownDirectoryExplainer.Explain(@"C:\Users\alice\.lmstudio", catalog);
            var wechat = KnownDirectoryExplainer.Explain(@"C:\Users\alice\Documents\WeChat Files", catalog);
            var dockerAppData = KnownDirectoryExplainer.Explain(@"C:\Users\alice\AppData\Local\Docker", catalog);
            var dockerProject = KnownDirectoryExplainer.Explain(@"C:\ProjectDev\Docker\my-app", catalog);

            Require(jetbrainsLocal is not null && jetbrainsLocal.RoleTitle.Contains("本地索引") && jetbrainsLocal.HintLevel == DirectoryHintLevel.Info, "JetBrains Local explanation mismatch");
            Require(jetbrainsRoaming is not null && jetbrainsRoaming.RoleTitle.Contains("用户配置") && jetbrainsRoaming.HintLevel == DirectoryHintLevel.Warning, "JetBrains Roaming explanation mismatch");
            Require(npmCache is not null && npmCache.RoleTitle.Contains("包管理器") && npmCache.HintLevel == DirectoryHintLevel.Info, "npm-cache explanation mismatch");
            Require(lmStudio is not null && lmStudio.RoleTitle.Contains("AI 模型") && lmStudio.HintLevel == DirectoryHintLevel.Info, ".lmstudio explanation mismatch");
            Require(wechat is not null && wechat.RoleTitle.Contains("微信") && wechat.HintLevel == DirectoryHintLevel.Info, "WeChat Files explanation mismatch");
            Require(dockerAppData is not null && dockerAppData.RoleTitle.Contains("Docker") && dockerAppData.HintLevel == DirectoryHintLevel.Warning, "AppData Docker explanation mismatch");
            Require(dockerProject is not null && dockerProject.RoleTitle.Contains("普通本地目录") && dockerProject.HintLevel == DirectoryHintLevel.None, "User project under Docker folder should not be misclassified as Docker Desktop");
        });
    }

    // ==================== 2. MftParserCases ====================
    private static void RunMftParserCases(Action<string, Action> test)
    {
        test("USA-fixup-valid-and-torn-write-corrupt", () =>
        {
            byte[] rec = BuildSyntheticMftRecord(1024, 512, usn: 0x1234, inUse: true, isDir: false, seq: 1);
            Require(NtfsMftRecordParser.ApplyUsaFixup(rec, 512), "Valid 1KB MFT record should pass USA fixup");

            byte[] torn = BuildSyntheticMftRecord(1024, 512, usn: 0x1234, inUse: true, isDir: false, seq: 1);
            // 破坏第 2 扇区末 2 字节模拟撕裂写
            torn[1022] = 0xFF;
            torn[1023] = 0xFF;
            Require(!NtfsMftRecordParser.ApplyUsaFixup(torn, 512), "Torn write record must fail USA fixup");
        });

        test("RunList-fragmented-sparse-and-overflow-guard", () =>
        {
            // Run1: len=4 (1B), off=+10 (1B) => LCN 10, count 4
            // Run2: sparse len=2 (1B), off=0B => IsSparse, count 2
            // Run3: len=3 (1B), off=-4 (1B, 0xFC) => LCN 6, count 3
            byte[] validRuns = [0x11, 0x04, 0x0A, 0x01, 0x02, 0x11, 0x03, 0xFC, 0x00];
            Require(NtfsMftRecordParser.TryParseRunList(validRuns, out var runs) && runs.Count == 3, "Expected 3 parsed runs");
            Require(runs[0] == new MftDataRun(10, 4, false), "Run 0 mismatch");
            Require(runs[1] == new MftDataRun(-1, 2, true), "Run 1 sparse mismatch");
            Require(runs[2] == new MftDataRun(6, 3, false), "Run 2 negative delta mismatch");

            // 恶意截断/超长字段不得越界抛异常
            byte[] malformed = [0x99, 0x01, 0x02];
            Require(!NtfsMftRecordParser.TryParseRunList(malformed, out _), "Malformed run list length > 8 must return false");
        });

        test("Parse-resident-ADS-WOF-Cloud-HardLink-Extension-Orphan-and-CSR", () =>
        {
            var records = new Dictionary<ulong, ParsedMftRecord>();

            // Record 5: 根目录
            records[5] = new ParsedMftRecord { RecordNumber = 5, SequenceNumber = 5, InUse = true, IsDirectory = true };

            // Record 20: 目录 DirA (parent=5)
            var dirA = new ParsedMftRecord { RecordNumber = 20, SequenceNumber = 1, InUse = true, IsDirectory = true };
            dirA.FileNames.Add(new ParsedFileNameEntry(5, 5, 1, "DirA"));
            records[20] = dirA;

            // Record 21: 目录 DirB (parent=5)
            var dirB = new ParsedMftRecord { RecordNumber = 21, SequenceNumber = 1, InUse = true, IsDirectory = true };
            dirB.FileNames.Add(new ParsedFileNameEntry(5, 5, 1, "DirB"));
            records[21] = dirB;

            // Record 30: 常驻小文件（Logical=128, Allocated=0, Resident=true）
            byte[] rec30Buf = BuildRecordWithAttributes(1024, 512, seq: 2, isDir: false,
                BuildFileNameAttrBytes(20, 1, 1, "tiny.txt"),
                BuildResidentDataAttrBytes("", 128));
            Require(NtfsMftRecordParser.ApplyUsaFixup(rec30Buf, 512), "rec30 fixup failed");
            ParsedMftRecord rec30 = NtfsMftRecordParser.ParseFixedUpRecord(rec30Buf, 30);
            Require(rec30.IsResidentDefaultData && rec30.DefaultDataLogicalBytes == 128 && rec30.DefaultDataAllocatedBytes == 0,
                "Resident file must report Logical=128 and Allocated=0");
            records[30] = rec30;

            // Record 31: WOF CompactOS 压缩文件（Reparse=0x80000017, WofCompressedData=4096）
            byte[] rec31Buf = BuildRecordWithAttributes(1024, 512, seq: 1, isDir: false,
                BuildFileNameAttrBytes(20, 1, 1, "compact.bin"),
                BuildNonResidentDataAttrBytes("", logicalSize: 65536, allocatedSize: 0),
                BuildNonResidentDataAttrBytes("WofCompressedData", logicalSize: 4096, allocatedSize: 4096),
                BuildReparseAttrBytes(NtfsMftRecordParser.IoReparseTagWof));
            Require(NtfsMftRecordParser.ApplyUsaFixup(rec31Buf, 512), "rec31 fixup failed");
            ParsedMftRecord rec31 = NtfsMftRecordParser.ParseFixedUpRecord(rec31Buf, 31);
            Require(rec31.IsWofCompressed && rec31.WofCompressedAllocatedBytes == 4096, "WOF compressed size mismatch");
            records[31] = rec31;

            // Record 32: OneDrive 云占位文件（Reparse=0x9000101A, Offline, Logical=100000, Allocated=0）
            byte[] rec32Buf = BuildRecordWithAttributes(1024, 512, seq: 1, isDir: false,
                BuildStandardInfoAttrBytes(dosAttributes: 0x00001000),
                BuildFileNameAttrBytes(20, 1, 1, "cloud.mp4"),
                BuildNonResidentDataAttrBytes("", logicalSize: 100_000, allocatedSize: 0),
                BuildReparseAttrBytes(0x9000101A));
            Require(NtfsMftRecordParser.ApplyUsaFixup(rec32Buf, 512), "rec32 fixup failed");
            ParsedMftRecord rec32 = NtfsMftRecordParser.ParseFixedUpRecord(rec32Buf, 32);
            Require(rec32.IsCloudPlaceholder && rec32.DefaultDataLogicalBytes == 100_000 && rec32.DefaultDataAllocatedBytes == 0,
                "Cloud placeholder detection mismatch");
            records[32] = rec32;

            // Record 33 + 扩展 Record 34：硬链接跨 DirA 和 DirB（含同父 DOS 8.3 短名过滤 + 命名 ADS）
            var rec33 = new ParsedMftRecord
            {
                RecordNumber = 33, SequenceNumber = 3, InUse = true, IsDirectory = false,
                DefaultDataLogicalBytes = 8192, DefaultDataAllocatedBytes = 8192
            };
            rec33.FileNames.Add(new ParsedFileNameEntry(20, 1, 2, "HARDLI~1.DAT")); // 同父 DOS 短名，应被滤除
            rec33.FileNames.Add(new ParsedFileNameEntry(20, 1, 1, "hardlink_a.dat"));
            records[33] = rec33;

            var ext34 = new ParsedMftRecord
            {
                RecordNumber = 34, SequenceNumber = 1, InUse = true, IsDirectory = false,
                BaseRecordNumber = 33, BaseSequenceNumber = 3,
                AdsLogicalBytes = 256, AdsAllocatedBytes = 4096, HasAlternateDataStreams = true
            };
            ext34.FileNames.Add(new ParsedFileNameEntry(21, 1, 1, "hardlink_b.dat"));
            records[34] = ext34;

            // Record 35: 孤立节点（指向不存在的父记录 999）
            var orphan35 = new ParsedMftRecord
            {
                RecordNumber = 35, SequenceNumber = 1, InUse = true, IsDirectory = false,
                DefaultDataLogicalBytes = 500, DefaultDataAllocatedBytes = 4096
            };
            orphan35.FileNames.Add(new ParsedFileNameEntry(999, 1, 1, "lost.dat"));
            records[35] = orphan35;

            var vol = new ScanVolumeInfo(@"Z:\", "Test", @"\\?\Volume{11111111-1111-1111-1111-111111111111}\", 1234, "NTFS", 10_000_000, 9_000_000, true, "OK");
            ScanSnapshot snap = NtfsMftRecordParser.BuildSnapshotFromParsedRecords(
                vol, records, DateTimeOffset.UtcNow, MigrationSafetyPolicy.CurrentPolicyVersion, 0, null, null, null, true, false);

            // 验证硬链接两个名字都保留，且仅一个计入 UniqueAllocatedBytes（8192+4096=12288）
            var hardLinkNodes = snap.Nodes.Where(n => n.MftRecordNumber == 33).ToArray();
            Require(hardLinkNodes.Length == 2, $"Expected 2 hard link nodes (DOS filtered), got {hardLinkNodes.Length}");
            Require(hardLinkNodes.Count(n => n.IsHardLinkSecondary) == 1, "Exactly one hard link entry must be marked secondary");
            Require(hardLinkNodes.Sum(n => n.UniqueAllocatedBytes) == 12288, $"UniqueAllocatedBytes for hardlink should be 12288, got {hardLinkNodes.Sum(n => n.UniqueAllocatedBytes)}");

            // 验证孤立节点进入虚拟“未解析位置”并标 IsOrphaned
            Require(snap.OrphanContainerNodeId >= 0, "Orphan container must be created");
            Require(snap.GetNodeName(snap.OrphanContainerNodeId) == "未解析位置", "Orphan container name mismatch");

            // 验证卷根汇总不重复计入硬链接：WOF(4096) + HardLink(12288) + Orphan(4096) = 20480
            Require(snap.Nodes[0].UniqueAllocatedBytes == 20480, $"Root UniqueAllocatedBytes expected 20480, got {snap.Nodes[0].UniqueAllocatedBytes}");

            // 验证子树危害检测能检出硬链接
            int dirANode = snap.Nodes.First(n => n.MftRecordNumber == 20).NodeId;
            var hazards = snap.InspectSubtreeHazards(dirANode);
            Require(hazards.HasHardLinks, "DirA contains a hard link and must report HasHardLinks=true");
        });

        test("Utf8NamePool-cross-page-allocation", () =>
        {
            var pool = new Utf8NamePool();
            string chunk = new string('测', 1000); // 3000 UTF-8 bytes
            (byte page, int offset, ushort len) last = default;
            for (int i = 0; i < 1500; i++) // ~4.5 MiB -> 跨入第 2 页
            {
                last = pool.AddName(chunk.AsSpan());
            }
            Require(pool.PageCount >= 2, $"Expected >= 2 pages, got {pool.PageCount}");
            Require(pool.GetString(last.page, last.offset, last.len) == chunk, "Cross-page UTF-8 roundtrip mismatch");
        });

        test("FIND-01-sparse-compressionUnit0-offset64-not-misread-and-FIND-13-cycle-recovery", () =>
        {
            // FIND-01: 非驻留属性含 AttrFlagSparse(0x8000) 且 compressionUnit == 0，Offset 64 处是 Data Run 字节（非 CompressedSize）
            byte[] sparseAttr = BuildNonResidentDataAttrBytes("", logicalSize: 16384, allocatedSize: 4096);
            BinaryPrimitives.WriteUInt16LittleEndian(sparseAttr.AsSpan(12, 2), 0x8000); // AttrFlagSparse
            // 在 Offset 64（runOffset）写入非零 run 字节，若误当 int64 会是巨大数字
            BinaryPrimitives.WriteInt64LittleEndian(sparseAttr.AsSpan(64, 8), 0x0000_7FFF_1234_5678L);
            byte[] recBuf = BuildRecordWithAttributes(1024, 512, seq: 1, isDir: false,
                BuildFileNameAttrBytes(5, 5, 1, "sparse_cu0.bin"),
                sparseAttr);
            Require(NtfsMftRecordParser.ApplyUsaFixup(recBuf, 512), "sparse_cu0 fixup failed");
            ParsedMftRecord parsed = NtfsMftRecordParser.ParseFixedUpRecord(recBuf, 40);
            Require(parsed.DefaultDataAllocatedBytes == 4096,
                $"FIND-01 regression: compressionUnit==0 sparse stream misread Offset 64 as {parsed.DefaultDataAllocatedBytes}");

            // FIND-13: 损坏 MFT 中父子成环目录（50 <-> 51）不得静默消失，应重挂至“未解析位置”并计入汇总
            var cyclicRecords = new Dictionary<ulong, ParsedMftRecord>
            {
                [5] = new ParsedMftRecord { RecordNumber = 5, SequenceNumber = 5, InUse = true, IsDirectory = true },
                [50] = new ParsedMftRecord { RecordNumber = 50, SequenceNumber = 1, InUse = true, IsDirectory = true },
                [51] = new ParsedMftRecord { RecordNumber = 51, SequenceNumber = 1, InUse = true, IsDirectory = true },
                [52] = new ParsedMftRecord { RecordNumber = 52, SequenceNumber = 1, InUse = true, IsDirectory = false, DefaultDataLogicalBytes = 4096, DefaultDataAllocatedBytes = 4096 }
            };
            cyclicRecords[50].FileNames.Add(new ParsedFileNameEntry(51, 1, 1, "CycleA"));
            cyclicRecords[51].FileNames.Add(new ParsedFileNameEntry(50, 1, 1, "CycleB"));
            cyclicRecords[52].FileNames.Add(new ParsedFileNameEntry(50, 1, 1, "trapped.bin"));

            var vol = new ScanVolumeInfo(@"Z:\", "Test", "", 1, "NTFS", 10_000_000, 9_000_000, true, "OK");
            ScanSnapshot snap = NtfsMftRecordParser.BuildSnapshotFromParsedRecords(
                vol, cyclicRecords, DateTimeOffset.UtcNow, MigrationSafetyPolicy.CurrentPolicyVersion, 0, null, null, null, true, false);
            Require(snap.OrphanContainerNodeId >= 0 && snap.Coverage == ScanCoverageStatus.Partial,
                "FIND-13: Cyclic subtree must be attached to orphan container and mark Partial");
            Require(snap.Nodes[0].UniqueAllocatedBytes == 4096,
                $"FIND-13: Trapped file in cycle must remain accounted (expected 4096, got {snap.Nodes[0].UniqueAllocatedBytes})");
        });

        test("MFT-extend-metafile-children-excluded-from-orphan-container", () =>
        {
            var records = new Dictionary<ulong, ParsedMftRecord>
            {
                [5] = new ParsedMftRecord { RecordNumber = 5, SequenceNumber = 5, InUse = true, IsDirectory = true },
                [11] = new ParsedMftRecord { RecordNumber = 11, SequenceNumber = 1, InUse = true, IsDirectory = true },
                [40] = new ParsedMftRecord { RecordNumber = 40, SequenceNumber = 1, InUse = true, IsDirectory = true },
                [41] = new ParsedMftRecord { RecordNumber = 41, SequenceNumber = 1, InUse = true, IsDirectory = false, DefaultDataLogicalBytes = 33_000_000, DefaultDataAllocatedBytes = 33_000_000 },
                [42] = new ParsedMftRecord { RecordNumber = 42, SequenceNumber = 1, InUse = true, IsDirectory = false, DefaultDataLogicalBytes = 12_000_000, DefaultDataAllocatedBytes = 12_000_000 },
                [50] = new ParsedMftRecord { RecordNumber = 50, SequenceNumber = 1, InUse = true, IsDirectory = true },
                [51] = new ParsedMftRecord { RecordNumber = 51, SequenceNumber = 1, InUse = true, IsDirectory = false, DefaultDataLogicalBytes = 4096, DefaultDataAllocatedBytes = 4096 }
            };
            records[11].FileNames.Add(new ParsedFileNameEntry(5, 5, 1, "$Extend"));
            records[40].FileNames.Add(new ParsedFileNameEntry(11, 1, 1, "$RmMetadata"));
            records[41].FileNames.Add(new ParsedFileNameEntry(11, 1, 1, "$UsnJrnl"));
            records[42].FileNames.Add(new ParsedFileNameEntry(40, 1, 1, "$TxfLog"));
            records[50].FileNames.Add(new ParsedFileNameEntry(5, 5, 1, "Games"));
            records[51].FileNames.Add(new ParsedFileNameEntry(50, 1, 1, "game.pak"));

            var vol = new ScanVolumeInfo(@"C:\", "Win", @"\\?\Volume{test}\", 1, "NTFS", 100_000_000, 50_000_000, true, "OK");
            ScanSnapshot snap = NtfsMftRecordParser.BuildSnapshotFromParsedRecords(
                vol, records, DateTimeOffset.UtcNow, MigrationSafetyPolicy.CurrentPolicyVersion, 0, null, null, null, false, false);

            Require(snap.OrphanContainerNodeId == -1, "$Extend children must not create '未解析位置' orphan container");
            Require(snap.Coverage == ScanCoverageStatus.Complete, $"Expected Complete coverage, got {snap.Coverage}");
            Require(snap.MetadataBytes >= 45_000_000, $"Expected $UsnJrnl+$TxfLog in MetadataBytes, got {snap.MetadataBytes}");
            Require(snap.Nodes.All(n => !snap.GetNodeName(n.NodeId).StartsWith("$", StringComparison.Ordinal)), "NTFS $Extend metafiles must not appear in user tree");
        });

        test("MFT-dev-cache-roundtrip-preserves-tree-and-forces-reverify", () =>
        {
            string cacheDir = Path.Combine(Path.GetTempPath(), "MoveFolderDevCacheTest_" + Guid.NewGuid().ToString("N"));
            try
            {
                ScanSnapshot original = BuildNestedHotspotSnapshot();
                string file = ScanSnapshotDevCache.Save(original, "模拟系统盘 (C:)", largeDirCount: 3, l1Count: 1, l1Bytes: 200L * 1024 * 1024 * 1024, cacheDir);
                Require(File.Exists(file), "Dev cache file was not written");

                var headers = ScanSnapshotDevCache.ListValidHeaders(original.PolicyVersion, TimeSpan.FromHours(1), cacheDir);
                Require(headers.Count == 1 && headers[0].NodeCount == original.Nodes.Length && headers[0].LargeDirCount == 3,
                    "Dev cache header listing mismatch");

                ScanSnapshot loaded = ScanSnapshotDevCache.Load(file, out DevScanCacheHeader loadedHeader);
                Require(loadedHeader.ScanId == original.ScanId && loaded.Nodes.Length == original.Nodes.Length, "Loaded node count mismatch");
                Require(loaded.RequiresLiveReverification, "Loaded dev cache snapshot must enforce RequiresLiveReverification = true");
                Require(loaded.GetFullPath(10) == original.GetFullPath(10), $"Loaded path mismatch: {loaded.GetFullPath(10)}");
                Require(loaded.GetChildrenSpan(4).Length == original.GetChildrenSpan(4).Length, "Loaded CSR children mismatch");

                ScanSnapshotDevCache.ClearAll(cacheDir);
                Require(ScanSnapshotDevCache.ListValidHeaders(original.PolicyVersion, TimeSpan.FromHours(1), cacheDir).Count == 0, "ClearAll did not remove cache");
            }
            finally
            {
                try { if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true); } catch { }
            }
        });
    }

    // ==================== 3. DirectoryWalkCases ====================
    private static async Task RunDirectoryWalkCasesAsync(Func<string, Func<Task>, Task> test)
    {
        await test("DirectoryWalk-fallback-no-reparse-follow-and-ACL-partial", async () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "MoveFolder_P1Walk_" + Guid.NewGuid().ToString("N"));
            string normalDir = Path.Combine(root, "Normal");
            string secretTarget = Path.Combine(root, "OutsideTarget");
            string junctionDir = Path.Combine(normalDir, "JunctionToOutside");
            string deniedDir = Path.Combine(root, "Denied");
            Directory.CreateDirectory(normalDir);
            Directory.CreateDirectory(secretTarget);
            Directory.CreateDirectory(deniedDir);
            File.WriteAllBytes(Path.Combine(normalDir, "a.bin"), new byte[1024]);
            File.WriteAllBytes(Path.Combine(secretTarget, "secret.bin"), new byte[8192]);

            // 用 mklink /J 创建目录联接（无需管理员权限）
            using (var proc = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junctionDir}\" \"{secretTarget}\"")
            {
                CreateNoWindow = true, UseShellExecute = false
            })!)
            {
                await proc.WaitForExitAsync();
            }

            string originalSddl = new DirectoryInfo(deniedDir).GetAccessControl(AccessControlSections.Access)
                .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            try
            {
                var deniedAcl = new DirectoryInfo(deniedDir).GetAccessControl(AccessControlSections.Access);
                deniedAcl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
                new DirectoryInfo(deniedDir).SetAccessControl(deniedAcl);

                var vol = new ScanVolumeInfo(Path.GetPathRoot(root)!, "Temp", "", 0, "NTFS", 100_000_000, 50_000_000, false, "测试降级遍历");
                var walker = new DirectoryWalkScanner();
                ScanSnapshot snap = await walker.ScanAsync(vol, rootDirectoryOverride: root, fallbackReason: "测试降级");

                Require(snap.Backend == ScanBackendKind.DirectoryWalkFallback, "Walker must declare DirectoryWalkFallback");
                Require(snap.Coverage == ScanCoverageStatus.Partial && snap.ErrorCount >= 1, "ACL denied dir must yield Partial coverage & ErrorCount >= 1");

                // 验证 JunctionToOutside 标记了 IsReparsePoint、准确记录 ReparseTag 且没有把 secret.bin (8192) 重复算入 Normal
                int normalId = snap.Nodes.First(n => snap.GetNodeName(n.NodeId) == "Normal").NodeId;
                int juncId = snap.Nodes.First(n => snap.GetNodeName(n.NodeId) == "JunctionToOutside").NodeId;
                Require(snap.Nodes[juncId].IsReparsePoint && snap.GetChildrenSpan(juncId).Length == 0, "Junction must not be traversed");
                Require(snap.Nodes[juncId].ReparseTag == 0xA0000003u,
                    $"Junction should expose IO_REPARSE_TAG_MOUNT_POINT (0xA0000003), got 0x{snap.Nodes[juncId].ReparseTag:X8}");
                Require(snap.Nodes[normalId].LogicalBytes == 1024, $"Normal dir should only count a.bin (1024), got {snap.Nodes[normalId].LogicalBytes}");
                // FIND-14: 子目录覆盖扫描 GetFullPath 必须包含子目录根前缀
                Require(snap.GetFullPath(normalId).Equals(normalDir, StringComparison.OrdinalIgnoreCase),
                    $"FIND-14: Expected GetFullPath={normalDir}, got {snap.GetFullPath(normalId)}");
            }
            finally
            {
                var restored = new DirectorySecurity();
                restored.SetSecurityDescriptorSddlForm(originalSddl, AccessControlSections.Access);
                new DirectoryInfo(deniedDir).SetAccessControl(restored);
                if (Directory.Exists(junctionDir)) Directory.Delete(junctionDir, false);
                Directory.Delete(root, true);
            }
        });

        await test("DirectoryWalk-concurrent-deep-tree-and-real-sparse-allocation", async () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "MoveFolder_P1WalkConc_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                long expectedLogical = 0;
                int expectedFiles = 0;

                // 构造多分支深层目录树，验证多线程并发队列汇总确定性与路径拼接
                for (int b = 0; b < 6; b++)
                {
                    string branch = Path.Combine(root, $"Branch_{b}", "Sub_A", "Sub_B");
                    Directory.CreateDirectory(branch);
                    for (int f = 0; f < 10; f++)
                    {
                        int size = (b + 1) * 100 + f;
                        File.WriteAllBytes(Path.Combine(branch, $"file_{f}.dat"), new byte[size]);
                        expectedLogical += size;
                        expectedFiles++;
                    }
                }

                // 构造一个 1 MiB 逻辑大小但零实际磁盘分配的稀疏文件，验证梯度二真实 AllocationSize
                string sparsePath = Path.Combine(root, "sparse_zero.bin");
                const long sparseLogicalSize = 1024 * 1024;
                bool sparseMarked;
                using (var fs = new FileStream(sparsePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    sparseMarked = Win32SparseHelper.TryMarkSparse(fs.SafeFileHandle);
                    fs.SetLength(sparseLogicalSize);
                    fs.Flush(flushToDisk: true);
                }
                expectedLogical += sparseLogicalSize;
                expectedFiles++;

                var vol = new ScanVolumeInfo(Path.GetPathRoot(root)!, "Temp", "", 0, "ReFS", 100_000_000, 50_000_000, false, "测试 ReFS 并发扫描");
                var walker = new DirectoryWalkScanner();
                var options = new DirectoryWalkOptions(MaxDegreeOfParallelism: 4, BufferSizeBytes: 64 * 1024, UseBackgroundIoPriority: true);
                ScanSnapshot snap = await walker.ScanAsync(vol, rootDirectoryOverride: root, fallbackReason: "ReFS 并发测试", options: options);

                Require(snap.Coverage == ScanCoverageStatus.Complete && snap.ErrorCount == 0,
                    $"Expected Complete with 0 errors, got {snap.Coverage} / {snap.ErrorCount}");
                Require(snap.Nodes[0].FileCount == expectedFiles,
                    $"Expected {expectedFiles} files, got {snap.Nodes[0].FileCount}");
                Require(snap.Nodes[0].LogicalBytes == expectedLogical,
                    $"Expected logical {expectedLogical}, got {snap.Nodes[0].LogicalBytes}");

                int sparseId = snap.Nodes.First(n => snap.GetNodeName(n.NodeId) == "sparse_zero.bin").NodeId;
                Require(snap.Nodes[sparseId].LogicalBytes == sparseLogicalSize,
                    $"Sparse file logical size mismatch: {snap.Nodes[sparseId].LogicalBytes}");
                if (sparseMarked)
                {
                    Require(snap.Nodes[sparseId].IsSparseOrCompressed, "Sparse file should set IsSparseOrCompressed");
                    Require(snap.Nodes[sparseId].AllocatedBytes == 0,
                        $"Zero-filled sparse file should report AllocatedBytes=0, got {snap.Nodes[sparseId].AllocatedBytes}");
                }

                // 验证预取消令牌按标准异步契约抛出 OperationCanceledException
                using var cancelledCts = new CancellationTokenSource();
                cancelledCts.Cancel();
                bool threwCancelled = false;
                try
                {
                    await walker.ScanAsync(vol, rootDirectoryOverride: root, fallbackReason: "取消测试", options: options, ct: cancelledCts.Token);
                }
                catch (OperationCanceledException)
                {
                    threwCancelled = true;
                }
                Require(threwCancelled, "Pre-cancelled scan must throw OperationCanceledException");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        });

        await test("DirectoryWalk-buffer-parser-WOF-cloud-and-media-throttling-policy", async () =>
        {
            await Task.CompletedTask;

            // 1. 验证不打满磁盘的保守并发度策略
            Require(DirectoryWalkScanner.ResolveWorkerCount(StorageMediaKind.Hdd, null) == 1,
                "HDD default concurrency must be 1 to avoid disk thrashing");
            Require(DirectoryWalkScanner.ResolveWorkerCount(StorageMediaKind.Unknown, null) == 1,
                "Unknown media default concurrency must conservatively be 1");
            int ssdWorkers = DirectoryWalkScanner.ResolveWorkerCount(StorageMediaKind.Ssd, null);
            Require(ssdWorkers is >= 2 and <= 4, $"SSD default concurrency must stay in [2..4], got {ssdWorkers}");

            // 2. 构造合成 FILE_FULL_DIR_INFO 连续缓冲区，验证 "."/".." 过滤、WOF 压缩、云占位与普通文件解包
            var raw = new List<byte>();
            static void AppendEntry(List<byte> buf, string name, uint attrs, long logical, long alloc, uint eaSize, bool isLast)
            {
                byte[] nameBytes = System.Text.Encoding.Unicode.GetBytes(name);
                int entryLen = Win32DirectoryEnumerator.FullDirInfoHeaderSize + nameBytes.Length;
                int alignedLen = (entryLen + 7) & ~7;
                byte[] chunk = new byte[isLast ? entryLen : alignedLen];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(0, 4), isLast ? 0u : (uint)alignedLen);
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(chunk.AsSpan(24, 8), DateTime.UtcNow.ToFileTimeUtc());
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(chunk.AsSpan(40, 8), logical);
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(chunk.AsSpan(48, 8), alloc);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(56, 4), attrs);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(60, 4), (uint)nameBytes.Length);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(64, 4), eaSize);
                nameBytes.CopyTo(chunk, Win32DirectoryEnumerator.FullDirInfoHeaderSize);
                buf.AddRange(chunk);
            }

            AppendEntry(raw, ".", Win32DirectoryEnumerator.FILE_ATTRIBUTE_DIRECTORY, 0, 0, 0, isLast: false);
            AppendEntry(raw, "..", Win32DirectoryEnumerator.FILE_ATTRIBUTE_DIRECTORY, 0, 0, 0, isLast: false);
            AppendEntry(raw, "wof.dll", Win32DirectoryEnumerator.FILE_ATTRIBUTE_REPARSE_POINT, 100_000, 32_768, NtfsMftRecordParser.IoReparseTagWof, isLast: false);
            AppendEntry(raw, "cloud.mp4",
                Win32DirectoryEnumerator.FILE_ATTRIBUTE_REPARSE_POINT | Win32DirectoryEnumerator.FILE_ATTRIBUTE_OFFLINE | Win32DirectoryEnumerator.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS,
                500_000_000, 500_000_000, 0x9000101Au, isLast: false);
            AppendEntry(raw, "normal.bin", 0x20, 4096, 4096, 0, isLast: true);

            byte[] buffer = raw.ToArray();
            var parsed = new List<ParsedDirEntrySlice>();
            Require(Win32DirectoryEnumerator.ParseFullDirectoryInfoBuffer(buffer, parsed), "Synthetic FILE_FULL_DIR_INFO buffer should parse cleanly");
            Require(parsed.Count == 3, $"Should skip '.' and '..' and yield 3 entries, got {parsed.Count}");

            string name0 = System.Text.Encoding.Unicode.GetString(buffer, parsed[0].NameOffset, parsed[0].NameByteLength);
            Require(name0 == "wof.dll" && (parsed[0].Flags & ScanNodeFlags.IsWofCompressed) != 0 && parsed[0].AllocatedBytes == 32_768,
                "WOF entry must set IsWofCompressed and preserve real AllocationSize");

            string name1 = System.Text.Encoding.Unicode.GetString(buffer, parsed[1].NameOffset, parsed[1].NameByteLength);
            Require(name1 == "cloud.mp4" && (parsed[1].Flags & ScanNodeFlags.IsCloudPlaceholder) != 0 && parsed[1].AllocatedBytes == 0 && parsed[1].LogicalBytes == 500_000_000,
                "Dehydrated cloud placeholder must report AllocatedBytes=0 while keeping LogicalBytes");

            string name2 = System.Text.Encoding.Unicode.GetString(buffer, parsed[2].NameOffset, parsed[2].NameByteLength);
            Require(name2 == "normal.bin" && parsed[2].AllocatedBytes == 4096 && parsed[2].LastWriteUtcTicks > 0,
                "Normal entry must preserve size and timestamp");
        });
    }

    // ==================== 4. CatalogCases ====================
    private static void RunCatalogCases(Action<string, Action> test)
    {
        var catalog = new ApplicationCatalog(
            BuiltInApplicationRules.All,
            [
                new InstalledSoftwareEvidence("NovaStudio 智玲同学", "1.5", "Nova", @"D:\Apps\NovaStudio", ApplicationEvidenceSource.RegistryCurrentUser),
                new InstalledSoftwareEvidence("NovaMax", "2.0", "LingLong", @"D:\Apps\NovaMax", ApplicationEvidenceSource.RegistryUninstall64),
                new InstalledSoftwareEvidence("Docker Desktop", "4.35", "Docker Inc.", @"C:\Program Files\Docker\Docker", ApplicationEvidenceSource.RegistryUninstall64),
                new InstalledSoftwareEvidence("NovaStudio Fake", "1.0", "UntrustedVendor", @"D:\FakeApps\Spoofed", ApplicationEvidenceSource.RegistryUninstall32)
            ]);

        test("Nova-business-data-L1-and-runtime-excluded", () =>
        {
            var m1 = catalog.Match(@"D:\Apps\NovaStudio\comfyui\models");
            Require(m1 is not null && m1.RecommendedLevel == MigrationAdmissionLevel.VerifiedEligible, "NovaStudio models must be L1");

            var m2 = catalog.Match(@"D:\Apps\NovaMax\knowledge");
            Require(m2 is not null && m2.RecommendedLevel == MigrationAdmissionLevel.VerifiedEligible, "NovaMax knowledge must be L1");

            var runtime = catalog.Match(@"D:\Apps\NovaStudio\python");
            Require(runtime is null, "NovaStudio python runtime must NOT receive L1");

            var installRoot = catalog.Match(@"D:\Apps\NovaStudio");
            Require(installRoot is null, "NovaStudio install root itself must NOT receive L1");
        });

        test("Docker-and-OneDrive-L2-native-guidance", () =>
        {
            var docker = catalog.Match(@"D:\Users\Bob\AppData\Local\Docker\wsl\disk");
            Require(docker is not null && docker.RecommendedLevel == MigrationAdmissionLevel.VendorNativeRecommended && !string.IsNullOrWhiteSpace(docker.NativeGuidance),
                "Docker WSL disk must match L2 VendorNativeRecommended");

            var onedrive = catalog.Match(@"D:\Users\Bob\OneDrive\Docs");
            Require(onedrive is not null && onedrive.RecommendedLevel == MigrationAdmissionLevel.VendorNativeRecommended,
                "OneDrive must match L2 VendorNativeRecommended");
        });

        test("Spoofed-publisher-uninstall-not-granted-L1", () =>
        {
            var spoof = catalog.Match(@"D:\FakeApps\Spoofed\models");
            Require(spoof is null, "Spoofed publisher uninstall entry must not be granted L1");

            // FIND-12: 便携版 D:\Steam\steamapps\common 必须命中 steam-library L1
            var steam = catalog.Match(@"D:\Steam\steamapps\common");
            Require(steam is not null && steam.ApplicationId == "steam-library" && steam.RecommendedLevel == MigrationAdmissionLevel.VerifiedEligible,
                "FIND-12: D:\\Steam\\steamapps\\common must match steam-library L1");
        });
    }

    // ==================== 5. BatchPlanCases ====================
    private static void RunBatchPlanCases(Action<string, Action> test)
    {
        var nonCEnv = new SafetyPolicyEnvironment(
            WindowsDirectories: [@"C:\Windows"],
            ProgramFilesDirectories: [@"C:\Program Files"],
            ProgramDataDirectories: [@"C:\ProgramData"],
            UsersRoots: [@"C:\Users"],
            UserProfileRoots: [@"C:\Users\Test"],
            AppDataRoots: [@"C:\Users\Test\AppData"],
            ToolAndStateDirectories: [@"C:\Tools\MoveFolder"],
            IsFixedNtfsVolume: _ => true);

        var catalog = new ApplicationCatalog(BuiltInApplicationRules.All, []);
        var planService = new MigrationPlanService(catalog, nonCEnv);

        test("Target-path-avoids-basename-collision", () =>
        {
            string t1 = MigrationPlanService.BuildDefaultTargetPath(@"D:\ProjA\models", @"E:\Migrated", null);
            string t2 = MigrationPlanService.BuildDefaultTargetPath(@"D:\ProjB\models", @"E:\Migrated", null);
            Require(!t1.Equals(t2, StringComparison.OrdinalIgnoreCase), $"Targets collided: {t1} == {t2}");
        });

        test("Overlap-and-collision-and-L3-ack-blocked", () =>
        {
            // 父子重叠
            var overlapPlan = planService.FreezeAndValidatePlan(
                [
                    new MigrationPlanDraftItem(@"D:\Data\Parent", @"E:\Mig\P1", 1024, 1024, L3RiskAcknowledged: true),
                    new MigrationPlanDraftItem(@"D:\Data\Parent\Child", @"E:\Mig\P2", 512, 512, L3RiskAcknowledged: true)
                ],
                VerificationMode.Basic,
                checkFilesystem: false,
                volumeAvailableBytesProvider: _ => 100L * 1024 * 1024 * 1024);
            Require(overlapPlan.HasBlockingIssues, "Parent-child source overlap must be blocked");

            // 多源撞同一目标
            var collidePlan = planService.FreezeAndValidatePlan(
                [
                    new MigrationPlanDraftItem(@"D:\Data\A", @"E:\Mig\Same", 1024, 1024, L3RiskAcknowledged: true),
                    new MigrationPlanDraftItem(@"D:\Data\B", @"E:\Mig\Same", 1024, 1024, L3RiskAcknowledged: true)
                ],
                VerificationMode.Basic,
                checkFilesystem: false,
                volumeAvailableBytesProvider: _ => 100L * 1024 * 1024 * 1024);
            Require(collidePlan.HasBlockingIssues, "Target collision must be blocked");

            // L3 未勾选风险确认
            var noAckPlan = planService.FreezeAndValidatePlan(
                [new MigrationPlanDraftItem(@"D:\Personal\Movies", @"E:\Mig\Movies", 1024, 1024, L3RiskAcknowledged: false)],
                VerificationMode.Basic,
                checkFilesystem: false,
                volumeAvailableBytesProvider: _ => 100L * 1024 * 1024 * 1024);
            Require(noAckPlan.HasBlockingIssues, "L3 item without risk acknowledgement must be blocked");
        });

        test("PlanDigest-changes-on-mode-or-target-and-readonly-process-preflight-blocks", () =>
        {
            var draft = new[] { new MigrationPlanDraftItem(@"D:\Apps\NovaStudio\comfyui\models", @"E:\Mig\NS_Models", 10_000, 10_000, L3RiskAcknowledged: true) };
            var planBasic = planService.FreezeAndValidatePlan(draft, VerificationMode.Basic, checkFilesystem: false, volumeAvailableBytesProvider: _ => 10L * 1024 * 1024 * 1024);
            var planHash = planService.FreezeAndValidatePlan(draft, VerificationMode.ContentHash, checkFilesystem: false, volumeAvailableBytesProvider: _ => 10L * 1024 * 1024 * 1024);

            Require(planBasic.PlanDigest != planHash.PlanDigest, "Changing VerificationMode must invalidate PlanDigest");
            Require(!planBasic.HasBlockingIssues, $"Valid L1 plan should be ready, got: {string.Join(';', planBasic.BlockingIssues)}");

            BatchPreflightReport blocked = planService.RunReadOnlyProcessPreflight(
                planBasic,
                runningProcessProvider: () => ["explorer", "NovaStudio"],
                volumeAvailableBytesProvider: _ => 10L * 1024 * 1024 * 1024);
            Require(!blocked.CanStart && blocked.RunningBlockingProcesses.Contains("NovaStudio"),
                "Running associated process must block preflight without killing process");

            BatchPreflightReport passed = planService.RunReadOnlyProcessPreflight(
                planBasic,
                runningProcessProvider: () => ["explorer"],
                volumeAvailableBytesProvider: _ => 10L * 1024 * 1024 * 1024);
            Require(passed.CanStart, $"Preflight should pass when process stopped, got: {string.Join(';', passed.BlockingIssues)}");
        });
    }

    // ==================== 6. ConcurrencyCases ====================
    private static async Task RunConcurrencyCasesAsync(Func<string, Func<Task>, Task> test)
    {
        await test("OperationExecutionGate-exclusive-and-release", async () =>
        {
            var gate = new OperationExecutionGate();
            var lease1 = await gate.TryAcquireAsync("TestOp", "任务1");
            Require(lease1 is not null && gate.IsBusy, "First acquire should succeed");

            var lease2 = await gate.TryAcquireAsync("TestOp2", "任务2");
            Require(lease2 is null, "Concurrent second acquire must be rejected");

            lease1!.Dispose();
            Require(!gate.IsBusy, "Gate must become idle after lease dispose");

            using var lease3 = await gate.TryAcquireAsync("TestOp3", "任务3");
            Require(lease3 is not null && gate.IsBusy, "Acquire after release should succeed");
        });

        await test("TransactionStore-lock-retry-survives-brief-sharing-violation", async () =>
        {
            string tempState = Path.Combine(Path.GetTempPath(), "MoveFolder_P1Lock_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempState);
            try
            {
                var store = new MigrationTransactionStore(tempState);
                string lockFile = Path.Combine(tempState, ".transactions.lock");

                using var blocker = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var releaseTask = Task.Run(async () =>
                {
                    await Task.Delay(80);
                    blocker.Dispose();
                });

                var records = await store.ReadAllAsync();
                await releaseTask;
                Require(records.Length == 0, "ReadAllAsync should succeed after bounded retry releases .transactions.lock");
            }
            finally
            {
                Directory.Delete(tempState, recursive: true);
            }
        });
    }

    // ==================== 7. ExitConfirmCases ====================
    private static void RunExitConfirmCases(Action<string, Action> test)
    {
        test("Same-source-multiple-unverified-backups-all-detected", () =>
        {
            var id1 = Guid.NewGuid();
            var id2 = Guid.NewGuid();
            var rec1 = new MigrationTransactionRecord
            {
                TransactionId = id1,
                Mode = MigrationMode.Migrate,
                Stage = MigrationStage.AwaitingUserVerification,
                SourcePath = @"D:\Data\App",
                TargetPath = @"E:\Mig\App",
                BackupPath = @"D:\Data\App.bak_" + id1.ToString("D"),
                BackupState = BackupState.RetainedUnverified,
                UserVerifiedAtUtc = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10)
            };
            var rec2 = new MigrationTransactionRecord
            {
                TransactionId = id2,
                Mode = MigrationMode.Restore,
                Stage = MigrationStage.AwaitingUserVerification,
                SourcePath = @"D:\Data\App",
                TargetPath = @"E:\Mig\App",
                BackupPath = @"E:\Mig\App.bak_" + id2.ToString("D"),
                BackupState = BackupState.RetainedUnverified,
                UserVerifiedAtUtc = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };

            var pending = UnverifiedExitConfirmation.SelectPendingUnverified([rec1, rec2], directoryExists: _ => true);
            Require(pending.Length == 2, $"Expected both live unverified backups to be reported, got {pending.Length}");
        });
    }

    // ==================== 合成 MFT 二进制测试辅助方法 ====================
    private static byte[] BuildSyntheticMftRecord(int recordSize, int sectorSize, ushort usn, bool inUse, bool isDir, ushort seq)
    {
        byte[] buf = new byte[recordSize];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), NtfsMftRecordParser.FileSignature);
        ushort usaOffset = 48;
        ushort usaCount = (ushort)(1 + recordSize / sectorSize);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4, 2), usaOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(6, 2), usaCount);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(16, 2), seq);
        ushort firstAttr = (ushort)((usaOffset + usaCount * 2 + 7) & ~7);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(20, 2), firstAttr);
        ushort flags = (ushort)((inUse ? 1 : 0) | (isDir ? 2 : 0));
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(22, 2), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(24, 4), (uint)(firstAttr + 4));
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(28, 4), (uint)recordSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(firstAttr, 4), NtfsMftRecordParser.AttrEndMarker);

        ApplyTestUsa(buf, sectorSize, usaOffset, usn);
        return buf;
    }

    private static byte[] BuildRecordWithAttributes(int recordSize, int sectorSize, ushort seq, bool isDir, params byte[][] attributes)
    {
        byte[] buf = new byte[recordSize];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), NtfsMftRecordParser.FileSignature);
        ushort usaOffset = 48;
        ushort usaCount = (ushort)(1 + recordSize / sectorSize);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4, 2), usaOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(6, 2), usaCount);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(16, 2), seq);
        int pos = (usaOffset + usaCount * 2 + 7) & ~7;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(20, 2), (ushort)pos);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(22, 2), (ushort)(1 | (isDir ? 2 : 0)));

        foreach (byte[] attr in attributes)
        {
            attr.CopyTo(buf, pos);
            pos += attr.Length;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(pos, 4), NtfsMftRecordParser.AttrEndMarker);
        pos += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(24, 4), (uint)pos);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(28, 4), (uint)recordSize);

        ApplyTestUsa(buf, sectorSize, usaOffset, 0x55AA);
        return buf;
    }

    private static void ApplyTestUsa(byte[] buf, int sectorSize, ushort usaOffset, ushort usn)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(usaOffset, 2), usn);
        int sectors = buf.Length / sectorSize;
        for (int s = 0; s < sectors; s++)
        {
            int tail = (s + 1) * sectorSize - 2;
            ushort orig = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(tail, 2));
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(usaOffset + (s + 1) * 2, 2), orig);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(tail, 2), usn);
        }
    }

    private static byte[] BuildStandardInfoAttrBytes(uint dosAttributes)
    {
        byte[] body = new byte[48];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(32, 4), dosAttributes);
        return WrapResidentAttr(NtfsMftRecordParser.AttrStandardInformation, "", body);
    }

    private static byte[] BuildFileNameAttrBytes(ulong parentRec, ushort parentSeq, byte ns, string name)
    {
        byte[] nameBytes = Encoding.Unicode.GetBytes(name);
        byte[] body = new byte[66 + nameBytes.Length];
        ulong parentRef = (parentRec & 0x0000_FFFF_FFFF_FFFFUL) | ((ulong)parentSeq << 48);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(0, 8), parentRef);
        body[64] = (byte)name.Length;
        body[65] = ns;
        nameBytes.CopyTo(body, 66);
        return WrapResidentAttr(NtfsMftRecordParser.AttrFileName, "", body);
    }

    private static byte[] BuildResidentDataAttrBytes(string streamName, int payloadLen) =>
        WrapResidentAttr(NtfsMftRecordParser.AttrData, streamName, new byte[payloadLen]);

    private static byte[] BuildNonResidentDataAttrBytes(string streamName, long logicalSize, long allocatedSize)
    {
        byte[] nameBytes = Encoding.Unicode.GetBytes(streamName);
        int headerLen = 64;
        int nameOffset = headerLen;
        int runOffset = (nameOffset + nameBytes.Length + 7) & ~7;
        int totalLen = (runOffset + 8 + 7) & ~7;
        byte[] attr = new byte[totalLen];
        BinaryPrimitives.WriteUInt32LittleEndian(attr.AsSpan(0, 4), NtfsMftRecordParser.AttrData);
        BinaryPrimitives.WriteUInt32LittleEndian(attr.AsSpan(4, 4), (uint)totalLen);
        attr[8] = 1; // Non-resident
        attr[9] = (byte)streamName.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(attr.AsSpan(10, 2), (ushort)nameOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(attr.AsSpan(32, 2), (ushort)runOffset);
        BinaryPrimitives.WriteInt64LittleEndian(attr.AsSpan(40, 8), allocatedSize);
        BinaryPrimitives.WriteInt64LittleEndian(attr.AsSpan(48, 8), logicalSize);
        BinaryPrimitives.WriteInt64LittleEndian(attr.AsSpan(56, 8), logicalSize);
        nameBytes.CopyTo(attr, nameOffset);
        attr[runOffset] = 0; // End run
        return attr;
    }

    private static byte[] BuildReparseAttrBytes(uint reparseTag)
    {
        byte[] body = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0, 4), reparseTag);
        return WrapResidentAttr(NtfsMftRecordParser.AttrReparsePoint, "", body);
    }

    private static byte[] WrapResidentAttr(uint attrType, string name, byte[] body)
    {
        byte[] nameBytes = Encoding.Unicode.GetBytes(name);
        int nameOffset = 24;
        int valueOffset = (nameOffset + nameBytes.Length + 7) & ~7;
        int totalLen = (valueOffset + body.Length + 7) & ~7;
        byte[] attr = new byte[totalLen];
        BinaryPrimitives.WriteUInt32LittleEndian(attr.AsSpan(0, 4), attrType);
        BinaryPrimitives.WriteUInt32LittleEndian(attr.AsSpan(4, 4), (uint)totalLen);
        attr[8] = 0; // Resident
        attr[9] = (byte)name.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(attr.AsSpan(10, 2), (ushort)nameOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(attr.AsSpan(16, 4), (uint)body.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(attr.AsSpan(20, 2), (ushort)valueOffset);
        nameBytes.CopyTo(attr, nameOffset);
        body.CopyTo(attr, valueOffset);
        return attr;
    }

    // ==================== 8. WpfUiSmokeCases (STA 运行时渲染 + RenderTransform 动画回归检测) ====================
    private static void RunWpfUiSmokeCases(Action<string, Action> test)
    {
        test("WPF-pages-layout-expand-and-render-transform-safety", () =>
        {
            Exception? captured = null;
            var staThread = new Thread(() =>
            {
                string tempRoot = Path.Combine(Path.GetTempPath(), "MoveFolderUiSmoke_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(Path.Combine(tempRoot, "Programs", "ComfyUI", "models"));
                    File.WriteAllBytes(Path.Combine(tempRoot, "Programs", "ComfyUI", "models", "weights.bin"), new byte[2 * 1024 * 1024]);
                    Directory.CreateDirectory(Path.Combine(tempRoot, "Programs", "OtherTool", "data"));
                    File.WriteAllBytes(Path.Combine(tempRoot, "Programs", "OtherTool", "data", "blob.bin"), new byte[2 * 1024 * 1024]);

                    var app = Application.Current as App ?? new App();
                    if (app.Resources.MergedDictionaries.Count == 0)
                    {
                        app.InitializeComponent();
                    }

                    // 1. 实例化并布局全局备份页与磁盘扫描页（已加载 App.xaml 全部隐式样式与 FluentTheme.xaml）
                    var backupPage = new GlobalBackupPage();
                    backupPage.Measure(new Size(1000, 640));
                    backupPage.Arrange(new Rect(0, 0, 1000, 640));
                    backupPage.UpdateLayout();
                    AssertAllButtonsSafeForRenderTransform(backupPage);

                    var scanPage = new DiskScanPage();
                    var scanVm = (DiskScanViewModel)scanPage.DataContext;

                    // 2. 用真实目录遍历生成快照并触发 00 -> 01 -> 展开文件夹 -> 02 -> 04 全四阶段布局
                    var walker = new DirectoryWalkScanner();
                    var volInfo = new ScanVolumeInfo(
                        DriveRoot: tempRoot,
                        VolumeLabel: "TestVol",
                        VolumeGuid: @"\\?\Volume{smoke}\",
                        VolumeSerial: 1,
                        FileSystem: "NTFS",
                        TotalBytes: 100L * 1024 * 1024 * 1024,
                        FreeBytes: 60L * 1024 * 1024 * 1024,
                        SupportsRawMft: false,
                        CapabilityReason: "UnitTest");
                    ScanSnapshot snap = walker.ScanAsync(volInfo, rootDirectoryOverride: tempRoot, fallbackReason: "ui-smoke").GetAwaiter().GetResult();

                    // 通过会话历史入口灌入快照，验证目录树投影与展开按钮
                    var recent = new RecentScanItem(snap.ScanId, "测试磁盘 (T:)", "12:00", "smoke", snap);
                    scanVm.OpenRecentScanCommand.Execute(recent);
                    Require(scanVm.ActiveSubView == DiskScanSubView.DirectoryTree, "OpenRecentScan should activate DirectoryTree sub-view");
                    Require(scanVm.VisibleTreeRows.Count > 0, "VisibleTreeRows should not be empty after loading snapshot");

                    // 展开第一层目录并验证 ListView 渲染后的所有 Button 不存在空 RenderTransform 冲突
                    ScanTreeRowItem firstDir = scanVm.VisibleTreeRows.First(r => r.HasChildren);
                    scanVm.ToggleExpandCommand.Execute(firstDir);
                    Require(firstDir.IsExpanded, "Directory row should expand cleanly");
                    scanVm.SelectedRow = firstDir;

                    // 3. 构造模拟真实 C 盘嵌套结构（Users -> Alice -> AppData -> Local -> Docker / JetBrains / Programs -> NovaStudio -> comfyui -> models），
                    //    验证点击“大软件目录”默认进入“按路径”且默认收起，再次点击“按路径”可一键展开/收起，且全局占比严格单调
                    ScanSnapshot nestedSnap = BuildNestedHotspotSnapshot();
                    scanVm.OpenRecentScanCommand.Execute(new RecentScanItem(nestedSnap.ScanId, "模拟系统盘 (C:)", "12:01", "hotspot-smoke", nestedSnap));
                    scanVm.SwitchToHotspotViewCommand.Execute(null);
                    Require(scanVm.BrowseMode == ScanBrowseMode.HotspotPathTree,
                        $"SwitchToHotspotViewCommand should default to HotspotPathTree, got {scanVm.BrowseMode}");
                    Require(!scanVm.IsHotspotPathTreeExpanded,
                        "HotspotPathTree should be collapsed by default");
                    Require(scanVm.VisibleTreeRows.Count == 1 && scanVm.VisibleTreeRows[0].Name == "Users" && scanVm.VisibleTreeRows[0].Depth == 0 && !scanVm.VisibleTreeRows[0].IsExpanded,
                        $"Collapsed HotspotPathTree should show only top-level Users row, got {scanVm.VisibleTreeRows.Count}");

                    // 3a. 收起态手动展开单个祖先节点时，仅展开剪枝后的大软件支路（Alice）
                    scanVm.ToggleExpandCommand.Execute(scanVm.VisibleTreeRows[0]);
                    Require(scanVm.VisibleTreeRows.Count == 2 && scanVm.VisibleTreeRows[1].Name == "Alice" && scanVm.VisibleTreeRows[1].Depth == 1,
                        "Manually expanding Users in collapsed HotspotPathTree should reveal pruned child Alice");

                    // 3b. 再次点击“按路径”按钮 -> 一键展开全部大软件支路
                    scanVm.SwitchToHotspotPathTreeCommand.Execute(null);
                    Require(scanVm.IsHotspotPathTreeExpanded, "Repeating SwitchToHotspotPathTreeCommand should expand tree");
                    Require(scanVm.VisibleTreeRows.Count > 3 && scanVm.VisibleTreeRows[0].Name == "Users" && scanVm.VisibleTreeRows[0].Depth == 0,
                        "Expanded HotspotPathTree should start from top-level Users at depth 0");
                    Require(scanVm.VisibleTreeRows.Any(r => r.Name == "Docker" && r.Depth == 4 && r.ShowWarningHint && !string.IsNullOrWhiteSpace(r.HintToolTip)),
                        "HotspotPathTree should indent Docker at depth 4 with amber warning hint tooltip");
                    Require(scanVm.VisibleTreeRows.Any(r => r.Name == "Programs" && r.AdmissionLevel == MigrationAdmissionLevel.HardDenied && r.ShowWarningHint),
                        "AppData\\Local\\Programs container root must be L0 HardDenied with amber warning hint");
                    Require(scanVm.VisibleTreeRows.Any(r => r.Name == "models" && r.ShowInfoHint && !string.IsNullOrWhiteSpace(r.HintToolTip)),
                        "Verified L1 models dir should show blue info hint tooltip");
                    Require(scanVm.VisibleTreeRows.All(r => r.IsDirectory),
                        "Tree/Hotspot views must filter out non-directory files by default");
                    Require(scanVm.ShareColumnHeader == "全盘占比",
                        $"HotspotPathTree should label share column as 全盘占比, got '{scanVm.ShareColumnHeader}'");
                    // 验证“按路径”采用全盘统一标尺：任意子目录占比严格 <= 其父目录占比（杜绝 5GB 子目录 100% 超过父目录的视觉倒挂）
                    var stack = new Stack<ScanTreeRowItem>();
                    foreach (ScanTreeRowItem row in scanVm.VisibleTreeRows)
                    {
                        while (stack.Count > 0 && stack.Peek().Depth >= row.Depth) stack.Pop();
                        if (stack.Count > 0)
                        {
                            ScanTreeRowItem parentRow = stack.Peek();
                            Require(row.ParentSharePercent <= parentRow.ParentSharePercent + 0.01,
                                $"HotspotPathTree share inversion: child '{row.Name}' ({row.ParentSharePercent:F1}%) > parent '{parentRow.Name}' ({parentRow.ParentSharePercent:F1}%)");
                        }
                        stack.Push(row);
                    }

                    // 3c. 第三次点击“按路径”按钮 -> 一键收回到顶层
                    scanVm.SwitchToHotspotPathTreeCommand.Execute(null);
                    Require(!scanVm.IsHotspotPathTreeExpanded && scanVm.VisibleTreeRows.Count == 1,
                        "Third click on SwitchToHotspotPathTreeCommand should collapse back to top-level rows");

                    // 4. 验证切到“直达列表”视图穿透 L0/套壳目录，返回完全互不重叠的真实大目录且总和 <= 根占用
                    scanVm.SwitchToHotspotFlatCommand.Execute(null);
                    Require(scanVm.VisibleTreeRows.Count == 3,
                        $"Expected 3 disjoint hotspot dirs (models, Docker, JetBrains), got {scanVm.VisibleTreeRows.Count}: {string.Join(", ", scanVm.VisibleTreeRows.Select(r => r.FullPath))}");
                    long hotspotSum = scanVm.VisibleTreeRows.Sum(r => r.UniqueAllocatedBytes);
                    long rootAlloc = nestedSnap.Nodes[nestedSnap.RootNodeId].UniqueAllocatedBytes;
                    Require(hotspotSum <= rootAlloc, $"Hotspot sum ({hotspotSum}) exceeded root unique allocated ({rootAlloc})");
                    for (int a = 0; a < scanVm.VisibleTreeRows.Count; a++)
                    {
                        for (int b = a + 1; b < scanVm.VisibleTreeRows.Count; b++)
                        {
                            string pa = scanVm.VisibleTreeRows[a].FullPath;
                            string pb = scanVm.VisibleTreeRows[b].FullPath;
                            Require(!PathIdentityService.IsWithin(pa, pb) && !PathIdentityService.IsWithin(pb, pa),
                                $"Hotspot rows overlap parent/child: '{pa}' vs '{pb}'");
                        }
                    }
                    Require(scanVm.VisibleTreeRows.Any(r => r.Name == "Docker" && r.AdmissionLevel == MigrationAdmissionLevel.VendorNativeRecommended),
                        "AppData\\Local\\Docker should be surfaced as L2 VendorNativeRecommended");
                    Require(scanVm.VisibleTreeRows.Any(r => r.Name == "models" && r.AdmissionLevel == MigrationAdmissionLevel.VerifiedEligible),
                        "NovaStudio\\comfyui\\models should be surfaced as L1 VerifiedEligible");
                    scanVm.SwitchToTreeViewCommand.Execute(null);
                    Require(!scanVm.VisibleTreeRows.Any(r => r.Name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)),
                        "Tree view must hide volume root $RECYCLE.BIN");
                    Require(!scanVm.VisibleTreeRows.Any(r => r.Name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)),
                        "Tree view must hide volume root System Volume Information");
                    Require(scanVm.VisibleTreeRows.Any(r => r.Name.Equals("Users", StringComparison.OrdinalIgnoreCase)),
                        "Tree view must still display normal directories like Users");

                    var rebuildRows = typeof(DiskScanViewModel).GetMethod("RebuildVisibleRows",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                    scanVm.SearchFilter = "Docker";
                    rebuildRows.Invoke(scanVm, null);
                    Require(scanVm.VisibleTreeRows.Any(r => r.Name == "Docker"), "Search must still find normal directories like Docker");
                    scanVm.SearchFilter = "RECYCLE";
                    rebuildRows.Invoke(scanVm, null);
                    Require(scanVm.VisibleTreeRows.Count == 0, "Search must not expose $RECYCLE.BIN");
                    scanVm.SearchFilter = "S-1-5-21-test";
                    rebuildRows.Invoke(scanVm, null);
                    Require(scanVm.VisibleTreeRows.Count == 0, "Search must not expose children under $RECYCLE.BIN");
                    scanVm.SearchFilter = "System Volume";
                    rebuildRows.Invoke(scanVm, null);
                    Require(scanVm.VisibleTreeRows.Count == 0, "Search must not expose System Volume Information");
                    scanVm.SearchFilter = string.Empty;
                    rebuildRows.Invoke(scanVm, null);
                    Require(!scanVm.TryLocateNodeInTree(nestedSnap.ScanId, 11), "TryLocateNodeInTree must refuse hidden $RECYCLE.BIN");
                    Require(!scanVm.TryLocateNodeInTree(nestedSnap.ScanId, 13), "TryLocateNodeInTree must refuse hidden System Volume Information");

                    foreach (DiskScanSubView sub in Enum.GetValues<DiskScanSubView>())
                    {
                        scanVm.ActiveSubView = sub;
                        scanPage.Measure(new Size(1000, 640));
                        scanPage.Arrange(new Rect(0, 0, 1000, 640));
                        scanPage.UpdateLayout();
                        AssertAllButtonsSafeForRenderTransform(scanPage);
                    }

                    // 5. 二期内置 AI 助手页面、磁盘扫描 AI 抽屉、建议采纳与目录定位联合烟测
                    Phase2AiCases.VerifyWpfUiInCurrentSta(AssertAllButtonsSafeForRenderTransform);
                }
                catch (Exception ex)
                {
                    captured = ex;
                }
                finally
                {
                    try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            Require(staThread.Join(TimeSpan.FromSeconds(30)), "WPF STA smoke test timed out");
            if (captured is not null) throw new InvalidOperationException("WPF STA smoke test failed: " + captured, captured);
        });
    }

    private static ScanSnapshot BuildNestedHotspotSnapshot()
    {
        const long GB = 1024L * 1024 * 1024;
        var pool = new Utf8NamePool();
        (byte p, int o, ushort l) Add(string n) => pool.AddName(n);

        // 0: C:\  1: Users  2: Alice  3: AppData  4: Local  5: Docker(100G)  6: JetBrains(60G)
        // 7: Programs(200G)  8: NovaStudio(200G)  9: comfyui(200G)  10: models(200G)
        // 11: $RECYCLE.BIN(20G)  12: S-1-5-21-test(20G)  13: System Volume Information(10G)
        var names = new[] { @"C:\", "Users", "Alice", "AppData", "Local", "Docker", "JetBrains", "Programs", "NovaStudio", "comfyui", "models", "$RECYCLE.BIN", "S-1-5-21-test", "System Volume Information" };
        var parents = new[] { -1, 0, 1, 2, 3, 4, 4, 4, 7, 8, 9, 0, 11, 0 };
        var sizes = new[] { 390 * GB, 360 * GB, 360 * GB, 360 * GB, 360 * GB, 100 * GB, 60 * GB, 200 * GB, 200 * GB, 200 * GB, 200 * GB, 20 * GB, 20 * GB, 10 * GB };

        var nodes = new ScanNodeRecord[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            var (page, off, len) = Add(names[i]);
            nodes[i] = new ScanNodeRecord
            {
                NodeId = i,
                ParentNodeId = parents[i],
                NamePageIndex = page,
                NameOffset = off,
                NameLength = len,
                Flags = ScanNodeFlags.IsDirectory,
                LogicalBytes = sizes[i],
                AllocatedBytes = sizes[i],
                UniqueAllocatedBytes = sizes[i],
                FileCount = 10
            };
        }

        var childrenByParent = new List<int>[names.Length];
        for (int i = 0; i < names.Length; i++) childrenByParent[i] = [];
        for (int i = 1; i < names.Length; i++) childrenByParent[parents[i]].Add(i);

        var offsets = new int[names.Length + 1];
        var csrChildren = new List<int>();
        for (int i = 0; i < names.Length; i++)
        {
            offsets[i] = csrChildren.Count;
            csrChildren.AddRange(childrenByParent[i]);
        }
        offsets[names.Length] = csrChildren.Count;

        return new ScanSnapshot
        {
            Volume = new ScanVolumeInfo(@"C:\", "Windows", @"\\?\Volume{sim}\", 1, "NTFS", 1000 * GB, 640 * GB, true, "sim"),
            Backend = ScanBackendKind.RawNtfsMft,
            Coverage = ScanCoverageStatus.Complete,
            StartedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            PolicyVersion = "test",
            RootNodeId = 0,
            Nodes = nodes,
            CsrOffsets = offsets,
            CsrChildren = csrChildren.ToArray(),
            NamePool = pool
        };
    }

    private static void AssertAllButtonsSafeForRenderTransform(DependencyObject root)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            DependencyObject current = queue.Dequeue();
            if (current is Button btn && HasRenderTransformStoryboardTrigger(btn.Style))
            {
                Require(btn.RenderTransform is ScaleTransform,
                    $"Button '{btn.Name}' (Content='{btn.Content}') inherits RenderTransform ScaleTransform Storyboard trigger but RenderTransform is '{btn.RenderTransform?.GetType().Name ?? "null"}'");
            }

            int count = VisualTreeHelper.GetChildrenCount(current);
            for (int i = 0; i < count; i++)
            {
                queue.Enqueue(VisualTreeHelper.GetChild(current, i));
            }
        }
    }

    private static bool HasRenderTransformStoryboardTrigger(Style? style)
    {
        for (Style? s = style; s is not null; s = s.BasedOn)
        {
            foreach (TriggerBase tb in s.Triggers)
            {
                foreach (TriggerAction action in tb.EnterActions.Concat(tb.ExitActions))
                {
                    if (action is BeginStoryboard bs && bs.Storyboard is not null)
                    {
                        foreach (Timeline tl in bs.Storyboard.Children)
                        {
                            PropertyPath? path = Storyboard.GetTargetProperty(tl);
                            if (path?.Path?.Contains("RenderTransform", StringComparison.Ordinal) == true)
                                return true;
                        }
                    }
                }
            }
        }
        return false;
    }
}

internal static class Win32SparseHelper
{
    private const uint FSCTL_SET_SPARSE = 0x000900C4;

    public static bool TryMarkSparse(Microsoft.Win32.SafeHandles.SafeFileHandle handle) =>
        DeviceIoControl(handle, FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        Microsoft.Win32.SafeHandles.SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);
}
