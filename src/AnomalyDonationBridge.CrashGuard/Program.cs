using AnomalyDonationBridge.App.Services;

namespace AnomalyDonationBridge.CrashGuard;

internal static class Program
{
    private static int Main(string[] args)
    {
        // Only the known app one directory above _internal can be restarted.
        // Never trust an executable
        // path from settings/command-line, and never restart this helper itself.
        var directory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        if (!Path.GetFileName(directory).Equals(SelfLaunch.ObserverDirectoryName, StringComparison.OrdinalIgnoreCase) ||
            Directory.GetParent(directory) is not { } root) return 2;
        var app = new SelfLaunch(Path.Combine(root.FullName, SelfLaunch.AppFileName), []);
        return CrashWatchdog.TryRun(args, out var result, app) ? result : 2;
    }
}
