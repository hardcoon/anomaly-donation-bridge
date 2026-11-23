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
    public bool Enabled { get => Model.Enabled; set { if (Model.Enabled != value) { Model.Enabled = value; RaisePropertyChanged(); } } }
    public string Command { get => Model.Command; set { if (Model.Command != value) { Model.Command = value; RaisePropertyChanged(); } } }
    public string BindingTitle => Model.Preset?.DisplayName ?? "Выбрать из активных";
    public bool HasPreset => Model.Preset is not null;
    private string _contributionText = string.Empty;
    private double _percentage;
    public string ContributionText { get => _contributionText; private set => SetProperty(ref _contributionText, value); }
    public double Percentage => _percentage;
    public string ShareText => _percentage.ToString("0.#", CultureInfo.CurrentCulture) + "%";
    private bool _canEdit;
    public bool CanEdit { get => _canEdit; set => SetProperty(ref _canEdit, value); }
    public void Bind(ActionDefinition action) { Model.Preset = action.Clone(); RaisePropertyChanged(nameof(BindingTitle)); RaisePropertyChanged(nameof(HasPreset)); }
    public void Update(decimal amount, decimal total, string currency)
    {
        Model.Amount = amount;
        ContributionText = VotingView.FormatMoney(amount, currency);
        var percentage = Enabled && total > 0 ? Math.Clamp((double)(amount / total * 100), 0, 100) : 0;
        if (SetProperty(ref _percentage, percentage, nameof(Percentage))) RaisePropertyChanged(nameof(ShareText));
    }
}

