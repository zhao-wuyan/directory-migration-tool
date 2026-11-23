using System.Text;
using MigrationCore.Services.Scanning;

namespace MigrationCore.Models.Scanning;

public enum ScanBackendKind
{
    RawNtfsMft,
    DirectoryWalkFallback
}

public enum ScanCoverageStatus
{
    Complete,
    Partial,
    Incomplete,
    Cancelled,
    AccessDenied
}

public enum ScanPhase
{
    ProbingVolume,
    ReadingMft,
    BuildingTree,
    AggregatingSizes,
    IdentifyingApplications,
    Completed,
    Cancelled
}

[Flags]
public enum ScanNodeFlags : ushort
{
    None = 0,
    IsDirectory = 1 << 0,
    IsReparsePoint = 1 << 1,
    IsWofCompressed = 1 << 2,
    IsCloudPlaceholder = 1 << 3,
    IsResidentData = 1 << 4,
    IsSparseOrCompressed = 1 << 5,
    HasAlternateDataStreams = 1 << 6,
    IsHardLinkSecondary = 1 << 7,
    IsIncomplete = 1 << 8,
    IsOrphaned = 1 << 9,
    IsDosShortNameOnly = 1 << 10
}

/// <summary>
/// 目录子树内硬链接的拓扑归属分类。
/// </summary>
public enum SubtreeHardLinkKind
{
    /// <summary>子树内不含任何硬链接文件。</summary>
    None = 0,
    /// <summary>全部硬链接的所有目录项均闭环在本子树内部，支持跨卷复制时在目标盘无损重建硬链接（零空间膨胀）。</summary>
    InternalOnly = 1,
    /// <summary>存在硬链接指向本子树外部，跨卷迁移必然断开共享关系且源盘对应空间无法释放，必须拦截。</summary>
    ExternalLinked = 2,
    /// <summary>无法通过 MFT 文件号核实硬链接是否闭环（如降级遍历或异常节点），按保守安全原则拦截。</summary>
    UnverifiedExternal = 3
}

/// <summary>
/// 目录子树预聚合风险位掩码（每节点仅占 1 字节，供左侧目录树 O(1) 展示感叹号/信息提示）。
/// </summary>
[Flags]
public enum SubtreeHazardFlags : byte
{
    None = 0,
    HasInternalHardLinks = 1 << 0,
    HasExternalHardLinks = 1 << 1,
    HasIncompleteNodes = 1 << 2,
    HasReparseDescendants = 1 << 3
}

/// <summary>
/// 单个目录子树的精确结构与硬链接诊断报告。
/// </summary>
public sealed record SubtreeHazardReport(
    bool HasHardLinks,
    SubtreeHardLinkKind HardLinkKind,
    int InternalHardLinkGroupCount,
    int InternalHardLinkEntryCount,
    int ExternalHardLinkFileCount,
    long HardLinkSharedBytes,
    string[] SampleExternalPeerPaths,
    bool HasIncompleteNodes,
    bool HasReparseDescendants)
{
    /// <summary>是否存在阻断迁移的外部或未验证硬链接（纯内部硬链接支持无损重建，不阻断迁移）。</summary>
    public bool HasBlockingHardLinks => HardLinkKind is SubtreeHardLinkKind.ExternalLinked or SubtreeHardLinkKind.UnverifiedExternal;

    /// <summary>是否仅包含闭环在目录子树内部的硬链接。</summary>
    public bool HasInternalOnlyHardLinks => HardLinkKind == SubtreeHardLinkKind.InternalOnly;
}

/// <summary>
/// 紧凑扁平节点结构（纯值类型，无托管 string/List 字段，适合百万级 MFT 记录低内存驻留）。
/// </summary>
public struct ScanNodeRecord
{
    public int NodeId;
    public int ParentNodeId;
    public ulong MftRecordNumber;
    public ushort SequenceNumber;
    public int NameOffset;
    public ushort NameLength;
    public byte NamePageIndex;
    public ScanNodeFlags Flags;
    public long LogicalBytes;
    public long AllocatedBytes;
    public long UniqueAllocatedBytes;
    public long AdsLogicalBytes;
    public int FileCount;
    public int DirectoryCount;
    public uint ReparseTag;
    public long LastWriteUtcTicks;
    public ushort LinkCount;

