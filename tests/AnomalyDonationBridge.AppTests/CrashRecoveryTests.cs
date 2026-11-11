using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.AppTests;

internal static class CrashRecoveryTests
{
    private static void Check(bool value, string reason)
    { if (!value) throw new InvalidOperationException(reason); }

    // This process harness never constructs WPF/App/BridgeRuntime, opens user
    // settings, connects to providers/game, or acquires the user's app mutex.
    internal static int RunProbe(string[] args)
    {
        var paths = new AppPaths(args[1]);
        var observer = new CrashRecovery(paths, CrashRestartPolicy.ReadAttempt(args), [args[0], paths.RootDirectory]);
        observer.SetEnabled(CrashRecovery.ReadEnabled(paths));
        if (!observer.IsMonitoring && CrashRecovery.ReadEnabled(paths)) return 99;
        File.AppendAllText(Path.Combine(paths.RootDirectory, "observers.txt"), (observer.ObserverPath ?? "disabled") + "\n");
        File.AppendAllText(Path.Combine(paths.RootDirectory, "launches.txt"), Environment.ProcessId + "\n");
        if (args[0] == "--crash-loop-probe") return 17; // Deliberately no intentional-stop signal.
        var commandFile = Path.Combine(paths.RootDirectory, "command.txt");
        for (var tick = 0; tick < 1000; tick++)
        {
            if (File.Exists(commandFile))
            {
                var command = File.ReadAllText(commandFile).Trim();
                File.Delete(commandFile);
                if (command == "close") { observer.Finish(intentional: true); return 17; }
                if (command == "normal") { observer.Finish(intentional: true); return 0; }
                if (command == "disable") { observer.SetEnabled(false); File.WriteAllText(Path.Combine(paths.RootDirectory, "disabled"), "ok"); }
                if (command == "enable") { observer.SetEnabled(true); File.WriteAllText(Path.Combine(paths.RootDirectory, "enabled"), "ok"); }
            }
            Thread.Sleep(20);
        }
        observer.Finish(intentional: true);
        return 0;
    }

