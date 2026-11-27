using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MigrationCore.Models;
using MigrationCore.Models.Safety;
using MigrationCore.Models.Scanning;
using MigrationCore.Services;
using MigrationCore.Services.Safety;
using MigrationCore.Services.Scanning;
using MoveWithSymlinkWPF.Helpers;
using MoveWithSymlinkWPF.Views;

namespace MoveWithSymlinkWPF.ViewModels;

public partial class DiskScanViewModel
{
    [RelayCommand]
    private void AddSelectedToPlan()
    {
        if (_snapshot is null || SelectedRow is null || SelectedDecision is null)
            return;

        if (IsSelectedAlreadyInPlan)
        {
            var existing = PlanItems.FirstOrDefault(p => p.SourcePath.Equals(SelectedRow.FullPath, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && !IsBatchExecuting)
            {
                PlanItems.Remove(existing);
                InvalidateFrozenPlan();
                UpdateCanAddSelected();
            }
            return;
        }

        if (!CanAddSelectedToPlan) return;

        if (!_snapshot.CanUseForMigrationPlanning)
        {
            CustomMessageBox.ShowWarning("当前扫描快照处于已取消或访问受限状态，不能直接用于生成迁移清单，请先完成完整扫描。", "无法加入清单");
            return;
        }

        SubtreeHazardReport hazards = _snapshot.AnalyzeSubtreeHazards(SelectedRow.NodeId, maxExternalSamples: 2);
        if (hazards.HasBlockingHardLinks)
        {
            string peerInfo = hazards.SampleExternalPeerPaths.Length > 0
                ? $"\n\n外部关联路径示例：\n• {string.Join("\n• ", hazards.SampleExternalPeerPaths)}"
                : string.Empty;
            string detail = hazards.HardLinkKind == SubtreeHardLinkKind.ExternalLinked
                ? $"该目录包含 {hazards.ExternalHardLinkFileCount} 个指向目录外部的硬链接文件；跨卷迁移会断开共享语义且无法释放源盘对应空间，禁止直接迁移。建议改用软件原生路径设置。{peerInfo}"
                : "该目录包含未能验证归属闭环的硬链接文件，为防止跨卷复制破坏共享语义，不允许加入迁移清单。";
            CustomMessageBox.ShowWarning(detail, "跨外部硬链接拦截");
            return;
        }
        if (hazards.HasIncompleteNodes)
        {
            CustomMessageBox.ShowWarning("该目录子树在扫描中存在无法读取或损坏节点，禁止加入批量清单。", "扫描不完整拦截");
            return;
        }

        string targetPath = string.Empty;
        if (!string.IsNullOrWhiteSpace(UnifiedTargetRoot))
        {
            try
            {
                targetPath = MigrationPlanService.BuildDefaultTargetPath(
                    SelectedRow.FullPath,
                    UnifiedTargetRoot,
                    SelectedDecision.MatchedApplicationId);
            }
            catch { }
        }

        PlanItems.Add(new BatchPlanRowItem
        {
            NodeId = SelectedRow.NodeId,
            ScanId = _snapshot.ScanId,
            SourcePath = SelectedRow.FullPath,
            TargetPath = targetPath,
            ApplicationId = SelectedDecision.MatchedApplicationId,
            ApplicationLabel = SelectedDecision.MatchedApplicationName ?? SelectedRow.AttributionLabel,
            LogicalBytes = SelectedRow.LogicalBytes,
            AllocatedBytes = SelectedRow.UniqueAllocatedBytes,
            IsCompressedOrSparse = (SelectedRow.Flags & (ScanNodeFlags.IsSparseOrCompressed | ScanNodeFlags.IsWofCompressed)) != 0,
            HasSubtreeHardLinks = hazards.HasBlockingHardLinks,
            HasInternalHardLinks = hazards.HasInternalOnlyHardLinks,
            HasIncompleteScanData = hazards.HasIncompleteNodes,
            ExpectedFileCount = SelectedRow.FileCount,
            HasReparseDescendants = hazards.HasReparseDescendants,
            Level = SelectedDecision.Level,
            L3RiskAcknowledged = SelectedDecision.Level == MigrationAdmissionLevel.ManualRiskConfirm && IsL3RiskConfirmedForSelection,
            StatusText = hazards.HasInternalOnlyHardLinks ? "待预检（无损重建内部硬链接）" : "待预检"
        });

        InvalidateFrozenPlan();
        UpdateCanAddSelected();
    }

    [RelayCommand]
    private void RemovePlanItem(BatchPlanRowItem? item)
    {
        if (item is null || IsBatchExecuting) return;
        PlanItems.Remove(item);
        InvalidateFrozenPlan();
        UpdateCanAddSelected();
    }

    [RelayCommand]
    private void ClearPlan()
    {
        if (IsBatchExecuting) return;
        PlanItems.Clear();
        InvalidateFrozenPlan();
        UpdateCanAddSelected();
    }

    [RelayCommand]
    private void OpenSelectedInExplorer()
    {
        if (SelectedRow is null || !SelectedRow.CanOpenInExplorer) return;
        ExplorerHelper.OpenInExplorer(SelectedRow.FullPath);
    }

    [RelayCommand]
    private void OpenUnifiedTargetInExplorer()
    {
        if (string.IsNullOrWhiteSpace(UnifiedTargetRoot)) return;
        ExplorerHelper.OpenInExplorer(UnifiedTargetRoot, fallbackToExistingAncestor: true);
    }

    [RelayCommand]
    private async Task FreezeAndPreflightAsync()
    {
        if (IsBatchExecuting) return;
        if (PlanItems.Count == 0)
        {
            PreflightSummary = "迁移清单为空，请先返回目录树勾选允许迁移的目录。";
            HasPreflightBlocking = false;
            CanStartBatch = false;
            return;
        }

        var drafts = PlanItems.Select(p => new MigrationPlanDraftItem(
            SourcePath: p.SourcePath,
            TargetPath: string.IsNullOrWhiteSpace(p.TargetPath) ? null : p.TargetPath,
            LogicalBytes: p.LogicalBytes,
            AllocatedBytes: p.AllocatedBytes,
            IsCompressedOrSparse: p.IsCompressedOrSparse,
            HasSubtreeHardLinks: p.HasSubtreeHardLinks,
            HasIncompleteScanData: p.HasIncompleteScanData,
            L3RiskAcknowledged: p.L3RiskAcknowledged,
            ScanId: p.ScanId,
            NodeId: p.NodeId,
            Source: p.Source,
            HasInternalHardLinks: p.HasInternalHardLinks,
            ExpectedFileCount: p.ExpectedFileCount,
            HasReparseDescendants: p.HasReparseDescendants)).ToArray();

        MigrationTransactionRecord[] existing = [];
        try { existing = await new MigrationTransactionStore().ReadAllAsync(); }
        catch { }

        VerificationMode mode = UseContentHash ? VerificationMode.ContentHash : VerificationMode.Basic;
        FrozenMigrationPlan frozen = _planService.FreezeAndValidatePlan(
            drafts,
            mode,
            UnifiedTargetRoot,
            checkFilesystem: true,
            existingTransactions: existing);

        BatchPreflightReport report = _planService.RunReadOnlyProcessPreflight(frozen);
        _frozenPlan = frozen;

        int blockedCount = 0;
        for (int i = 0; i < PlanItems.Count; i++)
        {
            var matched = frozen.Items.FirstOrDefault(x => x.SourcePath.Equals(PlanItems[i].SourcePath, StringComparison.OrdinalIgnoreCase));
            if (matched is not null)
            {
                PlanItems[i].TargetPath = matched.TargetPath;
                PlanItems[i].ExecutionStatus = matched.Status;
                PlanItems[i].StatusText = matched.Status == BatchPlanItemStatus.Ready ? "预检通过" : "存在阻断";
                PlanItems[i].BlockingReason = matched.BlockingReason ?? string.Empty;
                if (matched.Status != BatchPlanItemStatus.Ready) blockedCount++;
            }
        }

        UpdateTargetSpaceHint();
        CanStartBatch = report.CanStart;
        HasPreflightBlocking = !report.CanStart;
        long totalBytes = PlanItems.Sum(p => Math.Max(p.AllocatedBytes, p.LogicalBytes));
        PreflightSummary = report.CanStart
            ? $"全部 {frozen.Items.Length} 项（共 {FileStatsService.FormatBytes(totalBytes)}）已通过预检（核对模式：{(UseContentHash ? "完整 SHA-256" : "快速核对")}），可开始串行迁移。"
            : $"{Math.Max(1, blockedCount)} 项存在预检阻断（共 {frozen.Items.Length} 项 · {FileStatsService.FormatBytes(totalBytes)}）：{string.Join("；", report.BlockingIssues.Take(2))}";
    }

    [RelayCommand]
    private async Task ExecuteBatchAsync()
    {
        if (IsBatchExecuting || _frozenPlan is null || !CanStartBatch) return;
        if (OperationExecutionGate.Shared.IsBusy)
        {
            CustomMessageBox.ShowWarning("当前有其他清理、迁移或备份操作正在执行，请等待完成后再启动批量迁移。", "操作互斥");
            return;
        }

        bool confirmed = CustomMessageBox.ShowQuestion(
            $"确认按冻结计划 #{_frozenPlan.PlanRevision} 串行迁移 {_frozenPlan.Items.Length} 个目录吗？\n\n" +
            $"• 核对模式：{(UseContentHash ? "完整内容 SHA-256 哈希核对（较耗时）" : "默认快速核对（相对路径 + 大小 + 元数据）")}\n" +
            "• 每项迁移都会保留原盘备份，不会自动删除原件；\n" +
            "• 任一单项失败会立即暂停后续队列并保留现场证据。",
            "确认开始批量安全迁移");

        if (!confirmed) return;

        ActiveSubView = DiskScanSubView.BatchExecution;
        IsBatchExecuting = true;
        IsBatchFinished = false;
        ExecutionHeaderTitle = "正在执行批量安全迁移";
        ExecutionHeaderSubtitle = $"串行复用手动迁移底座（{ActiveVerifyModeText}）；完成后原目录将在源盘改名保留，绝不自动删除。";
        BatchLogs.Clear();

        foreach (var row in PlanItems)
        {
            row.ExecutionStatus = BatchPlanItemStatus.Ready;
            row.StatusText = "排队等待中";
            row.BlockingReason = string.Empty;
        }

        _batchCts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<BatchItemProgress>(p =>
            {
                ExecutionQueueBadgeText = $"{p.ItemIndex + 1} / {p.TotalItems} 进行中";
                var row = PlanItems.FirstOrDefault(r => r.SourcePath.Equals(p.SourcePath, StringComparison.OrdinalIgnoreCase));
                if (row is not null)
                {
                    row.ExecutionStatus = p.ItemStatus;
                    ActiveItemTitle = row.DisplayTitle;
                    ActiveItemSourcePath = row.SourcePath;
                    ActiveItemTargetPath = row.TargetPath;

                    double pct = p.InnerProgress?.PercentComplete
                        ?? (p.ItemStatus == BatchPlanItemStatus.CompletedAwaitingVerification ? 100 : 0);
                    ActiveItemPercent = Math.Clamp(pct, 0, 100);
                    ActivePercentText = $"{ActiveItemPercent:F0}%";

                    row.StatusText = p.ItemStatus switch
                    {
                        BatchPlanItemStatus.Running => $"正在迁移 · {ActiveItemPercent:F0}%",
                        BatchPlanItemStatus.CompletedAwaitingVerification => "已完成 · 待用户验证",
                        BatchPlanItemStatus.Failed => "失败（队列已暂停）",
                        BatchPlanItemStatus.Cancelled => "已取消",
                        _ => "排队等待中"
                    };
                }

                UpdateExecutionMetricsAndSteps(p);
            });

            var logProgress = new Progress<string>(msg =>
            {
                Application.Current?.Dispatcher.BeginInvoke(() => BatchLogs.Add($"[{DateTime.Now:HH:mm:ss}] {msg}"));
            });

            BatchMigrationSummary summary = await _batchService.ExecuteBatchAsync(
                _frozenPlan,
                robocopyThreads: 8,
                largeFileThresholdMB: 1024,
                progress,
                logProgress,
                _batchCts.Token);

            foreach (MigrationPlanItem finalItem in summary.FinalItems)
            {
                var row = PlanItems.FirstOrDefault(r => r.SourcePath.Equals(finalItem.SourcePath, StringComparison.OrdinalIgnoreCase));
                if (row is not null)
                {
                    row.ExecutionStatus = finalItem.Status;
                    row.BlockingReason = finalItem.BlockingReason ?? string.Empty;
                    row.StatusText = finalItem.Status switch
                    {
                        BatchPlanItemStatus.CompletedAwaitingVerification => "已完成 · 待用户验证",
                        BatchPlanItemStatus.Failed => "失败（队列已暂停）",
                        BatchPlanItemStatus.Cancelled => "已取消",
                        _ => row.StatusText
                    };
                }
            }

            IsBatchFinished = true;
            ExecutionQueueBadgeText = $"{summary.SucceededCount} / {summary.FinalItems.Length} 已完成";
            ExecutionHeaderTitle = summary.PausedDueToFailure ? "批量迁移已暂停（检测到失败项）" : "批量安全迁移已完成（待用户验证）";
            ExecutionHeaderSubtitle = summary.PausedDueToFailure
                ? (summary.PauseReason ?? "单项失败已自动暂停后续队列并保留现场证据。")
                : $"已完成 {summary.SucceededCount} 项技术迁移，全部原始备份已保留在源盘；请验证软件正常后前往「备份管理」处理。";

            if (Application.Current?.MainWindow?.DataContext is MainViewModel main)
            {
                await main.RefreshGlobalStatusAsync();
            }

            if (summary.PausedDueToFailure)
            {
                CustomMessageBox.ShowWarning(
                    $"{summary.PauseReason}\n\n已成功的 {summary.SucceededCount} 项原始备份均已保留，请前往顶栏「备份管理」验证或核查。",
                    "批量队列已暂停");
            }
            else if (summary.SucceededCount > 0)
            {
                CustomMessageBox.ShowSuccess(
                    $"已完成 {summary.SucceededCount} 项技术迁移！\n\n原始备份均已保留在源盘（不会释放原盘空间，也不会自动删除）。\n请先启动软件验证功能正常，再前往顶栏「备份管理」经两次确认后处理备份。",
                    "批量迁移完成（待用户验证）");
            }
        }
        catch (Exception ex)
        {
            IsBatchFinished = true;
            ExecutionHeaderTitle = "批量迁移异常中止";
            ExecutionHeaderSubtitle = ex.Message;
            CustomMessageBox.ShowError($"批量迁移失败：\n{ex.Message}", "执行错误");
        }
        finally
        {
            IsBatchExecuting = false;
            InvalidateFrozenPlan();
            _batchCts?.Dispose();
            _batchCts = null;
        }
    }

