using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using MigrationCore.Models.Scanning;
using MigrationCore.Services.Safety;

namespace MigrationCore.Services.Scanning;

/// <summary>
/// 卷底层存储介质类型，用于自动决定不打满磁盘的温和并发策略。
/// </summary>
public enum StorageMediaKind
{
    Unknown = 0,
    Ssd = 1,
    Hdd = 2
}

/// <summary>
/// 梯度二目录遍历扫描器的并发与磁盘 I/O 保护配置（默认策略确保不打满磁盘）。
/// </summary>
public sealed record DirectoryWalkOptions(
    int? MaxDegreeOfParallelism = null,
    int BufferSizeBytes = 64 * 1024,
    bool UseBackgroundIoPriority = true,
    int DirectoriesPerYield = 32);

/// <summary>
/// 非 NTFS（含 ReFS / Dev Drive / exFAT）、无原始卷权限或 MFT 解析失败时的梯度二并发目录遍历扫描器。
/// 基于公开 Win32 API <c>GetFileInformationByHandleEx(FileFullDirectoryInfo)</c> + 64 KiB 池化缓冲 + 纯 Safe C# 二进制解包：
/// <list type="bullet">
/// <item>单次内核调用批量获取目录项，文件节点零托管 string 分配直写 <see cref="Utf8NamePool"/>，直接读取真实 <c>AllocationSize</c> 与 <c>ReparseTag</c>。</item>
/// <item>自动探测 SSD / HDD 介质并启用 <c>THREAD_MODE_BACKGROUND_BEGIN</c> 后台低 I/O 优先级与保守并发度，避免打满磁盘影响前台操作。</item>
/// <item>严格不跟随目录重解析点；所有访问拒绝均显式计入 <c>ErrorCount</c> 并标记父子树 <c>IsIncomplete</c>，绝不冒充 MFT 或把漏扫计为 0。</item>
/// </list>
/// </summary>
public sealed class DirectoryWalkScanner
{
    private const int MinBufferBytes = 4 * 1024;
    private const int MaxBufferBytes = 512 * 1024;

    public Task<ScanSnapshot> ScanAsync(
        ScanVolumeInfo volume,
        string? rootDirectoryOverride = null,
        string? fallbackReason = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        return ScanAsync(volume, rootDirectoryOverride, fallbackReason, options: null, progress, ct);
    }

    public Task<ScanSnapshot> ScanAsync(
        ScanVolumeInfo volume,
        string? rootDirectoryOverride,
        string? fallbackReason,
        DirectoryWalkOptions? options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(
            () => ScanCore(volume, rootDirectoryOverride ?? volume.DriveRoot, fallbackReason, options ?? new DirectoryWalkOptions(), progress, ct),
            ct);
    }

