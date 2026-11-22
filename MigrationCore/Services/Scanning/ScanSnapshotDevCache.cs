using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using MigrationCore.Models.Scanning;

namespace MigrationCore.Services.Scanning;

public sealed record DevScanCacheHeader(
    string FilePath,
    Guid ScanId,
    string DriveRoot,
    string VolumeTitle,
    DateTimeOffset CompletedAtUtc,
    long UsedBytes,
    int NodeCount,
    int LargeDirCount,
    int L1Count,
    long L1Bytes,
    ScanBackendKind Backend,
    string PolicyVersion);

/// <summary>
/// 仅限开发调试模式（#if DEBUG）使用的只读扫描快照临时磁盘缓存。
/// 采用“非压缩快速头部 + Deflate Fastest 块压缩扁平结构数组”格式：
/// - 启动枚举仅读 ~300 字节头部（&lt;1ms），不占用内存；
/// - 点击缓存项秒级恢复百万级 MFT 快照，且强制标记 RequiresLiveReverification = true，绝不绕过执行前实时复核。
/// </summary>
public static class ScanSnapshotDevCache
{
    private const uint Magic = 0x4D465443; // "MFTC"
    private const int FormatVersion = 1;

    public static string DefaultCacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MoveWithSymlinkWPF",
        "DevCache");

    public static string GetCacheFilePath(string driveRoot, string? cacheDirectory = null)
    {
        string dir = cacheDirectory ?? DefaultCacheDirectory;
        char letter = !string.IsNullOrWhiteSpace(driveRoot) && char.IsLetter(driveRoot[0])
            ? char.ToUpperInvariant(driveRoot[0])
            : 'X';
        return Path.Combine(dir, $"mft_dev_cache_{letter}.bin");
    }

    public static string Save(
        ScanSnapshot snapshot,
        string volumeTitle,
        int largeDirCount,
        int l1Count,
        long l1Bytes,
        string? cacheDirectory = null)
    {
        string dir = cacheDirectory ?? DefaultCacheDirectory;
        Directory.CreateDirectory(dir);
        string targetPath = GetCacheFilePath(snapshot.Volume.DriveRoot, dir);
        string tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 262144))
            using (var headerWriter = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true))
            {
                // 1. 非压缩轻量头部（供启动秒级枚举展示）
                headerWriter.Write(Magic);
                headerWriter.Write(FormatVersion);
                headerWriter.Write(Unsafe.SizeOf<ScanNodeRecord>());
                headerWriter.Write(snapshot.ScanId.ToByteArray());
                headerWriter.Write(snapshot.Volume.DriveRoot);
                headerWriter.Write(volumeTitle);
                headerWriter.Write(snapshot.StartedAtUtc.UtcTicks);
                headerWriter.Write(snapshot.CompletedAtUtc.UtcTicks);
                headerWriter.Write(snapshot.Volume.UsedBytes);
                headerWriter.Write(snapshot.Nodes.Length);
                headerWriter.Write(largeDirCount);
                headerWriter.Write(l1Count);
                headerWriter.Write(l1Bytes);
                headerWriter.Write((int)snapshot.Backend);
                headerWriter.Write(snapshot.PolicyVersion);
                headerWriter.Flush();

                // 2. Deflate Fastest 压缩体（顺序写入扁平 struct / CSR / UTF-8 名称池）
                using var deflate = new DeflateStream(fs, CompressionLevel.Fastest, leaveOpen: true);
                using var bodyWriter = new BinaryWriter(deflate, Encoding.UTF8, leaveOpen: true);

                bodyWriter.Write((int)snapshot.Coverage);
                bodyWriter.Write(snapshot.FallbackReason ?? string.Empty);
                bodyWriter.Write(snapshot.ErrorCount);
                bodyWriter.Write(snapshot.IncompleteNodeCount);
                bodyWriter.Write(snapshot.MetadataBytes);
                bodyWriter.Write(snapshot.UnknownBytes);
                WriteNullableInt64(bodyWriter, snapshot.UsnStartJournalId);
                WriteNullableInt64(bodyWriter, snapshot.UsnStartNextUsn);
                WriteNullableInt64(bodyWriter, snapshot.UsnEndNextUsn);
                bodyWriter.Write(snapshot.RootNodeId);
                bodyWriter.Write(snapshot.OrphanContainerNodeId);
                bodyWriter.Write(snapshot.ScanRootPath ?? string.Empty);

                // ScanVolumeInfo
                ScanVolumeInfo v = snapshot.Volume;
                bodyWriter.Write(v.VolumeLabel);
                bodyWriter.Write(v.VolumeGuid);
                bodyWriter.Write(v.VolumeSerial);
                bodyWriter.Write(v.FileSystem);
                bodyWriter.Write(v.TotalBytes);
                bodyWriter.Write(v.FreeBytes);
                bodyWriter.Write(v.SupportsRawMft);
                bodyWriter.Write(v.CapabilityReason);

                // 扁平数组块写入（分片写入避免单次超大临时 buffer）
                bodyWriter.Write(snapshot.Nodes.Length);
                WriteStructArrayChunked(deflate, snapshot.Nodes);

                bodyWriter.Write(snapshot.CsrOffsets.Length);
                WriteStructArrayChunked(deflate, snapshot.CsrOffsets);

                bodyWriter.Write(snapshot.CsrChildren.Length);
                WriteStructArrayChunked(deflate, snapshot.CsrChildren);

                snapshot.NamePool.WriteTo(bodyWriter);
                bodyWriter.Flush();
            }

            File.Move(tempPath, targetPath, overwrite: true);
            return targetPath;
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    public static IReadOnlyList<DevScanCacheHeader> ListValidHeaders(string expectedPolicyVersion, TimeSpan maxAge, string? cacheDirectory = null)
    {
        string dir = cacheDirectory ?? DefaultCacheDirectory;
        if (!Directory.Exists(dir)) return [];

        var list = new List<DevScanCacheHeader>();
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - maxAge;
        foreach (string file in Directory.EnumerateFiles(dir, "mft_dev_cache_*.bin"))
        {
            if (TryReadHeader(file, out DevScanCacheHeader? header)
                && header is not null
                && header.PolicyVersion == expectedPolicyVersion
                && header.CompletedAtUtc >= cutoff)
            {
                list.Add(header);
            }
        }

        return list.OrderByDescending(h => h.CompletedAtUtc).ToArray();
    }

    public static bool TryReadHeader(string filePath, out DevScanCacheHeader? header)
    {
        header = null;
        if (!File.Exists(filePath)) return false;

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);
            header = ReadHeaderCore(filePath, reader, out _);
            return header is not null;
        }
        catch
        {
            return false;
        }
    }

    public static ScanSnapshot Load(string filePath, out DevScanCacheHeader header)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 262144);
        using var headerReader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

        DevScanCacheHeader? parsedHeader = ReadHeaderCore(filePath, headerReader, out DateTimeOffset startedAtUtc)
            ?? throw new InvalidDataException("开发扫描缓存头部不兼容或已损坏。");
        header = parsedHeader;

        using var deflate = new DeflateStream(fs, CompressionMode.Decompress, leaveOpen: true);
        using var bodyReader = new BinaryReader(deflate, Encoding.UTF8, leaveOpen: true);

        var coverage = (ScanCoverageStatus)bodyReader.ReadInt32();
        string fallbackReason = bodyReader.ReadString();
        int errorCount = bodyReader.ReadInt32();
        int incompleteCount = bodyReader.ReadInt32();
        long metadataBytes = bodyReader.ReadInt64();
        long unknownBytes = bodyReader.ReadInt64();
        long? usnJournalId = ReadNullableInt64(bodyReader);
        long? usnStart = ReadNullableInt64(bodyReader);
        long? usnEnd = ReadNullableInt64(bodyReader);
        int rootNodeId = bodyReader.ReadInt32();
        int orphanContainerNodeId = bodyReader.ReadInt32();
        string scanRootPath = bodyReader.ReadString();

        string volLabel = bodyReader.ReadString();
        string volGuid = bodyReader.ReadString();
        uint volSerial = bodyReader.ReadUInt32();
        string fileSystem = bodyReader.ReadString();
        long totalBytes = bodyReader.ReadInt64();
        long freeBytes = bodyReader.ReadInt64();
        bool supportsRawMft = bodyReader.ReadBoolean();
        string capReason = bodyReader.ReadString();

        var volume = new ScanVolumeInfo(
            header.DriveRoot,
            volLabel,
            volGuid,
            volSerial,
            fileSystem,
            totalBytes,
            freeBytes,
            supportsRawMft,
            capReason);

        int nodeCount = bodyReader.ReadInt32();
        ScanNodeRecord[] nodes = ReadStructArrayChunked<ScanNodeRecord>(deflate, nodeCount);

        int offsetCount = bodyReader.ReadInt32();
        int[] csrOffsets = ReadStructArrayChunked<int>(deflate, offsetCount);

        int childCount = bodyReader.ReadInt32();
        int[] csrChildren = ReadStructArrayChunked<int>(deflate, childCount);

        Utf8NamePool namePool = Utf8NamePool.ReadFrom(bodyReader);

        return new ScanSnapshot
        {
            ScanId = header.ScanId,
            Volume = volume,
            Backend = header.Backend,
            Coverage = coverage,
            FallbackReason = string.IsNullOrEmpty(fallbackReason) ? null : fallbackReason,
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = header.CompletedAtUtc,
            PolicyVersion = header.PolicyVersion,
            ErrorCount = errorCount,
            IncompleteNodeCount = incompleteCount,
            MetadataBytes = metadataBytes,
            UnknownBytes = unknownBytes,
            UsnStartJournalId = usnJournalId,
            UsnStartNextUsn = usnStart,
            UsnEndNextUsn = usnEnd,
            // 缓存快照始终强制执行前逐项实时复核，不凭旧缓存跳过安全检查
            RequiresLiveReverification = true,
            RootNodeId = rootNodeId,
            OrphanContainerNodeId = orphanContainerNodeId,
            ScanRootPath = string.IsNullOrEmpty(scanRootPath) ? null : scanRootPath,
            Nodes = nodes,
            CsrOffsets = csrOffsets,
            CsrChildren = csrChildren,
            NamePool = namePool
        };
    }

    public static void ClearAll(string? cacheDirectory = null)
    {
        string dir = cacheDirectory ?? DefaultCacheDirectory;
        if (!Directory.Exists(dir)) return;
        foreach (string file in Directory.EnumerateFiles(dir, "mft_dev_cache_*.bin"))
        {
            try { File.Delete(file); } catch { }
        }
    }

    private static DevScanCacheHeader? ReadHeaderCore(string filePath, BinaryReader reader, out DateTimeOffset startedAtUtc)
    {
        startedAtUtc = default;
        if (reader.ReadUInt32() != Magic) return null;
        if (reader.ReadInt32() != FormatVersion) return null;
        if (reader.ReadInt32() != Unsafe.SizeOf<ScanNodeRecord>()) return null;

        Guid scanId = new(reader.ReadBytes(16));
        string driveRoot = reader.ReadString();
        string volumeTitle = reader.ReadString();
        startedAtUtc = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        DateTimeOffset completedAtUtc = new(reader.ReadInt64(), TimeSpan.Zero);
        long usedBytes = reader.ReadInt64();
        int nodeCount = reader.ReadInt32();
        int largeDirCount = reader.ReadInt32();
        int l1Count = reader.ReadInt32();
        long l1Bytes = reader.ReadInt64();
        var backend = (ScanBackendKind)reader.ReadInt32();
        string policyVersion = reader.ReadString();

        return new DevScanCacheHeader(
            filePath,
            scanId,
            driveRoot,
            volumeTitle,
            completedAtUtc,
            usedBytes,
            nodeCount,
            largeDirCount,
            l1Count,
            l1Bytes,
            backend,
            policyVersion);
    }

    private static void WriteNullableInt64(BinaryWriter writer, long? value)
    {
        writer.Write(value.HasValue);
        if (value.HasValue) writer.Write(value.Value);
    }

    private static long? ReadNullableInt64(BinaryReader reader) =>
        reader.ReadBoolean() ? reader.ReadInt64() : null;

    private static void WriteStructArrayChunked<T>(Stream stream, T[] array) where T : unmanaged
    {
        if (array.Length == 0) return;
        int elemSize = Unsafe.SizeOf<T>();
        int chunkElems = Math.Max(1, (1024 * 1024) / elemSize);
        int offset = 0;
        while (offset < array.Length)
        {
            int count = Math.Min(chunkElems, array.Length - offset);
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(array.AsSpan(offset, count));
            stream.Write(bytes);
            offset += count;
        }
    }

    private static T[] ReadStructArrayChunked<T>(Stream stream, int length) where T : unmanaged
    {
        if (length < 0 || length > 100_000_000) throw new InvalidDataException("缓存数组长度异常。");
        if (length == 0) return [];
        var array = new T[length];
        int elemSize = Unsafe.SizeOf<T>();
        int chunkElems = Math.Max(1, (1024 * 1024) / elemSize);
        int offset = 0;
        while (offset < length)
        {
            int count = Math.Min(chunkElems, length - offset);
            Span<byte> bytes = MemoryMarshal.AsBytes(array.AsSpan(offset, count));
            int filled = 0;
            while (filled < bytes.Length)
            {
                int n = stream.Read(bytes[filled..]);
                if (n <= 0) throw new EndOfStreamException("扫描缓存数据流意外截断。");
                filled += n;
            }
            offset += count;
        }
        return array;
    }
}
