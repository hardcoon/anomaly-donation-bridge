using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using AnomalyDonationBridge.App.ViewModels;

namespace AnomalyDonationBridge.App;

public partial class CompactMainView : UserControl
{
    internal const int VisiblePriceCount = 3;
    public static readonly DependencyProperty SelectedBrowserProperty = DependencyProperty.Register(
        nameof(SelectedBrowser), typeof(ActionBrowserViewModel), typeof(CompactMainView));
    public static readonly DependencyProperty VisibleItemsProperty = DependencyProperty.Register(
        nameof(VisibleItems), typeof(IEnumerable), typeof(CompactMainView));
    public static readonly DependencyProperty IsHistorySectionProperty = DependencyProperty.Register(
        nameof(IsHistorySection), typeof(bool), typeof(CompactMainView), new PropertyMetadata(false));
    public static readonly DependencyProperty IsActiveSectionProperty = DependencyProperty.Register(
        nameof(IsActiveSection), typeof(bool), typeof(CompactMainView), new PropertyMetadata(true));
    public static readonly DependencyProperty IsSidebarExpandedProperty = DependencyProperty.Register(
        nameof(IsSidebarExpanded), typeof(bool), typeof(CompactMainView), new PropertyMetadata(false));
    private static readonly DependencyProperty SequenceWiredProperty = DependencyProperty.RegisterAttached(
        "SequenceWired", typeof(bool), typeof(CompactMainView), new PropertyMetadata(false));

    public ActionBrowserViewModel? SelectedBrowser => (ActionBrowserViewModel?)GetValue(SelectedBrowserProperty);
    public IEnumerable? VisibleItems => (IEnumerable?)GetValue(VisibleItemsProperty);
    public bool IsHistorySection => (bool)GetValue(IsHistorySectionProperty);
    public bool IsActiveSection => (bool)GetValue(IsActiveSectionProperty);
    public bool IsSidebarExpanded => (bool)GetValue(IsSidebarExpandedProperty);
    internal MainWindow? OwnerWindow { get; set; }
    private MainViewModel? _model;
    private ContextMenu? _openMenu;

    public CompactMainView()
    {
        InitializeComponent();
        var assembly = typeof(MainWindow).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? assembly.GetName().Version?.ToString(3);
        VersionLabel.Text = "ADB " + version;
        DataContextChanged += (_, args) =>
        {
            if (_model is not null) PropertyChangedEventManager.RemoveHandler(_model, ModelChanged, string.Empty);
            _model = args.NewValue as MainViewModel;
            if (_model is not null) PropertyChangedEventManager.AddHandler(_model, ModelChanged, string.Empty);
            UpdateSection();
        };
        SizeChanged += (_, _) =>
        {
            BrandName.Text = ActualWidth >= 720 ? "ANOMALY DONATION BRIDGE" : "ADB";
            UpdateSidebar();
        };
        IsVisibleChanged += (_, _) => { if (!IsVisible) CloseTransientViews(); };
    }

    internal void SetApplicationLogo(ImageSource logo) => SmallLogo.Source = logo;
    private void UpdateSidebar() => SetValue(IsSidebarExpandedProperty, ActualWidth >= 760);
    internal void FocusSearch()
    {
        UpdateLayout();
        SmallSearchInput.Focus();
    }

