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
    private void ToggleExpand(ScanTreeRowItem? row)
    {
        if (row is null || _snapshot is null) return;

        if (row.IsLoadMorePlaceholder)
        {
            LoadMoreChildren(row);
            return;
        }

        if (!row.HasChildren) return;

        int index = VisibleTreeRows.IndexOf(row);
        if (index < 0) return;

        if (row.IsExpanded)
        {
            int removeIdx = index + 1;
            while (removeIdx < VisibleTreeRows.Count && VisibleTreeRows[removeIdx].Depth > row.Depth)
            {
                VisibleTreeRows.RemoveAt(removeIdx);
            }
            row.IsExpanded = false;
        }
        else
        {
            row.IsExpanded = true;
            if (BrowseMode == ScanBrowseMode.HotspotPathTree
                && _hotspotPathAncestorNodes is not null
                && _hotspotPathKeptNodes is not null
                && _hotspotPathAncestorNodes.Contains(row.NodeId))
            {
                // 按路径模式下手动展开祖先容器时，仅展开通向大软件的剪枝子目录（保持全盘统一标尺）
                long rootTotal = GetNodeSizeForShare(_snapshot.RootNodeId);
                ReadOnlySpan<int> children = _snapshot.GetChildrenSpan(row.NodeId);
                int cursor = index + 1;
                for (int i = 0; i < children.Length; i++)
                {
                    int cid = children[i];
                    if (!_hotspotPathKeptNodes.Contains(cid)) continue;
                    VisibleTreeRows.Insert(cursor++, BuildRowItem(cid, row.Depth + 1, rootTotal));
                }
            }
            else
            {
                InsertChildRows(row.NodeId, row.Depth + 1, index + 1, startOffset: 0);
            }
        }
    }

    [RelayCommand]
    private void ToggleRowInPlan(ScanTreeRowItem? row)
    {
        if (row is null || _snapshot is null || !row.CanToggleInPlan) return;

        SelectedRow = row;
        var existing = PlanItems.FirstOrDefault(p => p.SourcePath.Equals(row.FullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (!IsBatchExecuting)
            {
                PlanItems.Remove(existing);
                InvalidateFrozenPlan();
                UpdateCanAddSelected();
            }
            return;
        }

        // L3 目录需要用户在右侧详情确认风险勾选框，避免行内误点绕过 L3 风险告知
        if (SelectedDecision?.Level == MigrationAdmissionLevel.ManualRiskConfirm && !IsL3RiskConfirmedForSelection)
        {
            CustomMessageBox.ShowInformation(
                "该目录属于 L3（未收录或普通个人数据），请先在右侧详情面板核对路径并勾选风险确认框后，再点击「加入待迁移清单」。",
                "需要人工风险确认");
            return;
        }

        AddSelectedToPlan();
    }

    [RelayCommand]
    private void CopyNativeGuidance()
    {
        if (string.IsNullOrWhiteSpace(SelectedNativeGuidance)) return;
        try
        {
            Clipboard.SetText(SelectedNativeGuidance);
            CustomMessageBox.ShowSuccess("官方原生迁移步骤已复制到剪贴板。", "已复制");
        }
        catch { }
    }

    [RelayCommand]
    private void ShowNativeGuidanceModal()
    {
        if (SelectedRow is null || string.IsNullOrWhiteSpace(SelectedNativeGuidance)) return;
        CustomMessageBox.ShowInformation(
            $"目录：{SelectedRow.FullPath}\n\n" +
            $"为何不建议直接建符号链接？\n{SelectedDecision?.Summary}\n\n" +
            $"官方原生迁移指引：\n{SelectedNativeGuidance}",
            "L2 厂商原生迁移指引");
    }

    [RelayCommand]
    private void BrowseUnifiedTarget()
    {
        var picker = new FolderPickerWindow { Owner = Application.Current.MainWindow };
        if (picker.ShowDialog() == true && !string.IsNullOrWhiteSpace(picker.SelectedPath))
        {
            UnifiedTargetRoot = picker.SelectedPath;
        }
    }

    [RelayCommand]
    private void EditPlanItemTarget(BatchPlanRowItem? item)
    {
        if (item is null || IsBatchExecuting) return;
        var picker = new FolderPickerWindow { Owner = Application.Current.MainWindow };
        if (picker.ShowDialog() == true && !string.IsNullOrWhiteSpace(picker.SelectedPath))
        {
            item.TargetPath = picker.SelectedPath;
            InvalidateFrozenPlan();
            UpdateTargetSpaceHint();
        }
    }

    private void RebuildVisibleRows()
    {
        VisibleTreeRows.Clear();
        if (_snapshot is null) return;

        string filter = SearchFilter.Trim();
        if (!string.IsNullOrEmpty(filter))
        {
            var matches = new List<(int NodeId, long Size)>();
            for (int i = 1; i < _snapshot.Nodes.Length; i++)
            {
                ref readonly ScanNodeRecord node = ref _snapshot.Nodes[i];
                if (!node.IsDirectory) continue;
                string name = _snapshot.GetNodeName(i);
                if (name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    && !IsHiddenSystemVolumeNode(_snapshot, i))
                {
                    matches.Add((i, Math.Max(node.UniqueAllocatedBytes, node.LogicalBytes)));
                }
            }

            long rootTotal = GetNodeSizeForShare(_snapshot.RootNodeId);
            foreach (var (nodeId, _) in matches.OrderByDescending(m => m.Size).Take(300))
            {
                VisibleTreeRows.Add(BuildRowItem(nodeId, depth: 0, parentBytes: rootTotal));
            }
            return;
        }

        if (BrowseMode == ScanBrowseMode.HotspotFlatList)
        {
            long rootTotal = GetNodeSizeForShare(_snapshot.RootNodeId);
            int[] hotspots = CollectDisjointHotspots(_snapshot, minAllocatedBytes: 50L * 1024 * 1024, maxCount: 80);
            if (hotspots.Length == 0)
            {
                hotspots = CollectDisjointHotspots(_snapshot, minAllocatedBytes: 1L * 1024 * 1024, maxCount: 80);
            }
            foreach (int nodeId in hotspots)
            {
                VisibleTreeRows.Add(BuildRowItem(nodeId, depth: 0, parentBytes: rootTotal, includeParentHint: true));
            }
            return;
        }

        if (BrowseMode == ScanBrowseMode.HotspotPathTree)
        {
            BuildHotspotPathTreeRows();
            return;
        }

        InsertChildRows(_snapshot.RootNodeId, depth: 0, insertAt: 0, startOffset: 0);
    }

    private long GetNodeSizeForShare(int nodeId)
    {
        ref readonly ScanNodeRecord node = ref _snapshot!.Nodes[nodeId];
        long bytes = node.UniqueAllocatedBytes > 0 ? node.UniqueAllocatedBytes : node.LogicalBytes;
        return Math.Max(1, bytes);
    }

    /// <summary>
    /// 构筑“按路径展开”的大软件剪枝路径树：仅保留通向真实大软件/大目录的祖先支路，从顶层自动缩进展开，
    /// 所有层级的空间占比均以全盘已用空间为统一标尺，避免出现“5 GB 子目录 100% 超过 500 GB 父目录”的视觉倒挂。
    /// </summary>
    private void BuildHotspotPathTreeRows()
    {
        if (_snapshot is null) return;

        int[] hotspots = CollectDisjointHotspots(_snapshot, minAllocatedBytes: 50L * 1024 * 1024, maxCount: 50);
        if (hotspots.Length == 0)
        {
            hotspots = CollectDisjointHotspots(_snapshot, minAllocatedBytes: 1L * 1024 * 1024, maxCount: 50);
        }
        if (hotspots.Length == 0) return;

        var keptNodes = new HashSet<int>();
        var autoExpandNodes = new HashSet<int>();
        foreach (int h in hotspots)
        {
            keptNodes.Add(h);
            int curr = _snapshot.Nodes[h].ParentNodeId;
            int guard = 0;
            while (curr >= 0 && curr != _snapshot.RootNodeId && guard++ < 64)
            {
                keptNodes.Add(curr);
                autoExpandNodes.Add(curr);
                curr = _snapshot.Nodes[curr].ParentNodeId;
            }
        }

        _hotspotPathKeptNodes = keptNodes;
        _hotspotPathAncestorNodes = autoExpandNodes;

        long rootTotal = GetNodeSizeForShare(_snapshot.RootNodeId);

        void EmitSubtree(int parentId, int depth)
        {
            ReadOnlySpan<int> children = _snapshot.GetChildrenSpan(parentId);
            for (int i = 0; i < children.Length; i++)
            {
                int cid = children[i];
                if (!keptNodes.Contains(cid)) continue;
                ScanTreeRowItem row = BuildRowItem(cid, depth, rootTotal);
                bool expand = IsHotspotPathTreeExpanded && autoExpandNodes.Contains(cid);
                row.IsExpanded = expand;
                VisibleTreeRows.Add(row);
                if (expand)
                {
                    EmitSubtree(cid, depth + 1);
                }
            }
        }

        EmitSubtree(_snapshot.RootNodeId, depth: 0);
    }

    private void InsertChildRows(int parentNodeId, int depth, int insertAt, int startOffset)
    {
        if (_snapshot is null) return;
        ReadOnlySpan<int> children = _snapshot.GetChildrenSpan(parentNodeId);
        // 在“按路径展开”大软件模式下手动展开节点时，仍延续全盘统一标尺，防止子项占比突然跳回 100%
        long shareBaseBytes = BrowseMode == ScanBrowseMode.HotspotPathTree
            ? GetNodeSizeForShare(_snapshot.RootNodeId)
            : GetNodeSizeForShare(parentNodeId);

        // 默认仅展示文件夹（隐藏无法迁移的单个文件如 hiberfil.sys/pagefile.sys），用户勾选“显示文件”时全部显示；
        // 卷根下 Windows 专属且隐藏的系统/更新/恢复/元数据目录（$WinREAgent、$WINDOWS.~BT、$Recycle.Bin、System Volume Information、Config.Msi 等）始终在目录树中隐藏
        int cursor = insertAt;
        int matched = 0;
        int displayed = 0;
        int remaining = 0;
        for (int i = 0; i < children.Length; i++)
        {
            int cid = children[i];
            ref readonly ScanNodeRecord child = ref _snapshot.Nodes[cid];
            if (!ShowFilesInTree && !child.IsDirectory) continue;
            if (IsHiddenSystemVolumeNode(_snapshot, cid)) continue;

            if (matched < startOffset)
            {
                matched++;
                continue;
            }

            if (displayed < DirectoryPageSize)
            {
                VisibleTreeRows.Insert(cursor++, BuildRowItem(cid, depth, shareBaseBytes));
                displayed++;
                matched++;
            }
            else
            {
                remaining++;
            }
        }

        if (remaining > 0)
        {
            VisibleTreeRows.Insert(cursor, new ScanTreeRowItem
            {
                IsLoadMorePlaceholder = true,
                LoadMoreParentNodeId = parentNodeId,
                LoadMoreNextOffset = startOffset + displayed,
                Depth = depth,
                Name = $"加载更多子项（剩余 {remaining:N0} 项）…",
                DisplayName = $"加载更多子项（剩余 {remaining:N0} 项）…"
            });
        }
    }

    private void LoadMoreChildren(ScanTreeRowItem placeholder)
    {
        int idx = VisibleTreeRows.IndexOf(placeholder);
        if (idx < 0) return;
        VisibleTreeRows.RemoveAt(idx);
        InsertChildRows(placeholder.LoadMoreParentNodeId, placeholder.Depth, idx, placeholder.LoadMoreNextOffset);
    }

    private ScanTreeRowItem BuildRowItem(int nodeId, int depth, long parentBytes, bool includeParentHint = false)
    {
        ref readonly ScanNodeRecord node = ref _snapshot!.Nodes[nodeId];
        string fullPath = _snapshot.GetFullPath(nodeId);
        string name = _snapshot.GetNodeName(nodeId);
        string displayName = name;
        long sizeForShare = node.UniqueAllocatedBytes > 0 ? node.UniqueAllocatedBytes : node.LogicalBytes;
        double share = Math.Clamp(sizeForShare * 100.0 / parentBytes, 0, 100);

        MigrationAdmissionLevel level = MigrationAdmissionLevel.ManualRiskConfirm;
        bool isToolSelfProtected = false;
        DirectoryHintLevel hintLevel = DirectoryHintLevel.None;
        string hintToolTip = string.Empty;
        string inlineSummary = string.Empty;
        string summaryToolTip = string.Empty;
        string attrLabel = string.Empty;
        if (node.IsDirectory)
        {
            if (node.IsOrphaned || nodeId == _snapshot.OrphanContainerNodeId)
            {
                // 虚拟孤立/未解析容器及其子项绝不允许作为 L3 迁移，强制标为 L0 系统保护
                level = MigrationAdmissionLevel.HardDenied;
                attrLabel = "只读诊断";
            }
            else
            {
                SafetyPolicyDecision preview = MigrationSafetyPolicy.EvaluateSource(
                    fullPath,
                    _catalog,
                    SafetyEvaluationContext.ScanPreview,
                    checkFilesystem: false);
                level = preview.Level;
                isToolSelfProtected = preview.ReasonCodes.Contains(SafetyReasonCodes.L0SelfOrActiveTransaction, StringComparer.OrdinalIgnoreCase);

                DirectoryExplanation? explanation = KnownDirectoryExplainer.Explain(fullPath, _catalog, preview);
                if (explanation is not null)
                {
                    if (!string.IsNullOrWhiteSpace(explanation.ShortSummary))
                    {
                        inlineSummary = explanation.ShortSummary;
                        summaryToolTip = KnownDirectoryExplainer.FormatHoverToolTip(explanation);
                    }

                    if (explanation.HintLevel != DirectoryHintLevel.None)
                    {
                        hintLevel = explanation.HintLevel;
                        hintToolTip = string.IsNullOrEmpty(summaryToolTip)
                            ? KnownDirectoryExplainer.FormatHoverToolTip(explanation)
                            : summaryToolTip;
                    }
                }

                // O(1) 预聚合子树硬链接与不完整风险警示：在左侧目录树直接标出感叹号（不可迁外部硬链接/损坏）或信息图标（可无损重建内部硬链接）
                if (level != MigrationAdmissionLevel.HardDenied)
                {
                    SubtreeHazardFlags subtreeFlags = _snapshot.GetSubtreeHazardFlags(nodeId);
                    bool hasExternalHardLinks = (subtreeFlags & SubtreeHazardFlags.HasExternalHardLinks) != 0;
                    bool hasInternalHardLinks = (subtreeFlags & SubtreeHazardFlags.HasInternalHardLinks) != 0;
                    bool hasIncompleteNodes = (subtreeFlags & SubtreeHazardFlags.HasIncompleteNodes) != 0;

                    if (hasExternalHardLinks || hasIncompleteNodes)
                    {
                        string hazardTip = hasExternalHardLinks
                            ? "⚠ 包含跨目录外部硬链接（禁止直接迁移）\n该目录存在与目录外部共享底层数据的 NTFS 硬链接；跨盘迁移会断开共享语义且无法释放源盘对应空间。\n建议：点击选中查看外部关联路径，或改用软件自身路径设置。"
                            : "⚠ 子树包含扫描不完整或受限节点（禁止迁移）\n该目录下存在扫描期间无法读取或损坏的记录，为保证数据安全已禁止加入迁移清单。";
                        hintLevel = DirectoryHintLevel.Warning;
                        hintToolTip = string.IsNullOrWhiteSpace(hintToolTip)
                            ? hazardTip
                            : $"{hazardTip}\n\n{hintToolTip}";
                    }
                    else if (hasInternalHardLinks)
                    {
                        string internalTip = "ℹ 包含目录内闭环硬链接（支持无损迁移）\n该目录内的硬链接全部闭环在目录内部；迁移时工具将在目标盘自动按 FileId 无损重建硬链接，零空间膨胀。";
                        if (hintLevel == DirectoryHintLevel.None)
                        {
                            hintLevel = DirectoryHintLevel.Info;
                        }
                        hintToolTip = string.IsNullOrWhiteSpace(hintToolTip)
                            ? internalTip
                            : $"{hintToolTip}\n\n{internalTip}";
                    }
                }

                string rawAttr = preview.MatchedApplicationName ?? _catalog.IdentifyAttributionLabel(fullPath) ?? string.Empty;

                if (includeParentHint)
                {
                    string? parentDir = Path.GetDirectoryName(fullPath);
                    string parentName = string.IsNullOrEmpty(parentDir) ? string.Empty : Path.GetFileName(parentDir.TrimEnd('\\'));
                    if (!string.IsNullOrEmpty(parentName) && !IsGenericContainerName(parentName))
                    {
                        displayName = $"{parentName} / {name}";
                    }

                    if (!string.IsNullOrEmpty(rawAttr)
                        && !displayName.Contains(rawAttr, StringComparison.OrdinalIgnoreCase))
                    {
                        attrLabel = rawAttr;
                    }
                    else if (parentDir is not null)
                    {
                        if (parentDir.EndsWith(@"\AppData\Local", StringComparison.OrdinalIgnoreCase)) attrLabel = "AppData\\Local";
                        else if (parentDir.EndsWith(@"\AppData\Roaming", StringComparison.OrdinalIgnoreCase)) attrLabel = "AppData\\Roaming";
                    }
                }
                else if (!string.IsNullOrEmpty(rawAttr) && !name.Equals(rawAttr, StringComparison.OrdinalIgnoreCase))
                {
                    attrLabel = rawAttr;
                }
            }
        }

        bool hasChildren = false;
        if (node.IsDirectory && !node.IsReparsePoint)
        {
            ReadOnlySpan<int> childIds = _snapshot.GetChildrenSpan(nodeId);
            if (ShowFilesInTree)
            {
                hasChildren = childIds.Length > 0;
            }
            else
            {
                for (int i = 0; i < childIds.Length; i++)
                {
                    if (_snapshot.Nodes[childIds[i]].IsDirectory) { hasChildren = true; break; }
                }
            }
        }
        return new ScanTreeRowItem
        {
            NodeId = nodeId,
            Depth = depth,
            IsDirectory = node.IsDirectory,
            HasChildren = hasChildren,
            Name = name,
            DisplayName = displayName,
            FullPath = fullPath,
            LogicalBytes = node.LogicalBytes,
            AllocatedBytes = node.AllocatedBytes,
            UniqueAllocatedBytes = node.UniqueAllocatedBytes,
            FileCount = node.FileCount,
            DirectoryCount = node.DirectoryCount,
            ParentSharePercent = share,
            AttributionLabel = attrLabel,
            InlineSummary = inlineSummary,
            SummaryToolTip = summaryToolTip,
            AdmissionLevel = level,
            IsToolSelfProtected = isToolSelfProtected,
            HintLevel = hintLevel,
            HintToolTip = hintToolTip,
            Flags = node.Flags,
            IsInPlan = PlanItems.Any(p => p.SourcePath.Equals(fullPath, StringComparison.OrdinalIgnoreCase)),
            HasDescendantInPlan = HasDescendantPathInPlan(fullPath, PlanItems.Select(p => p.SourcePath))
        };
    }

    /// <summary>
    /// 判断节点是否为卷根下应隐藏的 Windows 专属隐藏系统目录（如 $WinREAgent、$WINDOWS.~BT、$Recycle.Bin、System Volume Information、Config.Msi 等）或其子项。
    /// 委托至 <see cref="MigrationSafetyPolicy.IsHiddenSystemVolumeNode"/> 单一事实源；仅在视图展示层过滤，保留底层 ScanSnapshot 的真实卷空间统计与 L0 安全策略。
    /// </summary>
    internal static bool IsHiddenSystemVolumeNode(ScanSnapshot snapshot, int nodeId) =>
        MigrationSafetyPolicy.IsHiddenSystemVolumeNode(snapshot, nodeId);

    private static bool IsGenericContainerName(string folderName) =>
        folderName.Equals("Users", StringComparison.OrdinalIgnoreCase)
        || folderName.Equals("AppData", StringComparison.OrdinalIgnoreCase)
        || folderName.Equals("Local", StringComparison.OrdinalIgnoreCase)
        || folderName.Equals("Roaming", StringComparison.OrdinalIgnoreCase)
        || folderName.Equals("LocalLow", StringComparison.OrdinalIgnoreCase)
        || folderName.Equals("Programs", StringComparison.OrdinalIgnoreCase)
        || folderName.Equals("Program Files", StringComparison.OrdinalIgnoreCase)
        || folderName.Equals("Program Files (x86)", StringComparison.OrdinalIgnoreCase)
        || folderName.Equals("ProgramData", StringComparison.OrdinalIgnoreCase)
        || folderName.Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase);

    private void SyncTreePlanChecks()
    {
        if (VisibleTreeRows.Count == 0) return;
        string[] planPaths = PlanItems
            .Select(p => p.SourcePath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToArray();
        var set = planPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in VisibleTreeRows)
        {
            bool direct = set.Contains(row.FullPath);
            row.IsInPlan = direct;
            row.HasDescendantInPlan = !direct && row.IsDirectory && HasDescendantPathInPlan(row.FullPath, planPaths);
        }
    }

    private static bool HasDescendantPathInPlan(string directoryPath, IEnumerable<string> planPaths)
    {
        if (string.IsNullOrWhiteSpace(directoryPath)) return false;
        string normalized = directoryPath.TrimEnd('\\');
        string prefix = normalized + "\\";

        foreach (string candidate in planPaths)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            string trimmedCandidate = candidate.TrimEnd('\\');
            if (trimmedCandidate.Length > prefix.Length
                && trimmedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private void InspectSelectedRow(ScanTreeRowItem? row)
    {
        IsL3RiskConfirmedForSelection = false;
        if (row is null || row.IsLoadMorePlaceholder || _snapshot is null)
        {
            SelectedDecision = null;
            SelectedLevelBadgeText = "未选中目录";
            SelectedLevelBadgeBg = "#F8FAFC";
            SelectedLevelBadgeBorder = "#E2E8F0";
            SelectedLevelBadgeFg = "#64748B";
            SelectedTitle = "请选择左侧目录";
            SelectedPathDisplay = "点击目录树中的任意目录查看空间占用与 L0–L3 安全准入评估。";
            SelectedAllocatedDisplay = "—";
            SelectedFileCountDisplay = "—";
            HasDirectoryExplanation = false;
            SelectedExplanationIsWarning = false;
            SelectedDirectoryRoleTitle = string.Empty;
            SelectedDirectoryPurpose = string.Empty;
            SelectedDirectoryAdvice = string.Empty;
            SelectedEvidenceIdentity = "• 识别依据：等待选择目录";
            SelectedEvidenceStructure = "• 结构核验：—";
            SelectedEvidenceStorage = "• 空间口径：—";
            SelectedNativeGuidance = string.Empty;
            ShowNativeGuidance = false;
            ShowL3ConfirmCheck = false;
            CanAddSelectedToPlan = false;
            IsSelectedAlreadyInPlan = false;
            return;
        }

        SelectedTitle = string.IsNullOrWhiteSpace(row.DisplayName) ? row.Name : row.DisplayName;
        SelectedPathDisplay = row.FullPath;
        SelectedAllocatedDisplay = FileStatsService.FormatBytes(row.UniqueAllocatedBytes);
        SelectedFileCountDisplay = row.IsDirectory ? $"{row.FileCount:N0} 个" : "1 个文件";

        HasDirectoryExplanation = false;
        SelectedExplanationIsWarning = false;
        SelectedDirectoryRoleTitle = string.Empty;
        SelectedDirectoryPurpose = string.Empty;
        SelectedDirectoryAdvice = string.Empty;

        // 更新面包屑
        string[] segs = row.FullPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        BreadcrumbText = "此电脑  ›  " + string.Join("  ›  ", segs);

        var storageTags = new List<string>();
        if ((row.Flags & ScanNodeFlags.IsResidentData) != 0) storageTags.Add("含常驻 MFT 小文件");
        if ((row.Flags & ScanNodeFlags.IsWofCompressed) != 0) storageTags.Add("含 WOF 压缩（跨卷可能膨胀）");
        if ((row.Flags & ScanNodeFlags.IsCloudPlaceholder) != 0) storageTags.Add("含云端占位文件");
        if ((row.Flags & ScanNodeFlags.IsHardLinkSecondary) != 0) storageTags.Add("含硬链接共享文件");
        if ((row.Flags & ScanNodeFlags.IsIncomplete) != 0) storageTags.Add("存在受限/损坏子节点");
        SelectedEvidenceStorage = $"• 空间口径：逻辑 {FileStatsService.FormatBytes(row.LogicalBytes)} / 唯一分配 {FileStatsService.FormatBytes(row.UniqueAllocatedBytes)}"
            + (storageTags.Count > 0 ? $"（{string.Join("、", storageTags)}）" : string.Empty);

        if ((row.Flags & ScanNodeFlags.IsOrphaned) != 0)
        {
            SelectedDecision = null;
            SelectedLevelBadgeText = "L0 硬拒绝 · 未解析系统/孤立记录";
            SelectedLevelBadgeBg = "#FEF2F2";
            SelectedLevelBadgeBorder = "#FECACA";
            SelectedLevelBadgeFg = "#C42B1C";
            SelectedEvidenceIdentity = "• 识别依据：无法解析到真实磁盘路径的孤立或元数据记录（仅供只读空间诊断）";
            SelectedEvidenceStructure = "• 结构核验：禁止迁移或删除此类虚拟诊断项";
            SelectedSafeNote = "该节点为只读诊断项，不可加入迁移清单。";
            ShowNativeGuidance = false;
            ShowL3ConfirmCheck = false;
            CanAddSelectedToPlan = false;
            IsSelectedAlreadyInPlan = false;
            return;
        }

        if (!row.IsDirectory)
        {
            SelectedDecision = null;
            SelectedLevelBadgeText = "单个文件 · 不可迁移";
            SelectedLevelBadgeBg = "#F8FAFC";
            SelectedLevelBadgeBorder = "#E2E8F0";
            SelectedLevelBadgeFg = "#64748B";
            SelectedEvidenceIdentity = "• 识别依据：单个文件对象（本工具仅支持目录级符号链接迁移）";
            SelectedEvidenceStructure = "• 结构核验：请选择其上级业务目录进行评估";
            ShowNativeGuidance = false;
            ShowL3ConfirmCheck = false;
            CanAddSelectedToPlan = false;
            IsSelectedAlreadyInPlan = false;
            return;
        }

        SafetyPolicyDecision decision = MigrationSafetyPolicy.EvaluateSource(
            row.FullPath,
            _catalog,
            SafetyEvaluationContext.NewMigration,
            checkFilesystem: true);

        SelectedDecision = decision;
        bool isSelfProtected = decision.ReasonCodes.Contains(SafetyReasonCodes.L0SelfOrActiveTransaction, StringComparer.OrdinalIgnoreCase);
        SubtreeHazardReport subtreeHazards = _snapshot.AnalyzeSubtreeHazards(row.NodeId, maxExternalSamples: 3);

        DirectoryExplanation? explanation = KnownDirectoryExplainer.Explain(row.FullPath, _catalog, decision);
        if (decision.Level != MigrationAdmissionLevel.HardDenied && (subtreeHazards.HasBlockingHardLinks || subtreeHazards.HasIncompleteNodes))
        {
            HasDirectoryExplanation = true;
            SelectedExplanationIsWarning = true;
            if (subtreeHazards.HasBlockingHardLinks)
            {
                string peerPreview = subtreeHazards.SampleExternalPeerPaths.Length > 0
                    ? $"（外部关联如：{string.Join("；", subtreeHazards.SampleExternalPeerPaths)}）"
                    : string.Empty;
                SelectedDirectoryRoleTitle = "检测到跨目录外部硬链接（不可直接迁移）";
                SelectedDirectoryPurpose = subtreeHazards.HardLinkKind == SubtreeHardLinkKind.ExternalLinked
                    ? $"该目录下有 {subtreeHazards.ExternalHardLinkFileCount} 个文件与目录外部共享底层 NTFS 数据{peerPreview}。"
                    : "该目录下存在未能核实闭环归属的硬链接文件。";
                SelectedDirectoryAdvice = "NTFS 硬链接无法跨盘维持，直接迁出会断开共享关系且源盘对应空间无法释放。建议改用软件原生路径设置，或扩大选择范围至包含所有硬链接的共同父目录（若安全）。";
            }
            else
            {
                SelectedDirectoryRoleTitle = "子树包含扫描不完整或受限节点";
                SelectedDirectoryPurpose = "扫描该目录子树时存在访问受限或损坏的节点记录。";
                SelectedDirectoryAdvice = "为保证迁移核对完整，禁止直接加入迁移清单；请尝试以管理员身份重新完整扫描后再评估。";
            }
        }
        else if (decision.Level != MigrationAdmissionLevel.HardDenied && subtreeHazards.HasInternalOnlyHardLinks && explanation is null)
        {
            HasDirectoryExplanation = true;
            SelectedExplanationIsWarning = false;
            SelectedDirectoryRoleTitle = "包含目录内闭环硬链接（支持无损重建）";
            SelectedDirectoryPurpose = $"检测到 {subtreeHazards.InternalHardLinkGroupCount} 组目录内硬链接（共 {subtreeHazards.InternalHardLinkEntryCount} 个文件项，节省约 {FileStatsService.FormatBytes(subtreeHazards.HardLinkSharedBytes)} 重复占用）。";
            SelectedDirectoryAdvice = "所有硬链接均闭环在本目录内部；迁移时工具会去重复制唯一文件并在目标盘无损重建硬链接，不会造成目标盘空间膨胀。";
        }
        else
        {
            HasDirectoryExplanation = explanation is not null;
            SelectedExplanationIsWarning = explanation?.HintLevel == DirectoryHintLevel.Warning;
            SelectedDirectoryRoleTitle = explanation?.RoleTitle ?? string.Empty;
            SelectedDirectoryPurpose = explanation?.Purpose ?? string.Empty;
            SelectedDirectoryAdvice = explanation?.MigrationAdvice ?? string.Empty;
        }

        (SelectedLevelBadgeText, SelectedLevelBadgeBg, SelectedLevelBadgeBorder, SelectedLevelBadgeFg) = decision.Level switch
        {
            MigrationAdmissionLevel.HardDenied => (isSelfProtected ? "L0 硬拒绝 · 工具自身/事务保护" : "L0 硬拒绝 · 系统/高危保护", "#FEF2F2", "#FECACA", "#C42B1C"),
            MigrationAdmissionLevel.VerifiedEligible => ("L1 推荐迁移 · 已验证规则", "#ECFDF5", "#A7F3D0", "#0F7B4B"),
            MigrationAdmissionLevel.VendorNativeRecommended => ("L2 原生引导 · 推荐官方方案", "#EBF5FF", "#BFDBFE", "#0078D4"),
            _ => ("L3 人工确认 · 普通/未收录目录", "#FFFBEB", "#FDE68A", "#B45309")
        };

        SelectedEvidenceIdentity = decision.MatchedApplicationId is not null
            ? $"• 识别依据：内置规则 #{decision.MatchedApplicationId}（{decision.MatchedApplicationName}）"
            : $"• 识别依据：{decision.Summary}";

        string hardLinkStructureNote = subtreeHazards.HardLinkKind switch
        {
            SubtreeHardLinkKind.InternalOnly => $"；含 {subtreeHazards.InternalHardLinkGroupCount} 组内部硬链接（支持目标盘无损重建）",
            SubtreeHardLinkKind.ExternalLinked => $"；含 {subtreeHazards.ExternalHardLinkFileCount} 个跨外部硬链接（禁止跨卷迁移）",
            SubtreeHardLinkKind.UnverifiedExternal => "；含未验证硬链接（禁止迁移）",
            _ => string.Empty
        };

        SelectedEvidenceStructure = (decision.ReasonCodes.Length > 0
            ? $"• 结构核验：{string.Join(" / ", decision.ReasonCodes)}"
            : "• 结构核验：无重解析点、无系统禁区命中、路径身份正常") + hardLinkStructureNote;

        SelectedSafeNote = decision.Level == MigrationAdmissionLevel.HardDenied
            ? $"已由统一安全底座拦截：{decision.Summary}"
            : subtreeHazards.HasBlockingHardLinks
                ? "检测到跨外部硬链接：跨卷复制会破坏共享语义且无法释放源盘空间，已禁止加入迁移清单。"
                : subtreeHazards.HasInternalOnlyHardLinks
                    ? $"检测到 {subtreeHazards.InternalHardLinkGroupCount} 组目录内硬链接：复制时将在目标盘自动无损重建硬链接（零空间膨胀），原目录仍保留为备份。"
                    : "迁移后原目录将在源盘改名保留为备份，待你运行对应软件验证加载正常并经两次确认后再手动删除。";

        ShowNativeGuidance = decision.Level == MigrationAdmissionLevel.VendorNativeRecommended;
        SelectedNativeGuidance = decision.NativeGuidance ?? string.Empty;
        ShowL3ConfirmCheck = decision.Level == MigrationAdmissionLevel.ManualRiskConfirm
            && !subtreeHazards.HasBlockingHardLinks
            && !subtreeHazards.HasIncompleteNodes;
        _selectedHasBlockingSubtreeHazard = subtreeHazards.HasBlockingHardLinks || subtreeHazards.HasIncompleteNodes;
        UpdateCanAddSelected();
    }

    private bool _selectedHasBlockingSubtreeHazard;

    private void UpdateCanAddSelected()
    {
        IsSelectedAlreadyInPlan = SelectedRow is not null
            && PlanItems.Any(p => p.SourcePath.Equals(SelectedRow.FullPath, StringComparison.OrdinalIgnoreCase));

        if (SelectedRow is null || !SelectedRow.IsDirectory || SelectedDecision is null)
        {
            CanAddSelectedToPlan = false;
            return;
        }

        if (_selectedHasBlockingSubtreeHazard && !IsSelectedAlreadyInPlan)
        {
            CanAddSelectedToPlan = false;
            return;
        }

        CanAddSelectedToPlan = IsSelectedAlreadyInPlan || SelectedDecision.Level switch
        {
            MigrationAdmissionLevel.VerifiedEligible => true,
            MigrationAdmissionLevel.ManualRiskConfirm => IsL3RiskConfirmedForSelection,
            _ => false
        };
    }

    private void RecomputeDraftTargets()
    {
        if (string.IsNullOrWhiteSpace(UnifiedTargetRoot)) return;
        foreach (BatchPlanRowItem item in PlanItems)
        {
            try
            {
                item.TargetPath = MigrationPlanService.BuildDefaultTargetPath(item.SourcePath, UnifiedTargetRoot, item.ApplicationId);
            }
            catch { }
        }
    }

    private void UpdateTargetSpaceHint()
    {
        try
        {
            string? root = Path.GetPathRoot(UnifiedTargetRoot);
            if (!string.IsNullOrWhiteSpace(root))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady)
                {
                    long required = PlanItems.Sum(p => Math.Max(p.AllocatedBytes, p.LogicalBytes));
                    TargetSpaceHintText = required > 0
                        ? $"可用 {FileStatsService.FormatBytes(drive.AvailableFreeSpace)} · 需预留 {FileStatsService.FormatBytes(required)}"
                        : $"可用 {FileStatsService.FormatBytes(drive.AvailableFreeSpace)}";
                    return;
                }
            }
        }
        catch { }

        TargetSpaceHintText = "请输入有效目标磁盘目录";
    }

    private void InvalidateFrozenPlan()
    {
        _frozenPlan = null;
        CanStartBatch = false;
    }
}
