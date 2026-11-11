using System.Runtime.CompilerServices;
using AnomalyDonationBridge.App.Services;

namespace AnomalyDonationBridge.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (CrashWatchdog.TryRun(args, out var result)) return result;
        return RunApplication();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApplication()
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
