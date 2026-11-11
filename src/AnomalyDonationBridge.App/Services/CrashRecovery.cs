using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.App.Services;

internal sealed class CrashRecovery : IDisposable
{
    private readonly object _gate = new();
    private readonly AppPaths _paths;
    private readonly SelfLaunch _launch;
    private readonly int _attempt;
    private readonly string[] _restartArguments;
    private EventWaitHandle? _stop;
    private Process? _watcher;
    private bool _finished;

    internal CrashRecovery(AppPaths paths, int attempt = 0, string[]? restartArguments = null)
    {
        _paths = paths; _attempt = attempt; _restartArguments = restartArguments ?? [];
        _launch = SelfLaunch.Current();
        // Invalidate any older observer, including one waiting to restart an
        // earlier crash while the user has already opened a different build.
        ClaimOwnership(Guid.NewGuid().ToString("N"));
    }

    internal bool IsMonitoring { get { lock (_gate) return _stop is not null && _watcher is { HasExited: false }; } }
    internal string? ObserverPath { get { lock (_gate) return _watcher is { HasExited: false } ? _watcher.MainModule?.FileName : null; } }
    internal static string OwnerFile(AppPaths paths) => CrashWatchdogState.OwnerFile(paths.RootDirectory);
    internal static string StopName(string session) => CrashWatchdogState.StopName(session);
    internal static string ReadyName(string session) => CrashWatchdogState.ReadyName(session);
    internal static bool ReadEnabled(AppPaths paths) => CrashWatchdogState.ReadEnabled(paths.RootDirectory);

    private void ClaimOwnership(string session)
    {
        _paths.EnsureExists();
        var temporary = OwnerFile(_paths) + "." + session + ".tmp";
        try
        {
            File.WriteAllText(temporary, session);
            File.Move(temporary, OwnerFile(_paths), true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_finished) return;
            if (!enabled) { StopCore(); return; }
            if (_watcher is { HasExited: false }) return;
            StopCore();
            try
            {
                var session = Guid.NewGuid().ToString("N");
                ClaimOwnership(session);
                _stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopName(session));
                using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyName(session));
                using var parent = Process.GetCurrentProcess();
                var restart = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_restartArguments)));
                _watcher = _launch.Observer().Start(["--crash-watchdog", parent.Id.ToString(CultureInfo.InvariantCulture),
                    parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture), session,
                    _attempt.ToString(CultureInfo.InvariantCulture), _paths.RootDirectory, restart]);
                if (!ready.WaitOne(TimeSpan.FromSeconds(5)))
                    throw new IOException("Наблюдатель не подтвердил запуск за 5 секунд.");
                WriteLog(_paths, "Защита от вылетов запущена; автоматический перезапуск включён.");
            }
            catch (Exception error)
            {
                StopCore();
                WriteLog(_paths, "Не удалось включить наблюдателя: " + error.Message);
            }
        }
    }

    private void StopCore()
    {
        _stop?.Set();
        _stop?.Dispose(); _stop = null;
        _watcher?.Dispose(); _watcher = null;
    }

    internal void Finish(bool intentional)
    {
        lock (_gate)
        {
            if (_finished) return;
            _finished = true;
            if (intentional) StopCore();
            else
            {
                // A nonzero application shutdown is still a crash. Closing OUR
                // handles must not signal the observer's intentional-stop event.
                _stop?.Dispose(); _stop = null;
                _watcher?.Dispose(); _watcher = null;
            }
        }
    }

    public void Dispose() => Finish(intentional: true);

    internal static void WriteLog(AppPaths paths, string message)
    {
        try { new SafeFileLogger(paths).WriteAsync("WATCHDOG", message).GetAwaiter().GetResult(); }
        catch { /* Restart must not depend on a writable log or healthy disk. */ }
    }
}
