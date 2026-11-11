using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Core;
using Microsoft.Win32;

namespace AnomalyDonationBridge.App;

public partial class MainWindow : Window
{
    private void TemplateSearch_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.ShowTemplateSearch) return;
        if (IsCompactLayout) { CompactShell.FocusSearch(); return; }
        MainSections.UpdateLayout();
        if (MainSections.Template.FindName("TemplateSearchTextBox", MainSections) is TextBox input) input.Focus();
    }

    private void ClearTemplateSearch_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ClearTemplateSearch();
        TemplateSearch_Click(sender, e);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ThemePopup.IsOpen) return;
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && _viewModel.ShowTemplateSearch)
        {
            TemplateSearch_Click(sender, e); e.Handled = true;
        }
        else if (e.Key == Key.Escape && _viewModel.ShowTemplateSearch && _viewModel.HasTemplateSearchText)
        {
            ClearTemplateSearch_Click(sender, e); e.Handled = true;
        }
    }

    private void GameKey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: ParameterValueViewModel parameter }) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.RightCtrl && Keyboard.Modifiers == ModifierKeys.Control &&
            !Keyboard.IsKeyDown(Key.LeftCtrl) && parameter.Definition.Validate("RCTRL") is null)
        {
            parameter.Value = "RCTRL";
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        var name = key.ToString().ToUpperInvariant();
        name = key switch
        {
            >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(),
            Key.PageUp => "PRIOR", Key.PageDown => "NEXT", Key.Enter => "RETURN",
            Key.OemMinus => "MINUS", Key.OemPlus => "EQUALS", Key.OemOpenBrackets => "LBRACKET",
            Key.OemCloseBrackets => "RBRACKET", Key.OemSemicolon => "SEMICOLON",
            Key.OemQuotes => "APOSTROPHE", Key.OemTilde => "GRAVE", Key.OemBackslash => "BACKSLASH",
            Key.OemPipe => "BACKSLASH", Key.OemComma => "COMMA", Key.OemPeriod => "PERIOD",
            Key.OemQuestion => "SLASH", _ => name
        };
        if (GameKeyboardKeys.IsValid(name, parameter.AllowsMouseKey)) parameter.Value = name;
    }

    private void GameKey_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox { DataContext: ParameterValueViewModel { AllowsMouseKey: true } parameter } box) return;
        e.Handled = true;
        // First click focuses the field; a following click captures a button.
        if (!box.IsKeyboardFocusWithin) { box.Focus(); return; }
        var name = e.ChangedButton switch
        {
            MouseButton.Left => "MOUSE_1", MouseButton.Right => "MOUSE_2", MouseButton.Middle => "MOUSE_3",
            MouseButton.XButton1 => "MOUSE_4", MouseButton.XButton2 => "MOUSE_5", _ => string.Empty
        };
        if (GameKeyboardKeys.IsValid(name, true)) parameter.Value = name;
    }

    private readonly MainViewModel _viewModel;
    private readonly BridgeRuntime _runtime;
    private readonly VotingObsController _votingObs;
    private VotingWindow? _votingWindow;

    public MainWindow(MainViewModel viewModel, BridgeRuntime runtime)
    {
        InitializeComponent();
        using (var icon = Application.GetResourceStream(new Uri("/AnomalyDonationBridge;component/Assets/AnomalyDonationBridge.ico", UriKind.Relative))!.Stream)
        {
            var decoder = BitmapDecoder.Create(icon, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            ApplicationLogo.Source = decoder.Frames.MaxBy(frame => frame.PixelWidth);
        }
        DarkSystemFrame.Attach(this);
        _viewModel = viewModel;
        _runtime = runtime;
        _votingObs = new VotingObsController(runtime);
        Closed += (_, _) => _votingObs.Dispose();
        DataContext = viewModel;
        viewModel.ShowTriggerEditor = ShowTriggerEditor;
        viewModel.PromptForPresetName = PromptForPresetName;
        viewModel.PromptForDonorLaunch = action =>
        {
            var dialog = new DonorLaunchWindow(action.DisplayName) { Owner = this };
            return dialog.ShowDialog() == true ? dialog.Result : null;
        };
        viewModel.SelectActiveExportPath = SelectActiveExportPath;
        viewModel.ShowSettings = ShowSettingsAsync;
        viewModel.ShowPresetHotkeySettings = ShowPresetHotkeySettingsAsync;
        viewModel.SelectCatalogExclusions = SelectCatalogExclusions;
        InitializeResponsiveLayout();
    }

    private string? SelectActiveExportPath()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Экспорт активных шаблонов для стрима",
            FileName = "donation-presets.txt",
            DefaultExt = ".txt",
            Filter = "Текстовый файл (*.txt)|*.txt|Все файлы (*.*)|*.*",
            AddExtension = true,
            OverwritePrompt = true
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private string? PromptForPresetName(string title, string initialValue)
    {
        var dialog = new PresetNameWindow(title, initialValue) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.PresetName : null;
    }

    private IReadOnlyList<CatalogEntry>? SelectCatalogExclusions(IReadOnlyList<CatalogEntry> candidates)
    {
        var dialog = new CatalogExclusionPickerWindow(candidates) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.SelectedEntries : null;
    }

    private TriggerEditorResult? ShowTriggerEditor(ActionViewModel action, TriggerRuleViewModel? existing)
    {
        var viewModel = new TriggerEditorViewModel(existing?.Model, _runtime.Settings.ObservedCurrencies,
            calculationCurrencies: _runtime.UiPreferences.AutomaticPriceCurrencies,
            saveCalculationCurrencies: currencies => _runtime.UpdateUiPreferencesAsync(current => current with
            {
                AutomaticPriceCurrencies = currencies.ToList()
            }));
        var dialog = new TriggerEditorWindow(viewModel) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private void EditableTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox textBox) textBox.SelectAll();
    }

    private void EditableTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.IsKeyboardFocusWithin) return;
        e.Handled = true;
        textBox.Focus();
    }

    private void PriceGroupHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ActivePriceGroupViewModel group } header) return;
        if (e.OriginalSource is DependencyObject source && IsInsideButton(source, header)) return;
        group.IsExpanded = !group.IsExpanded;
        header.Focus();
        e.Handled = true;
    }

    private void PriceGroupHeader_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space) ||
            sender is not FrameworkElement { DataContext: ActivePriceGroupViewModel group }) return;
        group.IsExpanded = !group.IsExpanded;
        e.Handled = true;
    }

    private static bool IsInsideButton(DependencyObject source, DependencyObject boundary)
    {
        for (DependencyObject? current = source; current is not null && !ReferenceEquals(current, boundary);)
        {
            if (current is ButtonBase) return true;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    private async void Settings_Click(object sender, RoutedEventArgs e) => await _viewModel.OpenSettingsAsync();

    private async void Profiles_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_votingWindow is not null) await _votingWindow.FlushPendingChangesAsync();
            var dialog = new ProfilesWindow(_runtime) { Owner = this };
            dialog.ShowDialog();
        }
        catch (Exception error) { AppDialogs.Info(this, "Профили", error.Message); }
    }

    private void Voting_Click(object sender, RoutedEventArgs e) => _ = ShowVotingAsync(null);

    private async Task ShowVotingAsync(ActionDefinition? action, string? collectionId = null)
    {
        if (_votingWindow is null)
        {
            _votingWindow = new VotingWindow(_runtime, _votingObs) { Owner = this };
            _votingWindow.Closed += (_, _) => _votingWindow = null;
        }
        _votingWindow.Show(); _votingWindow.Activate();
        if (action is not null && collectionId is not null) await _votingWindow.AddTemplateAsync(action, collectionId);
    }

    private void VotingTargets_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender) || sender is not MenuItem { CommandParameter: ActionViewModel action } menu) return;
        PopulateVotingTargets(menu, _runtime.Voting.CollectionsFor(_runtime.Settings.ActiveProfileId), id => ShowVotingAsync(action.Model, id));
    }

    internal static void PopulateVotingTargets(MenuItem menu, IReadOnlyList<VotingRound> rounds, Func<string, Task> select)
    {
        // Keep HasItems true while an already-open submenu is refreshed.
        var previousCount = menu.Items.Count;
        foreach (var round in rounds)
        {
            var id = round.CollectionId;
            var item = new MenuItem { Header = new TextBlock { Text = round.Title, MaxWidth = 420, TextWrapping = TextWrapping.Wrap }, Tag = id };
            item.Click += async (_, e) => { e.Handled = true; await select(id); };
            menu.Items.Add(item);
        }
        if (rounds.Count == 0) menu.Items.Add(new MenuItem { Header = "Сначала создайте голосование", IsEnabled = false });
        for (var index = 0; index < previousCount; index++) menu.Items.RemoveAt(0);
    }

    private void CardMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button) OpenWideTemplateMenu(button, false);
    }

    private void ConfigureAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ActionViewModel action }) return;
        var dialog = new ActionEditorWindow(_viewModel.CreateActionEditor(action), (DataTemplate)Resources["ActionEditorTemplate"])
        {
            Owner = this
        };
        // Modal ownership blocks outside clicks; only Cancel, Save or the window close button can dismiss it.
        dialog.ShowDialog();
    }

    private void CategoryTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox { DataContext: ActionBrowserViewModel browser }) return;
        var list = ReferenceEquals(PresetsList?.ItemsSource, browser.VisibleActions) ? PresetsList :
            ReferenceEquals(UserPresetsList?.ItemsSource, browser.VisibleActions) ? UserPresetsList :
            ReferenceEquals(ActiveActionsList?.ItemsSource, browser.VisibleActions) ? ActiveActionsList : null;
        for (DependencyObject? parent = list; parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not ScrollViewer scroll) continue;
            scroll.ScrollToTop();
            break;
        }
    }

    private void ThemePicker_Click(object sender, RoutedEventArgs e)
    {
        ThemePopupError.Visibility = Visibility.Collapsed;
        ThemePopup.PlacementTarget = sender as UIElement ?? ThemeButton;
        ThemePopup.IsOpen = true;
    }

    private void ThemePopup_Opened(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke((Action)(() =>
        {
            var selected = new[] { ThemeRedChoice, ThemeGreenChoice, ThemeBlueChoice, ThemeYellowChoice }
                .FirstOrDefault(choice => choice.IsChecked == true);
            selected?.Focus();
        }), System.Windows.Threading.DispatcherPriority.Input);

    private async void ThemeColor_Checked(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && sender is RadioButton { Tag: string color } && color != _viewModel.ThemeColor)
            await ApplyThemeChoiceAsync(() => _viewModel.SetThemeColorAsync(color));
    }

    private async void ThemeBackground_Checked(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && sender is RadioButton { Tag: string background } &&
            (background == "white") != _viewModel.IsLightTheme)
            await ApplyThemeChoiceAsync(() => _viewModel.SetLightThemeAsync(background == "white"));
    }

    private async Task ApplyThemeChoiceAsync(Func<Task> apply)
    {
        ThemeChoices.IsEnabled = false;
        ThemePopupError.Visibility = Visibility.Collapsed;
        try
        {
            await apply();
        }
        catch (Exception exception)
        {
            ThemePopupError.Text = "Не удалось сохранить тему: " + exception.Message;
            ThemePopupError.Visibility = Visibility.Visible;
        }
        finally { ThemeChoices.IsEnabled = true; }
    }

    private void CloseThemePopup_Click(object sender, RoutedEventArgs e)
    {
        ThemePopup.IsOpen = false;
        ThemePopup.PlacementTarget?.Focus();
    }

    private void ThemePopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ThemePopup.IsOpen = false;
            ThemePopup.PlacementTarget?.Focus();
            e.Handled = true;
        }
    }

    private async Task ShowSettingsAsync()
    {
        var dialog = new SettingsWindow(new SettingsViewModel(_runtime)) { Owner = this };
        dialog.ShowDialog();
        await Task.CompletedTask;
    }

    private async Task ShowPresetHotkeySettingsAsync(int presetNumber)
    {
        var settings = new SettingsViewModel(_runtime);
        settings.AddActivePresetHotkey(presetNumber);
        var dialog = new SettingsWindow(settings, openHotkeys: true) { Owner = this };
        dialog.ShowDialog();
        await Task.CompletedTask;
    }
}
