using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MigrationCore.Models.Cleanup;
using MigrationCore.Services;
using MigrationCore.Services.Cleanup;
using MoveWithSymlinkWPF.Helpers;
using MoveWithSymlinkWPF.Views;

namespace MoveWithSymlinkWPF.ViewModels;

public partial class CleanupViewModel
{
    [RelayCommand]
    private async Task ExecuteSelectedCleanupAsync()
    {
        if (CurrentSnapshot is null || !CanExecuteSelected) return;

        CleanupRuleItemViewModel[] checkedItems = AllRuleItems.Where(r => r.IsChecked).ToArray();
        if (checkedItems.Length == 0) return;

        var dialog = new CleanupConfirmWindow(checkedItems);
        if (Application.Current?.MainWindow is { IsVisible: true } owner)
        {
            dialog.Owner = owner;
            dialog.Icon = owner.Icon;
        }

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string[] executedRuleIds = checkedItems.Select(i => i.RuleId).ToArray();
        CleanupScanSnapshot snapshotBeforeCleanup = CurrentSnapshot;

        HasExecutionReceipt = false;
        IsExecuting = true;
        ExecutionProgressPercent = 0;
        ExecutionProgressIndeterminate = false;
        ExecutionProgressHeader = $"正在执行所选清理（0 / {executedRuleIds.Length} 项）";
        ExecutionProgressBadgeText = "0%";
        ExecutionProgressMessage = "正在获取全局安全执行门控并核验待清理项…";
        StatusMessage = ExecutionProgressMessage;
        FooterSelectionSummary = ExecutionProgressMessage;
        ExecuteButtonText = "正在清理… 0%";

        _execCts?.Cancel();
        _execCts?.Dispose();
        _execCts = new CancellationTokenSource();

        try
        {
            var detailedProgress = new Progress<CleanupExecutionProgress>(p => DispatchToUi(() =>
            {
                ExecutionProgressIndeterminate = p.IsIndeterminate;
                ExecutionProgressPercent = p.OverallPercent;
                ExecutionProgressHeader = p.TotalRules > 0
                    ? $"正在执行所选清理（{Math.Min(p.CompletedRules + (p.PhaseText == "已完成" ? 0 : 1), p.TotalRules)} / {p.TotalRules} 项 · {p.PhaseText}）"
                    : $"正在执行所选清理（{p.PhaseText}）";
                ExecutionProgressBadgeText = p.IsIndeterminate
                    ? p.PhaseText
                    : $"{Math.Round(p.OverallPercent):0}%";
                ExecutionProgressMessage = p.Message;
                StatusMessage = p.Message;
                FooterSelectionSummary = p.Message;
                ExecuteButtonText = p.IsIndeterminate
                    ? "正在清理…"
                    : $"正在清理… {Math.Round(p.OverallPercent):0}%";
            }));

            CleanupExecutionResult result = await _cleanupService.ExecuteRuleCleanupAsync(
                snapshotBeforeCleanup,
                executedRuleIds,
                fileAction: dialog.SelectedFileAction,
                cautionConfirmationPhrase: dialog.EnteredCautionPhrase,
                detailedProgress: detailedProgress,
                ct: _execCts.Token);

            // 清理完成后：秒级定向局部复扫已执行的规则（绝不重扫全部 80 条，且不与 IsExecuting 自锁），先原地更新界面再展示回执
            DispatchToUi(() =>
            {
                ExecutionProgressIndeterminate = false;
                ExecutionProgressPercent = 98;
                ExecutionProgressBadgeText = "98%";
                ExecutionProgressHeader = "正在核验并刷新已清理项状态…";
                ExecutionProgressMessage = "正在局部复核已清理目录并同步左侧体积与右侧明细…";
                FooterSelectionSummary = ExecutionProgressMessage;
                ExecuteButtonText = "刷新状态…";
            });

            CleanupScanSnapshot updatedSnapshot = await _cleanupService.RescanRulesAndMergeSnapshotAsync(
                snapshotBeforeCleanup,
                executedRuleIds);

            DispatchToUi(() =>
            {
                ExecutionProgressPercent = 100;
                ExecutionProgressBadgeText = "100%";
                ApplyPostCleanupSnapshot(updatedSnapshot, executedRuleIds);
                _aiAssistant?.SyncCleanupScanSnapshot(updatedSnapshot);
                StatusMessage = result.SummaryMessage;
                ApplyExecutionReceipt(result);
                _overviewDirty = true;
                _quarantineDirty = true;
                SharedWorkspaceStateCache.Shared.Invalidate();
                _ = OnEnteredCleanupPageAsync(force: true);
            });
        }
        catch (Exception ex)
        {
            DispatchToUi(() =>
            {
                StatusMessage = $"清理未完成：{ex.Message}";
                ExecutionReceiptTitle = "清理执行异常";
                ExecutionReceiptMessage = ex.Message;
                ExecutionReceiptDetailText = ex.Message;
                ExecutionReceiptHasQuarantined = false;
                ExecutionReceiptIsWarning = true;
                ExecutionReceiptBg = "#FEF2F2";
                ExecutionReceiptBorder = "#FECACA";
                ExecutionReceiptFg = "#C42B1C";
                HasExecutionReceipt = true;
            });
        }
        finally
        {
            IsExecuting = false;
            DispatchToUi(RecalculateSelectionSummary);
        }
    }

