using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AnomalyDonationBridge.App.Services;

namespace AnomalyDonationBridge.App;

// A themed modal window, not a Win32 MessageBox: no system warning icon/sound.
internal class AppDialogWindow : Window
{
    internal AppDialogWindow(string title, string message, string confirmText, bool confirmation, bool destructive)
    {
        Title = title; Width = 520; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        SetResourceReference(BackgroundProperty, "BackgroundBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        DarkSystemFrame.Attach(this);
        var root = new StackPanel();
        root.SetResourceReference(Panel.BackgroundProperty, "BackgroundBrush");
        root.Children.Add(new TextBlock { Text = title, FontSize = 21, FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
        var details = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 21 };
        details.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var card = new Border { Child = details, Padding = new Thickness(16), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush"); card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        root.Children.Add(new ScrollViewer { Content = card, MaxHeight = 480, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, IsDefault = true, MinWidth = 110, Height = 36 };
        cancel.Click += (_, _) => DialogResult = false;
        var confirm = new Button { Content = confirmText, MinWidth = 118, Height = 36, Margin = new Thickness(confirmation ? 8 : 0, 0, 0, 0), IsDefault = !confirmation, IsCancel = !confirmation };
        if (destructive) confirm.SetResourceReference(StyleProperty, "DangerButtonStyle");
        confirm.Click += (_, _) => DialogResult = true;
        if (confirmation) actions.Children.Add(cancel);
        actions.Children.Add(confirm); root.Children.Add(actions);
        var surface = new Border { Child = root, Padding = new Thickness(24) }; surface.SetResourceReference(Border.BackgroundProperty, "BackgroundBrush"); Content = surface;
        Loaded += (_, _) => { if (confirmation) cancel.Focus(); else confirm.Focus(); };
    }
}

internal static class AppDialogs
{
    internal static bool Confirm(Window? owner, string title, string message, string confirmText, bool destructive = true) =>
        Show(new AppDialogWindow(title, message, confirmText, confirmation: true, destructive), owner);

    internal static void Info(Window? owner, string title, string message) =>
        Show(new AppDialogWindow(title, message, "ОК", confirmation: false, destructive: false), owner);

    private static bool Show(Window dialog, Window? owner)
    {
        owner ??= Application.Current?.Windows.OfType<Window>().LastOrDefault(window => window.IsActive && window.IsVisible)
            ?? (Application.Current?.MainWindow is { IsVisible: true } main ? main : null);
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        else { dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen; dialog.ShowInTaskbar = true; }
        return dialog.ShowDialog() == true;
    }
}

// Preserve the existing voting API; all dialogs share one themed surface.
internal sealed class VotingConfirmationWindow : AppDialogWindow
{
    internal VotingConfirmationWindow(string title, string message, string confirmText)
        : base(title, message, confirmText, confirmation: true, destructive: true) { }

    internal static bool Confirm(Window owner, string title, string message, string confirmText) =>
        AppDialogs.Confirm(owner, title, message, confirmText);
}