    public readonly bool IsDirectory => (Flags & ScanNodeFlags.IsDirectory) != 0;
    public readonly bool IsReparsePoint => (Flags & ScanNodeFlags.IsReparsePoint) != 0;
    public readonly bool IsWofCompressed => (Flags & ScanNodeFlags.IsWofCompressed) != 0;
    public readonly bool IsCloudPlaceholder => (Flags & ScanNodeFlags.IsCloudPlaceholder) != 0;
    public readonly bool IsResidentData => (Flags & ScanNodeFlags.IsResidentData) != 0;
    public readonly bool IsSparseOrCompressed => (Flags & ScanNodeFlags.IsSparseOrCompressed) != 0;
    public readonly bool IsHardLinkSecondary => (Flags & ScanNodeFlags.IsHardLinkSecondary) != 0;
    public readonly bool IsIncomplete => (Flags & ScanNodeFlags.IsIncomplete) != 0;
    public readonly bool IsOrphaned => (Flags & ScanNodeFlags.IsOrphaned) != 0;
}

public sealed record ScanVolumeInfo(
    string DriveRoot,
    string VolumeLabel,
    string VolumeGuid,
    uint VolumeSerial,
    string FileSystem,
    long TotalBytes,
    long FreeBytes,
    bool SupportsRawMft,
    string CapabilityReason)
{
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
    public double UsedPercent => TotalBytes > 0 ? Math.Clamp(UsedBytes * 100.0 / TotalBytes, 0, 100) : 0;
}

public sealed record ScanProgress(
    ScanPhase Phase,
    ScanBackendKind Backend,
    long ProcessedRecords,
    long? EstimatedTotalRecords,
    string Message)
{
    public double? Percent => EstimatedTotalRecords is > 0
        ? Math.Clamp(ProcessedRecords * 100.0 / EstimatedTotalRecords.Value, 0, 100)
        : null;
}

/// <summary>
/// 不可变只读扫描快照：采用扁平 struct 数组 + CSR 父子邻接表 + 分页 UTF-8 名称池。
/// 完整路径仅在显示、选中或查询时按需构建，不预分配百万条路径字符串。
/// </summary>
public sealed class ScanSnapshot
{
    public Guid ScanId { get; init; } = Guid.NewGuid();
    public required ScanVolumeInfo Volume { get; init; }
    public required ScanBackendKind Backend { get; init; }
    public required ScanCoverageStatus Coverage { get; init; }
    public string? FallbackReason { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }
    public required string PolicyVersion { get; init; }
    public int ErrorCount { get; init; }
    public int IncompleteNodeCount { get; init; }
    public long MetadataBytes { get; init; }
    public long UnknownBytes { get; init; }
    public long? UsnStartJournalId { get; init; }
    public long? UsnStartNextUsn { get; init; }
    public long? UsnEndNextUsn { get; init; }
    public bool RequiresLiveReverification { get; init; }
    public int RootNodeId { get; init; }
    public string? ScanRootPath { get; init; }
    public int OrphanContainerNodeId { get; init; } = -1;
    public required ScanNodeRecord[] Nodes { get; init; }
    public required int[] CsrOffsets { get; init; }
    public required int[] CsrChildren { get; init; }
    public required Utf8NamePool NamePool { get; init; }

    public bool CanUseForMigrationPlanning =>
        Coverage is ScanCoverageStatus.Complete or ScanCoverageStatus.Partial
        && Coverage != ScanCoverageStatus.Cancelled;

    public string GetNodeName(int nodeId)
    {
        ref readonly ScanNodeRecord node = ref Nodes[nodeId];
        return NamePool.GetString(node.NamePageIndex, node.NameOffset, node.NameLength);
    }

