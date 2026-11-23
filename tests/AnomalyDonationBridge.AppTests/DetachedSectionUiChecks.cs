using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AnomalyDonationBridge.App;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.Persistence;
using AnomalyDonationBridge.ViewerChat;

namespace AnomalyDonationBridge.AppTests;

internal static class DetachedSectionUiChecks
{
    internal static int Run(string output)
    {
        try
        {
            Directory.CreateDirectory(output);
            var app = new AnomalyDonationBridge.App.App(startRuntime: false); app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
            var task = CheckAsync(output); var frame = new DispatcherFrame();
            _ = task.ContinueWith(_ => app.Dispatcher.BeginInvoke((Action)(() => frame.Continue = false)));
            Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
            app.Shutdown();
            Console.WriteLine("PASS detached sections: native ownerless windows, minimize/hide independence, single live views, history actions, return/cleanup, shared queue, 100000-message bounded feed and themes.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); Application.Current?.Shutdown(); return 1; }
    }

    private static async Task CheckAsync(string output)
    {
        using var temporary = new Program.TemporaryDirectory();
        await using var runtime = new BridgeRuntime(new AppPaths(Path.Combine(temporary.Path, "data")));
        var action = new ActionDefinition { DisplayName = "Локальный шаблон", HandlerId = "add_radiation", IsActive = true };
        runtime.Settings.Actions = [action];
        await runtime.InitializeVotingModuleAsync();
        var draft = runtime.Voting.Snapshot;
        draft.MoneyGoal = false; draft.TimeGoal = true; draft.DurationSeconds = 600;
        draft.Options = [new() { Command = "!ДА", Preset = action.Clone() }, new() { Command = "!НЕТ", Preset = action.Clone() }];
        await runtime.Voting.SaveSettingsAsync(draft);
        var model = new MainViewModel(runtime);
        for (var i = 0; i < 80; i++) model.History.Add(new()
        {
            DonationId = "history-" + i, CommandId = "command-" + i, ActionSnapshot = action.Clone(),
            ActionName = "Локальный шаблон " + i, Donor = "Зритель " + i, Amount = i, Currency = "RUB",
            State = i % 3 == 0 ? DispatchState.Queued : DispatchState.Executed, Detail = "Локальная проверка", DonationCreatedAt = DateTimeOffset.Now
        });
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.QueueStatusText))!.SetValue(model,
            "Ждём готовности игры · в очереди: 1 · следующий эффект «Локальная проверка»");
        var trace = new StringWriter(); var listener = new TextWriterTraceListener(trace);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        var main = new MainWindow(model, runtime);
        try
        {
            PrepareOffscreen(main); main.Show(); await Settle();
            model.SelectedSectionIndex = MainViewModel.ViewerChatSectionIndex;
            var chat = Field<ViewerChatView>(main, "_viewerChatView");
            model.SelectedSectionIndex = MainViewModel.VotingSectionIndex;
            var voting = Field<VotingView>(main, "_votingView");
            var tabs = (TabControl)main.FindName("MainSections");
            foreach (var (wide, small, section) in new[]
            {
                ((FrameworkElement)tabs.Items[3], (FrameworkElement)main.CompactShell.HistoryTab, 3),
                (main.VotingNavigationTab, (FrameworkElement)main.CompactShell.VotingNavigationTab, MainViewModel.VotingSectionIndex),
                (main.ViewerChatNavigationTab, (FrameworkElement)main.CompactShell.ViewerChatNavigationTab, MainViewModel.ViewerChatSectionIndex)
            })
            {
                foreach (var navigation in new[] { wide, small })
                {
                    Check(navigation.ContextMenu?.Items.OfType<MenuItem>().Single().Header?.ToString() == "Открыть в новом окне", "Missing navigation pop-out menu");
                    var content = navigation is TabItem tab ? (DependencyObject)tab.Header : (DependencyObject)((ContentControl)navigation).Content;
                    Check(!Descendants<Button>(content).Any(button => Equals(button.Tag, "OpenDetachedSection")),
                        "Navigation must use context-only opening without pop-out icons");
                }
            }
            var chatWindow = main.GetOrCreateDetachedSection(MainViewModel.ViewerChatSectionIndex);
            var votingWindow = main.GetOrCreateDetachedSection(MainViewModel.VotingSectionIndex);
            var historyWindow = main.GetOrCreateDetachedSection(3);
            var windows = new[] { chatWindow, votingWindow, historyWindow };
            foreach (var window in windows)
            {
                Check(window.Owner is null && window.ShowInTaskbar, "Detached section has a main-window owner or no taskbar entry");
                PrepareOffscreen(window); window.Show();
                Check(GetWindow(new WindowInteropHelper(window).Handle, 4) == IntPtr.Zero, "Native detached window has an owner");
            }
            await Settle();
            Check(ReferenceEquals(Window.GetWindow(chat), chatWindow) && ReferenceEquals(Window.GetWindow(voting), votingWindow) &&
                ReferenceEquals(Window.GetWindow(voting.Player), votingWindow), "Opening a window cloned or lost a live section/player");
            Check(ReferenceEquals(main.GetOrCreateDetachedSection(3), historyWindow), "Repeated opening created a duplicate window");
            foreach (var light in new[] { false, true })
            {
                ThemeManager.Apply(light); await Settle();
                foreach (var window in windows) Save((FrameworkElement)window.Content, Path.Combine(output, window.SectionIndex + "-" + (light ? "light" : "dark") + ".png"));
            }
            main.WindowState = WindowState.Minimized; await Settle();
            foreach (var window in windows) CheckVisible(window, "Minimizing the main app hid an independent section");
            main.Hide(); await Settle();
            foreach (var window in windows) CheckVisible(window, "Hiding the main app hid an independent section");
            model.SelectedSectionIndex = 0;
            runtime.ViewerChat.Publish(new(ChatPlatform.Twitch, "offline-qa", "while-main-hidden", "viewer", "Зритель", "Сообщение при скрытом основном окне", DateTimeOffset.UtcNow));
            chat.Refresh(); await Settle();
            Check(((ListBox)chat.FindName("MessageList")).Items.Count == 1, "Detached chat did not refresh while the main app was hidden");
            foreach (var window in windows)
                Check(Descendants<ContentPresenter>((FrameworkElement)window.Content).Any(item => item.ContentTemplate == Application.Current.FindResource("QueueStatusTemplate") && item.ActualHeight > 0),
                    "Detached section lost the common queue status");
            main.WindowState = WindowState.Normal; main.Show(); main.Width = 900; main.Height = 650; await Settle();
            LayoutSurface(main, 900, 650);
            // Hidden/offscreen HWNDs can defer render-size notifications after restore.
            typeof(MainWindow).GetMethod("UpdateResponsiveLayout", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(main, [new Size(900, 650)]);
            Check(main.IsCompactLayout && ReferenceEquals(Window.GetWindow(chat), chatWindow) && ReferenceEquals(Window.GetWindow(voting), votingWindow),
                $"Responsive switch failed: compact={main.IsCompactLayout}, main={main.ActualWidth}x{main.ActualHeight}, state={main.WindowState}, chat={Window.GetWindow(chat)?.Title}, voting={Window.GetWindow(voting)?.Title}");
            historyWindow.Width = 780; LayoutSurface(historyWindow, 780, 650); await Settle();
            Check(ReferenceEquals(Window.GetWindow(main.CompactShell.SmallHistoryList), historyWindow), "Detached history did not adopt its compact view");
            model.SetHistorySelection(model.History.Take(2));
            Check(main.CompactShell.SmallHistoryList.SelectedItems.Count == 2, "Detached compact history lost selection/commands");
            historyWindow.Width = 1280; LayoutSurface(historyWindow, 1280, 720); await Settle();
            var historyGrid = (DataGrid)main.FindName("HistoryGrid");
            Check(ReferenceEquals(Window.GetWindow(historyGrid), historyWindow) && historyGrid.SelectedItems.Count == 2,
                "Wide history did not retain the selected range");
            Check(Descendants<Button>((FrameworkElement)historyWindow.Content).Any(button => ReferenceEquals(button.Command, model.RetryCommand)) &&
                  Descendants<Button>((FrameworkElement)historyWindow.Content).Any(button => ReferenceEquals(button.Command, model.ClearQueueCommand)), "Detached history lost its real command bindings");
            Field<TextBox>(voting, "_minutes").Text = "2";
            votingWindow.Close(); await main.FlushEditorWritesAsync(); await Settle();
            Check(runtime.Voting.Snapshot.DurationSeconds == 120 && !Field<bool>(voting, "_closed") &&
                ReferenceEquals(Window.GetWindow(voting), main), $"Closing vote failed: duration={runtime.Voting.Snapshot.DurationSeconds}, disposed={Field<bool>(voting, "_closed")}, parent={Window.GetWindow(voting)?.Title}");
            chatWindow.Close(); historyWindow.Close(); await Settle();
            Check(!Field<bool>(chat, "_disposed") && ReferenceEquals(Window.GetWindow(chat), main) && ReferenceEquals(Window.GetWindow(historyGrid), main),
                "Closing a detached section did not return its live contents");
            Check(main.VotingNavigationTab.ContextMenu?.Items.Count == 1 && main.ViewerChatNavigationTab.ContextMenu?.Items.Count == 1, "Pop-out menus were duplicated after returning views");

            var feedWatch = Stopwatch.StartNew();
            for (var i = 0; i < 100000; i++) runtime.ViewerChat.Publish(new(ChatPlatform.Twitch, "burst", "id-" + i, "viewer", "Зритель", "Сообщение " + i, DateTimeOffset.UtcNow));
            feedWatch.Stop();
            Check(runtime.ViewerChat.Snapshot.Messages.Count == 1500 && runtime.ViewerChat.Snapshot.ReceivedCount == 100001,
                "100000 received messages grew the retained chat history beyond 1500");
            var reopened = main.GetOrCreateDetachedSection(MainViewModel.ViewerChatSectionIndex);
            PrepareOffscreen(reopened); reopened.Show();
            for (var i = 0; i < 6; i++) { chat.Refresh(); await Settle(); }
            var messageList = (ListBox)chat.FindName("MessageList");
            Check(messageList.Items.Count == 1500 && Descendants<ListBoxItem>(messageList).Count() < 120,
                "Detached chat realized the entire message history instead of a bounded viewport");
            File.WriteAllText(Path.Combine(output, "feed-result.txt"), $"Published=100000; retained=1500; realized={Descendants<ListBoxItem>(messageList).Count()}; feedMs={feedWatch.ElapsedMilliseconds}");
            Save((FrameworkElement)reopened.Content, Path.Combine(output, "chat-bounded-feed.png"));
            main.Close(); await Settle();
            Check(!reopened.IsVisible, "Independent section was left alive after the application window actually closed");
            Check(trace.ToString().Length == 0, "WPF binding errors: " + trace);
        }
        finally
        {
            if (Application.Current.Windows.Cast<Window>().Contains(main)) main.Close();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); listener.Dispose();
        }
    }