    private static ScanSnapshot ScanCore(
        ScanVolumeInfo volume,
        string rootPath,
        string? fallbackReason,
        DirectoryWalkOptions options,
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

        StorageMediaKind mediaKind = StorageMediaDetector.Detect(volume.DriveRoot);
        int workerCount = ResolveWorkerCount(mediaKind, options.MaxDegreeOfParallelism);
        int bufferSize = Math.Clamp(options.BufferSizeBytes, MinBufferBytes, MaxBufferBytes);
        int yieldInterval = Math.Max(1, options.DirectoriesPerYield);
        bool throttleHdd = mediaKind != StorageMediaKind.Ssd;

        var queue = new ConcurrentQueue<(int NodeId, string Path)>();
        queue.Enqueue((0, rootPath));

        int pendingDirectories = 1;
        using var workAvailable = new SemaphoreSlim(1, int.MaxValue);
        using var doneCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        Lock commitLock = new();
        int errorCount = 0;
        int incompleteCount = 0;
        long processed = 0;
        long nextProgressThreshold = 2048;
        bool cancelled = false;
        bool rootDenied = false;

        string mediaTag = mediaKind switch
        {
            StorageMediaKind.Ssd => $"SSD·{workerCount}线程低I/O",
            StorageMediaKind.Hdd => "HDD·单线程低I/O",
            _ => $"{workerCount}线程低I/O"
        };

        void CommitBatch(int parentNodeId, string parentPath, byte[] rawBuffer, List<ParsedDirEntrySlice> batch)
        {
            if (batch.Count == 0) return;

            int childDirsEnqueued = 0;
            long snapshotProcessed;
            bool shouldReport = false;

            lock (commitLock)
            {
                for (int i = 0; i < batch.Count; i++)
                {
                    ParsedDirEntrySlice entry = batch[i];
                    ReadOnlySpan<byte> nameUtf16 = rawBuffer.AsSpan(entry.NameOffset, entry.NameByteLength);
                    // 普通文件与重解析点直接从内核 UTF-16LE 缓冲写入 UTF-8 名称池，零托管 string 分配
                    var pooled = namePool.AddUtf16LeBytes(nameUtf16);

                    int nodeId = nodes.Count;
                    nodes.Add(new ScanNodeRecord
                    {
                        NodeId = nodeId,
                        ParentNodeId = parentNodeId,
                        NamePageIndex = pooled.PageIndex,
                        NameOffset = pooled.Offset,
                        NameLength = pooled.Length,
                        Flags = entry.Flags,
                        LogicalBytes = entry.LogicalBytes,
                        AllocatedBytes = entry.AllocatedBytes,
                        UniqueAllocatedBytes = entry.AllocatedBytes,
                        FileCount = entry.IsDirectory ? 0 : 1,
                        ReparseTag = entry.ReparseTag,
                        LastWriteUtcTicks = entry.LastWriteUtcTicks,
                        LinkCount = 1
                    });

                    // 严格禁止跟随目录重解析点（符号链接 / Junction / 挂载点）；仅可遍历子目录构造路径
                    if (entry.IsDirectory && !entry.IsReparsePoint)
                    {
                        string childName = Encoding.Unicode.GetString(nameUtf16);
                        string childPath = CombineChildPath(parentPath, childName);
                        Interlocked.Increment(ref pendingDirectories);
                        queue.Enqueue((nodeId, childPath));
                        childDirsEnqueued++;
                    }
                }

                processed += batch.Count;
                snapshotProcessed = processed;
                if (snapshotProcessed >= nextProgressThreshold)
                {
                    nextProgressThreshold = snapshotProcessed + 2048;
                    shouldReport = true;
                }
            }

            if (childDirsEnqueued > 0)
            {
                workAvailable.Release(childDirsEnqueued);
            }

            if (shouldReport)
            {
                progress?.Report(new ScanProgress(
                    ScanPhase.ReadingMft,
                    ScanBackendKind.DirectoryWalkFallback,
                    snapshotProcessed,
                    null,
                    $"并发目录扫描（{mediaTag}）：已遍历 {snapshotProcessed:N0} 项…"));
            }
        }

        void WorkerLoop()
        {
            bool bgPriorityEnabled = options.UseBackgroundIoPriority && Win32DirectoryEnumerator.BeginBackgroundIoMode();
            byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            var batchBuffer = new List<ParsedDirEntrySlice>(256);
            int dirsHandled = 0;

            try
            {
                while (true)
                {
                    if (ct.IsCancellationRequested)
                    {
                        Volatile.Write(ref cancelled, true);
                        break;
                    }

                    try
                    {
                        workAvailable.Wait(doneCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        if (ct.IsCancellationRequested)
                        {
                            Volatile.Write(ref cancelled, true);
                        }
                        break;
                    }

                    if (!queue.TryDequeue(out var task))
                    {
                        if (Volatile.Read(ref pendingDirectories) <= 0)
                        {
                            break;
                        }
                        continue;
                    }

                    try
                    {
                        if (ct.IsCancellationRequested)
                        {
                            Volatile.Write(ref cancelled, true);
                            break;
                        }

                        DirectoryEnumStatus enumStatus = Win32DirectoryEnumerator.EnumerateDirectoryChunked(
                            task.Path,
                            isRootDirectory: task.NodeId == 0,
                            ref buffer,
                            batchBuffer,
                            (buf, entries) => CommitBatch(task.NodeId, task.Path, buf, entries),
                            ct);

                        if (enumStatus == DirectoryEnumStatus.Cancelled)
                        {
                            Volatile.Write(ref cancelled, true);
                            break;
                        }

                        if (enumStatus is DirectoryEnumStatus.AccessDenied or DirectoryEnumStatus.IoError or DirectoryEnumStatus.PartialError)
                        {
                            lock (commitLock)
                            {
                                errorCount++;
                                MarkIncomplete(nodes, task.NodeId, ref incompleteCount);
                                if (task.NodeId == 0 && enumStatus == DirectoryEnumStatus.AccessDenied)
                                {
                                    rootDenied = true;
                                }
                            }
                        }

                        dirsHandled++;
                        if (dirsHandled % yieldInterval == 0)
                        {
                            if (throttleHdd)
                            {
                                // 机械硬盘或未知介质：周期性微让步，避免连续队列把磁盘活动时间打满
                                Thread.Sleep(1);
                            }
                            else
                            {
                                Thread.Yield();
                            }
                        }
                    }
                    finally
                    {
                        if (Interlocked.Decrement(ref pendingDirectories) == 0)
                        {
                            try
                            {
                                doneCts.Cancel();
                            }
                            catch (ObjectDisposedException)
                            {
                            }
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                if (bgPriorityEnabled)
                {
                    Win32DirectoryEnumerator.EndBackgroundIoMode();
                }
            }
        }

        // 即使单线程也放到独立线程执行，确保 THREAD_MODE_BACKGROUND_BEGIN 绝不污染 .NET ThreadPool 线程
        var threads = new Thread[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            threads[i] = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"DiskWalk-{i}"
            };
            threads[i].Start();
        }

        for (int i = 0; i < workerCount; i++)
        {
            threads[i].Join();
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
                ? "已取消目录遍历扫描"
                : $"目录扫描完成（{mediaTag}）：{nodeArray.Length:N0} 项，访问受限 {errorCount} 处"));

        return new ScanSnapshot
        {
            Volume = volume,
            Backend = ScanBackendKind.DirectoryWalkFallback,
            FallbackReason = fallbackReason ?? $"使用并发低占用目录遍历（{mediaTag}，非 MFT）",
            Coverage = coverage,
            StartedAtUtc = started,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            PolicyVersion = MigrationSafetyPolicy.CurrentPolicyVersion,
            ErrorCount = errorCount,
            IncompleteNodeCount = incompleteCount,
            MetadataBytes = 0,
            UnknownBytes = 0,
            RequiresLiveReverification = true,
            RootNodeId = 0,
            OrphanContainerNodeId = -1,
            ScanRootPath = rootPath,
            Nodes = nodeArray,
            CsrOffsets = csrOffsets,
            CsrChildren = csrChildren,
            NamePool = namePool
        };
    }

    internal static int ResolveWorkerCount(StorageMediaKind mediaKind, int? configuredParallelism)
    {
        if (configuredParallelism.HasValue)
        {
            return Math.Clamp(configuredParallelism.Value, 1, 8);
        }

        // 不打满磁盘的保守并发策略：SSD 限制 2..4 线程，HDD 或未知介质严格 1 线程顺序遍历
        return mediaKind == StorageMediaKind.Ssd
            ? Math.Clamp(Environment.ProcessorCount / 4, 2, 4)
            : 1;
    }

    private static string CombineChildPath(string parentPath, string childName)
    {
        if (parentPath.Length > 0 && (parentPath[^1] == '\\' || parentPath[^1] == '/'))
        {
            return parentPath + childName;
        }
        return parentPath + "\\" + childName;
    }

    private static void MarkIncomplete(List<ScanNodeRecord> nodes, int nodeId, ref int incompleteCount)
    {
        int curr = nodeId;
        int guard = 0;
        while (curr >= 0 && curr < nodes.Count && guard++ < 512)
        {
            ScanNodeRecord rec = nodes[curr];
            if ((rec.Flags & ScanNodeFlags.IsIncomplete) == 0)
            {
                rec.Flags |= ScanNodeFlags.IsIncomplete;
                nodes[curr] = rec;
                incompleteCount++;
            }
            curr = rec.ParentNodeId;
        }
    }
}

internal enum DirectoryEnumStatus
{
    Ok,
    AccessDenied,
    IoError,
    PartialError,
    Cancelled
}

/// <summary>
/// 指向内核返回缓冲区的纯值类型条目切片（零托管 string 分配）。
/// </summary>
internal readonly struct ParsedDirEntrySlice
{
    public readonly int NameOffset;
    public readonly int NameByteLength;
    public readonly ScanNodeFlags Flags;
    public readonly long LogicalBytes;
    public readonly long AllocatedBytes;
    public readonly uint ReparseTag;
    public readonly long LastWriteUtcTicks;

    public ParsedDirEntrySlice(
        int nameOffset,
        int nameByteLength,
        ScanNodeFlags flags,
        long logicalBytes,
        long allocatedBytes,
        uint reparseTag,
        long lastWriteUtcTicks)
    {
        NameOffset = nameOffset;
        NameByteLength = nameByteLength;
        Flags = flags;
        LogicalBytes = logicalBytes;
        AllocatedBytes = allocatedBytes;
        ReparseTag = reparseTag;
        LastWriteUtcTicks = lastWriteUtcTicks;
    }

    public bool IsDirectory => (Flags & ScanNodeFlags.IsDirectory) != 0;
    public bool IsReparsePoint => (Flags & ScanNodeFlags.IsReparsePoint) != 0;
}

/// <summary>
/// 纯 Safe C# 封装 Win32 <c>GetFileInformationByHandleEx(FileFullDirectoryInfo)</c> 目录枚举与缓冲解包。
/// </summary>
internal static class Win32DirectoryEnumerator
{
    private const uint FILE_LIST_DIRECTORY = 0x00000001;
    private const uint FILE_SHARE_ALL = 0x00000007; // READ | WRITE | DELETE
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    // FILE_INFO_BY_HANDLE_CLASS.FileFullDirectoryInfo = 14 (0x0E)
    private const int FileFullDirectoryInfoClass = 14;
    // FILE_FULL_DIR_INFO 固定头字节数：FileName 起始于偏移 68
    internal const int FullDirInfoHeaderSize = 68;

    private const int ERROR_FILE_NOT_FOUND = 2;
    private const int ERROR_PATH_NOT_FOUND = 3;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_NO_MORE_FILES = 18;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const int ERROR_MORE_DATA = 234;

    internal const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    internal const uint FILE_ATTRIBUTE_SPARSE_FILE = 0x00000200;
    internal const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;
    internal const uint FILE_ATTRIBUTE_COMPRESSED = 0x00000800;
    internal const uint FILE_ATTRIBUTE_OFFLINE = 0x00001000;
    internal const uint FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x00400000;

    private const int THREAD_MODE_BACKGROUND_BEGIN = 0x00010000;
    private const int THREAD_MODE_BACKGROUND_END = 0x00020000;

    private const int MaxExpandedBufferBytes = 512 * 1024;

    public static bool BeginBackgroundIoMode() =>
        SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN);

    public static void EndBackgroundIoMode() =>
        SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_END);

