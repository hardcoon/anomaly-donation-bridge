using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.App;

public sealed class VotingOptionRow : ObservableObject
{
    public VotingOption Model { get; }
    public VotingOptionRow(VotingOption model) => Model = model.Clone();
    public bool Enabled { get => Model.Enabled; set { Model.Enabled = value; RaisePropertyChanged(); } }
    public string Command { get => Model.Command; set { Model.Command = value; RaisePropertyChanged(); } }
    public string BindingTitle => Model.Preset?.DisplayName ?? "Выбрать из активных";
    public bool HasPreset => Model.Preset is not null;
    public string Statistics { get; private set; } = string.Empty;
    private bool _canEdit;
    public bool CanEdit { get => _canEdit; set => SetProperty(ref _canEdit, value); }
    public void Bind(ActionDefinition action) { Model.Preset = action.Clone(); RaisePropertyChanged(nameof(BindingTitle)); RaisePropertyChanged(nameof(HasPreset)); }
    public void Update(decimal amount, decimal total, string currency)
    {
        Model.Amount = amount;
        Statistics = VotingWindow.FormatMoney(amount, currency) + " · " + (total > 0 ? amount / total * 100 : 0).ToString("0.#") + "%";
        RaisePropertyChanged(nameof(Statistics));
    }
}

