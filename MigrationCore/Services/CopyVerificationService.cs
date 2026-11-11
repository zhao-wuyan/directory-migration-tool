using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text.Json;
using MigrationCore.Models;

namespace MigrationCore.Services;

public sealed class CopyVerificationService
{
    private const AccessControlSections SecuritySections = AccessControlSections.All;
    private const FileAttributes SupportedAttributes = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System
        | FileAttributes.Archive | FileAttributes.Normal | FileAttributes.Temporary | FileAttributes.NotContentIndexed | FileAttributes.Directory;
    private readonly PathIdentityService _identity = new();
    private static readonly EnumerationOptions Enumeration = new()
    {
        AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false, ReturnSpecialDirectories = false
    };

    // 路径无关的规范摘要：重命名后仍可与持久化基线比较，不重新认领当前集合。
    public static string Fingerprint(FileManifest manifest, bool includeIdentity)
    {
        using var hash = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartArray();
            json.WriteNumberValue((int)manifest.Mode);
            foreach (var pair in manifest.Entries.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                FileManifestEntry item = pair.Value;
                json.WriteStartArray();
                json.WriteStringValue(pair.Key.ToUpperInvariant());
                json.WriteBooleanValue(item.IsDirectory);
                json.WriteNumberValue(item.Length);
                json.WriteNumberValue(item.LastWriteTimeUtc.Ticks);
                json.WriteNumberValue((int)item.Attributes);
                json.WriteStringValue(item.AclSddl);
                json.WriteStringValue(item.Hash);
                foreach (var ads in item.AlternateStreams)
                {
                    json.WriteStartArray();
                    json.WriteStringValue(ads.Name);
                    json.WriteNumberValue(ads.Length);
                    json.WriteStringValue(ads.Hash);
                    json.WriteEndArray();
                }
                if (includeIdentity)
                {
                    json.WriteStartArray();
                    json.WriteStringValue(item.Identity.VolumeGuid);
                    json.WriteNumberValue(item.Identity.VolumeSerial);
                    json.WriteNumberValue(item.Identity.FileId);
                    json.WriteBooleanValue(item.Identity.IsReparsePoint);
                    json.WriteBooleanValue(item.Identity.IsDirectory);
                    json.WriteEndArray();
                }
                json.WriteEndArray();
            }
            json.WriteEndArray();
        }
        stream.FlushFinalBlock();
        return Convert.ToHexString(hash.Hash!);
    }

    public async Task<FileManifest> CaptureAsync(string root, VerificationMode mode, CancellationToken ct)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        root = PathIdentityService.Normalize(root);
        MigrationRuntime.AssertDataPath(root);
        DirectoryIdentity rootIdentity = _identity.ReadDirectory(root, false);
        if (rootIdentity.IsReparsePoint) throw new IOException($"不能核对未知重解析目录：{root}");
        var entries = new Dictionary<string, FileManifestEntry>(StringComparer.OrdinalIgnoreCase);
        var directorySecurity = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<FileSystemInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.TryPop(out FileSystemInfo? info))
        {
            FileManifestEntry entry = await CaptureEntryAsync(info, root, mode, directorySecurity, FileShare.Read, ct).ConfigureAwait(false);
            if (!entries.TryAdd(entry.RelativePath, entry)) throw new IOException($"相对路径存在大小写歧义：{entry.RelativePath}");
            if (entry.IsDirectory)
            {
                foreach (FileSystemInfo child in ((DirectoryInfo)info).EnumerateFileSystemInfos("*", Enumeration)) pending.Push(child);
            }
        }
        _identity.RequireSame(root, rootIdentity);
        return new FileManifest(root, rootIdentity, mode, entries);
    }

    internal Task<FileManifestEntry> CaptureDeletionEntryAsync(string path, string root, bool directory, VerificationMode mode, CancellationToken ct) =>
        CaptureEntryAsync(directory ? new DirectoryInfo(path) : new FileInfo(path), root, mode, null, FileShare.Read | FileShare.Delete, ct);

    private async Task<FileManifestEntry> CaptureEntryAsync(FileSystemInfo info, string root, VerificationMode mode,
        Dictionary<string, byte[]>? directorySecurity, FileShare share, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        DirectoryIdentity id = _identity.ReadEntry(info.FullName);
        FileAttributes attributes = info.Attributes;
        if (id.IsReparsePoint || (attributes & ~SupportedAttributes) != 0)
            throw new IOException($"存在无法安全保存或核对的重解析点/属性：{info.FullName} ({attributes})");
        bool directory = id.IsDirectory;
        long length = directory ? 0 : ((FileInfo)info).Length;
        string relative = info.FullName.Equals(root, StringComparison.OrdinalIgnoreCase) ? "" : Path.GetRelativePath(root, info.FullName);
        FileSystemSecurity security = directory ? ((DirectoryInfo)info).GetAccessControl(SecuritySections) : ((FileInfo)info).GetAccessControl(SecuritySections);
        byte[] descriptor = security.GetSecurityDescriptorBinaryForm();
        string parentPath = Path.GetDirectoryName(info.FullName) ?? throw new IOException("无法定位 ACL 父目录。");
        byte[] parentSecurity = CanonicalDirectorySecurity(parentPath, root, directorySecurity);
        (string acl, byte[]? canonicalDirectory) = CanonicalAcl(descriptor, parentSecurity, directory, directory && directorySecurity is not null);
        if (canonicalDirectory is not null) directorySecurity![info.FullName] = canonicalDirectory;
        List<AlternateStreamManifest>? streams = null;
        foreach (var stream in EnumerateStreams(info.FullName))
        {
            ct.ThrowIfCancellationRequested();
            string? hash = mode == VerificationMode.ContentHash
                ? await HashAsync(info.FullName + stream.Name[..^6], stream.Length, ct, share).ConfigureAwait(false) : null;
            (streams ??= []).Add(new AlternateStreamManifest(stream.Name, stream.Length, hash));
        }
        streams?.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
        string? contentHash = mode == VerificationMode.ContentHash && !directory
            ? await HashAsync(info.FullName, length, ct, share).ConfigureAwait(false) : null;
        var entry = new FileManifestEntry(relative, directory, length, info.LastWriteTimeUtc,
            attributes, acl, streams?.ToArray() ?? [], contentHash, id);
        _identity.RequireSame(info.FullName, id);
        return entry;
    }

    internal static bool SameEntry(FileManifestEntry expected, FileManifestEntry actual, bool identity, bool writeTime = true) =>
        expected.IsDirectory == actual.IsDirectory && expected.Length == actual.Length
        && (!writeTime || expected.LastWriteTimeUtc == actual.LastWriteTimeUtc) && expected.Attributes == actual.Attributes
        && expected.AclSddl == actual.AclSddl && expected.Hash == actual.Hash
        && expected.AlternateStreams.SequenceEqual(actual.AlternateStreams) && (!identity || expected.Identity == actual.Identity);

    public async Task<VerificationSummary> VerifyAsync(FileManifest sourceBefore, string sourceRoot, string targetRoot,
        VerificationMode mode, CancellationToken ct)
    {
        if (sourceBefore.Mode != mode) throw new ArgumentException("清单与核对级别不一致。", nameof(mode));
        var mismatches = new List<string>();
        try
        {
            FileManifest sourceAfter = await CaptureAsync(sourceRoot, mode, ct).ConfigureAwait(false);
            FileManifest target = await CaptureAsync(targetRoot, mode, ct).ConfigureAwait(false);
            if (sourceAfter.RootIdentity != sourceBefore.RootIdentity) mismatches.Add("源实体身份变化。");
            Compare(sourceBefore, sourceAfter, true, "源在复制期间变化", mismatches);
            Compare(sourceBefore, target, false, "目标核对失败", mismatches);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            mismatches.Add($"无法完成必要核对：{ex.Message}");
        }
        return new VerificationSummary { Mode = mode, IsMatch = mismatches.Count == 0, Mismatches = mismatches.ToArray() };
    }

    private static void Compare(FileManifest expected, FileManifest actual, bool compareIdentity,
        string scope, List<string> mismatches)
    {
        foreach (var pair in expected.Entries)
        {
            if (!actual.Entries.TryGetValue(pair.Key, out FileManifestEntry? found))
            {
                mismatches.Add($"{scope}：缺少 {pair.Key}");
                continue;
            }
            FileManifestEntry item = pair.Value;
            if (!SameEntry(item, found, compareIdentity))
                mismatches.Add($"{scope}：类型/长度/时间/属性/ADS/ACL/内容或身份不符 {pair.Key}");
        }
        foreach (string path in actual.Entries.Keys)
            if (!expected.Entries.ContainsKey(path)) mismatches.Add($"{scope}：出现未知额外项 {path}");
    }

    private static async Task<string> HashAsync(string path, long expectedLength, CancellationToken ct, FileShare share = FileShare.Read)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, share,
            1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length != expectedLength) throw new IOException($"读取内容前长度已经变化：{path}");
        byte[] digest = await SHA256.HashDataAsync(input, ct).ConfigureAwait(false);
        if (input.Length != expectedLength) throw new IOException($"读取内容期间长度变化：{path}");
        return Convert.ToHexString(digest);
    }

    private byte[] CanonicalDirectorySecurity(string path, string root, Dictionary<string, byte[]>? cache)
    {
        if (cache is not null && cache.TryGetValue(path, out byte[]? known)) return known;
        // 继承语义取决于父目录，不能混用原始和规范化后的父描述符。
        var pending = new Stack<string>();
        byte[]? parent = null;
        string? current = path;
        while (current is not null)
        {
            if (cache is not null && cache.TryGetValue(current, out parent)) break;
            if (!PathIdentityService.IsWithin(current, root))
            {
                if (_identity.ReadDirectory(current, false).IsReparsePoint) throw new IOException($"ACL 父目录不是普通目录：{current}");
                parent = new DirectoryInfo(current).GetAccessControl(SecuritySections).GetSecurityDescriptorBinaryForm();
                cache?.Add(current, parent);
                break;
            }
            pending.Push(current);
            current = Path.GetDirectoryName(current);
        }
        while (pending.TryPop(out current))
        {
            if (_identity.ReadDirectory(current, false).IsReparsePoint) throw new IOException($"ACL 祖先不是普通目录：{current}");
            byte[] descriptor = new DirectoryInfo(current).GetAccessControl(SecuritySections).GetSecurityDescriptorBinaryForm();
            parent = parent is null ? descriptor : CanonicalAcl(descriptor, parent, true, true).DirectoryDescriptor!;
            cache?.Add(current, parent);
        }
        return parent ?? throw new IOException("无法核对完整 ACL 父目录链。");
    }

    private static (string Sddl, byte[]? DirectoryDescriptor) CanonicalAcl(byte[] descriptor, byte[] parent, bool directory, bool captureDescriptor)
    {
        // 由 Windows 转为等价继承格式，不把旧式/自动继承布局误当成权限变化。
        // 保留 Owner、Group、所有 ACE 与审计信息；不手工删除或排序访问规则。
        var mapping = new GenericMapping { Read = 0x120089, Write = 0x120116, Execute = 0x1200A0, All = 0x1F01FF };
        if (!ConvertToAutoInheritPrivateObjectSecurity(parent, descriptor, out IntPtr converted,
                IntPtr.Zero, directory, ref mapping))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法核对 ACL 的等价继承格式。");
        try
        {
            if (!ConvertSecurityDescriptorToStringSecurityDescriptorW(converted, 1, 0xF, out IntPtr text, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法生成完整 ACL 摘要。");
            try
            {
                string sddl = Marshal.PtrToStringUni(text) ?? throw new IOException("ACL 摘要缺失。");
                byte[]? canonical = null;
                if (captureDescriptor)
                {
                    uint length = GetSecurityDescriptorLength(converted);
                    if (length == 0) throw new IOException("规范化 ACL 描述符无效。");
                    canonical = new byte[checked((int)length)];
                    Marshal.Copy(converted, canonical, 0, canonical.Length);
                }
                return (sddl, canonical);
            }
            finally { LocalFree(text); }
        }
        finally { DestroyPrivateObjectSecurity(ref converted); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GenericMapping { public uint Read, Write, Execute, All; }

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertToAutoInheritPrivateObjectSecurity(byte[] parent, byte[] current,
        out IntPtr converted, IntPtr objectType, [MarshalAs(UnmanagedType.U1)] bool directory, ref GenericMapping mapping);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(IntPtr descriptor, uint revision,
        uint information, out IntPtr text, out uint length);
    [DllImport("advapi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyPrivateObjectSecurity(ref IntPtr descriptor);
    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    private static IEnumerable<(string Name, long Length)> EnumerateStreams(string path)
    {
        IntPtr handle = FindFirstStreamW(path, 0, out StreamData data, 0);
        if (handle == new IntPtr(-1))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 38) yield break;
            throw new Win32Exception(error, $"无法枚举必需的 ADS：{path}");
        }
        try
        {
            do
            {
                if (data.Name != "::$DATA")
                {
                    if (!data.Name.StartsWith(':') || !data.Name.EndsWith(":$DATA", StringComparison.Ordinal) || data.Size < 0)
                        throw new IOException($"无法核对的 ADS：{path} {data.Name}");
                    yield return (data.Name, data.Size);
                }
            } while (FindNextStreamW(handle, out data));
            int error = Marshal.GetLastWin32Error();
            if (error != 38) throw new Win32Exception(error, $"ADS 枚举未完成：{path}");
        }
        finally { FindClose(handle); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StreamData
    {
        public long Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string Name;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr FindFirstStreamW(string fileName, int infoLevel, out StreamData data, uint flags);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStreamW(IntPtr handle, out StreamData data);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr handle);
}