    public static DirectoryEnumStatus EnumerateDirectoryChunked(
        string directoryPath,
        bool isRootDirectory,
        ref byte[] buffer,
        List<ParsedDirEntrySlice> batchBuffer,
        Action<byte[], List<ParsedDirEntrySlice>> onBatch,
        CancellationToken ct)
    {
        string extendedPath = ToExtendedPath(directoryPath);
        uint flags = FILE_FLAG_BACKUP_SEMANTICS;
        if (!isRootDirectory)
        {
            // 非根子目录打开时携带 OPEN_REPARSE_POINT，彻底消除枚举后被替换为符号链接的 TOCTOU 跟随风险
            flags |= FILE_FLAG_OPEN_REPARSE_POINT;
        }

        using SafeFileHandle handle = CreateFileW(
            extendedPath,
            FILE_LIST_DIRECTORY,
            FILE_SHARE_ALL,
            IntPtr.Zero,
            OPEN_EXISTING,
            flags,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            return err == ERROR_ACCESS_DENIED
                ? DirectoryEnumStatus.AccessDenied
                : DirectoryEnumStatus.IoError;
        }

        bool producedAny = false;
        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                return DirectoryEnumStatus.Cancelled;
            }

            if (GetFileInformationByHandleEx(handle, FileFullDirectoryInfoClass, buffer, (uint)buffer.Length))
            {
                batchBuffer.Clear();
                bool parsedOk = ParseFullDirectoryInfoBuffer(buffer.AsSpan(), batchBuffer);
                if (batchBuffer.Count > 0)
                {
                    producedAny = true;
                    onBatch(buffer, batchBuffer);
                }

                if (!parsedOk)
                {
                    return DirectoryEnumStatus.PartialError;
                }
                continue;
            }

