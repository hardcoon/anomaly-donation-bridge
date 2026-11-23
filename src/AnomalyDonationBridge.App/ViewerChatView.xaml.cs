using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.ViewerChat;

namespace AnomalyDonationBridge.App;

public partial class ViewerChatView : UserControl, IDisposable
{
    private readonly BridgeRuntime _runtime;
    private readonly ObservableCollection<ChatDisplayRow> _rows = [];
    private readonly Dictionary<ChatPlatform, ConnectionRow> _connections = new();
    private readonly DispatcherTimer _timer;
    private long _revision = -1;
    private bool _followTail = true, _disposed, _light, _tailScrollPending;
    private ScrollViewer? _messageScroll;
    private bool _tailScrollInProgress;

    public ViewerChatView(BridgeRuntime runtime)
    {
        _runtime = runtime;
        InitializeComponent();
        MessageList.ItemsSource = _rows;
        AddConnection(ChatPlatform.YouTube, "YouTube", runtime.Settings.YouTubeChatVideoUrl, "Ссылка на текущую трансляцию YouTube");
        AddConnection(ChatPlatform.Twitch, "Twitch", runtime.Settings.TwitchChatChannel, "Имя канала или https://twitch.tv/канал");
        AddConnection(ChatPlatform.VkVideoLive, "VK Видео Live", runtime.Settings.VkVideoLiveChatChannel, "https://live.vkvideo.ru/канал");
        ConnectionsExpander.IsExpanded = !(runtime.Settings.YouTubeChatEnabled || runtime.Settings.TwitchChatEnabled ||
            runtime.Settings.VkVideoLiveChatEnabled);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) => { if (IsVisible && !_disposed) { Refresh(); _timer.Start(); } else _timer.Stop(); };
        Refresh();
    }

    private void AddConnection(ChatPlatform platform, string name, string value, string hint)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 7) };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(105) });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var icon = new ChatPlatformIcon { Platform = platform, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
        var input = new TextBox { Text = value, MinWidth = 90, ToolTip = hint, Tag = hint, Margin = new Thickness(0, 0, 8, 0) };
        input.Style = (Style)FindResource("ChatLinkInputStyle");
        var toggle = new Button { MinWidth = 104, Content = "Подключить" };
        var status = new TextBlock { FontSize = 11, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap };
        status.SetResourceReference(ForegroundProperty, "MutedBrush");
        Grid.SetColumn(label, 1); Grid.SetColumn(input, 2); Grid.SetColumn(toggle, 3);
        Grid.SetColumn(status, 2); Grid.SetRow(status, 1); Grid.SetColumnSpan(status, 2);
        grid.Children.Add(icon); grid.Children.Add(label); grid.Children.Add(input); grid.Children.Add(toggle); grid.Children.Add(status);
        var row = new ConnectionRow(input, toggle, status);
        _connections.Add(platform, row);
        toggle.Click += async (_, _) =>
        {
            if (row.Busy) return;
            row.Busy = true; toggle.IsEnabled = false; ErrorHint.Text = "";
            try
            {
                var state = _runtime.ViewerConnection(platform).State;
                if (state is ViewerChatState.Connected or ViewerChatState.Connecting or ViewerChatState.Retrying)
                    await _runtime.DisconnectViewerChatAsync(platform);
                else await _runtime.ConnectViewerChatAsync(platform, input.Text);
            }
            catch (Exception e) { ErrorHint.Text = e is ViewerChatException or InvalidOperationException or AnomalyDonationBridge.YouTubeChat.YouTubeChatException
                    ? e.Message : "Не удалось изменить подключение чата."; }
            finally { row.Busy = false; if (!_disposed) Refresh(); }
        };
        input.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter && toggle.IsEnabled) toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
        ConnectionRows.Children.Add(grid);
    }

    internal void Refresh()
    {
        if (_disposed) return;
        foreach (var pair in _connections)
        {
            var state = _runtime.ViewerConnection(pair.Key);
            var row = pair.Value;
            if (row.Status.Text != state.Status) row.Status.Text = state.Status;
            if (row.LastState != state.State)
            {
                row.LastState = state.State;
                row.Status.SetResourceReference(ForegroundProperty, state.State == ViewerChatState.Error ? "BadBrush" :
                    state.State == ViewerChatState.Connected ? "GoodBrush" : "MutedBrush");
            }
            var label = state.State is ViewerChatState.Connected or ViewerChatState.Connecting or ViewerChatState.Retrying ? "Отключить" : "Подключить";
            if (row.Label != label)
            {
                row.Label = label;
                row.Button.Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { new VectorIcon { Glyph = label == "Подключить" ? "\uE768" : "\uE711", Width = 12, Height = 12,
                        Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = label } }
                };
            }
            row.Button.IsEnabled = !row.Busy;
        }
        if (_light != ThemeManager.IsLightTheme)
        {
            _light = ThemeManager.IsLightTheme;
            foreach (var row in _rows) row.SetTheme(_light);
        }
        var snapshot = _runtime.ViewerChat.Snapshot;
        if (_revision == snapshot.Revision) return;
        var present = snapshot.Messages.Select(item => item.Sequence).ToHashSet();
        for (var i = _rows.Count - 1; i >= 0; i--) if (!present.Contains(_rows[i].Sequence)) _rows.RemoveAt(i);
        var existing = _rows.Select(row => row.Sequence).ToHashSet();
        var incoming = snapshot.Messages.Where(item => !existing.Contains(item.Sequence)).ToArray();
        // Bound each dispatcher turn. The rest is appended by the next tick,
        // preserving existing rows instead of resetting/repainting the entire list.
        foreach (var item in incoming.Take(250)) _rows.Add(new ChatDisplayRow(item, _light));
        if (incoming.Length <= 250) _revision = snapshot.Revision;
        EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FeedCount.Text = $"В ленте: {_rows.Count} · получено: {snapshot.ReceivedCount}";
        if (_rows.Count == 0) { _followTail = true; NewMessagesButton.Visibility = Visibility.Collapsed; }
        if (_followTail) QueueTailScroll();
        else if (incoming.Length > 0) NewMessagesButton.Visibility = Visibility.Visible;
    }

    private void Clear_Click(object sender, RoutedEventArgs e) { _runtime.ViewerChat.Clear(); Refresh(); }
    private void Messages_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0) _followTail = false;
        else if (e.Delta < 0 && _messageScroll is { } scroll && NearChatEnd(scroll))
        {
            _followTail = true; NewMessagesButton.Visibility = Visibility.Collapsed;
            QueueTailScroll();
        }
    }
    private static bool NearChatEnd(ScrollViewer scroll) =>
        scroll.ScrollableHeight - scroll.VerticalOffset <= Math.Max(1, scroll.ScrollableHeight * 0.15);
    private void Messages_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer scroll) return;
        _messageScroll = scroll;
        if (!_tailScrollInProgress && e.VerticalChange < 0 &&
            (Mouse.LeftButton == MouseButtonState.Pressed && scroll.IsMouseCaptureWithin ||
             e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0 || !NearChatEnd(scroll)))
            _followTail = false;
        else if (!_tailScrollInProgress && e.VerticalChange > 0)
        {
            // Logical scrolling may also change the viewport's item count when
            // wrapped messages have different heights. Do not discard that move.
            _followTail = NearChatEnd(scroll);
        }
        else if (e.VerticalChange == 0 && e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0) return;
        if (_followTail)
        {
            NewMessagesButton.Visibility = Visibility.Collapsed;
            QueueTailScroll();
        }
    }
    private void QueueTailScroll()
    {
        if (_tailScrollPending || _disposed || !_followTail || _rows.Count == 0) return;
        _tailScrollPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _tailScrollPending = false;
            if (_disposed || !_followTail || _rows.Count == 0) return;
            _tailScrollInProgress = true;
            if (_messageScroll is { } scroll) scroll.ScrollToEnd();
            else MessageList.ScrollIntoView(_rows[^1]);
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => _tailScrollInProgress = false));
        }));
    }
    private void NewMessages_Click(object sender, RoutedEventArgs e)
    {
        _followTail = true; NewMessagesButton.Visibility = Visibility.Collapsed;
        QueueTailScroll();
    }
    public void Dispose() { _disposed = true; _timer.Stop(); }
    private sealed class ConnectionRow(TextBox input, Button button, TextBlock status)
    {
        public TextBox Input { get; } = input;
        public Button Button { get; } = button;
        public TextBlock Status { get; } = status;
        public string Label { get; set; } = "";
        public bool Busy { get; set; }
        public ViewerChatState? LastState { get; set; }
    }
}

