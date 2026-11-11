using System.Windows;

namespace AnomalyDonationBridge.App.Services;

internal static class StartupPresentation
{
    // ProcessWindowStyle.Hidden cannot suppress a later WPF Show(). Do not
    // show the recovered window at all; hotkeys can still create its HWND.
    internal static bool Prepare(Window window, bool crashRecovery)
    {
        window.ShowActivated = !crashRecovery;
        window.ShowInTaskbar = !crashRecovery;
        return !crashRecovery;
    }

    internal static void PrepareUserOpen(Window window)
    {
        window.ShowActivated = true;
        window.ShowInTaskbar = true;
    }
}
