using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

// Also compiled by Windows PowerShell 5.1 Add-Type; keep the syntax C# 5 compatible.
public static class DiskLabPath
{
    private const uint ShareReadWriteDelete = 7;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint VolumeNameGuid = 1;
    private const uint LockVolumeControl = 0x00090018;
    private const string VolumePrefix = @"\\?\Volume{";
    private const uint DirectoryAttribute = (uint)FileAttributes.Directory;
    private const uint ReparseAttribute = (uint)FileAttributes.ReparsePoint;

    public sealed class Identity
    {
        public string FinalPath { get; private set; }
        public uint Serial { get; private set; }
        public ulong FileId { get; private set; }
        public uint Attributes { get; private set; }
        public uint LinkCount { get; private set; }

        internal Identity(string finalPath, uint serial, ulong fileId, uint attributes, uint linkCount)
        {
            FinalPath = finalPath;
            Serial = serial;
            FileId = fileId;
            Attributes = attributes;
            LinkCount = linkCount;
        }
    }

    // This is an identity reader, not authorization. In particular, following a leaf
    // is only for callers that created and already verified that exact link/target.
    public static Identity ReadIdentity(string path, bool followLink)
    {
        ParseDosPath(path);
        Identity leaf = Walk(path, null, 0, false, true);
        return followLink && (leaf.Attributes & ReparseAttribute) != 0 ? Read(path, true, false) : leaf;
    }

