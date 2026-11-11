using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace AnomalyDonationBridge.App.Services;

// Shared source, not a second implementation of the restart protocol. The
// helper needs only the base runtime: no WPF, integrations or persistence model.
internal sealed record SelfLaunch(string FileName, string[] Prefix)
{
    internal const string AppFileName = "AnomalyDonationBridge.exe";
    internal const string ObserverFileName = "AnomalyDonationBridge.CrashGuard.exe";
    internal const string ObserverDirectoryName = "_internal";
    internal static string ObserverRelativePath => Path.Combine(ObserverDirectoryName, ObserverFileName);
    internal const string ObserverTitle = "Anomaly Donation Bridge — Защита от вылетов";

    internal static SelfLaunch Current()
    {
        var exe = Environment.ProcessPath ?? throw new IOException("Не найден путь программы.");
        return new(exe, Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? [Path.Combine(AppContext.BaseDirectory, Assembly.GetEntryAssembly()!.GetName().Name + ".dll")] : []);
    }

    internal SelfLaunch Observer()
    {
        // Development/test hosts keep the existing headless entry point. The
        // actual app MUST ship its named helper: no silent identical-process fallback.
        if (Prefix.Length > 0 || !Path.GetFileName(FileName).Equals(AppFileName, StringComparison.OrdinalIgnoreCase)) return this;
        var helper = Path.Combine(Path.GetDirectoryName(FileName)!, ObserverRelativePath);
        if (!File.Exists(helper)) throw new FileNotFoundException("Не найден наблюдатель. Восстановите полную папку программы.", helper);
        // ZIP extraction/copying may discard the folder's Hidden attribute.
        // Visibility is cosmetic: a read-only directory must not disable recovery.
        try
        {
            var directory = Path.GetDirectoryName(helper)!;
            File.SetAttributes(directory, File.GetAttributes(directory) | FileAttributes.Hidden);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return new(helper, []);
    }

    internal Process Start(IEnumerable<string> arguments, bool hidden = true)
    {
        var info = new ProcessStartInfo(FileName)
        {
            UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
            WorkingDirectory = Path.GetDirectoryName(FileName)!
        };
        foreach (var argument in Prefix.Concat(arguments)) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new IOException("Не удалось запустить процесс.");
    }
}

internal static class CrashRestartPolicy
{
    internal const int MaximumRestarts = 3;
    internal static readonly TimeSpan StableUptime = TimeSpan.FromMinutes(5);
    internal static int? NextAttempt(int attempt, TimeSpan uptime, int exitCode, bool stopped)
    {
        if (stopped || exitCode == 0) return null;
        if (uptime >= StableUptime) attempt = 0;
        return attempt < MaximumRestarts ? attempt + 1 : null;
    }

    internal static int ReadAttempt(string[] args)
    {
        var index = Array.IndexOf(args, "--crash-restart");
        return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var value) &&
            value is >= 0 and <= MaximumRestarts ? value : 0;
    }
}

internal static class CrashWatchdogState
{
    internal static string OwnerFile(string root) => Path.Combine(root, "crash-recovery.owner");
    internal static string StopName(string session) => @"Local\ADB.CrashRecovery." + session + ".stop";
    internal static string ReadyName(string session) => @"Local\ADB.CrashRecovery." + session + ".ready";

    internal static bool ReadEnabled(string root)
    {
        try
        {
            var settings = Path.Combine(root, "settings.json");
            if (!File.Exists(settings)) return true;
            using var file = File.OpenRead(settings);
            using var json = JsonDocument.Parse(file);
            return !json.RootElement.TryGetProperty("restartAfterCrash", out var value) || value.ValueKind == JsonValueKind.True;
        }
        catch { return false; } // Never overwrite or guess a damaged setting.
    }

    internal static void WriteLog(string root, string message)
    {
        try
        {
            // Callers pass fixed messages and numeric process/attempt data only;
            // never command-line payloads, settings, or raw exception messages.
            var line = $"{DateTimeOffset.UtcNow:O}\tWATCHDOG\t{message}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(root, "bridge.log"), line, new UTF8Encoding(false));
        }
        catch { /* Restart must not depend on a writable log or healthy disk. */ }
    }
}

internal static class CrashWatchdog
{
    private sealed class ProcessExitWait : WaitHandle
    {
        internal ProcessExitWait(Process process) => SafeWaitHandle = new SafeWaitHandle(process.Handle, ownsHandle: false);
    }

    internal static bool TryRun(string[] args, out int result, SelfLaunch? expectedParent = null)
    {
        result = 0;
        if (args.Length == 0 || args[0] != "--crash-watchdog") return false;
        if (args.Length != 7 || !int.TryParse(args[1], out var pid) || pid <= 0 ||
            !long.TryParse(args[2], out var started) || !Guid.TryParseExact(args[3], "N", out _) ||
            !int.TryParse(args[4], out var attempt) || attempt is < 0 or > CrashRestartPolicy.MaximumRestarts ||
            args[5].Length > 32767 || args[6].Length > 16384)
        { result = 2; return true; }
        string? root = null;
        try
        {
            root = Path.GetFullPath(args[5]);
            using var payload = JsonDocument.Parse(Convert.FromBase64String(args[6]));
            if (payload.RootElement.ValueKind != JsonValueKind.Array) { result = 2; return true; }
            var restartArguments = new List<string>();
            foreach (var item in payload.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: <= 4096 } value)
                { result = 2; return true; }
                restartArguments.Add(value);
            }
            using var stop = EventWaitHandle.OpenExisting(CrashWatchdogState.StopName(args[3]));
            using var ready = EventWaitHandle.OpenExisting(CrashWatchdogState.ReadyName(args[3]));
            using var parent = Process.GetProcessById(pid);
            var launch = expectedParent ?? SelfLaunch.Current();
            if (parent.StartTime.ToUniversalTime().Ticks != started ||
                !string.Equals(parent.MainModule?.FileName, launch.FileName, StringComparison.OrdinalIgnoreCase)) return true;
            using var exited = new ProcessExitWait(parent);
            ready.Set();
            if (WaitHandle.WaitAny([stop, exited]) == 0 || stop.WaitOne(0)) return true;
            var uptime = DateTime.UtcNow - new DateTime(started, DateTimeKind.Utc);
            var code = parent.ExitCode;
            var next = CrashRestartPolicy.NextAttempt(attempt, uptime, code, stopped: false);
            bool StillOwner() => File.ReadAllText(CrashWatchdogState.OwnerFile(root)) == args[3];
            if (!StillOwner() || code == 0) return true;
            CrashWatchdogState.WriteLog(root, $"Аварийное завершение PID={pid}, код={code} (0x{unchecked((uint)code):X8}).");
            if (next is null)
            {
                CrashWatchdogState.WriteLog(root, "Перезапуск остановлен: 3 последовательных попытки. Запустите программу вручную и проверьте FATAL-записи.");
                return true;
            }
            if (stop.WaitOne(TimeSpan.FromSeconds(3)) || !StillOwner() || !CrashWatchdogState.ReadEnabled(root)) return true;
            CrashWatchdogState.WriteLog(root, $"Автоматический перезапуск, попытка {next}/{CrashRestartPolicy.MaximumRestarts}.");
            using var replacement = launch.Start(restartArguments.Concat(["--crash-restart", next.Value.ToString(CultureInfo.InvariantCulture)]), hidden: false);
        }
        catch (Exception error)
        {
            if (root is not null) CrashWatchdogState.WriteLog(root, "Наблюдатель завершён без перезапуска: " + error.GetType().Name);
            result = 1;
        }
        return true;
    }
}
