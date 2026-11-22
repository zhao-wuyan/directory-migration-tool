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

    /// <summary>
    /// 检查指定目录子树中是否存在硬链接（LinkCount > 1 或 IsHardLinkSecondary）或未完整扫描/孤立节点。
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