    internal static async Task RunAsync()
    {
        Check(new AppSettings().RestartAfterCrash, "Restart is not enabled by default.");
        var old = JsonSerializer.Deserialize<AppSettings>("{}", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Check(old.RestartAfterCrash, "Old settings do not default to enabled.");
        Check(CrashRestartPolicy.NextAttempt(0, TimeSpan.Zero, 17, false) == 1 &&
            CrashRestartPolicy.NextAttempt(3, TimeSpan.FromMinutes(1), 17, false) is null &&
            CrashRestartPolicy.NextAttempt(3, TimeSpan.FromMinutes(6), 17, false) == 1 &&
            CrashRestartPolicy.NextAttempt(0, TimeSpan.Zero, 0, false) is null &&
            CrashRestartPolicy.NextAttempt(0, TimeSpan.Zero, 17, true) is null, "Unsafe restart/loop policy.");

        var parentRoot = Path.Combine(Environment.GetEnvironmentVariable("ADB_TEST_ROOT") ?? Path.GetTempPath(), "watchdog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parentRoot);
        try
        {
            var paths = new AppPaths(Path.Combine(parentRoot, "settings"));
            paths.EnsureExists();
            var store = new JsonSettingsStore(paths);
            await store.SaveAsync(new AppSettings { RestartAfterCrash = false });
            Check(!CrashRecovery.ReadEnabled(paths) && !(await store.LoadAsync()).RestartAfterCrash, "Disabled preference was lost on disk.");
            var runtime = new BridgeRuntime(paths);
            await runtime.InitializeAsync();
            try
            {
                var vm = new SettingsViewModel(runtime);
                Check(!vm.RestartAfterCrash, "Settings checkbox ignores saved preference.");
                var game = Path.Combine(parentRoot, "mock-game");
                Directory.CreateDirectory(Path.Combine(game, "bin"));
                await File.WriteAllTextAsync(Path.Combine(game, "fsgame.ltx"), "$game_data$=true|true|$fs_root$|gamedata\\");
                await File.WriteAllTextAsync(Path.Combine(game, "bin", "AnomalyDX11.exe"), "test marker; never executed");
                vm.GamePath = game;
                vm.RestartAfterCrash = true;
                await vm.SaveAsync();
                Check(CrashRecovery.ReadEnabled(paths), "Settings dialog does not save restart preference.");
            }
            finally { await runtime.DisposeAsync(); }

            var xaml = XDocument.Load(Path.Combine(FindRoot(), "src/AnomalyDonationBridge.App/SettingsWindow.xaml"));
            var box = xaml.Descendants().Single(n => (string?)n.Attribute("Content") == "Принудительный перезапуск при вылете");
            Check(box.Attribute("IsChecked")!.Value.Contains("RestartAfterCrash"), "General settings checkbox is not wired.");

            await ProbeCaseAsync(parentRoot, "crash", crash: true);
            await ProbeCaseAsync(parentRoot, "intentional", crash: false);
            await ProbeCaseAsync(parentRoot, "disabled", crash: true, disable: true);
            await ProbeCaseAsync(parentRoot, "enabled-live", crash: true, initiallyDisabled: true);
            await CrashLoopAsync(parentRoot);
        }
        finally { Directory.Delete(parentRoot, recursive: true); }
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AnomalyDonationBridge.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new IOException("Project root not found.");
    }

    private static string[] Launches(string root) => File.Exists(Path.Combine(root, "launches.txt"))
        ? File.ReadAllLines(Path.Combine(root, "launches.txt")).Where(s => s.Length > 0).ToArray() : [];

    private static async Task WaitUntilAsync(Func<bool> ready)
    {
        var timeout = Stopwatch.StartNew();
        while (true)
        {
            try { if (ready()) return; }
            catch (IOException) { /* A child may still be appending its ready/log file. */ }
            if (timeout.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Watchdog child did not report expected state.");
            await Task.Delay(50);
        }
    }

    private static async Task ProbeCaseAsync(string parentRoot, string name, bool crash, bool disable = false, bool initiallyDisabled = false, SelfLaunch? launch = null)
    {
        var root = Path.Combine(parentRoot, name);Directory.CreateDirectory(root);
        var paths = new AppPaths(root);
        await File.WriteAllTextAsync(paths.SettingsFile, initiallyDisabled ? "{\"restartAfterCrash\":false}" : "{}");
        using var child = (launch ?? SelfLaunch.Current()).Start(["--crash-recovery-probe", root]);
        Process? replacement = null;
        try
        {
            await WaitUntilAsync(() => Launches(root).Length == 1);
            if (disable || initiallyDisabled)
            {
                await File.WriteAllTextAsync(paths.SettingsFile, disable ? "{\"restartAfterCrash\":false}" : "{}");
                await File.WriteAllTextAsync(Path.Combine(root, "command.txt"), disable ? "disable" : "enable");
                await WaitUntilAsync(() => File.Exists(Path.Combine(root, disable ? "disabled" : "enabled")));
            }
            if (crash) child.Kill();
            else await File.WriteAllTextAsync(Path.Combine(root, "command.txt"), "close");
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (crash && !disable)
            {
                await WaitUntilAsync(() => Launches(root).Length == 2);
                replacement = Process.GetProcessById(int.Parse(Launches(root)[1]));
                await File.WriteAllTextAsync(Path.Combine(root, "command.txt"), "normal");
                await replacement.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Check(File.ReadAllText(paths.LogFile).Contains("Автоматический перезапуск"), "Restart was not logged.");
            }
            await Task.Delay(3500);
            Check(Launches(root).Length == (crash && !disable ? 2 : 1), "Unexpected restart after manual close/disable.");
        }
        finally
        {
            // Suppress any observer still finishing a failed test.
            await File.WriteAllTextAsync(paths.SettingsFile, "{\"restartAfterCrash\":false}");
            if (!child.HasExited) child.Kill();
            if (replacement is not null) { if (!replacement.HasExited) replacement.Kill(); replacement.Dispose(); }
            await Task.Delay(100);
        }
    }

    private static async Task CrashLoopAsync(string parentRoot, SelfLaunch? launch = null)
    {
        var root = Path.Combine(parentRoot, "loop");Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), "{}");
        using var child = (launch ?? SelfLaunch.Current()).Start(["--crash-loop-probe", root]);
        try
        {
            await WaitUntilAsync(() => File.Exists(Path.Combine(root, "bridge.log")) && File.ReadAllText(Path.Combine(root, "bridge.log")).Contains("Перезапуск остановлен"));
            await Task.Delay(500);
            Check(Launches(root).Length == 4, "Crash loop did not stop after three restarts.");
        }
        catch
        {
            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), "{\"restartAfterCrash\":false}");
            if (!child.HasExited) child.Kill();
            await Task.Delay(3500);
            throw;
        }
    }

    // Run against the actual trimmed portable helper, not a mock or the test
    // host's fallback. The renamed harness has the same trusted sibling layout
    // as the real app, but never constructs its GUI/runtime or reads user data.
    internal static async Task<int> RunNamedObserverAsync(string helper)
    {
        helper = Path.GetFullPath(helper);
        var info = FileVersionInfo.GetVersionInfo(helper);
        Check(info.FileDescription == SelfLaunch.ObserverTitle, "Task Manager description is not distinct.");
        Check(Path.GetFileName(helper) == SelfLaunch.ObserverFileName, "Observer filename is not distinct.");
        var root = Path.Combine(Environment.GetEnvironmentVariable("ADB_TEST_ROOT") ?? Path.GetTempPath(), "named-watchdog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var host = Path.Combine(root, "host"); Directory.CreateDirectory(host);
            var target = new SelfLaunch(Path.Combine(host, SelfLaunch.AppFileName), []);
            try { target.Observer(); throw new InvalidOperationException("Missing helper silently fell back to an identically named process."); }
            catch (FileNotFoundException) { }
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
            {
                // Development output can contain the helper's unbundled apphost
                // from project references; it is not the portable layout.
                if (Path.GetFileName(file).Equals(SelfLaunch.ObserverFileName, StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(file, Path.Combine(host, Path.GetFileName(file)));
            }
            File.Copy(SelfLaunch.Current().FileName, target.FileName, overwrite: true);
            var copiedHelper = Path.Combine(host, SelfLaunch.ObserverRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(copiedHelper)!);
            File.Copy(helper, copiedHelper, overwrite: true);
            Check(target.Observer().FileName == copiedHelper, "App does not select named helper.");
            Check(!File.Exists(Path.Combine(host, SelfLaunch.ObserverFileName)) &&
                File.GetAttributes(Path.GetDirectoryName(copiedHelper)!).HasFlag(FileAttributes.Hidden),
                "Observer leaked into the top-level app folder or its service directory is not hidden.");
            await ProbeCaseAsync(root, "crash", crash: true, launch: target);
            var observers = File.ReadAllLines(Path.Combine(root, "crash", "observers.txt"));
            Check(observers.Length == 2 && observers.All(p => p.Equals(copiedHelper, StringComparison.OrdinalIgnoreCase)),
                "Initial/restarted app is not monitored by the distinct EXE.");
            await ProbeCaseAsync(root, "intentional", crash: false, launch: target);
            await ProbeCaseAsync(root, "disabled", crash: true, disable: true, launch: target);
            await ProbeCaseAsync(root, "enabled-live", crash: true, initiallyDisabled: true, launch: target);
            await CrashLoopAsync(root, target);
            Console.WriteLine("PASS hidden portable observer: nested/hidden directory, file description, real helper process, crash restart, manual close, preference and loop limit");
            return 0;
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