    internal void CloseTransientViews()
    {
        StatusPopup.IsOpen = false;
        if (_openMenu is not null) _openMenu.IsOpen = false;
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedSectionIndex)) UpdateSection();
    }

    private void UpdateSection()
    {
        if (_model is null) return;
        var section = _model.SelectedSectionIndex;
        var browser = section switch { 1 => _model.UserPresetBrowser, 2 => _model.PresetBrowser, _ => _model.ActiveBrowser };
        SetValue(SelectedBrowserProperty, browser);
        SetValue(VisibleItemsProperty, section == 3 ? Array.Empty<object>() :
            section == 0 ? browser.VisibleEntries : browser.VisibleActions);
        SetValue(IsHistorySectionProperty, section == 3);
        SetValue(IsActiveSectionProperty, section == 0);
        var tabs = new[] { ActiveTab, UserTab, TemplatesTab, HistoryTab };
        for (var index = 0; index < tabs.Length; index++) tabs[index].IsChecked = index == section;
        ActionScroll.ScrollToTop();
    }

    private void Section_Click(object sender, RoutedEventArgs e)
    {
        if (_model is not null && sender is FrameworkElement { Tag: string text } && int.TryParse(text, out var section))
            _model.SelectedSectionIndex = section;
        UpdateSection();
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        if (_model?.ShowTemplateSearch != true) return;
        FocusSearch();
    }
    private void ClearSearch_Click(object sender, RoutedEventArgs e) { _model?.ClearTemplateSearch(); FocusSearch(); }
    private void Categories_SelectionChanged(object sender, SelectionChangedEventArgs e) => ActionScroll?.ScrollToTop();
    private void ConfigureAction_Click(object sender, RoutedEventArgs e) => OwnerWindow?.ConfigureCompactAction(sender, e);
    private void Theme_Click(object sender, RoutedEventArgs e) => OwnerWindow?.OpenCompactTheme(sender, e);
    private void Profiles_Click(object sender, RoutedEventArgs e) => OwnerWindow?.OpenCompactProfiles(sender, e);
    private void Settings_Click(object sender, RoutedEventArgs e) => OwnerWindow?.OpenCompactSettings(sender, e);
    private void Voting_Click(object sender, RoutedEventArgs e) => OwnerWindow?.OpenCompactVoting(sender, e);
    private void StrictSequence_Click(object sender, RoutedEventArgs e) => OwnerWindow?.SetCompactStrictSequence(sender, e);
    private void GroupDelay_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => OwnerWindow?.SaveCompactGroupDelay(sender, e);
    private void GroupDelay_KeyDown(object sender, KeyEventArgs e) => OwnerWindow?.HandleCompactGroupDelayKey(sender, e);
    private void Status_Click(object sender, RoutedEventArgs e)
    {
        StatusPopup.PlacementTarget = (UIElement)sender;
        StatusPopup.IsOpen = !StatusPopup.IsOpen;
    }

    private void ExpandGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ActivePriceGroupViewModel group }) group.IsExpanded = !group.IsExpanded;
    }

    private void ActionRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border { DataContext: ActionViewModel { IsSequenceStep: true } } row ||
            (bool)row.GetValue(SequenceWiredProperty) || OwnerWindow is null) return;
        if (FindNamed(row, "OrdinalHandle") is not FrameworkElement handle) return;
        OwnerWindow.AttachCompactSequenceHandlers(row, handle);
        row.SetValue(SequenceWiredProperty, true);
    }

    private static FrameworkElement? FindNamed(DependencyObject parent, string name)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is FrameworkElement element && element.Name == name) return element;
            if (FindNamed(child, name) is { } found) return found;
        }
        return null;
    }

    private void ShowMenu(FrameworkElement target, ContextMenu menu, PlacementMode placement = PlacementMode.Bottom)
    {
        if (_openMenu is not null) _openMenu.IsOpen = false;
        _openMenu = menu;
        menu.PlacementTarget = target;
        menu.Placement = placement;
        menu.VerticalOffset = placement == PlacementMode.MousePoint ? 0 : 4;
        menu.Closed += (_, _) => { if (ReferenceEquals(_openMenu, menu)) _openMenu = null; };
        menu.IsOpen = true;
    }

    private static MenuItem CommandItem(string label, ICommand command, object? parameter = null) =>
        new() { Header = label, Command = command, CommandParameter = parameter };

    private static MenuItem DeleteItem(string label, ICommand command, object parameter)
    {
        var item = CommandItem(label, command, parameter);
        item.SetResourceReference(StyleProperty, "DangerMenuItemStyle");
        return item;
    }

    private void ViewMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null || sender is not Button button) return;
        var menu = new ContextMenu();
        var description = new MenuItem { Header = "Описание и параметры", IsCheckable = true, IsChecked = _model.ShowTemplateDescriptions };
        description.Click += (_, _) => _model.ShowTemplateDescriptions = description.IsChecked;
        menu.Items.Add(description);
        ShowMenu(button, menu);
    }

    private void ListMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null || sender is not Button button) return;
        var menu = new ContextMenu();
        menu.Items.Add(CommandItem("Экспорт для стрима…", _model.ExportActivePresetsCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(CommandItem("По возрастанию цены", _model.SortActiveByPriceCommand, "ascending"));
        menu.Items.Add(CommandItem("По убыванию цены", _model.SortActiveByPriceCommand, "descending"));
        ShowMenu(button, menu);
    }

    private static IReadOnlyList<TriggerRuleViewModel> Prices(object? context) => context switch
    {
        ActionViewModel action => action.Triggers,
        ActivePriceGroupViewModel group => group.SharedTriggers,
        _ => Array.Empty<TriggerRuleViewModel>()
    };

    private void PrimaryPrice_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null || sender is not Button button) return;
        if (button.DataContext is TriggerRuleViewModel trigger)
            _model.EditTriggerCommand.Execute(trigger);
        else ShowPrices(button, false);
    }
    private void ExtraPrices_Click(object sender, RoutedEventArgs e) { if (sender is Button button) ShowPrices(button, true); }

    private void ShowPrices(Button button, bool extraOnly)
    {
        if (_model is not null) ShowMenu(button, BuildPriceMenu(button.DataContext, extraOnly));
    }

    internal ContextMenu BuildPriceMenu(object? context, bool extraOnly)
    {
        var menu = new ContextMenu();
        if (_model is null) return menu;
        foreach (var trigger in Prices(context).Skip(extraOnly ? VisiblePriceCount : 0))
        {
            var label = trigger.Model.Enabled ? trigger.Label : "Отключено: " + trigger.Label;
            menu.Items.Add(context is ActivePriceGroupViewModel ?
                new MenuItem { Header = label, IsEnabled = false } : CommandItem(label, _model.EditTriggerCommand, trigger));
        }
        return menu;
    }

    internal ContextMenu BuildActionMenu(ActionViewModel action)
    {
        var menu = new ContextMenu();
        if (_model is null) return menu;
        if (action.IsBuiltInPreset)
            menu.Items.Add(CommandItem("В пользовательские", _model.SaveAsUserPresetCommand, action));
        else
        {
            menu.Items.Add(CommandItem("+ Сумма", _model.AddTriggerCommand, action));
            if (action.IsUserPreset) menu.Items.Add(CommandItem("В активные", _model.AddFromPresetCommand, action));
            else menu.Items.Add(CommandItem("Горячая клавиша", _model.AddPresetHotkeyCommand, action));
        }
        menu.Items.Add(new Separator());
        var donor = CommandItem("Запустить от имени донатера", _model.RunAsDonorCommand, action);
        donor.SetResourceReference(StyleProperty, "SuccessMenuItemStyle");
        menu.Items.Add(donor);
        var voting = new MenuItem { Header = "Добавить в голосование", CommandParameter = action };
        voting.Items.Add(new MenuItem { Header = "Выберите голосование", IsEnabled = false });
        voting.SubmenuOpened += (sender, args) => OwnerWindow?.PrepareCompactVotingTargets(sender, args);
        menu.Items.Add(voting);
        menu.Items.Add(new Separator());
        if (action.IsUserPreset)
        {
            menu.Items.Add(CommandItem("Переименовать", _model.RenameUserPresetCommand, action));
            menu.Items.Add(CommandItem("Сохранить копию", _model.SaveAsUserPresetCommand, action));
        }
        else if (!action.IsPreset)
            menu.Items.Add(CommandItem("Сохранить как пользовательский шаблон", _model.SaveAsUserPresetCommand, action));
        menu.Items.Add(CommandItem("Открыть код", _model.CodeCommand, action));
        if (action.IsUserPreset)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(DeleteItem("Удалить пользовательский шаблон", _model.DeleteUserPresetCommand, action));
        }
        else if (!action.IsPreset)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(DeleteItem("Удалить из активных", _model.DeleteActiveCommand, action));
        }
        return menu;
    }
    internal void OpenTemplateMenu(FrameworkElement target, ContextMenu menu, bool atPointer) =>
        ShowMenu(target, menu, atPointer ? PlacementMode.MousePoint : PlacementMode.Bottom);
    private void ActionMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ActionViewModel action } button) ShowMenu(button, BuildActionMenu(action));
    }

    private void GroupMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ActivePriceGroupViewModel group } button) ShowMenu(button, BuildGroupMenu(group));
    }

    internal ContextMenu BuildGroupMenu(ActivePriceGroupViewModel group)
    {
        var menu = new ContextMenu();
        if (_model is null) return menu;
        menu.Items.Add(CommandItem("+ Сумма для всей группы", _model.AddPriceGroupTriggerCommand, group));
        menu.Items.Add(CommandItem("Переименовать группу", _model.RenamePriceGroupCommand, group));
        return menu;
    }

}

public sealed class CompactExtraPricesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int count && count > CompactMainView.VisiblePriceCount ? $"ещё {count - CompactMainView.VisiblePriceCount}" : string.Empty;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class CompactVisiblePricesConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.FirstOrDefault() is IEnumerable<TriggerRuleViewModel> prices ? prices.Take(CompactMainView.VisiblePriceCount).ToArray() : Array.Empty<TriggerRuleViewModel>();
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