public sealed partial class VotingView : UserControl, IDisposable
{
    private readonly BridgeRuntime _runtime;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _autosaveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private long _editRevision, _draftSession;
    private bool _autosaving, _closed;
    private int _displayQueued;
    private readonly ObservableCollection<VotingOptionRow> _rows = [];
    private readonly TextBlock _title = new() { FontSize = 24, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _automationStatus = new() { FontSize = 14, FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 15) };
    private readonly CheckBox _money = new() { Content = "Набрать сумму" }, _time = new() { Content = "По времени" };
    private readonly TextBox _target = new(), _minutes = new();
    private readonly CheckBox _donationVotes = new() { Content = "Описание доната" };
    private readonly CheckBox _youTubeVotes = new() { Content = "Чат зрителей", ToolTip = "Голоса из подключённых чатов YouTube, Twitch и VK Видео Live." };
    private readonly TextBox _youTubeWeight = new() { Width = 88, MaxLength = 16 };
    private readonly TextBlock _youTubeCurrency = new() { Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _youTubeChange = new();
    private readonly ComboBox _currency = new() { DisplayMemberPath = "Label", SelectedValuePath = "Code" };
    private readonly ComboBox _condition = new() { ItemsSource = new[] { "Любое из условий (ИЛИ)", "Оба условия (И)" } };
    private readonly StackPanel _conditionRow = new();
    private readonly TextBlock _targetCurrency = new() { Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _total = new() { TextTrimming = TextTrimming.CharacterEllipsis }, _remaining = new();
    private FrameworkElement _amountMetric = null!, _timeMetric = null!;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _saveProblem = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock _autosaveStatus = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 4, 0, 12) };
    private readonly ProgressBar _progress = new() { Height = 4 };
    private readonly Button _spin = new() { Content = "Крутить", MinWidth = 90 };
    private readonly Button _selectedSpin = new() { Content = "Крутить", Visibility = Visibility.Collapsed };
    private readonly TextBox _testAmount = new() { Text = "100" }, _testMessage = new() { Tag = "!команда", MaxLength = 4096 };
    private readonly Grid _settings = new();
    private readonly Button _add = new() { Content = "Добавить команду", MinHeight = 30, Height = 30, Padding = new Thickness(8, 3, 8, 3) };
    private readonly Button _test = new() { Content = "Внести", Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _reset = new() { Content = "Сбросить", Height = 34, Margin = new Thickness(7, 0, 0, 0) };
    private readonly Button _rename = new() { Content = "✎" };
    private readonly Button _delete = new() { Content = "\uE74D", FontSize = 16,
        Width = 34, Height = 34, Padding = new Thickness(0), Margin = new Thickness(7, 0, 0, 0), ToolTip = "Удалить это голосование" };
    private readonly Button _obs = new() { Content = "Вывести окно OBS", Height = 36 };
    private readonly Button _panelToggle = new() { Content = "Настройки", Height = 34, Margin = new Thickness(7, 0, 0, 0) };
    private readonly TextBlock _compactSummary = new() { FontSize = 12, Margin = new Thickness(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _testPanelToggle = new() { Content = "Тестовый взнос", MinHeight = 29, Padding = new Thickness(6, 3, 6, 3) };
    private FrameworkElement _testFields = null!;
    private ColumnDefinition _settingsColumn = null!;
    private bool _settingsCollapsed;
    private readonly WrapPanel _headerActions = new() { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private Border _settingsCard = null!;
    private readonly Button _collectionToggle = new() { Height = 34 };
    private readonly TextBlock _collectionToggleLabel = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private readonly TextBlock _statusDot = new() { Text = "●", FontSize = 12, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _generalButton = new() { Content = "Общие настройки" };
    private readonly Button _playerTitle = new() { HorizontalContentAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _playerState = new() { FontSize = 11 };
    private readonly TextBlock _playerName = new() { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _playerEmpty = new() { Text = "Нет активного голосования", TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _playerToggle = new() { Width = 36, Height = 36, Padding = new Thickness(0), FontSize = 15 };
    private readonly Button _playerNext = new() { Width = 36, Height = 36, Padding = new Thickness(0) };
    private FrameworkElement _playerProgress = null!;
    private readonly Border _player = new();
    private readonly StackPanel _playerActions = new() { Orientation = Orientation.Horizontal };
    private string? _playerCollectionId;
    private string? _pausedPlayerId;
    private readonly CheckBox _winnerAuto = new() { Content = new TextBlock { Text = "Автоматически активировать эффект победителя", TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 14, 0, 0) };
    private readonly Grid _commandsPanel = new();
    private readonly ListBox _commandList = new();
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
    private bool _generalSelected;
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
    private bool _dirty, _loading, _busy;
    private Window? HostWindow => Window.GetWindow(this);
    internal ObservableCollection<VotingOptionRow> Rows => _rows;
    internal Border Player => _player;

    public VotingView(BridgeRuntime runtime, VotingObsController obs)
        : this(runtime, obs, hostPlayerSeparately: false) { }

    internal VotingView(BridgeRuntime runtime, VotingObsController obs, bool hostPlayerSeparately)
    {
        _runtime = runtime;
        _settingsCollapsed = runtime.UiPreferences.VotingSettingsPanelCollapsed;
        _generationCollapsed = runtime.UiPreferences.VotingGenerationPanelCollapsed;
        _profileId = runtime.Settings.ActiveProfileId;
        var rounds = runtime.Voting.CollectionsFor(_profileId);
        var lastSelectedId = runtime.Voting.Snapshot.CollectionId;
        _selectedId = rounds.FirstOrDefault(round => round.CollectionId == lastSelectedId)?.CollectionId
            ?? rounds.FirstOrDefault()?.CollectionId ?? string.Empty;
        _generalSelected = _selectedId.Length == 0;
        InitializeComponent();
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
        UseLayoutRounding = true;
        SetResourceReference(BackgroundProperty, "BackgroundBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        Content = Build(hostPlayerSeparately);
        foreach (var box in new[] { _target, _minutes, _youTubeWeight }) box.TextChanged += (_, _) => Dirty();
        foreach (var check in new[] { _donationVotes, _youTubeVotes, _youTubeChange })
        { check.Checked += (_, _) => Dirty(); check.Unchecked += (_, _) => Dirty(); }
        _currency.SelectionChanged += (_, _) => Dirty();
        _money.Checked += (_, _) => Dirty(); _money.Unchecked += (_, _) => Dirty();
        _time.Checked += (_, _) => Dirty(); _time.Unchecked += (_, _) => Dirty();
        _condition.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            _conditionValue = _condition.SelectedIndex == 1 ? VotingCondition.All : VotingCondition.Any;
            Dirty();
        };
        _spin.Click += async (_, _) =>
        {
            if (_playerCollectionId is { } id)
            {
                var pendingId = (string?)_spin.CommandParameter;
                await RunAsync(() => SpinAsync(id, pendingId, fromPlayer: true));
            }
        };
        _selectedSpin.Click += async (_, _) =>
        {
            var id = _selectedId;
            var pendingId = (string?)_selectedSpin.CommandParameter;
            await RunAsync(() => SpinAsync(id, pendingId));
        };
        _add.Click += (_, _) =>
        {
            if (AddRow(new VotingOption()) is { } row) _commandList.ScrollIntoView(row);
        };
        _test.Click += async (_, _) => await RunAsync(TestContributionAsync);
        _rename.Click += async (_, _) => await RunAsync(() => RenameCollectionAsync(_selectedId));
        _reset.Click += async (_, _) => await RunAsync(() => ConfirmResetAsync(_selectedId));
        _delete.Click += async (_, _) => await RunAsync(() => ConfirmDeleteAsync(_selectedId));
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
        _collectionToggle.Click += async (_, _) =>
        {
            if (!_generalSelected) await RunAsync(() => ChangeCollectionActivityAsync(_selectedId, activate: true));
        };
        _playerNext.Click += async (_, _) =>
        {
            var profileId = _profileId;
            var currentId = _playerCollectionId;
            await RunAsync(async () =>
            {
                await FlushPendingChangesAsync();
                if (profileId == _runtime.Settings.ActiveProfileId && !_runtime.VotingIsSpinning)
                    await _runtime.Voting.AdvanceSequenceAsync(profileId, currentId);
            });
        };
        _playerTitle.Click += async (_, _) =>
        {
            if (_playerCollectionId is { } id && (_generalSelected || _selectedId != id))
                await RunAsync(() => SelectPageAsync(id));
        };
        _playerToggle.Click += async (_, _) =>
        {
            if (_playerCollectionId is { } id)
            {
                var resume = _pausedPlayerId == id;
                await RunAsync(() => ChangeCollectionActivityAsync(id, resume));
            }
        };
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
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && !_closed) { UpdateDisplay(); _timer.Start(); }
            else { _timer.Stop(); CancelSequenceDrag(); }
        };
        LoadDraft();
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _generationPopup.IsOpen = false; _generationTip.IsOpen = false;
        CancelSequenceDrag();
        _timer.Stop();
        _autosaveTimer.Stop();
        _runtime.Voting.Changed -= VotingChanged;
        _runtime.SettingsChanged -= ProfileChanged;
    }

    private static Border Card(UIElement child, double padding)
    {
        var border = new Border { Child = child, Padding = new Thickness(padding), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush"); border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); return border;
    }

    private FrameworkElement BuildGeneral()
    {
        var content = new Grid { Margin = new Thickness(0, 0, 8, 0) };
        for (var row = 0; row < 3; row++) content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(_automationStatus);
        var columns = new Grid(); Grid.SetRow(columns, 1); content.Children.Add(columns);
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
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
        columns.SizeChanged += (_, e) =>
        {
            var stacked = e.NewSize.Width < 570;
            columns.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : 24);
            columns.ColumnDefinitions[2].Width = stacked ? new GridLength(0) : new GridLength(1.1, GridUnitType.Star);
            Grid.SetColumn(hudCard, stacked ? 0 : 2);
            Grid.SetRow(hudCard, stacked ? 1 : 0);
            hudCard.Margin = stacked ? new Thickness(0, 12, 0, 0) : new Thickness(0);
        };
        panel.Children.Add(Heading("Порядок запуска голосований")); _order.Margin = new Thickness(0, 10, 0, 16); panel.Children.Add(_order);
        panel.Children.Add(Heading("Голосования в автоматической очереди"));
        panel.Children.Add(new TextBlock { Text = "Отметьте голосования для автозапуска и кнопки «Следующее» в плеере. Чтобы изменить порядок, перетащите строку за значок ⠿ или название.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 12) });
        panel.Children.Add(_sequence);
        var saving = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 14, 0, 0) };
        saving.SetBinding(TextBlock.TextProperty, new Binding(nameof(TextBlock.Text)) { Source = _autosaveStatus });
        saving.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(TextBlock.Foreground)) { Source = _autosaveStatus });
        Grid.SetRow(saving, 2); content.Children.Add(saving);
        return new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private IReadOnlyList<VotingRound> ProfileCollections => _runtime.Voting.CollectionsFor(_profileId);
    private VotingRound SelectedRound => FindCollection(_selectedId)
        ?? new VotingRound { CollectionId = string.Empty, ProfileId = _profileId };

    private VotingRound? FindCollection(string collectionId)
    {
        var round = _runtime.Voting.SnapshotFor(collectionId);
        return round.CollectionId == collectionId && round.ProfileId == _profileId ? round : null;
    }

    private bool IsEditingCollection(string collectionId) => !_generalSelected && _selectedId == collectionId;

    private void ProfileChanged() => Dispatcher.BeginInvoke(async () =>
    {
        if (_closed) return;
        if (_profileId == _runtime.Settings.ActiveProfileId)
        { RefreshGenerationChoices(); UpdateDisplay(); return; }
        try
        {
            // Finish any in-flight edit against its original collection before
            // replacing the editor with the new profile's independent draft.
            await SaveSettingsAsync();
            _profileId = _runtime.Settings.ActiveProfileId;
            _pausedPlayerId = null;
            _selectedId = ProfileCollections.FirstOrDefault()?.CollectionId ?? string.Empty;
            _generalSelected = _selectedId.Length == 0;
            CancelSequenceDrag();
            LoadDraft();
        }
        catch (Exception error) { _message.Text = error.Message; }
    });

    private async Task SelectPageAsync(string? collectionId)
    {
        await SaveSettingsAsync();
        if (collectionId is not null)
        {
            await _runtime.Voting.SelectCollectionAsync(collectionId, _profileId);
            _selectedId = collectionId;
        }
        _generalSelected = collectionId is null;
        LoadDraft();
    }

    private void UpdateNavigation(IReadOnlyList<VotingRound> rounds, VotingRound? active)
    {
        var activeId = active?.CollectionId ?? string.Empty;
        var signature = string.Join("|", rounds.Select(round => round.CollectionId + ":" + round.Title + ":" + round.UseInSequence)) +
            activeId + active?.Phase + _selectedId + _generalSelected;
        if (_dragCard is not null || _sidebarSignature == signature) return; _sidebarSignature = signature;
        var focusedId = _sidebar.Children.OfType<Button>().FirstOrDefault(button => button.IsKeyboardFocusWithin)?.Tag as string;
        _sidebar.Children.Clear();
        if (_generalSelected) _generalButton.SetResourceReference(Control.BackgroundProperty, "WarningPanelBrush");
        else _generalButton.ClearValue(Control.BackgroundProperty);
        foreach (var round in rounds)
        {
            var isSelected = !_generalSelected && round.CollectionId == _selectedId;
            var isOnAir = round.CollectionId == activeId;
            var button = MakeButton(round.Title, async (_, _) =>
            {
                if (_generalSelected || _selectedId != round.CollectionId)
                    await RunAsync(() => SelectPageAsync(round.CollectionId));
            });
            var labels = new StackPanel();
            labels.Children.Add(new TextBlock { Text = round.Title, TextTrimming = TextTrimming.CharacterEllipsis });
            var activity = isOnAir ? round.Phase switch
            {
                VotingPhase.Collecting => "В эфире",
                VotingPhase.Ready => "Готово к розыгрышу",
                VotingPhase.Draft => "Ожидает настройки",
                _ => "Завершено"
            } : "";
            if (isOnAir)
            {
                var live = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
                var dot = new TextBlock { Text = "●", FontSize = 10, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
                var caption = new TextBlock { Text = activity, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                dot.SetResourceReference(TextBlock.ForegroundProperty, "GoodBrush");
                caption.SetResourceReference(TextBlock.ForegroundProperty, "GoodBrush");
                live.Children.Add(dot);
                live.Children.Add(caption);
                labels.Children.Add(live);
            }
            var marker = new Border { Child = labels, Padding = new Thickness(6, 0, 0, 0), BorderThickness = new Thickness(2, 0, 0, 0) };
            if (isSelected)
            {
                marker.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
                button.SetResourceReference(Control.BackgroundProperty, "WarningPanelBrush");
                button.SetResourceReference(Control.BorderBrushProperty, "WarningBorderBrush");
            }
            button.Content = marker;
            button.ToolTip = isOnAir ? round.Title + " · " + activity : round.Title;
            System.Windows.Automation.AutomationProperties.SetName(button, (string)button.ToolTip);
            button.Tag = round.CollectionId;
            button.ContextMenu = BuildCollectionMenu(round.CollectionId);
            button.SetResourceReference(StyleProperty, "VotingListButtonStyle");
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            button.Padding = new Thickness(1, 7, 9, 7);
            button.Margin = new Thickness(0, 0, 0, 4);
            _sidebar.Children.Add(button);
            if (round.CollectionId == focusedId) Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => button.Focus()));
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

    private ContextMenu BuildCollectionMenu(string collectionId)
    {
        var toggle = new MenuItem();
        var rename = new MenuItem { Header = "Переименовать" };
        var reset = new MenuItem { Header = "Сбросить" };
        var delete = new MenuItem { Header = "Удалить" };
        delete.SetResourceReference(StyleProperty, "DangerMenuItemStyle");

        var menu = new ContextMenu { Items = { toggle, rename, reset, delete } };
        menu.Opened += (_, _) =>
        {
            var round = FindCollection(collectionId);
            var canUse = round is not null && _profileId == _runtime.Settings.ActiveProfileId && !_busy && _runtime.VotingError.Length == 0;
            rename.IsEnabled = reset.IsEnabled = delete.IsEnabled = canUse;
            toggle.Header = round is null ? "Запустить" : ActivityCaption(round);
            toggle.CommandParameter = round?.IsActive == false;
            toggle.IsEnabled = round is not null && canUse && CanChangeActivity(round);
        };
        toggle.Click += async (_, _) =>
        {
            var activate = toggle.CommandParameter is true;
            await RunAsync(() => ChangeCollectionActivityAsync(collectionId, activate));
        };
        rename.Click += async (_, _) => await RunAsync(() => RenameCollectionAsync(collectionId));
        reset.Click += async (_, _) => await RunAsync(() => ConfirmResetAsync(collectionId));
        delete.Click += async (_, _) => await RunAsync(() => ConfirmDeleteAsync(collectionId));
        return menu;
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

    private void RemoveCommand(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: VotingOptionRow { CanEdit: true } row }) _rows.Remove(row);
    }

    private void PickActive(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { DataContext: VotingOptionRow row } button) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom, MinWidth = button.ActualWidth };
        foreach (var action in _runtime.Settings.Actions.Where(action => action.IsActive))
        {
            var item = new MenuItem { Header = action.DisplayName };
            item.Click += (_, _) => { if (row.CanEdit && _rows.Contains(row)) row.Bind(action); }; menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "Нет активных шаблонов", IsEnabled = false });
        button.ContextMenu = menu;
        menu.Closed += (_, _) => { if (ReferenceEquals(button.ContextMenu, menu)) button.ContextMenu = null; };
        menu.IsOpen = true;
    }

    public Task AddTemplateAsync(ActionDefinition action, string collectionId)
    {
        return RunAsync(async () =>
        {
            if (ProfileCollections.All(round => round.CollectionId != collectionId)) throw new InvalidOperationException("Голосование больше не существует в этом профиле.");
            if (_runtime.Voting.SnapshotFor(collectionId).Generation.Enabled)
                throw new InvalidOperationException("Это голосование формируется автоматически. Отметьте шаблон в его списке.");
            await SaveSettingsAsync();
            _generalSelected = false; _selectedId = collectionId;
            await _runtime.Voting.SelectCollectionAsync(collectionId, _runtime.Settings.ActiveProfileId); LoadDraft();
            AddRow(new VotingOption { Preset = action.Clone() });
        });
    }

    private VotingOptionRow? AddRow(VotingOption option)
    {
        if (_rows.Count >= 200) { _message.Text = "Допускается не более 200 команд."; return null; }
        var row = new VotingOptionRow(option);
        row.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(VotingOptionRow.Command) or nameof(VotingOptionRow.Enabled) or nameof(VotingOptionRow.BindingTitle)) Dirty(); };
        _rows.Add(row);
        return row;
    }

    private void Dirty()
    {
        if (_loading || _closed) return;
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
        _saveProblem.Text = error ? text : "";
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
        _condition.SelectedIndex = round.Condition == VotingCondition.All ? 1 : 0;
        _donationVotes.IsChecked = round.AcceptDonationVotes; _youTubeVotes.IsChecked = round.AcceptYouTubeVotes;
        _youTubeWeight.Text = round.YouTubeVoteWeight.ToString(CultureInfo.CurrentCulture); _youTubeChange.IsChecked = round.AllowYouTubeVoteChange;
        LoadGeneration(round);
        _rows.Clear(); foreach (var option in round.Options) AddRow(option);
        var automation = _runtime.Voting.Automation; _automaticDraw.IsChecked = automation.AutomaticDraw;
        _next.IsChecked = automation.AutoNextCollection; _order.SelectedIndex = automation.Order == VotingSequenceOrder.Random ? 1 : 0;
        _winnerAuto.IsChecked = automation.AutomaticWinnerEffect;
        _gameHud.IsChecked = automation.ShowGameHud; _gameHudCommands.IsChecked = automation.ShowGameHudCommands;
        _gameHudTemplateNames.IsChecked = automation.ShowGameHudTemplateNames;
        _gameHudFormat.SelectedIndex = (int)automation.GameHudFormat;
        _testMessage.Text = _testFields.Visibility == Visibility.Visible
            ? _rows.FirstOrDefault(row => row.Enabled && row.Command.Length > 0)?.Command ?? ""
            : "";
        _commandList.SelectedIndex = -1;
        _dirty = false; _loading = false; SetAutosaveStatus("Изменения сохраняются автоматически."); UpdateDisplay();
    }

    internal Task FlushPendingChangesAsync() => _dirty || _autosaving ? SaveSettingsAsync() : Task.CompletedTask;

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
                    draft.YouTubeVoteWeight = Parse(_youTubeWeight.Text, "Вес голоса чата"); draft.AllowYouTubeVoteChange = _youTubeChange.IsChecked == true;
                    draft.Generation = CaptureGeneration();
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

    private async Task RenameCollectionAsync(string collectionId)
    {
        if (IsEditingCollection(collectionId)) await SaveSettingsAsync();
        if (FindCollection(collectionId) is not { } round) return;
        var dialog = new PresetNameWindow("Заголовок голосования", round.Title) { Owner = HostWindow };
        if (AppDialogs.ShowModal(dialog) == true && round.ProfileId == _runtime.Settings.ActiveProfileId)
            await _runtime.Voting.RenameAsync(dialog.PresetName, collectionId);
    }

    private async Task ConfirmResetAsync(string collectionId)
    {
        if (FindCollection(collectionId) is { } round &&
            VotingConfirmationWindow.Confirm(HostWindow, "Сбросить прогресс", $"«{round.Title}»\n\n{ResetConfirmation}", "Сбросить"))
            await ResetCollectionAsync(collectionId);
    }

    internal async Task ResetCollectionAsync(string collectionId)
    {
        if (IsEditingCollection(collectionId)) await SaveSettingsAsync();
        if (_profileId != _runtime.Settings.ActiveProfileId || FindCollection(collectionId) is null) return;
        await _runtime.ResetVotingAsync(collectionId);
        if (IsEditingCollection(collectionId)) LoadDraft();
        _message.Text = FindCollection(collectionId)?.Notice ?? string.Empty;
    }

    private async Task EditHudPositionAsync()
    {
        await SaveSettingsAsync();
        var round = ProfileCollections.FirstOrDefault(item => item.IsActive) ?? SelectedRound;
        var editor = new VotingHudPositionWindow(round, _runtime.Voting.Automation) { Owner = HostWindow };
        if (editor.ShowDialog() != true) return;
        await _runtime.Voting.SaveHudPositionAsync(editor.PositionX, editor.PositionY);
        await _runtime.AdvanceVotingHudAsync();
        _message.Text = "Положение HUD сохранено. Оно применяется в игре автоматически при подключении.";
    }

    private async Task ConfirmDeleteAsync(string collectionId)
    {
        if (FindCollection(collectionId) is { } round &&
            VotingConfirmationWindow.Confirm(HostWindow, "Удалить голосование", DeleteConfirmation(round), "Удалить"))
            await DeleteCollectionAsync(collectionId);
    }

    internal async Task DeleteCollectionAsync(string collectionId)
    {
        await _saveGate.WaitAsync();
        try
        {
            if (_profileId != _runtime.Settings.ActiveProfileId || FindCollection(collectionId) is null) return;
            await _runtime.DeleteVotingAsync(collectionId);
            if (_selectedId == collectionId)
            {
                _selectedId = ProfileCollections.FirstOrDefault()?.CollectionId ?? string.Empty;
                if (!_generalSelected)
                {
                    _generalSelected = _selectedId.Length == 0;
                    LoadDraft();
                }
            }
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

    private async Task SpinAsync(string collectionId, string? pendingRoundId, bool fromPlayer = false)
    {
        if (!_generalSelected && collectionId == _selectedId) await SaveSettingsAsync();
        var round = ProfileCollections.FirstOrDefault(item => item.CollectionId == collectionId);
        if (round is null || fromPlayer && !round.IsActive) return;
        if (pendingRoundId is not null)
        {
            await _runtime.SubmitVotingWinnerAsync(roundId: pendingRoundId);
            return;
        }
        if (round.Phase != VotingPhase.Ready) return;
        var started = false;
        try
        {
            await _runtime.BeginVotingDrawAsync(collectionId: collectionId);
            started = true;
        }
        finally
        {
            if (started) await _runtime.FinishVotingDrawAsync();
            UpdateDisplay();
        }
    }

    private static string ActivityCaption(VotingRound round) => round switch
    {
        { IsActive: true, Phase: VotingPhase.Collecting } => "Пауза",
        { IsActive: true } => "Отключить",
        { Phase: VotingPhase.Paused } => "Продолжить",
        _ => "Запустить"
    };

    private bool CanChangeActivity(VotingRound round) => !_runtime.VotingIsSpinning ||
        round is { IsActive: true, Phase: VotingPhase.Collecting };

    private async Task ChangeCollectionActivityAsync(string collectionId, bool activate)
    {
        if (activate && IsEditingCollection(collectionId)) await SaveSettingsAsync();
        if (FindCollection(collectionId) is not { } target || target.ProfileId != _runtime.Settings.ActiveProfileId ||
            target.IsActive == activate || !CanChangeActivity(target)) return;

        if (activate)
        {
            _runtime.Voting.ValidateCollectionStart(target, _profileId);
            if (ProfileCollections.FirstOrDefault(round => round.CollectionId != collectionId &&
                    round is { IsActive: true, Phase: VotingPhase.Collecting }) is { } active &&
                !VotingConfirmationWindow.Confirm(HostWindow, "Переключить голосование?",
                    $"«{active.Title}» встанет на паузу. Прогресс и оставшееся время сохранятся.", ActivityCaption(target)))
                return;
            if (target.ProfileId != _runtime.Settings.ActiveProfileId || !CanChangeActivity(target)) return;
        }

        await _runtime.Voting.SetCollectionActiveAsync(collectionId, activate, target.ProfileId);
        if (target.ProfileId == _profileId) _pausedPlayerId = activate ? null : collectionId;
    }

    private void VotingChanged()
    {
        if (_closed || Dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _displayQueued, 1) != 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref _displayQueued, 0);
            if (!_closed) UpdateDisplay();
        }));
    }

    private void ApplyPanelLayout()
    {
        _settingsCard.Visibility = _settingsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        _commandsPanel.Visibility = _narrow && !_settingsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        _settingsColumn.Width = new GridLength(_settingsCollapsed || _narrow ? 0 : 266);
        Grid.SetColumn(_settingsCard, _narrow ? 0 : 1);
        _panelToggle.Tag = _settingsCollapsed ? "\uE70D" : "\uE70E";
        if (_settingsCollapsed)
        {
            _panelToggle.ClearValue(Control.BackgroundProperty);
            _panelToggle.ClearValue(Control.ForegroundProperty);
            _panelToggle.ClearValue(Control.BorderBrushProperty);
        }
        else
        {
            _panelToggle.SetResourceReference(Control.BackgroundProperty, "WarningPanelBrush");
            _panelToggle.SetResourceReference(Control.ForegroundProperty, "AccentForegroundBrush");
            _panelToggle.SetResourceReference(Control.BorderBrushProperty, "WarningBorderBrush");
        }
        _panelToggle.ToolTip = _settingsCollapsed ? "Показать настройки" : "Скрыть настройки";
        System.Windows.Automation.AutomationProperties.SetName(_panelToggle, (string)_panelToggle.ToolTip);
    }

    private void UpdatePlayer(VotingRound? active, IReadOnlyList<VotingRound> rounds, IReadOnlyDictionary<string, VotingRound> pending, DateTimeOffset now, bool canUse)
    {
        if (active is not null || !rounds.Any(item => item.CollectionId == _pausedPlayerId && item.Phase == VotingPhase.Paused))
            _pausedPlayerId = null;
        var round = active ?? rounds.FirstOrDefault(item => item.CollectionId == _pausedPlayerId);
        _playerCollectionId = round?.CollectionId;
        var next = _runtime.Voting.PreviewNextSequenceCollection(_profileId, _playerCollectionId);
        _playerNext.IsEnabled = canUse && !_runtime.VotingIsSpinning && next.HasNext;
        _playerNext.ToolTip = next switch
        {
            (true, { } title) => $"Следующее голосование · {title}",
            (true, null) => "Случайное голосование из автоматической очереди",
            _ => "Нет следующего настроенного голосования в автоматической очереди"
        };

        var hasRound = round is not null ? Visibility.Visible : Visibility.Collapsed;
        _playerTitle.Visibility = _playerProgress.Visibility = hasRound;
        _playerToggle.Visibility = round is null || round.Phase == VotingPhase.Paused && IsEditingCollection(round.CollectionId)
            ? Visibility.Collapsed : Visibility.Visible;
        _playerEmpty.Visibility = round is null ? Visibility.Visible : Visibility.Collapsed;
        _playerState.Text = round is null ? "Сейчас в эфире" : round.Phase switch
        {
            VotingPhase.Collecting => "Сейчас в эфире",
            VotingPhase.Paused => "На паузе",
            VotingPhase.Ready => "Готово к розыгрышу",
            VotingPhase.Failed => "Голосование завершено",
            VotingPhase.Drawn => _runtime.VotingDrawingCollectionId == round.CollectionId ? "Идёт розыгрыш" : "Победитель · " + round.Winner?.Command,
            _ => "Ожидает настройки"
        };
        _playerState.SetResourceReference(TextBlock.ForegroundProperty, active is null ? "MutedBrush" : "GoodBrush");
        if (round is null)
        {
            _spin.Visibility = Visibility.Collapsed;
            return;
        }

        _playerName.Text = round.Title;
        _playerTitle.ToolTip = "Открыть «" + round.Title + "»";
        System.Windows.Automation.AutomationProperties.SetName(_playerTitle, (string)_playerTitle.ToolTip);
        _total.Text = round.MoneyGoal ? FormatMoney(round.Total, round.Currency, round.Target) : "";
        _total.ToolTip = _total.Text;
        _amountMetric.Visibility = round.MoneyGoal ? Visibility.Visible : Visibility.Collapsed;
        _remaining.Text = VotingProgressDisplay.Remaining(round, now);
        _timeMetric.Visibility = string.IsNullOrEmpty(_remaining.Text) ? Visibility.Collapsed : Visibility.Visible;
        _progress.Value = (double)VotingProgressDisplay.Percent(round, now);
        (_playerToggle.Content, _playerToggle.ToolTip) = (round.IsActive, round.Phase) switch
        {
            (false, _) => ("\uE768", "Продолжить голосование"),
            (true, VotingPhase.Collecting) => ("\uE769", "Поставить на паузу"),
            _ => ("\uE71A", "Отключить голосование")
        };
        System.Windows.Automation.AutomationProperties.SetName(_playerToggle, (string)_playerToggle.ToolTip);
        _playerToggle.IsEnabled = canUse && CanChangeActivity(round);
        UpdateSpinButton(_spin, round, pending.GetValueOrDefault(round.CollectionId), canUse && round.IsActive);
    }

    private void UpdateSpinButton(Button button, VotingRound round, VotingRound? pending, bool canUse)
    {
        var waitingForGame = pending is { GamePresentationRequired: true, GamePresentationCompleted: false };
        button.CommandParameter = pending?.Id;
        button.Content = waitingForGame ? "Ожидает игровую рулетку" : pending is not null ? "Активировать эффект победителя" : "Крутить";
        button.Visibility = pending is not null || round.Phase != VotingPhase.Drawn ? Visibility.Visible : Visibility.Collapsed;
        button.IsEnabled = canUse && !_runtime.VotingIsSpinning && !waitingForGame && (pending is not null || round.Phase == VotingPhase.Ready);
        button.ToolTip = pending is not null ? pending.Winner?.Command + " → " + pending.Winner?.Preset?.DisplayName
            : button.IsEnabled ? "Разыграть голосование по накопленным долям" : "Крутить можно после выполнения условий голосования";
    }

    private void UpdateDisplay()
    {
        var rounds = ProfileCollections;
        var round = rounds.FirstOrDefault(item => item.CollectionId == _selectedId);
        var active = rounds.FirstOrDefault(item => item.IsActive);
        var error = _runtime.VotingError;
        var canUse = !_busy && error.Length == 0;
        var pending = _runtime.Voting.PendingWinners.Where(item => item.ProfileId == _profileId)
            .GroupBy(item => item.CollectionId).ToDictionary(group => group.Key, group => group.First());
        var now = DateTimeOffset.UtcNow;
        UpdateNavigation(rounds, active);
        UpdatePlayer(active, rounds, pending, now, canUse);

        var editing = !_generalSelected && round is not null;
        _generalPanel.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        _collectionPanel.Visibility = _headerActions.Visibility = _rename.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        _collectionToggle.Visibility = editing && !round!.IsActive ? Visibility.Visible : Visibility.Collapsed;
        _title.Text = editing ? round!.Title : "Голосование: Общие настройки";
        _title.ToolTip = _title.Text;
        _statusDot.Visibility = editing && round!.IsActive ? Visibility.Visible : Visibility.Collapsed;
        _collectionToggleLabel.Visibility = _compactSummary.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        _automationStatus.Text = active is null ? "Голосования выключены. Включите нужное голосование на его странице." : "Активно: " + active.Title;
        _automationStatus.SetResourceReference(TextBlock.ForegroundProperty, active is null ? "MutedBrush" : "GoodBrush");
        _automaticDraw.IsEnabled = _order.IsEnabled = _next.IsEnabled = _winnerAuto.IsEnabled = canUse;
        _gameHudTemplateNames.IsEnabled = canUse && _gameHud.IsChecked == true && _gameHudCommands.IsChecked == true;
        if (error.Length > 0) _saveProblem.Text = error;
        ApplyPanelLayout();
        if (!editing || round is null) return;

        var canEdit = canUse && round.CanEdit;
        _reset.IsEnabled = _delete.IsEnabled = _rename.IsEnabled = canUse;
        var activityCaption = round.Phase == VotingPhase.Paused ? "Продолжить" : "Старт";
        _collectionToggle.Content = activityCaption;
        _collectionToggle.ToolTip = $"{activityCaption} «{round.Title}»";
        System.Windows.Automation.AutomationProperties.SetName(_collectionToggle, (string)_collectionToggle.ToolTip);
        _collectionToggle.IsEnabled = canUse && CanChangeActivity(round);
        _collectionToggleLabel.Text = round.Phase switch
        {
            VotingPhase.Collecting => "В эфире",
            VotingPhase.Paused => "На паузе",
            VotingPhase.Ready => "Готово к розыгрышу",
            VotingPhase.Drawn or VotingPhase.Failed => "Завершено",
            _ => "Отключено"
        };
        _collectionToggleLabel.SetResourceReference(TextBlock.ForegroundProperty, round.IsActive ? "GoodBrush" : "MutedBrush");
        _settings.IsEnabled = error.Length == 0;
        _money.IsEnabled = _time.IsEnabled = _currency.IsEnabled = canEdit;
        _donationVotes.IsEnabled = _youTubeVotes.IsEnabled = canEdit;
        _youTubeWeight.IsEnabled = _youTubeChange.IsEnabled = canEdit && _youTubeVotes.IsChecked == true;
        _targetCurrency.Text = _youTubeCurrency.Text = CurrencySuffix(_currency.SelectedValue as string ?? round.Currency);
        _target.IsEnabled = canEdit && _money.IsChecked == true;
        _minutes.IsEnabled = canEdit && _time.IsChecked == true;
        _conditionRow.Visibility = _money.IsChecked == true && _time.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _condition.IsEnabled = canEdit;
        UpdateGeneration(round, canEdit);
        var amounts = round.Options.ToDictionary(option => option.Id, option => option.Amount);
        var total = _rows.Where(row => row.Enabled).Sum(row => amounts.GetValueOrDefault(row.Model.Id));
        foreach (var row in _rows)
        {
            row.CanEdit = canEdit && _generationToggle.IsChecked != true;
            row.Update(amounts.GetValueOrDefault(row.Model.Id), total, round.Currency);
        }
        var summary = _settingsCollapsed ? CompactConditions(round, now) : GoalSummary(round);
        _compactSummary.Text = summary.Length == 0 ? "" : "·  " + summary;
        _compactSummary.ToolTip = summary;
        var selectedPending = pending.GetValueOrDefault(round.CollectionId);
        UpdateSpinButton(_selectedSpin, round, selectedPending, canUse);
        if (_playerCollectionId == round.CollectionId || selectedPending is null && round.Phase != VotingPhase.Ready)
            _selectedSpin.Visibility = Visibility.Collapsed;

        var details = new List<string>();
        if (round.Phase is (VotingPhase.Ready or VotingPhase.Failed or VotingPhase.Drawn) && round.Notice.Length > 0) details.Add(round.Notice);
        if (round.Winner is { } winner) details.Add("Победитель: " + winner.Command + " → " + winner.Preset?.DisplayName);
        if (round.DeliveryNote.Length > 0) details.Add(round.DeliveryNote);
        if (selectedPending is not null && selectedPending.Id != round.Id)
            details.Add("Ожидает активации: " + selectedPending.Winner?.Command + " → " + selectedPending.Winner?.Preset?.DisplayName);
        _status.Text = string.Join("\n", details);
        _add.IsEnabled = canEdit && _rows.Count < 200;
        _test.IsEnabled = canUse && round.Phase == VotingPhase.Collecting;
    }

    private async Task RunAsync(Func<Task> operation)
    {
        if (_runtime.VotingError.Length > 0) { _message.Text = _runtime.VotingError; return; }
        if (_busy) return; _busy = true; UpdateDisplay();
        try { await operation(); }
        catch (Exception error) { _message.Text = error.Message; }
        finally { _busy = false; UpdateDisplay(); }
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
    private static string CurrencySuffix(string currency) => currency switch { "RUB" => "₽", "USD" => "$", "EUR" => "€", _ => currency };
    internal static string FormatMoney(decimal amount, string currency, decimal? target = null)
    {
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        var text = amount.ToString("#,0.##", culture);
        if (target is { } goal) text += " / " + goal.ToString("#,0.##", culture);
        return text + " " + CurrencySuffix(currency);
    }
    private static string GoalSummary(VotingRound round)
    {
        var goals = new List<string>(2);
        if (round.MoneyGoal) goals.Add(FormatMoney(round.Target, round.Currency));
        if (round.TimeGoal)
        {
            var minutes = round.DurationSeconds / 60;
            var seconds = round.DurationSeconds % 60;
            goals.Add(seconds == 0 ? $"{minutes} мин" : $"{minutes}:{seconds:00}");
        }
        return string.Join(round.Condition == VotingCondition.All ? " и " : " или ", goals);
    }
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
    public static string RemainingText(VotingRound round) => VotingProgressDisplay.Remaining(round, DateTimeOffset.UtcNow);
}
