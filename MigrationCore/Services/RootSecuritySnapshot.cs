using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace MigrationCore.Services;

internal static class RootSecuritySnapshot
{
    internal static RawSecurityDescriptor Create(string sddl)
    {
        var descriptor = new RawSecurityDescriptor(sddl);
        // 只冻结根目录的继承边界，保留每个 ACE 的内容、顺序与来源标志。
        descriptor.SetFlags(descriptor.ControlFlags | ControlFlags.DiscretionaryAclProtected | ControlFlags.SystemAclProtected);
        return descriptor;
    }

    internal static void Apply(string path, string sddl)
    {
        var descriptor = Create(sddl);
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        using var current = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Duplicate);
        // 使用独立的模拟令牌启用审计权限，不修改进程令牌，避免并发任务互相影响。
        if (!DuplicateTokenEx(current.AccessToken, 0x2C, IntPtr.Zero, 2, 2, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建根权限恢复令牌。");
        using (token)
        {
            if (!LookupPrivilegeValueW(null, "SeSecurityPrivilege", out var luid))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法定位审计权限。");
            var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
            bool adjusted = AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
            int error = Marshal.GetLastWin32Error();
            if (!adjusted || error != 0)
                throw new Win32Exception(error, "无法启用根目录审计权限。");
            WindowsIdentity.RunImpersonated(token, () =>
            {
                // SetAccessControl/SetNamedSecurityInfo 会重建继承并重排规则；此处需要原样恢复空根目录的描述符。
                // SetFileSecurity 不向已有子项传播，调用者必须先确认 staging 为空。
                if (!SetFileSecurityW(path, 0xF, bytes))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法保留根目录安全描述符：{path}");
            });
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle existing, uint access, IntPtr attributes,
        int impersonationLevel, int tokenType, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivileges privileges, uint length, IntPtr previous, IntPtr returnedLength);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileSecurityW(string path, uint information, byte[] descriptor);
}
