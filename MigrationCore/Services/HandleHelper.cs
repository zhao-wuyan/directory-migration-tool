using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace MigrationCore.Services;

public enum HandleDiagnosticStatus { Found, NotFound, Unavailable, TimedOut }

public sealed record HandleDiagnosticResult(HandleDiagnosticStatus Status,
    IReadOnlyList<HandleHelper.HandleInfo> Handles, string? Error = null, bool ProcessExited = true, int? ProcessId = null);

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
    /// 输出读取和进程等待共享五秒截止；失败不是“无人占用”。
    /// </summary>
    public static async Task<HandleDiagnosticResult> GetProcessesLockingPathAsync(string path, CancellationToken ct)
    {
        Process? process = null;
        Task? lifecycle = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            path = PathIdentityService.Normalize(path);
            MigrationRuntime.AssertDataPath(path, allowLink: true);
            string? executable = FindHandleExecutable();
            if (executable is null)
                return new(HandleDiagnosticStatus.Unavailable, [], "未找到可用的 Handle.exe，不能据此认定无人占用。");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8
            };
            start.ArgumentList.Add(path);
            start.ArgumentList.Add("-nobanner");
            // 不替用户自动接受工具许可；许可提示也受同一个截止约束。
            process = Process.Start(start) ?? throw new IOException("无法启动 Handle 诊断进程。");
            Task<string> output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            Task<string> error = process.StandardError.ReadToEndAsync(deadline.Token);
            lifecycle = Task.WhenAll(output, error, process.WaitForExitAsync(deadline.Token));
            await lifecycle.WaitAsync(deadline.Token).ConfigureAwait(false);
            string text = await output.ConfigureAwait(false);
            string stderr = await error.ConfigureAwait(false);
            var handles = ParseHandleOutput(text, path);
            if (process.ExitCode == 0 && handles.Count != 0)
                return new(HandleDiagnosticStatus.Found, handles, FormatHandleInfo(handles), true, process.Id);
            bool noMatches = text.Split('\n').Any(line => line.Trim().TrimEnd('.')
                .Equals("No matching handles found", StringComparison.OrdinalIgnoreCase));
            if (process.ExitCode is 0 or 1 && noMatches && string.IsNullOrWhiteSpace(stderr))
                return new(HandleDiagnosticStatus.NotFound, [], "Handle 未发现匹配句柄，重命名失败仍须处理。", true, process.Id);
            return new(HandleDiagnosticStatus.Unavailable, [],
                $"Handle 退出码 {process.ExitCode}，输出不可确认为成功诊断：{stderr} {text}", true, process.Id);
        }
        catch (Exception ex)
        {
            string error = ex.Message;
            bool exited = process is null || process.HasExited;
            if (process is not null && !exited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    exited = process.HasExited;
                }
                catch (Exception stopError) { error += $"；无法确认诊断进程退出：{stopError.Message}"; }
            }
            if (lifecycle is not null && exited)
            {
                try { await lifecycle.ConfigureAwait(false); }
                catch (Exception readError) { error += $"；输出读取结束：{readError.Message}"; }
            }
            HandleDiagnosticStatus status = deadline.IsCancellationRequested && !ct.IsCancellationRequested
                ? HandleDiagnosticStatus.TimedOut : HandleDiagnosticStatus.Unavailable;
            return new(status, [], error, exited, process?.Id);
        }
        finally { process?.Dispose(); }
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

        if (MigrationRuntime.IsTestLab) return null;
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
    /// 只接受可解析且落在请求路径分量边界内的句柄；未知输出由调用方报告不可用。
    /// </summary>
    private static List<HandleInfo> ParseHandleOutput(string output, string targetPath)
    {
        var result = new List<HandleInfo>();
        foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Regex.Match(line, @"^(\S+)\s+pid:\s*(\d+)\s+type:\s*(\w+)\s+(.+)$", RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            string path = match.Groups[4].Value.Trim();
            int drive = path.IndexOf(":\\", StringComparison.Ordinal);
            if (drive > 0) path = path[(drive - 1)..];
            if (!PathIdentityService.IsWithin(path, targetPath)) continue;
            result.Add(new HandleInfo
            {
                ProcessName = match.Groups[1].Value,
                ProcessId = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                HandleType = match.Groups[3].Value,
                FilePath = path
            });
        }
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