    public ReadOnlySpan<int> GetChildrenSpan(int nodeId)
    {
        if ((uint)nodeId >= (uint)Nodes.Length)
        {
            return ReadOnlySpan<int>.Empty;
        }
        int start = CsrOffsets[nodeId];
        int end = CsrOffsets[nodeId + 1];
        return CsrChildren.AsSpan(start, end - start);
    }

    public int[] GetChildren(int nodeId) => GetChildrenSpan(nodeId).ToArray();

    /// <summary>
    /// 按需沿父指针回溯拼装完整本地 DOS 路径。
    /// </summary>
    public string GetFullPath(int nodeId)
    {
        if ((uint)nodeId >= (uint)Nodes.Length)
        {
            return string.Empty;
        }

        string baseRoot = string.IsNullOrWhiteSpace(ScanRootPath) ? Volume.DriveRoot : ScanRootPath;
        if (nodeId == RootNodeId)
        {
            return baseRoot;
        }

        var parts = new List<string>(16);
        int current = nodeId;
        int guard = 0;
        while (current >= 0 && current != RootNodeId && guard++ < 512)
        {
            parts.Add(GetNodeName(current));
            int parent = Nodes[current].ParentNodeId;
            if (parent == current) break;
            current = parent;
        }

        parts.Reverse();
        var sb = new StringBuilder(baseRoot.Length + parts.Count * 16);
        sb.Append(baseRoot);
        if (!baseRoot.EndsWith('\\') && parts.Count > 0)
        {
            sb.Append('\\');
        }
        for (int i = 0; i < parts.Count; i++)
        {
            if (i > 0) sb.Append('\\');
            sb.Append(parts[i]);
        }
        return sb.ToString();
    }

    private readonly object _hazardCacheSync = new();
    private byte[]? _subtreeHazardFlagCache;

    /// <summary>
    /// 检查指定目录子树中是否存在硬链接（LinkCount > 1 或 IsHardLinkSecondary）或未完整扫描/孤立节点（兼容保留）。
    /// </summary>
    public (bool HasHardLinks, bool HasIncompleteNodes, bool HasReparseDescendants) InspectSubtreeHazards(int rootNodeId)
    {
        bool hasHardLinks = false;
        bool hasIncomplete = false;
        bool hasReparse = false;

        var stack = new Stack<int>();
        stack.Push(rootNodeId);
        while (stack.Count > 0)
        {
            int current = stack.Pop();
            ref readonly ScanNodeRecord node = ref Nodes[current];
            if (node.LinkCount > 1 || node.IsHardLinkSecondary) hasHardLinks = true;
            if (node.IsIncomplete || node.IsOrphaned) hasIncomplete = true;
            if (current != rootNodeId && node.IsReparsePoint) hasReparse = true;

            if (hasHardLinks && hasIncomplete && hasReparse)
            {
                break;
            }

            if (node.IsDirectory && !node.IsReparsePoint)
            {
                ReadOnlySpan<int> children = GetChildrenSpan(current);
                for (int i = 0; i < children.Length; i++)
                {
                    stack.Push(children[i]);
                }
            }
        }

        return (hasHardLinks, hasIncomplete, hasReparse);
    }

    /// <summary>
    /// O(1) 获取目录子树的预聚合风险标记（首次调用时 O(N) 扫描整棵树构建 1 byte/node 缓存，供左侧目录树实时显示感叹号警示）。
    /// </summary>
    public SubtreeHazardFlags GetSubtreeHazardFlags(int nodeId)
    {
        if ((uint)nodeId >= (uint)Nodes.Length)
        {
            return SubtreeHazardFlags.None;
        }

        byte[] cache = _subtreeHazardFlagCache ?? BuildSubtreeHazardFlagCache();
        return (SubtreeHazardFlags)cache[nodeId];
    }

