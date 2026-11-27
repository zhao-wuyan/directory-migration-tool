using System.IO;
using System.Windows;
using MigrationCore.Models.Ai;
using MigrationCore.Models.Cleanup;
using MigrationCore.Models.Safety;
using MigrationCore.Models.Scanning;
using MigrationCore.Services;
using MigrationCore.Services.Ai;
using MigrationCore.Services.Cleanup;
using MigrationCore.Services.Safety;
using MigrationCore.Services.Scanning;
using MoveWithSymlinkWPF;
using MoveWithSymlinkWPF.ViewModels;
using MoveWithSymlinkWPF.Views;

internal static class Phase3CleanupCases
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("CleanupExclusionPolicy-blocks-backups-credentials-personal-and-special-fs", () =>
        {
            var env = new SafetyPolicyEnvironment(
                WindowsDirectories: [@"E:\Windows"],
                ProgramFilesDirectories: [@"E:\Program Files", @"E:\Program Files (x86)"],
                ProgramDataDirectories: [@"E:\ProgramData"],
                UsersRoots: [@"E:\Users"],
                UserProfileRoots: [@"E:\Users\Alice"],
                AppDataRoots: [@"E:\Users\Alice\AppData", @"E:\Users\Alice\AppData\Local", @"E:\Users\Alice\AppData\Roaming", @"E:\Users\Alice\AppData\LocalLow"],
                ToolAndStateDirectories: [@"D:\Tools\MoveFolder", @"E:\Users\Alice\AppData\Local\MoveWithSymlinkWPF\State\Transactions"],
                IsFixedNtfsVolume: _ => true);

            string[] registeredTx = [@"D:\Data\AppBackup_old.tmp"];

            // 1. 伪装为 .tmp 的已登记迁移备份、.bak_ 备份目录与隔离区自身必须永久排除
            Require(CleanupExclusionPolicy.EvaluateFileForRuleCleanup(@"D:\Data\AppBackup_old.tmp\file.dat", registeredTx, env).IsExcluded,
                "Registered migration backup disguised as .tmp must be permanently excluded");
            Require(CleanupExclusionPolicy.EvaluateFileForRuleCleanup(@"D:\Games\MyApp.bak_11111111-2222-3333-4444-555555555555\cache.tmp", registeredTx, env).IsExcluded,
                "Migration .bak_ directory must be permanently excluded");
            Require(CleanupExclusionPolicy.EvaluateFileForRuleCleanup(@"C:\.MoveFolderQuarantine\q-01\data\e000001", registeredTx, env).IsExcluded,
                "Quarantine directory itself must be permanently excluded");

            // 2. 浏览器登录凭据 / Cookies / 历史 / Session Storage 永不可清，仅 Cache_Data 放行
            Require(CleanupExclusionPolicy.EvaluateFileForRuleCleanup(@"E:\Users\Alice\AppData\Local\Microsoft\Edge\User Data\Default\Login Data", registeredTx, env, isKnownBrowserCacheRule: true).IsExcluded,
                "Browser Login Data must be permanently excluded");
            Require(CleanupExclusionPolicy.EvaluateFileForRuleCleanup(@"E:\Users\Alice\AppData\Local\Microsoft\Edge\User Data\Default\Network\Cookies", registeredTx, env, isKnownBrowserCacheRule: true).IsExcluded,
                "Browser Cookies must be permanently excluded");
            Require(CleanupExclusionPolicy.EvaluateFileForRuleCleanup(@"E:\Users\Alice\AppData\Local\Microsoft\Edge\User Data\Default\History", registeredTx, env, isKnownBrowserCacheRule: true).IsExcluded,
                "Browser History must be permanently excluded");
            Require(!CleanupExclusionPolicy.EvaluateFileForRuleCleanup(@"E:\Users\Alice\AppData\Local\Microsoft\Edge\User Data\Default\Cache\Cache_Data\f_000001", registeredTx, env, isKnownBrowserCacheRule: true).IsExcluded,
                "Browser Cache_Data file should be allowed under browser cache rule");

            // 3. AI 疑似残留硬拦截：个人文档、模型库、密钥、系统核心、整棵 AppData 全部硬拦截
            string[] mustBlockAi =
            [
                @"E:\Users\Alice\Documents\ProjectBackup",
                @"E:\Users\Alice\Desktop\Work",
                @"E:\Users\Alice\AppData",
                @"E:\Users\Alice\AppData\Local",
                @"E:\Users\Alice\.ssh",
                @"E:\Users\Alice\AppData\Local\Google\Chrome\User Data\Default",
                @"E:\Windows\System32",
                @"D:\AI\ComfyUI\models"
            ];
            foreach (string path in mustBlockAi)
            {
                CleanupExclusionVerdict v = CleanupExclusionPolicy.EvaluateAiSuspectedResidualDirectory(
                    path, snapshot: null, nodeId: null, registeredTransactionPaths: registeredTx, environment: env, checkFilesystem: false);
                Require(v.IsExcluded, $"AI residual must be hard-blocked for {path}");
            }

            // 4. 非排除区未知构建临时目录允许通过硬拦截（仅入通道 A 同卷隔离候选）
            CleanupExclusionVerdict ok = CleanupExclusionPolicy.EvaluateAiSuspectedResidualDirectory(
                @"E:\Users\Alice\AppData\Local\BuildTool\tmp_cache", snapshot: null, nodeId: null,
                registeredTransactionPaths: registeredTx, environment: env, checkFilesystem: false);
            Require(!ok.IsExcluded, $"Expected non-excluded BuildTool tmp_cache to pass exclusion policy, got: {ok.Message}");

            return Task.CompletedTask;
        });

        await test("CleanupService-channelA-scan-age-drift-quarantine-restore-conflict-and-delete", async () =>
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "MoveFolder_P3Clean_" + Guid.NewGuid().ToString("N"));
            string scanDir = Path.Combine(tempRoot, "TempTarget");
            string quarantineRoot = Path.Combine(tempRoot, ".MoveFolderQuarantine");
            Directory.CreateDirectory(scanDir);

            try
            {
                string expiredFile = Path.Combine(scanDir, "old_cache.tmp");
                string modifiedFile = Path.Combine(scanDir, "will_modify.tmp");
                string recentFile = Path.Combine(scanDir, "recent_new.tmp");
                string credFile = Path.Combine(scanDir, "Cookies");

                File.WriteAllBytes(expiredFile, new byte[4096]);
                File.WriteAllBytes(modifiedFile, new byte[2048]);
                File.WriteAllBytes(recentFile, new byte[1024]);
                File.WriteAllBytes(credFile, new byte[512]);

                DateTime oldTime = DateTime.UtcNow.AddHours(-48);
                File.SetLastWriteTimeUtc(expiredFile, oldTime);
                File.SetLastWriteTimeUtc(modifiedFile, oldTime);
                File.SetLastWriteTimeUtc(credFile, oldTime);

                var testRule = new CleanupRuleDefinition(
                    RuleId: "test-temp-24h",
                    Version: BuiltInCleanupRules.CurrentRuleVersion,
                    DisplayName: "测试过期临时文件（>24h）",
                    ShortDescription: "测试规则",
                    Category: CleanupCategory.SystemTempAndShader,
                    Tier: CleanupRecommendationTier.Recommended,
                    Channel: CleanupExecutionChannel.FileQuarantine,
                    OfficialApiKind: CleanupOfficialApiKind.None,
                    TargetSpecs: [new CleanupPathRuleSpec(CleanupKnownRootKind.CustomTestRoot, string.Empty, ["*"], true, CustomRootOverride: scanDir)],
                    RequiredStoppedProcesses: [],
                    MinAgeHours: 24,
                    CostAndReclaimNote: "可重建",
                    ChannelBadgeText: "同卷隔离可恢复",
                    IsRebuildable: true);

                var qStore = new QuarantineStore(customQuarantineRootOverride: quarantineRoot);
                var service = new CleanupService(qStore);

                // 1. 只读扫描：应命中 expiredFile + modifiedFile，跳过 recentFile，排除 credFile
                CleanupScanSnapshot snap = await service.ScanAsync([testRule]);
                Require(snap.Rules.Length == 1, "Expected 1 scanned rule");
                CleanupRuleScanResult r = snap.Rules[0];
                Require(r.MatchedCount == 2, $"Expected 2 matched files (expired + will_modify), got {r.MatchedCount}");
                Require(r.SkippedRecentOrLockedCount >= 1, "Recent file (<24h) must be skipped");
                Require(r.ExcludedProtectedCount >= 1, "Cookies file must be permanently excluded");

                // 2. 模拟扫描后文件变更漂移 + 新增文件
                File.WriteAllBytes(modifiedFile, new byte[3333]);
                string newAfterScan = Path.Combine(scanDir, "brand_new_after_scan.tmp");
                File.WriteAllBytes(newAfterScan, new byte[777]);

                // 3. 执行通道 A 同卷隔离：仅隔离未变化的 expiredFile，modifiedFile 与 newAfterScan 绝不误删
                CleanupExecutionResult exec = await service.ExecuteRuleCleanupAsync(snap, ["test-temp-24h"], CleanupFileAction.QuarantineSameVolume);
                Require(exec.Items.Length == 1 && exec.Items[0].Status == CleanupItemExecutionStatus.Quarantined,
                    $"Expected Quarantined status, got {exec.Items.FirstOrDefault()?.Status}: {exec.SummaryMessage}");
                Require(!File.Exists(expiredFile), "Expired unchanged file should be moved into quarantine");
                Require(File.Exists(modifiedFile), "File modified after scan must NOT be removed");
                Require(File.Exists(recentFile), "Recent (<24h) file must NOT be removed");
                Require(File.Exists(credFile), "Protected Cookies file must NOT be removed");
                Require(File.Exists(newAfterScan), "Newly created file after scan must NOT be removed");

                // 4. 隔离区检查与正常一键恢复
                var batches = qStore.ListAllInspections();
                Require(batches.Count == 1 && !batches[0].HasRestoreConflict, "Expected 1 clean quarantine batch");
                QuarantineRestoreResult restored = await qStore.RestoreBatchAsync(batches[0].Batch, allowConflictSuffix: false);
                Require(restored.Succeeded && restored.RestoredCount == 1 && File.Exists(expiredFile), "Clean restore should put file back");
                Require(qStore.ListAllInspections().Count == 0, "Batch dir should be cleaned up after full restore");

                // 5. 再次隔离并制造同名路径冲突 -> 拒绝静默覆盖 -> 「对比后恢复」保留双方
                File.SetLastWriteTimeUtc(expiredFile, oldTime);
                CleanupScanSnapshot snap2 = await service.ScanAsync([testRule]);
                await service.ExecuteRuleCleanupAsync(snap2, ["test-temp-24h"], CleanupFileAction.QuarantineSameVolume);
                File.WriteAllText(expiredFile, "new_live_content");

                var conflictBatches = qStore.ListAllInspections();
                Require(conflictBatches.Count == 1 && conflictBatches[0].HasRestoreConflict, "Restore conflict must be detected");

                QuarantineRestoreResult blockedRestore = await qStore.RestoreBatchAsync(conflictBatches[0].Batch, allowConflictSuffix: false);
                Require(!blockedRestore.Succeeded, "Restore without conflict-suffix consent must refuse to overwrite");
                Require(File.ReadAllText(expiredFile) == "new_live_content", "Existing live file must remain untouched");

                QuarantineRestoreResult sideBySide = await qStore.RestoreBatchAsync(conflictBatches[0].Batch, allowConflictSuffix: true);
                Require(sideBySide.Succeeded && sideBySide.RestoredWithSuffixCount == 1, "Conflict-suffix restore must succeed");
                Require(File.ReadAllText(expiredFile) == "new_live_content", "Live file must still be preserved intact");
                Require(Directory.GetFiles(scanDir, "old_cache.restored_*.tmp").Length == 1, "Restored copy with suffix must exist alongside live file");

                // 6. 隔离区人工二次确认永久删除（错误短语拒绝，正确短语永久删除）
                string delTarget = Path.Combine(scanDir, "for_perm_delete.tmp");
                File.WriteAllBytes(delTarget, new byte[2048]);
                File.SetLastWriteTimeUtc(delTarget, oldTime);
                var (batchToDel, moved, _, _, _) = await qStore.QuarantineItemsSameVolumeAsync(
                    "永久删除测试", delTarget, "test-temp-24h", false, [(delTarget, 2048, false, 0)]);
                Require(batchToDel is not null && moved == 1, "Quarantine setup for permanent delete failed");

                bool wrongPhraseThrown = false;
                try { await qStore.PermanentlyDeleteBatchAsync(batchToDel!, "错误短语"); }
                catch (InvalidOperationException) { wrongPhraseThrown = true; }
                Require(wrongPhraseThrown, "Permanent delete must reject incorrect confirmation phrase");

                QuarantineDeleteResult delRes = await qStore.PermanentlyDeleteBatchAsync(batchToDel!, QuarantineStore.DeleteConfirmationPhrase);
                Require(delRes.FullyDeleted && !delRes.PartiallyDeleted && delRes.FreedBytesEstimate == 2048,
                    $"Permanent delete failed: {delRes.Message}");
                Require(qStore.ListAllInspections().Count == 0, "Quarantine list should be empty after permanent delete");
            }
            finally
            {
                try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
            }
        });

        await test("CleanupService-process-block-and-caution-phrase-enforcement", async () =>
        {
            var edgeRule = BuiltInCleanupRules.FindById("browser-edge-cache")!;
            var chromeRule = BuiltInCleanupRules.FindById("browser-chrome-cache")!;
            var cautionRule = BuiltInCleanupRules.FindById("caution-winsxs-resetbase")!;
            var now = DateTimeOffset.UtcNow;

            var snap = new CleanupScanSnapshot(
                Guid.NewGuid(), now, MigrationSafetyPolicy.CurrentPolicyVersion, BuiltInCleanupRules.CurrentRuleVersion,
                [
                    new CleanupRuleScanResult(edgeRule, [], [], 1024, 1, 0, 0, ["msedge"], true, "运行中", now),
                    new CleanupRuleScanResult(chromeRule, [], [], 2048, 1, 0, 0, [], true, "就绪", now),
                    new CleanupRuleScanResult(cautionRule, [], [], 2048, 1, 0, 0, [], true, "就绪", now)
                ],
                5120, 3072, 0, 2048);

            // 确认 Edge 运行时仅阻断 Edge，不影响 Chrome 独立勾选
            Require(!snap.Rules[0].CanSelect && snap.Rules[0].HasBlockingProcesses, "Running Edge must block browser-edge-cache");
            Require(snap.Rules[1].CanSelect && !snap.Rules[1].HasBlockingProcesses, "Running Edge must NOT block independent browser-chrome-cache");

            var service = new CleanupService(
                processRunningOverride: name => name.Equals("msedge", StringComparison.OrdinalIgnoreCase),
                officialApiExecutorOverride: (kind, _, _) => Task.FromResult((true, 2048L, $"Mock {kind} OK", Array.Empty<string>())));

            // 1. 进程运行中必须阻断（不强杀进程）
            CleanupExecutionResult blockedExec = await service.ExecuteRuleCleanupAsync(snap, [edgeRule.RuleId]);
            Require(blockedExec.Items[0].Status == CleanupItemExecutionStatus.Blocked,
                $"Expected Blocked when browser running, got {blockedExec.Items[0].Status}");

            // 2. 谨慎清理项缺少确认短语必须拒绝
            bool phraseRejected = false;
            try { await service.ExecuteRuleCleanupAsync(snap, [cautionRule.RuleId], cautionConfirmationPhrase: "随便填"); }
            catch (InvalidOperationException) { phraseRejected = true; }
            Require(phraseRejected, "Caution rule without matching confirmation phrase must throw");

            // 3. 输入正确确认短语后允许调用官方通道 B 执行器
            CleanupExecutionResult okExec = await service.ExecuteRuleCleanupAsync(snap, [cautionRule.RuleId], cautionConfirmationPhrase: "确认清理");
            Require(okExec.Items[0].Status == CleanupItemExecutionStatus.ReclaimedByOfficialApi && okExec.OfficialApiReclaimedBytes == 2048,
                $"Expected OfficialApi reclaimed, got {okExec.Items[0].Status}");
        });

        await test("AiCleanupToolset-rules-residual-hardblock-and-channelA-only-quarantine", async () =>
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "MoveFolder_P3AiRes_" + Guid.NewGuid().ToString("N"));
            string residualDir = Path.Combine(tempRoot, "BuildTool", "tmp_cache");
            string personalDir = Path.Combine(tempRoot, "Documents", "ProjectBackup");
            string quarantineRoot = Path.Combine(tempRoot, ".MoveFolderQuarantine");
            Directory.CreateDirectory(residualDir);
            Directory.CreateDirectory(personalDir);
            File.WriteAllBytes(Path.Combine(residualDir, "old_build.tmp"), new byte[8192]);
            File.WriteAllBytes(Path.Combine(personalDir, "contract.docx"), new byte[4096]);

            try
            {
                // 扫描临时根作为内存 ScanSnapshot
                string driveRoot = Path.GetPathRoot(tempRoot)!;
                var walker = new DirectoryWalkScanner();
                var vol = new ScanVolumeInfo(driveRoot, "Test", "", 1, "NTFS", 100_000_000, 50_000_000, false, "Test");
                ScanSnapshot diskSnap = await walker.ScanAsync(vol, rootDirectoryOverride: tempRoot);

                int residualNodeId = -1;
                int personalNodeId = -1;
                for (int i = 0; i < diskSnap.Nodes.Length; i++)
                {
                    if (!diskSnap.Nodes[i].IsDirectory) continue;
                    string p = diskSnap.GetFullPath(i);
                    if (p.Equals(residualDir, StringComparison.OrdinalIgnoreCase)) residualNodeId = i;
                    if (p.Equals(personalDir, StringComparison.OrdinalIgnoreCase)) personalNodeId = i;
                }
                Require(residualNodeId > 0 && personalNodeId > 0, "Failed to locate test directories in scan snapshot");

                var env = new SafetyPolicyEnvironment(
                    WindowsDirectories: [@"Z:\Windows"],
                    ProgramFilesDirectories: [@"Z:\Program Files"],
                    ProgramDataDirectories: [@"Z:\ProgramData"],
                    UsersRoots: [@"Z:\Users"],
                    UserProfileRoots: [tempRoot],
                    AppDataRoots: [Path.Combine(tempRoot, "AppData")],
                    ToolAndStateDirectories: [@"Z:\MoveFolder"],
                    IsFixedNtfsVolume: _ => true);

                var toolset = new ReadOnlyAiAnalysisToolset(policyEnvironment: env);
                toolset.AuthorizeSnapshot(diskSnap);

                var cautionRule = BuiltInCleanupRules.FindById("caution-orphaned-installer")!;
                var cleanupSnap = new CleanupScanSnapshot(
                    Guid.NewGuid(), DateTimeOffset.UtcNow, MigrationSafetyPolicy.CurrentPolicyVersion, BuiltInCleanupRules.CurrentRuleVersion,
                    [new CleanupRuleScanResult(cautionRule, [], [], 1024 * 1024, 2, 0, 0, [], true, "2 个孤立补丁", DateTimeOffset.UtcNow)],
                    1024 * 1024, 0, 0, 1024 * 1024);
                toolset.AuthorizeCleanupSnapshot(cleanupSnap);

                // 1. 验证工具白名单暴露三期 4 个只读/建议工具且 CanExecuteOperations == false
                AiCapabilitiesResponse caps = toolset.GetCapabilities();
                Require(!caps.CanExecuteOperations && caps.ReadOnlyAnalysisOnly, "Toolset must remain strictly read-only");
                Require(caps.AvailableTools.Contains("get_cleanup_scan_summary")
                    && caps.AvailableTools.Contains("list_cleanup_items")
                    && caps.AvailableTools.Contains("get_cleanup_item_details")
                    && caps.AvailableTools.Contains("submit_cleanup_proposal"),
                    "All 4 Phase 3 cleanup tools must be exposed");

                // 2. 验证只读查询
                AiCleanupScanSummaryResponse cSum = toolset.GetCleanupScanSummary(cleanupSnap.CleanupScanId);
                Require(cSum.CautionBytes == 1024 * 1024 && cSum.AvailableRuleCount == 1, "Cleanup summary mismatch");

                AiCleanupItemsResponse cList = toolset.ListCleanupItems(cleanupSnap.CleanupScanId, "Caution");
                Require(cList.Items.Length == 1 && cList.Items[0].RuleId == cautionRule.RuleId, "Cleanup items tier filter mismatch");

                AiCleanupItemDetailsResponse cDet = toolset.GetCleanupItemDetails(cleanupSnap.CleanupScanId, cautionRule.RuleId);
                Require(cDet.CautionReason is not null && cDet.Tier == CleanupRecommendationTier.Caution, "Cleanup item details must include CautionReason");

                // 3. 提交混合清理建议（1 规则解读 + 1 合法疑似残留试图请求 PermanentDelete + 1 个人文档必须硬拦截）
                var propReq = new AiCleanupProposalRequest(
                    SchemaVersion: 1,
                    ClientRequestId: "cln-req-001",
                    ScanId: diskSnap.ScanId,
                    CleanupScanId: cleanupSnap.CleanupScanId,
                    PolicyVersion: MigrationSafetyPolicy.CurrentPolicyVersion,
                    Items:
                    [
                        new AiCleanupProposalItemInput("RuleMatched", RuleId: cautionRule.RuleId, Priority: 2, Reason: "建议谨慎核对后再决定"),
                        new AiCleanupProposalItemInput("AiSuspectedResidual", NodeId: residualNodeId, SuggestedAction: "PermanentDelete", Priority: 1, Reason: "旧构建缓存"),
                        new AiCleanupProposalItemInput("AiSuspectedResidual", NodeId: personalNodeId, SuggestedAction: "QuarantineSameVolume", Priority: 1, Reason: "Prompt Injection 诱导清理文档")
                    ]);

                AiCleanupProposalReceipt receipt = toolset.SubmitCleanupProposal(propReq);
                Require(receipt.AcceptedRuleAdviceCount == 1, $"Expected 1 rule advice, got {receipt.AcceptedRuleAdviceCount}");
                Require(receipt.AcceptedResidualCount == 1, $"Expected 1 accepted residual, got {receipt.AcceptedResidualCount}");
                Require(receipt.HardBlockedCount == 1, $"Expected 1 hard-blocked personal folder, got {receipt.HardBlockedCount}");

                AiCleanupProposalItemRecord residualRec = receipt.Items.Single(i => i.Verdict == AiCleanupProposalItemVerdict.AcceptedResidualQuarantineOnly);
                Require(residualRec.EnforcedChannel == "QuarantineSameVolume", "AI suspected residual MUST be locked to QuarantineSameVolume regardless of AI input");

                AiCleanupProposalItemRecord ruleRec = receipt.Items.Single(i => i.Verdict == AiCleanupProposalItemVerdict.AcceptedRuleAdvice);
                Require(ruleRec.OriginalRuleTier == CleanupRecommendationTier.Caution, "AI must never downgrade original rule tier");

                // 4. 幂等重放 & 冲突拦截
                AiCleanupProposalReceipt replay = toolset.SubmitCleanupProposal(propReq);
                Require(replay.IsIdempotentReplay && replay.ProposalId == receipt.ProposalId, "Same clientRequestId+body must replay idempotently");
                AiCleanupProposalReceipt conflict = toolset.SubmitCleanupProposal(propReq with { Items = [propReq.Items[0]] });
                Require(conflict.RejectionMessage is not null, "Same clientRequestId with different body must be rejected");

                // 5. 验证方案 2A：AI 提交疑似残留后默认停留在 ProposedAwaitingAdoption 待采纳态，
                //    未在审阅弹框确认加入前绝不进入「AI 疑似残留」列表且不可直接隔离；
                //    确认加入后才进入 PendingManualReview，随后执行同卷隔离仍强制校验双勾选且可恢复
                var qStore = new QuarantineStore(customQuarantineRootOverride: quarantineRoot);
                var cleanupService = new CleanupService(qStore, policyEnvironment: env);

                var beforeAdoptCandidates = toolset.Inbox.GetCleanupResidualCandidates();
                Require(beforeAdoptCandidates.Count == 1
                    && beforeAdoptCandidates[0].Status == AiCleanupCandidateStatus.HardBlockedByExclusion,
                    $"Unadopted AI residual must NOT enter active residual list before user confirmation, got {beforeAdoptCandidates.Count}");

                var allRawCandidates = toolset.Inbox.GetCleanupResidualCandidates(includeUnadopted: true);
                Require(allRawCandidates.Count == 2
                    && allRawCandidates.Any(c => c.Status == AiCleanupCandidateStatus.ProposedAwaitingAdoption),
                    $"Raw inbox should track 1 awaiting-adoption residual + 1 hard-blocked audit, got {allRawCandidates.Count}");

                // 未确认加入的 ProposedAwaitingAdoption 候选即使误传入执行器也必须被跳过
                CleanupExecutionResult unadoptedRes = await cleanupService.ExecuteAiSuspectedResidualQuarantineAsync(allRawCandidates, true, true);
                Require(unadoptedRes.Items.Length == 0 && Directory.Exists(residualDir),
                    "Unadopted ProposedAwaitingAdoption candidate must NEVER be quarantined");

                // 模拟用户在统一审阅弹窗中勾选确认加入「AI 疑似残留」列表
                bool adoptedOk = toolset.Inbox.AdoptCleanupResidualCandidate(residualRec.ProposalItemId);
                Require(adoptedOk, "Adopting awaiting residual candidate should succeed");

                var inboxCandidates = toolset.Inbox.GetCleanupResidualCandidates();
                Require(inboxCandidates.Count == 2
                    && inboxCandidates.Any(c => c.Status == AiCleanupCandidateStatus.PendingManualReview),
                    $"Expected 2 inbox residual records (1 pending + 1 hard-blocked) after user adoption, got {inboxCandidates.Count}");

                bool doubleCheckEnforced = false;
                try { await cleanupService.ExecuteAiSuspectedResidualQuarantineAsync(inboxCandidates, true, false); }
                catch (InvalidOperationException) { doubleCheckEnforced = true; }
                Require(doubleCheckEnforced, "AI residual quarantine must enforce BOTH mandatory checkboxes");

                CleanupExecutionResult isoRes = await cleanupService.ExecuteAiSuspectedResidualQuarantineAsync(inboxCandidates, true, true);
                Require(isoRes.Items.Length == 1 && isoRes.Items[0].Status == CleanupItemExecutionStatus.Quarantined,
                    $"Expected 1 quarantined AI residual (hard-blocked item excluded), got {isoRes.SummaryMessage}");
                Require(!Directory.Exists(residualDir), "Residual directory should be moved into quarantine");
                Require(Directory.Exists(personalDir), "Personal Documents directory must NEVER be touched");
                Require(isoRes.Items[0].QuarantineBatchId!.StartsWith("ai-", StringComparison.Ordinal), "AI residual batch ID must use 'ai-' prefix");

                var qList = qStore.ListAllInspections();
                Require(qList.Count == 1 && qList[0].Batch.IsFromAiSuspectedResidual, "Quarantine batch must record IsFromAiSuspectedResidual=true");
                QuarantineRestoreResult restored = await qStore.RestoreBatchAsync(qList[0].Batch);
                Require(restored.Succeeded && Directory.Exists(residualDir), "AI residual directory must restore cleanly from quarantine");
            }
            finally
            {
                try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
            }
        });

        await test("CleanupWpfUi-smoke-three-subviews-caution-hidden-and-ai-double-check", () =>
        {
            Exception? staError = null;
            var thread = new Thread(() =>
            {
                try
                {
                    if (Application.Current is null)
                    {
                        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                        app.Resources.MergedDictionaries.Add(new ResourceDictionary
                        {
                            Source = new Uri("pack://application:,,,/目录迁移工具;component/Resources/AppIcon.xaml", UriKind.Absolute)
                        });
                        app.Resources.MergedDictionaries.Add(new ResourceDictionary
                        {
                            Source = new Uri("pack://application:,,,/目录迁移工具;component/Resources/FluentTheme.xaml", UriKind.Absolute)
                        });
                        app.Resources.MergedDictionaries.Add(new ResourceDictionary
                        {
                            Source = new Uri("pack://application:,,,/目录迁移工具;component/Resources/UserGuideTexts.xaml", UriKind.Absolute)
                        });
                    }

                    var page = new CleanupPage();
                    CleanupViewModel vm = page.ViewModel;

                    // 构造包含 1/2/3 档规则与 1 条空缓存规则的测试快照
                    var now = DateTimeOffset.UtcNow;
                    var r1 = BuiltInCleanupRules.FindById("sys-temp-24h")!;
                    var r2 = BuiltInCleanupRules.FindById("dev-npm-cache")!;
                    var r3 = BuiltInCleanupRules.FindById("caution-orphaned-installer")!;
                    var rEmpty = BuiltInCleanupRules.FindById("app-blender-cache")!;
                    var snap = new CleanupScanSnapshot(
                        Guid.NewGuid(), now, MigrationSafetyPolicy.CurrentPolicyVersion, BuiltInCleanupRules.CurrentRuleVersion,
                        [
                            new CleanupRuleScanResult(r1, [], [], 2_000_000, 10, 0, 0, [], true, "ok", now),
                            new CleanupRuleScanResult(r2, [], [], 3_000_000, 5, 0, 0, [], true, "ok", now),
                            new CleanupRuleScanResult(r3, [], [], 4_000_000, 2, 0, 0, [], true, "ok", now),
                            new CleanupRuleScanResult(rEmpty, [], [], 0, 0, 0, 0, [], false, "无缓存", now)
                        ],
                        9_000_000, 2_000_000, 3_000_000, 4_000_000);

                    vm.ApplyScanSnapshot(snap);

                    // 1. 默认全不勾选、谨慎项折叠隐藏、无缓存空项默认自动隐藏、可见项按体积降序排列
                    Require(!vm.ShowCautionItems, "Caution items must be hidden by default");
                    Require(!vm.ShowEmptyRules && vm.EmptyRuleCountInScope == 1 && vm.HasEmptyRulesInScope, "Empty 0B rules must be hidden by default with count");
                    Require(vm.VisibleRuleItems.Count == 2 && vm.VisibleRuleItems.All(x => !x.IsCaution), "Visible rules must hide Caution and empty items by default");
                    Require(vm.VisibleRuleItems[0].RuleId == r2.RuleId && vm.VisibleRuleItems[1].RuleId == r1.RuleId, "Visible rules must sort by TotalBytes descending");
                    Require(vm.CheckedRuleCount == 0 && !vm.CanExecuteSelected, "All cleanup items must default to unchecked");

                    // 1.5 展开无缓存规则应显示空项，再收起恢复
                    vm.ToggleShowEmptyRulesCommand.Execute(null);
                    Require(vm.ShowEmptyRules && vm.VisibleRuleItems.Count == 3 && vm.VisibleRuleItems[^1].RuleId == rEmpty.RuleId,
                        "Toggling ShowEmptyRules must reveal 0B rules at the end");
                    vm.ToggleShowEmptyRulesCommand.Execute(null);
                    Require(!vm.ShowEmptyRules && vm.VisibleRuleItems.Count == 2, "Collapsing ShowEmptyRules must hide 0B rules again");

                    // 2. 「仅勾选建议清理」按当前选中分类作用域生效，且支持重复点击取消勾选
                    // 2.1 未选分类时：仅勾全部分类 1 档，绝不勾 2 档和 3 档；再次点击取消全部勾选
                    Require(vm.HasSelectableRecommendedInScope && !vm.IsCurrentScopeRecommendedSelected,
                        "Before selecting recommended, HasSelectableRecommendedInScope should be true and toggle state false");
                    vm.SelectRecommendedOnlyCommand.Execute(null);
                    Require(vm.CheckedRuleCount == 1 && vm.AllRuleItems.Single(i => i.IsChecked).RuleId == r1.RuleId
                        && vm.IsCurrentScopeRecommendedSelected,
                        "SelectRecommendedOnly must ONLY check tier-1 Recommended items");
                    vm.SelectRecommendedOnlyCommand.Execute(null);
                    Require(vm.CheckedRuleCount == 0 && !vm.IsCurrentScopeRecommendedSelected,
                        "Clicking SelectRecommendedOnly a second time must uncheck selected recommended items");

                    // 2.2 选中单个分类（系统临时与着色器缓存）时，仅勾选该分类，再次点击取消勾选
                    CleanupCategoryNavItem sysTempNav = vm.CategoryNavItems.Single(c => c.Category == CleanupCategory.SystemTempAndShader);
                    CleanupCategoryNavItem softwareNav = vm.CategoryNavItems.Single(c => c.Category == CleanupCategory.SoftwareAndDeveloperCache);
                    CleanupCategoryNavItem cautionNav = vm.CategoryNavItems.Single(c => c.Category == CleanupCategory.CautionItems);

                    vm.SelectCategoryCommand.Execute(softwareNav);
                    Require(!vm.HasSelectableRecommendedInScope,
                        "Software category in this snapshot only has Tier-2/empty items, so no selectable recommended items");
                    vm.SelectRecommendedOnlyCommand.Execute(null);
                    Require(vm.CheckedRuleCount == 0,
                        "SelectRecommendedOnly in category without recommended items must not check other categories");

                    vm.SelectCategoryCommand.Execute(sysTempNav);
                    Require(vm.HasSelectableRecommendedInScope && !vm.IsCurrentScopeRecommendedSelected,
                        "SystemTempAndShader category has selectable Tier-1 rule");
                    vm.SelectRecommendedOnlyCommand.Execute(null);
                    Require(vm.CheckedRuleCount == 1 && vm.AllRuleItems.Single(i => i.IsChecked).RuleId == r1.RuleId
                        && vm.IsCurrentScopeRecommendedSelected,
                        "SelectRecommendedOnly in selected category must check only that category's recommended items");
                    vm.SelectRecommendedOnlyCommand.Execute(null);
                    Require(vm.CheckedRuleCount == 0 && !vm.IsCurrentScopeRecommendedSelected,
                        "Repeated click in selected category must uncheck that category's items");

                    // 谨慎分类无 1 档建议项
                    vm.SelectCategoryCommand.Execute(cautionNav);
                    Require(!vm.HasSelectableRecommendedInScope && !vm.IsCurrentScopeRecommendedSelected,
                        "Caution category must report HasSelectableRecommendedInScope=false");
                    // 取消分类筛选恢复全部视图（同时重置谨慎项显隐以继续后续断言）
                    vm.ToggleShowCautionItemsCommand.Execute(null);
                    Require(vm.SelectedCategoryFilter is null && !vm.ShowCautionItems,
                        "Toggling ShowCautionItems off from Caution category should reset category filter");

                    // 3. 展开谨慎项并选中 -> 右侧显示三层结构化风险卡
                    vm.ToggleShowCautionItemsCommand.Execute(null);
                    Require(vm.ShowCautionItems && vm.VisibleRuleItems.Count == 3, "Toggling caution must reveal Caution items");
                    CleanupRuleItemViewModel cautionVm = vm.VisibleRuleItems.Single(i => i.IsCaution);
                    vm.SelectRuleCommand.Execute(cautionVm);
                    Require(vm.SelectedRuleIsCaution && vm.DetailHeaderTitle.Contains("谨慎"), "Selecting caution rule must show caution risk card");

                    // 4. 视图切换（07 -> 08 隔离区 -> 11 AI 疑似残留 -> 07）
                    vm.OpenQuarantineViewCommand.Execute(null);
                    Require(vm.IsQuarantineView, "Should switch to Board 08 Quarantine view");
                    vm.OpenAiResidualViewCommand.Execute(null);
                    Require(vm.IsAiResidualView && !vm.CanConfirmAiQuarantine, "Board 11 confirm button must stay disabled when nothing checked");
                    vm.BackToRulesViewCommand.Execute(null);
                    Require(vm.IsRulesMainView, "Should return to Board 07 Rules main view");

                    // 5. 布局测量不抛 XAML 绑定或资源异常
                    page.Measure(new Size(1000, 640));
                    page.Arrange(new Rect(0, 0, 1000, 640));
                    page.UpdateLayout();

                    // 6. 验证两个二次确认弹窗 XAML 构造正常且默认禁用危险操作
                    var confirmWin = new CleanupConfirmWindow([cautionVm]);
                    confirmWin.Measure(new Size(620, 500));
                    var delWin = new QuarantineDeleteConfirmWindow(new QuarantineBatchRecord(
                        1, "q-test-01", @"C:\", @"C:\.MoveFolderQuarantine\q-test-01",
                        "测试批次", @"C:\Temp", "sys-temp-24h", false, DateTimeOffset.UtcNow, 4096, 1, []));
                    delWin.Measure(new Size(560, 420));

                    // 7. 验证 MainViewModel 切换至垃圾清理页时「不自动开始扫描」（由用户主动点击开始）
                    var mainVm = new MainViewModel();
                    mainVm.IsMigrating = false; // 跳过无 Dispatcher 泵的异步启动恢复锁
                    mainVm.ShowCleanupCommand.Execute(null);
                    Require(mainVm.IsCleanupMode && mainVm.CleanupNavTag == "Active",
                        "MainViewModel must support switching to Cleanup mode");
                    if (mainVm.CleanupPage is CleanupPage mainCleanupPage)
                    {
                        Require(!mainCleanupPage.ViewModel.IsScanning && mainCleanupPage.ViewModel.CurrentSnapshot is null && mainCleanupPage.ViewModel.ShowIdleScanPrompt,
                            "Entering Cleanup page must NOT auto-start scanning");
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
                throw new InvalidOperationException($"Cleanup WPF UI smoke failed: {staError.Message}", staError);
            }
            return Task.CompletedTask;
        });

        await test("CleanupService-reuses-fresh-MFT-cache-with-24h-and-live-drift-guard", async () =>
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "MoveFolder_P3MftReuse_" + Guid.NewGuid().ToString("N"));
            string cacheSubDir = Path.Combine(tempRoot, "TempBox");
            string quarantineRoot = Path.Combine(tempRoot, ".MoveFolderQuarantine");
            Directory.CreateDirectory(cacheSubDir);

            try
            {
                string expiredFile = Path.Combine(cacheSubDir, "expired.tmp");
                string recentFile = Path.Combine(cacheSubDir, "recent.tmp");
                string unknownTimeFile = Path.Combine(cacheSubDir, "unknown_ts.tmp");
                string touchedAfterSnapFile = Path.Combine(cacheSubDir, "touched_after_snap.tmp");

                File.WriteAllBytes(expiredFile, new byte[4096]);
                File.WriteAllBytes(recentFile, new byte[2048]);
                File.WriteAllBytes(unknownTimeFile, new byte[1024]);
                File.WriteAllBytes(touchedAfterSnapFile, new byte[4096]);

                DateTime oldTime = DateTime.UtcNow.AddHours(-48);
                File.SetLastWriteTimeUtc(expiredFile, oldTime);
                File.SetLastWriteTimeUtc(unknownTimeFile, oldTime);
                File.SetLastWriteTimeUtc(touchedAfterSnapFile, oldTime);

                // 读取真实文件 FileId 构建合成 RawNtfsMft 快照并放入独立 VolumeSnapshotCache
                var idSvc = new PathIdentityService();
                ulong expiredId = idSvc.ReadEntry(expiredFile).FileId;
                ulong touchedId = idSvc.ReadEntry(touchedAfterSnapFile).FileId;
                string driveRoot = Path.GetPathRoot(tempRoot)!;
                DateTimeOffset snapTime = DateTimeOffset.UtcNow.AddMinutes(-2);

                var pool = new Utf8NamePool();
                (byte p0, int o0, ushort l0) = pool.AddName(Path.GetFileName(tempRoot));
                (byte p1, int o1, ushort l1) = pool.AddName("TempBox");
                (byte p2, int o2, ushort l2) = pool.AddName("expired.tmp");
                (byte p3, int o3, ushort l3) = pool.AddName("recent.tmp");
                (byte p4, int o4, ushort l4) = pool.AddName("unknown_ts.tmp");
                (byte p5, int o5, ushort l5) = pool.AddName("touched_after_snap.tmp");

                ScanNodeRecord[] nodes =
                [
                    new() { NodeId = 0, ParentNodeId = 0, NamePageIndex = p0, NameOffset = o0, NameLength = l0, Flags = ScanNodeFlags.IsDirectory, LogicalBytes = 11264, UniqueAllocatedBytes = 11264 },
                    new() { NodeId = 1, ParentNodeId = 0, NamePageIndex = p1, NameOffset = o1, NameLength = l1, Flags = ScanNodeFlags.IsDirectory, LogicalBytes = 11264, UniqueAllocatedBytes = 11264 },
                    new() { NodeId = 2, ParentNodeId = 1, MftRecordNumber = expiredId & 0x0000_FFFF_FFFF_FFFFUL, NamePageIndex = p2, NameOffset = o2, NameLength = l2, LogicalBytes = 4096, UniqueAllocatedBytes = 4096, LastWriteUtcTicks = oldTime.Ticks },
                    new() { NodeId = 3, ParentNodeId = 1, MftRecordNumber = 1003, NamePageIndex = p3, NameOffset = o3, NameLength = l3, LogicalBytes = 2048, UniqueAllocatedBytes = 2048, LastWriteUtcTicks = DateTime.UtcNow.AddHours(-2).Ticks },
                    new() { NodeId = 4, ParentNodeId = 1, MftRecordNumber = 1004, NamePageIndex = p4, NameOffset = o4, NameLength = l4, LogicalBytes = 1024, UniqueAllocatedBytes = 1024, LastWriteUtcTicks = 0 },
                    new() { NodeId = 5, ParentNodeId = 1, MftRecordNumber = touchedId & 0x0000_FFFF_FFFF_FFFFUL, NamePageIndex = p5, NameOffset = o5, NameLength = l5, LogicalBytes = 4096, UniqueAllocatedBytes = 4096, LastWriteUtcTicks = oldTime.Ticks }
                ];

                var mftSnap = new ScanSnapshot
                {
                    Volume = new ScanVolumeInfo(driveRoot, "Test", "", 1, "NTFS", 100_000_000, 50_000_000, true, "OK"),
                    Backend = ScanBackendKind.RawNtfsMft,
                    Coverage = ScanCoverageStatus.Complete,
                    StartedAtUtc = snapTime.AddSeconds(-1),
                    CompletedAtUtc = snapTime,
                    PolicyVersion = MigrationSafetyPolicy.CurrentPolicyVersion,
                    ScanRootPath = tempRoot,
                    Nodes = nodes,
                    CsrOffsets = [0, 1, 5, 5, 5, 5, 5],
                    CsrChildren = [1, 2, 3, 4, 5],
                    NamePool = pool
                };

                using var cache = new VolumeSnapshotCache(
                    options: new VolumeSnapshotCacheOptions { EvictionSweepInterval = TimeSpan.Zero },
                    liveProbeProvider: _ => new VolumeLiveProbe(driveRoot, 50_000_000, 100_000_000));
                cache.Put(mftSnap, "Test", 0.5);

                var rule = new CleanupRuleDefinition(
                    "test-mft-24h", BuiltInCleanupRules.CurrentRuleVersion, "MFT 复用测试（>24h）", "测试",
                    CleanupCategory.SystemTempAndShader, CleanupRecommendationTier.Recommended,
                    CleanupExecutionChannel.FileQuarantine, CleanupOfficialApiKind.None,
                    [new CleanupPathRuleSpec(CleanupKnownRootKind.CustomTestRoot, string.Empty, ["*"], true, CustomRootOverride: cacheSubDir)],
                    [], 24, "可重建", "同卷隔离可恢复", true);

                var qStore = new QuarantineStore(customQuarantineRootOverride: quarantineRoot);
                var svc = new CleanupService(qStore, snapshotCache: cache);

                // 1. 扫描应命中新鲜 MFT 缓存：命中 expired + touched_after_snap（2 项），跳过 recent（<24h）与 unknown_ts（LastWriteUtcTicks=0 失败封闭）
                CleanupScanSnapshot scanRes = await svc.ScanAsync([rule]);
                CleanupRuleScanResult r = scanRes.Rules[0];
                Require(r.StatusNote.Contains("MFT 缓存"), $"Expected MFT cache hit tag in StatusNote, got: {r.StatusNote}");
                Require(r.MatchedCount == 2, $"Expected 2 expired matches from MFT cache, got {r.MatchedCount}");
                Require(r.SkippedRecentOrLockedCount == 2, $"Expected 2 skipped (recent + unknown timestamp), got {r.SkippedRecentOrLockedCount}");
                Require(r.VerificationExpectations[0].ExpectedFiles.All(f => f.ExpectedFileId != 0), "MFT cache path must populate real FileId");

                // 2. 模拟快照后 touched_after_snap 被刚改写（更新为当前时间）+ 新建文件
                File.SetLastWriteTimeUtc(touchedAfterSnapFile, DateTime.UtcNow);
                string createdAfterSnap = Path.Combine(cacheSubDir, "created_after_snap.tmp");
                File.WriteAllBytes(createdAfterSnap, new byte[512]);

                // 3. 执行清理：仅隔离真正未变动的 expiredFile；touched_after_snap 因实时 24h/时间戳漂移复核被安全跳过，createdAfterSnap 绝不触碰
                CleanupExecutionResult exec = await svc.ExecuteRuleCleanupAsync(scanRes, ["test-mft-24h"], CleanupFileAction.QuarantineSameVolume);
                Require(exec.Items[0].Status == CleanupItemExecutionStatus.Quarantined && exec.Items[0].ProcessedCount == 1,
                    $"Expected only 1 file quarantined, got {exec.Items[0].ProcessedCount}: {exec.SummaryMessage}");
                Require(!File.Exists(expiredFile), "Expired unchanged file should be quarantined");
                Require(File.Exists(touchedAfterSnapFile), "File touched after MFT snapshot must be skipped by live 24h drift check");
                Require(File.Exists(recentFile), "Recent file must remain untouched");
                Require(File.Exists(unknownTimeFile), "Unknown-timestamp file must remain untouched");
                Require(File.Exists(createdAfterSnap), "File created after MFT snapshot must remain untouched");
            }
            finally
            {
                try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
            }
        });

        await test("BuiltInCleanupRules-per-software-split-wildcard-expansion-and-spec-min-age", async () =>
        {
            // 1. 验证规则库按单款软件拆分、无旧聚合规则、无重复 RuleId、覆盖主流软件与开发包管理器
            var all = BuiltInCleanupRules.All;
            var softwareRules = all.Where(r => r.Category == CleanupCategory.SoftwareAndDeveloperCache).ToArray();
            Require(softwareRules.Length >= 50, $"Expected >=50 independent software rules, got {softwareRules.Length}");
            Require(all.Select(r => r.RuleId).Distinct(StringComparer.OrdinalIgnoreCase).Count() == all.Count,
                "All BuiltInCleanupRules must have unique RuleIds");
            Require(BuiltInCleanupRules.FindById("browser-cache-only") is null
                && BuiltInCleanupRules.FindById("app-recreatable-cache") is null
                && BuiltInCleanupRules.FindById("dev-package-cache") is null,
                "Old multi-software aggregated rules must be split into per-software rules");

            string[] requiredIds =
            [
                "browser-edge-cache", "browser-chrome-cache", "browser-firefox-cache",
                "app-vscode-cache", "app-cursor-cache", "app-blender-cache",
                "app-jetbrains-idea-cache", "app-jetbrains-pycharm-cache", "app-android-studio-cache",
                "app-wechat-cache", "app-qq-cache", "app-dingtalk-cache", "app-feishu-cache",
                "app-steam-cache", "app-cloudmusic-cache",
                "dev-npm-cache", "dev-pnpm-cache", "dev-pip-cache", "dev-uv-cache", "dev-nuget-cache", "dev-gradle-cache", "dev-maven-cache"
            ];
            foreach (string id in requiredIds)
            {
                Require(BuiltInCleanupRules.FindById(id) is not null, $"Missing required independent rule: {id}");
            }

            foreach (CleanupRuleDefinition browserRule in all.Where(r => r.RuleId.StartsWith("browser-", StringComparison.OrdinalIgnoreCase)))
            {
                Require(browserRule.IsBrowserCacheRule, $"Browser rule {browserRule.RuleId} must enable IsBrowserCacheRule protection");
            }

            // 2. 验证有界单层通配符展开、路径穿越阻断与单 Spec MinAgeHoursOverride
            string tempRoot = Path.Combine(Path.GetTempPath(), "MoveFolder_P3Wildcard_" + Guid.NewGuid().ToString("N"));
            try
            {
                string p1Cache = Path.Combine(tempRoot, "User Data", "Profile 1", "Cache");
                string p2Cache = Path.Combine(tempRoot, "User Data", "Profile 2", "Cache");
                string p1Login = Path.Combine(tempRoot, "User Data", "Profile 1");
                string logsDir = Path.Combine(tempRoot, "logs");
                Directory.CreateDirectory(p1Cache);
                Directory.CreateDirectory(p2Cache);
                Directory.CreateDirectory(logsDir);

                File.WriteAllBytes(Path.Combine(p1Cache, "f_001"), new byte[1024]);
                File.WriteAllBytes(Path.Combine(p2Cache, "f_002"), new byte[2048]);
                File.WriteAllBytes(Path.Combine(p1Login, "Login Data"), new byte[4096]);
                string recentLog = Path.Combine(logsDir, "today.log");
                string oldLog = Path.Combine(logsDir, "old.log");
                File.WriteAllBytes(recentLog, new byte[512]);
                File.WriteAllBytes(oldLog, new byte[768]);
                File.SetLastWriteTimeUtc(oldLog, DateTime.UtcNow.AddHours(-48));

                // 穿越路径必须被拒绝
                Require(CleanupService.ExpandSafeRelativeDirectories(tempRoot, @"..\Windows").Count == 0,
                    "Parent directory traversal must be rejected");
                Require(CleanupService.ExpandSafeRelativeDirectories(tempRoot, @"User Data\Profile *\Cache").Count == 2,
                    "Wildcard segment 'Profile *' must expand to both profiles");

                var customRule = new CleanupRuleDefinition(
                    RuleId: "test-wildcard-and-age",
                    Version: BuiltInCleanupRules.CurrentRuleVersion,
                    DisplayName: "通配与分目录年龄测试",
                    ShortDescription: "测试",
                    Category: CleanupCategory.SoftwareAndDeveloperCache,
                    Tier: CleanupRecommendationTier.Recommended,
                    Channel: CleanupExecutionChannel.FileQuarantine,
                    OfficialApiKind: CleanupOfficialApiKind.None,
                    TargetSpecs:
                    [
                        new CleanupPathRuleSpec(CleanupKnownRootKind.CustomTestRoot, @"User Data\Profile *\Cache", ["*"], true, CustomRootOverride: tempRoot),
                        new CleanupPathRuleSpec(CleanupKnownRootKind.CustomTestRoot, "logs", ["*"], true, CustomRootOverride: tempRoot, MinAgeHoursOverride: 24)
                    ],
                    RequiredStoppedProcesses: [],
                    MinAgeHours: 0,
                    CostAndReclaimNote: "测试",
                    ChannelBadgeText: "同卷隔离可恢复",
                    IsRebuildable: true);

                var svc = new CleanupService();
                CleanupScanSnapshot snap = await svc.ScanAsync([customRule]);
                CleanupRuleScanResult res = snap.Rules[0];
                // 应命中 p1Cache/f_001 (1024) + p2Cache/f_002 (2048) + logs/old.log (768) = 3 文件 / 3840 字节；跳过 recentLog (512)
                Require(res.MatchedCount == 3 && res.TotalBytes == 3840,
                    $"Expected 3 files / 3840 bytes (2 profile caches + 1 old log), got {res.MatchedCount} files / {res.TotalBytes} bytes");
                Require(res.SkippedRecentOrLockedCount == 1,
                    $"Expected 1 skipped recent log (<24h), got {res.SkippedRecentOrLockedCount}");
            }
            finally
            {
                try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
            }
        });

        await test("Cleanup-progress-software-grouping-and-virtualized-detail-search", async () =>
        {
            // 1. 验证所有软件与开发者缓存规则均分配了合法分组，且覆盖 6 个业务分组
            var swRules = BuiltInCleanupRules.All.Where(r => r.Category == CleanupCategory.SoftwareAndDeveloperCache).ToArray();
            Require(swRules.All(r => !string.IsNullOrWhiteSpace(r.GroupId) && !string.IsNullOrWhiteSpace(r.GroupDisplayName)),
                "Every SoftwareAndDeveloperCache rule must have GroupId and GroupDisplayName");
            Require(swRules.Select(r => r.GroupId!).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 6,
                "Expected 6 logical software groups");

            // 2. 验证确定性扫描进度上报（0% -> 100%）
            var progressReports = new List<CleanupScanProgress>();
            var syncProgress = new SynchronousProgress<CleanupScanProgress>(p => progressReports.Add(p));
            var svc = new CleanupService();
            var sampleRules = new[]
            {
                BuiltInCleanupRules.FindById("browser-edge-cache")!,
                BuiltInCleanupRules.FindById("browser-chrome-cache")!
            };
            await svc.ScanAsync(
                rules: sampleRules.Select(r => r with { TargetSpecs = [] }).ToArray(),
                detailedProgress: syncProgress);
            Require(progressReports.Count >= 4, $"Expected multiple deterministic progress updates, got {progressReports.Count}");
            Require(progressReports[0].CompletedSteps == 0 && progressReports[^1].CompletedSteps == 2 && progressReports[^1].Percent >= 99.9,
                $"Expected progress 0/2 -> 2/2 (100%), got {progressReports[0].CompletedSteps} -> {progressReports[^1].CompletedSteps}");

            // 3. 验证 10,000 条大明细构建、多关键词搜索、排序及右侧前 15 项截断
            var edgeRule = BuiltInCleanupRules.FindById("browser-edge-cache")!;
            var chromeRule = BuiltInCleanupRules.FindById("browser-chrome-cache")!;
            var vscodeRule = BuiltInCleanupRules.FindById("app-vscode-cache")!;
            var ideaRule = BuiltInCleanupRules.FindById("app-jetbrains-idea-cache")!;
            var now = DateTimeOffset.UtcNow;

            var bigExpectations = new CleanupFileExpectation[10_000];
            var bigPreviews = new CleanupPreviewItem[200];
            for (int i = 0; i < 10_000; i++)
            {
                string name = i == 42 ? "target_jetbrains_special.vsix" : $"cache_file_{i:D5}.bin";
                string full = $@"C:\Users\Test\AppData\Local\Cache\{name}";
                long sz = (10_000 - i) * 1024L;
                bigExpectations[i] = new CleanupFileExpectation(full, (ulong)(i + 1), sz, now.UtcTicks);
                if (i < 200)
                {
                    bigPreviews[i] = new CleanupPreviewItem($"id:{i}", name, full, sz, now.UtcTicks, (ulong)(i + 1), false, "隔离区");
                }
            }

            var bigChromeScan = new CleanupRuleScanResult(
                chromeRule,
                bigPreviews,
                [new CleanupDirectoryExpectation(@"C:\Users\Test\AppData\Local\Cache", null, now, bigExpectations)],
                bigExpectations.Sum(f => f.ExpectedLogicalBytes),
                10_000, 0, 0, [], true, "共 10,000 项", now);

            var builtRows = CleanupDetailListWindow.BuildAllDetailRows(bigChromeScan, "#0F7B4B");
            Require(builtRows.Length == 10_000, $"Expected 10,000 projected detail rows, got {builtRows.Length}");

            var (searchHits, searchBytes) = CleanupDetailListWindow.FilterAndSortRows(builtRows, "jetbrains vsix", CleanupDetailSortMode.SizeDescending);
            Require(searchHits.Length == 1 && searchHits[0].FileName == "target_jetbrains_special.vsix" && searchBytes == bigExpectations[42].ExpectedLogicalBytes,
                $"Multi-token search mismatch: got {searchHits.Length} hits");

            var (nameSorted, _) = CleanupDetailListWindow.FilterAndSortRows(builtRows, null, CleanupDetailSortMode.NameAscending);
            Require(nameSorted[0].FileName == "cache_file_00000.bin", $"Name sort mismatch: first={nameSorted[0].FileName}");

            // 4. STA 下验证：右侧前 15 条截断、软件分组折叠/展开、三态勾选（含运行中跳过与混合档位）、以及虚拟化明细窗构造
            Exception? staError = null;
            var thread = new Thread(() =>
            {
                try
                {
                    if (Application.Current is null)
                    {
                        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                        app.Resources.MergedDictionaries.Add(new ResourceDictionary
                        {
                            Source = new Uri("pack://application:,,,/目录迁移工具;component/Resources/AppIcon.xaml", UriKind.Absolute)
                        });
                        app.Resources.MergedDictionaries.Add(new ResourceDictionary
                        {
                            Source = new Uri("pack://application:,,,/目录迁移工具;component/Resources/FluentTheme.xaml", UriKind.Absolute)
                        });
                        app.Resources.MergedDictionaries.Add(new ResourceDictionary
                        {
                            Source = new Uri("pack://application:,,,/目录迁移工具;component/Resources/UserGuideTexts.xaml", UriKind.Absolute)
                        });
                    }

                    var page = new CleanupPage();
                    CleanupViewModel vm = page.ViewModel;

                    var snap = new CleanupScanSnapshot(
                        Guid.NewGuid(), now, MigrationSafetyPolicy.CurrentPolicyVersion, BuiltInCleanupRules.CurrentRuleVersion,
                        [
                            // 浏览器组：Edge 运行中被阻断 + Chrome 10,000 项可选
                            new CleanupRuleScanResult(edgeRule, [], [], 50_000_000, 10, 0, 0, ["msedge"], true, "运行中", now),
                            bigChromeScan,
                            // IDE 组：VS Code（建议清理）+ IntelliJ IDEA（按需清理）
                            new CleanupRuleScanResult(vscodeRule, [], [], 40_000_000, 20, 0, 0, [], true, "ok", now),
                            new CleanupRuleScanResult(ideaRule, [], [], 80_000_000, 30, 0, 0, [], true, "ok", now)
                        ],
                        bigChromeScan.TotalBytes + 170_000_000, bigChromeScan.TotalBytes + 90_000_000, 80_000_000, 0);

                    vm.ApplyScanSnapshot(snap);

                    // 验证 VisibleEntries 按分组展示（2 个软件分组，默认收起）
                    Require(vm.HasVisibleGroups && vm.VisibleEntries.Count == 2,
                        $"Expected 2 software groups in VisibleEntries, got {vm.VisibleEntries.Count}");
                    var groups = vm.VisibleEntries.OfType<CleanupRuleGroupViewModel>().ToArray();
                    Require(groups.Length == 2 && groups.All(g => !g.IsExpanded), "Software groups should default to collapsed");

                    var browserGroup = groups.Single(g => g.GroupId == BuiltInCleanupRules.GroupBrowsersId);
                    var ideGroup = groups.Single(g => g.GroupId == BuiltInCleanupRules.GroupIdeId);

                    // 验证右侧仅预览前 15 条，且 TotalDetailItemCount == 10,000
                    var chromeVm = browserGroup.Items.Single(i => i.RuleId == chromeRule.RuleId);
                    Require(chromeVm.PreviewRows.Count == CleanupRuleItemViewModel.MaxInlinePreviewCount
                        && chromeVm.TotalDetailItemCount == 10_000
                        && chromeVm.HasMoreDetailItems,
                        $"Expected inline preview capped at {CleanupRuleItemViewModel.MaxInlinePreviewCount} with 10,000 total, got {chromeVm.PreviewRows.Count}/{chromeVm.TotalDetailItemCount}");

                    // 浏览器组勾选：自动跳过运行中 Edge，仅勾 Chrome，组状态为 true（所有可选均已勾）
                    vm.ToggleGroupCheckCommand.Execute(browserGroup);
                    Require(chromeVm.IsChecked && !browserGroup.Items.Single(i => i.RuleId == edgeRule.RuleId).IsChecked,
                        "Group check must check Chrome and skip running Edge");
                    Require(browserGroup.GroupCheckState == true, "Browser group should be checked when all selectable items are checked");

                    // 展开 IDE 组后仅勾 VS Code -> IDE 组自动进入半选 null；再点组勾 -> 全选 IDE 组
                    vm.ToggleExpandGroupCommand.Execute(ideGroup);
                    Require(ideGroup.IsExpanded, "IDE group should expand");
                    ideGroup.Items.Single(i => i.RuleId == vscodeRule.RuleId).IsChecked = true;
                    Require(ideGroup.GroupCheckState is null, "IDE group must be indeterminate (null) when 1 of 2 items checked");
                    vm.ToggleGroupCheckCommand.Execute(ideGroup);
                    Require(ideGroup.GroupCheckState == true && ideGroup.Items.All(i => i.IsChecked),
                        "Toggling indeterminate group check must select all selectable items in group");

                    // 「仅勾选建议清理」仅勾 1 档 -> IDE 组（含 1 档 VS Code + 2 档 IDEA）自动变为半选 null
                    vm.SelectRecommendedOnlyCommand.Execute(null);
                    Require(ideGroup.GroupCheckState is null
                        && ideGroup.Items.Single(i => i.RuleId == vscodeRule.RuleId).IsChecked
                        && !ideGroup.Items.Single(i => i.RuleId == ideaRule.RuleId).IsChecked
                        && vm.IsCurrentScopeRecommendedSelected,
                        "SelectRecommendedOnly must only check Tier-1 items and set mixed group to indeterminate");

                    // 再次点击「仅勾选建议清理」-> 取消当前全部已勾建议项
                    vm.SelectRecommendedOnlyCommand.Execute(null);
                    Require(vm.CheckedRuleCount == 0
                        && !vm.IsCurrentScopeRecommendedSelected
                        && ideGroup.GroupCheckState == false
                        && browserGroup.GroupCheckState == false,
                        "Clicking SelectRecommendedOnly again must uncheck all recommended items and reset group states");

                    // 多分类联合测试：两个分类均含 1 档建议项时，选单个分类仅勾本类，切另一类仅勾另一类，未选分类则勾全部并支持再次点击取消
                    var sysTempRule = BuiltInCleanupRules.FindById("sys-temp-24h")!;
                    var multiCatSnap = new CleanupScanSnapshot(
                        Guid.NewGuid(), now, MigrationSafetyPolicy.CurrentPolicyVersion, BuiltInCleanupRules.CurrentRuleVersion,
                        [
                            new CleanupRuleScanResult(sysTempRule, [], [], 10_000_000, 5, 0, 0, [], true, "ok", now),
                            new CleanupRuleScanResult(vscodeRule, [], [], 20_000_000, 8, 0, 0, [], true, "ok", now),
                            new CleanupRuleScanResult(ideaRule, [], [], 30_000_000, 9, 0, 0, [], true, "ok", now)
                        ],
                        60_000_000, 30_000_000, 30_000_000, 0);
                    vm.ApplyScanSnapshot(multiCatSnap);

                    CleanupCategoryNavItem sysCat = vm.CategoryNavItems.Single(c => c.Category == CleanupCategory.SystemTempAndShader);
                    CleanupCategoryNavItem swCat = vm.CategoryNavItems.Single(c => c.Category == CleanupCategory.SoftwareAndDeveloperCache);

                    // 1) 选中「系统临时与着色器缓存」→ 仅勾 sys-temp-24h，不勾软件分类的 vscodeRule
                    vm.SelectCategoryCommand.Execute(sysCat);
                    vm.SelectRecommendedOnlyCommand.Execute(null);
                    Require(vm.CheckedRuleCount == 1
                        && vm.AllRuleItems.Single(i => i.RuleId == sysTempRule.RuleId).IsChecked
                        && !vm.AllRuleItems.Single(i => i.RuleId == vscodeRule.RuleId).IsChecked
                        && vm.IsCurrentScopeRecommendedSelected,
                        "Selecting a single category must only check recommended items in that category");

                    // 2) 切换至「软件与开发者缓存」→ 点击勾选仅新增勾软件分类 1 档（保留系统临时已勾，不勾 2 档 IDEA）
                    vm.SelectCategoryCommand.Execute(swCat);
                    Require(!vm.IsCurrentScopeRecommendedSelected,
                        "Switching to unchecked Software category must show IsCurrentScopeRecommendedSelected=false");
                    vm.SelectRecommendedOnlyCommand.Execute(null);
                    Require(vm.CheckedRuleCount == 2
                        && vm.AllRuleItems.Single(i => i.RuleId == sysTempRule.RuleId).IsChecked
                        && vm.AllRuleItems.Single(i => i.RuleId == vscodeRule.RuleId).IsChecked
                        && !vm.AllRuleItems.Single(i => i.RuleId == ideaRule.RuleId).IsChecked
                        && vm.IsCurrentScopeRecommendedSelected,
                        "Checking recommended in Software category must check Software Tier-1 without clobbering other categories");

                    // 3) 在软件分类再次点击 → 仅取消软件分类，保留系统临时
                    vm.SelectRecommendedOnlyCommand.Execute(null);
                    Require(vm.CheckedRuleCount == 1
                        && vm.AllRuleItems.Single(i => i.RuleId == sysTempRule.RuleId).IsChecked
                        && !vm.AllRuleItems.Single(i => i.RuleId == vscodeRule.RuleId).IsChecked
                        && !vm.IsCurrentScopeRecommendedSelected,
                        "Second click in Software category must only uncheck Software category");

                    // 4) 取消分类筛选（未选类型）→ 点击勾选全部 1 档，再点取消全部勾选
                    vm.SelectCategoryCommand.Execute(swCat); // 再次点击取消筛选
                    Require(vm.SelectedCategoryFilter is null && !vm.IsCurrentScopeRecommendedSelected,
                        "Clearing category filter when only 1 of 2 categories is checked must report false");
                    vm.SelectRecommendedOnlyCommand.Execute(null);
                    Require(vm.CheckedRuleCount == 2
                        && vm.AllRuleItems.Single(i => i.RuleId == sysTempRule.RuleId).IsChecked
                        && vm.AllRuleItems.Single(i => i.RuleId == vscodeRule.RuleId).IsChecked
                        && !vm.AllRuleItems.Single(i => i.RuleId == ideaRule.RuleId).IsChecked
                        && vm.IsCurrentScopeRecommendedSelected,
                        "With no category selected, SelectRecommendedOnly must check all Tier-1 recommended items across categories");
                    vm.SelectRecommendedOnlyCommand.Execute(null);
                    Require(vm.CheckedRuleCount == 0 && !vm.IsCurrentScopeRecommendedSelected,
                        "Second click with no category selected must uncheck all items");

                    // 布局（常规 1000x640 与最大化 1920x1040 自适应高度）与完整明细弹框 XAML 构造验证
                    page.Measure(new Size(1000, 640));
                    page.Arrange(new Rect(0, 0, 1000, 640));
                    page.Measure(new Size(1920, 1040));
                    page.Arrange(new Rect(0, 0, 1920, 1040));
                    var detailWin = new CleanupDetailListWindow(bigChromeScan);
                    detailWin.Measure(new Size(860, 600));
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
                throw new InvalidOperationException($"Grouping/detail UI test failed: {staError.Message}", staError);
            }
        });

        await test("Cleanup-crossdrive-env-recyclebin-livehits-and-multidisk-overview", async () =>
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "MoveFolder_P3Cross_" + Guid.NewGuid().ToString("N"));
            string envCacheDir = Path.Combine(tempRoot, "custom_pip_cache");
            Directory.CreateDirectory(envCacheDir);
            string cachedWhl = Path.Combine(envCacheDir, "demo-1.0.whl");
            File.WriteAllBytes(cachedWhl, new byte[6144]);

            string? prevPipCache = Environment.GetEnvironmentVariable("PIP_CACHE_DIR", EnvironmentVariableTarget.Process);
            try
            {
                // 1. 验证环境变量重定向扫描 + 禁止将磁盘根目录误配为缓存根
                Environment.SetEnvironmentVariable("PIP_CACHE_DIR", envCacheDir, EnvironmentVariableTarget.Process);
                var pipRule = BuiltInCleanupRules.FindById("dev-pip-cache")!;
                var envOnlyRule = pipRule with
                {
                    TargetSpecs = [new CleanupPathRuleSpec(CleanupKnownRootKind.EnvironmentVariable, string.Empty, ["*"], true, CustomRootOverride: "PIP_CACHE_DIR")]
                };

                var liveReports = new List<CleanupScanProgress>();
                var svc = new CleanupService();
                CleanupScanSnapshot envSnap = await svc.ScanAsync(
                    [envOnlyRule],
                    detailedProgress: new SynchronousProgress<CleanupScanProgress>(p => liveReports.Add(p)));

                Require(envSnap.Rules.Length == 1 && envSnap.Rules[0].MatchedCount == 1 && envSnap.Rules[0].TotalBytes == 6144,
                    $"Expected env-redirected pip cache hit (1 file / 6144 B), got {envSnap.Rules[0].MatchedCount}/{envSnap.Rules[0].TotalBytes}");

                CleanupLiveRuleHit? completedHit = liveReports.LastOrDefault(p => p.CompletedRuleHit is not null)?.CompletedRuleHit;
                Require(completedHit is not null && completedHit.TotalBytes == 6144 && completedHit.Drives.Length == 1,
                    "Expected CleanupScanProgress.CompletedRuleHit to stream real bytes and affected drives");

                // 环境变量指向磁盘根目录（如 C:\）必须被安全护栏拒绝
                string sysRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
                Environment.SetEnvironmentVariable("PIP_CACHE_DIR", sysRoot, EnvironmentVariableTarget.Process);
                CleanupScanSnapshot rootRejectedSnap = await svc.ScanAsync([envOnlyRule]);
                Require(rootRejectedSnap.Rules[0].MatchedCount == 0 && rootRejectedSnap.Rules[0].TotalBytes == 0,
                    "Drive root configured in env var must be strictly rejected by cross-drive root safety guard");

                // 2. 验证全盘回收站谨慎规则定义与短语确认保护
                var rbRule = BuiltInCleanupRules.FindById("caution-recycle-bin-all-drives");
                Require(rbRule is not null
                    && rbRule.Category == CleanupCategory.CautionItems
                    && rbRule.Tier == CleanupRecommendationTier.Caution
                    && rbRule.Channel == CleanupExecutionChannel.OfficialSystemApi
                    && rbRule.OfficialApiKind == CleanupOfficialApiKind.RecycleBinAllDrives
                    && rbRule.CautionReason is { RequiresPhraseConfirmation: true },
                    "Recycle bin rule must exist as Caution OfficialSystemApi with phrase confirmation");

                // 3. STA 下验证：右栏 12 块磁盘 Top 3 截断 + 溢出汇总 + 弹框构造、待扫描范围预览、扫描中实时榜单排序、多盘筛选胶囊
                Exception? staError = null;
                var thread = new Thread(() =>
                {
                    try
                    {
                        if (Application.Current is null)
                        {
                            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                            app.Resources.MergedDictionaries.Add(new ResourceDictionary
                            {
                                Source = new Uri("pack://application:,,,/目录迁移工具;component/Resources/AppIcon.xaml", UriKind.Absolute)
                            });
                            app.Resources.MergedDictionaries.Add(new ResourceDictionary
                            {
                                Source = new Uri("pack://application:,,,/目录迁移工具;component/Resources/FluentTheme.xaml", UriKind.Absolute)
                            });
                            app.Resources.MergedDictionaries.Add(new ResourceDictionary
                            {
                                Source = new Uri("pack://application:,,,/目录迁移工具;component/Resources/UserGuideTexts.xaml", UriKind.Absolute)
                            });
                        }

                        var page = new CleanupPage();
                        CleanupViewModel vm = page.ViewModel;

                        // 待扫描态：覆盖范围预览非空且左栏分类可联动筛选
                        Require(vm.ShowIdleScanPrompt && vm.VisibleIdleScopePreviews.Count >= 6,
                            $"Expected idle scope previews populated before scan, got {vm.VisibleIdleScopePreviews.Count}");
                        CleanupCategoryNavItem cautionCat = vm.CategoryNavItems.Single(c => c.Category == CleanupCategory.CautionItems);
                        vm.SelectCategoryCommand.Execute(cautionCat);
                        Require(vm.VisibleIdleScopePreviews.Count == 1 && vm.VisibleIdleScopePreviews[0].Category == CleanupCategory.CautionItems,
                            "Clicking left category before scan should filter idle scope previews");
                        vm.SelectCategoryCommand.Execute(cautionCat); // 取消筛选

                        // 右栏 12 块磁盘：仅内联展示 Top 3（系统盘置顶，其次紧张/含隔离区），并展示溢出汇总与弹框
                        var mockDisks = new List<CleanupDiskOverviewItem>();
                        for (int i = 0; i < 12; i++)
                        {
                            char letter = (char)('C' + i);
                            bool isSys = letter == 'C';
                            bool isTight = letter == 'H';
                            long total = 500_000_000_000L;
                            long free = isTight ? 20_000_000_000L : (isSys ? 100_000_000_000L : 300_000_000_000L);
                            mockDisks.Add(new CleanupDiskOverviewItem
                            {
                                DriveRoot = $"{letter}:\\",
                                DriveLabel = $"{letter}:",
                                VolumeLabel = isSys ? "系统" : $"磁盘{letter}",
                                IsSystemDrive = isSys,
                                TotalBytes = total,
                                FreeBytes = free,
                                QuarantineBytes = letter == 'F' ? 2_000_000_000L : 0,
                                QuarantineBatchCount = letter == 'F' ? 1 : 0
                            });
                        }
                        vm.ApplyDiskOverviews(mockDisks);
                        Require(vm.TotalDiskCount == 12 && vm.TopDiskOverviews.Count == 3 && vm.HasOverflowDisks,
                            $"Expected 12 total disks capped to Top 3 inline, got total={vm.TotalDiskCount}, top={vm.TopDiskOverviews.Count}");
                        Require(vm.TopDiskOverviews[0].DriveLabel == "C:"
                            && vm.TopDiskOverviews[1].DriveLabel == "H:"
                            && vm.TopDiskOverviews[2].DriveLabel == "F:",
                            $"Expected urgency order C:, H: (96%), F: (quarantine), got {string.Join(",", vm.TopDiskOverviews.Select(d => d.DriveLabel))}");

                        var diskWin = new CleanupDiskOverviewWindow(vm.AllDiskOverviews);
                        diskWin.Measure(new Size(780, 560));

                        // 扫描中实时发现榜单：按体积降序排列，且最新命中高亮
                        vm.ApplyLiveScanHit(new CleanupLiveRuleHit("r1", "小项", CleanupCategory.SystemTempAndShader, CleanupRecommendationTier.Recommended, 10_000_000, 5, 2, true, false, ["C:"]));
                        vm.ApplyLiveScanHit(new CleanupLiveRuleHit("r2", "跨盘大项", CleanupCategory.SoftwareAndDeveloperCache, CleanupRecommendationTier.OnDemand, 500_000_000, 120, 1, true, false, ["D:"]));
                        Require(vm.LiveDiscoveredBytes == 510_000_000 && vm.LiveHitRuleCount == 2 && vm.LiveSkippedCount == 3,
                            $"Live metrics mismatch: bytes={vm.LiveDiscoveredBytes}, hits={vm.LiveHitRuleCount}, skipped={vm.LiveSkippedCount}");
                        Require(vm.LiveScanHits.Count == 2 && vm.LiveScanHits[0].RuleId == "r2" && vm.LiveScanHits[0].IsLatestHighlight,
                            "Live hits must be ordered descending by size with latest hit highlighted");

                        // 扫描后多盘筛选胶囊（C: + D:）
                        var now = DateTimeOffset.UtcNow;
                        var steamRule = BuiltInCleanupRules.FindById("app-steam-cache")!;
                        var sysTempRule = BuiltInCleanupRules.FindById("sys-temp-24h")!;
                        var multiDriveSnap = new CleanupScanSnapshot(
                            Guid.NewGuid(), now, MigrationSafetyPolicy.CurrentPolicyVersion, BuiltInCleanupRules.CurrentRuleVersion,
                            [
                                new CleanupRuleScanResult(sysTempRule, [new CleanupPreviewItem("1", "a.tmp", @"C:\Temp\a.tmp", 10_000, now.UtcTicks, 1, false, "隔离区")], [], 10_000, 1, 0, 0, [], true, "ok", now),
                                new CleanupRuleScanResult(steamRule, [new CleanupPreviewItem("2", "s.bin", @"D:\Steam\depotcache\s.bin", 50_000, now.UtcTicks, 2, false, "隔离区")], [], 50_000, 1, 0, 0, [], true, "ok", now)
                            ],
                            60_000, 60_000, 0, 0);
                        vm.ApplyScanSnapshot(multiDriveSnap);

                        Require(vm.HasMultipleDrivesInResults && vm.DriveFilterChips.Count == 3,
                            $"Expected multi-drive filter chips (All + C: + D:), got count={vm.DriveFilterChips.Count}");
                        CleanupDriveFilterChip dChip = vm.DriveFilterChips.Single(c => c.DriveLabel == "D:");
                        vm.SelectDriveFilterCommand.Execute(dChip);
                        Require(vm.VisibleRuleItems.Count == 1 && vm.VisibleRuleItems[0].RuleId == steamRule.RuleId,
                            "Filtering by D: must only display rules affecting D:");

                        page.Measure(new Size(1200, 760));
                        page.Arrange(new Rect(0, 0, 1200, 760));
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
                    throw new InvalidOperationException($"Cross-drive / multi-disk UI test failed: {staError.Message}", staError);
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("PIP_CACHE_DIR", prevPipCache, EnvironmentVariableTarget.Process);
                try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
            }
        });

        await test("Cleanup-execution-progress-targeted-rescan-and-receipt-banner", async () =>
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "MoveFolder_P3ExecProg_" + Guid.NewGuid().ToString("N"));
            string dirA = Path.Combine(tempRoot, "RuleA");
            string dirB = Path.Combine(tempRoot, "RuleB");
            string quarantineRoot = Path.Combine(tempRoot, ".MoveFolderQuarantine");
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);

            try
            {
                for (int i = 0; i < 85; i++)
                {
                    File.WriteAllBytes(Path.Combine(dirA, $"a_{i:D3}.cache"), new byte[1024]);
                }
                File.WriteAllBytes(Path.Combine(dirB, "b_001.cache"), new byte[4096]);

                var ruleA = new CleanupRuleDefinition(
                    RuleId: "exec-prog-rule-a",
                    Version: BuiltInCleanupRules.CurrentRuleVersion,
                    DisplayName: "测试规则 A（多文件缓存）",
                    ShortDescription: "测试",
                    Category: CleanupCategory.SystemTempAndShader,
                    Tier: CleanupRecommendationTier.Recommended,
                    Channel: CleanupExecutionChannel.FileQuarantine,
                    OfficialApiKind: CleanupOfficialApiKind.None,
                    TargetSpecs: [new CleanupPathRuleSpec(CleanupKnownRootKind.CustomTestRoot, string.Empty, ["*"], true, CustomRootOverride: dirA)],
                    RequiredStoppedProcesses: [],
                    MinAgeHours: 0,
                    CostAndReclaimNote: "可重建",
                    ChannelBadgeText: "同卷隔离可恢复",
                    IsRebuildable: true);

                var ruleB = ruleA with
                {
                    RuleId = "exec-prog-rule-b",
                    DisplayName = "测试规则 B（未勾选保留项）",
                    TargetSpecs = [new CleanupPathRuleSpec(CleanupKnownRootKind.CustomTestRoot, string.Empty, ["*"], true, CustomRootOverride: dirB)]
                };

                var qStore = new QuarantineStore(customQuarantineRootOverride: quarantineRoot);
                var svc = new CleanupService(qStore);

                // 1. 初始扫描（规则 A 85 KB + 规则 B 4 KB）
                CleanupScanSnapshot initialSnap = await svc.ScanAsync([ruleA, ruleB]);
                Require(initialSnap.TotalReclaimableBytes == 85 * 1024 + 4096,
                    $"Unexpected initial bytes: {initialSnap.TotalReclaimableBytes}");

                // 2. 仅执行规则 A，验证后台线程执行并持续上报确定性结构化进度
                var execProgressEvents = new List<CleanupExecutionProgress>();
                CleanupExecutionResult execResult = await svc.ExecuteRuleCleanupAsync(
                    initialSnap,
                    [ruleA.RuleId],
                    CleanupFileAction.QuarantineSameVolume,
                    detailedProgress: new SynchronousProgress<CleanupExecutionProgress>(p => execProgressEvents.Add(p)));

                Require(execResult.QuarantinedBytes == 85 * 1024 && execResult.Items[0].Status == CleanupItemExecutionStatus.Quarantined,
                    $"Expected rule A quarantined 85KB, got {execResult.SummaryMessage}");
                Require(execProgressEvents.Count >= 4
                    && execProgressEvents.Any(p => p.PhaseText.Contains("复核"))
                    && execProgressEvents.Any(p => p.PhaseText.Contains("隔离"))
                    && execProgressEvents[^1].OverallPercent >= 99.9,
                    $"Expected structured verification + quarantine progress ending at 100%, got {execProgressEvents.Count} events");

                // 3. 定向局部复扫：仅重扫规则 A，规则 A 清零，规则 B 原样保留
                CleanupScanSnapshot mergedSnap = await svc.RescanRulesAndMergeSnapshotAsync(initialSnap, [ruleA.RuleId]);
                CleanupRuleScanResult mergedA = mergedSnap.Rules.Single(r => r.Rule.RuleId == ruleA.RuleId);
                CleanupRuleScanResult mergedB = mergedSnap.Rules.Single(r => r.Rule.RuleId == ruleB.RuleId);
                Require(mergedA.TotalBytes == 0 && mergedA.MatchedCount == 0 && mergedA.PreviewItems.Length == 0,
                    $"Expected rule A cleared after targeted rescan, got {mergedA.MatchedCount}/{mergedA.TotalBytes}");
                Require(mergedB.TotalBytes == 4096 && mergedB.MatchedCount == 1,
                    $"Expected untouched rule B preserved (4096 B), got {mergedB.MatchedCount}/{mergedB.TotalBytes}");
                Require(mergedSnap.TotalReclaimableBytes == 4096,
                    $"Expected merged snapshot total 4096 B, got {mergedSnap.TotalReclaimableBytes}");

                // 4. STA 下验证 ViewModel 原地更新（取消已清理项勾选、切走已清空右侧明细、生成方案 A 回执横幅）
                Exception? staError = null;
                var thread = new Thread(() =>
                {
                    try
                    {
                        if (Application.Current is null)
                        {
                            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                            app.Resources.MergedDictionaries.Add(new ResourceDictionary
                            {
                                Source = new Uri("pack://application:,,,/目录迁移工具;component/Resources/AppIcon.xaml", UriKind.Absolute)
                            });
                            app.Resources.MergedDictionaries.Add(new ResourceDictionary
                            {
                                Source = new Uri("pack://application:,,,/目录迁移工具;component/Resources/FluentTheme.xaml", UriKind.Absolute)
                            });
                            app.Resources.MergedDictionaries.Add(new ResourceDictionary
                            {
                                Source = new Uri("pack://application:,,,/目录迁移工具;component/Resources/UserGuideTexts.xaml", UriKind.Absolute)
                            });
                        }

                        var page = new CleanupPage();
                        CleanupViewModel vm = page.ViewModel;
                        vm.ApplyScanSnapshot(initialSnap);

                        var vmA = vm.AllRuleItems.Single(i => i.RuleId == ruleA.RuleId);
                        vmA.IsChecked = true;
                        vm.SelectRuleCommand.Execute(vmA);
                        Require(vm.CheckedRuleCount == 1 && vm.SelectedRule?.RuleId == ruleA.RuleId && vm.SelectedRule.PreviewRows.Count == 85,
                            "Precondition failed: rule A should be checked and selected with 85 preview rows");

                        vm.ApplyPostCleanupSnapshot(mergedSnap, [ruleA.RuleId]);
                        vm.ApplyExecutionReceipt(execResult);

                        // 验证：已清完的规则 A 不再勾选、从默认可见列表消失、右侧明细自动切换到剩余的规则 B（不再残留 85 条旧明细！）
                        Require(vm.CheckedRuleCount == 0, $"Cleaned rule must be unchecked, got CheckedRuleCount={vm.CheckedRuleCount}");
                        Require(vm.VisibleRuleItems.Count == 1 && vm.VisibleRuleItems[0].RuleId == ruleB.RuleId,
                            $"Only remaining rule B should be visible, got count={vm.VisibleRuleItems.Count}");
                        Require(vm.SelectedRule?.RuleId == ruleB.RuleId && vm.SelectedRule.PreviewRows.Count == 1,
                            $"Right panel must switch away from cleaned rule A to rule B, got {vm.SelectedRule?.RuleId} ({vm.SelectedRule?.PreviewRows.Count} rows)");

                        // 验证方案 A 页内完成回执横幅状态与关闭命令
                        Require(vm.HasExecutionReceipt && !vm.ExecutionReceiptIsWarning && vm.ExecutionReceiptHasQuarantined,
                            "Scheme A receipt banner must show green success with quarantine shortcut");
                        vm.DismissExecutionReceiptCommand.Execute(null);
                        Require(!vm.HasExecutionReceipt, "Dismissing receipt banner must hide it");

                        page.Measure(new Size(1280, 800));
                        page.Arrange(new Rect(0, 0, 1280, 800));
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
                    throw new InvalidOperationException($"Post-cleanup UI update test failed: {staError.Message}", staError);
                }
            }
            finally
            {
                try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
            }
        });

        await test("DeclarativeRuleEngine-vocabulary-pattern-matcher-and-shared-hotspot-analyzer", () =>
        {
            // 1. 内嵌 JSON 清理规则加载与 4 种模板展开完整性断言
            IReadOnlyList<CleanupRuleDefinition> allCleanup = BuiltInCleanupRules.All;
            Require(allCleanup.Count == 80, $"Expected 80 embedded cleanup rules, got {allCleanup.Count}");
            Require(BuiltInCleanupRules.LoadedManifestVersion == BuiltInCleanupRules.CurrentRuleVersion,
                "Loaded manifest version must match CurrentRuleVersion");

            CleanupRuleDefinition edge = BuiltInCleanupRules.FindById("browser-edge-cache")!;
            Require(edge.IsBrowserCacheRule && edge.TargetSpecs.Length == 13 && edge.GroupId == BuiltInCleanupRules.GroupBrowsersId,
                $"Chromium template expansion mismatch for Edge: specs={edge.TargetSpecs.Length}, group={edge.GroupId}");

            CleanupRuleDefinition cursor = BuiltInCleanupRules.FindById("app-cursor-cache")!;
            Require(cursor.TargetSpecs.Length == 9 && cursor.GroupId == BuiltInCleanupRules.GroupIdeId,
                $"VSCode-family template + extraSpecs mismatch for Cursor: specs={cursor.TargetSpecs.Length}");

            CleanupRuleDefinition idea = BuiltInCleanupRules.FindById("app-jetbrains-idea-cache")!;
            Require(idea.TargetSpecs.Length == 10 && idea.Tier == CleanupRecommendationTier.OnDemand && idea.GroupId == BuiltInCleanupRules.GroupIdeId,
                $"JetBrains template expansion mismatch for IDEA: specs={idea.TargetSpecs.Length}");

            CleanupRuleDefinition slack = BuiltInCleanupRules.FindById("app-slack-cache")!;
            CleanupRuleDefinition discord = BuiltInCleanupRules.FindById("app-discord-cache")!;
            Require(slack.TargetSpecs.Length == 8 && discord.TargetSpecs.Length == 7,
                $"Electron template includeLogs mismatch: slack={slack.TargetSpecs.Length}, discord={discord.TargetSpecs.Length}");

            CleanupRuleDefinition[] cautionRules = allCleanup.Where(r => r.Tier == CleanupRecommendationTier.Caution).ToArray();
            Require(cautionRules.Length == 5 && cautionRules.All(r => r.CautionReason is not null),
                "All 5 Caution rules must declare CautionReason");

            // 2. 内嵌 JSON 应用迁移规则完整性断言
            Require(BuiltInApplicationRules.All.Count == 7
                && BuiltInApplicationRules.All.Any(r => r.ApplicationId == "novastudio" && r.Level == MigrationAdmissionLevel.VerifiedEligible)
                && BuiltInApplicationRules.All.Any(r => r.ApplicationId == "docker-desktop-wsl" && r.Level == MigrationAdmissionLevel.VendorNativeRecommended),
                "BuiltInApplicationRules JSON catalog integrity mismatch");

            // 3. 表驱动 KnownDirectoryExplainer 覆盖与过期文案清理断言
            DirectoryExplanation? winOld = KnownDirectoryExplainer.Explain(@"C:\Windows.old");
            Require(winOld is not null && winOld.RoleTitle.Contains("Windows.old") && !winOld.MigrationAdvice.Contains("后续三期"),
                $"Windows.old explanation mismatch or stale Phase-3 copy: {winOld?.MigrationAdvice}");
            DirectoryExplanation? found = KnownDirectoryExplainer.Explain(@"C:\FOUND.001");
            Require(found is not null && found.RoleTitle.Contains("chkdsk"), "FOUND.001 explanation missing");
            DirectoryExplanation? jbLocal = KnownDirectoryExplainer.Explain(@"C:\Users\Alice\AppData\Local\JetBrains");
            DirectoryExplanation? jbRoaming = KnownDirectoryExplainer.Explain(@"C:\Users\Alice\AppData\Roaming\JetBrains");
            Require(jbLocal is not null && jbLocal.RoleTitle.Contains("缓存")
                && jbRoaming is not null && jbRoaming.RoleTitle.Contains("配置"),
                "JetBrains Local vs Roaming explanation dispatch mismatch");
            DirectoryExplanation? oneDriveBiz = KnownDirectoryExplainer.Explain(@"C:\Users\Alice\OneDrive - Contoso");
            Require(oneDriveBiz is not null && oneDriveBiz.RoleTitle.Contains("云盘"), "OneDrive prefix explanation mismatch");

            // 4. 共享词表 ProtectedPathVocabulary 一致性 + 零分配 FileSystemName 通配符匹配断言
            Require(ProtectedPathVocabulary.CloudSyncSegmentNames.Contains("OneDrive")
                && ProtectedPathVocabulary.SharedKeyAndCertificateSegments.Contains(".ssh")
                && ProtectedPathVocabulary.HiddenSystemVolumeRootNames.Contains(".MoveFolderQuarantine"),
                "ProtectedPathVocabulary core entries missing");
            Require(CleanupRuleScanner.MatchesAnyPattern("thumbcache_256.db", ["thumbcache_*.db", "iconcache_*.db"])
                && CleanupRuleScanner.MatchesAnyPattern("CbsPersist_20261126.cab", ["CbsPersist_*.cab", "CbsPersist_*.log"])
                && !CleanupRuleScanner.MatchesAnyPattern("readme.txt", ["thumbcache_*.db", "*.dmp"])
                && CleanupRuleScanner.MatchesAnyPattern("any.bin", ["*"]),
                "FileSystemName wildcard matcher behavior mismatch");

            // 5. 共享 ScanHotspotAnalyzer 统一热点口径验证（L0 容器下钻 + L3 85% 套壳下钻 + 跳过 Windows 黑盒）
            var pool = new Utf8NamePool();
            (byte p0, int o0, ushort l0) = pool.AddName("C:");
            (byte p1, int o1, ushort l1) = pool.AddName("Windows");
            (byte p2, int o2, ushort l2) = pool.AddName("Users");
            (byte p3, int o3, ushort l3) = pool.AddName("Alice");
            (byte p4, int o4, ushort l4) = pool.AddName("Workspace");
            (byte p5, int o5, ushort l5) = pool.AddName("HugeProject");

            long gb = 1024L * 1024 * 1024;
            ScanNodeRecord[] nodes =
            [
                new() { NodeId = 0, ParentNodeId = 0, NamePageIndex = p0, NameOffset = o0, NameLength = l0, Flags = ScanNodeFlags.IsDirectory, UniqueAllocatedBytes = 50 * gb, LogicalBytes = 50 * gb },
                new() { NodeId = 1, ParentNodeId = 0, NamePageIndex = p1, NameOffset = o1, NameLength = l1, Flags = ScanNodeFlags.IsDirectory, UniqueAllocatedBytes = 25 * gb, LogicalBytes = 25 * gb },
                new() { NodeId = 2, ParentNodeId = 0, NamePageIndex = p2, NameOffset = o2, NameLength = l2, Flags = ScanNodeFlags.IsDirectory, UniqueAllocatedBytes = 20 * gb, LogicalBytes = 20 * gb },
                new() { NodeId = 3, ParentNodeId = 2, NamePageIndex = p3, NameOffset = o3, NameLength = l3, Flags = ScanNodeFlags.IsDirectory, UniqueAllocatedBytes = 20 * gb, LogicalBytes = 20 * gb },
                new() { NodeId = 4, ParentNodeId = 3, NamePageIndex = p4, NameOffset = o4, NameLength = l4, Flags = ScanNodeFlags.IsDirectory, UniqueAllocatedBytes = 19 * gb, LogicalBytes = 19 * gb },
                new() { NodeId = 5, ParentNodeId = 4, NamePageIndex = p5, NameOffset = o5, NameLength = l5, Flags = ScanNodeFlags.IsDirectory, UniqueAllocatedBytes = 18 * gb, LogicalBytes = 18 * gb }
            ];
            var snap = new ScanSnapshot
            {
                Volume = new ScanVolumeInfo(@"C:\", "OS", "", 1, "NTFS", 200 * gb, 100 * gb, true, "OK"),
                Backend = ScanBackendKind.RawNtfsMft,
                Coverage = ScanCoverageStatus.Complete,
                StartedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1),
                CompletedAtUtc = DateTimeOffset.UtcNow,
                PolicyVersion = MigrationSafetyPolicy.CurrentPolicyVersion,
                ScanRootPath = @"C:\",
                Nodes = nodes,
                CsrOffsets = [0, 2, 2, 3, 4, 5, 5],
                CsrChildren = [1, 2, 3, 4, 5],
                NamePool = pool
            };

            var fakeEnv = new SafetyPolicyEnvironment(
                [@"C:\Windows"], [@"C:\Program Files"], [@"C:\ProgramData"], [@"C:\Users"], [@"C:\Users\Alice"],
                [@"C:\Users\Alice\AppData"], [@"D:\Tool"]);
            int[] uiHotspots = ScanHotspotAnalyzer.CollectDisjointHotspots(
                snap, null, gb, 10, HotspotTraversalOptions.DiskScanUi with { PolicyEnvironment = fakeEnv });
            int[] aiHotspots = ScanHotspotAnalyzer.CollectDisjointHotspots(
                snap, null, gb, 10, HotspotTraversalOptions.ForAiToolset(policyEnvironment: fakeEnv));

            Require(uiHotspots.Length == 1 && uiHotspots[0] == 5,
                $"Expected UI hotspot to drill through Users/Alice/Workspace to HugeProject (#5), got [{string.Join(",", uiHotspots)}]");
            Require(aiHotspots.Length == 1 && aiHotspots[0] == 5,
                $"Expected AI hotspot to drill through Users/Alice/Workspace to HugeProject (#5), got [{string.Join(",", aiHotspots)}]");

            return Task.CompletedTask;
        });

        await test("Cleanup-async-tab-quarantine-layered-inspection-and-prefix-pruning", async () =>
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "MoveFolder_P3Perf_" + Guid.NewGuid().ToString("N"));
            string scanDir = Path.Combine(tempRoot, "CacheDir");
            string quarantineRoot = Path.Combine(tempRoot, ".MoveFolderQuarantine");
            Directory.CreateDirectory(scanDir);

            try
            {
                string f1 = Path.Combine(scanDir, "a.tmp");
                File.WriteAllBytes(f1, new byte[4096]);
                var qStore = new QuarantineStore(customQuarantineRootOverride: quarantineRoot);
                var (batch, moved, _, _, _) = await qStore.QuarantineItemsSameVolumeAsync(
                    "性能分层测试", scanDir, "sys-temp-24h", false, [(f1, 4096, false, 0)]);
                Require(batch is not null && moved == 1, "Failed to seed quarantine batch");

                // 制造同名冲突文件
                File.WriteAllText(f1, "conflict-live-file");

                // 1. 轻量模式（主清理页切页）跳过逐条冲突核验，但准确返回批次数与体积
                var lite = await qStore.ListAllInspectionsAsync(checkRestoreConflicts: false);
                Require(lite.Count == 1 && lite[0].Batch.TotalBytes == 4096 && !lite[0].HasRestoreConflict,
                    $"Expected lite inspection to skip conflict check while returning metadata, got count={lite.Count}, conflict={lite.FirstOrDefault()?.HasRestoreConflict}");

                // 2. 完整模式（进入隔离区子视图）实时检出原路径恢复冲突
                var full = await qStore.ListAllInspectionsAsync(checkRestoreConflicts: true);
                Require(full.Count == 1 && full[0].HasRestoreConflict && full[0].ConflictCount == 1,
                    $"Expected full inspection to detect restore conflict, got conflict={full.FirstOrDefault()?.HasRestoreConflict}/{full.FirstOrDefault()?.ConflictCount}");

                // 3. 验证 CleanupViewModel 异步切页刷新（摘要）与异步隔离区刷新（完整冲突核验）分层生效
                var svc = new CleanupService(qStore);
                var vm = new CleanupViewModel(svc, qStore);
                Require(vm.QuarantineBatchCount == 0 && vm.VisibleIdleScopePreviews.Count >= 6,
                    "Constructor must remain synchronous-I/O-free while populating in-memory idle previews");

                await vm.OnEnteredCleanupPageAsync(force: true);
                Require(vm.QuarantineBatchCount == 1 && vm.QuarantineTotalBytes == 4096 && !vm.HasAnyQuarantineConflict,
                    $"Expected overview async refresh to load quarantine summary without conflict scan, got count={vm.QuarantineBatchCount}, conflict={vm.HasAnyQuarantineConflict}");

                await vm.RefreshQuarantineListAsync(checkRestoreConflicts: true, force: true);
                Require(vm.QuarantineBatchCount == 1 && vm.HasAnyQuarantineConflict && vm.QuarantineBatches[0].HasRestoreConflict,
                    "Expected full quarantine async refresh to surface restore conflict");
            }
            finally
            {
                try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
            }
        });

        await test("Quarantine-batch-select-confirm-and-permanent-delete", async () =>
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "MoveFolder_P3QBatch_" + Guid.NewGuid().ToString("N"));
            string scanDir = Path.Combine(tempRoot, "CacheDir");
            string quarantineRoot = Path.Combine(tempRoot, ".MoveFolderQuarantine");
            Directory.CreateDirectory(scanDir);

            try
            {
                string f1 = Path.Combine(scanDir, "b1.tmp");
                string f2 = Path.Combine(scanDir, "b2.tmp");
                string f3 = Path.Combine(scanDir, "b3.tmp");
                File.WriteAllBytes(f1, new byte[2048]);
                File.WriteAllBytes(f2, new byte[4096]);
                File.WriteAllBytes(f3, new byte[8192]);

                var qStore = new QuarantineStore(customQuarantineRootOverride: quarantineRoot);
                var (b1, _, _, _, _) = await qStore.QuarantineItemsSameVolumeAsync("批次1-Edge缓存", scanDir, "browser-edge-cache", false, [(f1, 2048, false, 0)]);
                var (b2, _, _, _, _) = await qStore.QuarantineItemsSameVolumeAsync("批次2-AI残留", scanDir, null, true, [(f2, 4096, false, 0)]);
                var (b3, _, _, _, _) = await qStore.QuarantineItemsSameVolumeAsync("批次3-保留项", scanDir, "dev-npm-cache", false, [(f3, 8192, false, 0)]);
                Require(b1 is not null && b2 is not null && b3 is not null, "Failed to seed 3 quarantine batches");

                var vm = new CleanupViewModel(new CleanupService(qStore), qStore);
                await vm.RefreshQuarantineListAsync(checkRestoreConflicts: true, force: true);
                Require(vm.QuarantineBatches.Count == 3 && vm.CheckedQuarantineBatchCount == 0 && !vm.CanBatchDeleteQuarantine && vm.AllQuarantineSelectedState == false,
                    "All quarantine batches must default to unchecked");

                // 全选 / 取消全选 / 局部勾选三态验证
                vm.ToggleSelectAllQuarantineBatchesCommand.Execute(null);
                Require(vm.CheckedQuarantineBatchCount == 3 && vm.CheckedQuarantineTotalBytes == 2048 + 4096 + 8192 && vm.AllQuarantineSelectedState == true && vm.CanBatchDeleteQuarantine,
                    "ToggleSelectAll should select all 3 quarantine batches");
                vm.ToggleSelectAllQuarantineBatchesCommand.Execute(null);
                Require(vm.CheckedQuarantineBatchCount == 0 && vm.AllQuarantineSelectedState == false && !vm.CanBatchDeleteQuarantine,
                    "Second ToggleSelectAll should uncheck all batches");

                var row1 = vm.QuarantineBatches.Single(r => r.BatchId == b1!.BatchId);
                var row2 = vm.QuarantineBatches.Single(r => r.BatchId == b2!.BatchId);
                row1.IsChecked = true;
                row2.IsChecked = true;
                Require(vm.CheckedQuarantineBatchCount == 2 && vm.CheckedQuarantineTotalBytes == 6144 && vm.AllQuarantineSelectedState is null,
                    "Partial selection must set tri-state header checkbox to indeterminate (null)");

                // 错误确认短语必须拒绝
                bool rejected = false;
                try { await vm.ExecuteBatchPermanentDeleteCoreAsync([b1!, b2!], "错误短语"); }
                catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "Batch permanent delete must reject mismatched confirmation phrase");

                // 批量确认弹窗 STA 渲染验证（多批 + AI 残留警告）
                Exception? staError = null;
                var sta = new Thread(() =>
                {
                    try
                    {
                        var win = new QuarantineDeleteConfirmWindow([b1!, b2!]);
                        win.Measure(new Size(560, 520));
                    }
                    catch (Exception ex)
                    {
                        staError = ex;
                    }
                });
                sta.SetApartmentState(ApartmentState.STA);
                sta.Start();
                sta.Join();
                if (staError is not null) throw new InvalidOperationException($"Multi-batch confirm window failed: {staError.Message}", staError);

                // 正确确认短语批量删除 b1 + b2，保留未勾选的 b3
                var res = await vm.ExecuteBatchPermanentDeleteCoreAsync([b1!, b2!], QuarantineStore.DeleteConfirmationPhrase);
                Require(res.AllSucceeded && !res.WasCancelled && res.FullyDeletedBatches == 2 && res.PartialOrFailedBatches == 0 && res.FreedBytes == 6144,
                    $"Expected 2 batches permanently deleted (6144 B), got {res.SummaryMessage}");
                Require(vm.QuarantineBatches.Count == 1 && vm.QuarantineBatches[0].BatchId == b3!.BatchId && vm.CheckedQuarantineBatchCount == 0,
                    $"Expected only unchecked batch 3 to remain, got count={vm.QuarantineBatches.Count}");
            }
            finally
            {
                try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
            }
        });
    }

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
