using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using MigrationCore.Models.Scanning;
using MigrationCore.Services.Safety;

namespace MigrationCore.Services.Scanning;

/// <summary>
/// 本地 NTFS 卷只读原始 MFT 扫描器（零内核驱动、零 unsafe 指针、绝不写卷或创建 USN 日志）。
/// 通过 FSCTL_GET_NTFS_VOLUME_DATA 读取卷参数，扇区对齐读取记录 0 的完整 $DATA run list（支持碎片化 MFT 与 $ATTRIBUTE_LIST），
/// 顺序分块解析全部 MFT 记录并输出紧凑 ScanSnapshot。
/// </summary>
public sealed class NtfsMftScanner
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareReadWriteDelete = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint FileFlagSequentialScan = 0x08000000;

    private const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;
    private const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4;
    private const int ChunkBytes = 4 * 1024 * 1024; // 4 MiB 顺序对齐块

    public Task<ScanSnapshot> ScanAsync(
        ScanVolumeInfo volume,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(() => ScanCore(volume, progress, ct), ct);
    }

    private static ScanSnapshot ScanCore(
        ScanVolumeInfo volume,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        ct.ThrowIfCancellationRequested();

        progress?.Report(new ScanProgress(ScanPhase.ProbingVolume, ScanBackendKind.RawNtfsMft, 0, null, $"正在以只读方式打开卷 {volume.DriveRoot}…"));

        string devicePath = @"\\.\" + volume.DriveRoot.TrimEnd('\\');
        using SafeFileHandle volumeHandle = CreateFileW(
            devicePath,
            GenericRead,
            FileShareReadWriteDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagSequentialScan,
            IntPtr.Zero);

        if (volumeHandle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err, $"无法以只读模式打开原始卷 {volume.DriveRoot}（错误码 {err}，请确认具备管理员权限且卷已解锁）。");
        }

        if (!DeviceIoControlVolumeData(volumeHandle, FSCTL_GET_NTFS_VOLUME_DATA, IntPtr.Zero, 0,
                out NtfsVolumeDataBuffer volData, (uint)Marshal.SizeOf<NtfsVolumeDataBuffer>(), out _, IntPtr.Zero))
        {
            int err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err, $"FSCTL_GET_NTFS_VOLUME_DATA 失败（错误码 {err}），该卷可能不是可读 NTFS 卷。");
        }

        int bytesPerSector = checked((int)volData.BytesPerSector);
        int bytesPerCluster = checked((int)volData.BytesPerCluster);
        int recordSize = checked((int)volData.BytesPerFileRecordSegment);
        if (bytesPerSector < 512 || bytesPerCluster < bytesPerSector || recordSize < 512 || recordSize % bytesPerSector != 0)
        {
            throw new IOException($"不支持的 NTFS 卷几何参数：Sector={bytesPerSector}, Cluster={bytesPerCluster}, Record={recordSize}");
        }

        // 只读查询 USN 水位（若日志不存在或不可读，绝不创建日志，直接标注需实时复核）
        bool hasUsnStart = TryQueryUsnJournal(volumeHandle, out long usnJournalId, out long usnStartNext);
        bool requiresLiveReverify = !hasUsnStart;

        // 读取记录 0（$MFT 自身），提取完整 $DATA run list（含碎片化 MFT 及 $ATTRIBUTE_LIST 扩展记录）
        long mftStartOffset = checked(volData.MftStartLcn * bytesPerCluster);
        List<MftDataRun> mftRuns = ReadMftSelfRuns(volumeHandle, mftStartOffset, bytesPerSector, bytesPerCluster, recordSize, ct);
        if (mftRuns.Count == 0)
        {
            throw new IOException("无法从 $MFT 记录 0 解析出有效的数据区段（run list）。");
        }

        long estimatedTotalRecords = volData.MftValidDataLength > 0 ? volData.MftValidDataLength / recordSize : 0;
        var parsedRecords = new Dictionary<ulong, ParsedMftRecord>(
            (int)Math.Clamp(estimatedTotalRecords / 2, 1024, 2_000_000));

        int alignedChunkSize = Math.Max(recordSize, (ChunkBytes / recordSize) * recordSize);
        if (alignedChunkSize % bytesPerSector != 0)
        {
            alignedChunkSize = ((alignedChunkSize + bytesPerSector - 1) / bytesPerSector) * bytesPerSector;
        }
        byte[] buffer = new byte[alignedChunkSize];

        ulong currentRecordNumber = 0;
        long processedRecords = 0;
        int corruptRecords = 0;
        bool cancelled = false;
        long maxMftBytes = volData.MftValidDataLength > 0 ? volData.MftValidDataLength : long.MaxValue;
        long logicalRunStartByte = 0;

        progress?.Report(new ScanProgress(ScanPhase.ReadingMft, ScanBackendKind.RawNtfsMft, 0, estimatedTotalRecords, "正在顺序读取 MFT 元数据…"));

        foreach (MftDataRun run in mftRuns)
        {
            if (ct.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            long runBytes = checked(run.ClusterCount * bytesPerCluster);
            if (logicalRunStartByte >= maxMftBytes) break;
            long effectiveRunBytes = Math.Min(runBytes, maxMftBytes - logicalRunStartByte);
            // 按每个 run 的逻辑字节起始位置精确锚定 MFT 记录号，避免短读或非整记录边界引起后续 run 记录号全局偏移
            currentRecordNumber = (ulong)(logicalRunStartByte / recordSize);

            if (run.IsSparse)
            {
                logicalRunStartByte += effectiveRunBytes;
                continue;
            }

            long runDiskOffset = checked(run.Lcn * bytesPerCluster);
            long runOffset = 0;
            long alignedRunLimit = (effectiveRunBytes / recordSize) * recordSize;

            while (runOffset < alignedRunLimit)
            {
                if (ct.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                int toRead = (int)Math.Min(buffer.Length, alignedRunLimit - runOffset);
                toRead = (toRead / recordSize) * recordSize;
                if (toRead <= 0) break;

                int actualRead = ReadAlignedBytes(volumeHandle, runDiskOffset + runOffset, buffer, toRead);
                int completeRecords = actualRead / recordSize;

                for (int i = 0; i < completeRecords; i++)
                {
                    Span<byte> slice = buffer.AsSpan(i * recordSize, recordSize);
                    if (!NtfsMftRecordParser.ApplyUsaFixup(slice, bytesPerSector))
                    {
                        if (slice.Length >= 4 && slice[0] == (byte)'F' && slice[1] == (byte)'I' && slice[2] == (byte)'L' && slice[3] == (byte)'E')
                        {
                            corruptRecords++;
                        }
                        currentRecordNumber++;
                        continue;
                    }

                    ParsedMftRecord parsed = NtfsMftRecordParser.ParseFixedUpRecord(slice, currentRecordNumber);
                    if (parsed.InUse)
                    {
                        if (parsed.IsCorrupt) corruptRecords++;
                        parsedRecords[currentRecordNumber] = parsed;
                    }

                    currentRecordNumber++;
                    processedRecords++;
                }

                runOffset += actualRead;

                if (processedRecords % 16384 < completeRecords)
                {
                    progress?.Report(new ScanProgress(
                        ScanPhase.ReadingMft,
                        ScanBackendKind.RawNtfsMft,
                        processedRecords,
                        estimatedTotalRecords > 0 ? estimatedTotalRecords : null,
                        $"已读取 {processedRecords:N0} 条 MFT 记录…"));
                }

                if (actualRead < toRead)
                {
                    // 发生短读时显式计错，但外层 logicalRunStartByte 仍按真实区段长度推进，绝不污染下一段记录号
                    corruptRecords++;
                    break;
                }
            }

            logicalRunStartByte += effectiveRunBytes;
        }

        long? usnEndNext = null;
        if (hasUsnStart && TryQueryUsnJournal(volumeHandle, out long endJournalId, out long endNext))
        {
            if (endJournalId != usnJournalId)
            {
                requiresLiveReverify = true;
            }
            usnEndNext = endNext;
        }
        else
        {
            requiresLiveReverify = true;
        }

        progress?.Report(new ScanProgress(ScanPhase.BuildingTree, ScanBackendKind.RawNtfsMft, processedRecords, estimatedTotalRecords > 0 ? estimatedTotalRecords : null, "正在构建目录树与汇总占用…"));

        ScanSnapshot snapshot = NtfsMftRecordParser.BuildSnapshotFromParsedRecords(
            volume,
            parsedRecords,
            started,
            MigrationSafetyPolicy.CurrentPolicyVersion,
            corruptRecords,
            hasUsnStart ? usnJournalId : null,
            hasUsnStart ? usnStartNext : null,
            usnEndNext,
            requiresLiveReverify,
            cancelled);

        progress?.Report(new ScanProgress(
            cancelled ? ScanPhase.Cancelled : ScanPhase.Completed,
            ScanBackendKind.RawNtfsMft,
            processedRecords,
            estimatedTotalRecords > 0 ? estimatedTotalRecords : null,
            cancelled ? "已取消 MFT 扫描（仅保留部分只读结果）" : $"MFT 扫描完成：{snapshot.Nodes.Length:N0} 个节点"));

        return snapshot;
    }

    private static List<MftDataRun> ReadMftSelfRuns(
        SafeFileHandle volumeHandle,
        long mftStartOffset,
        int bytesPerSector,
        int bytesPerCluster,
        int recordSize,
        CancellationToken ct)
    {
        byte[] rec0Buf = new byte[recordSize];
        int read = ReadAlignedBytes(volumeHandle, mftStartOffset, rec0Buf, recordSize);
        if (read != recordSize || !NtfsMftRecordParser.ApplyUsaFixup(rec0Buf, bytesPerSector))
        {
            throw new IOException("$MFT 记录 0 读取或 USA Fixup 校验失败。");
        }

        ParsedMftRecord rec0 = NtfsMftRecordParser.ParseFixedUpRecord(rec0Buf, 0, captureDefaultDataRuns: true);
        var allRuns = new List<MftDataRun>(rec0.DefaultDataRuns);

        // 处理碎片化 $MFT 自身的非驻留 $ATTRIBUTE_LIST：拼接全部 runs 并严格按 dataSize 截断，避免簇尾脏字节污染解析
        if (rec0.NonResidentAttributeListRuns.Count > 0)
        {
            long expectedSize = rec0.NonResidentAttributeListDataSize;
            const int maxAttrListBytes = 4 * 1024 * 1024;
            if (expectedSize <= 0 || expectedSize > maxAttrListBytes)
            {
                throw new IOException($"$MFT 非驻留 $ATTRIBUTE_LIST 大小异常：{expectedSize}");
            }

            using var ms = new MemoryStream(checked((int)expectedSize));
            foreach (MftDataRun alRun in rec0.NonResidentAttributeListRuns)
            {
                ct.ThrowIfCancellationRequested();
                if (ms.Length >= expectedSize) break;
                int runByteLen = checked((int)Math.Min(alRun.ClusterCount * bytesPerCluster, maxAttrListBytes - ms.Length));
                if (alRun.IsSparse)
                {
                    ms.Write(new byte[runByteLen]);
                    continue;
                }
                byte[] alBuf = new byte[runByteLen];
                int actual = ReadAlignedBytes(volumeHandle, checked(alRun.Lcn * bytesPerCluster), alBuf, runByteLen);
                if (actual < runByteLen)
                {
                    throw new IOException("$MFT 非驻留 $ATTRIBUTE_LIST 读取不完整。");
                }
                ms.Write(alBuf, 0, actual);
            }

            int validLen = checked((int)Math.Min(expectedSize, ms.Length));
            NtfsMftRecordParser.ParseAttributeListBuffer(ms.GetBuffer().AsSpan(0, validLen), rec0.AttributeListEntries);
        }

        // 若 $MFT 记录 0 存在指向扩展记录的 $DATA 项，按已读取到的 runs 定位并合并其余扩展记录的 $DATA runs；任一扩展失败显式抛错触发降级
        var extRecordIds = rec0.AttributeListEntries
            .Where(e => e.AttributeType == NtfsMftRecordParser.AttrData && e.TargetRecordNumber != 0)
            .Select(e => e.TargetRecordNumber)
            .Distinct()
            .ToArray();

        if (extRecordIds.Length > 0)
        {
            if (allRuns.Count == 0)
            {
                throw new IOException("$MFT 记录 0 缺失基础 $DATA runs，无法解析扩展记录。");
            }

            byte[] extBuf = new byte[recordSize];
            foreach (ulong extId in extRecordIds)
            {
                ct.ThrowIfCancellationRequested();
                if (!TryResolveMftRecordDiskOffset(allRuns, extId, recordSize, bytesPerCluster, out long diskOffset))
                {
                    throw new IOException($"无法定位 $MFT 扩展记录 #{extId} 的磁盘偏移。");
                }

                int extRead = ReadAlignedBytes(volumeHandle, diskOffset, extBuf, recordSize);
                if (extRead != recordSize || !NtfsMftRecordParser.ApplyUsaFixup(extBuf, bytesPerSector))
                {
                    throw new IOException($"$MFT 扩展记录 #{extId} 读取或 Fixup 校验失败。");
                }

                ParsedMftRecord extRec = NtfsMftRecordParser.ParseFixedUpRecord(extBuf, extId, captureDefaultDataRuns: true);
                allRuns.AddRange(extRec.DefaultDataRuns);
            }
        }

        return allRuns;
    }

    private static bool TryResolveMftRecordDiskOffset(
        List<MftDataRun> runs,
        ulong recordNumber,
        int recordSize,
        int bytesPerCluster,
        out long diskOffset)
    {
        long targetLogicalByte = checked((long)recordNumber * recordSize);
        long logicalCursor = 0;
        foreach (MftDataRun run in runs)
        {
            long runBytes = checked(run.ClusterCount * bytesPerCluster);
            if (targetLogicalByte >= logicalCursor && targetLogicalByte + recordSize <= logicalCursor + runBytes)
            {
                if (run.IsSparse)
                {
                    diskOffset = 0;
                    return false;
                }
                diskOffset = checked(run.Lcn * bytesPerCluster + (targetLogicalByte - logicalCursor));
                return true;
            }
            logicalCursor += runBytes;
        }

        diskOffset = 0;
        return false;
    }

    private static int ReadAlignedBytes(SafeFileHandle handle, long offset, byte[] buffer, int count)
    {
        if (!SetFilePointerEx(handle, offset, out _, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"定位原始卷偏移失败：{offset}");
        }

        if (!ReadFile(handle, buffer, (uint)count, out uint bytesRead, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"读取原始卷数据失败：offset={offset}, count={count}");
        }

        return checked((int)bytesRead);
    }

    private static bool TryQueryUsnJournal(SafeFileHandle volumeHandle, out long journalId, out long nextUsn)
    {
        journalId = 0;
        nextUsn = 0;
        if (!DeviceIoControlUsn(volumeHandle, FSCTL_QUERY_USN_JOURNAL, IntPtr.Zero, 0,
                out UsnJournalDataV0 usnData, (uint)Marshal.SizeOf<UsnJournalDataV0>(), out _, IntPtr.Zero))
        {
            return false;
        }
        journalId = usnData.UsnJournalID;
        nextUsn = usnData.NextUsn;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NtfsVolumeDataBuffer
    {
        public long VolumeSerialNumber;
        public long NumberSectors;
        public long TotalClusters;
        public long FreeClusters;
        public long TotalReserved;
        public uint BytesPerSector;
        public uint BytesPerCluster;
        public uint BytesPerFileRecordSegment;
        public uint ClustersPerFileRecordSegment;
        public long MftValidDataLength;
        public long MftStartLcn;
        public long Mft2StartLcn;
        public long MftZoneStart;
        public long MftZoneEnd;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UsnJournalDataV0
    {
        public long UsnJournalID;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public long MaximumSize;
        public long AllocationDelta;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFilePointerEx(SafeFileHandle hFile, long liDistanceToMove, out long lpNewFilePointer, uint dwMoveMethod);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(SafeFileHandle hFile, [Out] byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlVolumeData(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        out NtfsVolumeDataBuffer lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlUsn(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        out UsnJournalDataV0 lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);
}