public sealed class VotingWindow : Window
{
    private readonly BridgeRuntime _runtime;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _autosaveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private long _editRevision, _draftSession;
    private bool _autosaving, _closed;
    private readonly ObservableCollection<VotingOptionRow> _rows = [];
    private readonly TextBlock _title = new() { FontSize = 23, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 480, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _automationStatus = new() { FontSize = 16, FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) };
    private readonly CheckBox _money = new() { Content = "Сумма голосования" }, _time = new() { Content = "Время голосования, мин" };
    private readonly TextBox _target = new(), _minutes = new();
    private readonly CheckBox _donationVotes = new() { Content = "Описания доната", Margin = new Thickness(0, 0, 18, 0) };
    private readonly CheckBox _youTubeVotes = new() { Content = "Чата YouTube" };
    private readonly TextBox _youTubeWeight = new() { Width = 100, Height = 36, MaxLength = 16 };
    private readonly TextBlock _youTubeCurrency = new() { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _youTubeChange = new();
    private readonly TextBlock _youTubeChangeLabel = new() { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _currency = new() { Width = 130, DisplayMemberPath = "Label", SelectedValuePath = "Code" };
    private readonly Button _condition = new() { MinWidth = 48, Height = 36 };
    private readonly TextBlock _total = new(), _remaining = new(), _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _autosaveStatus = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 14, 0, 0) };
    private readonly ProgressBar _progress = new() { Height = 9, Maximum = 100, Minimum = 0 };
    private readonly Button _spin = new() { Content = "Крутить", MinWidth = 100 };
    private readonly TextBox _testAmount = new() { Text = "100", Width = 80 }, _testMessage = new() { Text = "!команда", MaxLength = 4096 };
    private readonly Grid _settings = new();
    private readonly Button _add = new() { Content = "+ Добавить команду", MinWidth = 160, Height = 36 };
    private readonly Button _test = new() { Content = "Внести", Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _reset = new() { Content = "Сбросить прогресс", Height = 36, Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _rename = new() { Content = "✎" };
    private readonly Button _delete = new() { Content = "\uE74D", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16,
        Width = 36, Height = 36, Padding = new Thickness(0), Margin = new Thickness(8, 0, 0, 0), ToolTip = "Удалить это голосование" };
    private readonly Button _obs = new() { Content = "Вывести окно OBS", Height = 36 };
    private readonly Button _panelToggle = new() { Content = "\uE700", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16,
        Width = 36, Height = 36, Padding = new Thickness(0), Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _compactSummary = new() { FontSize = 18, FontWeight = FontWeights.Medium,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
    private readonly StackPanel _settingsActions = new() { Margin = new Thickness(0, 16, 0, 0) };
    private readonly StackPanel _compactActions = new() { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 0, 0) };
    private readonly StackPanel _compactFeedback = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _compactStatus = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
    private ColumnDefinition _settingsColumn = null!, _settingsGap = null!;
    private bool _settingsCollapsed;
    private readonly Grid _headerActions = new() { Margin = new Thickness(16, 0, 0, 0) };
    private FrameworkElement? _settingsCard;
    private readonly Button _collectionToggle = new() { MinWidth = 118, Height = 36 };
    private readonly TextBlock _collectionToggleLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _statusDot = new() { Text = "●", FontSize = 12, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _winnerAuto = new() { Content = new TextBlock { Text = "Автоматически активировать эффект победителя", TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 14, 0, 0) };
    private readonly Grid _commandsPanel = new();
    private readonly CheckBox _automaticDraw = new() { Content = new TextBlock { Text = "Автоматический запуск рулетки по окончании голосования", TextWrapping = TextWrapping.Wrap } };
    private readonly ComboBox _order = new() { MinWidth = 200, ItemsSource = new[] { "По порядку", "Случайно" } };
    private readonly CheckBox _next = new() { Content = new TextBlock { Text = "Автоматически начинать следующее голосование", TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 14, 0, 20) };
    private readonly CheckBox _gameHud = new() { Content = "Показывать прогресс голосования в игре", Margin = new Thickness(0, 12, 0, 12) };
    private readonly Button _gameHudEdit = new() { Content = "Редактировать положение", Height = 36, MinWidth = 190 };
    private readonly ComboBox _gameHudFormat = new() { MinWidth = 170, ItemsSource = new[] { "Проценты", "Сумма", "Таймер" } };
    private readonly CheckBox _gameHudCommands = new() { Content = "Выводить список команд", Margin = new Thickness(0, 12, 0, 14) };
    private readonly CheckBox _gameHudTemplateNames = new() { Content = "Выводить название шаблонов", Margin = new Thickness(0, 10, 0, 0),
        ToolTip = "Показывать название шаблона под каждой командой в игровом списке голосования" };
    private readonly StackPanel _sidebar = new(), _sequence = new();
    private FrameworkElement _generalPanel = null!, _collectionPanel = null!;
    private bool _generalSelected = true;
    private string _selectedId;
    private string _profileId;
    private string _sidebarSignature = string.Empty;
    private bool _sequenceDragging;
    private Border? _dragCard;
    private Point _dragOrigin;
    private int _dragInsertion;
    private SequenceDragAdorner? _dragAdorner;
    private AdornerLayer? _dragLayer;
    internal const string ResetConfirmation = "Сбросить накопленные суммы этого голосования и отменить ожидающий эффект победителя?\n\nКоманды, выбранные шаблоны, настройки программы и оставшееся время не изменятся. Уже отправленные эффекты не отменяются.";
    internal static string DeleteConfirmation(VotingRound round) => "Удалить голосование «" + round.Title + "»?\n\nЕго команды, настройки и накопленные суммы будут удалены. Ожидающий эффект победителя будет отменён. Другие голосования и настройки программы не изменятся. Уже отправленные эффекты не отменяются.";
    private VotingCondition _conditionValue;
    private bool _dirty, _loading, _busy, _spinning, _closing;
    internal ObservableCollection<VotingOptionRow> Rows => _rows;

    public VotingWindow(BridgeRuntime runtime, VotingObsController obs)
    {
        _runtime = runtime;
        _settingsCollapsed = runtime.UiPreferences.VotingSettingsPanelCollapsed;
        _profileId = runtime.Settings.ActiveProfileId;
        _selectedId = runtime.Voting.CollectionsFor(_profileId).FirstOrDefault(round => round.CollectionId == runtime.Voting.Snapshot.CollectionId)?.CollectionId
            ?? runtime.Voting.CollectionsFor(_profileId).FirstOrDefault()?.CollectionId ?? string.Empty;
        Title = "Голосование — Anomaly Donation Bridge"; Width = 1350; Height = 920; MinWidth = 1120; MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        SetResourceReference(BackgroundProperty, "BackgroundBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        DarkSystemFrame.Attach(this);
        Content = Build();
        foreach (var box in new[] { _target, _minutes, _youTubeWeight }) box.TextChanged += (_, _) => Dirty();
        foreach (var check in new[] { _donationVotes, _youTubeVotes, _youTubeChange })
        { check.Checked += (_, _) => Dirty(); check.Unchecked += (_, _) => Dirty(); }
        _currency.SelectionChanged += (_, _) => Dirty();
        _money.Checked += (_, _) => Dirty(); _money.Unchecked += (_, _) => Dirty();
        _time.Checked += (_, _) => Dirty(); _time.Unchecked += (_, _) => Dirty();
        _condition.Click += (_, _) => { _conditionValue = _conditionValue == VotingCondition.All ? VotingCondition.Any : VotingCondition.All; Dirty(); UpdateDisplay(); };
        _spin.Click += async (_, _) => await RunAsync(SpinAsync);
        _add.Click += (_, _) => AddRow(new VotingOption());
        _test.Click += async (_, _) => await RunAsync(TestContributionAsync);
        _reset.Click += async (_, _) => await RunAsync(async () =>
        {
            if (!VotingConfirmationWindow.Confirm(this, "Сбросить прогресс", ResetConfirmation, "Сбросить")) return;
            await ResetSelectedAsync();
        });
        _delete.Click += async (_, _) => await RunAsync(async () =>
        {
            if (VotingConfirmationWindow.Confirm(this, "Удалить голосование", DeleteConfirmation(SelectedRound), "Удалить"))
                await DeleteSelectedAsync();
        });
        _obs.Click += (_, _) => obs.Show();
        _obs.ToolTip = "Одно окно OBS для текущего активного голосования";
        System.Windows.Automation.AutomationProperties.SetName(_delete, "Удалить это голосование");
        _panelToggle.Click += async (_, _) =>
        {
            var collapsed = _settingsCollapsed = !_settingsCollapsed;
            UpdateDisplay();
            try { await _runtime.UpdateUiPreferencesAsync(current => current with { VotingSettingsPanelCollapsed = collapsed }); }
            catch (Exception error) { _message.Text = "Не удалось сохранить вид окна: " + error.Message; }
        };
        _collectionToggle.Click += async (_, _) => await RunAsync(async () =>
        {
            await SaveSettingsAsync();
            await _runtime.Voting.SetCollectionActiveAsync(_selectedId, !SelectedRound.IsActive, _runtime.Settings.ActiveProfileId);
        });
        _automaticDraw.Checked += (_, _) => Dirty(); _automaticDraw.Unchecked += (_, _) => Dirty();
        _order.SelectionChanged += (_, _) => Dirty();
        _next.Checked += (_, _) => Dirty(); _next.Unchecked += (_, _) => Dirty();
        _winnerAuto.Checked += (_, _) => Dirty(); _winnerAuto.Unchecked += (_, _) => Dirty();
        _gameHud.Checked += (_, _) => Dirty(); _gameHud.Unchecked += (_, _) => Dirty();
        _gameHudCommands.Checked += (_, _) => Dirty(); _gameHudCommands.Unchecked += (_, _) => Dirty();
        _gameHudTemplateNames.Checked += (_, _) => Dirty(); _gameHudTemplateNames.Unchecked += (_, _) => Dirty();
        _gameHudEdit.Click += async (_, _) => await RunAsync(EditHudPositionAsync);
        _gameHudFormat.SelectionChanged += (_, _) => Dirty();
        _rows.CollectionChanged += (_, _) => Dirty();
        _autosaveTimer.Tick += async (_, _) => await AutosaveAsync();
        _runtime.Voting.Changed += VotingChanged;
        _runtime.SettingsChanged += ProfileChanged;
        _timer.Tick += (_, _) => UpdateDisplay();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && _dragCard is not null) { CancelSequenceDrag(); e.Handled = true; } };
        Closed += (_, _) =>
        {
            _closed = true; CancelSequenceDrag(); _timer.Stop(); _autosaveTimer.Stop(); _runtime.Voting.Changed -= VotingChanged;
            _runtime.SettingsChanged -= ProfileChanged;
        };
        Closing += ClosingAsync;
        LoadDraft(); _timer.Start();
    }

    private UIElement Build()
    {
        var shell = new Grid { Margin = new Thickness(18) };
        shell.SetResourceReference(Panel.BackgroundProperty, "BackgroundBrush");
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        shell.ColumnDefinitions.Add(new ColumnDefinition());
        var navigation = new Grid(); navigation.RowDefinitions.Add(new RowDefinition()); navigation.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        navigation.Children.Add(new ScrollViewer { Content = _sidebar, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var addCollection = MakeButton("+ Добавить", async (_, _) => await RunAsync(async () =>
        { await SaveSettingsAsync(); _selectedId = await _runtime.Voting.AddCollectionAsync(sourceCollectionId: _selectedId, profileId: _profileId); _generalSelected = false; LoadDraft(); }));
        addCollection.ToolTip = "Добавить голосование";
        System.Windows.Automation.AutomationProperties.SetName(addCollection, "Добавить голосование");
        Grid.SetRow(addCollection, 1); navigation.Children.Add(addCollection);
        var sideBorder = new Border { Child = navigation, BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(0, 0, 12, 0) };
        sideBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); shell.Children.Add(sideBorder);
        var root = new Grid(); Grid.SetColumn(root, 2); shell.Children.Add(root);
        root.SetResourceReference(Panel.BackgroundProperty, "BackgroundBrush");
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = height });
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 15) };
        var headerActions = _headerActions;
        headerActions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); headerActions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); headerActions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerActions.Children.Add(_reset); Grid.SetColumn(_delete, 1); headerActions.Children.Add(_delete); Grid.SetColumn(_panelToggle, 2); headerActions.Children.Add(_panelToggle);
        _reset.Margin = new Thickness(0);
        _delete.SetResourceReference(Control.ForegroundProperty, "BadBrush"); DockPanel.SetDock(headerActions, Dock.Right); header.Children.Add(headerActions);
        var rename = _rename; rename.Click += async (_, _) => await RunAsync(async () =>
        {
            if (_generalSelected) return;
            await SaveSettingsAsync();
            var dialog = new PresetNameWindow("Заголовок голосования", SelectedRound.Title) { Owner = this };
            if (dialog.ShowDialog() == true) await _runtime.Voting.RenameAsync(dialog.PresetName, _selectedId);
        });
        rename.ToolTip = "Переименовать заголовок"; rename.Margin = new Thickness(9, 0, 0, 0); rename.Width = rename.Height = 36; rename.Padding = new Thickness(0);
        var statusContent = new StackPanel { Orientation = Orientation.Horizontal }; statusContent.Children.Add(_statusDot); statusContent.Children.Add(_collectionToggleLabel);
        _collectionToggle.Content = statusContent;
        _title.Margin = new Thickness(12, 0, 0, 0);
        var titleRow = new Grid(); titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); titleRow.ColumnDefinitions.Add(new ColumnDefinition());
        titleRow.Children.Add(_collectionToggle); Grid.SetColumn(rename, 1); titleRow.Children.Add(rename); Grid.SetColumn(_title, 2); titleRow.Children.Add(_title);
        header.Children.Add(titleRow); root.Children.Add(header);
        _compactSummary.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        Grid.SetRow(_compactSummary, 1); root.Children.Add(_compactSummary);
        _collectionPanel = BuildCollection(); Grid.SetRow(_collectionPanel, 2); root.Children.Add(_collectionPanel);
        _generalPanel = BuildGeneral(); Grid.SetRow(_generalPanel, 2); root.Children.Add(_generalPanel);
        return shell;
    }

    private FrameworkElement BuildCollection()
    {
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });
        _settingsGap = new ColumnDefinition { Width = new GridLength(24) };
        _settingsColumn = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 380, MaxWidth = 440 };
        columns.ColumnDefinitions.Add(_settingsGap); columns.ColumnDefinitions.Add(_settingsColumn);
        var commands = _commandsPanel; commands.Margin = new Thickness(0, 16, 0, 0);
        commands.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); commands.RowDefinitions.Add(new RowDefinition());
        commands.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); commands.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        commands.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        commands.Children.Add(Heading("Команды голосования"));
        var items = new ItemsControl { ItemsSource = _rows, ItemTemplate = RowTemplate() };
        var scroll = new ScrollViewer { Content = items, Margin = new Thickness(0, 14, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); commands.Children.Add(scroll);
        var adding = new StackPanel { Margin = new Thickness(0, 12, 0, 16) };
        var addRow = new Grid(); addRow.ColumnDefinitions.Add(new ColumnDefinition()); addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _add.HorizontalAlignment = HorizontalAlignment.Left; addRow.Children.Add(_add);
        Grid.SetColumn(_compactActions, 1); addRow.Children.Add(_compactActions); adding.Children.Add(addRow);
        var hint = new TextBlock { Text = "или Шаблон → ⋯ → Добавить в голосование", FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); adding.Children.Add(hint); Grid.SetRow(adding, 2); commands.Children.Add(adding);
        var testing = new StackPanel(); testing.Children.Add(Heading("Тестовый взнос"));
        var testHint = new TextBlock { Text = "Без запуска обычного эффекта доната", FontSize = 12, Margin = new Thickness(0, 5, 0, 12), TextWrapping = TextWrapping.Wrap };
        testHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); testing.Children.Add(testHint);
        var testRow = new Grid(); testRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        testRow.ColumnDefinitions.Add(new ColumnDefinition()); testRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        _testAmount.Width = double.NaN; _testMessage.Margin = new Thickness(8, 0, 8, 0); _test.Margin = new Thickness(0);
        foreach (var input in new[] { _target, _minutes, _testAmount, _testMessage, _youTubeWeight }) { input.Height = 36; input.Padding = new Thickness(8, 3, 8, 3); }
        _test.Height = 36; _test.Padding = new Thickness(10, 6, 10, 6);
        testRow.Children.Add(_testAmount); Grid.SetColumn(_testMessage, 1); testRow.Children.Add(_testMessage); Grid.SetColumn(_test, 2); testRow.Children.Add(_test);
        testing.Children.Add(testRow);
        var testCard = Card(testing, 12); Grid.SetRow(testCard, 3); commands.Children.Add(testCard); columns.Children.Add(commands);
        var compactSaving = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        compactSaving.SetBinding(TextBlock.TextProperty, new Binding(nameof(TextBlock.Text)) { Source = _autosaveStatus });
        compactSaving.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(TextBlock.Foreground)) { Source = _autosaveStatus });
        _compactFeedback.Children.Add(compactSaving);
        var compactMessage = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        compactMessage.SetBinding(TextBlock.TextProperty, new Binding(nameof(TextBlock.Text)) { Source = _message });
        _compactFeedback.Children.Add(compactMessage);
        _compactStatus.SetBinding(TextBlock.TextProperty, new Binding(nameof(TextBlock.Text)) { Source = _status });
        _compactStatus.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); _compactFeedback.Children.Add(_compactStatus);
        Grid.SetRow(_compactFeedback, 4); commands.Children.Add(_compactFeedback);

        _settings.RowDefinitions.Add(new RowDefinition()); _settings.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var settings = new StackPanel(); settings.Children.Add(Heading("Прогресс и настройки"));
        var goals = new Grid { Margin = new Thickness(0, 16, 0, 16) };
        goals.ColumnDefinitions.Add(new ColumnDefinition()); goals.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) }); goals.ColumnDefinitions.Add(new ColumnDefinition());
        goals.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); goals.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _money.Content = "Сумма"; _time.Content = "Время, мин"; _money.MinHeight = _time.MinHeight = 24;
        goals.Children.Add(_money); Grid.SetColumn(_time, 2); goals.Children.Add(_time);
        Grid.SetRow(_target, 1); _target.Margin = new Thickness(0, 8, 0, 0); goals.Children.Add(_target);
        Grid.SetColumn(_condition, 1); Grid.SetRow(_condition, 1); _condition.Margin = new Thickness(8, 8, 8, 0); _condition.Padding = new Thickness(0); goals.Children.Add(_condition);
        Grid.SetColumn(_minutes, 2); Grid.SetRow(_minutes, 1); _minutes.Margin = new Thickness(0, 8, 0, 0); _minutes.ToolTip = "Длительность в минутах"; goals.Children.Add(_minutes); settings.Children.Add(goals);
        var currencyRow = new Grid { Margin = new Thickness(0, 0, 0, 20) }; currencyRow.ColumnDefinitions.Add(new ColumnDefinition()); currencyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        currencyRow.Children.Add(new TextBlock { Text = "Валюта голосования", VerticalAlignment = VerticalAlignment.Center });
        _currency.Width = 148; _currency.Height = 36; Grid.SetColumn(_currency, 1);
        _currency.ItemsSource = new[] { "RUB", "USD", "EUR" }.Concat(_runtime.Settings.ObservedCurrencies).Distinct()
            .Select(code => new { Code = code, Label = code == "RUB" ? "руб. (RUB)" : code }).ToList(); currencyRow.Children.Add(_currency); settings.Children.Add(currencyRow);
        settings.Children.Add(BuildVoteSources());
        _total.FontSize = 18; _total.FontWeight = FontWeights.Medium; _total.TextWrapping = TextWrapping.Wrap;
        _total.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush"); settings.Children.Add(_total);
        _remaining.FontSize = 12; _remaining.Margin = new Thickness(0, 5, 0, 9); _remaining.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); settings.Children.Add(_remaining);
        _progress.Height = 10; settings.Children.Add(_progress);
        _status.Margin = new Thickness(0, 14, 0, 0); _status.FontSize = 13; _status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); settings.Children.Add(_status);
        var explanation = new TextBlock { Text = "Команды учитываются из выбранных источников. Обычные эффекты по сумме доната работают независимо.", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        explanation.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var separator = new Border { Child = explanation, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 14, 0, 0), Margin = new Thickness(0, 20, 0, 0) };
        separator.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); settings.Children.Add(separator);
        settings.Children.Add(_autosaveStatus);
        _message.FontSize = 12; _message.Margin = new Thickness(0, 10, 0, 0); settings.Children.Add(_message);
        _settings.Children.Add(new ScrollViewer { Content = settings, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var actions = _settingsActions;
        _spin.Height = 36; _spin.Style = (Style)FindResource("PrimaryButtonStyle"); actions.Children.Add(_spin);
        Grid.SetRow(actions, 1); _settings.Children.Add(actions);
        var settingsCard = Card(_settings, 16); _settingsCard = settingsCard;
        Grid.SetColumn(settingsCard, 2); columns.Children.Add(settingsCard);
        return columns;
    }

    private FrameworkElement BuildVoteSources()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Обрабатывать голоса из:", FontWeight = FontWeights.Medium, Margin = new Thickness(0, 0, 0, 10) });
        var sources = new WrapPanel(); sources.Children.Add(_donationVotes); sources.Children.Add(_youTubeVotes); panel.Children.Add(sources);
        panel.Children.Add(new TextBlock { Text = "Воспринимать сообщение из чата YouTube как:", TextWrapping = TextWrapping.Wrap,
            FontSize = 12, Margin = new Thickness(0, 14, 0, 7) });
        var weight = new StackPanel { Orientation = Orientation.Horizontal };
        weight.Children.Add(_youTubeWeight); weight.Children.Add(_youTubeCurrency); panel.Children.Add(weight);
        var change = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        change.ColumnDefinitions.Add(new ColumnDefinition()); change.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        change.Children.Add(new TextBlock { Text = "Пользователь YouTube может менять голос", TextWrapping = TextWrapping.Wrap,
            FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        _youTubeChange.Style = (Style)FindResource("SwitchCheckBoxStyle");
        _youTubeChange.ToolTip = "Переносит уже учтённый голос на другую команду, не добавляя вес.";
        System.Windows.Automation.AutomationProperties.SetName(_youTubeChange, "Пользователь YouTube может менять голос");
        var toggle = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        toggle.Children.Add(_youTubeChange); toggle.Children.Add(_youTubeChangeLabel); Grid.SetColumn(toggle, 1); change.Children.Add(toggle); panel.Children.Add(change);
        var block = new Border { Child = panel, BorderThickness = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(0, 16, 0, 16), Margin = new Thickness(0, 0, 0, 20) };
        block.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); return block;
    }

    private static Border Card(UIElement child, double padding)
    {
        var border = new Border { Child = child, Padding = new Thickness(padding), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush"); border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); return border;
    }

    private FrameworkElement BuildGeneral()
    {
        var content = new Grid { Margin = new Thickness(0, 0, 8, 0) };
        for (var row = 0; row < 4; row++) content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(_automationStatus);
        var columns = new Grid(); Grid.SetRow(columns, 1); content.Children.Add(columns);
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        var panel = new StackPanel(); columns.Children.Add(panel);
        panel.Children.Add(_automaticDraw);
        panel.Children.Add(_winnerAuto);
        panel.Children.Add(_next);
        var hud = new StackPanel();
        hud.Children.Add(Heading("Прогресс в игре и OBS"));
        _gameHud.Margin = new Thickness(0, 14, 0, 14); hud.Children.Add(_gameHud);
        var hudFields = new WrapPanel();
        var field = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        field.Children.Add(new TextBlock { Text = "Выводить", Margin = new Thickness(0, 0, 0, 6) }); field.Children.Add(_gameHudFormat);
        hudFields.Children.Add(field);
        _gameHudEdit.VerticalAlignment = VerticalAlignment.Bottom; hudFields.Children.Add(_gameHudEdit);
        hud.Children.Add(hudFields);
        _gameHudCommands.Margin = new Thickness(0, 14, 0, 0); hud.Children.Add(_gameHudCommands);
        hud.Children.Add(_gameHudTemplateNames);
        _obs.Margin = new Thickness(0, 16, 0, 0); _obs.HorizontalAlignment = HorizontalAlignment.Left; hud.Children.Add(_obs);
        var hudCard = Card(hud, 16); hudCard.Tag = "VotingGameHudSettings";
        hudCard.VerticalAlignment = VerticalAlignment.Top; Grid.SetColumn(hudCard, 2); columns.Children.Add(hudCard);
        panel.Children.Add(Heading("Порядок запуска голосований")); _order.Margin = new Thickness(0, 10, 0, 16); panel.Children.Add(_order);
        panel.Children.Add(Heading("Голосования в автоматической очереди"));
        panel.Children.Add(new TextBlock { Text = "Галочки выбирают участников очереди, но не включают их. Одновременно работает только одно голосование. Для изменения порядка потяните за значок ⠿ или название строки.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 12) });
        panel.Children.Add(_sequence);
        var saving = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 14, 0, 0) };
        saving.SetBinding(TextBlock.TextProperty, new Binding(nameof(TextBlock.Text)) { Source = _autosaveStatus });
        saving.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(TextBlock.Foreground)) { Source = _autosaveStatus });
        Grid.SetRow(saving, 2); content.Children.Add(saving);
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        message.SetBinding(TextBlock.TextProperty, new Binding(nameof(TextBlock.Text)) { Source = _message });
        Grid.SetRow(message, 3); content.Children.Add(message);
        return new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private IReadOnlyList<VotingRound> ProfileCollections => _runtime.Voting.CollectionsFor(_profileId);
    private VotingRound SelectedRound
    {
        get
        {
            var round = _runtime.Voting.SnapshotFor(_selectedId);
            return round.CollectionId == _selectedId && round.ProfileId == _profileId
                ? round : new VotingRound { CollectionId = string.Empty, ProfileId = _profileId };
        }
    }

    private void ProfileChanged() => Dispatcher.BeginInvoke(async () =>
    {
        if (_closed || _profileId == _runtime.Settings.ActiveProfileId) return;
        try
        {
            // Finish any in-flight edit against its original collection before
            // replacing the editor with the new profile's independent draft.
            await SaveSettingsAsync();
            _profileId = _runtime.Settings.ActiveProfileId;
            _selectedId = ProfileCollections.FirstOrDefault()?.CollectionId ?? string.Empty;
            _generalSelected = true; CancelSequenceDrag(); LoadDraft();
        }
        catch (Exception error) { _message.Text = error.Message; }
    });

    private async Task SelectPageAsync(string? collectionId)
    {
        await SaveSettingsAsync(); _generalSelected = collectionId is null;
        if (collectionId is not null)
        { _selectedId = collectionId; await _runtime.Voting.SelectCollectionAsync(collectionId, _runtime.Settings.ActiveProfileId); }
        LoadDraft();
    }

    private void UpdateNavigation()
    {
        var rounds = ProfileCollections;
        var activeId = rounds.FirstOrDefault(round => round.IsActive)?.CollectionId ?? string.Empty;
        var signature = string.Join("|", rounds.Select(round => round.CollectionId + ":" + round.Title + ":" + round.UseInSequence)) + activeId + _selectedId + _generalSelected;
        if (_dragCard is not null || _sidebarSignature == signature) return; _sidebarSignature = signature;
        _sidebar.Children.Clear();
        var general = MakeButton("Общие настройки", async (_, _) => await RunAsync(() => SelectPageAsync(null)));
        general.Margin = new Thickness(0, 0, 0, 12); general.HorizontalContentAlignment = HorizontalAlignment.Left;
        if (_generalSelected) general.SetResourceReference(BackgroundProperty, "SelectionSurfaceBrush");
        _sidebar.Children.Add(general);
        foreach (var round in rounds)
        {
            var button = MakeButton(round.Title + (round.CollectionId == activeId ? "  ●" : ""), async (_, _) => await RunAsync(() => SelectPageAsync(round.CollectionId)));
            var label = new Grid(); label.ColumnDefinitions.Add(new ColumnDefinition()); label.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            label.Children.Add(new TextBlock { Text = round.Title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            if (round.IsActive)
            {
                var dot = new TextBlock { Text = "●", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                dot.SetResourceReference(TextBlock.ForegroundProperty, "GoodBrush"); Grid.SetColumn(dot, 1); label.Children.Add(dot);
            }
            button.Content = label; button.ToolTip = round.Title + (round.IsActive ? " — активно" : " — отключено");
            button.Tag = round.CollectionId; button.Template = LeftAlignedButtonTemplate(); button.Padding = new Thickness(10, 8, 10, 8);
            button.HorizontalContentAlignment = HorizontalAlignment.Left; button.Margin = new Thickness(0, 0, 0, 6);
            if (!_generalSelected && round.CollectionId == _selectedId) button.SetResourceReference(BackgroundProperty, "SelectionSurfaceBrush");
            _sidebar.Children.Add(button);
        }
        _sequence.Children.Clear();
        foreach (var round in rounds)
        {
            var row = new DockPanel();
            var grip = new TextBlock { Text = "⠿", FontSize = 21, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.SizeAll };
            grip.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); row.Children.Add(grip);
            var enabled = new CheckBox { IsChecked = round.UseInSequence, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Участвует в автоматической очереди (не включает голосование)" };
            DockPanel.SetDock(enabled, Dock.Left);
            enabled.Click += async (_, _) => await RunAsync(async () =>
            {
                var latest = ProfileCollections;
                var included = latest.Where(item => item.CollectionId == round.CollectionId ? enabled.IsChecked == true : item.UseInSequence).Select(item => item.CollectionId).ToHashSet();
                await _runtime.Voting.SetSequenceAsync(latest.Select(item => item.CollectionId).ToList(), included, profileId: _profileId);
            });
            row.Children.Add(enabled); row.Children.Add(new TextBlock { Text = round.Title, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var card = Card(row, 10); card.Margin = new Thickness(0, 0, 0, 8); card.Tag = round.CollectionId;
            WireSequenceDrag(card, round.CollectionId); _sequence.Children.Add(card);
        }
    }

    private void WireSequenceDrag(Border card, string collectionId)
    {
        card.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_busy) return;
            for (var current = e.OriginalSource as DependencyObject; current is not null && current != card; current = VisualTreeHelper.GetParent(current))
                if (current is CheckBox) return;
            _dragCard = card; _dragOrigin = e.GetPosition(_sequence);
            card.CaptureMouse(); e.Handled = true;
        };
        card.PreviewMouseMove += (_, e) =>
        {
            if (_dragCard != card || e.LeftButton != MouseButtonState.Pressed) return;
            var position = e.GetPosition(_sequence);
            if (!_sequenceDragging)
            {
                if (Math.Abs(position.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(position.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _sequenceDragging = true; card.Opacity = .35; card.Cursor = Cursors.SizeAll;
                _dragLayer = AdornerLayer.GetAdornerLayer(_sequence);
                _dragAdorner = new SequenceDragAdorner(_sequence, _runtime.Voting.SnapshotFor(collectionId).Title,
                    (Brush)FindResource("PanelBrush"), (Brush)FindResource("AccentBrush"), (Brush)FindResource("TextBrush"), card.ActualHeight);
                _dragLayer?.Add(_dragAdorner);
            }
            UpdateSequenceDrag(position); e.Handled = true;
        };
        card.PreviewMouseLeftButtonUp += async (_, e) =>
        {
            if (_dragCard != card) return;
            var others = _sequence.Children.OfType<Border>().Where(row => row != card).ToList();
            var moved = _sequenceDragging && others.Count > 0;
            var index = Math.Min(_dragInsertion, others.Count);
            var targetId = moved ? (string)others[Math.Min(index, others.Count - 1)].Tag : string.Empty;
            CancelSequenceDrag(); e.Handled = true;
            if (moved) await RunAsync(() => MoveSequenceAsync(collectionId, targetId, index == others.Count));
        };
        card.LostMouseCapture += (_, _) => { if (_dragCard == card) CancelSequenceDrag(); };
    }

    private void UpdateSequenceDrag(Point pointer)
    {
        var others = _sequence.Children.OfType<Border>().Where(row => row != _dragCard).ToList();
        _dragInsertion = InsertionIndex(others.Select(row => row.TranslatePoint(new Point(0, row.ActualHeight / 2), _sequence).Y).ToList(), pointer.Y);
        if (_dragAdorner is null || others.Count == 0 || _dragCard is null) return;
        var line = _dragInsertion == others.Count ? others[^1].TranslatePoint(new Point(0, others[^1].ActualHeight), _sequence).Y + 4
            : others[_dragInsertion].TranslatePoint(new Point(), _sequence).Y - 4;
        var grabbed = _dragOrigin.Y - _dragCard.TranslatePoint(new Point(), _sequence).Y;
        _dragAdorner.Move(pointer.Y - grabbed, Math.Max(0, line));
    }

    internal static int InsertionIndex(IReadOnlyList<double> rowMidpoints, double pointerY)
    { var index = 0; while (index < rowMidpoints.Count && pointerY >= rowMidpoints[index]) index++; return index; }

    private void CancelSequenceDrag()
    {
        var card = _dragCard; _dragCard = null; _sequenceDragging = false;
        if (_dragAdorner is not null) _dragLayer?.Remove(_dragAdorner);
        _dragAdorner = null; _dragLayer = null;
        if (card is not null)
        {
            card.Opacity = 1; card.ClearValue(CursorProperty);
            if (card.IsMouseCaptured) card.ReleaseMouseCapture();
            _sidebarSignature = string.Empty; UpdateDisplay();
        }
    }

    private sealed class SequenceDragAdorner(FrameworkElement owner, string title, Brush background, Brush accent, Brush text, double height) : Adorner(owner)
    {
        private double _top, _line;
        public void Move(double top, double line) { IsHitTestVisible = false; _top = top; _line = line; InvalidateVisual(); }
        protected override void OnRender(DrawingContext dc)
        {
            var width = Math.Max(1, AdornedElement.RenderSize.Width);
            dc.DrawLine(new Pen(accent, 3), new Point(0, _line), new Point(width, _line));
            dc.PushOpacity(.9); dc.DrawRoundedRectangle(background, new Pen(accent, 1), new Rect(0, _top, width, height), 5, 5);
            var label = new FormattedText("⠿   " + title, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 14, text, VisualTreeHelper.GetDpi(this).PixelsPerDip)
            { MaxTextWidth = Math.Max(1, width - 24), MaxTextHeight = Math.Max(1, height - 12), Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(label, new Point(12, _top + Math.Max(6, (height - label.Height) / 2))); dc.Pop();
        }
    }

    internal async Task MoveSequenceAsync(string sourceId, string targetId, bool after)
    {
        var latest = ProfileCollections.ToList();
        var source = latest.FirstOrDefault(item => item.CollectionId == sourceId);
        if (source is null || sourceId == targetId || latest.All(item => item.CollectionId != targetId)) return;
        latest.Remove(source); var index = latest.FindIndex(item => item.CollectionId == targetId);
        latest.Insert(index + (after ? 1 : 0), source);
        await _runtime.Voting.SetSequenceAsync(latest.Select(item => item.CollectionId).ToList(), latest.Where(item => item.UseInSequence).Select(item => item.CollectionId).ToHashSet(), profileId: _profileId);
    }

    private DataTemplate RowTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border)); border.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        border.SetValue(Border.PaddingProperty, new Thickness(12)); border.SetValue(Border.MarginProperty, new Thickness(0, 0, 0, 10)); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetValue(MinHeightProperty, 78d);
        var dock = new FrameworkElementFactory(typeof(DockPanel)); border.AppendChild(dock);
        var remove = new FrameworkElementFactory(typeof(Button)); remove.SetValue(ContentControl.ContentProperty, "×"); remove.SetValue(DockPanel.DockProperty, Dock.Right);
        remove.SetValue(FrameworkElement.MarginProperty, new Thickness(5, 0, 0, 0)); remove.SetBinding(IsEnabledProperty, new Binding(nameof(VotingOptionRow.CanEdit)));
        remove.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        remove.SetValue(WidthProperty, 32d); remove.SetValue(HeightProperty, 32d); remove.SetValue(Control.PaddingProperty, new Thickness(0));
        remove.AddHandler(Button.ClickEvent, new RoutedEventHandler((sender, _) => { if (sender is FrameworkElement { DataContext: VotingOptionRow row }) { _rows.Remove(row); Dirty(); } })); dock.AppendChild(remove);
        var enabled = new FrameworkElementFactory(typeof(CheckBox)); enabled.SetValue(DockPanel.DockProperty, Dock.Left); enabled.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        enabled.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(VotingOptionRow.Enabled)) { Mode = BindingMode.TwoWay }); enabled.SetBinding(IsEnabledProperty, new Binding(nameof(VotingOptionRow.CanEdit))); dock.AppendChild(enabled);
        var command = new FrameworkElementFactory(typeof(TextBox)); command.SetValue(DockPanel.DockProperty, Dock.Left); command.SetValue(WidthProperty, 116d); command.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 0, 12, 0));
        command.SetValue(HeightProperty, 36d); command.SetValue(Control.PaddingProperty, new Thickness(8, 3, 8, 3));
        command.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        command.SetValue(TextBox.MaxLengthProperty, 80); command.SetBinding(TextBox.TextProperty, new Binding(nameof(VotingOptionRow.Command)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }); command.SetBinding(IsEnabledProperty, new Binding(nameof(VotingOptionRow.CanEdit))); dock.AppendChild(command);
        var details = new FrameworkElementFactory(typeof(StackPanel)); details.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        var bind = new FrameworkElementFactory(typeof(Button)); bind.SetValue(StyleProperty, PresetPickerStyle());
        bind.SetValue(MinHeightProperty, 20d); bind.SetValue(CursorProperty, Cursors.Hand); bind.SetValue(Control.FontWeightProperty, FontWeights.Medium);
        bind.SetResourceReference(Control.ForegroundProperty, "AccentBrush"); bind.SetValue(ToolTipProperty, "Выбрать или заменить шаблон");
        bind.SetValue(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left); bind.SetValue(Control.FontSizeProperty, 13d);
        bind.SetBinding(IsEnabledProperty, new Binding(nameof(VotingOptionRow.CanEdit))); var title = new FrameworkElementFactory(typeof(TextBlock)); title.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); title.SetBinding(TextBlock.TextProperty, new Binding(nameof(VotingOptionRow.BindingTitle))); bind.AppendChild(title);
        bind.AddHandler(Button.ClickEvent, new RoutedEventHandler(PickActive)); details.AppendChild(bind);
        var statistics = new FrameworkElementFactory(typeof(TextBlock)); statistics.SetValue(TextBlock.FontSizeProperty, 12d); statistics.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 0)); statistics.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); statistics.SetBinding(TextBlock.TextProperty, new Binding(nameof(VotingOptionRow.Statistics))); details.AppendChild(statistics); dock.AppendChild(details);
        return new DataTemplate { VisualTree = border };
    }

    private Style PresetPickerStyle()
    {
        var style = new Style(typeof(Button), (Style)FindResource("GhostButtonStyle"));
        style.Setters.Add(new Setter(Control.TemplateProperty, LeftAlignedButtonTemplate(drawBorder: true)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        var missing = new DataTrigger { Binding = new Binding(nameof(VotingOptionRow.HasPreset)), Value = false };
        missing.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("PanelAltBrush")));
        missing.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("BorderStrongBrush")));
        missing.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        missing.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7, 0, 7, 0)));
        missing.Setters.Add(new Setter(HorizontalAlignmentProperty, HorizontalAlignment.Left));
        style.Triggers.Add(missing); return style;
    }

    private static ControlTemplate LeftAlignedButtonTemplate(bool drawBorder = false)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
        if (drawBorder)
        {
            border.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = RelativeSource.TemplatedParent });
        }
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetBinding(ContentPresenter.ContentProperty, new Binding("Content") { RelativeSource = RelativeSource.TemplatedParent });
        presenter.SetBinding(MarginProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center); border.AppendChild(presenter);
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    private void PickActive(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { DataContext: VotingOptionRow row } button) return;
        var menu = new ContextMenu { PlacementTarget = button };
        foreach (var action in _runtime.Settings.Actions.Where(action => action.IsActive))
        {
            var item = new MenuItem { Header = action.DisplayName };
            item.Click += (_, _) => { row.Bind(action); Dirty(); }; menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "Нет активных шаблонов", IsEnabled = false });
        menu.IsOpen = true;
    }

    public Task AddTemplateAsync(ActionDefinition action, string collectionId)
    {
        return RunAsync(async () =>
        {
            if (ProfileCollections.All(round => round.CollectionId != collectionId)) throw new InvalidOperationException("Голосование больше не существует в этом профиле.");
            await SaveSettingsAsync();
            _generalSelected = false; _selectedId = collectionId;
            await _runtime.Voting.SelectCollectionAsync(collectionId, _runtime.Settings.ActiveProfileId); LoadDraft();
            AddRow(new VotingOption { Preset = action.Clone() });
        });
    }

    private void AddRow(VotingOption option)
    {
        if (_rows.Count >= 200) { _message.Text = "Допускается не более 200 команд."; return; }
        var row = new VotingOptionRow(option) { CanEdit = SelectedRound.CanEdit };
        row.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(VotingOptionRow.Command) or nameof(VotingOptionRow.Enabled) or nameof(VotingOptionRow.BindingTitle)) Dirty(); };
        _rows.Add(row); Dirty();
    }

    private void Dirty()
    {
        if (_loading || _closed || !SelectedRound.CanEdit) return;
        _dirty = true; _editRevision++;
        SetAutosaveStatus("Изменения сохранятся автоматически…");
        ScheduleAutosave(); UpdateDisplay();
    }

    private void ScheduleAutosave()
    {
        _autosaveTimer.Stop();
        if (_closed || !_dirty) return;
        _autosaveTimer.Interval = TimeSpan.FromMilliseconds(600); _autosaveTimer.Start();
    }

    private void SetAutosaveStatus(string text, bool error = false)
    {
        _autosaveStatus.Text = text;
        _autosaveStatus.SetResourceReference(TextBlock.ForegroundProperty, error ? "BadBrush" : "MutedBrush");
    }

    private async Task AutosaveAsync()
    {
        _autosaveTimer.Stop();
        if (_closed || !_dirty) return;
        if (_busy || _loading || _autosaving) { ScheduleAutosave(); return; }
        _autosaving = true; var session = _draftSession;
        try { await SaveSettingsAsync(flush: false); }
        catch (Exception error) when (error is InvalidOperationException or OverflowException)
        {
            if (session == _draftSession && !_closed)
                SetAutosaveStatus("Пока не сохранено: " + error.Message + " Последние корректные настройки сохранены.", true);
        }
        catch (Exception error)
        {
            if (session == _draftSession && !_closed)
            {
                SetAutosaveStatus("Не удалось сохранить: " + error.Message + " Повторим автоматически.", true);
                _autosaveTimer.Interval = TimeSpan.FromSeconds(3); _autosaveTimer.Start();
            }
        }
        finally { _autosaving = false; if (!_closed) UpdateDisplay(); }
    }
    private void LoadDraft()
    {
        _autosaveTimer.Stop(); _draftSession++; _loading = true; _message.Text = string.Empty; var round = SelectedRound;
        _target.Text = round.Target.ToString(CultureInfo.CurrentCulture); _minutes.Text = (round.DurationSeconds / 60m).ToString("0.####", CultureInfo.CurrentCulture);
        _money.IsChecked = round.MoneyGoal; _time.IsChecked = round.TimeGoal; _currency.SelectedValue = round.Currency; _conditionValue = round.Condition;
        _donationVotes.IsChecked = round.AcceptDonationVotes; _youTubeVotes.IsChecked = round.AcceptYouTubeVotes;
        _youTubeWeight.Text = round.YouTubeVoteWeight.ToString(CultureInfo.CurrentCulture); _youTubeChange.IsChecked = round.AllowYouTubeVoteChange;
        _rows.Clear(); foreach (var option in round.Options) AddRow(option);
        var automation = _runtime.Voting.Automation; _automaticDraw.IsChecked = automation.AutomaticDraw;
        _next.IsChecked = automation.AutoNextCollection; _order.SelectedIndex = automation.Order == VotingSequenceOrder.Random ? 1 : 0;
        _winnerAuto.IsChecked = automation.AutomaticWinnerEffect;
        _gameHud.IsChecked = automation.ShowGameHud; _gameHudCommands.IsChecked = automation.ShowGameHudCommands;
        _gameHudTemplateNames.IsChecked = automation.ShowGameHudTemplateNames;
        _gameHudFormat.SelectedIndex = (int)automation.GameHudFormat;
        _dirty = false; _loading = false; SetAutosaveStatus("Изменения сохраняются автоматически."); UpdateDisplay();
    }

    internal Task FlushPendingChangesAsync() => SaveSettingsAsync();

    private async Task SaveSettingsAsync(bool flush = true)
    {
        await _saveGate.WaitAsync();
        try
        {
            if (_runtime.VotingError.Length > 0) throw new InvalidOperationException(_runtime.VotingError);
            while (_dirty && !_closed)
            {
                _autosaveTimer.Stop(); var revision = _editRevision; var session = _draftSession;
                if (_generalSelected)
                {
                    var automation = _runtime.Voting.Automation; automation.AutomaticDraw = _automaticDraw.IsChecked == true;
                    automation.AutoNextCollection = _next.IsChecked == true; automation.Order = _order.SelectedIndex == 1 ? VotingSequenceOrder.Random : VotingSequenceOrder.InOrder;
                    automation.AutomaticWinnerEffect = _winnerAuto.IsChecked == true;
                    automation.ShowGameHud = _gameHud.IsChecked == true; automation.ShowGameHudCommands = _gameHudCommands.IsChecked == true;
                    automation.ShowGameHudTemplateNames = _gameHudTemplateNames.IsChecked == true;
                    automation.GameHudFormat = (VotingHudFormat)Math.Max(0, _gameHudFormat.SelectedIndex);
                    await _runtime.Voting.SaveAutomationAsync(automation);
                }
                else
                {
                    var draft = SelectedRound;
                    draft.MoneyGoal = _money.IsChecked == true; draft.TimeGoal = _time.IsChecked == true;
                    if (draft.MoneyGoal) draft.Target = Parse(_target.Text, "Сумма голосования");
                    if (draft.TimeGoal) draft.DurationSeconds = checked((int)(Parse(_minutes.Text, "Время в минутах") * 60));
                    draft.Currency = _currency.SelectedValue as string ?? "RUB"; draft.Condition = _conditionValue; draft.Options = _rows.Select(row => row.Model.Clone()).ToList();
                    draft.AcceptDonationVotes = _donationVotes.IsChecked == true; draft.AcceptYouTubeVotes = _youTubeVotes.IsChecked == true;
                    draft.YouTubeVoteWeight = Parse(_youTubeWeight.Text, "Вес голоса YouTube"); draft.AllowYouTubeVoteChange = _youTubeChange.IsChecked == true;
                    await _runtime.Voting.SaveSettingsAsync(draft);
                }
                if (session != _draftSession) return;
                // Typing can continue while the disk is writing. Only mark the
                // exact saved revision clean; a newer edit retains its debounce.
                if (revision == _editRevision) { _dirty = false; _autosaveTimer.Stop(); SetAutosaveStatus("Изменения сохранены автоматически."); }
                else { SetAutosaveStatus("Изменения сохранятся автоматически…"); ScheduleAutosave(); }
                if (!flush) break;
            }
        }
        finally { _saveGate.Release(); }
    }

    internal async Task ResetSelectedAsync()
    {
        await SaveSettingsAsync(); await _runtime.ResetVotingAsync(_selectedId); LoadDraft(); _message.Text = string.Empty;
    }

    private async Task EditHudPositionAsync()
    {
        await SaveSettingsAsync();
        var round = ProfileCollections.FirstOrDefault(item => item.IsActive) ?? SelectedRound;
        var editor = new VotingHudPositionWindow(round, _runtime.Voting.Automation) { Owner = this };
        if (editor.ShowDialog() != true) return;
        await _runtime.Voting.SaveHudPositionAsync(editor.PositionX, editor.PositionY);
        await _runtime.AdvanceVotingHudAsync();
        _message.Text = "Положение HUD сохранено. Оно применяется в игре автоматически при подключении.";
    }

    internal async Task DeleteSelectedAsync()
    {
        await _saveGate.WaitAsync();
        try
        {
            _autosaveTimer.Stop(); var id = _selectedId; await _runtime.DeleteVotingAsync(id);
            _selectedId = ProfileCollections.FirstOrDefault()?.CollectionId ?? string.Empty;
            _generalSelected = ProfileCollections.Count == 0; _dirty = false; LoadDraft();
            _message.Text = "Голосование удалено. Другие голосования и настройки сохранены.";
        }
        finally { _saveGate.Release(); }
    }

    private async Task TestContributionAsync()
    {
        await SaveSettingsAsync();
        if (SelectedRound.Phase != VotingPhase.Collecting) throw new InvalidOperationException("Включите это голосование.");
        var result = await _runtime.Voting.RecordAsync(new DonationEvent { Id = "voting-test:" + Guid.NewGuid().ToString("N"),
            Amount = Parse(_testAmount.Text, "Тестовая сумма"), Currency = SelectedRound.Currency,
            Message = _testMessage.Text, CreatedAt = DateTimeOffset.UtcNow }, _runtime.Settings.ActiveProfileId);
        _message.Text = result switch { "counted" => "Тестовый взнос учтён. Обычный эффект не запускался.",
            "ambiguous_commands" => "В сообщении несколько разных команд; взнос не учтён.",
            "no_command" => "Команда не найдена в сообщении.", _ => "Взнос не учтён: " + result };
    }

    private async Task SpinAsync()
    {
        await SaveSettingsAsync();
        var pending = _runtime.Voting.PendingWinners.FirstOrDefault(round => round.CollectionId == _selectedId);
        if (pending is not null) { await _runtime.SubmitVotingWinnerAsync(roundId: pending.Id); return; }
        var started = false;
        try
        {
            await _runtime.BeginVotingDrawAsync(collectionId: _selectedId); started = true;
        }
        finally { if (started) await _runtime.FinishVotingDrawAsync(); UpdateDisplay(); }
    }

    private void VotingChanged() => Dispatcher.BeginInvoke(new Action(UpdateDisplay));
    private void ApplyPanelLayout()
    {
        var compact = !_generalSelected && _settingsCollapsed;
        MinWidth = compact ? 780 : 1120;
        MinHeight = compact ? 560 : 650;
        _settingsCard!.Visibility = _settingsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        _settingsColumn.MinWidth = 0;
        _settingsColumn.MaxWidth = _settingsCollapsed ? 0 : 440;
        _settingsColumn.Width = _settingsCollapsed ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        _settingsColumn.MinWidth = _settingsCollapsed ? 0 : 380;
        _settingsGap.Width = new GridLength(_settingsCollapsed ? 0 : 24);
        _commandsPanel.Margin = new Thickness(0, _settingsCollapsed ? 0 : 16, 0, 0);
        _compactSummary.Visibility = _compactActions.Visibility = _compactFeedback.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        _headerActions.Visibility = _generalSelected ? Visibility.Collapsed : Visibility.Visible;
        _panelToggle.ToolTip = _settingsCollapsed ? "Показать настройки" : "Скрыть настройки";
        System.Windows.Automation.AutomationProperties.SetName(_panelToggle, (string)_panelToggle.ToolTip);
        var spinHost = _settingsCollapsed ? _compactActions : _settingsActions;
        if (!ReferenceEquals(_spin.Parent, spinHost))
        {
            if (_spin.Parent is Panel previous) previous.Children.Remove(_spin);
            spinHost.Children.Add(_spin);
        }
    }
    private void UpdateDisplay()
    {
        var round = SelectedRound;
        _spinning = _runtime.VotingDrawingCollectionId == _selectedId; UpdateNavigation();
        _generalPanel.Visibility = _generalSelected ? Visibility.Visible : Visibility.Collapsed;
        _collectionPanel.Visibility = _generalSelected ? Visibility.Collapsed : Visibility.Visible;
        _reset.Visibility = _generalSelected ? Visibility.Collapsed : Visibility.Visible;
        _rename.Visibility = _delete.Visibility = _collectionToggle.Visibility = _generalSelected ? Visibility.Collapsed : Visibility.Visible;
        _statusDot.Visibility = !_generalSelected && round.IsActive ? Visibility.Visible : Visibility.Collapsed;
        _title.Text = _generalSelected ? "Голосование: Общие настройки" : round.Title;
        _collectionToggleLabel.Text = round.IsActive ? "Активно" : "Отключено";
        _collectionToggle.SetResourceReference(Control.ForegroundProperty, round.IsActive ? "GoodBrush" : "BadBrush");
        _statusDot.SetResourceReference(TextBlock.ForegroundProperty, "GoodBrush");
        _statusDot.ToolTip = "Голосование активно";
        _title.ToolTip = _title.Text;
        var active = ProfileCollections.FirstOrDefault(item => item.IsActive);
        _automationStatus.Text = active is null ? "Голосования выключены. Включите нужное голосование на его странице." : "Активно: " + active.Title;
        _automationStatus.SetResourceReference(TextBlock.ForegroundProperty, active is null ? "MutedBrush" : "GoodBrush");
        ApplyPanelLayout();
        var canEdit = !_busy && round.CanEdit && _runtime.VotingError.Length == 0;
        _settings.IsEnabled = _runtime.VotingError.Length == 0;
        _money.IsEnabled = _time.IsEnabled = _currency.IsEnabled = canEdit;
        _donationVotes.IsEnabled = _youTubeVotes.IsEnabled = canEdit;
        _youTubeWeight.IsEnabled = _youTubeChange.IsEnabled = canEdit && _youTubeVotes.IsChecked == true;
        _youTubeCurrency.Text = (_currency.SelectedValue as string ?? round.Currency) is "RUB" ? "руб." : _currency.SelectedValue as string ?? round.Currency;
        _youTubeChangeLabel.Text = _youTubeChange.IsChecked == true ? "Да" : "Нет";
        _target.IsEnabled = canEdit && _money.IsChecked == true; _minutes.IsEnabled = canEdit && _time.IsChecked == true;
        _condition.Content = _conditionValue == VotingCondition.All ? "И" : "ИЛИ"; _condition.IsEnabled = canEdit && _money.IsChecked == true && _time.IsChecked == true;
        foreach (var row in _rows)
        {
            row.CanEdit = canEdit;
            row.Update(round.Options.FirstOrDefault(option => option.Id == row.Model.Id)?.Amount ?? 0, round.Total, round.Currency);
        }
        _total.Text = FormatMoney(round.Total, round.Currency) + (round.MoneyGoal ? " / " + FormatMoney(round.Target, round.Currency) : "");
        _remaining.Text = RemainingText(round); _progress.Value = Progress(round);
        _compactSummary.Text = CompactConditions(round, DateTimeOffset.UtcNow);
        _status.Text = _runtime.VotingError.Length > 0 ? _runtime.VotingError : round.Notice.Length > 0 ? round.Notice : "Настройте команды и включите голосование. Изменения сохраняются автоматически.";
        if (round.Winner is { } winner) _status.Text += "\nПобедитель: " + winner.Command + " → " + winner.Preset?.DisplayName;
        if (round.DeliveryNote.Length > 0) _status.Text += "\n" + round.DeliveryNote;
        var pending = _runtime.Voting.PendingWinners.FirstOrDefault(item => item.CollectionId == _selectedId);
        if (pending is not null && pending.Id != round.Id)
            _status.Text += "\nОжидает активации: " + pending.Winner?.Command + " → " + pending.Winner?.Preset?.DisplayName;
        _compactStatus.Visibility = _runtime.VotingError.Length > 0 || pending is not null || round.Phase is VotingPhase.Ready or VotingPhase.Failed or VotingPhase.Drawn
            ? Visibility.Visible : Visibility.Collapsed;
        var waitingForGame = pending is { GamePresentationRequired: true, GamePresentationCompleted: false };
        _spin.Content = waitingForGame ? "Ожидает игровую рулетку" : pending is not null ? "Активировать эффект победителя" : "Крутить";
        _spin.ToolTip = pending is not null ? pending.Winner?.Command + " → " + pending.Winner?.Preset?.DisplayName : "Разыграть голосование по накопленным долям";
        _spin.Visibility = !_generalSelected && (pending is not null || round.Phase != VotingPhase.Drawn) ? Visibility.Visible : Visibility.Collapsed;
        _spin.IsEnabled = !_busy && _runtime.VotingError.Length == 0 && !_runtime.VotingIsSpinning && !waitingForGame && (pending is not null || round.Phase == VotingPhase.Ready);
        _reset.IsEnabled = _delete.IsEnabled = _collectionToggle.IsEnabled = !_busy && _runtime.VotingError.Length == 0;
        _automaticDraw.IsEnabled = _order.IsEnabled = _next.IsEnabled = _winnerAuto.IsEnabled = !_busy && _runtime.VotingError.Length == 0;
        _gameHudTemplateNames.IsEnabled = !_busy && _runtime.VotingError.Length == 0 && _gameHud.IsChecked == true && _gameHudCommands.IsChecked == true;
        _add.IsEnabled = canEdit && _rows.Count < 200; _test.IsEnabled = !_busy && round.Phase == VotingPhase.Collecting;
    }

    private async Task RunAsync(Func<Task> operation)
    {
        if (_runtime.VotingError.Length > 0) { _message.Text = _runtime.VotingError; return; }
        if (_busy) return; _busy = true; UpdateDisplay();
        try { await operation(); }
        catch (Exception error) { _message.Text = error.Message; }
        finally { _busy = false; UpdateDisplay(); }
    }

    private async void ClosingAsync(object? sender, CancelEventArgs args)
    {
        if (_closing || !_dirty) return; args.Cancel = true;
        try { await SaveSettingsAsync(); _closing = true; Close(); }
        catch (Exception error) { _message.Text = error.Message; }
    }

    private static Button MakeButton(string text, RoutedEventHandler click)
    { var button = new Button { Content = text }; button.Click += click; return button; }
    private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeights.Medium, FontSize = 15 };
    private static decimal Parse(string value, string label)
    {
        value = value.Replace(" ", "").Replace("\u00a0", "");
        if (decimal.TryParse(value.Replace(',', '.'), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var result)) return result;
        throw new InvalidOperationException(label + ": введите число.");
    }
    public static string FormatMoney(decimal amount, string currency) => amount.ToString("N2", CultureInfo.GetCultureInfo("ru-RU")).TrimEnd('0').TrimEnd(',') + " " + (currency == "RUB" ? "руб." : currency);
    internal static string CompactConditions(VotingRound round, DateTimeOffset now)
    {
        var conditions = new List<string>(2);
        if (round.MoneyGoal)
        {
            var culture = CultureInfo.GetCultureInfo("ru-RU");
            conditions.Add(round.Total.ToString("0.########", culture) + "/" + round.Target.ToString("0.########", culture)
                + " " + (round.Currency == "RUB" ? "рублей" : round.Currency));
        }
        if (round.TimeGoal)
        {
            var timer = VotingProgressDisplay.Timer(round, now);
            // Keep one minute digit for 0:00, without changing hours-long timers.
            if (timer.Length > 1 && timer[0] == '0' && timer[1] != ':') timer = timer[1..];
            conditions.Add(timer + " (таймер)");
        }
        return string.Join(round.Condition == VotingCondition.All ? " И " : " ИЛИ ", conditions);
    }
    public static string RemainingText(VotingRound round)
    {
        if (!round.TimeGoal || round.Phase is VotingPhase.Ready or VotingPhase.Failed or VotingPhase.Drawn) return string.Empty;
        var at = round.ClosedAt ?? (round.Phase == VotingPhase.Paused ? round.PausedAt ?? DateTimeOffset.UtcNow : DateTimeOffset.UtcNow);
        var seconds = round.Deadline is { } deadline ? Math.Max(0, (int)Math.Ceiling((deadline - at).TotalSeconds)) : round.DurationSeconds;
        return "Осталось " + (seconds / 60).ToString("00") + ":" + (seconds % 60).ToString("00");
    }
    public static double Progress(VotingRound round) => round.Phase is VotingPhase.Ready or VotingPhase.Drawn ? 100
        : round.MoneyGoal ? Math.Clamp((double)(round.Total / Math.Max(1, round.Target) * 100), 0, 100)
        : round.Deadline is { } deadline ? Math.Clamp(100 - (deadline - (round.PausedAt ?? DateTimeOffset.UtcNow)).TotalSeconds / Math.Max(1, round.DurationSeconds) * 100, 0, 100) : 0;
}