public sealed class ChatDisplayRow : INotifyPropertyChanged
{
    private static readonly string[] Dark = ["#8ACBFF", "#C4A2FF", "#8DD5AC", "#F4CD79", "#ED9FC8", "#FFAC8D", "#8DD5D7", "#DBBC87",
        "#A8C5FF", "#DDA5E9", "#B5D87C", "#F3B9A4", "#A5CED5", "#D0BCF2", "#E4CD9A", "#E8A9AE"];
    private static readonly string[] Light = ["#155E9B", "#7343AA", "#266E45", "#875914", "#9C366E", "#A34120", "#176B70", "#73521F",
        "#355BA2", "#8B3D96", "#4C6B18", "#964F37", "#326770", "#694E91", "#796022", "#923F48"];
    private readonly ViewerMessage _message;
    private readonly int _colorIndex;
    private Brush _nameBrush = Brushes.White;
    public ChatDisplayRow(ViewerChatEntry item, bool light)
    {
        Sequence = item.Sequence; _message = item.Message;
        var identity = _message.Platform + ":" + (_message.UserId.Length > 0 ? _message.UserId : _message.Author);
        _colorIndex = SHA256.HashData(Encoding.UTF8.GetBytes(identity))[0] % Dark.Length;
        SetTheme(light);
    }
    public long Sequence { get; }
    public ChatPlatform Platform => _message.Platform;
    public string Author => _message.Author;
    public string Text => _message.Text;
    public Brush NameBrush => _nameBrush;
    public string Hint => $"{PlatformLabel(Platform)} · {_message.ChannelId} · " +
        (_message.PublishedAt?.ToLocalTime().ToString("HH:mm:ss") ?? "Время не указано") +
        (_message.IsHistorical ? " · история чата" : "") + (_message.IsMirrored ? " · отражено из Twitch" : "");
    internal void SetTheme(bool light)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString((light ? Light : Dark)[_colorIndex]));
        brush.Freeze(); _nameBrush = brush; PropertyChanged?.Invoke(this, new(nameof(NameBrush)));
    }
    public static string PlatformLabel(ChatPlatform platform) => platform switch
    { ChatPlatform.YouTube => "YouTube", ChatPlatform.Twitch => "Twitch", _ => "VK Видео Live" };
    public event PropertyChangedEventHandler? PropertyChanged;
}
