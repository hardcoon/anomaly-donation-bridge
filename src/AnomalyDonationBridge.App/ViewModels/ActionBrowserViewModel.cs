using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Threading;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.App.ViewModels;

public sealed record ActionCategory(string Id, string Label);
public sealed record BrowserViewOption(int Columns, string Label);

public sealed class ActivePriceGroupViewModel : ObservableObject
{
    private bool _isExpanded;
    private string _displayName;
    private IReadOnlyList<ActionViewModel> _actions;
    private IReadOnlyList<TriggerRuleViewModel> _sharedTriggers;
    private int _displayOrdinal;
    private bool _strictSequence;
    private int _groupDelaySeconds;
    private readonly Action<bool> _expandedChanged;

    internal ActivePriceGroupViewModel(
        string key,
        IReadOnlyList<ActionViewModel> actions,
        int displayOrdinal,
        string displayName,
        bool isExpanded,
        Action<bool> expandedChanged,
        bool deferRendering = false)
    {
        Key = key;
        _actions = actions;
        RenderedActions = new(deferRendering ? [] : actions);
        _displayOrdinal = displayOrdinal;
        _displayName = displayName;
        _isExpanded = isExpanded;
        _expandedChanged = expandedChanged;
        _sharedTriggers = actions[0].Triggers.Where(trigger => trigger.Model.Enabled).ToArray();
    }

    public string Key { get; }
    public IReadOnlyList<ActionViewModel> Actions => _actions;
    public ObservableCollection<ActionViewModel> RenderedActions { get; }
    public IReadOnlyList<TriggerRuleViewModel> SharedTriggers => _sharedTriggers;
    public int DisplayOrdinal => _displayOrdinal;
    public string DisplayOrdinalLabel => $"№{DisplayOrdinal}";
    public string DisplayName
    {
        get => _displayName;
        internal set => SetProperty(ref _displayName, value);
    }
    public string CountLabel => $"Шаблонов: {Actions.Count}";
    public string Description => $"Одна сумма запускает {Actions.Count} активных шаблона";
    public bool CanTest => SharedTriggers.Count > 0 && Actions.All(action => action.CanTest);
    public bool StrictSequence { get => _strictSequence; internal set => SetProperty(ref _strictSequence, value); }
    public int GroupDelaySeconds { get => _groupDelaySeconds; internal set => SetProperty(ref _groupDelaySeconds, value); }
    internal void Refresh(IReadOnlyList<ActionViewModel> actions, int ordinal, string name, bool strict, int delay)
    {
        if (!Actions.SequenceEqual(actions))
        {
            _actions = actions;
            RaisePropertyChanged(nameof(Actions));
            RaisePropertyChanged(nameof(CountLabel));
            RaisePropertyChanged(nameof(Description));
        }
        if (_displayOrdinal != ordinal)
        {
            _displayOrdinal = ordinal;
            RaisePropertyChanged(nameof(DisplayOrdinal));
            RaisePropertyChanged(nameof(DisplayOrdinalLabel));
        }
        var shared = actions[0].Triggers.Where(trigger => trigger.Model.Enabled).ToArray();
        if (!SharedTriggers.SequenceEqual(shared))
        {
            _sharedTriggers = shared;
            RaisePropertyChanged(nameof(SharedTriggers));
        }
        DisplayName = name;
        StrictSequence = strict;
        GroupDelaySeconds = delay;
        NotifyAvailabilityChanged();
    }
    internal void NotifyAvailabilityChanged() => RaisePropertyChanged(nameof(CanTest));
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value)) return;
            _expandedChanged(value);
        }
    }
}

public sealed class ActiveActionListEntryViewModel
{
    private ActiveActionListEntryViewModel(ActionViewModel? action, ActivePriceGroupViewModel? group)
    {
        Action = action;
        Group = group;
    }

    public ActionViewModel? Action { get; }
    public ActivePriceGroupViewModel? Group { get; }
    public bool IsGroup => Group is not null;

    internal static ActiveActionListEntryViewModel Single(ActionViewModel action) => new(action, null);
    internal static ActiveActionListEntryViewModel PriceGroup(ActivePriceGroupViewModel group) => new(null, group);
}

