using MigrationCore.Models.Scanning;

namespace MigrationCore.Services.Scanning;

/// <summary>
/// 统一磁盘扫描协调服务：负责枚举本地卷能力、优先使用原始 NTFS MFT 主路径、
/// 能力不足时显式降级为 DirectoryWalkScanner 并诚实标注原因（绝不将遍历冒充 MFT）。
/// </summary>
public sealed class DiskScanService
{
    private readonly NtfsMftScanner _mftScanner = new();
    private readonly DirectoryWalkScanner _walkScanner = new();
    private readonly PathIdentityService _identity = new();

    public IReadOnlyList<ScanVolumeInfo> ListVolumes()
    {
        bool isAdmin = PathValidator.IsAdministrator();
        var volumes = new List<ScanVolumeInfo>();

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
            {
                continue;
            }

            string root = drive.RootDirectory.FullName;
            string fs = string.Empty;
            string label = string.Empty;
            long total = 0;
            long free = 0;
            string guid = string.Empty;
            uint serial = 0;

            try
            {
                fs = drive.DriveFormat;
                label = drive.VolumeLabel;
                total = drive.TotalSize;
                free = drive.AvailableFreeSpace;
            }
            catch
            {
                // 跳过瞬时不可读卷
                continue;
            }

            try
            {
                var id = _identity.ReadDirectory(root, followLink: false);
                guid = id.VolumeGuid;
                serial = id.VolumeSerial;
            }
            catch
            {
                // 无法读取卷 GUID 时保留空并在迁移阶段拦阻
            }

            bool isNtfs = fs.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
            bool supportsMft = isNtfs && isAdmin;
            string reason = !isNtfs
                ? $"文件系统为 {fs}（仅支持普通遍历只读浏览，禁止执行迁移）"
                : !isAdmin
                    ? "当前非管理员权限，无法打开原始 NTFS 卷，将降级为普通目录遍历"
                    : "支持只读原始 NTFS MFT 高速扫描";

            volumes.Add(new ScanVolumeInfo(root, label, guid, serial, fs, total, free, supportsMft, reason));
        }

        return volumes;
    }

    public async Task<ScanSnapshot> ScanVolumeAsync(
        string driveRoot,
        bool allowFallbackToDirectoryWalk = true,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        string normalizedRoot = PathIdentityService.Normalize(driveRoot.EndsWith('\\') ? driveRoot : driveRoot + "\\");
        ScanVolumeInfo volume = ListVolumes()
            .FirstOrDefault(v => v.DriveRoot.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException($"未找到可用的本地固定驱动器：{normalizedRoot}");

        if (volume.SupportsRawMft)
        {
            try
            {
                return await _mftScanner.ScanAsync(volume, progress, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (allowFallbackToDirectoryWalk)
            {
                string reason = $"原始 MFT 扫描不可用，已显式降级为普通目录遍历：{ex.Message}";
                progress?.Report(new ScanProgress(ScanPhase.ProbingVolume, ScanBackendKind.DirectoryWalkFallback, 0, null, reason));
                return await _walkScanner.ScanAsync(volume, volume.DriveRoot, reason, progress, ct).ConfigureAwait(false);
            }
        }

        if (!allowFallbackToDirectoryWalk)
        {
            throw new IOException($"无法使用原始 MFT 扫描：{volume.CapabilityReason}");
        }

        return await _walkScanner.ScanAsync(volume, volume.DriveRoot, volume.CapabilityReason, progress, ct).ConfigureAwait(false);
    }
}
