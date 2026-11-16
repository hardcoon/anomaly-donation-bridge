using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace AnomalyDonationBridge.App.Services;

internal static class InterfaceScaling
{
    public static Size GetWorkArea(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return SystemParameters.WorkArea.Size;

        var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(window);
        return new Size(area.Width / dpi.DpiScaleX, area.Height / dpi.DpiScaleY);
    }

    public static void ConfigureRendering(FrameworkElement surface, double scale)
    {
        // Display uses a realization at the monitor DPI even when the layout
        // has been zoomed. Ideal + Fixed redraws glyphs at the requested scale.
        TextOptions.SetTextFormattingMode(surface, scale == 1 ? TextFormattingMode.Display : TextFormattingMode.Ideal);
        TextOptions.SetTextHintingMode(surface, TextHintingMode.Fixed);
        surface.SetValue(VectorIcon.LayoutScaleProperty, scale);
    }

    public static void ApplyToDialog(Window dialog, MainWindow main)
    {
        var scale = main.InterfaceScale;
        if (dialog.Content is not FrameworkElement content) return;
        ConfigureRendering(content, scale);
        if (scale == 1) return;

        var area = GetWorkArea(main);
        content.LayoutTransform = new ScaleTransform(scale, scale);
        dialog.MinWidth = Math.Min(dialog.MinWidth * scale, area.Width);
        dialog.MinHeight = Math.Min(dialog.MinHeight * scale, area.Height);
        dialog.MaxWidth = Math.Min(dialog.MaxWidth * scale, area.Width);
        dialog.MaxHeight = Math.Min(dialog.MaxHeight * scale, area.Height);
        dialog.Width = Math.Min(dialog.Width * scale, dialog.MaxWidth);
        dialog.Height = Math.Min(dialog.Height * scale, dialog.MaxHeight);
    }
}
