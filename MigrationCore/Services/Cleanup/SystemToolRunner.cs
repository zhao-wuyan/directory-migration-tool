using System.Globalization;
using System.IO.Enumeration;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MigrationCore.Services.Cleanup;

/// <summary>
/// 垃圾清理模块共享的系统命令执行器（OwnedProcessJob 受管进程）、pnputil 输出解析器、
/// Windows Installer 注册表交叉核对器与有界目录体积估算器。
/// </summary>
internal static partial class SystemToolRunner
{
    internal sealed record ParsedDriverPackage(
        string PublishedName,
        string OriginalName,
        string ProviderName,
        string ClassName,
        string VersionText,
        Version ParsedVersion,
        DateTime DateStamp);

    [GeneratedRegex(@"^oem\d+\.inf$", RegexOptions.IgnoreCase)]
    internal static partial Regex OemInfRegex();

    internal static HashSet<string> CollectReferencedWindowsInstallerFiles()
    {
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void CollectLocalPackages(RegistryKey? rootKey, string subKeyPath)
        {
            if (rootKey is null) return;
            using RegistryKey? baseKey = rootKey.OpenSubKey(subKeyPath);
            if (baseKey is null) return;

            foreach (string childName in baseKey.GetSubKeyNames())
            {
                using RegistryKey? child = baseKey.OpenSubKey(childName);
                if (child is null) continue;

                // UserData\<SID>\Products\<ID>\InstallProperties\LocalPackage
                using (RegistryKey? products = child.OpenSubKey("Products"))
                {
                    if (products is not null)
                    {
                        foreach (string prodId in products.GetSubKeyNames())
                        {
                            using RegistryKey? props = products.OpenSubKey($@"{prodId}\InstallProperties");
                            AddPackageRef(refs, props?.GetValue("LocalPackage") as string);
                        }
                    }
                }

                // UserData\<SID>\Patches\<ID>\LocalPackage
                using (RegistryKey? patches = child.OpenSubKey("Patches"))
                {
                    if (patches is not null)
                    {
                        foreach (string patchId in patches.GetSubKeyNames())
                        {
                            using RegistryKey? patchKey = patches.OpenSubKey(patchId);
                            AddPackageRef(refs, patchKey?.GetValue("LocalPackage") as string);
                        }
                    }
                }
            }
        }

        CollectLocalPackages(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData");
        using (RegistryKey? wow = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
        {
            CollectLocalPackages(wow, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData");
        }

        // HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\Managed 补充
        using (RegistryKey? managed = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\Managed"))
        {
            if (managed is not null)
            {
                foreach (string sid in managed.GetSubKeyNames())
                {
                    using RegistryKey? patches = managed.OpenSubKey($@"{sid}\Installer\Patches");
                    if (patches is null) continue;
                    foreach (string pid in patches.GetSubKeyNames())
                    {
                        using RegistryKey? pk = patches.OpenSubKey(pid);
                        AddPackageRef(refs, pk?.GetValue("LocalPackage") as string);
                    }
                }
            }
        }

        return refs;
    }

    private static void AddPackageRef(HashSet<string> refs, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        try
        {
            string norm = PathIdentityService.Normalize(raw.Trim('"'));
            refs.Add(norm);
            refs.Add(Path.GetFileName(norm));
        }
        catch
        {
            refs.Add(Path.GetFileName(raw.Trim('"')));
        }
    }

    internal static bool IsWindowsUpdateServiceRunning()
    {
        try
        {
            string scExe = Path.Combine(Environment.SystemDirectory, "sc.exe");
            if (!File.Exists(scExe)) return false;
            string output = RunReadOnlySystemTool(scExe, ["query", "wuauserv"], TimeSpan.FromSeconds(5), CancellationToken.None);
            return output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static string RunReadOnlySystemTool(string exePath, string[] args, TimeSpan timeout, CancellationToken ct)
    {
        using var job = new OwnedProcessJob();
        job.Start(exePath, args, Environment.SystemDirectory, Encoding.UTF8);
        job.Resume();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        Task<string> outTask = job.Output.ReadToEndAsync(cts.Token);
        try
        {
            job.WaitForExitAsync(cts.Token).GetAwaiter().GetResult();
            return outTask.GetAwaiter().GetResult();
        }
        catch
        {
            try { job.Stop(); } catch { }
            throw;
        }
    }

    internal static async Task<(int ExitCode, string Output)> RunManagedSystemCommandAsync(string exePath, string[] args, CancellationToken ct)
    {
        using var job = new OwnedProcessJob();
        job.Start(exePath, args, Environment.SystemDirectory, Encoding.UTF8);
        job.Resume();

        using CancellationTokenRegistration cancelReg = ct.Register(() =>
        {
            try { job.Stop(); } catch { }
        });

        Task<string> stdOutTask = job.Output.ReadToEndAsync(ct);
        Task<string> stdErrTask = job.Error.ReadToEndAsync(ct);

        await job.WaitForExitAsync(ct).ConfigureAwait(false);
        string stdOut = await stdOutTask.ConfigureAwait(false);
        string stdErr = await stdErrTask.ConfigureAwait(false);
        string combined = string.IsNullOrWhiteSpace(stdErr) ? stdOut : $"{stdOut}\n{stdErr}";
        return (job.ExitCode, combined);
    }

    internal static List<ParsedDriverPackage> ParsePnpUtilDrivers(string output)
    {
        var list = new List<ParsedDriverPackage>();
        string? pub = null, orig = null, provider = null, cls = null, verLine = null;

        void Flush()
        {
            if (string.IsNullOrWhiteSpace(pub) || string.IsNullOrWhiteSpace(orig)) return;
            (DateTime dt, Version ver, string verTxt) = ParseDriverVersion(verLine ?? string.Empty);
            list.Add(new ParsedDriverPackage(pub, orig, provider ?? "Unknown", cls ?? "Unknown", verTxt, ver, dt));
            pub = orig = provider = cls = verLine = null;
        }

        foreach (string rawLine in output.Split(['\r', '\n']))
        {
            string line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
            {
                Flush();
                continue;
            }

            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string key = line[..colon].Trim();
            string val = line[(colon + 1)..].Trim();

            if (key.Contains("Published Name", StringComparison.OrdinalIgnoreCase) || key.Contains("发布名称", StringComparison.OrdinalIgnoreCase))
            {
                if (pub is not null) Flush();
                pub = val;
            }
            else if (key.Contains("Original Name", StringComparison.OrdinalIgnoreCase) || key.Contains("原始名称", StringComparison.OrdinalIgnoreCase))
            {
                orig = val;
            }
            else if (key.Contains("Provider Name", StringComparison.OrdinalIgnoreCase) || key.Contains("提供程序名称", StringComparison.OrdinalIgnoreCase))
            {
                provider = val;
            }
            else if (key.Contains("Class Name", StringComparison.OrdinalIgnoreCase) || key.Contains("类名", StringComparison.OrdinalIgnoreCase))
            {
                cls = val;
            }
            else if (key.Contains("Driver Version", StringComparison.OrdinalIgnoreCase) || key.Contains("驱动程序版本", StringComparison.OrdinalIgnoreCase))
            {
                verLine = val;
            }
        }
        Flush();
        return list;
    }

    private static (DateTime Date, Version Ver, string Text) ParseDriverVersion(string line)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        DateTime dt = DateTime.MinValue;
        Version ver = new(0, 0, 0, 0);
        string verText = line;
        foreach (string p in parts)
        {
            if (DateTime.TryParse(p, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsedDt))
            {
                dt = parsedDt;
            }
            else if (Version.TryParse(p, out Version? parsedVer))
            {
                ver = parsedVer;
                verText = p;
            }
        }
        return (dt, ver, verText);
    }

    internal static long EstimateDriverRepositorySize(string originalInfName)
    {
        try
        {
            string repo = Path.Combine(Environment.SystemDirectory, "DriverStore", "FileRepository");
            if (!Directory.Exists(repo)) return 0;
            string stem = Path.GetFileNameWithoutExtension(originalInfName);
            if (string.IsNullOrWhiteSpace(stem)) return 0;
            string? match = Directory.EnumerateDirectories(repo, stem + ".inf_*").FirstOrDefault();
            return match is null ? 0 : EstimateDirectoryBytesBounded(match, 2000, CancellationToken.None);
        }
        catch
        {
            return 0;
        }
    }

    internal static long EstimateDirectoryBytesBounded(string rootDir, int maxFiles, CancellationToken ct)
    {
        long total = 0;
        int count = 0;
        var enumOptions = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false
        };
        var stack = new Stack<string>();
        stack.Push(rootDir);

        while (stack.Count > 0 && count < maxFiles)
        {
            ct.ThrowIfCancellationRequested();
            string cur = stack.Pop();
            try
            {
                var entries = new FileSystemEnumerable<(string Path, long Len, bool IsDir, FileAttributes Attrs)>(
                    QuarantineStore.ToExtendedPath(cur),
                    (ref FileSystemEntry e) =>
                    {
                        string p = e.ToFullPath();
                        return (p.StartsWith(@"\\?\", StringComparison.Ordinal) ? p[4..] : p, e.IsDirectory ? 0L : e.Length, e.IsDirectory, e.Attributes);
                    },
                    enumOptions);

                foreach (var entry in entries)
                {
                    if ((entry.Attrs & FileAttributes.ReparsePoint) != 0) continue;
                    if (entry.IsDir)
                    {
                        stack.Push(entry.Path);
                    }
                    else
                    {
                        if (++count > maxFiles) break;
                        total += entry.Len;
                    }
                }
            }
            catch
            {
            }
        }
        return total;
    }

    internal static string[] SplitLines(string text, int maxLines) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(maxLines)
            .ToArray();
}