            int err = Marshal.GetLastWin32Error();
            if (err == ERROR_NO_MORE_FILES)
            {
                return DirectoryEnumStatus.Ok;
            }

            if ((err == ERROR_MORE_DATA || err == ERROR_INSUFFICIENT_BUFFER) && buffer.Length < MaxExpandedBufferBytes)
            {
                int newSize = Math.Min(buffer.Length * 2, MaxExpandedBufferBytes);
                ArrayPool<byte>.Shared.Return(buffer);
                buffer = ArrayPool<byte>.Shared.Rent(newSize);
                continue;
            }

            if (err == ERROR_ACCESS_DENIED)
            {
                return DirectoryEnumStatus.AccessDenied;
            }

            if (err is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND)
            {
                return DirectoryEnumStatus.IoError;
            }

            return producedAny ? DirectoryEnumStatus.PartialError : DirectoryEnumStatus.IoError;
        }
    }

    /// <summary>
    /// 纯 Safe C# 解包 <c>FILE_FULL_DIR_INFO</c> 连续缓冲区（供生产扫描与单元测试复用）。
    /// </summary>
    internal static bool ParseFullDirectoryInfoBuffer(ReadOnlySpan<byte> buffer, List<ParsedDirEntrySlice> results)
    {
        int offset = 0;
        int guard = 0;

        while (offset >= 0 && offset + FullDirInfoHeaderSize <= buffer.Length)
        {
            if (++guard > 1_000_000)
            {
                return false;
            }

            uint nextEntryOffset = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset, 4));
            long lastWriteFileTime = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset + 24, 8));
            long endOfFile = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset + 40, 8));
            long allocationSize = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset + 48, 8));
            uint fileAttributes = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset + 56, 4));
            uint fileNameLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset + 60, 4));
            uint eaSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset + 64, 4));

            int nameStart = offset + FullDirInfoHeaderSize;
            if (fileNameLength > int.MaxValue ||
                (fileNameLength & 1) != 0 ||
                nameStart + (int)fileNameLength > buffer.Length)
            {
                return false;
            }

            ReadOnlySpan<byte> nameUtf16 = buffer.Slice(nameStart, (int)fileNameLength);
            if (!IsDotOrDotDot(nameUtf16))
            {
                results.Add(ClassifyEntrySlice(
                    nameStart,
                    (int)fileNameLength,
                    fileAttributes,
                    endOfFile,
                    allocationSize,
                    eaSize,
                    lastWriteFileTime));
            }

            if (nextEntryOffset == 0)
            {
                return true;
            }

            if (nextEntryOffset < FullDirInfoHeaderSize || nextEntryOffset > (uint)(buffer.Length - offset))
            {
                return false;
            }

            offset += (int)nextEntryOffset;
        }

        return false;
    }

    internal static ParsedDirEntrySlice ClassifyEntrySlice(
        int nameOffset,
        int nameByteLength,
        uint fileAttributes,
        long endOfFile,
        long allocationSize,
        uint eaSize,
        long lastWriteFileTime)
    {
        bool isDir = (fileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
        bool isReparse = (fileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0;
        bool isSparseOrCompressed = (fileAttributes & (FILE_ATTRIBUTE_SPARSE_FILE | FILE_ATTRIBUTE_COMPRESSED)) != 0;
        uint reparseTag = isReparse ? eaSize : 0u;

        bool isWof = isReparse && reparseTag == NtfsMftRecordParser.IoReparseTagWof;
        bool isCloudTag = isReparse && (reparseTag & NtfsMftRecordParser.IoReparseTagCloudMask) == NtfsMftRecordParser.IoReparseTagCloudBase;
        bool hasOfflineOrRecall = (fileAttributes & (FILE_ATTRIBUTE_OFFLINE | FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS)) != 0;
        bool isCloud = isCloudTag || hasOfflineOrRecall;

        ScanNodeFlags flags = ScanNodeFlags.None;
        if (isDir) flags |= ScanNodeFlags.IsDirectory;
        if (isReparse) flags |= ScanNodeFlags.IsReparsePoint;
        if (isWof) flags |= ScanNodeFlags.IsWofCompressed;
        if (isCloud) flags |= ScanNodeFlags.IsCloudPlaceholder;
        if (isSparseOrCompressed) flags |= ScanNodeFlags.IsSparseOrCompressed;

        long logicalBytes = isDir ? 0 : Math.Max(0L, endOfFile);
        long allocatedBytes;
        if (isDir)
        {
            allocatedBytes = 0;
        }
        else if (isCloud && hasOfflineOrRecall)
        {
            // 未水合云盘占位文件：本地不占真实物理空间，避免高估可释放空间
            allocatedBytes = 0;
        }
        else
        {
            allocatedBytes = Math.Max(0L, allocationSize);
        }

        long writeTicks = FileTimeToUtcTicks(lastWriteFileTime);
        return new ParsedDirEntrySlice(nameOffset, nameByteLength, flags, logicalBytes, allocatedBytes, reparseTag, writeTicks);
    }

    internal static string ToExtendedPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return @"\\?\UNC\" + path.AsSpan(2).ToString();
        }
        return @"\\?\" + path;
    }

    private static bool IsDotOrDotDot(ReadOnlySpan<byte> utf16Le)
    {
        if (utf16Le.Length == 2)
        {
            return utf16Le[0] == '.' && utf16Le[1] == 0;
        }
        if (utf16Le.Length == 4)
        {
            return utf16Le[0] == '.' && utf16Le[1] == 0 &&
                   utf16Le[2] == '.' && utf16Le[3] == 0;
        }
        return false;
    }

    private static long FileTimeToUtcTicks(long fileTime)
    {
        if (fileTime <= 0) return 0;
        try
        {
            return DateTime.FromFileTimeUtc(fileTime).Ticks;
        }
        catch
        {
            return 0;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle hFile,
        int fileInformationClass,
        [Out] byte[] lpFileInformation,
        uint dwBufferSize);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(IntPtr hThread, int nPriority);
}

