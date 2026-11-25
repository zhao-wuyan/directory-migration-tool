using MigrationCore.Models;
using MigrationCore.Models.Safety;
using MigrationCore.Models.Scanning;
using MigrationCore.Services.Safety;
using MigrationCore.Services.Scanning;

namespace MigrationCore.Services;

/// <summary>
/// 批量安全迁移串行调度器：在应用级 OperationExecutionGate 排他锁内，逐项实时重验后直接复用现有 MigrationService。
/// 绝不自建第二套复制/链接/删除逻辑；单项失败立即暂停后续队列，保留全部已成功项与当前项证据，永不自动删除任何备份。
/// </summary>
public sealed class MigrationBatchService
{
    private readonly MigrationPlanService _planService;
    private readonly OperationExecutionGate _gate;
    private readonly TargetedPathVerifier _pathVerifier;
    private readonly VolumeSnapshotCache? _snapshotCache;
    private readonly TargetedMigrationVerifyOptions _verifyOptions;

    public MigrationBatchService(
        MigrationPlanService? planService = null,
        OperationExecutionGate? gate = null,
        TargetedPathVerifier? pathVerifier = null,
        VolumeSnapshotCache? snapshotCache = null,
        TargetedMigrationVerifyOptions? verifyOptions = null)
    {
        _planService = planService ?? new MigrationPlanService();
        _gate = gate ?? OperationExecutionGate.Shared;
        _pathVerifier = pathVerifier ?? new TargetedPathVerifier();
        _snapshotCache = snapshotCache ?? VolumeSnapshotCache.Shared;
        _verifyOptions = verifyOptions ?? new TargetedMigrationVerifyOptions();
    }

