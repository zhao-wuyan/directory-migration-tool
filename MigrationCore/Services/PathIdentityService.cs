using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using MigrationCore.Models;

namespace MigrationCore.Services;

public sealed class PathIdentityService
{
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\'
            || path.Contains('/') || path.Length > 32760)
            throw new IOException($"需要本地绝对 DOS 路径：{path}");
        string result = path.Length == 3 ? path : path.TrimEnd('\\');
        foreach (string component in result[3..].Split('\\', StringSplitOptions.None))
        {
            if (result.Length == 3) break;
            if (string.IsNullOrWhiteSpace(component) || component is "." or ".." || component.EndsWith('.')
                || component.EndsWith(' ') || component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new IOException($"无效路径分量：{path}");
            string stem = component.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal)
                    || stem.StartsWith("LPT", StringComparison.Ordinal)) && "0123456789¹²³".Contains(stem[3])))
                throw new IOException($"保留的 DOS 名称：{path}");
        }
        return result;
    }

    public static bool IsWithin(string path, string root)
    {
        path = Normalize(path); root = Normalize(root);
        return path.Equals(root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root.EndsWith('\\') ? root : root + "\\", StringComparison.OrdinalIgnoreCase);
    }

    public DirectoryIdentity ReadDirectory(string path, bool followLink)
    {
        DirectoryIdentity identity = ReadEntry(path, followLink);
        if (!identity.IsDirectory) throw new IOException($"不是目录：{path}");
        return identity;
    }

    public DirectoryIdentity ReadEntry(string path, bool followLink = false) =>
        ReadEntryWithLinkCount(path, followLink).Identity;

    public (DirectoryIdentity Identity, uint LinkCount) ReadEntryWithLinkCount(string path, bool followLink = false)
    {
        path = Normalize(path);
        for (int end = 3; end < path.Length;)
        {
            DirectoryIdentity parent = ReadNative(path[..end], false);
            if (!parent.IsDirectory || parent.IsReparsePoint) throw new IOException($"未知重解析祖先：{path[..end]}");
            int next = path.IndexOf('\\', end + 1);
            if (next < 0) break;
            end = next;
        }
        return ReadNativeWithLinkCount(path, followLink);
    }

    public void RequireSame(string path, DirectoryIdentity? expected, bool followLink = false)
    {
        if (expected is null || ReadEntry(path, followLink) != expected)
            throw new IOException($"路径身份不符：{path}");
    }

    public DirectoryIdentity? ReadIfExists(string path)
    {
        try { return ReadEntry(path); }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 2 or 3) { return null; }
    }

    /// <summary>
    /// 仅读取单个路径自身身份（不重复向上遍历盘符祖先链）。
    /// 仅供调用方已确认父目录非重解析点且位于预期卷时复用，避免批量核验时重复触发数十万次祖先 CreateFileW。
    /// </summary>
    public DirectoryIdentity? ReadDirectIfExists(string path, bool followLink = false)
    {
        path = Normalize(path);
        try { return ReadNative(path, followLink); }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 2 or 3) { return null; }
    }

    /// <summary>
    /// 单次句柄读取文件自身身份、大小与修改时间（不重复遍历祖先链），用于极小目录或批量目录枚举回退探测。
    /// </summary>
    internal bool TryReadDirectFileMetadata(
        string path,
        out DirectoryIdentity? identity,
        out long length,
        out long lastWriteUtcTicks)
    {
        identity = null;
        length = 0;
        lastWriteUtcTicks = 0;
        try
        {
            path = Normalize(path);
            using SafeFileHandle handle = CreateFileW(@"\\?\" + path, 0, 7, IntPtr.Zero, 3,
                0x02000000U | 0x00200000U, IntPtr.Zero);
            if (handle.IsInvalid) return false;
            if (!GetFileInformationByHandle(handle, out FileInformation info)) return false;

            var name = new StringBuilder(512);
            uint nameLen = GetFinalPathNameByHandleW(handle, name, (uint)name.Capacity, 1);
            if (nameLen >= name.Capacity)
            {
                name = new StringBuilder(checked((int)nameLen + 1));
                nameLen = GetFinalPathNameByHandleW(handle, name, (uint)name.Capacity, 1);
            }
            if (nameLen == 0) return false;

            string final = name.ToString();
            const int rootLength = 49;
            if (nameLen >= name.Capacity || final.Length < rootLength || !final.StartsWith(@"\\?\Volume{", StringComparison.Ordinal)
                || final[47] != '}' || final[48] != '\\' || !Guid.TryParseExact(final.AsSpan(11, 36), "D", out Guid volume) || volume == Guid.Empty)
            {
                return false;
            }

            identity = new DirectoryIdentity(
                final[..rootLength].ToUpperInvariant(),
                info.VolumeSerialNumber,
                ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow,
                (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0,
                (info.Attributes & (uint)FileAttributes.Directory) != 0);
            length = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
            long fileTime = ((long)info.LastWriteTime.dwHighDateTime << 32) | (uint)info.LastWriteTime.dwLowDateTime;
            lastWriteUtcTicks = fileTime > 0 ? DateTime.FromFileTimeUtc(fileTime).Ticks : 0;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void RequireMissing(string path)
    {
        if (ReadIfExists(path) is not null) throw new IOException($"路径已存在，不得覆盖：{path}");
    }

    public void EnsureOrdinaryDirectory(string path)
    {
        path = Normalize(path);
        if (path.Length > 3)
        {
            string parent = Path.GetDirectoryName(path)!;
            EnsureOrdinaryDirectory(parent);
            if (ReadIfExists(path) is null) Directory.CreateDirectory(path);
        }
        DirectoryIdentity identity = ReadDirectory(path, false);
        if (identity.IsReparsePoint) throw new IOException($"状态目录不能是重解析点：{path}");
    }

    private static DirectoryIdentity ReadNative(string path, bool followLink) =>
        ReadNativeWithLinkCount(path, followLink).Identity;

    private static (DirectoryIdentity Identity, uint LinkCount) ReadNativeWithLinkCount(string path, bool followLink)
    {
        using SafeFileHandle handle = CreateFileW(@"\\?\" + path, 0, 7, IntPtr.Zero, 3,
            0x02000000U | (followLink ? 0U : 0x00200000U), IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), $"读取对象身份失败：{path}");
        if (!GetFileInformationByHandle(handle, out FileInformation info))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"读取文件 ID 失败：{path}");
        var name = new StringBuilder(512);
        uint length = GetFinalPathNameByHandleW(handle, name, (uint)name.Capacity, 1);
        if (length >= name.Capacity)
        {
            name = new StringBuilder(checked((int)length + 1));
            length = GetFinalPathNameByHandleW(handle, name, (uint)name.Capacity, 1);
        }
        if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), $"读取最终卷路径失败：{path}");
        string final = name.ToString();
        const int rootLength = 49;
        if (length >= name.Capacity || final.Length < rootLength || !final.StartsWith(@"\\?\Volume{", StringComparison.Ordinal)
            || final[47] != '}' || final[48] != '\\' || !Guid.TryParseExact(final.AsSpan(11, 36), "D", out Guid volume) || volume == Guid.Empty)
            throw new IOException($"无法核对本地卷 GUID：{path}");
        var identity = new DirectoryIdentity(final[..rootLength].ToUpperInvariant(), info.VolumeSerialNumber,
            ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow,
            (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0,
            (info.Attributes & (uint)FileAttributes.Directory) != 0);
        return (identity, Math.Max(1u, info.NumberOfLinks));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder name, uint capacity, uint flags);
}