    /// <summary>
    /// 清理完成后原地合并局部复扫快照：清除已执行项的旧勾选与旧明细，同步所有分类/分组/盘符汇总，
    /// 并保留用户当前未清理项的展开分组与分类上下文。
    /// </summary>
    public void ApplyPostCleanupSnapshot(CleanupScanSnapshot updatedSnapshot, IReadOnlyCollection<string> executedRuleIds)
    {
        var executedSet = new HashSet<string>(executedRuleIds, StringComparer.OrdinalIgnoreCase);

        // 保留未被本次执行的其他规则勾选态（如有），而本次执行过的规则一律取消勾选
        var preservedCheckedIds = new HashSet<string>(
            AllRuleItems.Where(r => r.IsChecked && !executedSet.Contains(r.RuleId)).Select(r => r.RuleId),
            StringComparer.OrdinalIgnoreCase);
        string? previousSelectedId = SelectedRule?.RuleId;

        CurrentSnapshot = updatedSnapshot;
        AllRuleItems.Clear();

        foreach (CleanupRuleScanResult ruleScan in updatedSnapshot.Rules)
        {
            var vm = new CleanupRuleItemViewModel(ruleScan, RecalculateSelectionSummary);
            if (preservedCheckedIds.Contains(vm.RuleId) && vm.CanCheck)
            {
                vm.IsChecked = true;
            }
            AllRuleItems.Add(vm);
        }

        CategoryHeaderText = $"清理分类（共 {FileStatsService.FormatBytes(updatedSnapshot.TotalReclaimableBytes)}）";
        UpdateCategorySubtitles(updatedSnapshot);
        RebuildDriveFilterChips();

        // 若此前选中的规则仍然在可见列表中（如仅部分清理或非本次执行项），刷新为新实例；否则自动切到下一可见项或回退右栏概览
        SelectedRule = previousSelectedId is not null
            ? AllRuleItems.FirstOrDefault(r => r.RuleId.Equals(previousSelectedId, StringComparison.OrdinalIgnoreCase))
            : null;
        if (SelectedRule is not null)
        {
            SelectedRule.IsSelected = true;
        }

        RebuildVisibleRules();
        RecalculateSelectionSummary();
    }

    /// <summary>
    /// 方案 A：生成页内顶部清理完成回执横幅（不弹窗打断连续清理，支持查看完整回执或一键进入隔离区）。
    /// </summary>
    public void ApplyExecutionReceipt(CleanupExecutionResult result)
    {
        bool hasIssues = result.WasCancelled
            || result.TotalFailedCount > 0
            || result.Items.Any(i => i.Status is CleanupItemExecutionStatus.Blocked or CleanupItemExecutionStatus.Failed);

        int succeededItems = result.Items.Count(i => i.Status is CleanupItemExecutionStatus.Quarantined
            or CleanupItemExecutionStatus.Recycled
            or CleanupItemExecutionStatus.ReclaimedByOfficialApi);

        ExecutionReceiptTitle = result.WasCancelled
            ? "清理已取消（已完成部分已保留）"
            : hasIssues
                ? (succeededItems > 0 ? "清理部分完成（含阻断或跳过项）" : "未执行清理（存在阻断或跳过项）")
                : "清理已完成";

        ExecutionReceiptMessage = result.SummaryMessage;
        ExecutionReceiptDetailText = FormatExecutionResultDetail(result);
        ExecutionReceiptHasQuarantined = result.QuarantinedBytes > 0;
        ExecutionReceiptIsWarning = hasIssues;

        if (hasIssues)
        {
            ExecutionReceiptBg = "#FFFBEB";
            ExecutionReceiptBorder = "#FDE68A";
            ExecutionReceiptFg = "#B45309";
        }
        else
        {
            ExecutionReceiptBg = "#ECFDF5";
            ExecutionReceiptBorder = "#A7F3D0";
            ExecutionReceiptFg = "#0F7B4B";
        }

        HasExecutionReceipt = true;
    }

