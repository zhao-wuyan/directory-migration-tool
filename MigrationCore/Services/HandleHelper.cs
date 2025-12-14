using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace MigrationCore.Services;

/// <summary>
/// Handle.exe 工具辅助类，用于检测文件占用
/// </summary>
public static class HandleHelper
{
    /// <summary>
    /// 占用进程信息
    /// </summary>
    public class HandleInfo
    {
        /// <summary>进程名</summary>
        public string ProcessName { get; set; } = string.Empty;

        /// <summary>进程ID</summary>
        public int ProcessId { get; set; }

        /// <summary>占用的文件路径</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>句柄类型</summary>
        public string HandleType { get; set; } = string.Empty;

        public override string ToString()
        {
            return $"{ProcessName} (PID: {ProcessId})";
        }
    }

    /// <summary>
    /// 获取占用指定路径的进程列表
    /// </summary>
    /// <param name="path">要检查的文件或目录路径</param>
    /// <returns>占用该路径的进程列表，如果 handle.exe 不可用则返回 null</returns>
    public static List<HandleInfo>? GetProcessesLockingPath(string path)
    {
        try
        {
            string? handleExePath = FindHandleExecutable();
            if (handleExePath == null)
            {
                return null;
            }

            // 执行 handle.exe
            var output = ExecuteHandle(handleExePath, path);
            if (string.IsNullOrEmpty(output))
            {
                return new List<HandleInfo>();
            }

            // 解析输出
            return ParseHandleOutput(output, path);
        }
        catch
        {
            // 发生任何错误都返回 null，表示无法获取信息
            return null;
        }
    }

