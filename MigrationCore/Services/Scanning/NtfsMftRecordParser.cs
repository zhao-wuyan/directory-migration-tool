using System.Buffers.Binary;
using System.Text;
using MigrationCore.Models.Scanning;

namespace MigrationCore.Services.Scanning;

/// <summary>
/// MFT 数据段（Run List 解码结果）。
/// </summary>
public readonly record struct MftDataRun(long Lcn, long ClusterCount, bool IsSparse);

/// <summary>
/// 解析自单个 $FILE_NAME 属性的目录项。
/// </summary>
public sealed record ParsedFileNameEntry(
    ulong ParentRecordNumber,
    ushort ParentSequenceNumber,
    byte Namespace,
    string Name);

/// <summary>
/// 解析自 $ATTRIBUTE_LIST 的扩展记录引用。
/// </summary>
public readonly record struct ParsedAttributeListEntry(
    uint AttributeType,
    ulong TargetRecordNumber,
    ushort TargetSequenceNumber);

/// <summary>
/// 单个 MFT 记录（基础记录或扩展记录）的 Safe Span 解析结果。
/// </summary>
public sealed class ParsedMftRecord
{
    public ulong RecordNumber { get; init; }
    public ushort SequenceNumber { get; init; }
    public bool InUse { get; set; }
    public bool IsDirectory { get; set; }
    public bool IsCorrupt { get; set; }
    public ulong BaseRecordNumber { get; set; }
    public ushort BaseSequenceNumber { get; set; }
    public bool IsExtensionRecord => BaseRecordNumber != 0;

    public long LastWriteUtcTicks { get; set; }
    public uint DosAttributes { get; set; }
    public uint ReparseTag { get; set; }
    public bool HasReparsePoint { get; set; }
    public bool IsWofCompressed { get; set; }
    public bool IsCloudPlaceholder { get; set; }
    public bool IsResidentDefaultData { get; set; }
    public bool IsSparseOrCompressed { get; set; }
    public bool HasAlternateDataStreams { get; set; }

    public long DefaultDataLogicalBytes { get; set; }
    public long DefaultDataAllocatedBytes { get; set; }
    public long AdsLogicalBytes { get; set; }
    public long AdsAllocatedBytes { get; set; }
    public long WofCompressedAllocatedBytes { get; set; }

    public List<ParsedFileNameEntry> FileNames { get; } = [];
    public List<MftDataRun> DefaultDataRuns { get; } = [];
    public List<ParsedAttributeListEntry> AttributeListEntries { get; } = [];
    public List<MftDataRun> NonResidentAttributeListRuns { get; } = [];
    public long NonResidentAttributeListDataSize { get; set; }
}

/// <summary>
/// 纯 Safe C#（Span + BinaryPrimitives + checked 算术）NTFS MFT 二进制记录解析与树汇总内核。
/// 不含任何 unsafe 指针，供真机卷读取器（NtfsMftScanner）与离线合成 MFT 测试共用。
/// </summary>
public static class NtfsMftRecordParser
{
    public const uint FileSignature = 0x454C4946; // "FILE"
    public const uint AttrStandardInformation = 0x10;
    public const uint AttrAttributeList = 0x20;
    public const uint AttrFileName = 0x30;
    public const uint AttrData = 0x80;
    public const uint AttrReparsePoint = 0xC0;
    public const uint AttrEndMarker = 0xFFFFFFFF;

    public const uint IoReparseTagWof = 0x80000017;
    public const uint IoReparseTagCloudMask = 0xFFFF0FFF;
    public const uint IoReparseTagCloudBase = 0x9000001A;
    public const ulong RootDirectoryRecordNumber = 5;

    private const ushort FlagInUse = 0x0001;
    private const ushort FlagDirectory = 0x0002;
    private const ushort AttrFlagCompressed = 0x0001;
    private const ushort AttrFlagEncrypted = 0x4000;
    private const ushort AttrFlagSparse = 0x8000;
    private const uint DosAttrOffline = 0x00001000;
    private const uint DosAttrRecallOnDataAccess = 0x00400000;

    /// <summary>
    /// 校验记录签名与 USA（Update Sequence Array）并将各扇区末 2 字节恢复为原始数据。
    /// 若发现签名无效、越界或撕裂写（扇区尾与 USA[0] 不一致），返回 false 而不越界或死循环。
    /// </summary>
    public static bool ApplyUsaFixup(Span<byte> recordBuffer, int bytesPerSector)
    {
        if (bytesPerSector < 512 || (bytesPerSector & (bytesPerSector - 1)) != 0)
        {
            return false;
        }

        if (recordBuffer.Length < 48 || recordBuffer.Length % bytesPerSector != 0)
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(recordBuffer[..4]) != FileSignature)
        {
            return false;
        }

        ushort usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(recordBuffer.Slice(4, 2));
        ushort usaCount = BinaryPrimitives.ReadUInt16LittleEndian(recordBuffer.Slice(6, 2));
        if (usaCount < 2 || usaOffset < 42)
        {
            return false;
        }

        int sectorCount = recordBuffer.Length / bytesPerSector;
        if (usaCount - 1 < sectorCount)
        {
            return false;
        }

        int usaEnd;
        try
        {
            usaEnd = checked(usaOffset + usaCount * 2);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (usaEnd > recordBuffer.Length)
        {
            return false;
        }

        ushort expectedUsn = BinaryPrimitives.ReadUInt16LittleEndian(recordBuffer.Slice(usaOffset, 2));
        for (int sector = 0; sector < sectorCount; sector++)
        {
            int tailOffset = (sector + 1) * bytesPerSector - 2;
            ushort actualUsn = BinaryPrimitives.ReadUInt16LittleEndian(recordBuffer.Slice(tailOffset, 2));
            if (actualUsn != expectedUsn)
            {
                // Torn write / 损坏记录
                return false;
            }

            ushort originalWord = BinaryPrimitives.ReadUInt16LittleEndian(recordBuffer.Slice(usaOffset + (sector + 1) * 2, 2));
            BinaryPrimitives.WriteUInt16LittleEndian(recordBuffer.Slice(tailOffset, 2), originalWord);
        }

        return true;
    }

