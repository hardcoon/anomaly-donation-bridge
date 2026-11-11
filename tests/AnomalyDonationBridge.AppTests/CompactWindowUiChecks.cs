using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

// Explicitly isolated WPF renderer: no IPC server, accounts or donation processing is started.
internal static class CompactWindowUiChecks
{
    public static int Run(string outputDirectory)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var application = new AnomalyDonationBridge.App.App(startRuntime: false);
            application.InitializeComponent();
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var errors = new StringWriter();
            PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(errors));
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            var isolated = Path.Combine(outputDirectory, "isolated-" + Guid.NewGuid().ToString("N"));
            var runtime = new BridgeRuntime(new AppPaths(Path.Combine(isolated, "data")));
            var profile = new DonationProfile { Name = "Isolated compact layout" };
            runtime.Settings.Profiles.Add(profile); runtime.Settings.ActiveProfileId = profile.Id;
            runtime.InitializeVotingModuleAsync().GetAwaiter().GetResult();
            var model = new MainViewModel(runtime) { ShowTemplateDescriptions = false };
            var effects = Path.Combine(AppContext.BaseDirectory, "effects");
            var game = Path.Combine(isolated, "fixture-game");
            var gameScripts = Path.Combine(game, "gamedata", "scripts");
            Directory.CreateDirectory(gameScripts);
            foreach (var file in Directory.EnumerateFiles(Path.Combine(effects, "gamedata", "scripts"), "*.script"))
                File.Copy(file, Path.Combine(gameScripts, Path.GetFileName(file)));
            var presets = new FilePresetLoader(effects).Load(game).Presets;
            Check(presets.Count == 70, "The renderer did not load the 70 actual shipped template headers.");
            foreach (var preset in presets) model.Presets.Add(new ActionViewModel(preset.Clone(), isPreset: true));
            string[] ids = ["god_mode", "hardcoon.adb.helicopter_companion", "hardcoon.adb.equipment_ammo", "hardcoon.adb.new_life"];
            var amounts = new[] { 31m, 500m, 100m, 1000m };
            for (var index = 0; index < ids.Length; index++)
            {
                var definition = presets.Single(preset => preset.HandlerId == ids[index]).CloneAsNewInstance();
                definition.IsActive = true;
                definition.Triggers = [new() { Amount = amounts[index], Currency = "RUB" }];
                if (index == 0)
                    foreach (var currency in new[] { "USD", "EUR", "BYN", "KZT", "UAH", "BRL", "TRY" })
                        definition.Triggers.Add(new() { Amount = 5, Currency = currency });
                runtime.Settings.Actions.Add(definition);
                model.ActiveActions.Add(new ActionViewModel(definition, displayOrdinal: index + 1));
                model.UserPresets.Add(new ActionViewModel(definition.CloneAsNewInstance(), isPreset: true, isUserPreset: true, displayOrdinal: index + 1));
                model.History.Add(new() { ActionName = definition.DisplayName, ActionSnapshot = definition.Clone(),
                    DonationCreatedAt = DateTimeOffset.Now.AddMinutes(-index), Donor = "Локальный тест", Amount = amounts[index], Currency = "RUB", State = DispatchState.Executed, Detail = "Изолированная проверка интерфейса" });
            }
            SetPresentation(model, nameof(MainViewModel.DonationStatus), "DonateX: hardcoon");
            SetPresentation(model, nameof(MainViewModel.GameStatus), "Готова к эффектам");
            SetPresentation(model, nameof(MainViewModel.ExtensionStatus), "Аддон 1.0.156 · 66 шаблонов");
            var window = new MainWindow(model, runtime);
            var root = (FrameworkElement)window.Content;
            var compact = (CompactMainView)window.FindName("CompactShell");
            var wide = (FrameworkElement)window.FindName("WideShell");
            var preferences = new[] { model.ActiveBrowser.SelectedView, model.UserPresetBrowser.SelectedView, model.PresetBrowser.SelectedView };
            foreach (var light in new[] { false, true })
            {
                ThemeManager.Apply(light, "yellow");
                CheckDonorDialog(outputDirectory,light);
                CheckActionMenus(compact, model, application, runtime.Voting.Collections, outputDirectory, light);
                foreach (var size in new[] { new Size(1440,860), new Size(1080,680), new Size(1079,680), new Size(1080,679), new Size(800,560), new Size(640,520), new Size(1500,560), new Size(1440,860) })
                {
                    Layout(window, size);
                    var expected = size.Width < 1080 || size.Height < 680;
                    Check(window.IsCompactLayout == expected && compact.Visibility == (expected ? Visibility.Visible : Visibility.Collapsed) && wide.Visibility == (expected ? Visibility.Collapsed : Visibility.Visible), "Incorrect automatic layout at " + size);
                    Check(compact.FontSize == 13 && window.FontSize == 13 && compact.LayoutTransform.Value.IsIdentity, "Compact mode scaled the text.");
                    if (!expected) continue;
                    var sidebar = (Border)compact.FindName("SidebarSurface");
                    Check(compact.IsSidebarExpanded == (compact.ActualWidth >= 760), "The sidebar did not adapt to the available width.");
                    Check(Math.Abs(sidebar.ActualWidth - (compact.IsSidebarExpanded ? 192 : 48)) < 1, "The sidebar has an unexpected width.");
                    var navigation = new[] { "ActiveTab", "UserTab", "TemplatesTab", "HistoryTab" }.Select(name => (ToggleButton)compact.FindName(name)).ToArray();
                    Check(navigation.Select(button => button.TranslatePoint(new Point(),root).Y).Distinct().Count() == 4, "Sections are not arranged vertically in the sidebar.");
                    foreach (var section in new[] { 0,1,2,3 })
                    {
                        model.SelectedSectionIndex = section; Layout(window,size);
                        Bounds(root);
                        Check(compact.IsHistorySection == (section == 3), "History did not follow the selected section.");
                        Check(compact.IsActiveSection == (section == 0), "Active-list tools leaked into another section.");
                        if(section!=3)
                        {
                            CheckCategoryRows(compact);
                            var item=Find<Button>(compact).First(button=>Displayed(button,root)&&button.DataContext is ActionViewModel);
                            var contextAction=(ActionViewModel)item.DataContext;
                            var context=window.GetTemplateContextMenu((DependencyObject)item.Parent,out var source);
                            Check(source is not null&&context is not null&&context.Items.OfType<MenuItem>().Select(entry=>entry.Header).SequenceEqual(compact.BuildActionMenu(contextAction).Items.OfType<MenuItem>().Select(entry=>entry.Header)),"Compact right-click menu differs from the ellipsis menu.");
                        }
                        if (section == 3)
                        {
                            model.SelectedHistory = model.History[0]; Layout(window,size);
                            var expectedText = ((SolidColorBrush)application.Resources["TextBrush"]).Color;
                            var labels = Find<TextBlock>(compact).Where(text => Displayed(text,root) && text.DataContext is EventHistoryEntry entry && (text.Text == entry.ActionName || text.Text == entry.Detail)).ToArray();
                            Check(labels.Length > 0 && labels.All(text => text.Foreground is SolidColorBrush brush && brush.Color == expectedText), "History titles/details have the wrong foreground in " + (light ? "light" : "dark") + " mode.");
                            if (size.Width == 640) Save(root,Path.Combine(outputDirectory,$"history-640-{(light?"light":"dark")}.png"));
                        }
                    }
                    model.SelectedSectionIndex = 0; Layout(window,size);
                    if (size.Width is 640 or 800) Save(root,Path.Combine(outputDirectory,$"compact-{size.Width:0}-{(light?"light":"dark")}.png"));
                    if(size.Width==640)Save(root,Path.Combine(outputDirectory,$"compact-640-{(light?"light":"dark")}-dpi150.png"),144);
                }
                model.SelectedSectionIndex=0;Layout(window,new Size(1440,860));
                foreach(var section in new[]{0,1,2})
                {
                    model.SelectedSectionIndex=section;Layout(window,new Size(1440,860));
                    var browser=section switch{1=>model.UserPresetBrowser,2=>model.PresetBrowser,_=>model.ActiveBrowser};
                    var saved=browser.SelectedView;
                    foreach(var option in ActionBrowserViewModel.ViewOptions)
                    {
                        browser.SelectedView=option;Layout(window,new Size(1440,860));
                        var card=Find<Button>(wide).First(button=>Displayed(button,root)&&button.ContextMenu is not null&&button.DataContext is ActionViewModel);
                        Check(ReferenceEquals(window.GetTemplateContextMenu((DependencyObject)card.Parent,out var anchor),card.ContextMenu)&&ReferenceEquals(anchor,card),"Right-click menu is missing in wide view "+option.Label);
                        card.ContextMenu!.PlacementTarget=card;CheckMenuPopups(card.ContextMenu);
                        CheckApprovedMenu(card.ContextMenu,(ActionViewModel)card.DataContext,application);
                    }
                    browser.SelectedView=saved;Layout(window,new Size(1440,860));
                    var target=Find<Button>(wide).First(button=>Displayed(button,root)&&button.ContextMenu is not null&&button.DataContext is ActionViewModel);
                    var wideMenu=target.ContextMenu!;wideMenu.PlacementTarget=target;
                    CheckMenuPopups(wideMenu);
                    var donor=wideMenu.Items.OfType<MenuItem>().Single(item=>Equals(item.Header,"Запустить от имени донатера"));
                    Check(ReferenceEquals(donor.Command,model.RunAsDonorCommand)&&ReferenceEquals(donor.CommandParameter,target.DataContext),"Wide donor launch lost its command or template.");
                    var voting=wideMenu.Items.OfType<MenuItem>().Single(item=>Equals(item.Header,"Добавить в голосование"));
                    Check(ReferenceEquals(voting.CommandParameter,target.DataContext),"Wide voting menu lost the selected template.");
                    foreach(var delete in wideMenu.Items.OfType<MenuItem>().Where(item=>(item.Header as string)?.StartsWith("Удалить",StringComparison.Ordinal)==true))
                        Check(delete.Foreground is SolidColorBrush brush&&brush.Color==((SolidColorBrush)application.Resources["BadBrush"]).Color,"Wide delete action lost its theme color.");
                }
                model.SelectedSectionIndex=0;Layout(window,new Size(1440,860));
                Save(root,Path.Combine(outputDirectory,$"regular-{(light?"light":"dark")}.png"));
            }
            Check(preferences.SequenceEqual(new[] { model.ActiveBrowser.SelectedView, model.UserPresetBrowser.SelectedView, model.PresetBrowser.SelectedView }), "Resizing overwrote the user's saved list/grid preferences.");
            foreach (var size in new[] { new Size(800,560), new Size(640,520) })
            {
                Layout(window,size);
                foreach (var name in new[] { "ActiveTab", "UserTab", "TemplatesTab", "HistoryTab" })
                {
                    var section = (ToggleButton)compact.FindName(name);
                    section.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(model.SelectedSectionIndex == int.Parse((string)section.Tag), "Sidebar navigation selected the wrong section.");
                }
            }
            model.SelectedSectionIndex=0;
            Layout(window,new Size(640,520));
            foreach (var section in new[]{0,1,2})
            {
                model.SelectedSectionIndex=section;model.ClearTemplateSearch();Layout(window,new Size(640,520));
                Check(((Border)compact.FindName("CompactSearch")).ActualWidth==220 && ((TextBox)compact.FindName("SmallSearchInput")).ActualWidth>100,
                    "Empty search must remain a text field in the compact window.");
                model.TemplateSearchText="гОд-МоД";Layout(window,new Size(640,520));
                Check(compact.VisibleItems!.Cast<object>().Count()==1,"Case-insensitive search failed in section "+section);
                CheckCategoryRows(compact);
                Bounds(root);
                if(section==0)Save(root,Path.Combine(outputDirectory,"compact-640-search-open.png"));
            }
            model.SelectedSectionIndex=0;SetPresentation(model,nameof(MainViewModel.QueueStatusText),"Следующий эффект через 00:12 · в очереди: 999");Layout(window,new Size(640,520));
            Check(model.ShowTemplateSearch&&model.TemplateSearchText=="гОд-МоД","Queue status hid the search.");
            var liveSearch=(TextBox)compact.FindName("SmallSearchInput");
            liveSearch.SetCurrentValue(TextBox.TextProperty,"нет такого шаблона 89133");Layout(window,new Size(640,520));
            Check(model.TemplateSearchText=="нет такого шаблона 89133"&&compact.VisibleItems!.Cast<object>().Count()==0,"Expanded search stopped accepting edits during the countdown.");
            liveSearch.SetCurrentValue(TextBox.TextProperty,"гОд-МоД");Layout(window,new Size(640,520));
            Bounds(root);Save(root,Path.Combine(outputDirectory,"compact-640-queue.png"));
            SetPresentation(model,nameof(MainViewModel.QueueStatusText),string.Empty);
            ((Button)compact.FindName("SmallClearSearchButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Layout(window,new Size(640,520));
            Check(model.TemplateSearchText.Length==0 && liveSearch.ActualWidth>100,"Clearing compact search must keep the input visible.");
            model.SelectedSectionIndex=2;Layout(window,new Size(640,520));
            var categoryTabs=(ListBox)compact.FindName("CompactCategories");categoryTabs.SelectedItem=ActionBrowserViewModel.Categories.Single(category=>category.Id=="inventory");Layout(window,new Size(640,520));
            Check(compact.VisibleItems!.Cast<object>().Count()==10,"The compact category picker did not filter inventory.");
            Layout(window,new Size(1440,860));Check(model.PresetBrowser.SelectedCategory.Id=="inventory","Expanding the window lost the category.");
            model.PresetBrowser.SelectCategory("all");model.SelectedSectionIndex=0;
            var action=model.ActiveActions[0];
            Check(window.GetTemplateContextMenu(new TextBox{DataContext=action},out _) is null,"Template context menu replaced the text editing menu.");
            Check(window.GetTemplateContextMenu(new MenuItem{DataContext=action},out _) is null,"A menu item recursively opens another template menu.");
            var menu=compact.BuildActionMenu(action);
            Check(menu.Items.OfType<MenuItem>().Any(item=>ReferenceEquals(item.Command,model.AddTriggerCommand)&&ReferenceEquals(item.CommandParameter,action)),"Compact rows lost Add Amount.");
            var remaining=compact.BuildPriceMenu(action,true).Items.OfType<MenuItem>().ToArray();
            Check(remaining.Length==5&&remaining.Select(item=>item.CommandParameter).SequenceEqual(action.Triggers.Skip(3))&&remaining.All(item=>ReferenceEquals(item.Command,model.EditTriggerCommand)),"The extra-price menu lost an edit target.");
            Layout(window,new Size(640,520));
            var visiblePrices=Find<Button>(compact).Where(button=>Displayed(button,root)&&button.DataContext is TriggerRuleViewModel trigger&&ReferenceEquals(trigger.Owner,action)).ToArray();
            Check(visiblePrices.Length==3&&visiblePrices.Select(button=>button.Content).SequenceEqual(action.Triggers.Take(3).Select(trigger=>(object)trigger.Label)),"The compact row hides one of the first three prices.");
            var grouping=model.ActiveActions[1].Model.CloneAsNewInstance();
            grouping.DisplayName="Очень длинное название второго шага для проверки переноса без наложения на кнопки";
            runtime.Settings.Actions.Add(grouping);model.ActiveActions.Add(new ActionViewModel(grouping,displayOrdinal:5));
            var group=model.ActiveBrowser.VisibleEntries.Single(entry=>entry.IsGroup).Group!;group.IsExpanded=true;
            Layout(window,new Size(640,520));
            var sequence=Find<Border>(compact).Where(row=>Equals(row.Tag,"SequenceStepRow")&&row.DataContext is ActionViewModel {IsSequenceStep:true}).ToList();
            Check(sequence.Count==2&&sequence.All(row=>row.AllowDrop),"Compact groups lost their sequence drop targets.");
            foreach(var row in sequence)row.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent,row));
            Check(Find<CheckBox>(compact).Any(box=>Equals(box.Content,"Выполнять строго по порядку")),"Strict-order setting disappeared.");
            var expand=Find<Button>(compact).Single(button=>ReferenceEquals(button.DataContext,group)&&Equals(button.ToolTip,"Развернуть или скрыть группу"));
            var groupMenu=window.GetTemplateContextMenu((DependencyObject)expand.Parent,out _);
            Check(groupMenu is not null&&groupMenu.Items.OfType<MenuItem>().Select(item=>item.Header).SequenceEqual(compact.BuildGroupMenu(group).Items.OfType<MenuItem>().Select(item=>item.Header)),"Compact group right-click lost its menu.");
            Check(expand.Parent is Grid header&&expand.ActualWidth>=header.ColumnDefinitions[1].ActualWidth-1&&expand.Template.FindName("GroupSurface",expand) is Border{Background:not null} surface&&surface.ActualWidth>=expand.ActualWidth-1,"Group expansion has no full-width hit target.");
            expand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(!group.IsExpanded,"Group did not collapse.");
            expand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(group.IsExpanded,"Group did not expand.");
            Bounds(root);Save(root,Path.Combine(outputDirectory,"compact-640-expanded-group.png"));
            model.ShowTemplateDescriptions=true;Layout(window,new Size(640,520));Bounds(root);
            Save(root,Path.Combine(outputDirectory,"compact-640-description.png"));
            model.SelectedSectionIndex=3;Layout(window,new Size(640,520));Bounds(root);Save(root,Path.Combine(outputDirectory,"compact-640-history.png"));
            model.SelectedHistory=model.History[1];Layout(window,new Size(1440,860));
            Check(ReferenceEquals(model.SelectedHistory,model.History[1]),"History selection changed across layouts.");
            Pump();Check(string.IsNullOrWhiteSpace(errors.ToString()),"WPF binding errors: "+errors);
            window.Close();application.Shutdown();
            Console.WriteLine("PASS sidebar window: two-row categories with search/queue, category clicks, right-click menus in all list/grid views and compact groups, text-edit menu preserved, three prices, group toggle, voting submenus, red delete items, dark/light themes, 640x520 and DPI 150%. No IPC/accounts started.");
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }

    private static void CheckActionMenus(CompactMainView compact,MainViewModel model,Application app,IReadOnlyList<VotingRound> rounds,string output,bool light)
    {
        foreach(var action in new[]{model.ActiveActions[0],model.UserPresets[0],model.Presets[0]})
        {
            var menu=compact.BuildActionMenu(action);
            Check(!menu.Items.OfType<MenuItem>().Any(item=>Equals(item.Header,"Суммы и валюты")||Equals(item.Header,"Условия суммы")),"The redundant price-menu section returned.");
            CheckMenuPopups(menu);
            CheckApprovedMenu(menu,action,app);
            var voting=menu.Items.OfType<MenuItem>().Single(item=>Equals(item.Header,"Добавить в голосование"));
            Check(ReferenceEquals(voting.CommandParameter,action),"Voting lost the selected template.");
            voting.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent,voting));
            Check(voting.Items.OfType<MenuItem>().Where(item=>item.Tag is string).Select(item=>item.Tag).SequenceEqual(rounds.Select(round=>(object)round.CollectionId)),"Compact voting submenu did not refresh from the current collections.");
            string? selected=null;
            var targets=Enumerable.Range(1,10).Select(index=>new VotingRound{CollectionId="menu-"+index,Title="Голосование "+index}).ToArray();
            MainWindow.PopulateVotingTargets(voting,targets,id=>{selected=id;return Task.CompletedTask;});
            ((MenuItem)voting.Items[7]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(selected=="menu-8","Voting selection routed to the wrong collection.");
            foreach(var item in MenuTree(menu.Items))
            {
                item.ApplyTemplate();
                if(item.HasItems)
                {
                    Check(item.Template.FindName("PART_Popup",item) is Popup popup&&BindingOperations.GetBinding(popup,Popup.IsOpenProperty)?.Path.Path=="IsSubmenuOpen","An expandable compact menu has no usable popup: "+item.Header);
                    Check(item.Template.FindName("SubmenuChevron",item) is System.Windows.Shapes.Path{Visibility:Visibility.Visible},"Submenu arrow is missing: "+item.Header);
                }
                if(item.Command is not null)
                {
                    Check(item.CommandParameter is TriggerRuleViewModel trigger?ReferenceEquals(trigger.Owner,action):ReferenceEquals(item.CommandParameter,action),"A menu command lost its action/price parameter: "+item.Header);
                    if((item.Header as string)?.StartsWith("Удалить",StringComparison.Ordinal)==true)
                        Check(item.Foreground is SolidColorBrush brush&&brush.Color==((SolidColorBrush)app.Resources["BadBrush"]).Color,"Delete menu item is not styled red.");
                }
            }
            var copyLabel=action.IsBuiltInPreset?"В пользовательские":action.IsUserPreset?"Сохранить копию":"Сохранить как пользовательский шаблон";
            foreach(var label in new[]{copyLabel,"Открыть код","Запустить от имени донатера"})
                Check(menu.Items.OfType<MenuItem>().Single(item=>Equals(item.Header,label)).Command is not null,"A template menu action is disconnected: "+label);
            Check(ReferenceEquals(menu.Items.OfType<MenuItem>().Single(item=>Equals(item.Header,"Запустить от имени донатера")).Command,model.RunAsDonorCommand),"Compact donor launch uses the wrong command.");
            if(!action.IsBuiltInPreset)
            {
                Check(menu.Items.OfType<MenuItem>().Any(item=>ReferenceEquals(item.Command,model.AddTriggerCommand)),"Add price is missing.");
            }
            Check(action.IsBuiltInPreset||menu.Items.OfType<MenuItem>().Any(item=>ReferenceEquals(item.Command,action.IsUserPreset?model.DeleteUserPresetCommand:model.DeleteActiveCommand)),"Delete action is missing.");
            if(action.IsUserPreset)
            {
                Check(menu.Items.OfType<MenuItem>().Any(item=>ReferenceEquals(item.Command,model.AddFromPresetCommand))&&menu.Items.OfType<MenuItem>().Any(item=>ReferenceEquals(item.Command,model.RenameUserPresetCommand)),"User preset actions are disconnected.");
            }
            var rows=new StackPanel();
            while(menu.Items.Count>0){var row=menu.Items[0];menu.Items.RemoveAt(0);rows.Children.Add((UIElement)row);}
            var frame=new Border{Width=320,Padding=new Thickness(4),Background=(Brush)app.Resources["SurfaceBrush"],BorderBrush=(Brush)app.Resources["BorderBrush"],BorderThickness=new Thickness(1),Child=rows};
            frame.Measure(new Size(320,double.PositiveInfinity));frame.Arrange(new Rect(frame.DesiredSize));frame.UpdateLayout();
            var role=action.IsBuiltInPreset?"builtin":action.IsUserPreset?"user":"active";
            Save(frame,Path.Combine(output,"action-menu-"+role+"-"+(light?"light":"dark")+".png"));
            if(action.IsUserPreset)Save(frame,Path.Combine(output,"action-menu-"+(light?"light":"dark")+".png"));
        }
    }
    private static void CheckApprovedMenu(ContextMenu menu,ActionViewModel action,Application app)
    {
        string[] expected=action.IsBuiltInPreset?
            ["В пользовательские","|","Запустить от имени донатера","Добавить в голосование","|","Открыть код"]:
            action.IsUserPreset?
            ["+ Сумма","В активные","|","Запустить от имени донатера","Добавить в голосование","|","Переименовать","Сохранить копию","Открыть код","|","Удалить пользовательский шаблон"]:
            ["+ Сумма","Горячая клавиша","|","Запустить от имени донатера","Добавить в голосование","|","Сохранить как пользовательский шаблон","Открыть код","|","Удалить из активных"];
        var outline=menu.Items.OfType<FrameworkElement>().Where(item=>item.Visibility==Visibility.Visible)
            .Select(item=>item is Separator?"|":((MenuItem)item).Header?.ToString()??string.Empty).ToArray();
        Check(outline.SequenceEqual(expected),"Template menu differs from the approved order: "+string.Join(" / ",outline));
        var donor=menu.Items.OfType<MenuItem>().Single(item=>Equals(item.Header,"Запустить от имени донатера"));
        Check(donor.Foreground is SolidColorBrush brush&&brush.Color==((SolidColorBrush)app.Resources["GoodBrush"]).Color,"Manual donor launch is not green in this layout/theme.");
        foreach(var delete in menu.Items.OfType<MenuItem>().Where(item=>item.Visibility==Visibility.Visible&&(item.Header as string)?.StartsWith("Удалить",StringComparison.Ordinal)==true))
            Check(delete.Foreground is SolidColorBrush color&&color.Color==((SolidColorBrush)app.Resources["BadBrush"]).Color,"Delete is not red in the ordered menu.");
    }
    private static void CheckDonorDialog(string output,bool light)
    {
        var dialog=new DonorLaunchWindow("Заспавнить напарника с именем донатера");
        var name=(TextBox)dialog.FindName("DonorNameBox");var message=(TextBox)dialog.FindName("MessageBox");var launch=(Button)dialog.FindName("LaunchButton");
        Check(!launch.IsEnabled&&!dialog.TryReadInput(out _),"Blank donor input can be submitted.");
        name.Text="  Исправленное имя  ";message.Text="Исправленный текст\nдоната";
        Check(launch.IsEnabled&&dialog.TryReadInput(out var input)&&input?.DonorName=="Исправленное имя"&&input.Message==message.Text,"Donor dialog lost supplied text.");
        Check(launch.IsDefault&&message.AcceptsReturn&&Find<Button>((FrameworkElement)dialog.Content).Any(button=>button.IsCancel),"Donor dialog lost submit/cancel or multiline input.");
        name.Text=string.Empty;message.Text=string.Empty;
        dialog.Left=-32000;dialog.Top=-32000;dialog.Opacity=0;dialog.ShowActivated=false;dialog.Show();dialog.UpdateLayout();Pump();
        var root=(FrameworkElement)dialog.Content;Bounds(root);
        foreach(var button in Find<Button>(root))Check(button.TranslatePoint(new Point(0,button.ActualHeight),root).Y<=root.ActualHeight+1,"Donor dialog clips a footer button.");
        var width=(int)Math.Ceiling(root.ActualWidth)+40;var height=(int)Math.Ceiling(root.ActualHeight)+40;
        var visual=new DrawingVisual();using(var context=visual.RenderOpen())
        {
            context.DrawRectangle(dialog.Background,null,new Rect(0,0,width,height));
            context.DrawRectangle(new VisualBrush(root),null,new Rect(20,20,root.ActualWidth,root.ActualHeight));
        }
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using(var file=File.Create(Path.Combine(output,"donor-launch-"+(light?"light":"dark")+".png")))encoder.Save(file);
        dialog.Close();
    }
    private static void CheckCategoryRows(CompactMainView compact)
    {
        var tabs=(ListBox)compact.FindName("CompactCategories");
        var items=Enumerable.Range(0,tabs.Items.Count).Select(index=>(ListBoxItem)tabs.ItemContainerGenerator.ContainerFromIndex(index)).ToArray();
        Check(items.Length==7,"Compact category tabs are missing.");
        var rows=items.GroupBy(item=>Math.Round(item.TranslatePoint(new Point(),tabs).Y)).OrderBy(row=>row.Key).Select(row=>row.Count()).ToArray();
        Check(rows.SequenceEqual(new[]{4,3}),"Compact categories are not laid out in two rows (4 + 3): "+string.Join(",",rows));
        Check(items.All(item=>item.TranslatePoint(new Point(item.ActualWidth,0),tabs).X<=tabs.ActualWidth+1),"Compact category text overflows the tabs.");
        var secondRow=items.Where(item=>Math.Round(item.TranslatePoint(new Point(),tabs).Y)==items.Max(item=>Math.Round(item.TranslatePoint(new Point(),tabs).Y))).ToArray();
        var search=(Border)compact.FindName("CompactSearch");var origin=search.TranslatePoint(new Point(),tabs);
        Check(origin.Y>=secondRow[0].TranslatePoint(new Point(),tabs).Y-1&&origin.Y+search.ActualHeight<=tabs.ActualHeight+1,"Compact search is not aligned with the second category row.");
        Check(origin.X>=secondRow.Max(item=>item.TranslatePoint(new Point(item.ActualWidth,0),tabs).X)+6,"Expanded search overlaps category tabs.");
    }
    private static IEnumerable<MenuItem> MenuTree(ItemCollection items)
    {
        foreach(var item in items.OfType<MenuItem>()){yield return item;foreach(var child in MenuTree(item.Items))yield return child;}
    }
    private static void CheckMenuPopups(ContextMenu menu)
    {
        // Load real WPF menu containers in a transparent, non-activating test window.
        var previousTarget=menu.PlacementTarget;
        var target=new Button{Content="Isolated menu host",ContextMenu=menu,DataContext=(previousTarget as FrameworkElement)?.DataContext,Tag=(previousTarget as FrameworkElement)?.Tag};
        var host=new Window{Content=target,Width=320,Height=100,Left=-32000,Top=-32000,Opacity=0,ShowActivated=false,ShowInTaskbar=false};
        try
        {
            host.Show();menu.Opacity=0;menu.PlacementTarget=target;menu.IsOpen=true;Pump();
            Check(menu.IsLoaded,"Context menu did not load.");
            foreach(var parent in menu.Items.OfType<MenuItem>().Where(item=>item.HasItems).ToArray()) Open(parent);
        }
        finally{menu.IsOpen=false;host.Close();menu.Opacity=1;menu.PlacementTarget=previousTarget;}
        static void Open(MenuItem item)
        {
            item.ApplyTemplate();
            Check(item.Template.FindName("PART_Popup",item) is Popup,"Submenu popup is absent: "+item.Header);
            var popup=(Popup)item.Template.FindName("PART_Popup",item);
            var surface=(FrameworkElement)popup.Child;surface.Opacity=0;
            try
            {
                item.IsSubmenuOpen=true;Pump();
                Check(popup.IsOpen&&Find<MenuItem>(surface).Any(),"Submenu opens without visible item containers: "+item.Header);
                foreach(var child in item.Items.OfType<MenuItem>().Where(child=>child.HasItems).ToArray()) Open(child);
            }
            finally{item.IsSubmenuOpen=false;Pump();surface.Opacity=1;}
        }
    }
    private static void SetPresentation(MainViewModel model,string property,string value)=>typeof(MainViewModel).GetProperty(property)!.SetValue(model,value);
    private static void Pump()=>Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
    private static void Layout(MainWindow window,Size size)
    {
        window.Width=size.Width;window.Height=size.Height;
        var root=(FrameworkElement)window.Content;root.Width=size.Width-16;root.Height=size.Height-40;
        for(var pass=0;pass<3;pass++){Pump();root.Measure(new Size(root.Width,root.Height));root.Arrange(new Rect(0,0,root.Width,root.Height));root.UpdateLayout();}
    }
    private static bool Displayed(FrameworkElement element,FrameworkElement root)
    {
        if(element.ActualWidth<=0||element.ActualHeight<=0)return false;
        for(DependencyObject? current=element;current is not null;current=VisualTreeHelper.GetParent(current))
        {if(current is UIElement {Visibility:not Visibility.Visible})return false;if(ReferenceEquals(current,root))return true;}
        return false;
    }
    private static IEnumerable<T> Find<T>(DependencyObject parent)where T:DependencyObject
    {for(var index=0;index<VisualTreeHelper.GetChildrenCount(parent);index++){var child=VisualTreeHelper.GetChild(parent,index);if(child is T result)yield return result;foreach(var nested in Find<T>(child))yield return nested;}}
    private static void Bounds(FrameworkElement root)
    {
        foreach(var control in Find<Control>(root).Where(item=>item is ButtonBase or ComboBox or TextBox).Where(item=>Displayed(item,root)))
        {var origin=control.TranslatePoint(new Point(),root);Check(origin.X>=-1&&origin.X+control.ActualWidth<=root.ActualWidth+1,$"Control overflows: {AutomationProperties.GetName(control)} {control.GetType().Name} {(control as ContentControl)?.Content}: {origin.X}+{control.ActualWidth}/{root.ActualWidth}");}
    }
    private static void Save(FrameworkElement root,string file,double dpi=96)
    {var scale=dpi/96;var bitmap=new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth*scale),(int)Math.Ceiling(root.ActualHeight*scale),dpi,dpi,PixelFormats.Pbgra32);bitmap.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(file);encoder.Save(output);}
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