    /// <summary>
    /// 查找 handle.exe 可执行文件
    /// </summary>
    /// <returns>handle.exe 的完整路径，未找到则返回 null</returns>
    private static string? FindHandleExecutable()
    {
        // 确定当前程序的基础路径
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string handleDir = Path.Combine(baseDir, "Resources", "bin", "Handle");

        // 根据系统架构选择合适的 handle.exe
        string handleExe;
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            handleExe = "handle64.exe";
        }
        else if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            handleExe = "handle64a.exe";
        }
        else
        {
            handleExe = "handle.exe";
        }

        string handlePath = Path.Combine(handleDir, handleExe);

        // 检查文件是否存在
        if (File.Exists(handlePath))
        {
            return handlePath;
        }

        // 如果找不到，尝试在系统 PATH 中查找
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (var dir in pathEnv.Split(';'))
            {
                var testPath = Path.Combine(dir.Trim(), "handle.exe");
                if (File.Exists(testPath))
                {
                    return testPath;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 执行 handle.exe 并获取输出
    /// </summary>
    private static string ExecuteHandle(string handleExePath, string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = handleExePath,
            Arguments = $"\"{path}\" -accepteula -nobanner",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            return string.Empty;
        }

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000); // 最多等待 5 秒

        return output;
    }

    /// <summary>
    /// 解析 handle.exe 的输出
    /// </summary>
    private static List<HandleInfo> ParseHandleOutput(string output, string targetPath)
    {
        var result = new List<HandleInfo>();

        // handle.exe 的输出格式示例：
        // explorer.exe       pid: 1234   type: File          C:\testMove\01
        // chrome.exe         pid: 5678   type: File          C:\testMove\01\test.txt

#if DEBUG
        Console.WriteLine($"[HandleHelper] 开始解析 handle.exe 输出，总行数: {output.Split('\n').Length}");
#endif

        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            // 跳过空行和标题行
            if (string.IsNullOrWhiteSpace(line) ||
                line.Contains("Handle v") ||
                line.Contains("Copyright") ||
                line.Contains("Sysinternals") ||
                line.Contains("No matching handles found"))
            {
                continue;
            }

            try
            {
                // 使用正则表达式解析输出
                // 格式: 进程名.exe       pid: 数字   type: 类型          路径
                var match = Regex.Match(line, @"^(\S+)\s+pid:\s*(\d+)\s+type:\s*(\w+)\s+(.+)$", RegexOptions.IgnoreCase);

                if (match.Success)
                {
                    var info = new HandleInfo
                    {
                        ProcessName = match.Groups[1].Value,
                        ProcessId = int.Parse(match.Groups[2].Value),
                        HandleType = match.Groups[3].Value,
                        FilePath = match.Groups[4].Value.Trim()
                    };

                    result.Add(info);
#if DEBUG
                    Console.WriteLine($"[HandleHelper] 解析成功: {info.ProcessName} (PID: {info.ProcessId})");
#endif
                }
                else
                {
#if DEBUG
                    Console.WriteLine($"[HandleHelper] 无法解析行: {line}");
#endif
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                Console.WriteLine($"[HandleHelper] 解析异常: {ex.Message}, 行内容: {line}");
#endif
                // 解析失败则跳过该行
                continue;
            }
        }

#if DEBUG
        Console.WriteLine($"[HandleHelper] 解析完成，共找到 {result.Count} 个占用进程");
#endif

        return result;
    }

    /// <summary>
    /// 格式化占用进程列表为用户友好的字符串
    /// </summary>
    /// <param name="handles">占用信息列表</param>
    /// <returns>格式化后的字符串</returns>
    public static string FormatHandleInfo(List<HandleInfo> handles)
    {
        if (handles == null || handles.Count == 0)
        {
            return "未检测到占用进程";
        }

#if DEBUG
        Console.WriteLine($"[HandleHelper] FormatHandleInfo: 输入 {handles.Count} 个句柄");
#endif

        var sb = new StringBuilder();
        sb.AppendLine("检测到以下进程正在占用文件：");
        sb.AppendLine();

        // 按进程名分组
        var grouped = handles.GroupBy(h => h.ProcessName).ToList();

#if DEBUG
        Console.WriteLine($"[HandleHelper] FormatHandleInfo: 分组后有 {grouped.Count} 个不同进程");
#endif

        foreach (var group in grouped)
        {
            var first = group.First();
            sb.AppendLine($"  - {first.ProcessName} (PID: {first.ProcessId})");

#if DEBUG
            Console.WriteLine($"[HandleHelper] FormatHandleInfo: 添加进程 {first.ProcessName} (PID: {first.ProcessId}), 占用文件数: {group.Count()}");
#endif

            // 如果同一进程占用多个文件，显示文件数量
            if (group.Count() > 1)
            {
                sb.AppendLine($"    占用了 {group.Count()} 个文件");
            }
        }

        sb.AppendLine();
        sb.AppendLine("💡 建议操作：");
        sb.AppendLine("  1. 关闭上述程序后重试");
        sb.AppendLine("  2. 如果无法关闭，可以尝试在任务管理器中结束进程");

        var result = sb.ToString();

#if DEBUG
        Console.WriteLine($"[HandleHelper] FormatHandleInfo: 格式化完成，输出长度: {result.Length}");
        Console.WriteLine($"[HandleHelper] FormatHandleInfo: 输出内容:\n{result}");
#endif

        return result;
    }

    /// <summary>
    /// 获取占用信息的简短描述（用于日志）
    /// </summary>
    public static string GetShortDescription(List<HandleInfo> handles)
    {
        if (handles == null || handles.Count == 0)
        {
            return string.Empty;
        }

        var processNames = handles
            .Select(h => h.ProcessName)
            .Distinct()
            .Take(3)
            .ToArray();

        if (processNames.Length == 1)
        {
            return $"被 {processNames[0]} 占用";
        }
        else if (processNames.Length <= 3)
        {
            return $"被 {string.Join(", ", processNames)} 占用";
        }
        else
        {
            return $"被 {processNames[0]}, {processNames[1]} 等 {handles.Count} 个进程占用";
        }
    }
}