    [RelayCommand]
    private void CancelBatch()
    {
        _batchCts?.Cancel();
        ActiveStageBadgeText = "正在安全停止…";
    }

    private void ApplySnapshot(ScanSnapshot snapshot, string volumeTitle, double elapsedSec) =>
        ApplySnapshotCore(snapshot, volumeTitle, elapsedSec, cacheLookup: null);

    private void ApplySnapshotCore(ScanSnapshot snapshot, string volumeTitle, double elapsedSec, VolumeSnapshotLookup? cacheLookup)
    {
        _snapshot = snapshot;
        _hotspotPathKeptNodes = null;
        _hotspotPathAncestorNodes = null;
        IsHotspotPathTreeExpanded = false;
        HasSnapshot = true;
        SnapshotVolumeTitle = volumeTitle;
        IsSnapshotMftBackend = snapshot.Backend == ScanBackendKind.RawNtfsMft;
        SnapshotModeBadge = IsSnapshotMftBackend
            ? $"MFT · {elapsedSec:F1} s"
            : $"目录遍历降级 · {elapsedSec:F1} s";
        SnapshotVolumeSummary = $"已用 {FileStatsService.FormatBytes(snapshot.Volume.UsedBytes)} / {FileStatsService.FormatBytes(snapshot.Volume.TotalBytes)} · 剩余 {FileStatsService.FormatBytes(snapshot.Volume.FreeBytes)}";
        UsnStatusNote = snapshot.Backend == ScanBackendKind.DirectoryWalkFallback
            ? (snapshot.FallbackReason ?? "降级遍历模式，执行迁移前将逐项实时核验")
            : snapshot.RequiresLiveReverification
                ? "活跃卷或 USN 水位变化，执行前将强制实时复核"
                : $"USN 水位稳定（{snapshot.UsnStartNextUsn}）";
        IsSnapshotStale = cacheLookup?.IsStale == true;
        SnapshotStaleMessage = cacheLookup?.StaleReason ?? string.Empty;
        BreadcrumbText = $"此电脑  ›  {snapshot.Volume.DriveRoot.TrimEnd('\\')}";

        // 同步选中卡片，确保顶部「重新扫描」总是重扫当前查看的盘
        VolumeCardItem? matchingCard = VolumeCards.FirstOrDefault(v =>
            v.DriveRoot.Equals(snapshot.Volume.DriveRoot, StringComparison.OrdinalIgnoreCase));
        if (matchingCard is not null)
        {
            SelectedVolumeCard = matchingCard;
        }

        SelectedRow = null;
        RebuildVisibleRows();
        SnapshotChanged?.Invoke(this, snapshot);
    }

