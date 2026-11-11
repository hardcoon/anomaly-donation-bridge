using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AnomalyDonationBridge.App;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.AppTests;

internal static class HistoryUiChecks
{
    internal static int Run(string output)
    {
        try
        {
            Directory.CreateDirectory(output);
            var app = new AnomalyDonationBridge.App.App(startRuntime: false); app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var runtime = new BridgeRuntime(new AppPaths(Path.Combine(output, "isolated-data-" + Guid.NewGuid().ToString("N"))));
            var game = Path.Combine(output, "isolated-game"); Directory.CreateDirectory(game);
            runtime.Settings.GamePath = game;
            runtime.InstallEffectsAsync(game, "").GetAwaiter().GetResult();
            var model = new MainViewModel(runtime) { SelectedSectionIndex = 3 };
            var action = runtime.Presets.Single(p => p.HandlerId == "change_player_name").Clone();
            action.IsActive = true; action.Triggers = [new() { Amount = 100, Currency = "RUB" }];
            for (var index = 0; index < 4; index++)
                runtime.RunAsDonorAsync(action, "Тест " + index, "Тестовая очередь").GetAwaiter().GetResult();
            Pump();
            var queued = model.History.ToArray();
            Check(queued.Length == 4, "Isolated queue did not populate history.");
            model.History.Add(new() { State = DispatchState.Executed, ActionName = "Выдать патроны к снаряжению",
                CommandId = "done", DonationId = "done", ActionSnapshot = action, Amount = 80, Currency = "RUB", Donor = "Другой донатер",
                Detail = "Выполнено", DonationCreatedAt = DateTimeOffset.Now.AddMinutes(-5) });
            var window = new MainWindow(model, runtime);
            var compact = (CompactMainView)window.FindName("CompactShell");
            var wide = (DataGrid)window.FindName("HistoryGrid");
            var small = (ListBox)compact.FindName("SmallHistoryList");
            Check(wide.SelectionMode == DataGridSelectionMode.Extended && wide.SelectionUnit == DataGridSelectionUnit.FullRow &&
                small.SelectionMode == SelectionMode.Extended, "Shift/Ctrl row selection is not enabled in both layouts.");
            foreach (var light in new[] { false, true })
            foreach (var size in new[] { new Size(640, 520), new Size(1080, 680), new Size(1440, 860) })
            {
                ThemeManager.Apply(light, "yellow"); Layout(window, size);
                var root = (FrameworkElement)window.Content;
                model.SetHistorySelection(queued.Take(3)); Pump();
                Check(small.SelectedItems.Count == 3 && wide.SelectedItems.Count == 3, "Selected range was lost on responsive switch.");
                var selector = size.Width < 1080 ? (Selector)small : wide;
                CheckRightClick(selector, queued[1], model, 3);
                CheckRightClick(selector, queued[3], model, 1);
                model.SetHistorySelection(queued.Take(3));
                var replacement = new EventHistoryEntry { CommandId = queued[1].CommandId, DonationId = queued[1].DonationId,
                    ActionName = queued[1].ActionName, Amount = 100, Currency = "RUB", State = DispatchState.Queued,
                    Donor = "Статус обновился", Detail = "В очереди", DonationCreatedAt = queued[1].DonationCreatedAt };
                typeof(MainViewModel).GetMethod("AddHistory", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, [replacement]);
                queued[1] = replacement; Pump();
                Check(model.SelectedHistoryEntries.Count == 3 && small.SelectedItems.Count == 3 && wide.SelectedItems.Count == 3,
                    "A live status update collapsed the selected range.");
                var menu = HistoryListBehavior.BuildMenu(model);
                string[] expected = ["Повторить", "Применить мгновенно", "В исключения", "Обновить",
                    "Удалить выбранное из очереди", "Очистить очередь"];
                var items = menu.Items.OfType<MenuItem>().ToArray();
                Check(items.Select(item => item.Header?.ToString()).SequenceEqual(expected), "History menu order differs from toolbar.");
                menu.PlacementTarget = selector; menu.ApplyTemplate();
                Check(!items[0].Command.CanExecute(null) && items[1].Command.CanExecute(null) && items[4].Command.CanExecute(null),
                    "Waiting selection has incorrect menu enablement.");
                foreach (var item in items.TakeLast(2)) Check(ReferenceEquals(item.Style, app.FindResource("DangerMenuItemStyle")), "Queue deletion is not styled red.");
                AssertButtonBounds(root);
                Save(root, Path.Combine(output, $"history-{size.Width}-{(light ? "light" : "dark")}.png"));
                // Render the actual styled menu independently; opening a popup
                // on the desktop is unnecessary for this isolated screenshot.
                menu.Width = 340;
                menu.Measure(new Size(340, double.PositiveInfinity)); menu.Arrange(new Rect(menu.DesiredSize)); menu.UpdateLayout();
                Save(menu, Path.Combine(output, $"history-menu-{(light ? "light" : "dark")}.png"));
                model.SetHistorySelection([model.History.Last()]);
                Check(!model.ApplyQueuedImmediatelyCommand.CanExecute(null) && model.RetryCommand.CanExecute(null),
                    "Completed-only UI selection enabled instant apply.");
            }
            window.Close(); runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Console.WriteLine("PASS real history table/list: Extended selection, shared ordered menus, right-click range preservation, live status replacement, command states, two themes, 640/1080/1440 widths. No IPC/providers/game actions.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void CheckRightClick(Selector selector, EventHistoryEntry entry, MainViewModel model, int expected)
    {
        var container = selector.ItemContainerGenerator.ContainerFromItem(entry) as FrameworkElement;
        Check(container is not null, "History row was not realized.");
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
        { RoutedEvent = Mouse.PreviewMouseDownEvent };
        container!.RaiseEvent(args);
        Check(args.Handled && model.SelectedHistoryEntries.Count == expected && model.SelectedHistoryEntries.Contains(entry),
            $"Right click lost range: {selector.GetType().Name}, handled={args.Handled}, expected={expected}, count={model.SelectedHistoryEntries.Count}, contains={model.SelectedHistoryEntries.Contains(entry)}, source={args.OriginalSource?.GetType().Name}, context={container.DataContext?.GetType().Name}.");
    }
    private static void Layout(MainWindow window, Size size)
    {
        window.Width = size.Width; window.Height = size.Height;
        var root = (FrameworkElement)window.Content; root.Width = size.Width - 16; root.Height = size.Height - 40;
        root.Margin = new Thickness(0);
        for (var i = 0; i < 3; i++) { Pump(); root.Measure(new Size(root.Width, root.Height)); root.Arrange(new Rect(0, 0, root.Width, root.Height)); root.UpdateLayout(); }
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (var item in Find<T>(child)) yield return item; }
    }
    private static void AssertButtonBounds(FrameworkElement root)
    {
        foreach (var button in Find<Button>(root))
        {
            if (button.ActualWidth < 1) continue;
            var visible = true;
            for (DependencyObject? p = button; p is not null && p != root; p = VisualTreeHelper.GetParent(p))
                if (p is UIElement { Visibility: not Visibility.Visible }) { visible = false; break; }
            if (!visible) continue;
            var rect = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
            Check(rect.Left >= -1 && rect.Right <= root.ActualWidth + 1, "History toolbar button clips: " + AutomationProperties.GetName(button) + button.Content);
        }
    }
    private static void Save(FrameworkElement root, string file)
    {
        var image = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(root); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(file); png.Save(stream);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
