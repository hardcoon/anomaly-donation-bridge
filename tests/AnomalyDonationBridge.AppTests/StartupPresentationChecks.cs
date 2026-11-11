using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using AnomalyDonationBridge.App.Services;

namespace AnomalyDonationBridge.AppTests;

internal static class StartupPresentationChecks
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    internal static Task RunAsync()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new Window();
                var activated = 0;
                window.Activated += (_, _) => activated++;
                if (StartupPresentation.Prepare(window, crashRecovery: true))
                    throw new Exception("Recovery requested a visible startup window.");
                // GlobalHotkeyManager creates the same hidden HWND. Doing so
                // must neither reveal the bridge nor activate it over a game.
                var handle = new WindowInteropHelper(window).EnsureHandle();
                if (handle == IntPtr.Zero || window.IsVisible || IsWindowVisible(handle) ||
                    GetForegroundWindow() == handle || window.ShowActivated || window.ShowInTaskbar || activated != 0)
                    throw new Exception("Recovered bridge became visible or activated while creating its hotkey window.");
                StartupPresentation.PrepareUserOpen(window);
                if (!window.ShowActivated || !window.ShowInTaskbar || window.IsVisible)
                    throw new Exception("Explicit tray opening did not restore normal window properties.");
                if (!StartupPresentation.Prepare(window, crashRecovery: false))
                    throw new Exception("Manual startup no longer requests the normal window.");
                window.Close();
            }
            catch (Exception error) { failure = error; }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10))) throw new Exception("Hidden window startup test timed out.");
        if (failure is not null) throw failure;
        if (CrashRestartPolicy.ReadAttempt(["--crash-restart", "1"]) != 1 ||
            CrashRestartPolicy.ReadAttempt([]) != 0 ||
            CrashRestartPolicy.ReadAttempt(["--crash-restart", "bad"]) != 0 ||
            CrashRestartPolicy.ReadAttempt(["--crash-restart", "99"]) != 0)
            throw new Exception("Recovery startup no longer distinguishes watchdog arguments from ordinary launches.");
        return Task.CompletedTask;
    }
}