    [RelayCommand]
    private void ToggleAiDrawer()
    {
        IsAiDrawerOpen = !IsAiDrawerOpen;
    }

    /// <summary>
    /// 接收用户人工审核并采纳的 AI 迁移建议（仅加入待迁移清单，绝不自动启动执行，仍须全量策略与子树复核）。
    /// </summary>
    public (bool Added, string Message) TryAddAiProposalToPlan(Guid scanId, int nodeId, string? suggestedTargetPath)
    {
        if (IsBatchExecuting)
        {
            return (false, "当前正在执行批量迁移，请等待完成后再修改清单。");
        }

        if (_snapshot is null || _snapshot.ScanId != scanId)
        {
            return (false, "当前磁盘扫描快照已变更或已释放，请重新扫描后再审阅 AI 建议。");
        }

        if (!_snapshot.CanUseForMigrationPlanning)
        {
            return (false, "当前扫描快照处于已取消或访问受限状态，不能用于生成迁移清单。");
        }

        if ((uint)nodeId >= (uint)_snapshot.Nodes.Length || !_snapshot.Nodes[nodeId].IsDirectory)
        {
            return (false, "建议指向的目录节点在当前快照中无效。");
        }

        ref readonly ScanNodeRecord node = ref _snapshot.Nodes[nodeId];
        string fullPath = _snapshot.GetFullPath(nodeId);

        if (PlanItems.Any(p => p.SourcePath.Equals(fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            return (true, $"「{fullPath}」已在待迁移清单中。");
        }

        SafetyPolicyDecision decision = MigrationSafetyPolicy.EvaluateSource(
            fullPath, _catalog, SafetyEvaluationContext.ScanPreview, checkFilesystem: false);

        if (decision.IsHardDenied)
        {
            return (false, $"安全策略硬拦截（L0）：{decision.Summary}");
        }

        if (decision.Level == MigrationAdmissionLevel.VendorNativeRecommended)
        {
            return (false, "该目录属于 L2 厂商原生引导项，不建议符号链接迁移，请使用官方原生方案。");
        }

        SubtreeHazardReport hazards = _snapshot.AnalyzeSubtreeHazards(nodeId, maxExternalSamples: 2);
        if (hazards.HasBlockingHardLinks)
        {
            string peerHint = hazards.SampleExternalPeerPaths.Length > 0
                ? $"（外部关联：{string.Join("；", hazards.SampleExternalPeerPaths)}）"
                : string.Empty;
            return (false, $"该目录子树包含指向目录外部的硬链接文件{peerHint}，为防止破坏共享语义与释放空间落空，禁止加入迁移清单。");
        }
        if (hazards.HasIncompleteNodes)
        {
            return (false, "该目录子树扫描不完整或存在损坏节点，禁止加入迁移清单。");
        }

        string targetPath = string.Empty;
        if (!string.IsNullOrWhiteSpace(suggestedTargetPath))
        {
            targetPath = suggestedTargetPath;
        }
        else if (!string.IsNullOrWhiteSpace(UnifiedTargetRoot))
        {
            try
            {
                targetPath = MigrationPlanService.BuildDefaultTargetPath(
                    fullPath, UnifiedTargetRoot, decision.MatchedApplicationId);
            }
            catch { }
        }

        PlanItems.Add(new BatchPlanRowItem
        {
            NodeId = nodeId,
            ScanId = _snapshot.ScanId,
            SourcePath = fullPath,
            TargetPath = targetPath,
            ApplicationId = decision.MatchedApplicationId,
            ApplicationLabel = decision.MatchedApplicationName ?? string.Empty,
            LogicalBytes = node.LogicalBytes,
            AllocatedBytes = node.UniqueAllocatedBytes,
            IsCompressedOrSparse = (node.Flags & (ScanNodeFlags.IsSparseOrCompressed | ScanNodeFlags.IsWofCompressed)) != 0,
            HasSubtreeHardLinks = hazards.HasBlockingHardLinks,
            HasInternalHardLinks = hazards.HasInternalOnlyHardLinks,
            HasIncompleteScanData = hazards.HasIncompleteNodes,
            ExpectedFileCount = node.FileCount,
            HasReparseDescendants = hazards.HasReparseDescendants,
            Level = decision.Level,
            Source = BatchPlanItemSource.AiProposal,
            // L3 项严禁替用户自动勾选确认，必须由用户在清单页人工核对后勾选
            L3RiskAcknowledged = false,
            StatusText = decision.Level == MigrationAdmissionLevel.ManualRiskConfirm
                ? (hazards.HasInternalOnlyHardLinks ? "待预检（AI 建议 · 内部硬链接无损重建 · 需人工确认 L3）" : "待预检（AI 建议 · 需人工确认 L3）")
                : (hazards.HasInternalOnlyHardLinks ? "待预检（AI 建议 · 内部硬链接无损重建）" : "待预检（AI 建议）")
        });

        foreach (ScanTreeRowItem row in VisibleTreeRows)
        {
            if (row.NodeId == nodeId)
            {
                row.IsInPlan = true;
            }
        }

        InvalidateFrozenPlan();
        UpdateCanAddSelected();

        return (true, $"已将「{Path.GetFileName(fullPath.TrimEnd('\\'))}」加入待迁移清单（未执行，请前往清单页核对目标并手动启动）。");
    }

    /// <summary>
    /// 在目录树中自动展开祖先路径并选中指定节点（供 AI 诊断卡片一键核实）。
    /// </summary>
    public bool TryLocateNodeInTree(Guid scanId, int nodeId)
    {
        if (_snapshot is null || _snapshot.ScanId != scanId) return false;
        if ((uint)nodeId >= (uint)_snapshot.Nodes.Length) return false;
        if (IsHiddenSystemVolumeNode(_snapshot, nodeId)) return false;

        ActiveSubView = DiskScanSubView.DirectoryTree;
        IsAiDrawerOpen = false;
        if (!string.IsNullOrEmpty(SearchFilter))
        {
            SearchFilter = string.Empty;
        }
        if (BrowseMode != ScanBrowseMode.DirectoryTree)
        {
            BrowseMode = ScanBrowseMode.DirectoryTree;
        }
        else
        {
            RebuildVisibleRows();
        }

        var ancestorChain = new Stack<int>();
        int cur = nodeId;
        int guard = 0;
        while (cur >= 0 && cur != _snapshot.RootNodeId && guard++ < 64)
        {
            ancestorChain.Push(cur);
            int parent = _snapshot.Nodes[cur].ParentNodeId;
            if (parent == cur) break;
            cur = parent;
        }

        ScanTreeRowItem? located = null;
        while (ancestorChain.Count > 0)
        {
            int nextId = ancestorChain.Pop();
            ScanTreeRowItem? row = VisibleTreeRows.FirstOrDefault(r => !r.IsLoadMorePlaceholder && r.NodeId == nextId);
            if (row is null) break;

            located = row;
            if (ancestorChain.Count > 0 && !row.IsExpanded && row.HasChildren)
            {
                ToggleExpand(row);
            }
        }

        if (located is not null)
        {
            SelectedRow = located;
            return true;
        }

        return false;
    }

    private (int LargeDirCount, int L1Count, long L1Bytes) SummarizeSnapshotHighlights(ScanSnapshot snapshot)
    {
        const long OneGb = 1024L * 1024 * 1024;
        int l1Count = 0;
        long l1Bytes = 0;

        int[] hotspots = CollectDisjointHotspots(snapshot, minAllocatedBytes: OneGb, maxCount: 120);
        int largeDirs = hotspots.Length;
        foreach (int nodeId in hotspots)
        {
            string fullPath = snapshot.GetFullPath(nodeId);
            SafetyPolicyDecision d = MigrationSafetyPolicy.EvaluateSource(fullPath, _catalog, SafetyEvaluationContext.ScanPreview, checkFilesystem: false);
            if (d.Level == MigrationAdmissionLevel.VerifiedEligible)
            {
                l1Count++;
                l1Bytes += snapshot.Nodes[nodeId].UniqueAllocatedBytes;
            }
        }
        return (largeDirs, l1Count, l1Bytes);
    }

    /// <summary>
    /// 采集互不嵌套的大软件/热点目录（穿透 L0 容器目录与单子项包装层，保证任意两项无父子包含关系，绝不重复计算空间）。
    /// </summary>
    private int[] CollectDisjointHotspots(ScanSnapshot snapshot, long minAllocatedBytes, int maxCount) =>
        ScanHotspotAnalyzer.CollectDisjointHotspots(
            snapshot,
            _catalog,
            minAllocatedBytes,
            maxCount,
            HotspotTraversalOptions.DiskScanUi);

    private void UpdateExecutionMetricsAndSteps(BatchItemProgress p)
    {
        MigrationProgress? inner = p.InnerProgress;
        if (inner is not null)
        {
            ActiveCopiedText = inner.TotalBytes > 0
                ? $"{FileStatsService.FormatBytes(inner.CopiedBytes)} / {FileStatsService.FormatBytes(inner.TotalBytes)}"
                : FileStatsService.FormatBytes(inner.CopiedBytes);
            ActiveSpeedText = inner.SpeedBytesPerSecond > 0
                ? $"{FileStatsService.FormatBytes((long)inner.SpeedBytesPerSecond)}/s"
                : "—";
            ActiveEtaText = inner.EstimatedTimeRemaining is TimeSpan eta && eta.TotalSeconds > 0
                ? (eta.TotalMinutes >= 1 ? $"约 {(int)eta.TotalMinutes} 分 {eta.Seconds} 秒" : $"约 {eta.Seconds} 秒")
                : "—";

            int phase = inner.CurrentPhase;
            if (phase <= 1)
            {
                ActiveStageBadgeText = "阶段 1 / 4 · 安全复核";
                ExecStep1State = 1; ExecStep1Note = "复核中…";
                ExecStep2State = 0; ExecStep2Note = "等待复核通过";
                ExecStep3State = 0; ExecStep3Note = "等待复制完成";
                ExecStep4State = 0; ExecStep4Note = "等待核对通过";
            }
            else if (phase == 2)
            {
                ActiveStageBadgeText = "阶段 2 / 4 · 复制文件";
                ExecStep1State = 2; ExecStep1Note = "已通过";
                ExecStep2State = 1; ExecStep2Note = "进行中 · 8 线程";
                ExecStep3State = 0; ExecStep3Note = "等待复制完成";
                ExecStep4State = 0; ExecStep4Note = "等待核对通过";
            }
            else if (phase == 3)
            {
                ActiveStageBadgeText = "阶段 3 / 4 · 完整性核对";
                ExecStep1State = 2; ExecStep1Note = "已通过";
                ExecStep2State = 2; ExecStep2Note = "复制完成";
                ExecStep3State = 1; ExecStep3Note = UseContentHash ? "SHA-256 核对中…" : "快速核对中…";
                ExecStep4State = 0; ExecStep4Note = "等待核对通过";
            }
            else
            {
                ActiveStageBadgeText = "阶段 4 / 4 · 改名保留与建链";
                ExecStep1State = 2; ExecStep1Note = "已通过";
                ExecStep2State = 2; ExecStep2Note = "复制完成";
                ExecStep3State = 2; ExecStep3Note = "核对通过";
                ExecStep4State = p.ItemStatus == BatchPlanItemStatus.CompletedAwaitingVerification ? 2 : 1;
                ExecStep4Note = p.ItemStatus == BatchPlanItemStatus.CompletedAwaitingVerification ? "已完成（备份保留）" : "建链与校验中…";
            }
        }
        else if (p.ItemStatus == BatchPlanItemStatus.CompletedAwaitingVerification)
        {
            ActiveStageBadgeText = "已完成 · 备份已保留";
            ExecStep1State = 2; ExecStep1Note = "已通过";
            ExecStep2State = 2; ExecStep2Note = "复制完成";
            ExecStep3State = 2; ExecStep3Note = "核对通过";
            ExecStep4State = 2; ExecStep4Note = "已完成（备份保留）";
        }
    }
}
