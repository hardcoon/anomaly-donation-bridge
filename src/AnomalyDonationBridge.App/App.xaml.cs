using System.Windows;
using System.Windows.Threading;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.App;

public partial class App : Application
{
    private readonly bool _startRuntime;

    public App() : this(startRuntime: true) { }

    // UI renderers load real application resources without starting accounts,
    // IPC, global hotkeys or the crash watchdog when pumping the dispatcher.
    internal App(bool startRuntime) => _startRuntime = startRuntime;

    private BridgeRuntime? _runtime;
    private TrayIconController? _trayIcon;
    private GlobalHotkeyManager? _globalHotkeys;
    private SingleInstanceGate? _singleInstance;
    private CrashRecovery? _crashRecovery;
    private bool _intentionalShutdown;
    private bool _fatalUiErrorReported;
    private bool _shutdownStarted;
    private bool _backgroundRecovery;

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (!_startRuntime) return;
        base.OnStartup(e);
        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            Shutdown(await SelfTestRunner.RunAsync());
            return;
        }
        if (e.Args.Contains("--ui-smoke-test", StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await SelfTestRunner.RunUiSmokeAsync());
            return;
        }

        _backgroundRecovery = CrashRestartPolicy.ReadAttempt(e.Args) > 0;
        _singleInstance = SingleInstanceGate.TryAcquire();
        if (_singleInstance is null)
        {
            if (!_backgroundRecovery) AppDialogs.Info(null, "Anomaly Donation Bridge",
                "Anomaly Donation Bridge уже запущен. Проверьте значок DB в области уведомлений рядом с часами.\n\n" +
                "Чтобы запустить новую версию, выберите у значка DB пункт «Выход».");
            Shutdown(_backgroundRecovery ? 0 : 2);
            return;
        }

        DispatcherUnhandledException += HandleDispatcherUnhandledException;
        try
        {
            var paths = AppPaths.CreateDefault();
            _crashRecovery = new CrashRecovery(paths, CrashRestartPolicy.ReadAttempt(e.Args));
            _crashRecovery.SetEnabled(CrashRecovery.ReadEnabled(paths));
        }
        catch (Exception error)
        {
            CrashRecovery.WriteLog(AppPaths.CreateDefault(), "Наблюдатель недоступен: " + error.Message);
        }
        try
        {
            _runtime = new BridgeRuntime();
            var viewModel = new MainViewModel(_runtime);
            await viewModel.InitializeAsync();
            _runtime.SettingsChanged += ConfigureCrashRecovery;
            ConfigureCrashRecovery();
            ThemeManager.Apply(viewModel.IsLightTheme, viewModel.ThemeColor);
            var window = new MainWindow(viewModel, _runtime);
            MainWindow = window;
            _trayIcon = new TrayIconController(
                window,
                viewModel,
                () => _runtime.Settings.MinimizeToTrayOnClose,
                () => ShutdownApplication(), MarkIntentionalShutdown);
            if (StartupPresentation.Prepare(window, _backgroundRecovery)) window.Show();
            _globalHotkeys = new GlobalHotkeyManager(
                window,
                _runtime,
                viewModel.ToggleDonationProcessingFromHotkeyAsync,
                viewModel.RepeatLastDonationFromHotkeyAsync,
                viewModel.ActivateActivePresetFromHotkeyAsync,
                viewModel.ReportHotkeyMessage);
            if (!_backgroundRecovery && string.IsNullOrWhiteSpace(_runtime.Settings.GamePath))
            {
                await viewModel.OpenSettingsAsync();
            }
        }
        catch (Exception exception)
        {
            TryWriteFatalLog("Startup", exception);
            if (!_backgroundRecovery && _crashRecovery?.IsMonitoring != true)
                MessageBox.Show(exception.Message, "Anomaly Donation Bridge", MessageBoxButton.OK, MessageBoxImage.Error);
            ShutdownApplication(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _crashRecovery?.Finish(_intentionalShutdown || e.ApplicationExitCode == 0);
        _crashRecovery = null;
        DispatcherUnhandledException -= HandleDispatcherUnhandledException;
        _globalHotkeys?.Dispose();
        _globalHotkeys = null;
        _trayIcon?.Dispose();
        _trayIcon = null;
        if (_runtime is not null)
        {
            var runtime = _runtime;
            _runtime = null;
            runtime.SettingsChanged -= ConfigureCrashRecovery;
            try
            {
                runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                TryWriteFatalLog("Shutdown", exception);
            }
        }
        _singleInstance?.Dispose();
        _singleInstance = null;
        base.OnExit(e);
    }

    private void HandleDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        TryWriteFatalLog("Unhandled UI exception", e.Exception);

        // The UI may be in an unknown state, so report the failure and shut down
        // deliberately instead of silently disappearing or attempting to continue.
        // UI smoke tests deliberately use the default dispatcher behavior so binding
        // regressions still produce a failing process exit code.
        e.Handled = true;
        if (!_backgroundRecovery && !_fatalUiErrorReported && _crashRecovery?.IsMonitoring != true)
        {
            _fatalUiErrorReported = true;
            var logPath = AppPaths.CreateDefault().LogFile;
            try
            {
                MessageBox.Show(
                    $"Приложение остановлено из-за внутренней ошибки. Подробности записаны в журнал:\n{logPath}",
                    "Anomaly Donation Bridge",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
                // Nothing else should obscure the original exception during shutdown.
            }
        }
        ShutdownApplication(1);
    }

    private void ShutdownApplication(int exitCode = 0)
    {
        if (exitCode == 0) MarkIntentionalShutdown();
        if (_shutdownStarted) return;
        _shutdownStarted = true;
        _trayIcon?.PrepareForShutdown();
        _ = CompleteShutdownAsync(exitCode);
    }

    private async Task CompleteShutdownAsync(int exitCode)
    {
        try
        {
            _globalHotkeys?.Dispose();
            _globalHotkeys = null;
            if (_runtime is not null)
            {
                var runtime = _runtime;
                _runtime = null;
                runtime.SettingsChanged -= ConfigureCrashRecovery;
                await runtime.DisposeAsync();
            }
        }
        catch (Exception exception)
        {
            TryWriteFatalLog("Shutdown", exception);
            exitCode = exitCode == 0 ? 1 : exitCode;
        }
        Shutdown(exitCode);
    }

    private void ConfigureCrashRecovery()
    {
        if (!_shutdownStarted && _runtime is { } runtime)
            _crashRecovery?.SetEnabled(runtime.Settings.RestartAfterCrash);
    }

    private void MarkIntentionalShutdown()
    {
        _intentionalShutdown = true;
        _crashRecovery?.Finish(intentional: true);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        MarkIntentionalShutdown();
        base.OnSessionEnding(e);
    }

    internal static bool TryWriteFatalLog(string context, Exception exception, AppPaths? paths = null)
    {
        try
        {
            var logger = new SafeFileLogger(paths ?? AppPaths.CreateDefault());
            logger.WriteAsync("FATAL", $"{context}: {exception}").GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            // Logging must never replace the original startup/UI failure.
            return false;
        }
    }
}