/// <summary>
/// 免管理员权限探测卷所在磁盘是否存在机械寻道惩罚（SSD vs HDD），用于选择不打满磁盘的温和并发度。
/// </summary>
internal static class StorageMediaDetector
{
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const uint StorageDeviceSeekPenaltyProperty = 7;
    private const uint PropertyStandardQuery = 0;
    private const uint FILE_SHARE_ALL = 0x00000007;
    private const uint OPEN_EXISTING = 3;

    public static StorageMediaKind Detect(string driveRoot)
    {
        if (string.IsNullOrWhiteSpace(driveRoot) || driveRoot.Length < 2 || driveRoot[1] != ':')
        {
            return StorageMediaKind.Unknown;
        }

        string volumeDevicePath = $@"\\.\{driveRoot[0]}:";
        try
        {
            // dwDesiredAccess = 0：仅查询设备属性，普通用户权限即可成功且绝不写入卷
            using SafeFileHandle handle = CreateFileW(
                volumeDevicePath,
                0,
                FILE_SHARE_ALL,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                return StorageMediaKind.Unknown;
            }

            var query = new StoragePropertyQuery
            {
                PropertyId = StorageDeviceSeekPenaltyProperty,
                QueryType = PropertyStandardQuery
            };

            if (!DeviceIoControlSeekPenalty(
                    handle,
                    IOCTL_STORAGE_QUERY_PROPERTY,
                    ref query,
                    (uint)Marshal.SizeOf<StoragePropertyQuery>(),
                    out DeviceSeekPenaltyDescriptor descriptor,
                    (uint)Marshal.SizeOf<DeviceSeekPenaltyDescriptor>(),
                    out _,
                    IntPtr.Zero))
            {
                return StorageMediaKind.Unknown;
            }

            return descriptor.IncursSeekPenalty == 0
                ? StorageMediaKind.Ssd
                : StorageMediaKind.Hdd;
        }
        catch
        {
            return StorageMediaKind.Unknown;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public uint PropertyId;
        public uint QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceSeekPenaltyDescriptor
    {
        public uint Version;
        public uint Size;
        public byte IncursSeekPenalty;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlSeekPenalty(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref StoragePropertyQuery lpInBuffer,
        uint nInBufferSize,
        out DeviceSeekPenaltyDescriptor lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);
}