    public async Task<BatchMigrationSummary> ExecuteBatchAsync(
        FrozenMigrationPlan frozenPlan,
        int robocopyThreads = 8,
        int largeFileThresholdMB = 1024,
        IProgress<BatchItemProgress>? progress = null,
        IProgress<string>? log = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(frozenPlan);

        string[] activePaths = frozenPlan.Items
            .SelectMany(i => new[] { i.SourcePath, i.TargetPath })
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToArray();

        await using var lease = await _gate.AcquireOrThrowAsync(
            "BatchMigration",
            $"批量安全迁移（共 {frozenPlan.Items.Length} 项，计划版本 #{frozenPlan.PlanRevision}）",
            activePaths,
            ct).ConfigureAwait(false);

        // 1. 全队列启动前预检（摘要校验 + 关联进程只读检测 + 目标卷空间复核）
        BatchPreflightReport preflight = _planService.RunReadOnlyProcessPreflight(frozenPlan);
        if (!preflight.CanStart)
        {
            string reason = string.Join("；", preflight.BlockingIssues);
            log?.Report($"[批量预检阻断] {reason}");
            return new BatchMigrationSummary(
                PlanRevision: frozenPlan.PlanRevision,
                SucceededCount: 0,
                FailedCount: 0,
                CancelledCount: 0,
                RemainingCount: frozenPlan.Items.Length,
                PausedDueToFailure: true,
                PauseReason: reason,
                FinalItems: frozenPlan.Items);
        }

        MigrationPlanItem[] workingItems = frozenPlan.Items.ToArray();
        int succeeded = 0;
        int failed = 0;
        int cancelled = 0;
        bool pausedDueToFailure = false;
        string? pauseReason = null;

        for (int i = 0; i < workingItems.Length; i++)
        {
            MigrationPlanItem item = workingItems[i];
            if (ct.IsCancellationRequested)
            {
                workingItems[i] = item with { Status = BatchPlanItemStatus.Cancelled, BlockingReason = "用户已取消后续队列。" };
                cancelled++;
                continue;
            }

            // 2. 单项执行前实时重验（复用 _planService 的 matcher/env、校验身份/策略/目标空/进程/目标卷空间）
            string? liveError = _planService.ReverifyItemBeforeExecution(item);
            if (liveError is null)
            {
                // 2b. 指定路径定向实时复扫（仅复扫 item.SourcePath，绝不全盘重扫；支持 NTFS USN 零 IO 快检）
                ScanSnapshot? cachedSnapshot = item.ScanId.HasValue
                    ? _snapshotCache?.TryGetByScanId(item.ScanId.Value, runLiveProbe: false)?.Entry.Snapshot
                    : null;

                TargetedMigrationVerificationResult targeted = await _pathVerifier.VerifyDirectoryForMigrationAsync(
                    sourcePath: item.SourcePath,
                    expectedRootIdentity: item.SourceIdentity,
                    expectedLogicalBytes: item.LogicalBytes,
                    expectedAllocatedBytes: item.AllocatedBytes,
                    expectedFileCount: item.ExpectedFileCount,
                    expectedHasReparseDescendants: item.HasReparseDescendants,
                    cachedSnapshot: cachedSnapshot,
                    cachedNodeId: item.NodeId,
                    options: _verifyOptions,
                    ct: ct).ConfigureAwait(false);

                if (!targeted.IsAllowed)
                {
                    liveError = targeted.Message ?? $"定向实时复扫未通过（{targeted.ReasonCode}）。";
                }
                else
                {
                    long liveRequired = MigrationPlanService.ComputeConservativeTargetBytes(
                        targeted.LiveLogicalBytes,
                        targeted.LiveAllocatedBytes,
                        item.IsCompressedOrSparse);
                    liveError = _planService.CheckTargetVolumeHasSpace(item.TargetPath, liveRequired);
                    if (liveError is null)
                    {
                        log?.Report(targeted.UsedUsnFastPath
                            ? $"[{i + 1}/{workingItems.Length}] USN 零变动快检通过：{item.SourcePath}"
                            : $"[{i + 1}/{workingItems.Length}] 定向路径复扫通过：{item.SourcePath}（最新 {FileStatsService.FormatBytes(Math.Max(targeted.LiveLogicalBytes, targeted.LiveAllocatedBytes))} / {targeted.LiveFileCount:N0} 文件）");
                    }
                }
            }

            if (liveError is not null)
            {
                workingItems[i] = item with { Status = BatchPlanItemStatus.Failed, BlockingReason = liveError };
                failed++;
                pausedDueToFailure = true;
                pauseReason = $"第 {i + 1}/{workingItems.Length} 项执行前实时重验未通过，已暂停后续队列：{liveError}";
                log?.Report($"[批量暂停] {pauseReason}");
                progress?.Report(new BatchItemProgress(i, workingItems.Length, item.PlanItemId, item.SourcePath, item.TargetPath, null, BatchPlanItemStatus.Failed, liveError));
                MarkRemainingSkipped(workingItems, i + 1, "前序项目失败，队列已暂停");
                break;
            }

            workingItems[i] = item with { Status = BatchPlanItemStatus.Running };
            progress?.Report(new BatchItemProgress(i, workingItems.Length, item.PlanItemId, item.SourcePath, item.TargetPath, null, BatchPlanItemStatus.Running, $"开始迁移：{item.SourcePath}"));
            log?.Report($"[{i + 1}/{workingItems.Length}] 开始串行迁移：{item.SourcePath} -> {item.TargetPath}（核对模式：{frozenPlan.VerificationMode}）");

            var config = new MigrationConfig
            {
                SourcePath = item.SourcePath,
                TargetPath = item.TargetPath,
                LargeFileThresholdMB = largeFileThresholdMB,
                RobocopyThreads = Math.Clamp(robocopyThreads, 1, 128),
                VerificationMode = frozenPlan.VerificationMode,
                SampleMilliseconds = 1000
            };

            var innerProgress = new Progress<MigrationProgress>(p =>
            {
                progress?.Report(new BatchItemProgress(i, workingItems.Length, item.PlanItemId, item.SourcePath, item.TargetPath, p, BatchPlanItemStatus.Running, p.Message));
            });

            // 直接复用现有手动安全迁移服务（零第二套复制/建链逻辑）
            var service = new MigrationService(config);
            MigrationResult result = await service.ExecuteMigrationAsync(innerProgress, log, ct).ConfigureAwait(false);

            if (result.Success)
            {
                succeeded++;
                workingItems[i] = item with
                {
                    Status = BatchPlanItemStatus.CompletedAwaitingVerification,
                    TransactionId = result.TransactionId,
                    ExecutionResult = result,
                    BlockingReason = null
                };
                progress?.Report(new BatchItemProgress(i, workingItems.Length, item.PlanItemId, item.SourcePath, item.TargetPath, null, BatchPlanItemStatus.CompletedAwaitingVerification, "技术迁移完成，原始备份已保留等待验证"));
            }
            else if (result.Stage == MigrationStage.Cancelled || ct.IsCancellationRequested)
            {
                cancelled++;
                workingItems[i] = item with
                {
                    Status = BatchPlanItemStatus.Cancelled,
                    TransactionId = result.TransactionId,
                    ExecutionResult = result,
                    BlockingReason = result.ErrorMessage ?? "用户已取消任务"
                };
                progress?.Report(new BatchItemProgress(i, workingItems.Length, item.PlanItemId, item.SourcePath, item.TargetPath, null, BatchPlanItemStatus.Cancelled, "已取消并停止后续队列"));
                MarkRemainingSkipped(workingItems, i + 1, "任务已取消");
                break;
            }
            else
            {
                failed++;
                pausedDueToFailure = true;
                pauseReason = $"第 {i + 1}/{workingItems.Length} 项失败（{result.Stage}），已保留已成功项与当前项证据并暂停后续队列：{result.ErrorMessage}";
                workingItems[i] = item with
                {
                    Status = BatchPlanItemStatus.Failed,
                    TransactionId = result.TransactionId,
                    ExecutionResult = result,
                    BlockingReason = result.ErrorMessage
                };
                log?.Report($"[批量暂停] {pauseReason}");
                progress?.Report(new BatchItemProgress(i, workingItems.Length, item.PlanItemId, item.SourcePath, item.TargetPath, null, BatchPlanItemStatus.Failed, result.ErrorMessage ?? "迁移失败"));
                MarkRemainingSkipped(workingItems, i + 1, "前序项目失败，队列已暂停");
                break;
            }
        }

        int remaining = workingItems.Count(x => x.Status is BatchPlanItemStatus.Pending or BatchPlanItemStatus.Ready or BatchPlanItemStatus.Skipped);
        return new BatchMigrationSummary(
            PlanRevision: frozenPlan.PlanRevision,
            SucceededCount: succeeded,
            FailedCount: failed,
            CancelledCount: cancelled,
            RemainingCount: remaining,
            PausedDueToFailure: pausedDueToFailure,
            PauseReason: pauseReason,
            FinalItems: workingItems);
    }

    private static void MarkRemainingSkipped(MigrationPlanItem[] items, int startIndex, string reason)
    {
        for (int k = startIndex; k < items.Length; k++)
        {
            items[k] = items[k] with { Status = BatchPlanItemStatus.Skipped, BlockingReason = reason };
        }
    }
}