public sealed class ActionBrowserViewModel : ObservableObject
{
    // -1 is a compact list; 0 lets the panel choose its grid density.
    public static IReadOnlyList<BrowserViewOption> ViewOptions { get; } =
    [new(-1, "Компактный список"), new(0, "Сетка: авто"), new(1, "1 колонка"),
     new(2, "2 колонки"), new(3, "3 колонки"), new(4, "4 колонки"), new(5, "5 колонок")];

    public static IReadOnlyList<ActionCategory> Categories { get; } =
    [new("all", "Все"), new("npc_mutants", "NPC и мутанты"), new("simulation", "Симуляция"), new("player_state", "Состояния игрока"), new("metamorphosis", "Метаморфоз"), new("inventory", "Инвентарь"), new("world", "Мировые события")];

    private ActionCategory _selectedCategory = Categories[0];
    private int _columns = 2;
    private bool _automaticColumns;
    private bool _compactMode;
    private string _searchText = string.Empty;
    private readonly ObservableCollection<ActionViewModel> _actions;
    private readonly ObservableCollection<ActionViewModel> _visibleActions = [];
    private readonly Dictionary<ActionViewModel, ActiveActionListEntryViewModel> _singleEntries = [];
    private readonly Dictionary<string, ActiveActionListEntryViewModel> _groupEntries = new(StringComparer.Ordinal);
    private readonly HashSet<ActionViewModel> _subscribed = [];
    private CancellationTokenSource? _searchCancellation;
    private SearchItem[] _searchItems = [];
    private bool _sourceDirty;
    private bool _isUpdating;
    private bool _presentationCurrent;
    private bool _hasBeenDisplayed;
    private string _appliedQuery = string.Empty;
    private string _appliedCategory = string.Empty;
    internal bool DeferPresentation { get; set; }
    internal event Action? PresentationInvalidated;
    private readonly bool _groupEqualPrices;
    private readonly Dictionary<string, bool> _expandedPriceGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _priceGroupNames = new(StringComparer.Ordinal);
    public event Action? PreferencesChanged;

    public ActionBrowserViewModel(
        ObservableCollection<ActionViewModel> actions,
        bool groupEqualPrices = false)
    {
        _actions = actions;
        _groupEqualPrices = groupEqualPrices;
        // Do not use the shared default view: Active and Presets have independent filters.
        VisibleActions = new ListCollectionView(_visibleActions);
        VisibleEntries = [];
        foreach (var action in actions) Subscribe(action);
        actions.CollectionChanged += ActionsChanged;
        RebuildVisibleEntries();
    }