    /// <summary>
    /// 精确分析指定目录子树内的硬链接拓扑（区分「纯子树内部闭环硬链接」与「跨外部硬链接」并采样外部关联路径）。
    /// </summary>
    public SubtreeHazardReport AnalyzeSubtreeHazards(int rootNodeId, int maxExternalSamples = 3)
    {
        if ((uint)rootNodeId >= (uint)Nodes.Length)
        {
            return new SubtreeHazardReport(false, SubtreeHardLinkKind.None, 0, 0, 0, 0, [], false, false);
        }

        bool hasIncomplete = false;
        bool hasReparse = false;
        bool hasUnverifiedHardLink = false;
        var subtreeHardLinkNodeIds = new HashSet<int>();
        var groupsByMft = new Dictionary<ulong, (int CountInSubtree, ushort ExpectedLinkCount, long LogicalBytes)>();

        var stack = new Stack<int>();
        stack.Push(rootNodeId);
        while (stack.Count > 0)
        {
            int current = stack.Pop();
            ref readonly ScanNodeRecord node = ref Nodes[current];
            if (node.IsIncomplete || node.IsOrphaned) hasIncomplete = true;
            if (current != rootNodeId && node.IsReparsePoint) hasReparse = true;

            if (!node.IsDirectory && (node.LinkCount > 1 || node.IsHardLinkSecondary))
            {
                subtreeHardLinkNodeIds.Add(current);
                if (node.MftRecordNumber == 0 || node.LinkCount <= 1)
                {
                    hasUnverifiedHardLink = true;
                }
                else if (groupsByMft.TryGetValue(node.MftRecordNumber, out var existing))
                {
                    groupsByMft[node.MftRecordNumber] = (
                        existing.CountInSubtree + 1,
                        Math.Max(existing.ExpectedLinkCount, node.LinkCount),
                        Math.Max(existing.LogicalBytes, node.LogicalBytes));
                }
                else
                {
                    groupsByMft[node.MftRecordNumber] = (1, node.LinkCount, node.LogicalBytes);
                }
            }

            if (node.IsDirectory && !node.IsReparsePoint)
            {
                ReadOnlySpan<int> children = GetChildrenSpan(current);
                for (int i = 0; i < children.Length; i++)
                {
                    stack.Push(children[i]);
                }
            }
        }

        int internalGroups = 0;
        int internalEntries = 0;
        int externalFiles = 0;
        long sharedBytes = 0;
        HashSet<ulong>? externalMftRecords = null;

        foreach (var kv in groupsByMft)
        {
            var (countInSubtree, expectedLinkCount, logicalBytes) = kv.Value;
            if (countInSubtree >= expectedLinkCount)
            {
                internalGroups++;
                internalEntries += countInSubtree;
                if (logicalBytes > 0 && countInSubtree > 1)
                {
                    sharedBytes += logicalBytes * (countInSubtree - 1);
                }
            }
            else
            {
                externalFiles += countInSubtree;
                (externalMftRecords ??= []).Add(kv.Key);
            }
        }

        string[] sampleExternalPeers = [];
        if (externalMftRecords is { Count: > 0 } && maxExternalSamples > 0)
        {
            var samples = new List<string>(maxExternalSamples);
            var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Nodes.Length && samples.Count < maxExternalSamples; i++)
            {
                ref readonly ScanNodeRecord candidate = ref Nodes[i];
                if (candidate.IsDirectory || candidate.MftRecordNumber == 0) continue;
                if (!externalMftRecords.Contains(candidate.MftRecordNumber)) continue;
                if (subtreeHardLinkNodeIds.Contains(i)) continue;

                string peerPath = GetFullPath(i);
                string peerDir = Path.GetDirectoryName(peerPath) ?? peerPath;
                if (seenDirs.Add(peerDir))
                {
                    samples.Add(peerPath);
                }
            }
            sampleExternalPeers = samples.ToArray();
        }

        bool hasAnyHardLinks = internalGroups > 0 || externalFiles > 0 || hasUnverifiedHardLink;
        SubtreeHardLinkKind kind = !hasAnyHardLinks
            ? SubtreeHardLinkKind.None
            : externalFiles > 0
                ? SubtreeHardLinkKind.ExternalLinked
                : hasUnverifiedHardLink
                    ? SubtreeHardLinkKind.UnverifiedExternal
                    : SubtreeHardLinkKind.InternalOnly;

