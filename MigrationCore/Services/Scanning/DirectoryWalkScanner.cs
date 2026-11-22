using MigrationCore.Models.Scanning;
using MigrationCore.Services.Safety;

namespace MigrationCore.Services.Scanning;

/// <summary>
/// 非 NTFS、无原始卷权限或 MFT 解析失败时的受限降级目录遍历扫描器。
/// 严格不跟随目录重解析点；所有访问拒绝均显式计入 ErrorCount 并标记父子树 IsIncomplete，绝不冒充 MFT 或把漏扫计为 0。
/// </summary>
public sealed class DirectoryWalkScanner
{
    public Task<ScanSnapshot> ScanAsync(
        ScanVolumeInfo volume,
        string? rootDirectoryOverride = null,
        string? fallbackReason = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(() => ScanCore(volume, rootDirectoryOverride ?? volume.DriveRoot, fallbackReason, progress, ct), ct);
    }

    private static ScanSnapshot ScanCore(
        ScanVolumeInfo volume,
        string rootPath,
        string? fallbackReason,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        var namePool = new Utf8NamePool();
        var nodes = new List<ScanNodeRecord>(4096);

        var rootPooled = namePool.AddName(rootPath.AsSpan());
        nodes.Add(new ScanNodeRecord
        {
            NodeId = 0,
            ParentNodeId = -1,
            NamePageIndex = rootPooled.PageIndex,
            NameOffset = rootPooled.Offset,
            NameLength = rootPooled.Length,
            Flags = ScanNodeFlags.IsDirectory,
            LinkCount = 1
        });

        int errorCount = 0;
        int incompleteCount = 0;
        long processed = 0;
        bool cancelled = false;
        bool rootDenied = false;

        var queue = new Queue<(int NodeId, string Path)>();
        queue.Enqueue((0, rootPath));

        var enumOptions = new EnumerationOptions
        {
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.None,
            ReturnSpecialDirectories = false
        };

        while (queue.Count > 0)
        {
            if (ct.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            var (parentId, currentPath) = queue.Dequeue();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                var dirInfo = new DirectoryInfo(currentPath);
                entries = dirInfo.EnumerateFileSystemInfos("*", enumOptions);
                using var enumerator = entries.GetEnumerator();
                while (true)
                {
                    if (ct.IsCancellationRequested)
                    {
                        cancelled = true;
                        break;
                    }

                    FileSystemInfo entry;
                    try
                    {
                        if (!enumerator.MoveNext()) break;
                        entry = enumerator.Current;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        errorCount++;
                        MarkIncomplete(nodes, parentId, ref incompleteCount);
                        break;
                    }
                    catch (IOException)
                    {
                        errorCount++;
                        MarkIncomplete(nodes, parentId, ref incompleteCount);
                        break;
                    }

                    FileAttributes attrs;
                    try
                    {
                        attrs = entry.Attributes;
                    }
                    catch
                    {
                        errorCount++;
                        MarkIncomplete(nodes, parentId, ref incompleteCount);
                        continue;
                    }
                    bool isDir = (attrs & FileAttributes.Directory) != 0;
                    bool isReparse = (attrs & FileAttributes.ReparsePoint) != 0;
                    bool isSparseOrCompressed = (attrs & (FileAttributes.SparseFile | FileAttributes.Compressed)) != 0;
                    bool isCloud = (attrs & FileAttributes.Offline) != 0;

                    var pooled = namePool.AddName(entry.Name.AsSpan());
                    ScanNodeFlags flags = ScanNodeFlags.None;
                    if (isDir) flags |= ScanNodeFlags.IsDirectory;
                    if (isReparse) flags |= ScanNodeFlags.IsReparsePoint;
                    if (isSparseOrCompressed) flags |= ScanNodeFlags.IsSparseOrCompressed;
                    if (isCloud) flags |= ScanNodeFlags.IsCloudPlaceholder;

                    long logicalBytes = 0;
                    long allocatedBytes = 0;
                    if (!isDir && entry is FileInfo fi)
                    {
                        try
                        {
                            logicalBytes = fi.Length;
                            allocatedBytes = isCloud ? 0 : logicalBytes;
                        }
                        catch
                        {
                            flags |= ScanNodeFlags.IsIncomplete;
                            errorCount++;
                            incompleteCount++;
                        }
                    }

                    int nodeId = nodes.Count;
                    nodes.Add(new ScanNodeRecord
                    {
                        NodeId = nodeId,
                        ParentNodeId = parentId,
                        NamePageIndex = pooled.PageIndex,
                        NameOffset = pooled.Offset,
                        NameLength = pooled.Length,
                        Flags = flags,
                        LogicalBytes = logicalBytes,
                        AllocatedBytes = allocatedBytes,
                        UniqueAllocatedBytes = allocatedBytes,
                        FileCount = isDir ? 0 : 1,
                        LastWriteUtcTicks = TryGetWriteTicks(entry),
                        LinkCount = 1
                    });

                    // 严格禁止跟随目录重解析点/符号链接/junction
                    if (isDir && !isReparse)
                    {
                        queue.Enqueue((nodeId, entry.FullName));
                    }

                    processed++;
                    if (processed % 2048 == 0)
                    {
                        progress?.Report(new ScanProgress(
                            ScanPhase.ReadingMft,
                            ScanBackendKind.DirectoryWalkFallback,
                            processed,
                            null,
                            $"普通遍历扫描（降级模式）：已遍历 {processed:N0} 项…"));
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                errorCount++;
                MarkIncomplete(nodes, parentId, ref incompleteCount);
                if (parentId == 0) rootDenied = true;
            }
            catch (Exception)
            {
                errorCount++;
                MarkIncomplete(nodes, parentId, ref incompleteCount);
            }
        }

        ScanNodeRecord[] nodeArray = nodes.ToArray();
        NtfsMftRecordParser.BuildCsrAndAggregate(nodeArray, rootNodeId: 0, out int[] csrOffsets, out int[] csrChildren);

        ScanCoverageStatus coverage = cancelled
            ? ScanCoverageStatus.Cancelled
            : rootDenied
                ? ScanCoverageStatus.AccessDenied
                : errorCount > 0
                    ? ScanCoverageStatus.Partial
                    : ScanCoverageStatus.Complete;

        progress?.Report(new ScanProgress(
            cancelled ? ScanPhase.Cancelled : ScanPhase.Completed,
            ScanBackendKind.DirectoryWalkFallback,
            processed,
            null,
            cancelled
                ? "已取消普通目录遍历扫描"
                : $"普通遍历完成（降级模式）：{nodeArray.Length:N0} 项，访问受限 {errorCount} 处"));

        return new ScanSnapshot
        {
            Volume = volume,
            Backend = ScanBackendKind.DirectoryWalkFallback,
            FallbackReason = fallbackReason ?? "使用受限普通目录遍历（降级模式，非 MFT）",
            Coverage = coverage,
            StartedAtUtc = started,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            PolicyVersion = MigrationSafetyPolicy.CurrentPolicyVersion,
            ErrorCount = errorCount,
            IncompleteNodeCount = incompleteCount,
            MetadataBytes = 0,
            UnknownBytes = Math.Max(0, volume.UsedBytes - nodeArray[0].UniqueAllocatedBytes),
            RequiresLiveReverification = true,
            RootNodeId = 0,
            ScanRootPath = rootPath,
            Nodes = nodeArray,
            CsrOffsets = csrOffsets,
            CsrChildren = csrChildren,
            NamePool = namePool
        };
    }

    private static void MarkIncomplete(List<ScanNodeRecord> nodes, int nodeId, ref int incompleteCount)
    {
        ScanNodeRecord node = nodes[nodeId];
        if ((node.Flags & ScanNodeFlags.IsIncomplete) == 0)
        {
            node.Flags |= ScanNodeFlags.IsIncomplete;
            nodes[nodeId] = node;
            incompleteCount++;
        }
    }

    private static long TryGetWriteTicks(FileSystemInfo entry)
    {
        try { return entry.LastWriteTimeUtc.Ticks; }
        catch { return 0; }
    }
}