    public static Identity ReadVolumeRoot(string volumeGuidPath)
    {
        RequireVolumeGuidRoot(volumeGuidPath);
        Identity root = ReadNative(volumeGuidPath, false, false);
        if ((root.Attributes & (DirectoryAttribute | ReparseAttribute)) != DirectoryAttribute ||
            !String.Equals(root.FinalPath, volumeGuidPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Not the registered ordinary volume root: " + volumeGuidPath);
        return root;
    }

    public static Identity Check(string path, string allowedRoot, string volumeGuidPath, uint serial,
        bool allowMissingLeaf, bool allowReparseLeaf)
    {
        ParseDosPath(path);
        ParseDosPath(allowedRoot);
        RequireVolumeGuidRoot(volumeGuidPath);
        if (!IsWithin(path, allowedRoot))
            throw new IOException("Outside allowed root: " + path);

        // The lexical gate is case insensitive. When the spelling differs, also resolve
        // the registered spelling so case-sensitive NTFS directories cannot alias it.
        Identity allowed = null;
        if (!IsWithinOrdinal(path, allowedRoot))
            allowed = Walk(allowedRoot, volumeGuidPath, serial, false, false);
        Identity result = Walk(path, volumeGuidPath, serial, allowMissingLeaf, allowReparseLeaf);
        if (allowed != null)
        {
            Identity actualAncestor = path.Length == allowedRoot.Length
                ? result : Walk(path.Substring(0, allowedRoot.Length), volumeGuidPath, serial, false, false);
            if (actualAncestor == null || actualAncestor.FileId != allowed.FileId ||
                !String.Equals(actualAncestor.FinalPath, allowed.FinalPath, StringComparison.Ordinal))
                throw new IOException("Outside allowed root: " + path);
        }
        return result;
    }

    public static Identity AssertOrdinaryPath(string path, bool allowMissingLeaf)
    {
        ParseDosPath(path);
        return Walk(path, null, 0, allowMissingLeaf, false);
    }

    // Pure parser helpers for callers that need to reject input before any OS lookup.
    public static bool IsWithin(string path, string root)
    {
        ParseDosPath(path);
        ParseDosPath(root);
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(root.Length == 3 ? root : root + "\\", StringComparison.OrdinalIgnoreCase);
    }

    public static void RequireVolumeGuidRoot(string path)
    {
        Guid guid;
        if (path == null || path.Length != VolumePrefix.Length + 38 ||
            !path.StartsWith(VolumePrefix, StringComparison.OrdinalIgnoreCase) ||
            !path.EndsWith("}\\", StringComparison.Ordinal) ||
            !Guid.TryParseExact(path.Substring(VolumePrefix.Length, 36), "D", out guid) || guid == Guid.Empty ||
            !String.Equals(path.Substring(VolumePrefix.Length, 36), guid.ToString("D"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Invalid volume GUID root: " + path);
    }

    public static SafeFileHandle LockVolume(string volumeGuidPath)
    {
        RequireVolumeGuidRoot(volumeGuidPath);

        // The caller must establish the registered volume's lab ownership first.
        // The GUID device handle does not depend on a mutable drive-letter mapping.
        string volume = volumeGuidPath.Substring(0, volumeGuidPath.Length - 1);
        SafeFileHandle handle = CreateFileW(volume, 0xC0000000, 3, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw NativeError("Opening volume for lock", volume, error);
        }
        uint returned;
        if (!DeviceIoControl(handle, LockVolumeControl, IntPtr.Zero, 0, IntPtr.Zero, 0, out returned, IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw NativeError("FSCTL_LOCK_VOLUME", volume, error);
        }
        return handle; // Closing this handle releases the lock.
    }

    private static Identity Walk(string path, string expectedGuidRoot, uint expectedSerial,
        bool allowMissingLeaf, bool allowReparseLeaf)
    {
        string driveRoot = path.Substring(0, 3);
        Identity root = Read(driveRoot, false, false);
        RequireVolumeGuidRoot(root.FinalPath); // A SUBST drive that resolves to a subdirectory fails here.
        if ((root.Attributes & (DirectoryAttribute | ReparseAttribute)) != DirectoryAttribute)
            throw new IOException("Not an ordinary drive root: " + driveRoot);
        if (expectedGuidRoot != null && !String.Equals(root.FinalPath, expectedGuidRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Volume GUID mismatch: " + driveRoot);
        if (expectedGuidRoot != null && root.Serial != expectedSerial)
            throw new IOException("Volume serial mismatch: " + driveRoot);
        if (path.Length == 3)
            return root;

        for (int start = 3; ; )
        {
            int end = path.IndexOf('\\', start);
            bool leaf = end < 0;
            string prefix = leaf ? path : path.Substring(0, end);
            Identity current = Read(prefix, false, leaf && allowMissingLeaf);
            if (current == null)
                return null; // Only ERROR_FILE_NOT_FOUND on the last component is permitted.
            if ((current.Attributes & ReparseAttribute) != 0 && !(leaf && allowReparseLeaf))
                throw new IOException("Reparse point rejected: " + prefix);
            if (!leaf && (current.Attributes & DirectoryAttribute) == 0)
                throw new IOException("Ancestor is not a directory: " + prefix);
            if (current.Serial != root.Serial || !String.Equals(current.FinalPath,
                root.FinalPath + prefix.Substring(3), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Final GUID path or volume serial changed: " + prefix);
            if (leaf)
                return current;
            start = end + 1;
        }
    }

    private static Identity Read(string path, bool followLink, bool allowMissing)
    {
        return ReadNative(@"\\?\" + path, followLink, allowMissing);
    }

    private static Identity ReadNative(string path, bool followLink, bool allowMissing)
    {
        using (SafeFileHandle handle = CreateFileW(path, 0, ShareReadWriteDelete, IntPtr.Zero,
            OpenExisting, BackupSemantics | (followLink ? 0U : OpenReparsePoint), IntPtr.Zero))
        {
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                if (allowMissing && error == 2)
                    return null;
                throw NativeError("Opening identity handle", path, error);
            }
            FileInformation info;
            if (!GetFileInformationByHandle(handle, out info))
                throw NativeError("Reading file identity", path, Marshal.GetLastWin32Error());

            StringBuilder name = new StringBuilder(512);
            uint length = GetFinalPathNameByHandleW(handle, name, (uint)name.Capacity, VolumeNameGuid);
            if (length >= name.Capacity)
            {
                name = new StringBuilder(checked((int)length + 1));
                length = GetFinalPathNameByHandleW(handle, name, (uint)name.Capacity, VolumeNameGuid);
            }
            if (length == 0)
                throw NativeError("Reading final GUID path", path, Marshal.GetLastWin32Error());
            if (length >= name.Capacity || length < VolumePrefix.Length + 38)
                throw new IOException("Invalid final GUID path: " + path);
            string final = name.ToString();
            RequireVolumeGuidRoot(final.Substring(0, VolumePrefix.Length + 38));
            return new Identity(final, info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow,
                info.FileAttributes, info.NumberOfLinks);
        }
    }

    private static void ParseDosPath(string path)
    {
        if (path == null || path.Length < 3 || path.Length > 32760 || !IsDriveLetter(path[0]) ||
            path[1] != ':' || path[2] != '\\')
            throw new IOException("Expected an absolute DOS path: " + path);
        if (path.Length == 3)
            return;

        for (int start = 3; ; )
        {
            int end = path.IndexOf('\\', start);
            bool leaf = end < 0;
            if (leaf)
                end = path.Length;
            if (end == start || end - start > 255 || path[start] == ' ' || path[end - 1] == ' ' || path[end - 1] == '.')
                throw new IOException("Invalid DOS path component: " + path);
            for (int i = start; i < end; i++)
            {
                char c = path[i];
                if (c < 32 || c == '/' || c == ':' || c == '"' || c == '<' || c == '>' || c == '|' || c == '?' || c == '*')
                    throw new IOException("Invalid DOS path component: " + path);
            }
            int dot = path.IndexOf('.', start, end - start);
            string stem = path.Substring(start, (dot < 0 ? end : dot) - start).TrimEnd(' ').ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || stem == "CLOCK$" ||
                stem == "CONIN$" || stem == "CONOUT$" ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                 "0123456789¹²³".IndexOf(stem[3]) >= 0))
                throw new IOException("Reserved DOS device component: " + path);
            if (leaf)
                return;
            start = end + 1;
        }
    }

    private static bool IsWithinOrdinal(string path, string root)
    {
        return path.Equals(root, StringComparison.Ordinal) ||
            path.StartsWith(root.Length == 3 ? root : root + "\\", StringComparison.Ordinal);
    }

    private static bool IsDriveLetter(char c)
    {
        return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
    }

    private static IOException NativeError(string operation, string path, int error)
    {
        return new IOException(operation + " failed for " + path + " (Win32 " + error + ").", new Win32Exception(error));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, uint inputSize,
        IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
}
