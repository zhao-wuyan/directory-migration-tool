using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;

namespace MoveWithSymlinkGUI;

public static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // For single-file deployment, ensure the working directory is set correctly
        var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        if (!string.IsNullOrEmpty(exePath))
        {
            var exeDir = Path.GetDirectoryName(exePath);
            if (!string.IsNullOrEmpty(exeDir) && Directory.Exists(exeDir))
            {
                Environment.CurrentDirectory = exeDir;
            }
        }
        
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start((p) =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }
}

