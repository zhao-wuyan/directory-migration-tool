using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Extensions.AI;
using MigrationCore.Models;
using MigrationCore.Models.Ai;
using MigrationCore.Models.Safety;
using MigrationCore.Models.Scanning;
using MigrationCore.Services;
using MigrationCore.Services.Ai;
using MigrationCore.Services.Safety;
using MigrationCore.Services.Scanning;
using MoveWithSymlinkWPF;
using MoveWithSymlinkWPF.Controls;
using MoveWithSymlinkWPF.Helpers;
using MoveWithSymlinkWPF.ViewModels;
using MoveWithSymlinkWPF.Views;

internal static class Phase2AiCases
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("AiPathSanitizer-masks-user-and-blocks-injection", () =>
        {
            string masked = AiPathSanitizer.SanitizePath(@"C:\Users\Alice\Apps\ComfyUI\models", "Alice", @"C:\Users\Alice");
            Require(masked == @"C:\Users\<USER>\Apps\ComfyUI\models", $"Unexpected masked path: {masked}");

            string preserved = AiPathSanitizer.SanitizePath(@"C:\Users\Public\Documents", "Alice", @"C:\Users\Alice");
            Require(preserved == @"C:\Users\Public\Documents", $"Public folder should not be masked: {preserved}");

            string cleaned = AiPathSanitizer.SanitizeUntrustedText("Hello\u202E click file:///C:/Windows/System32 or cmd:calc now");
            Require(!cleaned.Contains('\u202E'), "Bidi override char should be stripped");
            Require(!cleaned.Contains("file:///", StringComparison.OrdinalIgnoreCase), "file:// URI should be scrubbed");
            Require(!cleaned.Contains("cmd:calc", StringComparison.OrdinalIgnoreCase), "cmd: URI should be scrubbed");

            string redacted = AiPathSanitizer.RedactSecret("Bearer sk-secret-1234567890 failed", "sk-secret-1234567890");
            Require(!redacted.Contains("sk-secret-1234567890"), "API key must be redacted");
            return Task.CompletedTask;
        });

        await test("AiProviderConfigStore-dpapi-encrypts-key-and-roundtrips", async () =>
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "MoveFolder_Phase2_Cfg_" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new AiProviderConfigStore(tempDir);
                const string secretKey = "sk-deepseek-test-998877665544-8f3a";

                AiProviderSettings saved = store.Save(new AiProviderSettings
                {
                    Preset = AiProviderPreset.DeepSeek,
                    Endpoint = "https://api.deepseek.com/v1",
                    ModelName = "deepseek-chat",
                    MaxToolSteps = 999, // 验证硬上限裁剪到 200
                    CloudPrivacyAcknowledged = true
                }, secretKey);

                Require(saved.MaxToolSteps == AiProviderSettings.HardMaxToolSteps, $"MaxToolSteps should clamp to {AiProviderSettings.HardMaxToolSteps}, got {saved.MaxToolSteps}");
                Require(saved.HasSavedApiKey, "EncryptedApiKeyBase64 should be populated");
                Require(!saved.IsLocalEndpoint, "DeepSeek should be classified as cloud endpoint");
                Require(AiProviderSettings.IsLoopbackEndpoint("http://localhost:11434/v1"), "Ollama localhost must be local");
                Require(AiProviderSettings.IsLoopbackEndpoint("http://127.0.0.1:1234/v1"), "LM Studio 127.0.0.1 must be local");

                string rawFile = File.ReadAllText(store.ConfigFilePath, Encoding.UTF8);
                Require(!rawFile.Contains(secretKey, StringComparison.Ordinal), "Plaintext API key MUST NEVER appear in config file!");

                AiProviderSettings reloaded = store.Load();
                string? decrypted = store.TryUnprotectApiKey(reloaded);
                Require(decrypted == secretKey, "DPAPI unprotect should return exact saved API key");
                Require(reloaded.MaskedApiKeyPreview is not null && reloaded.MaskedApiKeyPreview.EndsWith("8f3a", StringComparison.Ordinal),
                    $"Unexpected masked preview: {reloaded.MaskedApiKeyPreview}");

                AiProviderSettings cleared = store.Save(reloaded, string.Empty);
                Require(!cleared.HasSavedApiKey && store.TryUnprotectApiKey(cleared) is null, "Clearing key should remove ciphertext");

                // 验证旧版 v1/v2 配置（默认 8 步）加载时平滑升级为不限步数（0）并补全 20 分钟整体兜底超时
                File.WriteAllText(store.ConfigFilePath, "{\"schemaVersion\":1,\"timeoutSeconds\":90,\"maxToolSteps\":8}", Encoding.UTF8);
                AiProviderSettings migrated = store.Load();
                Require(migrated.MaxToolSteps == 0 && migrated.IsToolStepUnlimited,
                    $"Legacy default maxToolSteps=8 should migrate to unlimited (0), got {migrated.MaxToolSteps}");
                Require(migrated.TimeoutSeconds == AiProviderSettings.DefaultTimeoutSeconds,
                    $"Idle timeout should stay {AiProviderSettings.DefaultTimeoutSeconds}s, got {migrated.TimeoutSeconds}");
                Require(migrated.OverallTimeoutSeconds == AiProviderSettings.DefaultOverallTimeoutSeconds,
                    $"Legacy v1 config should populate default overall timeout {AiProviderSettings.DefaultOverallTimeoutSeconds}s, got {migrated.OverallTimeoutSeconds}");

                AiProviderSettings customSaved = store.Save(migrated with { TimeoutSeconds = 180, MaxToolSteps = 30, ResponseLanguage = AiResponseLanguage.English });
                AiProviderSettings customReloaded = store.Load();
                Require(customSaved.TimeoutSeconds == 180 && customReloaded.TimeoutSeconds == 180,
                    $"Custom idle timeout should persist as 180s, got {customReloaded.TimeoutSeconds}");
                Require(customSaved.MaxToolSteps == 30 && customReloaded.MaxToolSteps == 30 && !customReloaded.IsToolStepUnlimited,
                    $"Custom MaxToolSteps should persist as 30, got {customReloaded.MaxToolSteps}");
                Require(migrated.ResponseLanguage == AiResponseLanguage.Chinese && customReloaded.ResponseLanguage == AiResponseLanguage.English,
                    $"Default response language should be Chinese and English selection should persist, got {customReloaded.ResponseLanguage}");

                // 验证回复语言切换会真实写入模型 System Prompt（中文/英文）
                var promptCapture = new PromptCapturingChatClient();
                var langToolset = new ReadOnlyAiAnalysisToolset();
                var langAgent = new BuiltInAiAgentService(langToolset, (_, _) => promptCapture);
                await langAgent.RunAnalysisAsync(migrated with { Endpoint = "http://localhost:11434/v1", ResponseLanguage = AiResponseLanguage.Chinese }, null, new AiAgentAnalysisRequest(null, "hi"));
                Require(promptCapture.LastSystemPrompt.Contains("简体中文"), "Chinese language setting must guide AI to respond in Simplified Chinese");
                await langAgent.RunAnalysisAsync(migrated with { Endpoint = "http://localhost:11434/v1", ResponseLanguage = AiResponseLanguage.English }, null, new AiAgentAnalysisRequest(null, "hi"));
                Require(promptCapture.LastSystemPrompt.Contains("Language Preference (English)"), "English language setting must guide AI to respond in English");
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
            }
        });

        await test("ReadOnlyAiAnalysisToolset-enforces-policy-idempotency-and-invalidation", () =>
        {
            var (snapshot, env, ids) = BuildAiTestSnapshot();
            var catalog = new ApplicationCatalog(BuiltInApplicationRules.All, []);
            var toolset = new ReadOnlyAiAnalysisToolset(
                catalog: catalog,
                policyEnvironment: env,
                targetCandidatesProvider: () =>
                [
                    new AiTargetCandidateDto("target-vol-d", "D:", @"D:\MigratedData", 500L * 1024 * 1024 * 1024, true)
                ]);

            toolset.AuthorizeSnapshot(snapshot);

            AiCapabilitiesResponse caps = toolset.GetCapabilities();
            Require(caps.ReadOnlyAnalysisOnly && !caps.CanExecuteOperations, "Toolset must declare strict readonly zero-execution capabilities");

            AiScanSummaryResponse summary = toolset.GetScanSummary(snapshot.ScanId);
            Require(summary.VerifiedL1Count >= 1, "Should identify L1 ComfyUI models");
            Require(summary.TargetCandidates.Length == 1 && summary.TargetCandidates[0].TargetCandidateId == "target-vol-d", "Should expose target-vol-d");
            Require(summary.RootNodeId == snapshot.RootNodeId, "Summary should expose RootNodeId for top-down drill-down");
            Require(summary.TopLevelDirectories is { Length: >= 2 }, "Summary should include top-level directories for immediate drill-down");
            Require(summary.TopLevelDirectories!.Any(d => d.Name == "Users" && d.CanDrillDown), "Users container root should allow drill-down");
            Require(summary.TopLevelDirectories!.Any(d => d.Name == "Windows" && !d.CanDrillDown), "L0 Windows core directory should not allow drill-down");
            Require(!summary.TopLevelDirectories!.Any(d => d.Name.Equals("$WinREAgent", StringComparison.OrdinalIgnoreCase)),
                "AI TopLevelDirectories must hide volume-root $WinREAgent");
            Require(!summary.TopLevelDirectories!.Any(d => d.Name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)),
                "AI TopLevelDirectories must hide volume-root $Recycle.Bin");

            // 验证层级钻入：Users -> <USER> 允许，minSizeMb 过滤生效，非容器 L0（Windows\System32）禁止钻入
            int usersNodeId = summary.TopLevelDirectories!.Single(d => d.Name == "Users").NodeId;
            AiDirectoryChildrenResponse usersChildren = toolset.ListDirectoryChildren(snapshot.ScanId, usersNodeId, pageSize: 10, minSizeMb: 100);
            Require(usersChildren.Children.Length == 1 && usersChildren.Children[0].CanDrillDown, "Should drill into user container directory");

            AiDirectoryChildrenResponse filteredOut = toolset.ListDirectoryChildren(snapshot.ScanId, usersNodeId, pageSize: 10, minSizeMb: 500_000);
            Require(filteredOut.Children.Length == 0 && filteredOut.FilteredOutSmallCount == 1, "minSizeMb should filter out smaller directories and report count");

            bool blockedL0Drill = false;
            try { toolset.ListDirectoryChildren(snapshot.ScanId, ids.WindowsSystem32); }
            catch (UnauthorizedAccessException) { blockedL0Drill = true; }
            Require(blockedL0Drill, "ListDirectoryChildren must reject drilling into non-container L0 directory");

            AiMigrationCandidatesResponse candidates = toolset.ListMigrationCandidates(snapshot.ScanId);
            Require(candidates.Candidates.All(c => c.AdmissionLevel != MigrationAdmissionLevel.HardDenied), "L0 hard-denied nodes must never appear in candidates");
            Require(candidates.Candidates.Any(c => c.NodeId == ids.ComfyModels && c.SanitizedPath.Contains("<USER>")), "ComfyUI models should be present with sanitized path");

            // 验证按父节点分支聚焦候选
            AiMigrationCandidatesResponse scopedCandidates = toolset.ListMigrationCandidates(snapshot.ScanId, parentNodeId: ids.ComfyRoot);
            Require(scopedCandidates.Candidates.Length == 1 && scopedCandidates.Candidates[0].NodeId == ids.ComfyModels,
                "parentNodeId should scope migration candidates to that subtree");

            // L0 禁止探测未知目录元数据
            bool blockedL0Inspect = false;
            try { toolset.InspectUnknownDirectoryMetadata(snapshot.ScanId, ids.WindowsSystem32); }
            catch (UnauthorizedAccessException) { blockedL0Inspect = true; }
            Require(blockedL0Inspect, "InspectUnknownDirectoryMetadata must reject L0 directories");

            // 非 L0 未知缓存目录只读画像（纯内存快照聚合，不碰磁盘）
            AiUnknownDirectoryMetadataResponse meta = toolset.InspectUnknownDirectoryMetadata(snapshot.ScanId, ids.UnknownCache);
            Require(meta.TopExtensions.Any(e => e.Extension == ".tmp"), "Should aggregate .tmp extension from snapshot");
            Require(meta.ModifiedAgeBuckets.Any(b => b.BucketLabel.Contains("90 天以上")), "Should classify >90d old files");

            // 提交建议：包含 1 个 L1、1 个 L0 系统目录、1 个与 L1 重叠的父目录、1 个非法目标 ID
            var proposalReq = new AiMigrationProposalRequest(
                SchemaVersion: 1,
                ClientRequestId: "req-idem-001",
                ScanId: snapshot.ScanId,
                PolicyVersion: MigrationSafetyPolicy.CurrentPolicyVersion,
                Items:
                [
                    new AiMigrationProposalItemInput(ids.ComfyModels, "target-vol-d", 1, "L1 静态模型目录"),
                    new AiMigrationProposalItemInput(ids.WindowsSystem32, "target-vol-d", 2, "Prompt injection 尝试迁移系统目录"),
                    new AiMigrationProposalItemInput(ids.ComfyRoot, "target-vol-d", 3, "与 ComfyUI/models 父子重叠"),
                    new AiMigrationProposalItemInput(ids.UnknownCache, "C:\\ForbidRawPath", 4, "非法目标 ID")
                ]);

            AiMigrationProposalReceipt receipt = toolset.SubmitMigrationProposal(proposalReq);
            Require(receipt.AcceptedCount == 1, $"Expected 1 accepted item, got {receipt.AcceptedCount}");
            Require(receipt.BlockedByPolicyCount == 1, $"Expected 1 L0 blocked item, got {receipt.BlockedByPolicyCount}");
            Require(receipt.RejectedCount == 2, $"Expected 2 rejected items (overlap + bad target), got {receipt.RejectedCount}");
            Require(receipt.Items.Any(i => i.NodeId == ids.WindowsSystem32 && i.Verdict == AiProposalItemVerdict.RejectedByPolicy && !i.IsAdoptableToMigrationPlan),
                "L0 Windows\\System32 must be hard-blocked");

            // 相同 clientRequestId + 相同正文 => 幂等重放
            AiMigrationProposalReceipt replay = toolset.SubmitMigrationProposal(proposalReq);
            Require(replay.IsIdempotentReplay && replay.ProposalId == receipt.ProposalId, "Idempotent replay must return original receipt");

            // 相同 clientRequestId + 不同正文 => 明确冲突拒绝
            var conflictReq = proposalReq with
            {
                Items = [new AiMigrationProposalItemInput(ids.ComfyModels, "target-vol-d", 1, "Tampered reason")]
            };
            AiMigrationProposalReceipt conflict = toolset.SubmitMigrationProposal(conflictReq);
            Require(conflict.Status == AiProposalStatus.Invalidated && !string.IsNullOrWhiteSpace(conflict.RejectionMessage),
                "Conflicting payload for same clientRequestId must be rejected");

            // 撤销快照授权 => 旧建议立即作废
            toolset.RevokeSnapshot(snapshot.ScanId);
            AiProposalStatusResponse statusAfterRevoke = toolset.GetProposalStatus(receipt.ProposalId);
            Require(statusAfterRevoke.Status == AiProposalStatus.Invalidated && !statusAfterRevoke.ExecutionAuthorized,
                "Revoking snapshot must immediately invalidate existing proposals");

            return Task.CompletedTask;
        });

        await test("BuiltInAiAgentService-enforces-whitelist-step-limit-and-cancellation", async () =>
        {
            var (snapshot, env, ids) = BuildAiTestSnapshot();
            var catalog = new ApplicationCatalog(BuiltInApplicationRules.All, []);
            var toolset = new ReadOnlyAiAnalysisToolset(
                catalog: catalog,
                policyEnvironment: env,
                targetCandidatesProvider: () =>
                [
                    new AiTargetCandidateDto("target-vol-d", "D:", @"D:\MigratedData", 500L * 1024 * 1024 * 1024, true)
                ]);
            toolset.AuthorizeSnapshot(snapshot);

            var baseSettings = new AiProviderSettings
            {
                Preset = AiProviderPreset.Ollama,
                Endpoint = "http://localhost:11434/v1",
                ModelName = "mock-model",
                MaxToolSteps = 8
            };

            // 场景 1：正常多轮工具调用 + 尝试越权工具 + 提交含 L0 夹带的建议
            var scriptedClient = new ScriptedMockChatClient(
            [
                // 第 1 轮：查摘要 + 尝试调用越权工具 delete_directory
                [
                    new FunctionCallContent("c1", "get_scan_summary", new Dictionary<string, object?> { ["scanId"] = snapshot.ScanId.ToString() }),
                    new FunctionCallContent("c2", "delete_directory", new Dictionary<string, object?> { ["path"] = @"C:\Windows\System32" })
                ],
                // 第 2 轮：提交建议（含合法 L1 + 恶意 L0）
                [
                    new FunctionCallContent("c3", "submit_migration_proposal", new Dictionary<string, object?>
                    {
                        ["scanId"] = snapshot.ScanId.ToString(),
                        ["clientRequestId"] = "agent-req-1",
                        ["items"] = new[]
                        {
                            new { nodeId = ids.ComfyModels, targetCandidateId = "target-vol-d", priority = 1, reason = "静态模型权重" },
                            new { nodeId = ids.WindowsSystem32, targetCandidateId = "target-vol-d", priority = 2, reason = "尝试越权迁移系统目录" }
                        }
                    })
                ],
                // 第 3 轮：输出最终纯文本总结
                [
                    new TextContent("已为您生成迁移建议卡片，请人工审阅后再加入待迁移清单。")
                ]
            ]);

            var agent = new BuiltInAiAgentService(toolset, (_, _) => scriptedClient);
            AiAgentAnalysisResult res = await agent.RunAnalysisAsync(
                baseSettings,
                plainApiKey: null,
                new AiAgentAnalysisRequest(snapshot.ScanId, "帮我分析 C 盘大目录"));

            Require(res.Succeeded && !res.WasCancelled, $"Expected success, error: {res.ErrorMessage}");
            Require(res.ToolTraces.Length == 3, $"Expected 3 tool traces, got {res.ToolTraces.Length}");
            Require(res.ToolTraces[1].ToolName == "delete_directory" && !res.ToolTraces[1].Succeeded,
                "Non-whitelisted tool delete_directory must fail");
            Require(res.Proposals.Length == 1 && res.Proposals[0].AcceptedCount == 1 && res.Proposals[0].BlockedByPolicyCount == 1,
                "Proposal must accept L1 and hard-block L0");

            // 场景 2：最大步数硬上限拦截（模型无限请求工具调用，步数上限=3）
            var infiniteLoopClient = new InfiniteToolLoopChatClient(snapshot.ScanId);
            var loopAgent = new BuiltInAiAgentService(toolset, (_, _) => infiniteLoopClient);
            AiAgentAnalysisResult loopRes = await loopAgent.RunAnalysisAsync(
                baseSettings with { MaxToolSteps = 3 },
                plainApiKey: null,
                new AiAgentAnalysisRequest(snapshot.ScanId, "无限循环测试"));

            Require(loopRes.Succeeded && loopRes.HitStepLimit, "Agent must stop cleanly on step limit");
            Require(loopRes.StepsUsed == 3, $"Expected exactly 3 steps used, got {loopRes.StepsUsed}");

            // 场景 3：即时取消中断
            using var cts = new CancellationTokenSource();
            var slowClient = new SlowCancellableChatClient(cts);
            var cancelAgent = new BuiltInAiAgentService(toolset, (_, _) => slowClient);
            AiAgentAnalysisResult cancelRes = await cancelAgent.RunAnalysisAsync(
                baseSettings,
                plainApiKey: null,
                new AiAgentAnalysisRequest(snapshot.ScanId, "测试立即停止"),
                cancellationToken: cts.Token);

            Require(!cancelRes.Succeeded && cancelRes.WasCancelled, "Agent must report WasCancelled when token cancelled");

            // 场景 4：工具结果中文不转义 \uXXXX + 超预算结构化裁剪保持 100% 合法 JSON 与有效 nextCursor
            var manyCandidates = Enumerable.Range(1, 40).Select(idx => new AiMigrationCandidateDto(
                NodeId: 100 + idx,
                DisplayName: $"应用模型数据目录_{idx}",
                SanitizedPath: $@"C:\Users\<USER>\Apps\BigDataApp_{idx}\ModelsAndCache",
                LogicalBytes: (50L - idx) * 1024 * 1024 * 1024,
                AllocatedBytes: (50L - idx) * 1024 * 1024 * 1024,
                UniqueAllocatedBytes: (50L - idx) * 1024 * 1024 * 1024,
                ConservativeTargetBytes: (50L - idx) * 1024 * 1024 * 1024,
                AdmissionLevel: MigrationAdmissionLevel.ManualRiskConfirm,
                ReasonCodes: [SafetyReasonCodes.L3PersonalOrUnlistedConfirm],
                MatchedApplicationId: null,
                MatchedApplicationName: null,
                DirectoryRoleTitle: "大型业务数据与缓存目录",
                RecommendedTargetCandidateId: "target-vol-d",
                SuggestedTargetDisplay: $@"D:\MigratedData\BigDataApp_{idx}",
                RequiredStoppedProcesses: ["app.exe"],
                HasSubtreeHardLinks: false,
                HasIncompleteNodes: false,
                Summary: "通过 L3 非高危策略复核，加入迁移清单时需用户人工确认兼容性风险。")).ToArray();

            var largeResp = new AiMigrationCandidatesResponse(
                ScanId: snapshot.ScanId,
                PolicyVersion: MigrationSafetyPolicy.CurrentPolicyVersion,
                Candidates: manyCandidates,
                NextCursor: "40",
                TotalCandidateCount: 80,
                Disclaimer: ReadOnlyAiAnalysisToolset.MandatoryDisclaimer);

            string clampedJson = BuiltInAiAgentService.SerializeAndClampToolResult(largeResp, out string clampedSummary, maxChars: 4000);
            Require(clampedJson.Length <= 4000, $"Structured budget clamp must fit within 4000 chars, got {clampedJson.Length}");
            Require(clampedJson.Contains("应用模型数据目录_1", StringComparison.Ordinal) && !clampedJson.Contains("\\u5e94", StringComparison.OrdinalIgnoreCase),
                "Tool JSON should keep UTF-8 Chinese characters unescaped to save budget");
            using (JsonDocument parsed = JsonDocument.Parse(clampedJson))
            {
                JsonElement root = parsed.RootElement;
                Require(root.TryGetProperty("truncatedByBudget", out JsonElement tb) && tb.GetBoolean(),
                    "Clamped candidate response should mark truncatedByBudget=true");
                Require(root.TryGetProperty("candidates", out JsonElement arr) && arr.GetArrayLength() >= 1 && arr.GetArrayLength() < 40,
                    "Clamped candidate response should contain a smaller valid subset");
                Require(root.TryGetProperty("nextCursor", out JsonElement nc) && nc.GetString() == arr.GetArrayLength().ToString(),
                    $"nextCursor should point to exact trimmed count, got {nc}");
            }
            Require(clampedSummary.Contains("自动收窄"), $"Expected budget clamp summary, got '{clampedSummary}'");
        });

        await test("BuiltInAiAgentService-dynamic-idle-timeout-and-overall-cap", async () =>
        {
            var (snapshot, env, _) = BuildAiTestSnapshot();
            var toolset = new ReadOnlyAiAnalysisToolset(new ApplicationCatalog(BuiltInApplicationRules.All, []), env);
            toolset.AuthorizeSnapshot(snapshot);
            var settings = new AiProviderSettings
            {
                Preset = AiProviderPreset.Ollama,
                Endpoint = "http://localhost:11434/v1",
                ModelName = "mock-timeout-model",
                MaxToolSteps = 4
            };

            // A. 持续流式输出超过 idleTimeout（5 * 55ms = 275ms > 150ms），因每次输出自动续期，必须顺利完成
            var steadyClient = new TimedStreamChatClient(chunkCount: 5, delayBetweenChunksMs: 55, stallAfterChunk: -1);
            var steadyAgent = new BuiltInAiAgentService(
                toolset,
                (_, _) => steadyClient,
                idleTimeoutOverride: TimeSpan.FromMilliseconds(150),
                overallTimeoutOverride: TimeSpan.FromSeconds(5));
            AiAgentAnalysisResult steadyRes = await steadyAgent.RunAnalysisAsync(
                settings, null, new AiAgentAnalysisRequest(snapshot.ScanId, "长回复持续输出测试"));
            Require(steadyRes.Succeeded, $"Continuous streaming should reset idle watchdog and succeed, error: {steadyRes.ErrorMessage}");
            Require(steadyRes.AssistantMessage.Contains("片段5"), $"Expected full streamed content, got '{steadyRes.AssistantMessage}'");

            // B. 模型卡死长时间不吐字，触发动态无响应超时
            var stalledClient = new TimedStreamChatClient(chunkCount: 5, delayBetweenChunksMs: 20, stallAfterChunk: 1, stallMs: 3000);
            var idleAgent = new BuiltInAiAgentService(
                toolset,
                (_, _) => stalledClient,
                idleTimeoutOverride: TimeSpan.FromMilliseconds(140),
                overallTimeoutOverride: TimeSpan.FromSeconds(5));
            AiAgentAnalysisResult idleRes = await idleAgent.RunAnalysisAsync(
                settings, null, new AiAgentAnalysisRequest(snapshot.ScanId, "卡死无响应测试"));
            Require(!idleRes.Succeeded && !idleRes.WasCancelled, "Stalled stream should fail as timeout (not manual cancel)");
            Require(idleRes.ErrorMessage is not null && idleRes.ErrorMessage.Contains("未输出任何内容"),
                $"Expected idle timeout message, got '{idleRes.ErrorMessage}'");

            // C. 模型一直慢速吐字但超过整体安全兜底上限，必须强制安全中止
            var endlessClient = new TimedStreamChatClient(chunkCount: 100, delayBetweenChunksMs: 35, stallAfterChunk: -1);
            var overallAgent = new BuiltInAiAgentService(
                toolset,
                (_, _) => endlessClient,
                idleTimeoutOverride: TimeSpan.FromSeconds(2),
                overallTimeoutOverride: TimeSpan.FromMilliseconds(220));
            AiAgentAnalysisResult overallRes = await overallAgent.RunAnalysisAsync(
                settings, null, new AiAgentAnalysisRequest(snapshot.ScanId, "整体兜底超时测试"));
            Require(!overallRes.Succeeded && !overallRes.WasCancelled, "Endless stream must abort at overall safety cap");
            Require(overallRes.ErrorMessage is not null && overallRes.ErrorMessage.Contains("总耗时") && overallRes.ErrorMessage.Contains("安全上限"),
                $"Expected overall cap message, got '{overallRes.ErrorMessage}'");
        });

        await test("AiRepetitionLoopDetector-and-agent-loop-recovery-or-abort", async () =>
        {
            // 0. 验证 5 秒持续观察窗口：4.9 秒内仅观察不触发，满 5 秒持续重复才触发；中途恢复正常新内容则解除观察不误报
            DateTimeOffset fakeClock = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
            var timedDetector = new AiRepetitionLoopDetector(TimeSpan.FromSeconds(5), () => fakeClock);
            Require(!timedDetector.Feed("正常开头" + new string('的', 25), out _) && timedDetector.IsSuspectingLoop,
                "First hit should enter 5s sustain observation instead of aborting immediately");
            fakeClock = fakeClock.AddSeconds(4.9);
            Require(!timedDetector.Feed("的的", out _) && timedDetector.IsSuspectingLoop,
                "Should still observe before full 5 seconds have elapsed");
            fakeClock = fakeClock.AddSeconds(0.2);
            Require(timedDetector.Feed("的", out string? timedReason) && timedReason is not null && timedReason.Contains("持续超过 5 秒"),
                $"Expected trigger after sustaining >5s, got '{timedReason}'");

            // 验证中途自行跳出复读 -> 解除观察不误报
            DateTimeOffset recoveryClock = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
            var recoveryDetector = new AiRepetitionLoopDetector(TimeSpan.FromSeconds(5), () => recoveryClock);
            Require(!recoveryDetector.Feed(new string('好', 26), out _) && recoveryDetector.IsSuspectingLoop,
                "Should enter suspect state on initial repeat");
            recoveryClock = recoveryClock.AddSeconds(2);
            Require(!recoveryDetector.Feed("接下来我整理了磁盘中占用空间最大的五个第三方目录并按安全等级逐一说明迁移建议与注意事项。", out _),
                "Normal recovery text should not trigger loop");
            Require(!recoveryDetector.IsSuspectingLoop, "Suspect state should clear once model resumes normal non-repeating output");

            // 1. 单字符连续重复检测（0 秒即时窗口验证模式识别精度）
            var d1 = new AiRepetitionLoopDetector(TimeSpan.Zero);
            Require(d1.Feed("正常开头" + new string('的', 26), out string? r1) && r1 is not null && r1.Contains("单字符"),
                $"Expected single-char loop detection, got '{r1}'");

            // 2. 短词组连续周期复读检测
            var d2 = new AiRepetitionLoopDetector(TimeSpan.Zero);
            Require(d2.Feed(string.Concat(Enumerable.Repeat("继续核验，", 14)), out string? r2) && r2 is not null && r2.Contains("循环重复"),
                $"Expected short phrase loop detection, got '{r2}'");

            // 3. 短句连续复读检测
            var d3 = new AiRepetitionLoopDetector(TimeSpan.Zero);
            Require(d3.Feed(string.Concat(Enumerable.Repeat("我需要进一步确认这个目录的归属。", 9)), out string? r3) && r3 is not null,
                $"Expected sentence loop detection, got '{r3}'");

            // 4. 带递增序号的同句高频复读检测
            var d4 = new AiRepetitionLoopDetector(TimeSpan.Zero);
            string numbered = string.Join("\n", Enumerable.Range(1, 11).Select(i => $"{i}. 正在反复核查同一路径的安全策略。"));
            Require(d4.Feed(numbered, out string? r4) && r4 is not null && r4.Contains("高频重复"),
                $"Expected numbered line repetition detection, got '{r4}'");

            // 5. 正常 Markdown 表格与分隔线绝不误报
            var d5 = new AiRepetitionLoopDetector(TimeSpan.Zero);
            const string normalMd = """
                ## 磁盘分析结果
                | 目录 | 大小 | 级别 |
                | --- | --- | --- |
                | `ComfyUI/models` | 128 GB | L1 |
                | `SteamLibrary` | 96 GB | L1 |
                | `Blender/assets` | 42 GB | L1 |
                ---
                以上目录均可由用户核实后加入待迁移清单。
                """;
            Require(!d5.Feed(normalMd, out string? r5), $"Normal Markdown should never trigger loop detector, got '{r5}'");

            // 6. TrimTrailingRepetition 保留正常前缀并剔除尾部复读污染
            string trimmed = AiRepetitionLoopDetector.TrimTrailingRepetition("有效结论：" + string.Concat(Enumerable.Repeat("重复废话。", 12)));
            Require(trimmed.StartsWith("有效结论：") && trimmed.Length < 25, $"Expected trimmed prefix, got '{trimmed}'");

            // 7. Agent 端到端：持续复读满观察窗口后自动打断并引导恢复成功
            var (snapshot, env, _) = BuildAiTestSnapshot();
            var toolset = new ReadOnlyAiAnalysisToolset(new ApplicationCatalog(BuiltInApplicationRules.All, []), env);
            toolset.AuthorizeSnapshot(snapshot);
            var settings = new AiProviderSettings
            {
                Preset = AiProviderPreset.Ollama,
                Endpoint = "http://localhost:11434/v1",
                ModelName = "mock-loop-model",
                MaxToolSteps = 4
            };

            var recoverEvents = new List<AiAgentRunEvent>();
            var recoverProgress = new SyncProgress<AiAgentRunEvent>(e => recoverEvents.Add(e));
            var recoverClient = new ScriptedStreamingChatClient(
            [
                // 第 1 轮：持续输出复读句式超过 80ms 观察窗口
                Enumerable.Range(0, 18).Select(_ => (AIContent)new TextContent("请等一下我再检查一下这个目录。")).ToArray(),
                // 第 2 轮：收到系统纠偏指令后正常收敛输出
                [new TextContent("## 分析结论\n\n建议优先迁移 `ComfyUI/models`（128 GB，L1）。")]
            ], delayPerChunkMs: 12);

            var recoverAgent = new BuiltInAiAgentService(
                toolset,
                (_, _) => recoverClient,
                repetitionSustainOverride: TimeSpan.FromMilliseconds(80));
            AiAgentAnalysisResult recoverRes = await recoverAgent.RunAnalysisAsync(
                settings, null, new AiAgentAnalysisRequest(snapshot.ScanId, "复读自愈测试"), recoverProgress);
            Require(recoverRes.Succeeded, $"Agent should recover after first repetition loop, error: {recoverRes.ErrorMessage}");
            Require(recoverEvents.Any(e => e.Kind == AiAgentEventKind.Warning), "Expected Warning event during loop recovery");
            Require(recoverRes.AssistantMessage.Contains("ComfyUI/models") && !recoverRes.AssistantMessage.Contains("请等一下"),
                $"Recovered answer should be clean, got '{recoverRes.AssistantMessage}'");

            // 8. Agent 端到端：纠偏后再次持续复读满观察窗口则果断安全中止
            var persistentLoopClient = new ScriptedStreamingChatClient(
            [
                Enumerable.Range(0, 36).Select(_ => (AIContent)new TextContent("好")).ToArray(),
                Enumerable.Range(0, 36).Select(_ => (AIContent)new TextContent("嗯")).ToArray()
            ], delayPerChunkMs: 10);
            var abortAgent = new BuiltInAiAgentService(
                toolset,
                (_, _) => persistentLoopClient,
                repetitionSustainOverride: TimeSpan.FromMilliseconds(80));
            AiAgentAnalysisResult abortRes = await abortAgent.RunAnalysisAsync(
                settings, null, new AiAgentAnalysisRequest(snapshot.ScanId, "持续复读中止测试"));
            Require(!abortRes.Succeeded && abortRes.ErrorMessage is not null && abortRes.ErrorMessage.Contains("重复输出死循环"),
                $"Persistent loop should abort cleanly, got succeeded={abortRes.Succeeded}, err='{abortRes.ErrorMessage}'");
        });

        await test("OpenAiCompatibleChatClient-parses-sse-toolcalls-and-redacts-errors", async () =>
        {
            const string testKey = "sk-unit-test-secret-abcdef1234";
            var handler = new StubOpenAiHttpHandler(testKey);
            using var http = new HttpClient(handler);
            using var client = new OpenAiCompatibleChatClient("https://api.deepseek.com/v1", "deepseek-chat", testKey, httpClient: http);

            // 1. 测试 SSE 流式 reasoning + text + tool_call 解析
            var updates = new List<ChatResponseUpdate>();
            await foreach (ChatResponseUpdate u in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "stream-test")]))
            {
                updates.Add(u);
            }

            string reasoning = string.Concat(updates.SelectMany(u => u.Contents).OfType<TextReasoningContent>().Select(r => r.Text));
            string text = string.Concat(updates.SelectMany(u => u.Contents).OfType<TextContent>().Select(t => t.Text));
            FunctionCallContent? fn = updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>().FirstOrDefault();

            Require(reasoning.Contains("先查快照"), $"Expected reasoning_content, got '{reasoning}'");
            Require(text.Contains("分析完成"), $"Expected streamed text, got '{text}'");
            Require(fn is not null && fn.Name == "get_scan_summary", "Expected parsed streamed tool call");

            // 2. 测试 TestConnectionAsync
            AiConnectionTestResult conn = await client.TestConnectionAsync();
            Require(conn.Success && conn.SupportsToolCalling, $"Expected successful connectivity test, got {conn.StatusBadgeText}: {conn.DetailMessage}");

            // 3. 测试 401 报错脱敏（响应体故意回显 key，客户端必须擦除）
            handler.ForceUnauthorized = true;
            AiConnectionTestResult failConn = await client.TestConnectionAsync();
            Require(!failConn.Success && !failConn.DetailMessage.Contains(testKey, StringComparison.Ordinal),
                "HTTP error detail MUST redact API key");
        });

        await test("EndToEnd-OpenAiHttpJson-to-MeaiAgent-submit-proposal", async () =>
        {
            var (snapshot, env, ids) = BuildAiTestSnapshot();
            var catalog = new ApplicationCatalog(BuiltInApplicationRules.All, []);
            var toolset = new ReadOnlyAiAnalysisToolset(
                catalog: catalog,
                policyEnvironment: env,
                targetCandidatesProvider: () =>
                [
                    new AiTargetCandidateDto("target-vol-d", "D:", @"D:\MigratedData", 500L * 1024 * 1024 * 1024, true)
                ]);
            toolset.AuthorizeSnapshot(snapshot);

            var e2eHandler = new EndToEndAgentHttpHandler(snapshot.ScanId, ids.ComfyModels, ids.WindowsSystem32);
            using var http = new HttpClient(e2eHandler);
            var agent = new BuiltInAiAgentService(
                toolset,
                (cfg, key) => new OpenAiCompatibleChatClient(cfg.Endpoint, cfg.ModelName, key, httpClient: http));

            var settings = new AiProviderSettings
            {
                Preset = AiProviderPreset.Ollama,
                Endpoint = "http://localhost:11434/v1",
                ModelName = "qwen2.5:14b",
                MaxToolSteps = 8
            };

            AiAgentAnalysisResult res = await agent.RunAnalysisAsync(
                settings,
                plainApiKey: null,
                new AiAgentAnalysisRequest(snapshot.ScanId, "端到端真实 JSON 协议工具调用验证"));

            Require(res.Succeeded, $"E2E HTTP->MEAI agent failed: {res.ErrorMessage}");
            Require(res.ToolTraces.Length == 2 && res.ToolTraces.All(t => t.Succeeded),
                $"Expected 2 succeeded E2E tool calls, got {string.Join("; ", res.ToolTraces.Select(t => $"{t.ToolName}:{t.Succeeded}:{t.ResultSummary}"))}");
            Require(res.Proposals.Length == 1 && res.Proposals[0].AcceptedCount == 1 && res.Proposals[0].BlockedByPolicyCount == 1,
                $"Expected 1 accepted + 1 L0 blocked via real JSON deserialization, got accepted={res.Proposals.FirstOrDefault()?.AcceptedCount}, blocked={res.Proposals.FirstOrDefault()?.BlockedByPolicyCount}");
        });

        await test("WpfUiPhase2-ai-assistant-viewmodel-and-adopt-flow", () =>
        {
            var (snapshot, env, ids) = BuildAiTestSnapshot();
            var catalog = new ApplicationCatalog(BuiltInApplicationRules.All, []);
            var toolset = new ReadOnlyAiAnalysisToolset(
                catalog: catalog,
                policyEnvironment: env,
                targetCandidatesProvider: () =>
                [
                    new AiTargetCandidateDto("target-vol-d", "D:", @"D:\MigratedData", 500L * 1024 * 1024 * 1024, true)
                ]);

            string tempCfgDir = Path.Combine(Path.GetTempPath(), "MoveFolder_Phase2_Vm_" + Guid.NewGuid().ToString("N"));
            try
            {
                var adoptedNodes = new List<(Guid ScanId, int NodeId)>();
                var aiVm = new AiAssistantViewModel(
                    toolset: toolset,
                    configStore: new AiProviderConfigStore(tempCfgDir),
                    adoptToPlanCallback: (scanId, nodeId, _) =>
                    {
                        adoptedNodes.Add((scanId, nodeId));
                        return (true, "已加入待迁移清单（未执行）");
                    });

                aiVm.SyncScanSnapshot(snapshot);
                Require(aiVm.HasAuthorizedSnapshot, "AiAssistantViewModel should receive authorized snapshot");
                Require(aiVm.ContextHotspots.Any(h => h.SanitizedPath.Contains("<USER>")),
                    "Context hotspots must display <USER> sanitized paths");

                // 切换预设到 Ollama 验证本地零外发标识
                AiPresetChipItem ollama = aiVm.PresetChips.Single(c => c.Preset == AiProviderPreset.Ollama);
                aiVm.SelectPresetCommand.Execute(ollama);
                Require(aiVm.IsLocalEndpoint && aiVm.OriginBadgeText.Contains("本地"),
                    $"Ollama preset should show local origin badge, got '{aiVm.OriginBadgeText}'");
                aiVm.SaveConfigCommand.Execute(null);

                // 撤销快照共享后状态与热点清空
                aiVm.ToggleSnapshotSharingCommand.Execute(null);
                Require(!aiVm.HasAuthorizedSnapshot && aiVm.ContextHotspots.Count == 0,
                    "Revoking snapshot sharing must clear authorized snapshot and hotspots");
            }
            finally
            {
                try { if (Directory.Exists(tempCfgDir)) Directory.Delete(tempCfgDir, recursive: true); } catch { }
            }
            return Task.CompletedTask;
        });

        await test("AiTimeline-interleaves-thinking-and-compact-expandable-tool-calls-without-8-step-cap", async () =>
        {
            var (snapshot, env, ids) = BuildAiTestSnapshot();
            var catalog = new ApplicationCatalog(BuiltInApplicationRules.All, []);
            var toolset = new ReadOnlyAiAnalysisToolset(
                catalog: catalog,
                policyEnvironment: env,
                targetCandidatesProvider: () =>
                [
                    new AiTargetCandidateDto("target-vol-d", "D:", @"D:\MigratedData", 500L * 1024 * 1024 * 1024, true)
                ]);
            toolset.AuthorizeSnapshot(snapshot);

            // 模拟 10 次不同工具调用（超过旧 8 步限制）且穿插多段思考与调用前的过渡文本，验证默认不限步数 + 统一顺序化时间线
            var turns = new List<AIContent[]>
            {
                new AIContent[]
                {
                    new TextReasoningContent("第一步先查看磁盘扫描总览。"),
                    new TextContent("先并行拉取扫描概览与安全策略。"),
                    new FunctionCallContent("t1", "get_scan_summary", new Dictionary<string, object?> { ["scanId"] = snapshot.ScanId.ToString() }),
                    new FunctionCallContent("t2", "get_migration_policy", new Dictionary<string, object?>())
                },
                new AIContent[]
                {
                    new TextReasoningContent("第二步钻入各分支目录并核查详情。"),
                    new TextContent("接下来逐层钻入头部大目录查看详情。"),
                    new FunctionCallContent("t3", "list_directory_children", new Dictionary<string, object?> { ["scanId"] = snapshot.ScanId.ToString(), ["nodeId"] = 0 }),
                    new FunctionCallContent("t4", "list_directory_children", new Dictionary<string, object?> { ["scanId"] = snapshot.ScanId.ToString(), ["nodeId"] = 3 }),
                    new FunctionCallContent("t5", "list_directory_children", new Dictionary<string, object?> { ["scanId"] = snapshot.ScanId.ToString(), ["nodeId"] = 4 }),
                    new FunctionCallContent("t6", "list_directory_children", new Dictionary<string, object?> { ["scanId"] = snapshot.ScanId.ToString(), ["nodeId"] = 5 }),
                    new FunctionCallContent("t7", "list_directory_children", new Dictionary<string, object?> { ["scanId"] = snapshot.ScanId.ToString(), ["nodeId"] = 6 }),
                    new FunctionCallContent("t8", "get_candidate_details", new Dictionary<string, object?> { ["scanId"] = snapshot.ScanId.ToString(), ["nodeId"] = ids.ComfyModels }),
                    new FunctionCallContent("t9", "inspect_unknown_directory_metadata", new Dictionary<string, object?> { ["scanId"] = snapshot.ScanId.ToString(), ["nodeId"] = ids.UnknownCache }),
                    new FunctionCallContent("t10", "list_migration_candidates", new Dictionary<string, object?> { ["scanId"] = snapshot.ScanId.ToString(), ["pageSize"] = 10 })
                },
                new AIContent[]
                {
                    new TextReasoningContent("证据齐备，汇总最终结论。"),
                    new TextContent("已完成 10 步深度只读分析。")
                }
            };

            string tempCfgDir = Path.Combine(Path.GetTempPath(), "MoveFolder_Phase2_Timeline_" + Guid.NewGuid().ToString("N"));
            try
            {
                var agent = new BuiltInAiAgentService(toolset, (_, _) => new ScriptedStreamingChatClient(turns));
                var vm = new AiAssistantViewModel(
                    toolset: toolset,
                    configStore: new AiProviderConfigStore(tempCfgDir),
                    agentService: agent);
                vm.SyncScanSnapshot(snapshot);
                vm.SelectPresetCommand.Execute(vm.PresetChips.Single(c => c.Preset == AiProviderPreset.Ollama));
                Require(vm.MaxToolSteps == 0, $"Default MaxToolSteps should be 0 (unlimited), got {vm.MaxToolSteps}");

                vm.UserPromptInput = "深度分析 C 盘";
                await vm.SendPromptCommand.ExecuteAsync(null);

                Require(vm.ConversationTurns.Count == 1, "Should have 1 conversation turn");
                AiConversationTurnItem turn = vm.ConversationTurns[0];
                Require(turn.StepsUsed == 10, $"Unlimited mode should allow all 10 tool calls without hitting old 8-step cap, got {turn.StepsUsed}");
                Require(turn.TraceHeaderText.Contains("已调用 10 次工具"), $"Unexpected header: {turn.TraceHeaderText}");

                // 验证严格统一时间线顺序（相邻工具调用自动聚合为分组）：
                // [0]Thinking1 -> [1]Msg1 -> [2]ToolBatch1(2 tools) -> [3]Thinking2 -> [4]Msg2 -> [5]ToolBatch2(8 tools) -> [6]Thinking3 -> [7]FinalMsg
                Require(turn.TimelineItems.Count == 8, $"Expected 8 unified timeline items (3 thinking + 3 messages + 2 tool batches), got {turn.TimelineItems.Count}");
                Require(turn.TimelineItems[0] is AiThinkingStepItem th1 && th1.Text.Contains("第一步") && !th1.IsStreaming, "Item 0 should be first thinking step");
                Require(turn.TimelineItems[1] is AiAssistantMessageStepItem msg1 && msg1.Text.Contains("先并行拉取") && !msg1.Text.Contains("逐层钻入") && !msg1.IsStreaming,
                    "Item 1 should be first preamble message placed right before ToolBatch 1");
                var batch1 = turn.TimelineItems[2] as AiToolBatchStepItem;
                Require(batch1 is not null && batch1.Items.Count == 2 && batch1.IsExpanded && batch1.SummaryText.Contains("获取扫描概览") && batch1.SummaryText.Contains("读取安全策略"),
                    $"Item 2 should be ToolBatch 1 with 2 tools, got '{batch1?.SummaryText}'");
                AiToolCallStepItem tc1 = batch1!.Items[0];
                Require(tc1.ToolName == "get_scan_summary" && !tc1.IsExpanded && !tc1.IsRunning && tc1.Succeeded && tc1.ActionVerb == "获取概览",
                    "First tool in batch 1 should be collapsed completed get_scan_summary");
                Require(batch1.Items[1].ToolName == "get_migration_policy", "Second tool in batch 1 should be get_migration_policy");

                Require(turn.TimelineItems[3] is AiThinkingStepItem th2 && th2.Text.Contains("第二步") && !th2.Text.Contains("第一步"),
                    "Item 3 should be separate second thinking step");
                Require(turn.TimelineItems[4] is AiAssistantMessageStepItem msg2 && msg2.Text.Contains("逐层钻入") && !msg2.Text.Contains("先并行拉取"),
                    "Item 4 should be separate second preamble message placed right before ToolBatch 2");
                var batch2 = turn.TimelineItems[5] as AiToolBatchStepItem;
                Require(batch2 is not null && batch2.Items.Count == 8 && batch2.SummaryText.Contains("钻入 5 个目录"),
                    $"Item 5 should be ToolBatch 2 with 8 tools, got '{batch2?.SummaryText}'");

                Require(turn.TimelineItems[6] is AiThinkingStepItem th3 && th3.Text.Contains("证据齐备") && !th3.IsStreaming,
                    "Item 6 should be final thinking step");
                Require(turn.TimelineItems[7] is AiAssistantMessageStepItem finalMsg && finalMsg.Text.Contains("已完成 10 步深度只读分析") && !finalMsg.IsStreaming,
                    "Last timeline item should be final assistant conclusion");

                // 验证单行工具项过滤 scanId 并可点击展开查看完整参数与结果
                Require(tc1.CompactTarget == "get_scan_summary", $"CompactTarget should omit redundant scanId, got '{tc1.CompactTarget}'");
                tc1.IsExpanded = true;
                Require(tc1.IsExpanded && tc1.DisplayArguments.Contains(snapshot.ScanId.ToString()), "Expanded tool step should expose full arguments summary");
            }
            finally
            {
                try { if (Directory.Exists(tempCfgDir)) Directory.Delete(tempCfgDir, recursive: true); } catch { }
            }
        });

        await test("DrawerResizeRangeHelper-enforces-safe-bounds-across-window-sizes", () =>
        {
            // 默认 1000px 窗口（扣除 Margin 36px ≈ 964px，分隔条 14px -> 可用 950px）
            DrawerResizeBounds normal = DrawerResizeRangeHelper.CalculateBounds(964, 14, 500, 320, 920, 0.55);
            Require(normal.LeftMinWidth == 500 && normal.RightMinWidth == 320 && Math.Abs(normal.RightMaxWidth - 450) < 0.1,
                $"Normal 1000px bounds mismatch: {normal}");
            Require(DrawerResizeRangeHelper.ClampWidth(760, normal) == 450, "Restoring from maximized should clamp drawer width to 450");

            // 1920px 最大化窗口（扣除 Margin 36px = 1884px，分隔条 14px -> 可用 1870px）
            DrawerResizeBounds maximized = DrawerResizeRangeHelper.CalculateBounds(1884, 14, 500, 320, 920, 0.55);
            Require(maximized.RightMaxWidth == 920 && maximized.LeftMinWidth == 500,
                $"Maximized 1920px bounds mismatch: {maximized}");
            Require(DrawerResizeRangeHelper.ClampWidth(760, maximized) == 760, "Maximized should restore user's preferred 760px width");

            // 1440px 窗口（受 55% 占比约束，不挤压左侧）
            DrawerResizeBounds laptop = DrawerResizeRangeHelper.CalculateBounds(1404, 14, 500, 320, 920, 0.55);
            Require(laptop.RightMaxWidth <= 1390 * 0.55 + 0.1 && laptop.RightMaxWidth >= 760,
                $"1440px bounds should cap at 55% ratio: {laptop}");

            // 极窄 650px 窗口回退：保证 LeftMin + RightMin <= available 且 RightMin <= RightMax
            DrawerResizeBounds narrow = DrawerResizeRangeHelper.CalculateBounds(650, 14, 500, 320, 920, 0.55);
            Require(narrow.RightMinWidth <= narrow.RightMaxWidth && narrow.LeftMinWidth + narrow.RightMinWidth <= 636 + 0.1,
                $"Narrow window fallback must prevent overflow: {narrow}");
            return Task.CompletedTask;
        });

        await test("SystemEnvironmentTool-and-StickyBottomScroll-verify-privacy-and-autoscroll", async () =>
        {
            // 1. 验证 get_system_environment 真实只读采集：包含磁盘与系统信息，严禁泄露真实用户名或计算机名
            var toolset = new ReadOnlyAiAnalysisToolset();
            AiCapabilitiesResponse caps = toolset.GetCapabilities();
            Require(caps.AvailableTools.Contains("get_system_environment"), "Capabilities must expose get_system_environment");

            AiSystemEnvironmentResponse envInfo = toolset.GetSystemEnvironment();
            Require(envInfo.Drives.Length >= 1, "System environment should enumerate at least one ready drive");
            Require(!string.IsNullOrWhiteSpace(envInfo.OsVersion) && !string.IsNullOrWhiteSpace(envInfo.SystemDrive),
                "System environment should report OS version and system drive");

            string serializedEnv = JsonSerializer.Serialize(envInfo);
            string currentUser = Environment.UserName;
            if (!string.IsNullOrWhiteSpace(currentUser) && currentUser.Length >= 3)
            {
                Require(!serializedEnv.Contains($"\\Users\\{currentUser}", StringComparison.OrdinalIgnoreCase),
                    "System environment special folders MUST mask real Windows username as <USER>");
            }
            string machineName = Environment.MachineName;
            if (!string.IsNullOrWhiteSpace(machineName) && machineName.Length >= 4)
            {
                Require(!serializedEnv.Contains(machineName, StringComparison.OrdinalIgnoreCase),
                    "System environment MUST NOT leak MachineName");
            }

            // 2. 验证 BuiltInAiAgentService 白名单可直接调用 get_system_environment 并归入时间线工具组
            var agentClient = new ScriptedStreamingChatClient(
            [
                [
                    new FunctionCallContent("env_1", "get_system_environment", new Dictionary<string, object?>())
                ],
                [
                    new TextContent("已根据本机磁盘环境生成建议。")
                ]
            ]);
            string tempCfgDir = Path.Combine(Path.GetTempPath(), "MoveFolder_Phase2_EnvTool_" + Guid.NewGuid().ToString("N"));
            try
            {
                var agent = new BuiltInAiAgentService(toolset, (_, _) => agentClient);
                var vm = new AiAssistantViewModel(toolset, new AiProviderConfigStore(tempCfgDir), agent);
                vm.SelectPresetCommand.Execute(vm.PresetChips.Single(c => c.Preset == AiProviderPreset.Ollama));
                vm.UserPromptInput = "看看我有哪些磁盘";
                await vm.SendPromptCommand.ExecuteAsync(null);

                Require(vm.ConversationTurns.Count == 1 && vm.ConversationTurns[0].StepsUsed == 1, "Agent should execute get_system_environment");
                var batch = vm.ConversationTurns[0].TimelineItems[0] as AiToolBatchStepItem;
                Require(batch is not null && batch.SummaryText.Contains("获取系统环境") && batch.Items[0].ActionVerb == "查询环境",
                    $"Expected tool batch summary for get_system_environment, got '{batch?.SummaryText}'");
                Require(batch!.Items[0].HasResultPayload && batch.Items[0].ResultPayload.Contains("\"drives\""),
                    "Completed tool call must capture raw sanitized ResultPayload for user inspection popup");
                string prettyJson = AiToolPayloadWindow.FormatPayloadForDisplay(batch.Items[0].ResultPayload);
                Require(prettyJson.Contains('\n') && prettyJson.Contains("已严格脱敏"),
                    "AiToolPayloadWindow.FormatPayloadForDisplay should pretty-print JSON while keeping readable Chinese text");
            }
            finally
            {
                try { if (Directory.Exists(tempCfgDir)) Directory.Delete(tempCfgDir, recursive: true); } catch { }
            }

            // 3. 验证回复框智能贴底跟踪状态机（底部增长自动跟随，上滑立即解除，滚回底部自动恢复）
            var (stick1, scroll1) = AiAssistantDrawerControl.ComputeNextStickToBottomState(true, extentHeightChange: 40, verticalOffset: 300, scrollableHeight: 340);
            Require(stick1 && scroll1, "Growing content while stuck at bottom must auto-scroll to end");

            var (stick2, scroll2) = AiAssistantDrawerControl.ComputeNextStickToBottomState(true, extentHeightChange: 0, verticalOffset: 120, scrollableHeight: 340);
            Require(!stick2 && !scroll2, "User scrolling up away from bottom must pause sticky auto-scroll");

            var (stick3, scroll3) = AiAssistantDrawerControl.ComputeNextStickToBottomState(false, extentHeightChange: 50, verticalOffset: 120, scrollableHeight: 390);
            Require(!stick3 && !scroll3, "Growing content while user is reading history must NOT yank scrollbar back to bottom");

            var (stick4, scroll4) = AiAssistantDrawerControl.ComputeNextStickToBottomState(false, extentHeightChange: 0, verticalOffset: 380, scrollableHeight: 390);
            Require(stick4 && !scroll4, "User scrolling back within bottom tolerance must re-enable sticky auto-scroll");
        });

        await test("NativeGuide-SingleDrivePremise-TreeIndeterminate-and-CardSync", async () =>
        {
            // 1. 验证 C:\ProgramData\DockerDesktop\vm-data 被内置规则识别为 L2 厂商原生引导（而非 L3 可迁移）
            var catalog = new ApplicationCatalog(BuiltInApplicationRules.All, []);
            SafetyPolicyDecision dockerDecision = MigrationSafetyPolicy.EvaluateSource(
                @"C:\ProgramData\DockerDesktop\vm-data",
                catalog,
                SafetyEvaluationContext.ScanPreview,
                checkFilesystem: false);
            Require(dockerDecision.Level == MigrationAdmissionLevel.VendorNativeRecommended,
                $"ProgramData\\DockerDesktop\\vm-data must be classified as L2 VendorNativeRecommended, got {dockerDecision.Level}");

            // 2. 验证 AI 提交 NativeGuideAdvisory 即使目录本身为 L3 也强制不可加入符号链接迁移清单
            var (snapshot, env, ids) = BuildAiTestSnapshot();
            var singleCDriveEnv = new AiSystemEnvironmentResponse(
                OsDescription: "Microsoft Windows 11",
                OsVersion: "10.0.26100.0",
                OsArchitecture: "X64",
                SystemDrive: "C:",
                IsAdministrator: true,
                IsDeveloperModeEnabled: false,
                CanCreateSymlinks: true,
                CanUseRawNtfsMftScan: true,
                AuthorizedScanCount: 1,
                Drives:
                [
                    new AiDiskVolumeInfoDto("C:", "Windows", "Fixed", "NTFS", 1000L * 1024 * 1024 * 1024, 800L * 1024 * 1024 * 1024, 200L * 1024 * 1024 * 1024, 80.0, true, false, null, "系统盘")
                ],
                SpecialFolders: [],
                PrivacyNotice: "test");

            var toolset = new ReadOnlyAiAnalysisToolset(
                catalog: catalog,
                policyEnvironment: env,
                targetCandidatesProvider: () => [],
                systemEnvironmentProvider: () => singleCDriveEnv);
            toolset.AuthorizeSnapshot(snapshot);

            AiMigrationProposalReceipt nativeReceipt = toolset.SubmitMigrationProposal(new AiMigrationProposalRequest(
                SchemaVersion: 1,
                ClientRequestId: "native-guide-check-1",
                ScanId: snapshot.ScanId,
                PolicyVersion: MigrationSafetyPolicy.CurrentPolicyVersion,
                Items:
                [
                    new AiMigrationProposalItemInput(ids.UnknownCache, null, 1, "厂商自带原生数据目录迁移设置，不建议符号链接", null, "NativeGuideAdvisory")
                ]));
            Require(nativeReceipt.AcceptedCount == 1 && !nativeReceipt.Items[0].IsAdoptableToMigrationPlan && nativeReceipt.Items[0].IsAdvisoryOnly,
                "NativeGuideAdvisory proposal item must NEVER be adoptable to symlink migration plan");

            // 3. 验证单 C 盘（无可用跨盘目标）前提在首轮 System Prompt 中被明确注入，要求 AI 开场告知用户并允许继续分析
            var promptCapture = new PromptCapturingChatClient();
            var singleDriveAgent = new BuiltInAiAgentService(toolset, (_, _) => promptCapture);
            await singleDriveAgent.RunAnalysisAsync(
                new AiProviderSettings { Preset = AiProviderPreset.Ollama, Endpoint = "http://localhost:11434/v1", ModelName = "mock" },
                null,
                new AiAgentAnalysisRequest(snapshot.ScanId, "分析大目录"));
            Require(promptCapture.LastSystemPrompt.Contains("没有其他可用的本地固定 NTFS 目标盘")
                && promptCapture.LastSystemPrompt.Contains("本轮回复开头第一段"),
                "Single-drive environment must inject upfront notice directive into System Prompt");
            Require(promptCapture.LastSystemPrompt.Contains("单个完整游戏根目录")
                && promptCapture.LastSystemPrompt.Contains("res_packages")
                && promptCapture.LastSystemPrompt.Contains("程序本体和共享运行环境"),
                "System Prompt must include tiered migration granularity rules for games vs dev/AI tools");
        });

        await test("HardLinkClassification-TreeHazardCache-and-LosslessInternalHardLinkCopy", async () =>
        {
            // 1. MFT 快照级测试：验证 O(1) GetSubtreeHazardFlags 与 AnalyzeSubtreeHazards 精准区分「纯内部硬链接」与「跨外部硬链接」
            const long GB = 1024L * 1024 * 1024;
            var pool = new Utf8NamePool();
            (byte p, int o, ushort l) AddName(string n) => pool.AddName(n);

            // 0: C:\
            // 1: ModelsInternal (纯内部硬链接：blobs\m1.safetensors <-> checkpoints\m1.safetensors, MFT=1001, LinkCount=2)
            //    2: blobs -> 3: m1.safetensors (MFT=1001, LinkCount=2)
            //    4: checkpoints -> 5: m1.safetensors (MFT=1001, LinkCount=2, Secondary)
            // 6: ModelsExternal (跨外部硬链接：ext.safetensors 链接到外部 9: ExternalCache\ext.safetensors, MFT=2002, LinkCount=2)
            //    7: ext.safetensors (MFT=2002, LinkCount=2)
            // 8: ExternalCache
            //    9: ext.safetensors (MFT=2002, LinkCount=2, Secondary)
            var nodeSpecs = new (string Name, int Parent, bool IsDir, ulong Mft, ushort LinkCount, ScanNodeFlags ExtraFlags, long Logical, long Unique)[]
            {
                (@"C:\", -1, true, 5, 1, ScanNodeFlags.None, 30 * GB, 20 * GB),
                ("ModelsInternal", 0, true, 100, 1, ScanNodeFlags.None, 20 * GB, 10 * GB),
                ("blobs", 1, true, 101, 1, ScanNodeFlags.None, 10 * GB, 10 * GB),
                ("m1.safetensors", 2, false, 1001, 2, ScanNodeFlags.None, 10 * GB, 10 * GB),
                ("checkpoints", 1, true, 102, 1, ScanNodeFlags.None, 10 * GB, 0),
                ("m1.safetensors", 4, false, 1001, 2, ScanNodeFlags.IsHardLinkSecondary, 10 * GB, 0),
                ("ModelsExternal", 0, true, 200, 1, ScanNodeFlags.None, 5 * GB, 5 * GB),
                ("ext.safetensors", 6, false, 2002, 2, ScanNodeFlags.None, 5 * GB, 5 * GB),
                ("ExternalCache", 0, true, 300, 1, ScanNodeFlags.None, 5 * GB, 0),
                ("ext.safetensors", 8, false, 2002, 2, ScanNodeFlags.IsHardLinkSecondary, 5 * GB, 0)
            };

            var nodes = new ScanNodeRecord[nodeSpecs.Length];
            var kids = Enumerable.Range(0, nodeSpecs.Length).Select(_ => new List<int>()).ToArray();
            for (int i = 0; i < nodeSpecs.Length; i++)
            {
                var s = nodeSpecs[i];
                var (pg, off, len) = AddName(s.Name);
                nodes[i] = new ScanNodeRecord
                {
                    NodeId = i,
                    ParentNodeId = s.Parent,
                    MftRecordNumber = s.Mft,
                    SequenceNumber = 1,
                    NamePageIndex = pg,
                    NameOffset = off,
                    NameLength = len,
                    Flags = (s.IsDir ? ScanNodeFlags.IsDirectory : ScanNodeFlags.None) | s.ExtraFlags,
                    LogicalBytes = s.Logical,
                    AllocatedBytes = s.Logical,
                    UniqueAllocatedBytes = s.Unique,
                    FileCount = s.IsDir ? 1 : 1,
                    DirectoryCount = s.IsDir ? 1 : 0,
                    LinkCount = s.LinkCount
                };
                if (s.Parent >= 0) kids[s.Parent].Add(i);
            }
            var offsets = new int[nodeSpecs.Length + 1];
            var flatKids = new List<int>();
            for (int i = 0; i < nodeSpecs.Length; i++)
            {
                offsets[i] = flatKids.Count;
                flatKids.AddRange(kids[i]);
            }
            offsets[nodeSpecs.Length] = flatKids.Count;

            var hlSnapshot = new ScanSnapshot
            {
                Volume = new ScanVolumeInfo(@"C:\", "System", @"\\?\Volume{hl-test}\", 1, "NTFS", 500 * GB, 200 * GB, true, "test"),
                Backend = ScanBackendKind.RawNtfsMft,
                Coverage = ScanCoverageStatus.Complete,
                StartedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1),
                CompletedAtUtc = DateTimeOffset.UtcNow,
                PolicyVersion = MigrationSafetyPolicy.CurrentPolicyVersion,
                RootNodeId = 0,
                Nodes = nodes,
                CsrOffsets = offsets,
                CsrChildren = flatKids.ToArray(),
                NamePool = pool
            };

            // 验证 O(1) 树缓存标记
            SubtreeHazardFlags internalDirFlags = hlSnapshot.GetSubtreeHazardFlags(1);
            Require((internalDirFlags & SubtreeHazardFlags.HasInternalHardLinks) != 0 && (internalDirFlags & SubtreeHazardFlags.HasExternalHardLinks) == 0,
                $"ModelsInternal should have InternalOnly flag, got {internalDirFlags}");

            SubtreeHazardFlags splitSubDirFlags = hlSnapshot.GetSubtreeHazardFlags(2); // blobs 只包住了 1/2 链接
            Require((splitSubDirFlags & SubtreeHazardFlags.HasExternalHardLinks) != 0,
                $"Subdir 'blobs' alone splits hardlink group and must be marked HasExternalHardLinks, got {splitSubDirFlags}");

            SubtreeHazardFlags externalDirFlags = hlSnapshot.GetSubtreeHazardFlags(6);
            Require((externalDirFlags & SubtreeHazardFlags.HasExternalHardLinks) != 0,
                $"ModelsExternal must be marked HasExternalHardLinks, got {externalDirFlags}");

            // 验证精确分析与 AI 只读工具策略：放行纯内部硬链接，拦截跨外部硬链接并返回外部对端路径
            SubtreeHazardReport internalReport = hlSnapshot.AnalyzeSubtreeHazards(1);
            Require(internalReport.HasInternalOnlyHardLinks && !internalReport.HasBlockingHardLinks && internalReport.InternalHardLinkGroupCount == 1,
                $"Expected InternalOnly report for ModelsInternal, got {internalReport}");

            SubtreeHazardReport externalReport = hlSnapshot.AnalyzeSubtreeHazards(6);
            Require(externalReport.HasBlockingHardLinks && externalReport.HardLinkKind == SubtreeHardLinkKind.ExternalLinked
                && externalReport.SampleExternalPeerPaths.Any(p => p.Contains("ExternalCache", StringComparison.OrdinalIgnoreCase)),
                $"Expected ExternalLinked report with ExternalCache sample peer, got {externalReport}");

            var (_, env, _) = BuildAiTestSnapshot();
            var toolset = new ReadOnlyAiAnalysisToolset(
                catalog: new ApplicationCatalog(BuiltInApplicationRules.All, []),
                policyEnvironment: env,
                targetCandidatesProvider: () => [new AiTargetCandidateDto("target-vol-d", "D:", @"D:\MigratedData", 500 * GB, true)]);
            toolset.AuthorizeSnapshot(hlSnapshot);

            AiCandidateDetailsResponse internalDetails = toolset.GetCandidateDetails(hlSnapshot.ScanId, 1);
            Require(internalDetails.IsAllowedForNewMigration && internalDetails.HardLinkKind == SubtreeHardLinkKind.InternalOnly,
                "Pure internal hardlink directory must be allowed for migration");

            AiCandidateDetailsResponse externalDetails = toolset.GetCandidateDetails(hlSnapshot.ScanId, 6);
            Require(!externalDetails.IsAllowedForNewMigration && externalDetails.HardLinkKind == SubtreeHardLinkKind.ExternalLinked
                && externalDetails.ExternalHardLinkSamplePaths is { Length: >= 1 },
                "External hardlink directory must be blocked with sample external peer paths");

            AiMigrationProposalReceipt hlReceipt = toolset.SubmitMigrationProposal(new AiMigrationProposalRequest(
                SchemaVersion: 1,
                ClientRequestId: "hl-proposal-1",
                ScanId: hlSnapshot.ScanId,
                PolicyVersion: MigrationSafetyPolicy.CurrentPolicyVersion,
                Items:
                [
                    new AiMigrationProposalItemInput(1, "target-vol-d", 1, "纯内部硬链接模型库"),
                    new AiMigrationProposalItemInput(6, "target-vol-d", 2, "含跨外部硬链接目录")
                ]));
            Require(hlReceipt.AcceptedCount == 1 && hlReceipt.BlockedByPolicyCount == 1,
                $"Expected 1 accepted (internal hardlink) and 1 blocked (external hardlink), got accepted={hlReceipt.AcceptedCount}, blocked={hlReceipt.BlockedByPolicyCount}");

            // 2. 真实 NTFS 文件系统端到端验证：CreateHardLinkW 构造真实内部硬链接 + 外部硬链接，执行无损重建复制与拓扑核对
            string tempRoot = Path.Combine(Path.GetTempPath(), "MoveFolder_HardLinkE2E_" + Guid.NewGuid().ToString("N"));
            try
            {
                string srcInternal = Path.Combine(tempRoot, "SrcInternal");
                string dstInternal = Path.Combine(tempRoot, "DstInternal");
                string srcExternal = Path.Combine(tempRoot, "SrcExternal");
                string externalPeerDir = Path.Combine(tempRoot, "ExternalPeer");
                Directory.CreateDirectory(Path.Combine(srcInternal, "blobs"));
                Directory.CreateDirectory(Path.Combine(srcInternal, "checkpoints"));
                Directory.CreateDirectory(dstInternal);
                Directory.CreateDirectory(srcExternal);
                Directory.CreateDirectory(externalPeerDir);

                string blobFile = Path.Combine(srcInternal, "blobs", "weight.bin");
                string ckptLink = Path.Combine(srcInternal, "checkpoints", "weight_link.bin");
                string sameDirLink = Path.Combine(srcInternal, "blobs", "weight_same_dir.bin");
                string plainFile = Path.Combine(srcInternal, "readme.txt");
                await File.WriteAllBytesAsync(blobFile, Encoding.UTF8.GetBytes("FAKE-SAFETENSORS-PAYLOAD-1234567890"));
                await File.WriteAllTextAsync(plainFile, "model readme", Encoding.UTF8);
                Require(CreateHardLinkW(@"\\?\" + ckptLink, @"\\?\" + blobFile, IntPtr.Zero), "CreateHardLinkW for cross-subdir internal link failed");
                Require(CreateHardLinkW(@"\\?\" + sameDirLink, @"\\?\" + blobFile, IntPtr.Zero), "CreateHardLinkW for same-dir internal link failed");

                string extSourceFile = Path.Combine(srcExternal, "shared.bin");
                string extPeerFile = Path.Combine(externalPeerDir, "shared_outside.bin");
                await File.WriteAllTextAsync(extSourceFile, "shared across subtrees", Encoding.UTF8);
                Require(CreateHardLinkW(@"\\?\" + extPeerFile, @"\\?\" + extSourceFile, IntPtr.Zero), "CreateHardLinkW for external link failed");

                var idSvc = new PathIdentityService();
                static FileManifest BuildRealManifestForTest(string rootPath, PathIdentityService identityService)
                {
                    DirectoryIdentity rootId = identityService.ReadDirectory(rootPath, false);
                    var entries = new Dictionary<string, FileManifestEntry>(StringComparer.OrdinalIgnoreCase);
                    void Walk(FileSystemInfo fsi)
                    {
                        var (id, lc) = identityService.ReadEntryWithLinkCount(fsi.FullName);
                        string rel = fsi.FullName.Equals(rootPath, StringComparison.OrdinalIgnoreCase)
                            ? ""
                            : Path.GetRelativePath(rootPath, fsi.FullName);
                        bool isDir = id.IsDirectory;
                        long len = isDir ? 0 : ((FileInfo)fsi).Length;
                        entries[rel] = new FileManifestEntry(rel, isDir, len, fsi.LastWriteTimeUtc, fsi.Attributes, string.Empty, [], null, id, isDir ? 1u : lc);
                        if (isDir)
                        {
                            foreach (FileSystemInfo child in ((DirectoryInfo)fsi).EnumerateFileSystemInfos())
                                Walk(child);
                        }
                    }
                    Walk(new DirectoryInfo(rootPath));
                    return new FileManifest(rootPath, rootId, VerificationMode.Basic, entries);
                }

                FileManifest extManifest = BuildRealManifestForTest(srcExternal, idSvc);
                var extAnalysis = CopyVerificationService.AnalyzeManifestHardLinks(extManifest);
                Require(extAnalysis.HasExternalHardLinks && extAnalysis.ExternalHardLinkRelativePaths.Contains("shared.bin"),
                    "Real NTFS manifest analysis MUST detect external hardlink");

                FileManifest intManifest = BuildRealManifestForTest(srcInternal, idSvc);
                var intAnalysis = CopyVerificationService.AnalyzeManifestHardLinks(intManifest);
                Require(!intAnalysis.HasExternalHardLinks && intAnalysis.HasInternalHardLinks && intAnalysis.InternalGroups.Length == 1
                    && intAnalysis.InternalGroups[0].SecondaryRelativePaths.Length == 2,
                    "Real NTFS manifest analysis MUST detect 1 closed internal hardlink group with 3 entries");

                var secondarySet = new HashSet<string>(intAnalysis.InternalGroups.SelectMany(g => g.SecondaryRelativePaths), StringComparer.OrdinalIgnoreCase);
                await CopyOperationExecutor.ExecuteHardLinkAwareCopyAsync(
                    intManifest,
                    srcInternal,
                    dstInternal,
                    intAnalysis,
                    secondarySet,
                    maxDegreeOfParallelism: 2,
                    sampleMilliseconds: 50,
                    (_, _) => { },
                    () => { },
                    CancellationToken.None);

                // 验证真实目标目录：内容一致、同组 3 个路径共享同一个新 FileId 且 LinkCount == 3，硬链接拓扑核对通过
                FileManifest dstManifest = BuildRealManifestForTest(dstInternal, idSvc);
                var topoMismatches = new List<string>();
                CopyVerificationService.VerifyHardLinkTopology(intManifest, dstManifest, "目标核对失败", topoMismatches);
                Require(topoMismatches.Count == 0, $"Lossless internal hardlink topology verification failed: {string.Join("; ", topoMismatches)}");

                var (dstBlobId, dstBlobLinks) = idSvc.ReadEntryWithLinkCount(Path.Combine(dstInternal, "blobs", "weight.bin"));
                var (dstCkptId, dstCkptLinks) = idSvc.ReadEntryWithLinkCount(Path.Combine(dstInternal, "checkpoints", "weight_link.bin"));
                var (dstSameId, _) = idSvc.ReadEntryWithLinkCount(Path.Combine(dstInternal, "blobs", "weight_same_dir.bin"));
                var srcBlobId = idSvc.ReadEntry(blobFile);
                Require(dstBlobId.FileId == dstCkptId.FileId && dstBlobId.FileId == dstSameId.FileId && dstBlobLinks == 3 && dstCkptLinks == 3,
                    $"Target hardlinks must share identical FileId with LinkCount=3, got {dstBlobId.FileId}/{dstCkptId.FileId}, links={dstBlobLinks}");
                Require(dstBlobId.FileId != srcBlobId.FileId, "Target hardlink group must be an independent FileId from source");
                Require(await File.ReadAllTextAsync(Path.Combine(dstInternal, "checkpoints", "weight_link.bin")) == "FAKE-SAFETENSORS-PAYLOAD-1234567890",
                    "Copied hardlink content must match source bytes");

                // 反向验证：若人为把目标硬链接替换成普通独立副本，拓扑核对必须立即检测并判为不匹配！
                string dstSamePath = Path.Combine(dstInternal, "blobs", "weight_same_dir.bin");
                DateTime origTime = File.GetLastWriteTimeUtc(dstSamePath);
                byte[] origBytes = await File.ReadAllBytesAsync(dstSamePath);
                File.Delete(dstSamePath);
                await File.WriteAllBytesAsync(dstSamePath, origBytes);
                File.SetLastWriteTimeUtc(dstSamePath, origTime);
                FileManifest brokenDstManifest = BuildRealManifestForTest(dstInternal, idSvc);
                var brokenMismatches = new List<string>();
                CopyVerificationService.VerifyHardLinkTopology(intManifest, brokenDstManifest, "目标核对失败", brokenMismatches);
                Require(brokenMismatches.Any(m => m.Contains("内部硬链接未保持共享身份")),
                    "Verification MUST catch broken hardlink topology when target links were split into independent files");
            }
            finally
            {
                try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
            }
        });
    }

    public static void VerifyWpfUiInCurrentSta(Action<DependencyObject> assertButtonsSafe)
    {
        var (snapshot, env, ids) = BuildAiTestSnapshot();
        var catalog = new ApplicationCatalog(BuiltInApplicationRules.All, []);
        var toolset = new ReadOnlyAiAnalysisToolset(
            catalog: catalog,
            policyEnvironment: env,
            targetCandidatesProvider: () =>
            [
                new AiTargetCandidateDto("target-vol-d", "D:", @"D:\MigratedData", 500L * 1024 * 1024 * 1024, true)
            ]);

        string tempCfgDir = Path.Combine(Path.GetTempPath(), "MoveFolder_Phase2_UiSta_" + Guid.NewGuid().ToString("N"));
        try
        {
            var diskPage = new DiskScanPage();
            var diskVm = (DiskScanViewModel)diskPage.DataContext;

            var aiVm = new AiAssistantViewModel(
                toolset: toolset,
                configStore: new AiProviderConfigStore(tempCfgDir),
                adoptToPlanCallback: (scanId, nodeId, suggestedTarget) => diskVm.TryAddAiProposalToPlan(scanId, nodeId, suggestedTarget),
                locateNodeInTreeCallback: (scanId, nodeId) => diskVm.TryLocateNodeInTree(scanId, nodeId),
                closeDrawerCallback: () => diskVm.IsAiDrawerOpen = false);

            diskVm.AiAssistant = aiVm;
            diskVm.SnapshotChanged += (_, snap) => aiVm.SyncScanSnapshot(snap);

            // 1. 实例化并布局画板 10 完整页（1000x620 -> 1920x1000 最大化 -> 1000x620 还原）+ 验证拖动范围与所有按钮无 RenderTransform 冲突
            var aiPage = new AiAssistantPage { DataContext = aiVm };
            aiPage.Measure(new Size(1000, 620));
            aiPage.Arrange(new Rect(0, 0, 1000, 620));
            aiPage.UpdateLayout();
            assertButtonsSafe(aiPage);

            aiPage.Measure(new Size(1920, 1000));
            aiPage.Arrange(new Rect(0, 0, 1920, 1000));
            aiPage.UpdateLayout();
            aiPage.Measure(new Size(1000, 620));
            aiPage.Arrange(new Rect(0, 0, 1000, 620));
            aiPage.UpdateLayout();
            assertButtonsSafe(aiPage);

            // 2. 注入扫描快照并验证自动脱敏同步
            var applyMethod = typeof(DiskScanViewModel).GetMethod(
                "ApplySnapshot",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Require(applyMethod is not null, "DiskScanViewModel.ApplySnapshot not found");
            applyMethod!.Invoke(diskVm, [snapshot, "本地磁盘 (C:)", 0.3]);

            Require(aiVm.HasAuthorizedSnapshot, "AiAssistantViewModel should receive authorized snapshot from DiskScanViewModel");
            Require(aiVm.ContextHotspots.Any(h => h.SanitizedPath.Contains("<USER>")),
                "Context hotspots must display <USER> sanitized paths");

            // 3. 磁盘扫描页展开共用 AI 抽屉并做 1000x620 布局与按钮安全检查
            diskVm.ActiveSubView = DiskScanSubView.DirectoryTree;
            diskVm.ToggleAiDrawerCommand.Execute(null);
            Require(diskVm.IsAiDrawerOpen, "DiskScanViewModel AI drawer should open");
            diskPage.Measure(new Size(1000, 620));
            diskPage.Arrange(new Rect(0, 0, 1000, 620));
            diskPage.UpdateLayout();
            assertButtonsSafe(diskPage);

            // 4. 验证 AI 建议采纳到待迁移清单：L0 被拦截，L1 仅加入清单绝不自动执行，L3 不自动确认
            var (l0Added, _) = diskVm.TryAddAiProposalToPlan(snapshot.ScanId, ids.WindowsSystem32, @"D:\MigratedData\bad");
            Require(!l0Added, "L0 Windows\\System32 must be rejected when adopting AI proposal");

            var (l1Added, _) = diskVm.TryAddAiProposalToPlan(snapshot.ScanId, ids.ComfyModels, @"D:\MigratedData\comfyui\models");
            Require(l1Added, "L1 ComfyUI models should be added to plan");
            Require(diskVm.PlanItems.Count == 1 && diskVm.PlanItems[0].Source == BatchPlanItemSource.AiProposal,
                "Plan item should record AiProposal source");
            Require(!diskVm.IsBatchExecuting, "Adopting AI proposal MUST NEVER start batch execution");

            // 4.1 验证目录树三态半选：展开到 ComfyUI/models 后，祖先行（Users/Alice/Apps/ComfyUI）均呈现半选 IsIndeterminateInPlan
            ScanTreeRowItem? usersRow = diskVm.VisibleTreeRows.FirstOrDefault(r => r.Name == "Users");
            Require(usersRow is not null && !usersRow.IsInPlan && usersRow.IsIndeterminateInPlan,
                "Parent row 'Users' must show indeterminate (half-checked) state when descendant ComfyUI\\models is in plan");

            // 4.2 验证双向同步：将一张 AI 建议卡加入清单后再从待迁移清单移除，AI 卡片 IsAdopted 应自动恢复为 false 且父节点半选清除
            var syncedCard = new AiProposalCardItem
            {
                ProposalId = Guid.NewGuid(),
                ScanId = snapshot.ScanId,
                NodeId = ids.ComfyModels,
                DisplayName = "models",
                SanitizedPath = @"C:\Users\<USER>\Apps\ComfyUI\models",
                ActualLocalPath = snapshot.GetFullPath(ids.ComfyModels),
                AdmissionLevel = MigrationAdmissionLevel.VerifiedEligible,
                Verdict = AiProposalItemVerdict.Accepted,
                IsAdoptableToMigrationPlan = true,
                IsAdopted = true
            };
            var syncedTurn = new AiConversationTurnItem { UserPrompt = "sync check" };
            syncedTurn.ProposalCards.Add(syncedCard);
            aiVm.ConversationTurns.Add(syncedTurn);

            BatchPlanRowItem comfyPlanItem = diskVm.PlanItems.Single(p => p.NodeId == ids.ComfyModels);
            diskVm.RemovePlanItemCommand.Execute(comfyPlanItem);
            Require(!syncedCard.IsAdopted && syncedCard.CanAdopt,
                "Removing item from plan / unchecking in tree MUST reset AI proposal card IsAdopted back to false");
            Require(!usersRow!.IsIndeterminateInPlan,
                "Removing last selected descendant must clear parent indeterminate state");

            // 恢复 L1 以便后续验证步骤不变
            var (l1ReAdded, _) = diskVm.TryAddAiProposalToPlan(snapshot.ScanId, ids.ComfyModels, @"D:\MigratedData\comfyui\models");
            Require(l1ReAdded && syncedCard.IsAdopted,
                "Re-adding item to plan must automatically mark matching AI card as IsAdopted=true");

            var (l3Added, _) = diskVm.TryAddAiProposalToPlan(snapshot.ScanId, ids.UnknownCache, @"D:\MigratedData\cache");
            Require(l3Added, "L3 non-system directory should be addable to plan for user review");
            BatchPlanRowItem l3Row = diskVm.PlanItems.Single(p => p.NodeId == ids.UnknownCache);
            Require(!l3Row.L3RiskAcknowledged, "AI adoption MUST NOT auto-acknowledge L3 risk");

            // 5. 验证 AI 诊断卡片定位目录树节点并自动收起抽屉
            Require(diskVm.TryLocateNodeInTree(snapshot.ScanId, ids.UnknownCache), "Should locate unknown cache node in tree");
            Require(!diskVm.IsAiDrawerOpen && diskVm.SelectedRow?.NodeId == ids.UnknownCache,
                "Locating node should close drawer and select target tree row");

            // 6. 验证 AiToolPayloadWindow 可安全实例化布局、按钮无 RenderTransform 冲突，且 JSON 文本框铺满容器并可垂直滚动（不被全局单行 TextBox Height=40 截断）
            string longPayload = JsonSerializer.Serialize(new
            {
                osDescription = "Microsoft Windows 10.0.26200",
                systemDrive = "C:",
                drives = Enumerable.Range(1, 24).Select(i => new
                {
                    driveLetter = $"D{i}:",
                    totalBytes = 1000000000L * i,
                    freeBytes = 500000000L * i
                })
            });
            var sampleStep = new AiToolCallStepItem
            {
                StepNumber = 2,
                ToolName = "get_system_environment",
                FriendlyLabel = "获取脱敏系统与磁盘环境信息",
                ResultSummary = "已返回只读数据",
                ResultPayload = longPayload,
                IsRunning = false,
                Succeeded = true,
                ElapsedMilliseconds = 12
            };
            var payloadWin = new AiToolPayloadWindow(sampleStep);
            var payloadRoot = payloadWin.Content as FrameworkElement;
            Require(payloadRoot is not null, "AiToolPayloadWindow must have a FrameworkElement root");
            payloadRoot!.Measure(new Size(820, 620));
            payloadRoot.Arrange(new Rect(0, 0, 820, 620));
            payloadRoot.UpdateLayout();
            assertButtonsSafe(payloadRoot);

            var payloadTextBox = payloadWin.FindName("PayloadTextBox") as TextBox;
            Require(payloadTextBox is not null, "AiToolPayloadWindow must contain PayloadTextBox");
            Require(double.IsNaN(payloadTextBox!.Height), $"PayloadTextBox.Height should be Auto (NaN), got {payloadTextBox.Height}");
            Require(payloadTextBox.ActualHeight >= 250,
                $"PayloadTextBox should stretch to fill available vertical space instead of collapsing to single-line height, got ActualHeight={payloadTextBox.ActualHeight}");
            Require(payloadTextBox.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
                $"PayloadTextBox should enable vertical auto scrollbar, got {payloadTextBox.VerticalScrollBarVisibility}");
            payloadTextBox.ApplyTemplate();
            var payloadContentHost = payloadTextBox.Template?.FindName("PART_ContentHost", payloadTextBox) as ScrollViewer;
            Require(payloadContentHost is not null, "PayloadTextBox template must define PART_ContentHost ScrollViewer");
            Require(payloadContentHost!.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
                $"PART_ContentHost must bind VerticalScrollBarVisibility=Auto, got {payloadContentHost.VerticalScrollBarVisibility}");
            Require(payloadContentHost.VerticalAlignment == VerticalAlignment.Stretch,
                $"PART_ContentHost must stretch vertically, got {payloadContentHost.VerticalAlignment}");
            Require(payloadContentHost.ActualHeight >= 220,
                $"PART_ContentHost viewport should fill PayloadTextBox, got ActualHeight={payloadContentHost.ActualHeight}");

            // 6b. 验证非 TestLab 模式下重启应用后，手动模式默认保持在步骤 1，不再强行跳入步骤 4 历史结果页
            var mainVm = new MainViewModel();
            Require(mainVm.CurrentStep == 1, $"Manual mode should start cleanly on Step 1 when not in TestLab, got Step {mainVm.CurrentStep}");

            // 7. 验证 SafeMarkdownViewer 安全渲染（支持标题/列表/加粗/代码/表格，物理上零 Hyperlink）
            const string sampleMd = """
                ## 空间分析结论
                发现 **128 GB** 可安全迁移目录 `C:\Users\<USER>\Apps\ComfyUI\models`（含 *L1 已验证规则* 与 ~~旧缓存~~）。

                > 提醒：迁移后原目录会在源盘保留备份，请先验证软件正常再释放空间。

                - 优先迁移：`C:\Users\<USER>\Apps\ComfyUI\models`
                - 请勿点击危险链接：[执行命令](file:///C:/Windows/System32/cmd.exe)

                1. 第一步：加入待迁移清单
                2. 第二步：人工核对后执行

                | 目录 | 大小 | 级别 |
                | --- | --- | --- |
                | `ComfyUI/models` | 128 GB | L1 |

                ```text
                C:\Users\<USER>\Apps\ComfyUI\models
                ```
                """;

            var mdViewer = new SafeMarkdownViewer { MarkdownText = sampleMd };
            mdViewer.Measure(new Size(340, 2000));
            mdViewer.Arrange(new Rect(0, 0, 340, 2000));
            mdViewer.UpdateLayout();

            int textBlockCount = 0;
            bool foundHyperlink = false;
            bool foundRawFileUri = false;
            var stack = new Stack<DependencyObject>();
            stack.Push(mdViewer);
            while (stack.Count > 0)
            {
                DependencyObject cur = stack.Pop();
                if (cur is TextBlock tb)
                {
                    textBlockCount++;
                    foreach (Inline inline in tb.Inlines)
                    {
                        if (inline is Hyperlink) foundHyperlink = true;
                        if (inline is Run r && r.Text.Contains("file:///", StringComparison.OrdinalIgnoreCase))
                        {
                            foundRawFileUri = true;
                        }
                    }
                    if (tb.Text.Contains("file:///", StringComparison.OrdinalIgnoreCase))
                    {
                        foundRawFileUri = true;
                    }
                }
                int childCount = VisualTreeHelper.GetChildrenCount(cur);
                for (int i = 0; i < childCount; i++)
                {
                    stack.Push(VisualTreeHelper.GetChild(cur, i));
                }
            }

            Require(textBlockCount >= 8, $"SafeMarkdownViewer should render structured blocks, got only {textBlockCount} TextBlocks");
            Require(!foundHyperlink, "SafeMarkdownViewer MUST NEVER create Hyperlink controls in elevated WPF process");
            Require(!foundRawFileUri, "SafeMarkdownViewer MUST scrub dangerous file:/// URIs");
        }
        finally
        {
            try { if (Directory.Exists(tempCfgDir)) Directory.Delete(tempCfgDir, recursive: true); } catch { }
        }
    }

    private sealed record TestNodeIds(int WindowsSystem32, int ComfyRoot, int ComfyModels, int UnknownCache);

    private static (ScanSnapshot Snapshot, SafetyPolicyEnvironment Env, TestNodeIds Ids) BuildAiTestSnapshot()
    {
        const long GB = 1024L * 1024 * 1024;
        var pool = new Utf8NamePool();
        (byte p, int o, ushort l) Add(string n) => pool.AddName(n);

        // 0: C:\
        // 1: Windows (L0) -> 2: System32 (L0)
        // 3: Users (L0 container) -> 4: Alice (L0 container) -> 5: Apps -> 6: ComfyUI -> 7: models (L1, 128 GB)
        // 8: AppData (under Alice) -> 9: Local -> 10: BuildTool -> 11: tmp_cache (L3, 14 GB) -> 12: old_build.tmp (file, 120d old)
        var specs = new (string Name, int Parent, bool IsDir, long Size, long AgeDays)[]
        {
            (@"C:\", -1, true, 300 * GB, 0),
            ("Windows", 0, true, 40 * GB, 10),
            ("System32", 1, true, 20 * GB, 10),
            ("Users", 0, true, 160 * GB, 1),
            ("Alice", 3, true, 160 * GB, 1),
            ("Apps", 4, true, 128 * GB, 5),
            ("ComfyUI", 5, true, 128 * GB, 5),
            ("models", 6, true, 128 * GB, 5),
            ("AppData", 4, true, 14 * GB, 120),
            ("Local", 8, true, 14 * GB, 120),
            ("BuildTool", 9, true, 14 * GB, 120),
            ("tmp_cache", 10, true, 14 * GB, 120),
            ("old_build.tmp", 11, false, 14 * GB, 120),
            ("$WinREAgent", 0, true, 3 * GB, 2),
            ("$Recycle.Bin", 0, true, 5 * GB, 1)
        };

        var nodes = new ScanNodeRecord[specs.Length];
        for (int i = 0; i < specs.Length; i++)
        {
            var (page, off, len) = Add(specs[i].Name);
            nodes[i] = new ScanNodeRecord
            {
                NodeId = i,
                ParentNodeId = specs[i].Parent,
                NamePageIndex = page,
                NameOffset = off,
                NameLength = len,
                Flags = specs[i].IsDir ? ScanNodeFlags.IsDirectory : ScanNodeFlags.None,
                LogicalBytes = specs[i].Size,
                AllocatedBytes = specs[i].Size,
                UniqueAllocatedBytes = specs[i].Size,
                FileCount = specs[i].IsDir ? 12 : 1,
                DirectoryCount = specs[i].IsDir ? 1 : 0,
                LastWriteUtcTicks = DateTimeOffset.UtcNow.AddDays(-specs[i].AgeDays).UtcTicks
            };
        }

        var childrenByParent = new List<int>[specs.Length];
        for (int i = 0; i < specs.Length; i++) childrenByParent[i] = [];
        for (int i = 1; i < specs.Length; i++) childrenByParent[specs[i].Parent].Add(i);

        var offsets = new int[specs.Length + 1];
        var csrChildren = new List<int>();
        for (int i = 0; i < specs.Length; i++)
        {
            offsets[i] = csrChildren.Count;
            csrChildren.AddRange(childrenByParent[i]);
        }
        offsets[specs.Length] = csrChildren.Count;

        var snapshot = new ScanSnapshot
        {
            Volume = new ScanVolumeInfo(@"C:\", "System", @"\\?\Volume{ai-test}\", 1, "NTFS", 500 * GB, 200 * GB, true, "test"),
            Backend = ScanBackendKind.RawNtfsMft,
            Coverage = ScanCoverageStatus.Complete,
            StartedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-2),
            CompletedAtUtc = DateTimeOffset.UtcNow,
            PolicyVersion = MigrationSafetyPolicy.CurrentPolicyVersion,
            RootNodeId = 0,
            Nodes = nodes,
            CsrOffsets = offsets,
            CsrChildren = csrChildren.ToArray(),
            NamePool = pool
        };

        var env = new SafetyPolicyEnvironment(
            WindowsDirectories: [@"C:\Windows"],
            ProgramFilesDirectories: [@"C:\Program Files", @"C:\Program Files (x86)"],
            ProgramDataDirectories: [@"C:\ProgramData"],
            UsersRoots: [@"C:\Users"],
            UserProfileRoots: [@"C:\Users\Alice"],
            AppDataRoots: [@"C:\Users\Alice\AppData", @"C:\Users\Alice\AppData\Local", @"C:\Users\Alice\AppData\Roaming", @"C:\Users\Alice\AppData\LocalLow"],
            ToolAndStateDirectories: [@"D:\Tools\MoveFolder"],
            IsFixedNtfsVolume: _ => true);

        return (snapshot, env, new TestNodeIds(WindowsSystem32: 2, ComfyRoot: 6, ComfyModels: 7, UnknownCache: 11));
    }

    private sealed class ScriptedMockChatClient(IReadOnlyList<AIContent[]> turns) : IChatClient
    {
        private int _index;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            AIContent[] next = _index < turns.Count ? turns[_index++] : [new TextContent("done")];
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, next)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            AIContent[] next = _index < turns.Count ? turns[_index++] : [new TextContent("done")];
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, next);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class InfiniteToolLoopChatClient(Guid scanId) : IChatClient
    {
        private int _counter;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "summary")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (options?.Tools is null or { Count: 0 })
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("已达步数上限，输出最终总结。")]);
                yield break;
            }

            _counter++;
            yield return new ChatResponseUpdate(ChatRole.Assistant,
            [
                new FunctionCallContent($"loop_{_counter}", "get_scan_summary", new Dictionary<string, object?> { ["scanId"] = scanId.ToString() })
            ]);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class SlowCancellableChatClient(CancellationTokenSource triggerCancel) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromCanceled<ChatResponse>(cancellationToken);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("partial")]);
            triggerCancel.Cancel();
            await Task.Delay(50, cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class StubOpenAiHttpHandler(string expectedKey) : HttpMessageHandler
    {
        public bool ForceUnauthorized { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is not null
                ? await request.Content.ReadAsStringAsync(cancellationToken)
                : string.Empty;

            if (ForceUnauthorized)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent($"{{\"error\":\"Invalid api key {expectedKey}\"}}", Encoding.UTF8, "application/json")
                };
            }

            if (body.Contains("\"stream\":true", StringComparison.Ordinal))
            {
                string sse = string.Join("\n",
                [
                    "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"先查快照\"},\"finish_reason\":null}]}",
                    "data: {\"choices\":[{\"delta\":{\"content\":\"分析完成\"},\"finish_reason\":null}]}",
                    "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"get_scan_summary\",\"arguments\":\"{\\\"scanId\\\":\\\"00000000-0000-0000-0000-000000000001\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}]}",
                    "data: [DONE]"
                ]);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
                };
            }

            const string okJson = """
                {
                  "choices": [
                    {
                      "finish_reason": "tool_calls",
                      "message": {
                        "role": "assistant",
                        "content": null,
                        "tool_calls": [
                          {
                            "id": "call_ping",
                            "type": "function",
                            "function": { "name": "ping_readonly", "arguments": "{}" }
                          }
                        ]
                      }
                    }
                  ]
                }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(okJson, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class EndToEndAgentHttpHandler(Guid scanId, int comfyModelsId, int winSystem32Id) : HttpMessageHandler
    {
        private int _turn;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _turn++;
            string sse = _turn switch
            {
                1 => string.Join("\n",
                [
                    $"data: {{\"choices\":[{{\"delta\":{{\"tool_calls\":[{{\"index\":0,\"id\":\"call_s1\",\"function\":{{\"name\":\"get_scan_summary\",\"arguments\":\"{{\\\"scanId\\\":\\\"{scanId}\\\"}}\"}}}}]}},\"finish_reason\":\"tool_calls\"}}]}}",
                    "data: [DONE]"
                ]),
                2 => string.Join("\n",
                [
                    $"data: {{\"choices\":[{{\"delta\":{{\"tool_calls\":[{{\"index\":0,\"id\":\"call_s2\",\"function\":{{\"name\":\"submit_migration_proposal\",\"arguments\":\"{{\\\"scanId\\\":\\\"{scanId}\\\",\\\"clientRequestId\\\":\\\"e2e-req-1\\\",\\\"items\\\":[{{\\\"nodeId\\\":{comfyModelsId},\\\"targetCandidateId\\\":\\\"target-vol-d\\\",\\\"priority\\\":1,\\\"reason\\\":\\\"L1 模型权重\\\"}},{{\\\"nodeId\\\":{winSystem32Id},\\\"targetCandidateId\\\":\\\"target-vol-d\\\",\\\"priority\\\":2,\\\"reason\\\":\\\"越权测试\\\"}}]}}\"}}}}]}},\"finish_reason\":\"tool_calls\"}}]}}",
                    "data: [DONE]"
                ]),
                _ => string.Join("\n",
                [
                    "data: {\"choices\":[{\"delta\":{\"content\":\"已完成端到端脱敏分析并提交建议卡片。\"},\"finish_reason\":\"stop\"}]}",
                    "data: [DONE]"
                ])
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            });
        }
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private sealed class TimedStreamChatClient(
        int chunkCount,
        int delayBetweenChunksMs,
        int stallAfterChunk,
        int stallMs = 2000) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (int i = 1; i <= chunkCount; i++)
            {
                await Task.Delay(delayBetweenChunksMs, cancellationToken);
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent($"片段{i} ")]);

                if (stallAfterChunk == i)
                {
                    await Task.Delay(stallMs, cancellationToken);
                }
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ScriptedStreamingChatClient(IReadOnlyList<AIContent[]> turns, int delayPerChunkMs = 0) : IChatClient
    {
        private int _index;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            AIContent[] chunks = _index < turns.Count ? turns[_index++] : [new TextContent("done")];
            foreach (AIContent chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (delayPerChunkMs > 0)
                {
                    await Task.Delay(delayPerChunkMs, cancellationToken);
                }
                else
                {
                    await Task.Yield();
                }
                yield return new ChatResponseUpdate(ChatRole.Assistant, [chunk]);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class PromptCapturingChatClient : IChatClient
    {
        public string LastSystemPrompt { get; private set; } = string.Empty;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastSystemPrompt = messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Text ?? string.Empty;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastSystemPrompt = messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Text ?? string.Empty;
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("ok")]);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