    /// <summary>
    /// 解析 NTFS 非驻留属性的 Data Run List（Mapping Pairs），严格校验长度与整数溢出。
    /// </summary>
    public static bool TryParseRunList(ReadOnlySpan<byte> data, out List<MftDataRun> runs)
    {
        runs = [];
        int pos = 0;
        long currentLcn = 0;
        int guard = 0;

        while (pos < data.Length)
        {
            if (++guard > 1_000_000)
            {
                return false;
            }

            byte header = data[pos++];
            if (header == 0)
            {
                return true;
            }

            int lengthBytes = header & 0x0F;
            int offsetBytes = (header >> 4) & 0x0F;
            if (lengthBytes == 0 || lengthBytes > 8 || offsetBytes > 8)
            {
                return false;
            }

            if (pos + lengthBytes + offsetBytes > data.Length)
            {
                return false;
            }

            long clusterCount = ReadUnsignedLittleEndian(data.Slice(pos, lengthBytes));
            pos += lengthBytes;
            if (clusterCount <= 0)
            {
                return false;
            }

            if (offsetBytes == 0)
            {
                runs.Add(new MftDataRun(-1, clusterCount, IsSparse: true));
                continue;
            }

            long lcnDelta = ReadSignedLittleEndian(data.Slice(pos, offsetBytes));
            pos += offsetBytes;

            try
            {
                currentLcn = checked(currentLcn + lcnDelta);
            }
            catch (OverflowException)
            {
                return false;
            }

            if (currentLcn < 0)
            {
                return false;
            }

            runs.Add(new MftDataRun(currentLcn, clusterCount, IsSparse: false));
        }

        return true;
    }

    /// <summary>
    /// 解析已通过 Fixup 的单条 MFT 记录（支持 1 KB / 4 KB 等任意合法记录大小）。
    /// </summary>
    public static ParsedMftRecord ParseFixedUpRecord(
        ReadOnlySpan<byte> recordBuffer,
        ulong recordNumber,
        bool captureDefaultDataRuns = false)
    {
        var parsed = new ParsedMftRecord { RecordNumber = recordNumber };

        if (recordBuffer.Length < 48
            || BinaryPrimitives.ReadUInt32LittleEndian(recordBuffer[..4]) != FileSignature)
        {
            parsed.IsCorrupt = true;
            return parsed;
        }

        ushort seq = BinaryPrimitives.ReadUInt16LittleEndian(recordBuffer.Slice(16, 2));
        ushort firstAttrOffset = BinaryPrimitives.ReadUInt16LittleEndian(recordBuffer.Slice(20, 2));
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(recordBuffer.Slice(22, 2));
        uint usedSize = BinaryPrimitives.ReadUInt32LittleEndian(recordBuffer.Slice(24, 4));
        ulong baseRef = BinaryPrimitives.ReadUInt64LittleEndian(recordBuffer.Slice(32, 8));

        parsed = new ParsedMftRecord
        {
            RecordNumber = recordNumber,
            SequenceNumber = seq,
            InUse = (flags & FlagInUse) != 0,
            IsDirectory = (flags & FlagDirectory) != 0,
            BaseRecordNumber = baseRef & 0x0000_FFFF_FFFF_FFFFUL,
            BaseSequenceNumber = (ushort)(baseRef >> 48)
        };

        if (!parsed.InUse)
        {
            return parsed;
        }

        int limit = usedSize > 0 && usedSize <= (uint)recordBuffer.Length ? (int)usedSize : recordBuffer.Length;
        if (firstAttrOffset < 42 || firstAttrOffset >= limit)
        {
            parsed.IsCorrupt = true;
            return parsed;
        }

        int pos = firstAttrOffset;
        int attrGuard = 0;
        while (pos + 4 <= limit)
        {
            if (++attrGuard > 4096)
            {
                parsed.IsCorrupt = true;
                return parsed;
            }

            uint attrType = BinaryPrimitives.ReadUInt32LittleEndian(recordBuffer.Slice(pos, 4));
            if (attrType == AttrEndMarker)
            {
                break;
            }

            if (pos + 16 > limit)
            {
                parsed.IsCorrupt = true;
                return parsed;
            }

            uint attrLen = BinaryPrimitives.ReadUInt32LittleEndian(recordBuffer.Slice(pos + 4, 4));
            if (attrLen < 16 || attrLen > (uint)(limit - pos))
            {
                parsed.IsCorrupt = true;
                return parsed;
            }

            ReadOnlySpan<byte> attr = recordBuffer.Slice(pos, (int)attrLen);
            byte nonResident = attr[8];
            byte nameLength = attr[9];
            ushort nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(attr.Slice(10, 2));
            ushort attrFlags = BinaryPrimitives.ReadUInt16LittleEndian(attr.Slice(12, 2));

            string attrName = string.Empty;
            if (nameLength > 0)
            {
                int nameByteLen = nameLength * 2;
                if (nameOffset + nameByteLen <= attr.Length)
                {
                    attrName = Encoding.Unicode.GetString(attr.Slice(nameOffset, nameByteLen));
                }
                else
                {
                    parsed.IsCorrupt = true;
                    return parsed;
                }
            }

            switch (attrType)
            {
                case AttrStandardInformation:
                    ParseStandardInfo(attr, nonResident, parsed);
                    break;
                case AttrAttributeList:
                    ParseAttributeListAttr(attr, nonResident, parsed);
                    break;
                case AttrFileName:
                    ParseFileNameAttr(attr, nonResident, parsed);
                    break;
                case AttrData:
                    ParseDataAttr(attr, nonResident, attrFlags, nameLength, nameOffset, attrName, captureDefaultDataRuns, parsed);
                    break;
                case AttrReparsePoint:
                    ParseReparsePointAttr(attr, nonResident, parsed);
                    break;
            }

            pos += (int)attrLen;
        }

        return parsed;
    }