    [RelayCommand]
    private async Task RestoreQuarantineBatchAsync(QuarantineBatchRowViewModel? row)
    {
        if (row is null || IsBusy) return;

        bool allowConflictSuffix = false;
        if (row.HasRestoreConflict)
        {
            bool confirmed = CustomMessageBox.ShowQuestion(
                $"检测到原路径已有 {row.Inspection.ConflictCount} 处同名新文件。\n\n" +
                "点击「是」将执行「对比后恢复」：完整保留当前原路径新文件，隔离区恢复项将自动附加 .restored_ 时间戳后缀放回原目录，绝不静默覆盖。\n\n是否继续？",
                "恢复冲突保护（保留双方）");
            if (!confirmed) return;
            allowConflictSuffix = true;
        }
        else
        {
            bool confirmed = CustomMessageBox.ShowQuestion(
                $"确定要将隔离批次「{row.DisplayTitle}」（{row.SizeText}）一键恢复至原路径吗？",
                "确认恢复隔离项");
            if (!confirmed) return;
        }

        IsExecuting = true;
        ExecutionProgressIndeterminate = true;
        ExecutionProgressHeader = $"正在恢复隔离项：{row.DisplayTitle}";
        ExecutionProgressMessage = $"正在将「{row.DisplayTitle}」（{row.SizeText}）原子移回原路径…";
        try
        {
            using var lease = await OperationExecutionGate.Shared
                .AcquireOrThrowAsync("QuarantineRestore", $"恢复隔离项：{row.DisplayTitle}", [row.Batch.QuarantineDirectoryPath]);

            QuarantineRestoreResult res = await _quarantineStore.RestoreBatchAsync(row.Batch, allowConflictSuffix);
            IReadOnlyList<QuarantineBatchInspection> updatedInspections = await _quarantineStore.ListAllInspectionsAsync(checkRestoreConflicts: true);
            DispatchToUi(() =>
            {
                ApplyQuarantineInspections(updatedInspections, isFullConflictInspection: true);
                _overviewDirty = true;
                SharedWorkspaceStateCache.Shared.Invalidate();
                _ = OnEnteredCleanupPageAsync(force: true);
                StatusMessage = res.Message;
                if (res.Succeeded)
                    CustomMessageBox.ShowInformation(res.Message, "恢复完成");
                else
                    CustomMessageBox.ShowWarning(res.Message + (res.Details.Length > 0 ? "\n\n" + string.Join("\n", res.Details.Take(8)) : ""), "恢复结果");
            });
        }
        catch (Exception ex)
        {
            DispatchToUi(() => CustomMessageBox.ShowWarning($"恢复失败：{ex.Message}", "恢复失败"));
        }
        finally
        {
            IsExecuting = false;
        }
    }

    [RelayCommand]
    private async Task PermanentlyDeleteQuarantineBatchAsync(QuarantineBatchRowViewModel? row)
    {
        if (row is null || IsBusy) return;

        var dialog = new QuarantineDeleteConfirmWindow(row.Batch);
        if (Application.Current?.MainWindow is { IsVisible: true } owner)
        {
            dialog.Owner = owner;
            dialog.Icon = owner.Icon;
        }

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsExecuting = true;
        ExecutionProgressIndeterminate = true;
        ExecutionProgressHeader = $"正在永久删除隔离项：{row.DisplayTitle}";
        ExecutionProgressMessage = $"正在释放「{row.DisplayTitle}」（{row.SizeText}）物理磁盘空间…";
        try
        {
            using var lease = await OperationExecutionGate.Shared
                .AcquireOrThrowAsync("QuarantinePermanentDelete", $"永久删除隔离项：{row.DisplayTitle}", [row.Batch.QuarantineDirectoryPath]);

            QuarantineDeleteResult res = await _quarantineStore.PermanentlyDeleteBatchAsync(
                row.Batch,
                dialog.EnteredPhrase,
                QuarantineStore.DeleteConfirmationPhrase);
            IReadOnlyList<QuarantineBatchInspection> updatedInspections = await _quarantineStore.ListAllInspectionsAsync(checkRestoreConflicts: true);

            DispatchToUi(() =>
            {
                ApplyQuarantineInspections(updatedInspections, isFullConflictInspection: true);
                _overviewDirty = true;
                SharedWorkspaceStateCache.Shared.Invalidate();
                _ = OnEnteredCleanupPageAsync(force: true);
                StatusMessage = res.Message;
                if (res.FullyDeleted)
                    CustomMessageBox.ShowInformation(res.Message, "永久删除完成");
                else
                    CustomMessageBox.ShowWarning(res.Message + (res.Details.Length > 0 ? "\n\n" + string.Join("\n", res.Details.Take(8)) : ""), "永久删除结果");
            });
        }
        catch (Exception ex)
        {
            DispatchToUi(() => CustomMessageBox.ShowWarning($"永久删除失败：{ex.Message}", "操作失败"));
        }
        finally
        {
            IsExecuting = false;
        }
    }

