using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.App.ViewModels;

public sealed record ActionCategory(string Id, string Label);
public sealed record BrowserViewOption(int Columns, string Label);

public sealed class ActivePriceGroupViewModel : ObservableObject
{
    private bool _isExpanded;
    private string _displayName;
    private readonly Action<bool> _expandedChanged;

    internal ActivePriceGroupViewModel(
        string key,
        IReadOnlyList<ActionViewModel> actions,
        int displayOrdinal,
        string displayName,
        bool isExpanded,
        Action<bool> expandedChanged)
    {
        Key = key;
        Actions = actions;
        DisplayOrdinal = displayOrdinal;
        _displayName = displayName;
        _isExpanded = isExpanded;
        _expandedChanged = expandedChanged;
        SharedTriggers = actions[0].Triggers.Where(trigger => trigger.Model.Enabled).ToArray();
    }

    public string Key { get; }
    public IReadOnlyList<ActionViewModel> Actions { get; }
    public IReadOnlyList<TriggerRuleViewModel> SharedTriggers { get; }
    public int DisplayOrdinal { get; }
    public string DisplayOrdinalLabel => $"№{DisplayOrdinal}";
    public string DisplayName
    {
        get => _displayName;
        internal set => SetProperty(ref _displayName, value);
    }
    public string CountLabel => $"Шаблонов: {Actions.Count}";
    public string Description => $"Одна сумма запускает {Actions.Count} активных шаблона";
    public bool CanTest => SharedTriggers.Count > 0 && Actions.All(action => action.CanTest);
    public bool StrictSequence { get; internal set; }
    public int GroupDelaySeconds { get; internal set; }
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
        VisibleActions = new ListCollectionView(actions) { Filter = MatchesFilter };
        VisibleEntries = [];
        foreach (var action in actions) Subscribe(action);
        actions.CollectionChanged += ActionsChanged;
        RebuildVisibleEntries();
    }

    public ICollectionView VisibleActions { get; }
    public ObservableCollection<ActiveActionListEntryViewModel> VisibleEntries { get; }
    public IReadOnlyDictionary<string, string> PriceGroupNames => _priceGroupNames;
    // Presentation only: never persist this query or change action membership.
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? string.Empty)) return;
            VisibleActions.Refresh();
            RebuildVisibleEntries();
        }
    }
    public ActionCategory SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (value is null || !Categories.Contains(value) || !SetProperty(ref _selectedCategory, value)) return;
            VisibleActions.Refresh();
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
        _priceGroupNames.Clear();
        if (names is not null)
        {
            foreach (var (key, value) in names)
            {
                var normalized = value.Trim();
                if (!string.IsNullOrWhiteSpace(key) && normalized.Length > 0)
                    _priceGroupNames[key] = normalized[..Math.Min(normalized.Length, 160)];
            }
        }
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
        if (change.OldItems is not null)
            foreach (ActionViewModel action in change.OldItems) Unsubscribe(action);
        if (change.NewItems is not null)
            foreach (ActionViewModel action in change.NewItems) Subscribe(action);
        VisibleActions.Refresh();
        RebuildVisibleEntries();
    }

    private void Subscribe(ActionViewModel action) => action.TriggersChanged += RebuildVisibleEntries;
    private void Unsubscribe(ActionViewModel action) => action.TriggersChanged -= RebuildVisibleEntries;

    private void RebuildVisibleEntries()
    {
        foreach (var action in _actions) action.SetSequenceOrdinal(0);
        var visible = VisibleActions.Cast<ActionViewModel>().ToList();
        VisibleEntries.Clear();
        if (!_groupEqualPrices)
        {
            foreach (var action in visible)
            {
                action.SetVisualDisplayOrdinal(action.DisplayOrdinal);
                VisibleEntries.Add(ActiveActionListEntryViewModel.Single(action));
            }
            return;
        }

        foreach (var action in _actions) action.SetVisualDisplayOrdinal(0);
        var keyed = visible
            .Select(action => (Action: action, Key: PriceSignature(action)))
            .ToList();
        var groups = keyed
            .Where(item => item.Key is not null)
            .GroupBy(item => item.Key!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.Select(item => item.Action).ToList(), StringComparer.Ordinal);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var displayOrdinal = 1;
        foreach (var item in keyed)
        {
            if (item.Key is not null && groups[item.Key].Count >= 2)
            {
                if (!emitted.Add(item.Key)) continue;
                var actions = groups[item.Key];
                foreach (var action in actions) action.SetVisualDisplayOrdinal(0);
                var allMembers = _actions.Where(action => PriceSignature(action) == item.Key).ToList();
                foreach (var action in actions) action.SetSequenceOrdinal(allMembers.IndexOf(action) + 1);
                var group = new ActivePriceGroupViewModel(
                    item.Key,
                    actions,
                    displayOrdinal++,
                    GroupName(item.Key, actions.Count),
                    _expandedPriceGroups.TryGetValue(item.Key, out var expanded) && expanded,
                    value => _expandedPriceGroups[item.Key] = value)
                {
                    StrictSequence = allMembers.Any(action => action.Model.StrictSequence),
                    GroupDelaySeconds = ActionPriceOrdering.ResolveGroupDelay(allMembers.Select(action => action.Model))
                };
                VisibleEntries.Add(ActiveActionListEntryViewModel.PriceGroup(group));
                continue;
            }

            item.Action.SetVisualDisplayOrdinal(displayOrdinal++);
            VisibleEntries.Add(ActiveActionListEntryViewModel.Single(item.Action));
        }
    }

    private static string? PriceSignature(ActionViewModel action)
        => ActionPriceOrdering.GroupKey(action.Model);

    private string GroupName(string key, int count) =>
        _priceGroupNames.TryGetValue(key, out var name) ? name : DefaultGroupName(count);

    private static string DefaultGroupName(int count) => $"Шаблонов: {count}";

    private bool MatchesFilter(object item) => item is ActionViewModel action &&
        action.DisplayName.Contains(SearchText.Trim(), StringComparison.CurrentCultureIgnoreCase) &&
        (SelectedCategory.Id == "all" ||
         (HasCanonicalSpawnCategory(action.HandlerId) || string.IsNullOrWhiteSpace(action.Model.CategoryId)
             ? CategoryFor(action.HandlerId)
             : action.Model.CategoryId) == SelectedCategory.Id);

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
