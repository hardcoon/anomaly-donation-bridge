using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AnomalyDonationBridge.App;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.AppTests;

// Offline WPF rendering and routed drop events. No game, accounts or real settings.
internal static class SelectedRandomGridUiChecks
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static int Run(string output)
    {
        try
        {
            Directory.CreateDirectory(output);
            var app = new AnomalyDonationBridge.App.App(startRuntime: false); app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ThemeManager.Apply(false);
            var errors = new StringWriter();
            PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(errors));
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            var paths = new AppPaths(Path.Combine(output, "isolated-" + Guid.NewGuid().ToString("N")));
            var runtime = new BridgeRuntime(paths);
            var presets = DefaultActionCatalog.CreatePresets();
            var profile = new DonationProfile { Name = "Изолированная проверка" };
            for (var i = 0; i < 5; i++)
            {
                var action = presets.Single(p => p.HandlerId == "change_money").CloneAsNewInstance();
                action.DisplayName = new[] { "Вертолёт-напарник", "Напарник с именем донатчика", "Новая жизнь", "Азазель", "Телепортация по прицелу" }[i];
                action.HasCustomDisplayName = true; action.StrictSequence = true;
                action.Triggers = [new() { Amount = 31, Currency = "RUB" }];
                profile.Actions.Add(action);
            }
            var random = presets.Single(p => p.HandlerId == ActiveTemplateSelection.HandlerId).CloneAsNewInstance();
            random.Triggers = [new() { Amount = 100, Currency = "RUB" }]; profile.Actions.Add(random);
            runtime.Settings.Profiles.Add(profile); runtime.Settings.ActiveProfileId = profile.Id;
            runtime.Settings.Actions = profile.Actions;
            var model = new MainViewModel(runtime);
            foreach (var action in profile.Actions) model.ActiveActions.Add(new(action));
            var window = new MainWindow(model, runtime);
            var root = (FrameworkElement)window.FindName("WideShell");
            model.ActiveBrowser.SelectedView = ActionBrowserViewModel.ViewOptions.Single(o => o.Columns == 3);
            model.ActiveBrowser.VisibleEntries.Single(e => e.IsGroup).Group!.IsExpanded = true;
            Layout(root, 1388, 900);
            var original = profile.Actions.Select(a => a.Id).ToArray();
            var rows = Find<Border>(root).Where(r => r.Name == "SequenceCardDropTarget").ToList();
            Check(rows.Count == 5 && rows[3].TranslatePoint(new Point(), root).Y > rows[0].TranslatePoint(new Point(), root).Y,
                "Grid must wrap onto a second row for this test.");
            Save(root, Path.Combine(output, "grid-before.png"));

            void Drop(string source, string target, bool after)
            {
                var row = Find<Border>(root).Single(r => r.Name == "SequenceCardDropTarget" && r.DataContext is ActionViewModel a && a.Id == target);
                var key = ((ActionViewModel)row.DataContext).SequenceGroupKey;
                var dragType = typeof(MainWindow).GetNestedType("SequenceDrag", BindingFlags.NonPublic)!;
                var drag = Activator.CreateInstance(dragType, key, source)!;
                var data = new DataObject("AnomalyDonationBridge.SequenceStep", drag);
                var constructor = typeof(DragEventArgs).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
                DragEventArgs Args(RoutedEvent kind) => (DragEventArgs)constructor.Invoke([data,
                    DragDropKeyStates.LeftMouseButton, DragDropEffects.Move, row,
                    new Point(after ? row.ActualWidth - 1 : 1, after ? 1 : row.ActualHeight - 1)]);
                var over = Args(DragDrop.PreviewDragOverEvent); over.RoutedEvent = DragDrop.PreviewDragOverEvent;
                row.RaiseEvent(over);
                Check(over.Handled && over.Effects == DragDropEffects.Move, "Actual grid drag-over rejected the selected card.");
                model.ReportHotkeyMessage(string.Empty);
                var drop = Args(DragDrop.PreviewDropEvent); drop.RoutedEvent = DragDrop.PreviewDropEvent;
                row.RaiseEvent(drop);
                var wait = Stopwatch.StartNew();
                while (!model.Notice.Contains("Порядок шагов сохранён") && wait.Elapsed < TimeSpan.FromSeconds(5))
                { Pump(); Thread.Sleep(10); }
                Check(drop.Handled && model.Notice.Contains("Порядок шагов сохранён"), "Actual routed drop did not save: " + model.Notice);
                model.ActiveBrowser.VisibleEntries.Single(e => e.IsGroup).Group!.IsExpanded = true;
                Layout(root, 1388, 900);
            }
            Drop(original[0], original[2], true);
            Check(runtime.Settings.Actions.Select(a => a.Id).SequenceEqual([original[1], original[2], original[0], original[3], original[4], original[5]]),
                "Right-half drop did not put the source after the target.");
            Drop(original[4], original[1], false);
            var expected = new[] { original[4], original[1], original[2], original[0], original[3], original[5] };
            Check(runtime.Settings.Actions.Select(a => a.Id).SequenceEqual(expected), "Wrapped-row move to the first card failed.");
            var loaded = new JsonSettingsStore(paths).LoadAsync().GetAwaiter().GetResult();
            Check(loaded.Actions.Select(a => a.Id).SequenceEqual(expected), "Routed drop order did not survive reload.");
            Save(root, Path.Combine(output, "grid-after.png"));

            var editorVm = model.CreateActionEditor(model.ActiveActions.Single(a => a.Id == random.Id));
            var editor = new ActionEditorWindow(editorVm, (DataTemplate)window.FindResource("ActionEditorTemplate"));
            var editorRoot = (FrameworkElement)editor.Content;
            foreach (var width in new[] { 760d, 980d })
            {
                Layout(editorRoot, width, 650);
                var checks = Find<CheckBox>(editorRoot).Where(c => c.DataContext is MultiChoiceOptionViewModel).ToList();
                var visibleChecks = checks.Where(c => c.IsVisible || c.ActualHeight > 0).ToList();
                Check(visibleChecks.Count == 5 && visibleChecks.Select(c => c.TranslatePoint(new Point(), editorRoot).Y).Distinct().Count() == 5,
                    "Multiselect should be a vertical list, not wrapped columns.");
                visibleChecks[0].IsChecked = true; visibleChecks[2].IsChecked = true;
                Check(ActiveTemplateSelection.SelectedIds(editorVm.Draft.Model).Count == 2, "Checkbox binding lost instance selections.");
                Save(editorRoot, Path.Combine(output, $"selected-random-{width}.png"));
            }
            Pump(); Check(errors.ToString().Length == 0, "WPF binding errors: " + errors);
            editor.Close(); window.Close(); runtime.DisposeAsync().AsTask().GetAwaiter().GetResult(); app.Shutdown();
            Console.WriteLine("PASS actual grid drag-over/drop, wrapped rows, persistence, selected-random editor and checkboxes; isolated data only.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Layout(FrameworkElement root, double width, double height)
    {
        root.Margin = new Thickness(0); root.Width = width; root.Height = height;
        for (var i = 0; i < 3; i++) { root.Measure(new(width, height)); root.Arrange(new(0, 0, width, height)); root.UpdateLayout(); Pump(); }
    }
    private static void Save(FrameworkElement root, string path)
    {
        var image = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(root);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(path); png.Save(file);
    }
    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is T item) yield return item; foreach (var nested in Find<T>(child)) yield return nested; }
    }
}