    [RelayCommand]
    private void ToggleSelectAllQuarantineBatches()
    {
        if (QuarantineBatches.Count == 0 || IsBusy) return;

        bool targetChecked = AllQuarantineSelectedState != true;
        _isBatchUpdatingQuarantineSelection = true;
        try
        {
            foreach (QuarantineBatchRowViewModel row in QuarantineBatches)
            {
                if (row.IsChecked != targetChecked)
                {
                    row.IsChecked = targetChecked;
                }
            }
        }
        finally
        {
            _isBatchUpdatingQuarantineSelection = false;
        }

        RecalculateQuarantineSelectionSummary();
    }

    [RelayCommand]
    private async Task BatchPermanentlyDeleteQuarantineAsync()
    {
        if (!CanBatchDeleteQuarantine) return;

        QuarantineBatchRecord[] selectedBatches = QuarantineBatches
            .Where(b => b.IsChecked)
            .Select(b => b.Batch)
            .ToArray();
        if (selectedBatches.Length == 0) return;

        var dialog = new QuarantineDeleteConfirmWindow(selectedBatches);
        if (Application.Current?.MainWindow is { IsVisible: true } owner)
        {
            dialog.Owner = owner;
            dialog.Icon = owner.Icon;
        }

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var summary = await ExecuteBatchPermanentDeleteCoreAsync(selectedBatches, dialog.EnteredPhrase);
            await DispatchToUiAsync(() =>
            {
                if (summary.AllSucceeded && !summary.WasCancelled)
                {
                    CustomMessageBox.ShowInformation(summary.SummaryMessage, "批量永久删除完成");
                }
                else
                {
                    string detailBody = summary.Details.Length > 0
                        ? summary.SummaryMessage + "\n\n" + string.Join("\n", summary.Details.Take(10))
                        : summary.SummaryMessage;
                    CustomMessageBox.ShowWarning(detailBody, summary.WasCancelled ? "批量永久删除已中止" : "批量永久删除结果");
                }
            });
        }
        catch (Exception ex)
        {
            await DispatchToUiAsync(() => CustomMessageBox.ShowWarning($"批量永久删除失败：{ex.Message}", "操作失败"));
        }
    }

    /// <summary>
    /// 在单一排他租约下后台逐批永久删除所选隔离区批次（支持中途取消、实时进度与异步回刷，供 UI 与单元测试共用）。
    /// </summary>
    public async Task<(bool AllSucceeded, bool WasCancelled, int FullyDeletedBatches, int PartialOrFailedBatches, long FreedBytes, string SummaryMessage, string[] Details)>
        ExecuteBatchPermanentDeleteCoreAsync(
            IReadOnlyList<QuarantineBatchRecord> batches,
            string enteredPhrase,
            CancellationToken externalCt = default)
    {
        ArgumentNullException.ThrowIfNull(batches);
        if (batches.Count == 0)
        {
            return (true, false, 0, 0, 0, "未选择任何待删除的隔离批次。", []);
        }

        string normalizedPhrase = enteredPhrase?.Trim() ?? string.Empty;
        if (!string.Equals(normalizedPhrase, QuarantineStore.DeleteConfirmationPhrase, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"确认短语不匹配：请输入「{QuarantineStore.DeleteConfirmationPhrase}」以授权批量永久删除。");
        }

        string[] lockPaths = batches
            .Select(b => b.QuarantineDirectoryPath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _execCts?.Cancel();
        _execCts?.Dispose();
        _execCts = externalCt.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(externalCt)
            : new CancellationTokenSource();
        CancellationToken ct = _execCts.Token;

        await DispatchToUiAsync(() =>
        {
            IsExecuting = true;
            ExecutionProgressIndeterminate = false;
            ExecutionProgressPercent = 0;
            ExecutionProgressHeader = $"正在批量永久删除隔离项（0 / {batches.Count} 批）";
            ExecutionProgressMessage = "正在获取全局安全执行门控并准备逐批释放空间…";
        });

        int fullyDeletedBatches = 0;
        int partialOrFailedBatches = 0;
        long totalFreedBytes = 0;
        bool cancelled = false;
        var details = new List<string>();

        try
        {
            using var lease = await OperationExecutionGate.Shared
                .AcquireOrThrowAsync("QuarantineBatchPermanentDelete", $"批量永久删除隔离项（{batches.Count} 批）", lockPaths, ct)
                .ConfigureAwait(false);

            for (int i = 0; i < batches.Count; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                QuarantineBatchRecord batch = batches[i];
                int step = i + 1;
                double startPct = i * 100.0 / batches.Count;
                await DispatchToUiAsync(() =>
                {
                    ExecutionProgressIndeterminate = false;
                    ExecutionProgressPercent = startPct;
                    ExecutionProgressHeader = $"正在批量永久删除隔离项（{step} / {batches.Count} 批）";
                    ExecutionProgressMessage = $"正在释放 ({step}/{batches.Count})：{batch.DisplayTitle}（{FileStatsService.FormatBytes(batch.TotalBytes)}）…";
                }).ConfigureAwait(false);

                try
                {
                    QuarantineDeleteResult res = await _quarantineStore
                        .PermanentlyDeleteBatchAsync(batch, normalizedPhrase, QuarantineStore.DeleteConfirmationPhrase, ct)
                        .ConfigureAwait(false);

                    totalFreedBytes += res.FreedBytesEstimate;
                    if (res.FullyDeleted)
                    {
                        fullyDeletedBatches++;
                    }
                    else
                    {
                        partialOrFailedBatches++;
                        details.Add($"• {batch.DisplayTitle}：{res.Message}");
                        foreach (string d in res.Details.Take(3))
                        {
                            details.Add($"  - {d}");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    details.Add($"• 已取消：停止删除「{batch.DisplayTitle}」及后续批次（未删除部分保留在隔离区）。");
                    break;
                }
                catch (Exception ex)
                {
                    partialOrFailedBatches++;
                    details.Add($"• {batch.DisplayTitle} 删除异常：{ex.Message}");
                }

                double donePct = step * 100.0 / batches.Count;
                await DispatchToUiAsync(() => ExecutionProgressPercent = donePct).ConfigureAwait(false);
            }

            IReadOnlyList<QuarantineBatchInspection> updatedInspections = await _quarantineStore
                .ListAllInspectionsAsync(checkRestoreConflicts: true)
                .ConfigureAwait(false);

            int skippedCount = Math.Max(0, batches.Count - fullyDeletedBatches - partialOrFailedBatches);
            string summaryMessage = cancelled
                ? $"批量永久删除已中止：已完成 {fullyDeletedBatches} 批（预计释放 {FileStatsService.FormatBytes(totalFreedBytes)}），未删 {skippedCount + partialOrFailedBatches} 批保留在隔离区。"
                : partialOrFailedBatches == 0
                    ? $"已批量永久删除全部 {fullyDeletedBatches} 批隔离项（预计释放 {FileStatsService.FormatBytes(totalFreedBytes)}）。"
                    : $"批量永久删除部分完成：成功释放 {fullyDeletedBatches} 批（预计释放 {FileStatsService.FormatBytes(totalFreedBytes)}），仍有 {partialOrFailedBatches} 批存在未删除文件并保留在隔离区。";

            await DispatchToUiAsync(() =>
            {
                ExecutionProgressPercent = 100;
                ApplyQuarantineInspections(updatedInspections, isFullConflictInspection: true);
                _overviewDirty = true;
                _ = OnEnteredCleanupPageAsync(force: true);
                StatusMessage = summaryMessage;
            }).ConfigureAwait(false);

            return (partialOrFailedBatches == 0 && !cancelled, cancelled, fullyDeletedBatches, partialOrFailedBatches, totalFreedBytes, summaryMessage, details.ToArray());
        }
        finally
        {
            await DispatchToUiAsync(() => IsExecuting = false).ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private void OpenDirectoryInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            string target = Directory.Exists(path) ? path : (Path.GetDirectoryName(path) ?? path);
            if (!Directory.Exists(target))
            {
                CustomMessageBox.ShowWarning($"目录已不存在：{target}", "无法打开目录");
                return;
            }
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            CustomMessageBox.ShowWarning($"无法打开资源管理器：{ex.Message}", "提示");
        }
    }
}
