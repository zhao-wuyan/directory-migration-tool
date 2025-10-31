using System.Configuration;
using System.Data;
using System.Windows;
using MoveWithSymlinkWPF.Services;

namespace MoveWithSymlinkWPF;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

#if DEBUG
        // Debug 模式下分配控制台窗口，便于查看实时日志
        ConsoleHelper.AllocateConsole();
        Console.WriteLine($"Application Version: {MoveWithSymlinkWPF.Services.VersionService.GetVersion()}");
        Console.WriteLine($"Working Directory: {Environment.CurrentDirectory}");
        Console.WriteLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine();

        // 注册全局异常处理器
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        
        Console.WriteLine("Global exception handlers registered");
        Console.WriteLine();
#endif
    }

#if DEBUG
    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception;
        Console.WriteLine("=== UNHANDLED EXCEPTION ===");
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {exception?.Message}");
        Console.WriteLine($"Stack Trace:\n{exception?.StackTrace}");
        Console.WriteLine("===========================");
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        Console.WriteLine("=== DISPATCHER EXCEPTION ===");
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {e.Exception.Message}");
        Console.WriteLine($"Stack Trace:\n{e.Exception.StackTrace}");
        Console.WriteLine("============================");
        
        // 不标记为已处理，让应用继续运行
        // e.Handled = true;
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Console.WriteLine("=== UNOBSERVED TASK EXCEPTION ===");
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {e.Exception.Message}");
        foreach (var inner in e.Exception.InnerExceptions)
        {
            Console.WriteLine($"Inner: {inner.Message}");
            Console.WriteLine($"Stack Trace:\n{inner.StackTrace}");
        }
        Console.WriteLine("=================================");
        
        e.SetObserved();
    }
#endif
}

