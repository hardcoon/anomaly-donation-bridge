using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AnomalyDonationBridge.App;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Persistence;
using AnomalyDonationBridge.ViewerChat;

namespace AnomalyDonationBridge.AppTests;

internal static class ViewerChatUiChecks
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        var app = new AnomalyDonationBridge.App.App(startRuntime: false); app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        using var temporary = new Program.TemporaryDirectory();
        var runtime = new BridgeRuntime(new AppPaths(Path.Combine(temporary.Path, "data")));
        var trace = new StringWriter();
        var listener = new TextWriterTraceListener(trace);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            foreach (var light in new[] { false, true })
            {
                runtime.UpdateUiPreferencesAsync(preferences => preferences with { ViewerChatShowTimestamps = false }).GetAwaiter().GetResult();
                ThemeManager.Apply(light);
                runtime.Settings.UseLightTheme = light;
                using var view = new ViewerChatView(runtime);
                Layout(view, 1000, 680);
                var sourceRows = (StackPanel)view.FindName("ConnectionRows");
                Check(sourceRows.Children.OfType<Grid>().Count() == 3, "Sources must remain ordinary connection rows");
                Check(view.FindName("YouTubeAnonymousToggle") is null && view.FindName("YouTubeKeyPanel") is null,
                    "Viewer chat still exposes API authorization controls");
                var connectionBlock = (Expander)view.FindName("ConnectionsExpander");
                connectionBlock.IsExpanded = true;
                Layout(view, 1000, 680);
                var expandedHeight = ((Border)view.FindName("ConnectionsPanel")).ActualHeight;
                foreach (var source in sourceRows.Children.OfType<Grid>())
                {
                    var input = source.Children.OfType<TextBox>().Single();
                    var placeholder = (TextBlock)input.Template.FindName("Placeholder", input);
                    var placeholderStart = placeholder.TransformToAncestor(input).Transform(new Point(0, 0)).X;
                    var caretStart = input.GetRectFromCharacterIndex(0).X;
                    Check(input.CaretIndex == 0 && Math.Abs(caretStart - placeholderStart) <= 2.01,
                        $"Link caret does not align with its hint: caret={caretStart}, hint={placeholderStart}");
                    input.Text = "a";
                    Layout(view, 1000, 680);
                    Check(placeholder.Visibility == Visibility.Collapsed && Math.Abs(input.GetRectFromCharacterIndex(0).X - caretStart) < .01,
                        "Typing moved the start of the link or left a hint over the text");
                    input.Clear();
                    Layout(view, 1000, 680);
                }
                Save(view, Path.Combine(output, $"chat-{(light ? "light" : "dark")}-settings.png"));
                connectionBlock.IsExpanded = false;
                Layout(view, 1000, 680);
                Check(((Border)view.FindName("ConnectionsPanel")).ActualHeight < expandedHeight - 100,
                    "The entire links block must collapse together");
                var settingsButton = (Button)view.FindName("ChatSettingsButton");
                Check(settingsButton.ContextMenu.Items.Count == 1 && settingsButton.TranslatePoint(new Point(), view).X > view.ActualWidth - 80,
                    "Chat settings must be a small gear at the right of Connections");
                Save(view, Path.Combine(output, $"chat-{(light ? "light" : "dark")}-empty.png"));
                for (var i = 0; i < 15; i++)
                    runtime.ViewerChat.Publish(new((ChatPlatform)(i % 3), "channel", "sample-" + light + "-" + i, "user-" + (i % 5),
                        new[] { "Сталкер", "Рыжий", "Зритель", "TheWolf", "Свобода" }[i % 5],
                        new[] { "Как попасть на кордон?", "Привет! Сообщения со всех площадок в одном чате.",
                            "Вот теперь видно, откуда пишет зритель 👍", "!аптечка — это пока обычный текст сообщения", "Очень длинное сообщение " + new string('я', 210) }[i % 5],
                        DateTimeOffset.UtcNow, IsHistorical: i < 3));
                connectionBlock.IsExpanded = false;
                view.Refresh(); Layout(view, 1000, 680);
                var list = (ListBox)view.FindName("MessageList");
                var original = list.Items[0];
                runtime.ViewerChat.Publish(new(ChatPlatform.Twitch, "channel", "another-" + light, "user-0", "Сталкер", "Новое сообщение", DateTimeOffset.UtcNow));
                view.Refresh();
                Check(ReferenceEquals(original, list.Items[0]), "Appending chat rebuilt existing rows");
                Save(view, Path.Combine(output, $"chat-{(light ? "light" : "dark")}-messages.png"));
                Layout(view, 510, 600);
                Save(view, Path.Combine(output, $"chat-{(light ? "light" : "dark")}-narrow.png"));
                Check(list.ActualWidth <= view.ActualWidth && list.ActualWidth > 0, "Chat overflows narrow pane");
                CheckTimestamps(view, runtime, new AppPaths(Path.Combine(temporary.Path, "data")), light, output);
                CheckAutoScroll(view, runtime, light);
            }
            ThemeManager.Apply(false);
            var model = new MainViewModel(runtime);
            var window = new MainWindow(model, runtime);
            typeof(MainViewModel).GetProperty(nameof(MainViewModel.QueueStatusText))!.SetValue(model,
                "Ждём готовности игры · в очереди: 1 · следующий эффект «Локальная проверка очереди»");
            model.SelectedSectionIndex = MainViewModel.ViewerChatSectionIndex;
            var root = (FrameworkElement)window.Content;
            Layout(root, 1440, 820);
            var tabs = (TabControl)window.FindName("MainSections");
            var chatTab = (TabItem)window.FindName("ViewerChatNavigationTab");
            Check(tabs.SelectedItem == chatTab && tabs.Items.IndexOf(chatTab) == MainViewModel.ViewerChatSectionIndex,
                "Wide chat navigation is not wired to its own tab");
            Check(window.FindName("WideViewerChatHost") is ContentControl { Content: ViewerChatView }, "Chat was not embedded");
            var votingTab = (TabItem)window.FindName("VotingNavigationTab");
            var settingsTab = (TabItem)window.FindName("SettingsNavigationTab");
            var historyTab = (TabItem)tabs.Items[3];
            Check(votingTab.TranslatePoint(new Point(), root).Y >= historyTab.TranslatePoint(new Point(0, historyTab.ActualHeight), root).Y &&
                  chatTab.TranslatePoint(new Point(), root).Y >= votingTab.TranslatePoint(new Point(0, votingTab.ActualHeight), root).Y &&
                  settingsTab.TranslatePoint(new Point(), root).Y > chatTab.TranslatePoint(new Point(0, chatTab.ActualHeight), root).Y,
                "Wide viewer sections are not grouped below History and above bottom Settings");
            Check(votingTab.Template.FindName("NavigationGroupDivider", votingTab) is Border { Visibility: Visibility.Visible },
                "Wide navigation lacks its section divider");
            Check(Descendants<ContentPresenter>(root).Any(item => item.Name == "QueueStatusSurface" &&
                  item.Visibility == Visibility.Visible && item.ActualHeight > 0), "Queue status is hidden in wide viewer chat");
            Save(root, Path.Combine(output, "chat-main-window.png"));
            window.Width = 900; window.Height = 700;
            Layout(root, 900, 700);
            var compact = (CompactMainView)window.FindName("CompactShell");
            Check(window.IsCompactLayout && compact.FindName("ViewerChatHost") is ContentControl { Content: ViewerChatView },
                "Chat was not moved into the compact tab");
            var compactVoting = (FrameworkElement)compact.FindName("VotingNavigationTab");
            var compactChat = (FrameworkElement)compact.FindName("ViewerChatNavigationTab");
            Check(compactVoting.TranslatePoint(new Point(), root).Y < compactChat.TranslatePoint(new Point(), root).Y &&
                  compact.FindName("ViewerSectionsDivider") is Border { ActualHeight: > 0 },
                "Compact viewer navigation lacks its ordered top group or divider");
            Check(compact.FindName("QueueStatusSurface") is ContentPresenter { Visibility: Visibility.Visible, ActualHeight: > 0 },
                "Queue status is hidden in compact viewer chat");
            Save(root, Path.Combine(output, "chat-main-compact.png"));
            window.Close();
            Check(trace.ToString().Length == 0, "WPF chat binding errors: " + trace);
            Console.WriteLine("PASS viewer chat rendering, 150-message paging, anchored history, timestamps, no message tooltip, auto-scroll and embedded navigation.");
            return 0;
        }
        finally { runtime.DisposeAsync().AsTask().GetAwaiter().GetResult(); app.Shutdown(); }
    }
    private static void CheckTimestamps(ViewerChatView view, BridgeRuntime runtime, AppPaths paths, bool light, string output)
    {
        var list = (ListBox)view.FindName("MessageList");
        var first = (ChatDisplayRow)list.Items[0];
        Check(first.TimestampText.Length == 0, "Timestamps should be hidden by default");
        var rowGrid = Descendants<Grid>(list).First(item => item.DataContext is ChatDisplayRow);
        Check(rowGrid.ToolTip is null, "Message metadata still appears as a hover tooltip");
        var setting = (MenuItem)view.FindName("ShowTimestampsMenuItem");
        setting.IsChecked = true; setting.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var wait = Stopwatch.StartNew();
        while (!setting.IsEnabled && wait.Elapsed < TimeSpan.FromSeconds(5))
        { Settle(view, 510, 600); Thread.Sleep(5); }
        view.Refresh(); Settle(view, 510, 600);
        Check(setting.IsEnabled && first.TimestampText.StartsWith('[') && ReferenceEquals(first, list.Items[0]),
            "Toggling timestamps rebuilt rows or failed to update existing messages");
        Check(new JsonUiPreferencesStore(paths).LoadAsync().GetAwaiter().GetResult().ViewerChatShowTimestamps,
            "Timestamp choice was not saved");
        using var independent = new ViewerChatView(runtime);
        Layout(independent, 1000, 680);
        var other = (ListBox)independent.FindName("MessageList");
        Check(other.Items.OfType<ChatDisplayRow>().All(row => row.TimestampText.Length > 0),
            "New chat windows do not share the saved timestamp setting");
        Save(view, Path.Combine(output, $"chat-{(light ? "light" : "dark")}-timestamps.png"));
        setting.IsChecked = false; setting.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        wait.Restart();
        while (!setting.IsEnabled && wait.Elapsed < TimeSpan.FromSeconds(5))
        { Settle(view, 510, 600); Thread.Sleep(5); }
        independent.Refresh();
        Check(first.TimestampText.Length == 0 && other.Items.OfType<ChatDisplayRow>().All(row => row.TimestampText.Length == 0),
            "Timestamp setting did not synchronize between independent windows");
    }
    private static void CheckAutoScroll(ViewerChatView view, BridgeRuntime runtime, bool light)
    {
        var list = (ListBox)view.FindName("MessageList");
        var newMessages = (Button)view.FindName("NewMessagesButton");
        runtime.ViewerChat.Clear(); view.Refresh();
        void Publish(int i) => runtime.ViewerChat.Publish(new(ChatPlatform.Twitch, "scroll-check", light + "-" + i,
            "viewer", "Зритель", i % 4 == 0 ? new string('я', 210) : "Сообщение " + i, DateTimeOffset.UtcNow));
        for (var i = 0; i < 400; i++) Publish(i);
        view.Refresh(); view.Refresh(); Settle(view, 510, 600);
        var scroll = Descendants<ScrollViewer>(list).First();
        Check(list.Items.Count == 150 && ((ChatDisplayRow)list.Items[0]).Text == "Сообщение 250",
            "Chat initially loaded more than its latest 150 messages");
        Check(scroll.ScrollableHeight > 30 && scroll.ScrollableHeight - scroll.VerticalOffset <= 1,
            "Chat did not initially follow the latest message");
        var anchor = ((ChatDisplayRow)list.Items[0]).Sequence;
        scroll.ScrollToTop(); Settle(view, 510, 600);
        Check(list.Items.Count == 300 && ((ChatDisplayRow)list.Items[(int)scroll.VerticalOffset]).Sequence == anchor,
            $"Prepending history did not add 150 messages and retain the visible row: rows={list.Items.Count}, offset={scroll.VerticalOffset}");
        var oldestLoaded = ((ChatDisplayRow)list.Items[0]).Sequence;
        scroll.ScrollToTop(); Settle(view, 510, 600);
        Check(list.Items.Count == 400 && ((ChatDisplayRow)list.Items[(int)scroll.VerticalOffset]).Sequence == oldestLoaded,
            "Final partial history page changed the reading position");
        newMessages.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Settle(view, 510, 600);
        Check(list.Items.Count == 150 && scroll.ScrollableHeight - scroll.VerticalOffset <= 1,
            $"Returning to live did not restore a single 150-message page: rows={list.Items.Count}, offset={scroll.VerticalOffset}, range={scroll.ScrollableHeight}, follow={typeof(ViewerChatView).GetField("_followTail", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(view)}");
        var bottom = scroll.VerticalOffset;
        list.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
        scroll.LineUp(); Settle(view, 510, 600);
        var reading = scroll.VerticalOffset;
        Check(reading < bottom, "Upward mouse wheel snapped the chat back to the bottom");
        Publish(450); view.Refresh(); Settle(view, 510, 600);
        Check(Math.Abs(scroll.VerticalOffset - reading) <= 1.01, "Incoming messages interrupt reading near the bottom after wheel-up");
        list.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
        scroll.LineDown(); Settle(view, 510, 600);
        Check(scroll.ScrollableHeight - scroll.VerticalOffset <= 1, "Downward mouse wheel near the end did not resume auto-scroll");
        scroll.ScrollToVerticalOffset(scroll.ScrollableHeight * .5); Settle(view, 510, 600);
        var position = scroll.VerticalOffset;
        Publish(400); view.Refresh(); Settle(view, 510, 600);
        Check(Math.Abs(scroll.VerticalOffset - position) <= 1.01 && newMessages.Visibility == Visibility.Visible,
            $"New messages interrupted reading chat history: before={position}, after={scroll.VerticalOffset}, range={scroll.ScrollableHeight}, button={newMessages.Visibility}");
        scroll.ScrollToVerticalOffset(scroll.ScrollableHeight * .9); Settle(view, 510, 600);
        Check(scroll.ScrollableHeight - scroll.VerticalOffset <= 1 && newMessages.Visibility == Visibility.Collapsed,
            "Returning to the lower 15% did not resume chat auto-scroll");
        Publish(401); view.Refresh(); Settle(view, 510, 600);
        Check(scroll.ScrollableHeight - scroll.VerticalOffset <= 1, "Chat stopped following incoming messages after resuming");
        Settle(view, 510, 500);
        Check(scroll.ScrollableHeight - scroll.VerticalOffset <= 1, "Resizing the chat lost auto-scroll");
        scroll.ScrollToVerticalOffset(scroll.ScrollableHeight * .7); Settle(view, 510, 500);
        runtime.ViewerChat.Clear(); view.Refresh(); Settle(view, 510, 500);
        Publish(402); view.Refresh(); Settle(view, 510, 600);
        Check(scroll.ScrollableHeight - scroll.VerticalOffset <= 1 && newMessages.Visibility == Visibility.Collapsed,
            "Clearing paused history did not restore automatic scrolling");
    }
    private static void Settle(FrameworkElement root, double width, double height)
    {
        for (var i = 0; i < 5; i++)
        {
            Layout(root, width, height);
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }
    private static void Layout(FrameworkElement root, double width, double height)
    { root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); }
    private static void Save(FrameworkElement root, string path)
    {
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (var nested in Descendants<T>(child)) yield return nested; }
    }
}
