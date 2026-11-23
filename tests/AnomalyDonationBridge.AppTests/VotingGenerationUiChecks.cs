using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AnomalyDonationBridge.App;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.AppTests;

internal static class VotingGenerationUiChecks
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
            Console.WriteLine("PASS native automatic voting, responsive 2/3/4-row fields, collapse persistence, multi-select, rich help and generated rows.");
            app.Shutdown(); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static async Task CheckAsync(string output)
    {
        using var temporary = new Program.TemporaryDirectory();
        var paths = new AppPaths(Path.Combine(temporary.Path, "data"));
        await using var runtime = new BridgeRuntime(paths);
        var names = new[] { "БТР атакует", "Напарник с именем донатчика", "Случайный телепорт", "Странствующий торговец" };
        runtime.Settings.Actions = Enumerable.Range(0, 14).Select(i => new ActionDefinition
        { DisplayName = i < names.Length ? names[i] : "Активный шаблон " + i, HandlerId = "add_radiation", IsActive = true }).ToList();
        runtime.Settings.UserPresets = Enumerable.Range(0, 12).Select(i => new ActionDefinition
        { DisplayName = "Пользовательский шаблон " + i, HandlerId = "add_radiation" }).ToList();
        await runtime.InitializeVotingModuleAsync();
        var draft = runtime.Voting.Snapshot;
        draft.Generation = new() { Enabled = true, Count = 4 };
        draft.MoneyGoal = false; draft.TimeGoal = true; draft.DurationSeconds = 1;
        await runtime.Voting.SaveSettingsAsync(draft);
        var trace = new StringWriter(); var listener = new TextWriterTraceListener(trace);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        using var controller = new VotingObsController(runtime);
        using var view = new VotingView(runtime, controller);
        var renderHost = new Window { Content = view, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            foreach (var light in new[] { false, true })
            {
                ThemeManager.Apply(light);
                Layout(view, 1180, 760);
                var form = Field<FrameworkElement>(view, "_generationForm");
                var count = Field<TextBox>(view, "_generationCount");
                var prefix = Field<TextBox>(view, "_generationPrefix");
                var number = Field<Button>(view, "_generationNumbers");
                CheckGenerationLayout(view, 2);
                Check(form.ActualHeight < 185, "Wide generation form still consumes too much vertical space");
                Check(Math.Abs(prefix.TranslatePoint(new Point(), view).Y - number.TranslatePoint(new Point(), view).Y) < 1.1,
                    "Prefix and command format are not aligned");
                Check(Field<Button>(view, "_add").Visibility == Visibility.Collapsed, "Automatic mode still shows add command");
                Check(Field<TextBlock>(view, "_generationNote").Text.StartsWith("При каждом старте:", StringComparison.Ordinal), "Colon missing from launch note");
                var choices = Field<ObservableCollection<VotingTemplateChoice>>(view, "_generationChoices");
                Check(choices.Count == 14 && choices.All(choice => choice.Selected), "Initial active source does not select all");
                Save(view, Path.Combine(output, "automatic-" + (light ? "light" : "dark") + ".png"));
            }
            ThemeManager.Apply(false);
            foreach (var (width, rows) in new[] { (1020d, 3), (850d, 4) })
            {
                Layout(view, width, 760); CheckGenerationLayout(view, rows);
                Save(view, Path.Combine(output, "responsive-" + rows + "-rows.png"));
            }
            Click(Field<Button>(view, "_panelToggle"));
            await Wait(() => runtime.UiPreferences.VotingSettingsPanelCollapsed);
            foreach (var (width, rows) in new[] { (700d, 3), (560d, 4) })
            {
                Layout(view, width, 760); CheckGenerationLayout(view, rows);
                Save(view, Path.Combine(output, "compact-" + rows + "-rows.png"));
            }
            Layout(view, 1180, 760);
            Click(Field<Button>(view, "_panelToggle"));
            await Wait(() => !runtime.UiPreferences.VotingSettingsPanelCollapsed);
            Layout(view, 1180, 760); CheckGenerationLayout(view, 2);
            var selectable = Field<ObservableCollection<VotingTemplateChoice>>(view, "_generationChoices");
            selectable[0].Selected = false; selectable[1].Selected = false;
            var pickerBody = (FrameworkElement)Field<Popup>(view, "_generationPopup").Child;
            pickerBody.Width = Field<Button>(view, "_generationPicker").ActualWidth;
            Layout(pickerBody, pickerBody.Width, 350); Save(pickerBody, Path.Combine(output, "template-multiselect.png"));
            await view.FlushPendingChangesAsync();
            Check(runtime.Voting.Snapshot.Generation.ActiveTemplateIds!.Count == 12, "Checkbox selections were not persisted");
            Click(Field<Button>(view, "_generationUser"));
            Check(selectable.Count == 12 && selectable.All(choice => choice.Selected), "New source did not default to all");
            selectable[0].Selected = false;
            await view.FlushPendingChangesAsync();
            Click(Field<Button>(view, "_generationActive"));
            Check(selectable.Count == 14 && selectable.Count(choice => choice.Selected) == 12, "Source switch lost its own selection");
            await view.FlushPendingChangesAsync();
            Click(Field<Button>(view, "_collectionToggle"));
            await Wait(() => runtime.Voting.Snapshot.Phase == VotingPhase.Collecting && !Field<bool>(view, "_busy") && view.Rows.Count == 4);
            var entries = view.Rows.Select(row => row.Model.Id).ToArray();
            Check(view.Rows.All(row => !row.CanEdit), "Generated rows expose manual editing");
            Layout(view, 1180, 760);
            var beforeCollapse = Field<ListBox>(view, "_commandList").TranslatePoint(new Point(), view).Y;
            Click(Field<Button>(view, "_generationDisclosure"));
            await Wait(() => runtime.UiPreferences.VotingGenerationPanelCollapsed);
            Layout(view, 1180, 760);
            Check(Field<FrameworkElement>(view, "_generationForm").Visibility == Visibility.Collapsed &&
                beforeCollapse - Field<ListBox>(view, "_commandList").TranslatePoint(new Point(), view).Y > 120,
                "Collapsing generation settings did not free command-list space");
            Check(Field<CheckBox>(view, "_generationToggle").IsChecked == true && view.Rows.Select(row => row.Model.Id).SequenceEqual(entries),
                "Collapsing settings changed the current generated round");
            Check((await new JsonUiPreferencesStore(paths).LoadAsync()).VotingGenerationPanelCollapsed,
                "Generation collapse preference is not durable");
            Save(view, Path.Combine(output, "automatic-collapsed.png"));
            using (var restoredView = new VotingView(runtime, controller))
            {
                Layout(restoredView, 1180, 760);
                Check(Field<FrameworkElement>(restoredView, "_generationForm").Visibility == Visibility.Collapsed &&
                    Field<CheckBox>(restoredView, "_generationToggle").IsChecked == true, "Reopening the view lost its collapse state or automatic mode");
            }
            Field<TextBox>(view, "_generationCount").Text = "99";
            Layout(view, 1180, 760);
            Check(Field<TextBlock>(view, "_generationProblem").Visibility == Visibility.Visible &&
                Field<StackPanel>(view, "_generationDetails").Visibility == Visibility.Collapsed &&
                Field<FrameworkElement>(view, "_generationForm").Visibility == Visibility.Visible,
                "Collapsed settings hide the reason a fresh start is unavailable");
            Field<TextBox>(view, "_generationCount").Text = "4";
            Click(Field<Button>(view, "_generationDisclosure"));
            await Wait(() => !runtime.UiPreferences.VotingGenerationPanelCollapsed);
            foreach (var light in new[] { false, true })
            {
                foreach (var color in new[] { "yellow", "red", "green", "blue" })
                {
                    ThemeManager.Apply(light, color);
                    Layout(view, 1180, 760);
                    CheckGeneratedTextContrast(view);
                }
                Save(view, Path.Combine(output, "automatic-running-" + (light ? "light" : "dark") + ".png"));
            }
            ThemeManager.Apply(false);
            Field<TextBox>(view, "_generationCount").Text = "5";
            Click(Field<Button>(view, "_generationUser"));
            Click(Field<Button>(view, "_generationNumbers"));
            Field<TextBox>(view, "_generationPrefix").Text = "";
            await view.FlushPendingChangesAsync();
            Check(runtime.Voting.Snapshot.Options.Count == 4 && view.Rows.Select(row => row.Model.Id).SequenceEqual(entries),
                "Editing next-launch settings changed current commands");
            var caret = Field<TextBox>(view, "_generationPrefix").GetRectFromCharacterIndex(0);
            Check(caret.X <= 11, "Voting prefix has doubled text padding");
            await Task.Delay(1100); await runtime.Voting.TickAsync();
            await runtime.Voting.SetCollectionActiveAsync(draft.CollectionId, false, runtime.Settings.ActiveProfileId);
            await Wait(() => !Field<bool>(view, "_busy"));
            Click(Field<Button>(view, "_collectionToggle"));
            await Wait(() => runtime.Voting.Snapshot.Phase == VotingPhase.Collecting && view.Rows.Count == 5 && !Field<bool>(view, "_busy"));
            Check(runtime.Voting.Snapshot.Options.All(option => runtime.Settings.UserPresets.Any(template => template.Id == option.Preset!.Id)) &&
                view.Rows.Select(row => row.Command).SequenceEqual(["1", "2", "3", "4", "5"]),
                "Native Start ignored new source, quantity or formatting");
            var persisted = await new JsonVotingStore(paths).LoadAsync();
            Check(persisted.Current.Generation.Count == 5 && persisted.Current.Generation.UserTemplateIds?.Count == 11 &&
                persisted.Current.Generation.ActiveTemplateIds?.Count == 12, "Saved JSON lost independent multi-select settings");
            Click(Field<Button>(view, "_generationInitials"));
            Field<TextBox>(view, "_generationPrefix").Text = "!";
            var help = Field<StackPanel>(view, "_generationTipContent");
            Check(Descendants<TextBlock>(help).Any(text => text.Text == "!БА") && Descendants<TextBlock>(help).Any(text => text.Text == "!СТ2"),
                "Rich help does not show abbreviation/collision examples");
            var tip = Field<ToolTip>(view, "_generationTip");
            var helpTarget = Field<FrameworkElement>(view, "_generationHelp");
            Check(helpTarget is not ButtonBase && !helpTarget.Focusable && !tip.IsHitTestVisible &&
                ReferenceEquals(tip.PlacementTarget, helpTarget), "Command help is clickable or lacks its placement anchor");
            tip.Content = null;
            var tipSurface = new Border { Child = help, Padding = new Thickness(12), Width = 400 };
            tipSurface.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
            tipSurface.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
            Layout(tipSurface, 400, 250); Save(tipSurface, Path.Combine(output, "command-examples.png"));
            tipSurface.Child = null; tip.Content = help;
            Field<CheckBox>(view, "_generationToggle").IsChecked = false;
            await view.FlushPendingChangesAsync();
            Check(Field<Button>(view, "_add").Visibility == Visibility.Visible && view.Rows.All(row => row.CanEdit), "Manual mode was not restored");
            foreach (var width in new[] { 1180d, 850d })
            {
                Layout(view, width, 760);
                var add = Field<Button>(view, "_add");
                var toggle = Field<CheckBox>(view, "_generationToggle");
                Check(Equals(toggle.Content, "Авто-формирование") &&
                    Math.Abs(toggle.ActualHeight - add.ActualHeight) < 1.1 && Math.Abs(toggle.ActualWidth - add.ActualWidth) < 1.1,
                    "Auto-formation must match the size of Add command");
                Check(Math.Abs(add.TranslatePoint(new Point(0, add.ActualHeight / 2), view).Y -
                    toggle.TranslatePoint(new Point(0, toggle.ActualHeight / 2), view).Y) < 1.1,
                    "Add command is not aligned with the generation switch");
                Check(Field<FrameworkElement>(view, "_commandHeading").TranslatePoint(new Point(), view).Y -
                    add.TranslatePoint(new Point(0, add.ActualHeight), view).Y >= 12,
                    "Add command is cramped against the command section divider");
                Save(view, Path.Combine(output, "manual-toolbar-" + width + ".png"));
            }
            var autoButton = Field<CheckBox>(view, "_generationToggle");
            var toggleProvider = (IToggleProvider)new CheckBoxAutomationPeer(autoButton).GetPattern(PatternInterface.Toggle);
            toggleProvider.Toggle(); await view.FlushPendingChangesAsync();
            Check(runtime.Voting.Snapshot.Generation.Enabled && autoButton.Template.FindName("Check", autoButton) is
                System.Windows.Shapes.Path { Visibility: Visibility.Visible }, "Auto-formation button did not check or save its state");
            toggleProvider.Toggle(); await view.FlushPendingChangesAsync();
            Check(!runtime.Voting.Snapshot.Generation.Enabled && autoButton.Template.FindName("Check", autoButton) is
                System.Windows.Shapes.Path { Visibility: Visibility.Collapsed }, "Auto-formation button did not uncheck");
            Check(trace.ToString().Length == 0, "WPF binding errors: " + trace);
        }
        finally
        {
            renderHost.Content = null; renderHost.Close();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); listener.Dispose();
        }
    }
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void CheckGenerationLayout(VotingView view, int expectedRows)
    {
        var fields = Field<Grid>(view, "_generationFields");
        var controls = new FrameworkElement[] { Field<Button>(view, "_generationActive"), Field<Button>(view, "_generationPicker"),
            Field<TextBox>(view, "_generationCount"), Field<TextBox>(view, "_generationPrefix"), Field<Button>(view, "_generationNumbers") };
        var rows = controls.Select(control => Math.Round(control.TranslatePoint(new Point(), fields).Y)).Distinct().Count();
        Check(rows == expectedRows && Field<Grid>(view, "_commandsPanel").Visibility == Visibility.Visible &&
            controls.All(control => control.Visibility == Visibility.Visible && control.ActualWidth > 0),
            $"Expected {expectedRows} visible generation rows; got {rows}, field width={fields.ActualWidth}");
        foreach (var control in controls.Append(Field<Button>(view, "_generationInitials")))
        {
            var start = control.TranslatePoint(new Point(), fields).X;
            Check(start >= -1 && start + control.ActualWidth <= fields.ActualWidth + 1 && control is Control { FontSize: >= 13 },
                "Responsive generation controls overflow or shrink the text");
        }
        if (expectedRows == 4)
            Check(Math.Abs(Field<TextBox>(view, "_generationCount").ActualWidth - fields.ActualWidth) < 1.1 &&
                Math.Abs(Field<TextBox>(view, "_generationPrefix").TranslatePoint(new Point(), fields).Y -
                    Field<Button>(view, "_generationNumbers").TranslatePoint(new Point(), fields).Y) < 1.1,
                "Narrow layout must retain a full-width count and a shared prefix/format row");
    }
    private static void CheckGeneratedTextContrast(VotingView view)
    {
        var rows = Descendants<Border>(view).Where(border => Equals(border.Tag, "VotingGeneratedCommandRow")).ToArray();
        Check(rows.Length > 0, "Generated rows were not rendered for the contrast check");
        var expected = ((SolidColorBrush)Application.Current.FindResource("TextBrush")).Color;
        foreach (var row in rows)
        {
            var data = (VotingOptionRow)row.DataContext;
            foreach (var value in new[] { data.Command, data.BindingTitle, data.ShareText })
            {
                var text = Descendants<TextBlock>(row).First(block => block.Text == value);
                var foreground = ((SolidColorBrush)text.Foreground).Color;
                var background = ((SolidColorBrush)(value == data.Command ?
                    Application.Current.FindResource("InputBrush") : row.Background)).Color;
                Check(foreground == expected && Contrast(foreground, background) >= 4.5,
                    $"Generated text is unreadable: {value}; foreground={foreground}, expected={expected}, background={background}, light={ThemeManager.IsLightTheme}, accent={ThemeManager.ColorKey}");
            }
        }
    }
    private static double Contrast(Color foreground, Color background)
    {
        static double Linear(byte channel) => channel / 255d <= 0.04045 ? channel / 255d / 12.92 : Math.Pow((channel / 255d + 0.055) / 1.055, 2.4);
        static double Luminance(Color color) => 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        var first = Luminance(foreground); var second = Luminance(background);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }
    private static async Task Wait(Func<bool> ready)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); while (!ready()) await Task.Delay(10, timeout.Token); }
    private static void Layout(FrameworkElement root, double width, double height)
    { root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); }
    private static void Save(FrameworkElement root, string path)
    {
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (var nested in Descendants<T>(child)) yield return nested; }
    }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
