using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MigrationCore.Services.Cleanup;

/// <summary>
/// 占用指定文件的进程摘要（仅用于删除失败后的冷路径只读诊断）。
/// </summary>
public sealed record LockingProcessInfo(int ProcessId, string ProcessName, string AppDescription)
{
    public string DisplayLabel => string.IsNullOrWhiteSpace(AppDescription)
        || AppDescription.Equals(ProcessName, StringComparison.OrdinalIgnoreCase)
        || AppDescription.Equals(Path.GetFileNameWithoutExtension(ProcessName), StringComparison.OrdinalIgnoreCase)
        ? $"{ProcessName} (PID {ProcessId})"
        : $"{ProcessName} / {AppDescription} (PID {ProcessId})";
}

/// <summary>
/// 基于 Windows Restart Manager API（rstrtmgr.dll）的只读文件占用进程查询器。
/// 严格仅在永久删除失败后的冷路径调用，不以写权限打开任何目标文件，零热路径开销、零杀软敏感行为。
/// </summary>
public static class FileLockInspector
{
    private const int ErrorSuccess = 0;
    private const int ErrorMoreData = 234;
    private const int RmMaxAppName = 255;
    private const int RmMaxSvcName = 63;

    /// <summary>
    /// 查询当前锁定指定文件路径的活跃进程列表（异常安全，失败时返回空数组，绝不抛出异常）。
    /// </summary>
    public static LockingProcessInfo[] FindLockingProcesses(string filePath, int maxResults = 5)
    {
        if (string.IsNullOrWhiteSpace(filePath) || maxResults <= 0)
        {
            return [];
        }

        // Restart Manager 要求常规路径（去掉 \\?\ 扩展前缀，否则部分系统版本无法匹配句柄表）
        string cleanPath = StripExtendedPrefix(filePath);
        if (string.IsNullOrWhiteSpace(cleanPath))
        {
            return [];
        }

        uint sessionHandle = 0;
        bool sessionStarted = false;
        try
        {
            string sessionKey = Guid.NewGuid().ToString("N");
            int startRc = RmStartSession(out sessionHandle, 0, sessionKey);
            if (startRc != ErrorSuccess)
            {
                return [];
            }
            sessionStarted = true;

            string[] resources = [cleanPath];
            int regRc = RmRegisterResources(sessionHandle, (uint)resources.Length, resources, 0, null, 0, null);
            if (regRc != ErrorSuccess)
            {
                return [];
            }

            uint procInfoNeeded = 0;
            uint procInfoCount = 0;
            uint rebootReasons = 0;

            int listRc = RmGetList(sessionHandle, out procInfoNeeded, ref procInfoCount, null, ref rebootReasons);
            if (listRc != ErrorMoreData || procInfoNeeded == 0)
            {
                return [];
            }

            var processInfos = new RM_PROCESS_INFO[procInfoNeeded];
            procInfoCount = procInfoNeeded;
            listRc = RmGetList(sessionHandle, out procInfoNeeded, ref procInfoCount, processInfos, ref rebootReasons);
            if (listRc != ErrorSuccess || procInfoCount == 0)
            {
                return [];
            }

            var seenPids = new HashSet<int>();
            var results = new List<LockingProcessInfo>(Math.Min((int)procInfoCount, maxResults));
            for (int i = 0; i < procInfoCount && results.Count < maxResults; i++)
            {
                int pid = processInfos[i].Process.dwProcessId;
                if (pid <= 0 || !seenPids.Add(pid))
                {
                    continue;
                }

                string appDesc = (processInfos[i].strAppName ?? string.Empty).Trim();
                string exeName = TryResolveProcessExeName(pid);
                if (string.IsNullOrWhiteSpace(exeName))
                {
                    exeName = string.IsNullOrWhiteSpace(appDesc) ? $"PID {pid}" : appDesc;
                }

                results.Add(new LockingProcessInfo(pid, exeName, appDesc));
            }

            return results.ToArray();
        }
        catch
        {
            return [];
        }
        finally
        {
            if (sessionStarted)
            {
                try { _ = RmEndSession(sessionHandle); } catch { }
            }
        }
    }

    private static string StripExtendedPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path[4..];
        }
        return path;
    }

    private static string TryResolveProcessExeName(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            string name = proc.ProcessName;
            return string.IsNullOrWhiteSpace(name)
                ? string.Empty
                : (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : $"{name}.exe");
        }
        catch
        {
            return string.Empty;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    private enum RM_APP_TYPE
    {
        RmUnknownApp = 0,
        RmMainWindow = 1,
        RmOtherWindow = 2,
        RmService = 3,
        RmExplorer = 4,
        RmConsole = 5,
        RmCritical = 1000
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RmMaxAppName + 1)]
        public string strAppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RmMaxSvcName + 1)]
        public string strServiceShortName;

        public RM_APP_TYPE ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint pSessionHandle,
        uint nFiles,
        string[]? rgsFilenames,
        uint nApplications,
        RM_UNIQUE_PROCESS[]? rgApplications,
        uint nServices,
        string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint dwSessionHandle,
        out uint pnProcInfoNeeded,
        ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps,
        ref uint lpdwRebootReasons);
}