        return new SubtreeHazardReport(
            HasHardLinks: hasAnyHardLinks,
            HardLinkKind: kind,
            InternalHardLinkGroupCount: internalGroups,
            InternalHardLinkEntryCount: internalEntries,
            ExternalHardLinkFileCount: externalFiles,
            HardLinkSharedBytes: sharedBytes,
            SampleExternalPeerPaths: sampleExternalPeers,
            HasIncompleteNodes: hasIncomplete,
            HasReparseDescendants: hasReparse);
    }

    private byte[] BuildSubtreeHazardFlagCache()
    {
        lock (_hazardCacheSync)
        {
            if (_subtreeHazardFlagCache is not null)
            {
                return _subtreeHazardFlagCache;
            }

            var flags = new byte[Nodes.Length];
            var hardLinkGroups = new Dictionary<ulong, List<int>>();

            // 1. 单趟收集硬链接组 + 向上传播不完整/重解析点标记（遇目录重解析点停止传播，与 InspectSubtreeHazards 语义严格一致）
            for (int i = 0; i < Nodes.Length; i++)
            {
                ref readonly ScanNodeRecord node = ref Nodes[i];

                if (node.IsIncomplete || node.IsOrphaned)
                {
                    MarkAncestors(flags, i, (byte)SubtreeHazardFlags.HasIncompleteNodes, includeSelf: true);
                }

                if (node.IsReparsePoint)
                {
                    MarkAncestors(flags, node.ParentNodeId, (byte)SubtreeHazardFlags.HasReparseDescendants, includeSelf: true);
                }

                if (!node.IsDirectory && (node.LinkCount > 1 || node.IsHardLinkSecondary))
                {
                    if (node.MftRecordNumber == 0 || node.LinkCount <= 1)
                    {
                        MarkAncestors(flags, node.ParentNodeId, (byte)SubtreeHazardFlags.HasExternalHardLinks, includeSelf: true);
                    }
                    else
                    {
                        if (!hardLinkGroups.TryGetValue(node.MftRecordNumber, out List<int>? list))
                        {
                            list = new List<int>(Math.Min((int)node.LinkCount, 8));
                            hardLinkGroups[node.MftRecordNumber] = list;
                        }
                        list.Add(i);
                    }
                }
            }

            // 2. 利用最小公共祖先（LCA）精确将每组硬链接归类到目录：
            //    - 包含整组全部成员的目录（LCA 及以上）标为 HasInternalHardLinks
            //    - 仅包含部分成员的目录（各成员父目录到 LCA 之下）标为 HasExternalHardLinks
            var ancestorScratch = new HashSet<int>();
            foreach (List<int> memberIds in hardLinkGroups.Values)
            {
                ushort expectedLinkCount = 0;
                for (int k = 0; k < memberIds.Count; k++)
                {
                    ushort lc = Nodes[memberIds[k]].LinkCount;
                    if (lc > expectedLinkCount) expectedLinkCount = lc;
                }

                if (memberIds.Count < expectedLinkCount)
                {
                    for (int k = 0; k < memberIds.Count; k++)
                    {
                        MarkAncestors(flags, Nodes[memberIds[k]].ParentNodeId, (byte)SubtreeHazardFlags.HasExternalHardLinks, includeSelf: true);
                    }
                    continue;
                }

                int lca = Nodes[memberIds[0]].ParentNodeId;
                for (int k = 1; k < memberIds.Count && lca >= 0; k++)
                {
                    lca = FindLowestCommonAncestor(lca, Nodes[memberIds[k]].ParentNodeId, ancestorScratch);
                }

                if (lca < 0)
                {
                    for (int k = 0; k < memberIds.Count; k++)
                    {
                        MarkAncestors(flags, Nodes[memberIds[k]].ParentNodeId, (byte)SubtreeHazardFlags.HasExternalHardLinks, includeSelf: true);
                    }
                    continue;
                }

                // lca 及其祖先完整闭环包含该硬链接组
                MarkAncestors(flags, lca, (byte)SubtreeHazardFlags.HasInternalHardLinks, includeSelf: true);

                // 每个成员父目录严格位于 lca 之下的路径，若单独迁移则会导致外部硬链接断开
                for (int k = 0; k < memberIds.Count; k++)
                {
                    int cur = Nodes[memberIds[k]].ParentNodeId;
                    int guard = 0;
                    while (cur >= 0 && cur != lca && cur < Nodes.Length && guard++ < 512)
                    {
                        if ((flags[cur] & (byte)SubtreeHazardFlags.HasExternalHardLinks) != 0)
                        {
                            // 注意：更高层可能仍未标记直到 lca，继续检查仅在已标记且不跳过未标节点时才提前停；这里直接设位
                        }
                        flags[cur] |= (byte)SubtreeHazardFlags.HasExternalHardLinks;
                        if (Nodes[cur].IsReparsePoint) break;
                        int parent = Nodes[cur].ParentNodeId;
                        if (parent == cur) break;
                        cur = parent;
                    }
                }
            }

            _subtreeHazardFlagCache = flags;
            return flags;
        }
    }

    private void MarkAncestors(byte[] flags, int startNodeId, byte bit, bool includeSelf)
    {
        int cur = includeSelf ? startNodeId : ((uint)startNodeId < (uint)Nodes.Length ? Nodes[startNodeId].ParentNodeId : -1);
        int guard = 0;
        while ((uint)cur < (uint)Nodes.Length && guard++ < 512)
        {
            if ((flags[cur] & bit) != 0)
            {
                break;
            }
            flags[cur] |= bit;
            if (cur != startNodeId && Nodes[cur].IsReparsePoint)
            {
                break;
            }
            int parent = Nodes[cur].ParentNodeId;
            if (parent == cur) break;
            cur = parent;
        }
    }

    private int FindLowestCommonAncestor(int a, int b, HashSet<int> scratch)
    {
        if (a == b) return a;
        scratch.Clear();

        int cur = a;
        int guard = 0;
        while ((uint)cur < (uint)Nodes.Length && guard++ < 512)
        {
            scratch.Add(cur);
            int parent = Nodes[cur].ParentNodeId;
            if (parent == cur) break;
            cur = parent;
        }

        cur = b;
        guard = 0;
        while ((uint)cur < (uint)Nodes.Length && guard++ < 512)
        {
            if (scratch.Contains(cur))
            {
                return cur;
            }
            int parent = Nodes[cur].ParentNodeId;
            if (parent == cur) break;
            cur = parent;
        }

        return -1;
    }

    /// <summary>
    /// 提取热点大目录列表（深度 1-4 层，按唯一分配空间降序），供 UI“大软件/热点目录聚合”视图使用。
    /// </summary>
    public int[] GetHotspotDirectories(int maxCount = 60, long minAllocatedBytes = 100L * 1024 * 1024)
    {
        var candidates = new List<(int NodeId, long Size)>();
        var queue = new Queue<(int NodeId, int Depth)>();
        queue.Enqueue((RootNodeId, 0));

        while (queue.Count > 0)
        {
            var (nodeId, depth) = queue.Dequeue();
            if (depth >= 4) continue;

            ReadOnlySpan<int> children = GetChildrenSpan(nodeId);
            for (int i = 0; i < children.Length; i++)
            {
                int childId = children[i];
                ref readonly ScanNodeRecord child = ref Nodes[childId];
                if (!child.IsDirectory || child.IsOrphaned) continue;

                long size = Math.Max(child.UniqueAllocatedBytes, child.LogicalBytes);
                if (depth >= 1 && size >= minAllocatedBytes)
                {
                    candidates.Add((childId, size));
                }

                if (!child.IsReparsePoint && depth + 1 < 4)
                {
                    queue.Enqueue((childId, depth + 1));
                }
            }
        }

        return candidates
            .OrderByDescending(x => x.Size)
            .Take(maxCount)
            .Select(x => x.NodeId)
            .ToArray();
    }
}
