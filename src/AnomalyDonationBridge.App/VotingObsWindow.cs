using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.App;

public sealed class VotingObsWindow : Window
{
    internal const string CaptureTitle = "ADB — Голосования OBS";
    private readonly TextBlock _title = Label(26);
    private readonly TextBlock _total = Label(17);
    private readonly TextBlock _time = Label(15);
    private readonly TextBlock _result = Label(20);
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Height = 11,
        Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(214, 178, 95)),
        Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(120, 20, 25, 20)), BorderThickness = new Thickness(0) };
    private readonly StackPanel _options = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _root = new(), _body = new(), _circle = new();
    private readonly DockPanel _summary = new();
    private readonly ScrollViewer _list = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    internal VotingWheelControl Wheel { get; } = new();
    internal bool IsCompact { get; private set; }
    private VotingRound? _round;
    private string _roundId = string.Empty;
    private VotingAutomation _display = new();
    private string _optionsSignature = string.Empty;

    public VotingObsWindow()
    {
        Title = CaptureTitle; Width = 840; Height = 540; MinWidth = 240; MinHeight = 100;
        // Alpha zero makes native layered windows click-through. A 1/255 input
        // surface keeps the background visually transparent but draggable.
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        ResizeMode = ResizeMode.CanResizeWithGrip; ShowInTaskbar = true;
        FontFamily = new FontFamily("Segoe UI"); Foreground = Brushes.White;
        var menu = new ContextMenu(); var close = new MenuItem { Header = "Закрыть окно OBS" };
        close.Click += (_, _) => Close(); menu.Items.Add(close); ContextMenu = menu;
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed || IsInteractiveSource(e.OriginalSource as DependencyObject)) return;
            e.Handled = true; DragMove();
        };
        var grid = _root; grid.Background = Brushes.Transparent; grid.Margin = new Thickness(20, 16, 20, 16); grid.Visibility = Visibility.Hidden;
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            grid.RowDefinitions.Add(new RowDefinition { Height = height });
        _title.HorizontalAlignment = HorizontalAlignment.Center; grid.Children.Add(ShadowHost(_title));
        var summary = _summary; summary.Margin = new Thickness(0, 15, 0, 8); Grid.SetRow(summary, 1);
        var timeHost = ShadowHost(_time); DockPanel.SetDock(timeHost, Dock.Right); summary.Children.Add(timeHost); summary.Children.Add(ShadowHost(_total)); grid.Children.Add(summary);
        Grid.SetRow(_progress, 2); grid.Children.Add(_progress);
        var body = _body; body.Margin = new Thickness(0, 18, 0, 8); Grid.SetRow(body, 3);
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var circle = new Grid { Width = 320, Height = 320 }; circle.Children.Add(Wheel);
        _circle.Children.Add(new Viewbox { Child = circle, Stretch = Stretch.Uniform, MaxWidth = 250, MaxHeight = 250,
            Margin = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }); body.Children.Add(_circle);
        _list.Content = _options; Grid.SetColumn(_list, 1); body.Children.Add(_list); grid.Children.Add(body);
        _result.TextAlignment = TextAlignment.Center; var resultHost = ShadowHost(_result); Grid.SetRow(resultHost, 4); grid.Children.Add(resultHost);
        Content = grid;
        grid.SizeChanged += (_, _) => UpdateLayoutMode();
    }

    public void Update(VotingRound round, bool spinning, VotingAutomation? display = null)
    {
        _round = round;
        _root.Visibility = Visibility.Visible;
        _display = display?.Clone() ?? new VotingAutomation();
        var now = DateTimeOffset.UtcNow;
        _title.Text = round.Title;
        _total.Text = VotingProgressDisplay.Value(round, _display.GameHudFormat, now);
        _time.Text = _display.GameHudFormat == VotingHudFormat.Timer ? "" : VotingProgressDisplay.Remaining(round, now);
        _progress.Value = (double)VotingProgressDisplay.Percent(round, now);
        _result.Text = spinning ? "Выбираем победителя…" : round.Winner is { } selected ? selected.Command + " → " + selected.Preset?.DisplayName : string.Empty;
        Wheel.Round = round;
        if (_roundId != round.Id)
        {
            _roundId = round.Id; Wheel.BeginAnimation(VotingWheelControl.AngleProperty, null); Wheel.Angle = 0;
        }
        if (round.Phase == VotingPhase.Drawn && !spinning)
        { Wheel.BeginAnimation(VotingWheelControl.AngleProperty, null); Wheel.Angle = -(double)round.WinnerFraction * 360; }
        UpdateLayoutMode(); BuildOptions();
    }

    public void Clear()
    {
        if (_round is null && _root.Visibility == Visibility.Hidden) return;
        _round = null; _roundId = _optionsSignature = string.Empty;
        Wheel.BeginAnimation(VotingWheelControl.AngleProperty, null); Wheel.Angle = 0; Wheel.Round = null;
        foreach (var label in new[] { _title, _total, _time, _result }) label.Text = string.Empty;
        _progress.Value = 0; _options.Children.Clear(); _root.Visibility = Visibility.Hidden;
    }

    private void UpdateLayoutMode()
    {
        // At small sizes switch only the graphics off. Keep the title, timer
        // and complete command descriptions readable at their own font sizes.
        var compact = _root.ActualWidth < 430 || _root.ActualHeight < 285 ||
            Math.Min(_root.ActualWidth * .48, _root.ActualHeight - 135) < 145;
        var changed = IsCompact != compact; IsCompact = compact;
        _circle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        _progress.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        _body.ColumnDefinitions[0].Width = compact ? new GridLength(0) : new GridLength(1.1, GridUnitType.Star);
        _body.ColumnDefinitions[1].Width = _display.ShowGameHudCommands ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        _list.Visibility = _display.ShowGameHudCommands ? Visibility.Visible : Visibility.Collapsed;
        _title.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        _title.FontSize = compact ? 24 : 26;
        _result.Visibility = string.IsNullOrEmpty(_result.Text) ? Visibility.Collapsed : Visibility.Visible;
        _result.FontSize = compact ? 16 : 20;
        _total.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        if (_round is { } round)
            _time.Text = compact || _display.GameHudFormat != VotingHudFormat.Timer
                ? VotingProgressDisplay.Remaining(round, DateTimeOffset.UtcNow) : string.Empty;
        _summary.Visibility = compact && string.IsNullOrEmpty(_time.Text) ? Visibility.Collapsed : Visibility.Visible;
        _summary.Margin = compact ? new Thickness(0, 12, 0, 6) : new Thickness(0, 15, 0, 8);
        _options.Margin = compact ? new Thickness(0) : new Thickness(16, 0, 0, 0);
        _body.Margin = compact ? new Thickness(0, 8, 0, 0) : new Thickness(0, 12, 0, 8);
        if (changed) BuildOptions();
    }

    private void BuildOptions()
    {
        if (_round is not { } round) return;
        var signature = $"{IsCompact}|{_display.GameHudFormat}|{_display.ShowGameHudCommands}|{round.Currency}|{round.Total}|" +
            string.Join(";", round.Options.Select(option => $"{option.Id}:{option.Enabled}:{option.Command}:{option.Preset?.DisplayName}:{option.Amount}"));
        if (signature == _optionsSignature) return;
        _optionsSignature = signature;
        _options.Children.Clear(); var index = 0;
        foreach (var option in round.Options.Where(option => option.Enabled && option.Preset is not null))
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, IsCompact ? 16 : 18) };
            var command = Label(IsCompact ? 18 : 20); command.Text = "● " + option.Command;
            // The neutral label remains readable; the swatch matches the wheel.
            command.Inlines.Clear(); command.Inlines.Add(new System.Windows.Documents.Run("● ") { Foreground = VotingWheelControl.Color(index++) });
            command.Inlines.Add(new System.Windows.Documents.Run(option.Command)); row.Children.Add(ShadowHost(command));
            var name = Label(13); name.Text = option.Preset!.DisplayName;
            row.Children.Add(ShadowHost(name));
            var amount = Label(IsCompact ? 14 : 16); amount.Text = VotingProgressDisplay.OptionValue(round, option, _display.GameHudFormat);
            row.Children.Add(ShadowHost(amount)); _options.Children.Add(row);
        }
    }

    public void Animate(VotingRound round)
    {
        var current = Wheel.Angle; var target = current + 1800 + ((360 - (double)round.WinnerFraction * 360 - current % 360 + 360) % 360);
        Wheel.BeginAnimation(VotingWheelControl.AngleProperty, new DoubleAnimation(current, target, TimeSpan.FromSeconds(4.2))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.HoldEnd });
    }

    internal static bool IsInteractiveSource(DependencyObject? source)
    {
        for (var current = source; current is not null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is ScrollBar or Thumb or ResizeGrip or MenuItem) return true;
        return false;
    }

    private static TextBlock Label(double size) => new() { FontSize = size, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap, Effect = new DropShadowEffect { Color = Colors.Black,
            BlurRadius = 2, Opacity = 1, ShadowDepth = 2, Direction = 270 } };

    private static Border ShadowHost(TextBlock label) => new() { Child = label,
        Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 1, Opacity = 1, ShadowDepth = 0 } };
}