    /// <summary>
    /// 解析 $ATTRIBUTE_LIST 内容缓冲区（同时适用于 resident 与从卷读取后的 non-resident 缓冲）。
    /// </summary>
    public static void ParseAttributeListBuffer(ReadOnlySpan<byte> buffer, List<ParsedAttributeListEntry> target)
    {
        int pos = 0;
        int guard = 0;
        while (pos + 26 <= buffer.Length && ++guard <= 65536)
        {
            uint attrType = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(pos, 4));
            ushort recordLen = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(pos + 4, 2));
            if (recordLen < 26 || pos + recordLen > buffer.Length)
            {
                break;
            }

            ulong fileRef = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(pos + 16, 8));
            ulong recNum = fileRef & 0x0000_FFFF_FFFF_FFFFUL;
            ushort seqNum = (ushort)(fileRef >> 48);
            target.Add(new ParsedAttributeListEntry(attrType, recNum, seqNum));
            pos += recordLen;
        }
    }

    /// <summary>
    /// 将全部已解析的 MFT 基础/扩展记录合并为紧凑的 ScanSnapshot（扁平 struct + CSR 树 + 硬链接唯一归属 + 孤立归档）。
    /// </summary>
    public static ScanSnapshot BuildSnapshotFromParsedRecords(
        ScanVolumeInfo volume,
        IReadOnlyDictionary<ulong, ParsedMftRecord> recordsByNumber,
        DateTimeOffset startedAtUtc,
        string policyVersion,
        int corruptRecordCount,
        long? usnJournalId,
        long? usnStartNext,
        long? usnEndNext,
        bool requiresLiveReverification,
        bool wasCancelled)
    {
        // 1. 合并扩展记录（BaseRecordNumber != 0）到其基础记录（校验序列号防止记录复用串接）
        var baseRecords = new Dictionary<ulong, ParsedMftRecord>(recordsByNumber.Count);
        var extensions = new List<ParsedMftRecord>();

        foreach (var kvp in recordsByNumber)
        {
            ParsedMftRecord rec = kvp.Value;
            if (!rec.InUse) continue;
            if (rec.IsExtensionRecord)
            {
                extensions.Add(rec);
            }
            else
            {
                baseRecords[rec.RecordNumber] = rec;
            }
        }

        int incompleteCount = corruptRecordCount;
        foreach (ParsedMftRecord ext in extensions)
        {
            if (baseRecords.TryGetValue(ext.BaseRecordNumber, out ParsedMftRecord? baseRec)
                && (ext.BaseSequenceNumber == 0 || baseRec.SequenceNumber == ext.BaseSequenceNumber))
            {
                MergeExtensionIntoBase(baseRec, ext);
            }
            else
            {
                incompleteCount++;
            }
        }

        // 2. 分配节点：节点 0 为卷根目录（MFT #5），先映射所有目录记录到节点 ID
        var namePool = new Utf8NamePool();
        var nodeList = new List<ScanNodeRecord>(Math.Max(16, baseRecords.Count));
        var dirRecordToNodeId = new Dictionary<ulong, (int NodeId, ushort Seq)>();

        // 节点 0：卷根
        var rootName = namePool.AddName(volume.DriveRoot.AsSpan());
        ushort rootSeq = baseRecords.TryGetValue(RootDirectoryRecordNumber, out var rootMft) ? rootMft.SequenceNumber : (ushort)5;
        nodeList.Add(new ScanNodeRecord
        {
            NodeId = 0,
            ParentNodeId = -1,
            MftRecordNumber = RootDirectoryRecordNumber,
            SequenceNumber = rootSeq,
            NamePageIndex = rootName.PageIndex,
            NameOffset = rootName.Offset,
            NameLength = rootName.Length,
            Flags = ScanNodeFlags.IsDirectory,
            LinkCount = 1
        });
        dirRecordToNodeId[RootDirectoryRecordNumber] = (0, rootSeq);

        // 先识别全部 NTFS 内部保留元文件及其子项（Record #0..#15 中除根 #5 外，以及 $Extend #11 下的 $UsnJrnl/$RmMetadata/$Deleted/$Quota/$ObjId/$Reparse/$Txf 等）
        var ntfsMetaRecordNumbers = new HashSet<ulong>();
        foreach (ParsedMftRecord rec in baseRecords.Values)
        {
            if (rec.RecordNumber < 16 && rec.RecordNumber != RootDirectoryRecordNumber)
            {
                ntfsMetaRecordNumbers.Add(rec.RecordNumber);
            }
        }
        bool addedMeta;
        do
        {
            addedMeta = false;
            foreach (ParsedMftRecord rec in baseRecords.Values)
            {
                if (rec.RecordNumber == RootDirectoryRecordNumber || ntfsMetaRecordNumbers.Contains(rec.RecordNumber))
                    continue;
                if (rec.FileNames.Count > 0 && rec.FileNames.All(fn => ntfsMetaRecordNumbers.Contains(fn.ParentRecordNumber)))
                {
                    ntfsMetaRecordNumbers.Add(rec.RecordNumber);
                    addedMeta = true;
                }
            }
        } while (addedMeta);

        // 先为所有非根、非 NTFS 内部元文件的用户目录创建节点占位
        long metadataBytes = 0;
        var pendingEntries = new List<(ParsedMftRecord Record, List<ParsedFileNameEntry> EffectiveNames)>(baseRecords.Count);

        foreach (ParsedMftRecord rec in baseRecords.Values)
        {
            if (rec.RecordNumber == RootDirectoryRecordNumber)
            {
                continue;
            }

            if (ntfsMetaRecordNumbers.Contains(rec.RecordNumber))
            {
                // NTFS 系统元数据文件（$MFT, $LogFile, $Bitmap, $Extend\$UsnJrnl, $Extend\$RmMetadata, $Extend\$Deleted 等）
                // 计入 MetadataBytes，绝不误归入“未解析位置”或展示为可迁移目录
                metadataBytes = SafeAdd(metadataBytes, rec.DefaultDataAllocatedBytes + rec.AdsAllocatedBytes);
                continue;
            }

            List<ParsedFileNameEntry> effectiveNames = SelectEffectiveNames(rec.FileNames);
            if (effectiveNames.Count == 0)
            {
                continue;
            }

            pendingEntries.Add((rec, effectiveNames));
            if (rec.IsDirectory)
            {
                // 目录通常只有 1 个目录项，取稳定排序首项映射其 MFT 编号
                ParsedFileNameEntry primary = effectiveNames[0];
                var pooled = namePool.AddName(primary.Name.AsSpan());
                int nodeId = nodeList.Count;
                ScanNodeFlags flags = BuildNodeFlags(rec, isSecondaryHardLink: false, isDosOnly: primary.Namespace == 2);
                nodeList.Add(new ScanNodeRecord
                {
                    NodeId = nodeId,
                    ParentNodeId = -1, // 稍后解析父节点
                    MftRecordNumber = rec.RecordNumber,
                    SequenceNumber = rec.SequenceNumber,
                    NamePageIndex = pooled.PageIndex,
                    NameOffset = pooled.Offset,
                    NameLength = pooled.Length,
                    Flags = flags,
                    ReparseTag = rec.ReparseTag,
                    LastWriteUtcTicks = rec.LastWriteUtcTicks,
                    LinkCount = (ushort)Math.Min(ushort.MaxValue, effectiveNames.Count)
                });
                dirRecordToNodeId[rec.RecordNumber] = (nodeId, rec.SequenceNumber);
            }
        }

        // 3. 解析所有目录与文件的父节点关系；无有效父目录（或父序列号不匹配）归入虚拟“未解析位置”节点
        int orphanContainerId = -1;
        int EnsureOrphanContainer()
        {
            if (orphanContainerId >= 0) return orphanContainerId;
            var orphanName = namePool.AddName("未解析位置".AsSpan());
            orphanContainerId = nodeList.Count;
            nodeList.Add(new ScanNodeRecord
            {
                NodeId = orphanContainerId,
                ParentNodeId = 0,
                MftRecordNumber = 0,
                SequenceNumber = 0,
                NamePageIndex = orphanName.PageIndex,
                NameOffset = orphanName.Offset,
                NameLength = orphanName.Length,
                Flags = ScanNodeFlags.IsDirectory | ScanNodeFlags.IsOrphaned | ScanNodeFlags.IsIncomplete,
                LinkCount = 1
            });
            return orphanContainerId;
        }

        foreach (var (rec, effectiveNames) in pendingEntries)
        {
            if (rec.IsDirectory)
            {
                int dirNodeId = dirRecordToNodeId[rec.RecordNumber].NodeId;
                ParsedFileNameEntry primary = effectiveNames[0];
                int parentNodeId = ResolveParentNode(primary, dirRecordToNodeId, EnsureOrphanContainer, out bool orphaned);
                ScanNodeRecord dirNode = nodeList[dirNodeId];
                dirNode.ParentNodeId = parentNodeId;
                if (orphaned)
                {
                    dirNode.Flags |= ScanNodeFlags.IsOrphaned | ScanNodeFlags.IsIncomplete;
                    incompleteCount++;
                }
                nodeList[dirNodeId] = dirNode;
                continue;
            }

            // 文件节点（保留硬链接的全部跨父目录项，按 (ParentRecordNumber, Name) 稳定排序，仅第 0 项归属 UniqueAllocatedBytes）
            long logicalTotal = SafeAdd(rec.DefaultDataLogicalBytes, rec.AdsLogicalBytes);
            long allocatedTotal = ComputeEffectiveAllocatedBytes(rec);

            for (int i = 0; i < effectiveNames.Count; i++)
            {
                ParsedFileNameEntry entry = effectiveNames[i];
                bool isSecondary = i > 0;
                int parentNodeId = ResolveParentNode(entry, dirRecordToNodeId, EnsureOrphanContainer, out bool orphaned);
                var pooled = namePool.AddName(entry.Name.AsSpan());
                ScanNodeFlags flags = BuildNodeFlags(rec, isSecondary, entry.Namespace == 2);
                if (orphaned)
                {
                    flags |= ScanNodeFlags.IsOrphaned | ScanNodeFlags.IsIncomplete;
                    incompleteCount++;
                }

                int fileNodeId = nodeList.Count;
                nodeList.Add(new ScanNodeRecord
                {
                    NodeId = fileNodeId,
                    ParentNodeId = parentNodeId,
                    MftRecordNumber = rec.RecordNumber,
                    SequenceNumber = rec.SequenceNumber,
                    NamePageIndex = pooled.PageIndex,
                    NameOffset = pooled.Offset,
                    NameLength = pooled.Length,
                    Flags = flags,
                    LogicalBytes = logicalTotal,
                    AllocatedBytes = allocatedTotal,
                    UniqueAllocatedBytes = isSecondary ? 0 : allocatedTotal,
                    AdsLogicalBytes = rec.AdsLogicalBytes,
                    FileCount = 1,
                    DirectoryCount = 0,
                    ReparseTag = rec.ReparseTag,
                    LastWriteUtcTicks = rec.LastWriteUtcTicks,
                    LinkCount = (ushort)Math.Min(ushort.MaxValue, effectiveNames.Count)
                });
            }
        }

        // 4. 检测父子成环（损坏 MFT 导致从根不可达的目录子树）并重挂至孤立容器，防止数据静默丢失
        {
            var reachable = new bool[nodeList.Count];
            var adj = new Dictionary<int, List<int>>();
            for (int i = 1; i < nodeList.Count; i++)
            {
                int p = nodeList[i].ParentNodeId;
                if (p >= 0 && p < nodeList.Count && p != i)
                {
                    if (!adj.TryGetValue(p, out var list))
                    {
                        list = [];
                        adj[p] = list;
                    }
                    list.Add(i);
                }
            }

            var bfs = new Queue<int>();
            reachable[0] = true;
            bfs.Enqueue(0);
            while (bfs.Count > 0)
            {
                int curr = bfs.Dequeue();
                if (adj.TryGetValue(curr, out var kids))
                {
                    foreach (int c in kids)
                    {
                        if (!reachable[c])
                        {
                            reachable[c] = true;
                            bfs.Enqueue(c);
                        }
                    }
                }
            }

            int initialCount = nodeList.Count;
            for (int i = 1; i < initialCount; i++)
            {
                if (!reachable[i])
                {
                    int orphanBox = EnsureOrphanContainer();
                    if (i == orphanBox) continue;
                    ScanNodeRecord cyclic = nodeList[i];
                    cyclic.ParentNodeId = orphanBox;
                    cyclic.Flags |= ScanNodeFlags.IsOrphaned | ScanNodeFlags.IsIncomplete;
                    nodeList[i] = cyclic;
                    incompleteCount++;
                }
            }
        }

        // 5. 构建 CSR（Compressed Sparse Row）父子邻接表并执行单遍自底向上汇总
        ScanNodeRecord[] nodes = nodeList.ToArray();
        BuildCsrAndAggregate(nodes, rootNodeId: 0, out int[] csrOffsets, out int[] csrChildren);

        long volumeUsed = volume.UsedBytes;
        long accounted = SafeAdd(nodes[0].UniqueAllocatedBytes, metadataBytes);
        long unknownBytes = Math.Max(0, volumeUsed - accounted);

        ScanCoverageStatus coverage = wasCancelled
            ? ScanCoverageStatus.Cancelled
            : incompleteCount > 0
                ? ScanCoverageStatus.Partial
                : ScanCoverageStatus.Complete;

        return new ScanSnapshot
        {
            Volume = volume,
            Backend = ScanBackendKind.RawNtfsMft,
            Coverage = coverage,
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            PolicyVersion = policyVersion,
            ErrorCount = corruptRecordCount,
            IncompleteNodeCount = incompleteCount,
            MetadataBytes = metadataBytes,
            UnknownBytes = unknownBytes,
            UsnStartJournalId = usnJournalId,
            UsnStartNextUsn = usnStartNext,
            UsnEndNextUsn = usnEndNext,
            RequiresLiveReverification = requiresLiveReverification || (usnStartNext != usnEndNext),
            RootNodeId = 0,
            OrphanContainerNodeId = orphanContainerId,
            Nodes = nodes,
            CsrOffsets = csrOffsets,
            CsrChildren = csrChildren,
            NamePool = namePool
        };
    }

    /// <summary>
    /// 为任意节点数组构建 CSR 邻接表（子节点按 UniqueAllocatedBytes / LogicalBytes 降序排列）并自底向上汇总目录大小。
    /// 供 NtfsMftScanner 与 DirectoryWalkScanner 共用。
    /// </summary>
    public static void BuildCsrAndAggregate(
        ScanNodeRecord[] nodes,
        int rootNodeId,
        out int[] csrOffsets,
        out int[] csrChildren)
    {
        int count = nodes.Length;
        int[] childCounts = new int[count];
        int totalEdges = 0;

        for (int i = 0; i < count; i++)
        {
            int p = nodes[i].ParentNodeId;
            if (p >= 0 && p < count && p != i)
            {
                childCounts[p]++;
                totalEdges++;
            }
        }

        csrOffsets = new int[count + 1];
        for (int i = 0; i < count; i++)
        {
            csrOffsets[i + 1] = csrOffsets[i] + childCounts[i];
        }

        csrChildren = new int[totalEdges];
        int[] writeCursor = new int[count];
        Array.Copy(csrOffsets, writeCursor, count);

        for (int i = 0; i < count; i++)
        {
            int p = nodes[i].ParentNodeId;
            if (p >= 0 && p < count && p != i)
            {
                csrChildren[writeCursor[p]++] = i;
            }
        }

        // 迭代后序遍历（避免深目录树递归栈溢出）汇总大小与计数
        int[] order = new int[count];
        int orderLen = 0;
        var visited = new bool[count];
        var stack = new Stack<int>();
        if (rootNodeId >= 0 && rootNodeId < count)
        {
            stack.Push(rootNodeId);
            visited[rootNodeId] = true;
        }

        while (stack.Count > 0)
        {
            int curr = stack.Pop();
            order[orderLen++] = curr;
            int start = csrOffsets[curr];
            int end = csrOffsets[curr + 1];
            for (int e = start; e < end; e++)
            {
                int child = csrChildren[e];
                if (!visited[child])
                {
                    visited[child] = true;
                    stack.Push(child);
                }
            }
        }

        // 逆序（叶子向根）汇总
        for (int idx = orderLen - 1; idx >= 0; idx--)
        {
            int curr = order[idx];
            if (!nodes[curr].IsDirectory) continue;

            long logical = 0;
            long allocated = 0;
            long uniqueAllocated = 0;
            long adsLogical = 0;
            int files = 0;
            int dirs = 0;
            bool childIncomplete = false;

            int start = csrOffsets[curr];
            int end = csrOffsets[curr + 1];
            for (int e = start; e < end; e++)
            {
                ref readonly ScanNodeRecord child = ref nodes[csrChildren[e]];
                logical = SafeAdd(logical, child.LogicalBytes);
                allocated = SafeAdd(allocated, child.AllocatedBytes);
                uniqueAllocated = SafeAdd(uniqueAllocated, child.UniqueAllocatedBytes);
                adsLogical = SafeAdd(adsLogical, child.AdsLogicalBytes);
                files += child.FileCount;
                dirs += child.IsDirectory ? 1 + child.DirectoryCount : 0;
                if (child.IsIncomplete) childIncomplete = true;
            }

            nodes[curr].LogicalBytes = logical;
            nodes[curr].AllocatedBytes = allocated;
            nodes[curr].UniqueAllocatedBytes = uniqueAllocated;
            nodes[curr].AdsLogicalBytes = adsLogical;
            nodes[curr].FileCount = files;
            nodes[curr].DirectoryCount = dirs;
            if (childIncomplete)
            {
                nodes[curr].Flags |= ScanNodeFlags.IsIncomplete;
            }

            // 按占用大小降序排序该目录的子节点，确保 UI 展开即呈 WizTree 排序体验
            int len = end - start;
            if (len > 1)
            {
                Array.Sort(csrChildren, start, len, new NodeSizeDescendingComparer(nodes));
            }
        }
    }

    private sealed class NodeSizeDescendingComparer(ScanNodeRecord[] nodes) : IComparer<int>
    {
        public int Compare(int x, int y)
        {
            long sx = Math.Max(nodes[x].UniqueAllocatedBytes, nodes[x].LogicalBytes);
            long sy = Math.Max(nodes[y].UniqueAllocatedBytes, nodes[y].LogicalBytes);
            int cmp = sy.CompareTo(sx);
            if (cmp != 0) return cmp;
            return x.CompareTo(y);
        }
    }

    private static int ResolveParentNode(
        ParsedFileNameEntry entry,
        Dictionary<ulong, (int NodeId, ushort Seq)> dirMap,
        Func<int> ensureOrphanContainer,
        out bool orphaned)
    {
        if (dirMap.TryGetValue(entry.ParentRecordNumber, out var parentInfo)
            && (entry.ParentSequenceNumber == 0 || parentInfo.Seq == 0 || parentInfo.Seq == entry.ParentSequenceNumber))
        {
            orphaned = false;
            return parentInfo.NodeId;
        }

        orphaned = true;
        return ensureOrphanContainer();
    }

    private static List<ParsedFileNameEntry> SelectEffectiveNames(List<ParsedFileNameEntry> rawNames)
    {
        if (rawNames.Count <= 1)
        {
            return rawNames;
        }

        // 按父目录分组：同一父目录下优先保留 Win32 / Win32+DOS / POSIX 长名，丢弃纯 DOS 8.3 短名（Namespace == 2）；
        // 跨父目录或同父多硬链接则全部保留，并按 (ParentRecordNumber, Name) 稳定排序保证 UniqueAllocatedBytes 归属确定性。
        var result = new List<ParsedFileNameEntry>(rawNames.Count);
        foreach (var group in rawNames.GroupBy(n => n.ParentRecordNumber))
        {
            var nonDos = group.Where(n => n.Namespace != 2).ToList();
            if (nonDos.Count > 0)
            {
                result.AddRange(nonDos.DistinctBy(n => n.Name, StringComparer.OrdinalIgnoreCase));
            }
            else
            {
                result.AddRange(group.DistinctBy(n => n.Name, StringComparer.OrdinalIgnoreCase));
            }
        }

        result.Sort((a, b) =>
        {
            int p = a.ParentRecordNumber.CompareTo(b.ParentRecordNumber);
            return p != 0 ? p : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        });
        return result;
    }

    private static long ComputeEffectiveAllocatedBytes(ParsedMftRecord rec)
    {
        // WOF / CompactOS 压缩文件：默认流为稀疏，真实本地磁盘占用落在 WofCompressedData 命名 ADS 的压缩分配大小中
        if (rec.IsWofCompressed && rec.WofCompressedAllocatedBytes > 0)
        {
            return rec.WofCompressedAllocatedBytes;
        }

        // OneDrive / 云占位未水合文件：标记 Offline 或 RecallOnDataAccess 时，默认流数据留在云端，本地不计入云端虚拟分配量
        if (rec.IsCloudPlaceholder && (rec.DosAttributes & (DosAttrOffline | DosAttrRecallOnDataAccess)) != 0)
        {
            return rec.IsSparseOrCompressed ? 0 : Math.Min(rec.DefaultDataAllocatedBytes, 0);
        }

        return SafeAdd(rec.DefaultDataAllocatedBytes, rec.AdsAllocatedBytes);
    }

    private static ScanNodeFlags BuildNodeFlags(ParsedMftRecord rec, bool isSecondaryHardLink, bool isDosOnly)
    {
        ScanNodeFlags flags = ScanNodeFlags.None;
        if (rec.IsDirectory) flags |= ScanNodeFlags.IsDirectory;
        if (rec.HasReparsePoint) flags |= ScanNodeFlags.IsReparsePoint;
        if (rec.IsWofCompressed) flags |= ScanNodeFlags.IsWofCompressed;
        if (rec.IsCloudPlaceholder) flags |= ScanNodeFlags.IsCloudPlaceholder;
        if (rec.IsResidentDefaultData) flags |= ScanNodeFlags.IsResidentData;
        if (rec.IsSparseOrCompressed) flags |= ScanNodeFlags.IsSparseOrCompressed;
        if (rec.HasAlternateDataStreams) flags |= ScanNodeFlags.HasAlternateDataStreams;
        if (isSecondaryHardLink) flags |= ScanNodeFlags.IsHardLinkSecondary;
        if (rec.IsCorrupt) flags |= ScanNodeFlags.IsIncomplete;
        if (isDosOnly) flags |= ScanNodeFlags.IsDosShortNameOnly;
        return flags;
    }

    private static void MergeExtensionIntoBase(ParsedMftRecord baseRec, ParsedMftRecord ext)
    {
        if (ext.IsCorrupt) baseRec.IsCorrupt = true;
        if (ext.LastWriteUtcTicks > baseRec.LastWriteUtcTicks) baseRec.LastWriteUtcTicks = ext.LastWriteUtcTicks;
        baseRec.DosAttributes |= ext.DosAttributes;
        if (ext.HasReparsePoint)
        {
            baseRec.HasReparsePoint = true;
            baseRec.ReparseTag = ext.ReparseTag;
            baseRec.IsWofCompressed |= ext.IsWofCompressed;
            baseRec.IsCloudPlaceholder |= ext.IsCloudPlaceholder;
        }
        baseRec.IsResidentDefaultData |= ext.IsResidentDefaultData;
        baseRec.IsSparseOrCompressed |= ext.IsSparseOrCompressed;
        baseRec.HasAlternateDataStreams |= ext.HasAlternateDataStreams;
        baseRec.DefaultDataLogicalBytes = Math.Max(baseRec.DefaultDataLogicalBytes, ext.DefaultDataLogicalBytes);
        baseRec.DefaultDataAllocatedBytes = SafeAdd(baseRec.DefaultDataAllocatedBytes, ext.DefaultDataAllocatedBytes);
        baseRec.AdsLogicalBytes = SafeAdd(baseRec.AdsLogicalBytes, ext.AdsLogicalBytes);
        baseRec.AdsAllocatedBytes = SafeAdd(baseRec.AdsAllocatedBytes, ext.AdsAllocatedBytes);
        baseRec.WofCompressedAllocatedBytes = SafeAdd(baseRec.WofCompressedAllocatedBytes, ext.WofCompressedAllocatedBytes);
        baseRec.FileNames.AddRange(ext.FileNames);
        baseRec.DefaultDataRuns.AddRange(ext.DefaultDataRuns);
    }

    private static void ParseStandardInfo(ReadOnlySpan<byte> attr, byte nonResident, ParsedMftRecord parsed)
    {
        if (nonResident != 0 || attr.Length < 24) return;
        uint valueLen = BinaryPrimitives.ReadUInt32LittleEndian(attr.Slice(16, 4));
        ushort valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(attr.Slice(20, 2));
        if (valueLen < 36 || valueOffset + valueLen > attr.Length) return;

        ReadOnlySpan<byte> body = attr.Slice(valueOffset, (int)valueLen);
        long fileTime = BinaryPrimitives.ReadInt64LittleEndian(body.Slice(8, 8));
        if (fileTime > 0)
        {
            try { parsed.LastWriteUtcTicks = DateTime.FromFileTimeUtc(fileTime).Ticks; }
            catch { /* 忽略非法时间戳 */ }
        }
        parsed.DosAttributes = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(32, 4));
    }

    private static void ParseAttributeListAttr(ReadOnlySpan<byte> attr, byte nonResident, ParsedMftRecord parsed)
    {
        if (nonResident == 0)
        {
            if (attr.Length < 24) return;
            uint valueLen = BinaryPrimitives.ReadUInt32LittleEndian(attr.Slice(16, 4));
            ushort valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(attr.Slice(20, 2));
            if (valueOffset + valueLen > attr.Length) { parsed.IsCorrupt = true; return; }
            ParseAttributeListBuffer(attr.Slice(valueOffset, (int)valueLen), parsed.AttributeListEntries);
        }
        else
        {
            if (attr.Length < 64) return;
            ushort runOffset = BinaryPrimitives.ReadUInt16LittleEndian(attr.Slice(32, 2));
            long dataSize = BinaryPrimitives.ReadInt64LittleEndian(attr.Slice(48, 8));
            if (runOffset >= attr.Length || dataSize < 0) { parsed.IsCorrupt = true; return; }
            parsed.NonResidentAttributeListDataSize = Math.Max(parsed.NonResidentAttributeListDataSize, dataSize);
            if (TryParseRunList(attr[runOffset..], out var runs))
            {
                parsed.NonResidentAttributeListRuns.AddRange(runs);
            }
            else
            {
                parsed.IsCorrupt = true;
            }
        }
    }

    private static void ParseFileNameAttr(ReadOnlySpan<byte> attr, byte nonResident, ParsedMftRecord parsed)
    {
        if (nonResident != 0 || attr.Length < 24) return;
        uint valueLen = BinaryPrimitives.ReadUInt32LittleEndian(attr.Slice(16, 4));
        ushort valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(attr.Slice(20, 2));
        if (valueLen < 66 || valueOffset + valueLen > attr.Length) return;

        ReadOnlySpan<byte> body = attr.Slice(valueOffset, (int)valueLen);
        ulong parentRef = BinaryPrimitives.ReadUInt64LittleEndian(body[..8]);
        ulong parentRecNum = parentRef & 0x0000_FFFF_FFFF_FFFFUL;
        ushort parentSeq = (ushort)(parentRef >> 48);
        byte nameCharLen = body[64];
        byte ns = body[65];
        int nameBytes = nameCharLen * 2;
        if (66 + nameBytes > body.Length)
        {
            parsed.IsCorrupt = true;
            return;
        }

        string name = Encoding.Unicode.GetString(body.Slice(66, nameBytes));
        if (!string.IsNullOrEmpty(name))
        {
            parsed.FileNames.Add(new ParsedFileNameEntry(parentRecNum, parentSeq, ns, name));
        }
    }

    private static void ParseDataAttr(
        ReadOnlySpan<byte> attr,
        byte nonResident,
        ushort attrFlags,
        byte nameLength,
        ushort nameOffset,
        string attrName,
        bool captureDefaultDataRuns,
        ParsedMftRecord parsed)
    {
        bool isNamedAds = !string.IsNullOrEmpty(attrName);
        bool isWofStream = attrName.Equals("WofCompressedData", StringComparison.OrdinalIgnoreCase);
        bool isCompressedOrSparse = (attrFlags & (AttrFlagCompressed | AttrFlagSparse)) != 0;
        if (isCompressedOrSparse || (attrFlags & AttrFlagEncrypted) != 0)
        {
            parsed.IsSparseOrCompressed = true;
        }

        if (isNamedAds && !isWofStream)
        {
            parsed.HasAlternateDataStreams = true;
        }

        if (nonResident == 0)
        {
            if (attr.Length < 24) return;
            uint valueLen = BinaryPrimitives.ReadUInt32LittleEndian(attr.Slice(16, 4));
            if (!isNamedAds)
            {
                // 常驻 MFT 小文件：数据存放在 MFT 记录内，逻辑大小 = valueLen，簇分配空间 = 0
                parsed.IsResidentDefaultData = true;
                parsed.DefaultDataLogicalBytes = Math.Max(parsed.DefaultDataLogicalBytes, valueLen);
            }
            else if (isWofStream)
            {
                parsed.IsWofCompressed = true;
                parsed.WofCompressedAllocatedBytes = SafeAdd(parsed.WofCompressedAllocatedBytes, valueLen);
            }
            else
            {
                parsed.AdsLogicalBytes = SafeAdd(parsed.AdsLogicalBytes, valueLen);
            }
            return;
        }

        // Non-resident $DATA
        if (attr.Length < 64) { parsed.IsCorrupt = true; return; }
        ulong startVcn = BinaryPrimitives.ReadUInt64LittleEndian(attr.Slice(16, 8));
        ushort runOffset = BinaryPrimitives.ReadUInt16LittleEndian(attr.Slice(32, 2));
        ushort compressionUnit = BinaryPrimitives.ReadUInt16LittleEndian(attr.Slice(34, 2));
        long allocatedSize = BinaryPrimitives.ReadInt64LittleEndian(attr.Slice(40, 8));
        long dataSize = BinaryPrimitives.ReadInt64LittleEndian(attr.Slice(48, 8));

        long effectiveAllocated = Math.Max(0, allocatedSize);
        // NTFS 规范：仅当 startVcn == 0 且 compressionUnit > 0 且头长度明确 >= 72（不与流名或 Run List 重叠）时，Offset 64 才是 CompressedSize
        if (startVcn == 0 && compressionUnit > 0 && attr.Length >= 72
            && runOffset >= 72 && (nameLength == 0 || nameOffset >= 72))
        {
            long compressedSize = BinaryPrimitives.ReadInt64LittleEndian(attr.Slice(64, 8));
            if (compressedSize >= 0)
            {
                effectiveAllocated = compressedSize;
            }
        }

        if (startVcn == 0)
        {
            if (!isNamedAds)
            {
                parsed.DefaultDataLogicalBytes = Math.Max(parsed.DefaultDataLogicalBytes, Math.Max(0, dataSize));
                parsed.DefaultDataAllocatedBytes = SafeAdd(parsed.DefaultDataAllocatedBytes, effectiveAllocated);
            }
            else if (isWofStream)
            {
                parsed.IsWofCompressed = true;
                parsed.WofCompressedAllocatedBytes = SafeAdd(parsed.WofCompressedAllocatedBytes, effectiveAllocated);
            }
            else
            {
                parsed.AdsLogicalBytes = SafeAdd(parsed.AdsLogicalBytes, Math.Max(0, dataSize));
                parsed.AdsAllocatedBytes = SafeAdd(parsed.AdsAllocatedBytes, effectiveAllocated);
            }
        }

        if (captureDefaultDataRuns && !isNamedAds && runOffset < attr.Length)
        {
            if (TryParseRunList(attr[runOffset..], out var runs))
            {
                parsed.DefaultDataRuns.AddRange(runs);
            }
            else
            {
                parsed.IsCorrupt = true;
            }
        }
    }

    private static void ParseReparsePointAttr(ReadOnlySpan<byte> attr, byte nonResident, ParsedMftRecord parsed)
    {
        parsed.HasReparsePoint = true;
        if (nonResident != 0 || attr.Length < 24) return;
        uint valueLen = BinaryPrimitives.ReadUInt32LittleEndian(attr.Slice(16, 4));
        ushort valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(attr.Slice(20, 2));
        if (valueLen < 4 || valueOffset + 4 > attr.Length) return;

        uint tag = BinaryPrimitives.ReadUInt32LittleEndian(attr.Slice(valueOffset, 4));
        parsed.ReparseTag = tag;
        if (tag == IoReparseTagWof)
        {
            parsed.IsWofCompressed = true;
        }
        else if ((tag & IoReparseTagCloudMask) == IoReparseTagCloudBase)
        {
            parsed.IsCloudPlaceholder = true;
        }
    }

    private static long ReadUnsignedLittleEndian(ReadOnlySpan<byte> bytes)
    {
        ulong value = 0;
        for (int i = bytes.Length - 1; i >= 0; i--)
        {
            value = (value << 8) | bytes[i];
        }
        return value > long.MaxValue ? -1 : (long)value;
    }

    private static long ReadSignedLittleEndian(ReadOnlySpan<byte> bytes)
    {
        ulong value = 0;
        for (int i = bytes.Length - 1; i >= 0; i--)
        {
            value = (value << 8) | bytes[i];
        }

        if (bytes.Length < 8 && (bytes[^1] & 0x80) != 0)
        {
            ulong signMask = ~((1UL << (bytes.Length * 8)) - 1UL);
            value |= signMask;
        }

        return unchecked((long)value);
    }

    private static long SafeAdd(long a, long b)
    {
        if (b <= 0) return a;
        if (a > long.MaxValue - b) return long.MaxValue;
        return a + b;
    }
}
