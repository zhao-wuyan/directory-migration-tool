using System.Text;
using MigrationCore.Models;
using MigrationCore.Services;

internal static partial class Program
{
    private static readonly string[] BatchCaseNames =
    [
        "B01-batch-serial-migrate-retains-backups-and-pauses-on-failure"
    ];

    private static async Task RunBatchCases(Lab lab, Guid attempt, CaseEvidence[] cases)
    {
        async Task Case(string name, Func<Dictionary<string, object?>, Task> action) =>
            await RunCase(lab, cases.Single(c => c.Name == name), action);

        await Case("B01-batch-serial-migrate-retains-backups-and-pauses-on-failure", async o =>
        {
            string srcScope = Path.Combine(lab.Source.Root, @"Data\Sources\Sample");
            string dstScope = Path.Combine(lab.Target.Root, @"Data\Targets");

            string src1 = Path.Combine(srcScope, $"Batch1-{attempt:D}");
            string src2 = Path.Combine(srcScope, $"Batch2-{attempt:D}");
            string src3 = Path.Combine(srcScope, $"Batch3-{attempt:D}");
            string dst1 = Path.Combine(dstScope, $"Batch1-{attempt:D}");
            string dst2 = Path.Combine(dstScope, $"Batch2-{attempt:D}");
            string dst3 = Path.Combine(dstScope, $"Batch3-{attempt:D}");

            NewDirectory(lab, lab.Source, srcScope, src1);
            NewDirectory(lab, lab.Source, srcScope, src2);
            NewDirectory(lab, lab.Source, srcScope, src3);
            WriteFile(lab, lab.Source, src1, Path.Combine(src1, PayloadName), Encoding.UTF8.GetBytes("batch item 1"));
            WriteFile(lab, lab.Source, src2, Path.Combine(src2, PayloadName), Encoding.UTF8.GetBytes("batch item 2"));
            WriteFile(lab, lab.Source, src3, Path.Combine(src3, PayloadName), Encoding.UTF8.GetBytes("batch item 3"));

            var planService = new MigrationPlanService();
            var frozen = planService.FreezeAndValidatePlan(
                [
                    new MigrationPlanDraftItem(src1, dst1, 1024, 4096, L3RiskAcknowledged: true),
                    new MigrationPlanDraftItem(src2, dst2, 1024, 4096, L3RiskAcknowledged: true),
                    new MigrationPlanDraftItem(src3, dst3, 1024, 4096, L3RiskAcknowledged: true)
                ],
                VerificationMode.Basic,
                checkFilesystem: true);

            Require(!frozen.HasBlockingIssues && frozen.Items.Length == 3,
                $"Expected valid frozen batch plan, got: {string.Join(';', frozen.BlockingIssues)}");

            // 冻结后在执行前故意污染第 2 项的目标目录，触发单项执行前实时重验失败并暂停第 3 项
            NewDirectory(lab, lab.Target, dstScope, dst2);
            WriteFile(lab, lab.Target, dst2, Path.Combine(dst2, "conflict.txt"), Encoding.UTF8.GetBytes("pre-existing"));

            var batchService = new MigrationBatchService(planService);
            BatchMigrationSummary summary = await batchService.ExecuteBatchAsync(frozen, robocopyThreads: 2);

            Require(summary.SucceededCount == 1 && summary.FailedCount == 1 && summary.PausedDueToFailure,
                $"Expected 1 succeeded, 1 failed & paused; got ok={summary.SucceededCount}, fail={summary.FailedCount}, paused={summary.PausedDueToFailure}");
            Require(summary.FinalItems[0].Status == BatchPlanItemStatus.CompletedAwaitingVerification, "Item 1 must await user verification");
            Require(summary.FinalItems[1].Status == BatchPlanItemStatus.Failed, "Item 2 must fail live re-verification");
            Require(summary.FinalItems[2].Status == BatchPlanItemStatus.Skipped && !Directory.Exists(dst3),
                "Item 3 must be paused/skipped without executing");

            string? backup1 = summary.FinalItems[0].ExecutionResult?.BackupPath;
            Require(!string.IsNullOrEmpty(backup1) && Directory.Exists(backup1), "Item 1 backup must be retained on source volume");

            o["succeeded"] = summary.SucceededCount;
            o["failed"] = summary.FailedCount;
            o["pausedReason"] = summary.PauseReason;
            o["item1Backup"] = backup1;
        });
    }
}
