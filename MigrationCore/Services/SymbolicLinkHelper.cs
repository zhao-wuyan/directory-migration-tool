using System.Runtime.InteropServices;

namespace MigrationCore.Services;

/// <summary>
/// 符号链接辅助类 - 使用 P/Invoke 创建符号链接
/// </summary>
public static class SymbolicLinkHelper
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateSymbolicLink(string lpSymlinkFileName, string lpTargetFileName, int dwFlags);

    private const int SYMBOLIC_LINK_FLAG_DIRECTORY = 0x1;
    private const int SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE = 0x2;

    /// <summary>
    /// 创建目录符号链接
    /// </summary>
    /// <param name="linkPath">链接路径</param>
    /// <param name="targetPath">目标路径</param>
    /// <returns>是否成功</returns>
    public static bool CreateDirectorySymbolicLink(string linkPath, string targetPath)
    {
        int flags = SYMBOLIC_LINK_FLAG_DIRECTORY | SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE;
        return CreateSymbolicLink(linkPath, targetPath, flags);
    }

    /// <summary>
    /// 使用 cmd mklink 命令创建符号链接（备选方案）
    /// </summary>
    public static bool CreateSymbolicLinkViaCmdAsync(string linkPath, string targetPath, out string error)
    {
        error = string.Empty;
        try
        {
            var processStartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /D \"{linkPath}\" \"{targetPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(processStartInfo);
            if (process == null)
            {
                error = "无法启动 cmd 进程";
                return false;
            }

            process.WaitForExit();
            
            if (process.ExitCode != 0)
            {
                error = process.StandardError.ReadToEnd();
                return false;
            }

            return Directory.Exists(linkPath);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 检查路径是否为重解析点（符号链接）
    /// </summary>
    public static bool IsSymbolicLink(string path)
    {
        try
        {
#if DEBUG
            Console.WriteLine($"[SymbolicLinkHelper] Checking path: {path}");
#endif
            var dirInfo = new DirectoryInfo(path);
            
            if (dirInfo.Exists)
            {
                var attributes = dirInfo.Attributes;
#if DEBUG
                Console.WriteLine($"[SymbolicLinkHelper] Directory exists. Attributes: {attributes}");
                Console.WriteLine($"[SymbolicLinkHelper] Has ReparsePoint flag: {(attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint}");
#endif
                bool isReparsePoint = (attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
                
                // 如果是重解析点，尝试读取链接目标以确认是符号链接
                if (isReparsePoint)
                {
                    try
                    {
                        string? linkTarget = dirInfo.LinkTarget;
#if DEBUG
                        Console.WriteLine($"[SymbolicLinkHelper] LinkTarget: {linkTarget}");
#endif
                        // 如果能读取到 LinkTarget，说明确实是符号链接
                        return !string.IsNullOrEmpty(linkTarget);
                    }
#if DEBUG
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SymbolicLinkHelper] Failed to read LinkTarget: {ex.Message}");
#else
                    catch
                    {
#endif
                        // 即使读取失败，有 ReparsePoint 标志也认为是符号链接
                        return true;
                    }
                }
                
                return false;
            }
            else
            {
                var fileInfo = new FileInfo(path);
                if (fileInfo.Exists)
                {
                    var attributes = fileInfo.Attributes;
#if DEBUG
                    Console.WriteLine($"[SymbolicLinkHelper] File exists. Attributes: {attributes}");
                    Console.WriteLine($"[SymbolicLinkHelper] Has ReparsePoint flag: {(attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint}");
#endif
                    return (attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
                }
            }
            
#if DEBUG
            Console.WriteLine($"[SymbolicLinkHelper] Path does not exist");
#endif
            return false;
        }
#if DEBUG
        catch (Exception ex)
        {
            Console.WriteLine($"[SymbolicLinkHelper] Exception: {ex.Message}");
#else
        catch
        {
#endif
            return false;
        }
    }
}