    private static void PrepareOffscreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual; window.ShowActivated = false;
        window.Left = SystemParameters.VirtualScreenLeft - window.Width - 256;
        window.Top = SystemParameters.VirtualScreenTop - window.Height - 256;
        var handle = new WindowInteropHelper(window).EnsureHandle();
        // Keep QA windows out of the taskbar without assigning an HWND owner.
        var style = GetWindowLongPtr(handle, -20).ToInt64();
        SetWindowLongPtr(handle, -20, new IntPtr((style & ~0x40000L) | 0x80L));
    }
    private static void CheckVisible(Window window, string message)
    { var handle = new WindowInteropHelper(window).Handle; Check(window.IsVisible && IsWindowVisible(handle) && !IsIconic(handle), message); }
    private static void LayoutSurface(Window window, double width, double height)
    {
        var root = (FrameworkElement)window.Content;
        root.Width = width - 16; root.Height = height - 40;
        root.Measure(new Size(root.Width, root.Height)); root.Arrange(new Rect(0, 0, root.Width, root.Height)); root.UpdateLayout();
    }
    private static async Task Settle() { await Dispatcher.Yield(DispatcherPriority.ContextIdle); await Task.Delay(25); }
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T value) yield return value; foreach (var nested in Descendants<T>(child)) yield return nested; } }
    private static void Save(FrameworkElement root, string file)
    {
        root.UpdateLayout(); var bitmap = new RenderTargetBitmap(Math.Max(1,(int)root.ActualWidth),Math.Max(1,(int)root.ActualHeight),96,96,PixelFormats.Pbgra32);
        bitmap.Render(root); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(file); encoder.Save(stream);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window,int index,IntPtr value);
}
