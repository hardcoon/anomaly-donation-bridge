using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.GameIpc;

namespace AnomalyDonationBridge.App;

// Desktop-only placement preview. No game window, input callback or effect is opened.
internal sealed class VotingHudPositionWindow : Window
{
    private readonly VotingAutomation _settings;
    private readonly Grid _layout = new();
    private readonly Viewbox _preview;
    private readonly Border _toolbar;
    private readonly int _contentHeight;
    internal decimal PositionX { get; private set; }
    internal decimal PositionY { get; private set; }

    internal VotingHudPositionWindow(VotingRound round, VotingAutomation settings)
    {
        _settings = settings.Clone(); PositionX = settings.GameHudX; PositionY = settings.GameHudY;
        Title = "Положение игрового HUD"; WindowStyle = WindowStyle.None; AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); Topmost = true;
        ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var lines = VotingGameHud.ContentLines(round, settings, DateTimeOffset.UtcNow); _contentHeight = lines.Sum(VotingGameHud.RowHeight);
        var text = new StackPanel { Width = 450 };
        foreach (var line in lines)
        {
            var row = new Grid { Height = VotingGameHud.RowHeight(line) };
            // Mirror the native HUD's eight black outline offsets and neutral grey.
            foreach (var offset in new[] { (-1,-1), (0,-1), (1,-1), (-1,0), (1,0), (-1,1), (0,1), (1,1), (0,0) })
                row.Children.Add(new TextBlock { Text = line, FontFamily = new FontFamily("Arial"), FontSize = 16,
                    Foreground = offset == (0,0) ? new SolidColorBrush(Color.FromRgb(190,190,190)) : Brushes.Black,
                    TextAlignment = TextAlignment.Left, TextTrimming = TextTrimming.CharacterEllipsis,
                    RenderTransform = new TranslateTransform(offset.Item1, offset.Item2), IsHitTestVisible = false });
            text.Children.Add(row);
        }
        _preview = new Viewbox { Child = text, Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Left };
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _layout.Children.Add(_preview);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        tools.Children.Add(new TextBlock { Text = "Перетащите HUD", Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0,0,12,0), FontSize = 13 });
        var apply = new Button { Content = "Применить", Height = 32, MinWidth = 100, IsDefault = true };
        var cancel = new Button { Content = "Отмена", Height = 32, MinWidth = 80, Margin = new Thickness(8,0,0,0), IsCancel = true };
        apply.Click += (_, _) => ApplyPosition(); cancel.Click += (_, _) => { DialogResult = false; };
        tools.Children.Add(apply); tools.Children.Add(cancel);
        _toolbar = new Border { Child = tools, Background = new SolidColorBrush(Color.FromRgb(30,31,30)), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10,6,10,6), Margin = new Thickness(0,6,0,0), HorizontalAlignment = HorizontalAlignment.Left,
            Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 1, BlurRadius = 3, ShadowDepth = 1 } };
        Grid.SetRow(_toolbar,1); _layout.Children.Add(_toolbar); Content = _layout;
        Loaded += (_, _) => PlaceInitially();
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed || IsButton(e.OriginalSource as DependencyObject)) return;
            e.Handled = true; DragMove(); AdjustAfterDrag();
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; } };
    }

    private static bool IsButton(DependencyObject? source)
    {
        for (var item = source; item is not null; item = item is Visual ? VisualTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item))
            if (item is Button) return true;
        return false;
    }

    private System.Drawing.Rectangle ScreenBounds() => System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).Bounds;
    private void ResizePreview(System.Drawing.Rectangle screen)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        Width = Math.Max(390, 450 * screen.Height / 768d / dpi.DpiScaleX);
        _preview.Width = 450 * screen.Height / 768d / dpi.DpiScaleX;
        _preview.Height = _contentHeight * screen.Height / 768d / dpi.DpiScaleY;
    }

    private void PlaceInitially()
    {
        var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(Owner ?? this).Handle).Bounds;
        ResizePreview(screen); UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        var y = (double)_settings.GameHudY * screen.Height;
        var above = y + ActualHeight * dpi.DpiScaleY > screen.Height;
        SetToolbarAbove(above); UpdateLayout();
        var offset = _preview.TranslatePoint(new Point(), this);
        Left = (screen.Left + (double)_settings.GameHudX * screen.Width) / dpi.DpiScaleX - offset.X;
        Top = (screen.Top + y) / dpi.DpiScaleY - offset.Y;
    }

    private void SetToolbarAbove(bool above)
    {
        Grid.SetRow(_toolbar, above ? 0 : 1); Grid.SetRow(_preview, above ? 1 : 0);
        _toolbar.Margin = above ? new Thickness(0,0,0,6) : new Thickness(0,6,0,0);
    }

    private void AdjustAfterDrag()
    {
        var screen = ScreenBounds(); var origin = _preview.PointToScreen(new Point());
        ResizePreview(screen); UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        var size = new Size(_preview.ActualWidth*dpi.DpiScaleX, _preview.ActualHeight*dpi.DpiScaleY);
        var position = Normalise(origin, new Rect(screen.Left,screen.Top,screen.Width,screen.Height),size);
        var y = (double)position.Y*screen.Height;
        SetToolbarAbove(y + ActualHeight*dpi.DpiScaleY > screen.Height); UpdateLayout();
        var current = _preview.PointToScreen(new Point());
        Left += (screen.Left+(double)position.X*screen.Width-current.X)/dpi.DpiScaleX;
        Top += (screen.Top+y-current.Y)/dpi.DpiScaleY;
    }

    private void ApplyPosition()
    {
        AdjustAfterDrag(); var screen = ScreenBounds(); var dpi = VisualTreeHelper.GetDpi(this);
        (PositionX,PositionY) = Normalise(_preview.PointToScreen(new Point()),
            new Rect(screen.Left,screen.Top,screen.Width,screen.Height),new Size(_preview.ActualWidth*dpi.DpiScaleX,_preview.ActualHeight*dpi.DpiScaleY));
        DialogResult = true;
    }

    internal static (decimal X, decimal Y) Normalise(Point origin, Rect screen, Size hud)
    {
        if (screen.Width <= 0 || screen.Height <= 0 || !double.IsFinite(origin.X) || !double.IsFinite(origin.Y))
            throw new InvalidOperationException("Не удалось определить положение экрана.");
        var width = (decimal)screen.Width; var height = (decimal)screen.Height;
        return (Math.Clamp(((decimal)origin.X-(decimal)screen.Left)/width,0,Math.Max(0,1-(decimal)hud.Width/width)),
            Math.Clamp(((decimal)origin.Y-(decimal)screen.Top)/height,0,Math.Max(0,1-(decimal)hud.Height/height)));
    }
}