    public ICollectionView VisibleActions { get; }
    public bool IsUpdating { get => _isUpdating; private set => SetProperty(ref _isUpdating, value); }
    public ObservableCollection<ActiveActionListEntryViewModel> VisibleEntries { get; }
    public IReadOnlyDictionary<string, string> PriceGroupNames => _priceGroupNames;
    // Presentation only: never persist this query or change action membership.
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? string.Empty)) return;
            RebuildVisibleEntries();
        }
    }
    public ActionCategory SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (value is null || !Categories.Contains(value) || !SetProperty(ref _selectedCategory, value)) return;
            RebuildVisibleEntries();
            PreferencesChanged?.Invoke();
        }
    }
    public int Columns
    {
        get => _columns;
        set
        {
            if (!SetProperty(ref _columns, Math.Clamp(value, 1, 5))) return;
            RaisePropertyChanged(nameof(EffectiveColumns));
            RaisePropertyChanged(nameof(GridLabel));
            RaisePropertyChanged(nameof(SelectedView));
            PreferencesChanged?.Invoke();
        }
    }
    public bool AutomaticColumns
    {
        get => _automaticColumns;
        set
        {
            if (!SetProperty(ref _automaticColumns, value)) return;
            RaisePropertyChanged(nameof(EffectiveColumns));
            RaisePropertyChanged(nameof(GridLabel));
            RaisePropertyChanged(nameof(SelectedView));
            PreferencesChanged?.Invoke();
        }
    }
    public int EffectiveColumns => AutomaticColumns ? 0 : Columns;
    public string GridLabel => AutomaticColumns ? "Сетка: авто" : $"Сетка: {Columns}";
    public bool CompactMode
    {
        get => _compactMode;
        set
        {
            if (!SetProperty(ref _compactMode, value)) return;
            RaisePropertyChanged(nameof(SelectedView));
            PreferencesChanged?.Invoke();
        }
    }
    public BrowserViewOption SelectedView
    {
        get => ViewOptions.Single(option => option.Columns == (CompactMode ? -1 : EffectiveColumns));
        set
        {
            if (value is null || !ViewOptions.Contains(value) || value == SelectedView) return;
            // One selection is one atomic preference change; keep the last grid width
            // while in compact/auto mode for compatibility with existing preferences.
            _compactMode = value.Columns == -1;
            if (!_compactMode) _automaticColumns = value.Columns == 0;
            if (value.Columns > 0) _columns = value.Columns;
            RaisePropertyChanged(nameof(CompactMode));
            RaisePropertyChanged(nameof(AutomaticColumns));
            RaisePropertyChanged(nameof(Columns));
            RaisePropertyChanged(nameof(EffectiveColumns));
            RaisePropertyChanged(nameof(GridLabel));
            RaisePropertyChanged(nameof(SelectedView));
            PreferencesChanged?.Invoke();
        }
    }
    public void SelectCategory(string id, string legacyPlayerSubcategory = "all")
    {
        if (id == "player") id = legacyPlayerSubcategory == "inventory" ? "inventory" : "player_state";
        SelectedCategory = Categories.FirstOrDefault(category => category.Id == id) ?? Categories[0];
    }

    public void RestorePriceGroupNames(IReadOnlyDictionary<string, string>? names)
    {
        var normalizedNames = new Dictionary<string, string>(StringComparer.Ordinal);
        if (names is not null)
        {
            foreach (var (key, value) in names)
            {
                var normalized = value.Trim();
                if (!string.IsNullOrWhiteSpace(key) && normalized.Length > 0)
                    normalizedNames[key] = normalized[..Math.Min(normalized.Length, 160)];
            }
        }
        if (_priceGroupNames.Count == normalizedNames.Count && normalizedNames.All(pair =>
            _priceGroupNames.TryGetValue(pair.Key, out var value) && value == pair.Value)) return;
        _priceGroupNames.Clear();
        foreach (var pair in normalizedNames) _priceGroupNames.Add(pair.Key, pair.Value);
        RebuildVisibleEntries();
    }

    public void RenamePriceGroup(ActivePriceGroupViewModel group, string name)
    {
        ArgumentNullException.ThrowIfNull(group);
        var normalized = name.Trim();
        if (normalized.Length == 0) throw new ArgumentException("Название группы не может быть пустым.", nameof(name));
        normalized = normalized[..Math.Min(normalized.Length, 160)];
        var fallback = DefaultGroupName(group.Actions.Count);
        if (string.Equals(normalized, fallback, StringComparison.Ordinal)) _priceGroupNames.Remove(group.Key);
        else _priceGroupNames[group.Key] = normalized;
        group.DisplayName = normalized;
        PreferencesChanged?.Invoke();
    }

    public IReadOnlyList<ActionDefinition> BuildExportActions()
    {
        if (!_groupEqualPrices) return _actions.Select(action => action.Model).ToList();

        var keyed = _actions.Select(action => (Action: action, Key: PriceSignature(action))).ToList();
        var groups = keyed
            .Where(item => item.Key is not null)
            .GroupBy(item => item.Key!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Action).ToList(), StringComparer.Ordinal);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ActionDefinition>();
        foreach (var item in keyed)
        {
            if (item.Key is not null && groups[item.Key].Count >= 2)
            {
                if (!emitted.Add(item.Key)) continue;
                var members = groups[item.Key];
                var groupDefinition = members[0].Model.Clone();
                groupDefinition.DisplayName = GroupName(item.Key, members.Count);
                result.Add(groupDefinition);
            }
            else
            {
                result.Add(item.Action.Model);
            }
        }
        return result;
    }

    private void ActionsChanged(object? sender, NotifyCollectionChangedEventArgs change)
    {
        // Moving a source item does not remove its cached presentation wrapper.
        if (change.Action == NotifyCollectionChangedAction.Move)
        {
            RebuildVisibleEntries();
            return;
        }
        if (change.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var action in _subscribed.ToArray()) Unsubscribe(action);
            foreach (var action in _actions) Subscribe(action);
            if (DeferPresentation && _actions.Count == 0)
            {
                _visibleActions.Clear();
                VisibleEntries.Clear();
            }
        }
        if (change.OldItems is not null)
            foreach (ActionViewModel action in change.OldItems) Unsubscribe(action);
        if (change.NewItems is not null)
            foreach (ActionViewModel action in change.NewItems) Subscribe(action);
        if (DeferPresentation && change.OldItems is not null)
        {
            // Do not leave deleted/previous-profile commands clickable while
            // the asynchronous result is waiting for the next dispatcher turn.
            var removed = change.OldItems.Cast<ActionViewModel>().ToHashSet();
            foreach (var action in removed) _visibleActions.Remove(action);
            foreach (var entry in VisibleEntries.Where(entry => entry.Action is { } action && removed.Contains(action) ||
                entry.Group is { } group && group.Actions.Any(removed.Contains)).ToArray()) VisibleEntries.Remove(entry);
        }
        RebuildVisibleEntries();
    }

    private void Subscribe(ActionViewModel action)
    {
        if (!_subscribed.Add(action)) return;
        action.TriggersChanged += RebuildVisibleEntries;
        action.PropertyChanged += ActionPropertyChanged;
    }

    private void Unsubscribe(ActionViewModel action)
    {
        if (_subscribed.Remove(action))
        {
            action.TriggersChanged -= RebuildVisibleEntries;
            action.PropertyChanged -= ActionPropertyChanged;
        }
        _singleEntries.Remove(action);
    }

    private void ActionPropertyChanged(object? sender, PropertyChangedEventArgs change)
    {
        if (change.PropertyName != nameof(ActionViewModel.CanTest)) return;
        foreach (var entry in _groupEntries.Values) entry.Group!.NotifyAvailabilityChanged();
    }

    internal void InvalidateSource() => RebuildVisibleEntries();

    private void RebuildVisibleEntries()
    {
        _searchCancellation?.Cancel();
        _sourceDirty = true;
        _presentationCurrent = false;
        if (DeferPresentation)
        {
            _searchCancellation = null;
            IsUpdating = true;
            PresentationInvalidated?.Invoke();
            return;
        }
        _searchItems = Snapshot();
        _sourceDirty = false;
        var result = Filter(_searchItems, SearchText.Trim(), SelectedCategory.Id,
            CultureInfo.CurrentCulture.CompareInfo, _groupEqualPrices, CancellationToken.None);
        foreach (var _ in Synchronize(_visibleActions, result.Actions)) { }
        foreach (var _ in Synchronize(VisibleEntries, CreateEntries(result))) { }
        _presentationCurrent = true;
        _appliedQuery = SearchText.Trim();
        _appliedCategory = SelectedCategory.Id;
    }

    internal void PrepareForDisplay()
    {
        _searchCancellation?.Cancel();
        _searchCancellation = null;
        // The first opening may contain thousands of never-realized rows.
        // Populate that initial view in batches; retain it on later visits.
        if (!_hasBeenDisplayed)
        {
            _hasBeenDisplayed = true;
            _presentationCurrent = false;
            _visibleActions.Clear();
            VisibleEntries.Clear();
        }
        IsUpdating = _sourceDirty || !_presentationCurrent;
    }

    internal async Task SearchAsync(string query, CancellationToken token)
    {
        _searchCancellation?.Cancel();
        SetProperty(ref _searchText, query, nameof(SearchText));
        if (!_sourceDirty && _presentationCurrent && _appliedQuery == query.Trim() && _appliedCategory == SelectedCategory.Id)
        {
            _searchCancellation = null;
            IsUpdating = false;
            return;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _searchCancellation = cancellation;
        IsUpdating = true;
        _presentationCurrent = false;
        if (_sourceDirty)
        {
            _searchItems = Snapshot();
            _sourceDirty = false;
        }
        var snapshot = _searchItems;
        var category = SelectedCategory.Id;
        var culture = CultureInfo.CurrentCulture.CompareInfo;
        var dispatcher = Dispatcher.CurrentDispatcher;
        try
        {
            var result = await Task.Run(() => Filter(snapshot, query.Trim(), category, culture,
                _groupEqualPrices, cancellation.Token), cancellation.Token).ConfigureAwait(false);
            await dispatcher.InvokeAsync(async () =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var watch = Stopwatch.StartNew();
                var batchSize = 0;
                var entries = CreateEntries(result, deferRendering: true);
                var retained = entries.ToHashSet();
                var removedGroups = VisibleEntries.Where(entry => entry.IsGroup && !retained.Contains(entry)).ToArray();
                var changes = removedGroups.SelectMany(entry => Synchronize(entry.Group!.RenderedActions, []))
                    .Concat(Synchronize(_visibleActions, result.Actions))
                    .Concat(Synchronize(VisibleEntries, entries))
                    .Concat(entries.Where(entry => entry.IsGroup)
                        .SelectMany(entry => Synchronize(entry.Group!.RenderedActions, entry.Group.Actions)));
                foreach (var _ in changes)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    // WPF realizes templates at the next layout pass, after collection notifications.
                    // Limit the number too, even when those notifications themselves are inexpensive.
                    if (++batchSize < 2 && watch.ElapsedMilliseconds < 8) continue;
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    cancellation.Token.ThrowIfCancellationRequested();
                    batchSize = 0;
                    watch.Restart();
                }
                cancellation.Token.ThrowIfCancellationRequested();
                _presentationCurrent = true;
                _appliedQuery = query.Trim();
                _appliedCategory = category;
            }, DispatcherPriority.Background, cancellation.Token).Task.Unwrap().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            await dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_searchCancellation, cancellation))
                {
                    _searchCancellation = null;
                    IsUpdating = false;
                }
            });
        }
    }

    private sealed record SearchItem(ActionViewModel Action, string Name, string Category,
        string? PriceKey, bool Strict, int? Delay, int Ordinal);
    private sealed record SearchEntry(ActionViewModel[] Actions, string? GroupKey, int Ordinal, bool Strict, int Delay);
    private sealed record SearchResult(ActionViewModel[] Actions, SearchEntry[] Entries,
        Dictionary<ActionViewModel, (int Visual, int Sequence)> Ordinals);

    private SearchItem[] Snapshot() => _actions.Select(action => new SearchItem(action, action.DisplayName,
        HasCanonicalSpawnCategory(action.HandlerId) || string.IsNullOrWhiteSpace(action.Model.CategoryId)
            ? CategoryFor(action.HandlerId) : action.Model.CategoryId,
        _groupEqualPrices ? PriceSignature(action) : null, action.Model.StrictSequence,
        action.Model.GroupDelaySeconds, action.DisplayOrdinal)).ToArray();

    private static SearchResult Filter(SearchItem[] source, string query, string category, CompareInfo culture,
        bool groupPrices, CancellationToken token)
    {
        var visible = new List<SearchItem>();
        foreach (var item in source)
        {
            token.ThrowIfCancellationRequested();
            if ((category == "all" || item.Category == category) &&
                culture.IndexOf(item.Name, query, CompareOptions.IgnoreCase) >= 0) visible.Add(item);
        }
        var ordinals = source.ToDictionary(item => item.Action,
            item => (Visual: groupPrices ? 0 : item.Ordinal, Sequence: 0));
        var allGroups = source.Where(item => item.PriceKey is not null)
            .GroupBy(item => item.PriceKey!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var visibleGroups = visible.Where(item => item.PriceKey is not null)
            .GroupBy(item => item.PriceKey!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Action).ToArray(), StringComparer.Ordinal);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var visibleActions = visible.Select(item => item.Action).ToHashSet();
        var entries = new List<SearchEntry>();
        var displayOrdinal = 1;
        foreach (var item in visible)
        {
            token.ThrowIfCancellationRequested();
            if (item.PriceKey is { } key && visibleGroups[key].Length >= 2)
            {
                if (!emitted.Add(key)) continue;
                var members = allGroups[key];
                for (var index = 0; index < members.Length; index++)
                    if (visibleActions.Contains(members[index].Action)) ordinals[members[index].Action] = (0, index + 1);
                entries.Add(new(visibleGroups[key], key, displayOrdinal++, members.Any(member => member.Strict),
                    Math.Clamp(members.Max(member => member.Delay) ?? 1, 0, 86_400)));
                continue;
            }
            var ordinal = groupPrices ? displayOrdinal++ : item.Ordinal;
            ordinals[item.Action] = (ordinal, 0);
            entries.Add(new([item.Action], null, ordinal, false, 0));
        }
        return new(visible.Select(item => item.Action).ToArray(), entries.ToArray(), ordinals);
    }

    private ActiveActionListEntryViewModel[] CreateEntries(SearchResult result, bool deferRendering = false)
    {
        var currentGroupKeys = _searchItems.Where(item => item.PriceKey is not null).GroupBy(item => item.PriceKey!)
            .Where(group => group.Count() >= 2).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in _groupEntries.Keys.Where(key => !currentGroupKeys.Contains(key)).ToArray()) _groupEntries.Remove(key);
        foreach (var (action, ordinal) in result.Ordinals)
        {
            action.SetSequenceOrdinal(ordinal.Sequence);
            action.SetVisualDisplayOrdinal(ordinal.Visual);
        }
        var entries = new List<ActiveActionListEntryViewModel>();
        foreach (var entry in result.Entries)
        {
            if (entry.GroupKey is not { } key)
            {
                var action = entry.Actions[0];
                if (!_singleEntries.TryGetValue(action, out var single))
                    _singleEntries[action] = single = ActiveActionListEntryViewModel.Single(action);
                entries.Add(single);
                continue;
            }
            var name = GroupName(key, entry.Actions.Length);
            if (!_groupEntries.TryGetValue(key, out var cached) || cached.Group is not { } group ||
                !group.Actions.SequenceEqual(entry.Actions))
            {
                group = new ActivePriceGroupViewModel(key, entry.Actions, entry.Ordinal, name,
                    _expandedPriceGroups.TryGetValue(key, out var expanded) && expanded,
                    value => _expandedPriceGroups[key] = value, deferRendering)
                { StrictSequence = entry.Strict, GroupDelaySeconds = entry.Delay };
                _groupEntries[key] = cached = ActiveActionListEntryViewModel.PriceGroup(group);
            }
            else group.Refresh(entry.Actions, entry.Ordinal, name, entry.Strict, entry.Delay);
            if (!deferRendering)
                foreach (var _ in Synchronize(group.RenderedActions, group.Actions)) { }
            else if (!VisibleEntries.Contains(cached))
                group.RenderedActions.Clear();
            entries.Add(cached);
        }
        return entries.ToArray();
    }

    private static IEnumerable<bool> Synchronize<T>(ObservableCollection<T> target, IReadOnlyList<T> wanted) where T : class
        => CollectionSynchronization.Apply(target, wanted);

    private static string? PriceSignature(ActionViewModel action)
        => ActionPriceOrdering.GroupKey(action.Model);

    private string GroupName(string key, int count) =>
        _priceGroupNames.TryGetValue(key, out var name) ? name : DefaultGroupName(count);

    private static string DefaultGroupName(int count) => $"Шаблонов: {count}";

    private static bool HasCanonicalSpawnCategory(string handlerId) => handlerId is
        "spawn_npcs" or "spawn_mutants" or
        "spawn_npc_squads" or "spawn_mutant_squads" or
        "spawn_random_hostile_squad" or "spawn_random_mutant_pack" or
        "spawn_random_npcs" or "spawn_random_mutants" or "spawn_named_companion" or
        "spawn_mutant_companion" or "spawn_donor_boss" or "kill_companions";

    public static string CategoryFor(string handlerId) => handlerId switch
    {
        "hardcoon.adb.burer_form" => "metamorphosis",
        "hardcoon.adb.special_companion" => "npc_mutants",
        "spawn_npcs" or "spawn_mutants" or
        "spawn_random_npcs" or "spawn_random_mutants" or "spawn_named_companion" or
        "spawn_mutant_companion" or "spawn_donor_boss" or "kill_companions" => "npc_mutants",
        "spawn_npc_squads" or "spawn_mutant_squads" or
        "spawn_random_hostile_squad" or "spawn_random_mutant_pack" => "simulation",
        "add_radiation" or "change_health" or "change_needs" or "get_drunk" or "sleep_hours" or
        "god_mode" or "teleport_random" or "change_player_faction" or "actor_boost" or
        "actor_parameters" or "hide_hud" or "change_player_name" => "player_state",
        "drop_active_weapon" or "block_inventory" or "spawn_items" or "change_money" or "remove_equipment" or
        "damage_equipment" or "drop_random_items" or "use_random_consumable" => "inventory",
        // Общие рандомные шаблоны и расширения показываются в мировых событиях и в разделе «Все».
        _ => "world"
    };
}
