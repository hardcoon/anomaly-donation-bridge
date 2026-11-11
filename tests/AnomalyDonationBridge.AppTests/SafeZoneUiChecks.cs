using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ellipse = System.Windows.Shapes.Ellipse;
using AnomalyDonationBridge.App;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.AppTests;

// Detached WPF rendering: no user settings, visible windows, game or providers.
internal static class SafeZoneUiChecks
{
    internal static int Run(string output)
    {
        try
        {
            Directory.CreateDirectory(output);
            var app = new AnomalyDonationBridge.App.App(startRuntime: false); app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            using var temporary = new Program.TemporaryDirectory();
            var runtime = new BridgeRuntime(new AppPaths(Path.Combine(temporary.Path, "data")));
            var game = Path.Combine(temporary.Path, "game");
            Directory.CreateDirectory(Path.Combine(game, "gamedata", "scripts"));
            var loader = new FilePresetLoader();
            foreach (var script in Directory.EnumerateFiles(loader.EffectsDirectory, "*.script"))
                File.Copy(script, Path.Combine(game, "gamedata", "scripts", Path.GetFileName(script)));
            var presets = loader.Load(game).Presets.ToList();
            Check(presets.Count == 70, "UI fixture must contain all 70 real templates.");
            typeof(BridgeRuntime).GetField("_presets", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, presets);
            var catalog = new CatalogSnapshot(new CatalogRevision("safe-zone-ui"), new string('a',64),
                [SafeZoneTests.Level("l01_escape","Кордон"),SafeZoneTests.Level("l05_bar","Бар"),SafeZoneTests.Level("l02_garbage","Свалка")]);
            var catalogField = typeof(BridgeRuntime).GetField("_catalogState", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var trace = new StringWriter(); var listener = new TextWriterTraceListener(trace);
            PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            try
            {
                foreach (var light in new[]{false,true})
                {
                    ThemeManager.Apply(light);
                    foreach (var width in new[]{860,1080,1320})
                    foreach (var state in new[]{"collapsed","selected","all","selected-disabled","all-disabled","empty-enabled","empty-disabled","no-catalog","long-name","adding"})
                    {
                        catalogField.SetValue(runtime, state == "no-catalog" ? CatalogRuntimeState.Empty :
                            new CatalogRuntimeState(CapabilitySet.Empty,catalog,false,"UI fixture"));
                        runtime.Settings.SafeZone = new SafeZoneSettings
                        {
                            Enabled = !state.EndsWith("-disabled",StringComparison.Ordinal),
                            Rules = state.StartsWith("empty-") || state == "no-catalog" ? [] :
                            [new() { LevelName="l01_escape",DisplayName="Кордон" },
                             new() { LevelName="l05_bar",DisplayName=state=="long-name" ?
                                 "Очень длинное название локации из модифицированной игры — промзона и исследовательский комплекс" : "Бар",
                                 BlockAll=false,HandlerIds=["spawn_mutants","spawn_donor_boss","hardcoon.adb.helicopter_boss"] }]
                        };
                        var viewModel = new SettingsViewModel(runtime);
                        if (state is "selected" or "selected-disabled") viewModel.SafeZone.SelectedRule = viewModel.SafeZone.Rules[1];
                        if (state is "all" or "all-disabled") viewModel.SafeZone.SelectedRule = viewModel.SafeZone.Rules[0];
                        if (state == "adding") viewModel.SafeZone.BeginAddLocationCommand.Execute(null);
                        var window = new SettingsWindow(viewModel);
                        var root = (FrameworkElement)window.Content; root.Margin = new Thickness(0);
                        var tabs = Find<TabControl>(root).Single();
                        var safeTab = tabs.Items.OfType<TabItem>().Single(tab=>Equals(tab.Header,"Безопасная зона"));
                        tabs.SelectedItem = safeTab;
                        Layout(root,width);
                        var pane=(FrameworkElement)window.FindName("SafeZonePane");
                        var heading=(FrameworkElement)window.FindName("SafeZoneHeading");
                        var host=(FrameworkElement)tabs.Template.FindName("PART_SelectedContentHost",tabs);
                        Check(safeTab.VerticalContentAlignment==VerticalAlignment.Stretch, "Tab still centres its content vertically.");
                        var gap=Bounds(heading,root).Top-Bounds(host,root).Top;
                        Check(gap>=0 && gap<=20, "Settings heading drifts into the middle of the tab: "+gap);
                        Check(!Find<ListBox>(pane).Any(), "Native empty/disabled ListBox can still paint a white surface.");
                        Check(Find<ScrollViewer>(pane).Count()==0, "Compact rules contain nested scrolling areas.");
                        var cards=Find<Border>(pane).Where(border=>Equals(border.Tag,"SafeZoneRuleCard")).ToList();
                        Check(cards.Count==viewModel.SafeZone.Rules.Count,"Missing compact rule rows.");
                        var editors=Find<Border>(pane).Where(border=>Equals(border.Tag,"SafeZoneExpandedEditor")).ToList();
                        Check(editors.Count==(state is "selected" or "all" or "selected-disabled" or "all-disabled" ? 1:0),"Collapsed rules keep extra editors alive.");
                        CheckThemedControls(pane,window,app,root,width);
                        foreach(var card in cards)
                        {
                            var bounds=Bounds(card,root);
                            Check(bounds.Left>=0 && bounds.Right<=root.ActualWidth+1,"Rule card overflows settings width.");
                            foreach(var button in Find<Button>(card).Where(button=>button.Tag is "SafeZoneToggleRule" or "SafeZoneRemoveRule"))
                            {
                                var buttonBounds=Bounds(button,root);
                                Check(buttonBounds.Right<=bounds.Right+1,"Rule buttons overflow card.");
                                Check(button.Command is not null && button.CommandParameter==card.DataContext,"Rule action lost its command or location.");
                            }
                        }
                        if(width>=1080)
                        {
                            var ys=tabs.Items.OfType<TabItem>().Select(tab=>Bounds(tab,root).Y).ToList();
                            Check(ys.Max()-ys.Min()<1,"Default settings tabs wrap to a second row.");
                        }
                        if(state=="selected")
                        {
                            var rule=viewModel.SafeZone.SelectedRule!;
                            var allMode=Find<RadioButton>(pane).Single(button=>Equals(button.Content,"Все эффекты"));
                            var selectedMode=Find<RadioButton>(pane).Single(button=>Equals(button.Content,"Выбранные эффекты"));
                            allMode.SetCurrentValue(ToggleButton.IsCheckedProperty,true);
                            allMode.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
                            Check(rule.BlockAll && selectedMode.IsChecked==false,"Styled radios broke exclusive mode selection.");
                            selectedMode.SetCurrentValue(ToggleButton.IsCheckedProperty,true);
                            selectedMode.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
                            Layout(root,width);
                            Check(rule.BlockSelected && allMode.IsChecked==false,"Styled radios failed to restore selected-effect mode.");
                            Check(rule.TotalCount==70,"Template picker lost shipped templates.");
                            var checkbox=Find<CheckBox>(pane).Single(box=>box.DataContext is SafeZoneEffectViewModel effect && effect.HandlerId=="spawn_mutants");
                            checkbox.SetCurrentValue(ToggleButton.IsCheckedProperty,false);
                            checkbox.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
                            Check(rule.SelectedCount==2,"Native checkbox did not update the rule.");
                            checkbox.SetCurrentValue(ToggleButton.IsCheckedProperty,true);
                            checkbox.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
                            var toggle=Find<Button>(cards[1]).Single(button=>Equals(button.Tag,"SafeZoneToggleRule"));
                            toggle.Command!.Execute(toggle.CommandParameter); Layout(root,width);
                            Check(!rule.IsExpanded && !Find<Border>(pane).Any(border=>Equals(border.Tag,"SafeZoneExpandedEditor")),"Collapse does not remove the editor.");
                            toggle.Command.Execute(toggle.CommandParameter); Layout(root,width);
                            Check(rule.IsExpanded && viewModel.SafeZone.Rules[0].BlockAll,"Switching rule editors changed another location's block mode.");
                        }
                        var empty=(Border)window.FindName("SafeZoneEmptyCard");
                        Check((empty.Visibility==Visibility.Visible)==!viewModel.SafeZone.HasRules,"Empty state is displayed over configured rules.");
                        var bitmap=Save(root,Path.Combine(output,$"safe-zone-{(light?"light":"dark")}-{width}-{state}.png"));
                        if(!viewModel.SafeZone.HasRules)
                        {
                            var rect=Bounds(empty,root);
                            var sample=new byte[4];
                            bitmap.CopyPixels(new Int32Rect((int)rect.Left+10,(int)rect.Top+10,1,1),sample,4,0);
                            var expected=((SolidColorBrush)app.FindResource("SurfaceBrush")).Color;
                            Check(sample[0]==expected.B && sample[1]==expected.G && sample[2]==expected.R,"Empty/disabled state has a foreign system background.");
                        }
                        if(state=="no-catalog")
                            Check(!viewModel.SafeZone.BeginAddLocationCommand.CanExecute(null),"Location picker enabled without a catalog.");
                        if(state=="adding")
                        {
                            var form=(Border)window.FindName("SafeZoneAddForm");
                            Check(form.Visibility==Visibility.Visible && Bounds(form,root).Right<=root.ActualWidth,"Inline add form is hidden or overflowing.");
                            var add=Find<Button>(form).Single(button=>Equals(button.Content,"Добавить"));
                            Check(add.Command is not null && add.Command.CanExecute(null),"Native Add button is disconnected.");
                            add.Command!.Execute(null); Layout(root,width);
                            Check(viewModel.SafeZone.Rules.Count==3 && !viewModel.SafeZone.IsAddingLocation && viewModel.SafeZone.SelectedRule is {IsExpanded:true},
                                "Adding a location failed to close the picker and open its editor.");
                        }
                        window.Close();
                    }
                }
                Check(trace.ToString().Length==0,"Safe-zone WPF binding errors: "+trace);
            }
            finally
            {
                PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
                runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            Console.WriteLine("PASS compact safe-zone WPF: 60 dark/light and width/state cases; themed radio indicators and chevrons; expand/collapse and exclusive selection; empty/disabled pixels; all 64 templates; no binding errors.");
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    private static void Check(bool condition,string reason){if(!condition)throw new InvalidOperationException(reason);}
    private static void CheckThemedControls(FrameworkElement pane,Window window,Application app,FrameworkElement root,int width)
    {
        foreach(var radio in Find<RadioButton>(pane))
        {
            Check(radio.Style==(Style)window.FindResource("SafeZoneRadioButtonStyle") && radio.Focusable,
                "Safe-zone radio fell back to a native Windows template or lost keyboard focus.");
            var ring=radio.Template.FindName("IndicatorRing",radio) as Ellipse;
            var dot=radio.Template.FindName("SelectedDot",radio) as Ellipse;
            Check(ring is not null && dot is not null,"Themed radio indicator is missing.");
            Check(((SolidColorBrush)ring!.Fill).Color==((SolidColorBrush)app.FindResource("InputBrush")).Color,
                "Radio uses a white system background rather than the app input surface.");
            Check(((SolidColorBrush)dot!.Fill).Color==((SolidColorBrush)app.FindResource("AccentBrush")).Color &&
                (dot.Visibility==Visibility.Visible)==(radio.IsChecked==true),"Radio selected mark ignores theme or checked state.");
        }
        foreach(var expander in Find<Expander>(pane).ToList())
        {
            Check(expander.Style==(Style)window.FindResource("SafeZoneGroupExpanderStyle"),"Group fell back to the native circular Windows icon.");
            if(expander.ActualHeight<=0)continue;
            var header=expander.Template.FindName("HeaderSite",expander) as ToggleButton;
            Check(header is not null && header.Focusable,"Themed group header is missing or not keyboard-accessible.");
            var arrow=header!.Template.FindName("Arrow",header) as System.Windows.Shapes.Path;
            Check(arrow is not null && !Find<Ellipse>(header).Any(),"Group header contains a native circle instead of the app chevron.");
            var expected=(SolidColorBrush)app.FindResource(expander.IsExpanded?"AccentForegroundBrush":"MutedBrush");
            Check(((SolidColorBrush)arrow!.Fill).Color==expected.Color &&
                ((RotateTransform)arrow.RenderTransform).Angle==(expander.IsExpanded?0:-90),"Chevron color/direction does not follow group state.");
            if(!header.IsEnabled)continue;
            var wasExpanded=expander.IsExpanded;
            header.SetCurrentValue(ToggleButton.IsCheckedProperty,!wasExpanded);
            header.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();Layout(root,width);
            Check(expander.IsExpanded!=wasExpanded &&
                ((RotateTransform)arrow.RenderTransform).Angle==(expander.IsExpanded?0:-90),"Styled header stopped expanding/collapsing the group.");
            header.SetCurrentValue(ToggleButton.IsCheckedProperty,wasExpanded);
            header.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();Layout(root,width);
        }
    }
    private static Rect Bounds(FrameworkElement element,FrameworkElement ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));
    private static void Layout(FrameworkElement root,int width)
    { root.Measure(new Size(width,700));root.Arrange(new Rect(0,0,width,700));root.UpdateLayout(); }
    private static IEnumerable<T> Find<T>(DependencyObject root) where T:DependencyObject
    {
        if(root is T result)yield return result;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
            foreach(var child in Find<T>(VisualTreeHelper.GetChild(root,i)))yield return child;
    }
    private static RenderTargetBitmap Save(FrameworkElement root,string path)
    {
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth),(int)Math.Ceiling(root.ActualHeight),96,96,PixelFormats.Pbgra32);
        bitmap.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream=File.Create(path);encoder.Save(stream);return bitmap;
    }
}
