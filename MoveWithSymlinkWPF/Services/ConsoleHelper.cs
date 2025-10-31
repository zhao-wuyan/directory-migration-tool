using System.Runtime.InteropServices;

namespace MoveWithSymlinkWPF.Services;

/// <summary>
/// 控制台辅助工具，用于在 Debug 模式下显示控制台窗口
/// </summary>
public static class ConsoleHelper
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    /// <summary>
    /// 分配控制台窗口（如果尚未存在）
    /// </summary>
    public static void AllocateConsole()
    {
        // 检查是否已经有控制台窗口
        if (GetConsoleWindow() != IntPtr.Zero)
        {
            return; // 已经有控制台了
        }

        // 尝试附加到父进程的控制台（如果从命令行启动）
        if (!AttachConsole(-1))
        {
            // 如果附加失败，创建新的控制台窗口
            AllocConsole();
        }

        Console.WriteLine("=== Debug Console Enabled ===");
        Console.WriteLine("Application started with console output");
        Console.WriteLine("=============================");
        Console.WriteLine();
    }
}

