using System.Collections.ObjectModel;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.App.ViewModels;

public sealed record SafeZoneLocationOption(string LevelName, string DisplayName);
public sealed record SafeZonePolicyOption(SafeZoneBlockedAction Value, string Label);

public sealed class SafeZoneEffectViewModel(string handlerId, string displayName, bool selected) : ObservableObject
{
    private bool _isSelected = selected;
    public string HandlerId { get; } = handlerId;
    public string DisplayName { get; } = displayName;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}

public sealed class SafeZoneEffectGroupViewModel(string name, IReadOnlyList<SafeZoneEffectViewModel> effects) : ObservableObject
{
    public bool IsExpanded { get; set; } = name == "NPC и мутанты";
    public string Name { get; } = name;
    public IReadOnlyList<SafeZoneEffectViewModel> Effects { get; } = effects;
    public string SelectionLabel => $"{Effects.Count(effect => effect.IsSelected)} / {Effects.Count}";
    internal void Refresh() => RaisePropertyChanged(nameof(SelectionLabel));
}

public sealed class SafeZoneRuleViewModel : ObservableObject
{
    private bool _blockAll;
    private bool _isExpanded;
    public SafeZoneRuleViewModel(SafeZoneRule source, IReadOnlyList<ActionDefinition> effects)
    {
        LevelName = source.LevelName;
        DisplayName = source.DisplayName;
        _blockAll = source.BlockAll;
        var selected = source.HandlerIds.ToHashSet(StringComparer.Ordinal);
        var known = effects.Select(effect => effect.HandlerId).ToHashSet(StringComparer.Ordinal);
        var rows = effects.Select(effect => (Category: CategoryName(effect.CategoryId),
            Row: new SafeZoneEffectViewModel(effect.HandlerId, effect.DisplayName, selected.Contains(effect.HandlerId))))
            .Concat(selected.Where(id => !known.Contains(id)).Select(id =>
                (Category: "Недоступные шаблоны", Row: new SafeZoneEffectViewModel(id, id + " (не установлен)", true))));
        Groups = rows.GroupBy(row => row.Category).OrderBy(group => CategoryOrder(group.Key))
            .Select(group => new SafeZoneEffectGroupViewModel(group.Key,
                group.Select(row => row.Row).OrderBy(row => row.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList()))
            .ToList();
        foreach (var group in Groups)
            foreach (var row in group.Effects)
                row.PropertyChanged += (_, _) => { group.Refresh(); RefreshSummary(); };
    }

    public string LevelName { get; }
    public string DisplayName { get; }
    public bool IsExpanded
    {
        get => _isExpanded;
        internal set
        {
            if (SetProperty(ref _isExpanded, value)) RaisePropertyChanged(nameof(ConfigureLabel));
        }
    }
    public string ConfigureLabel => IsExpanded ? "Свернуть" : "Настроить";
    public IReadOnlyList<SafeZoneEffectGroupViewModel> Groups { get; }
    public bool BlockAll
    {
        get => _blockAll;
        set
        {
            if (!SetProperty(ref _blockAll, value)) return;
            RaisePropertyChanged(nameof(BlockSelected));
            RefreshSummary();
        }
    }
    public bool BlockSelected { get => !BlockAll; set { if (value) BlockAll = false; } }
    public int SelectedCount => Groups.Sum(group => group.Effects.Count(row => row.IsSelected));
    public int TotalCount => Groups.Sum(group => group.Effects.Count);
    public string Summary => BlockAll ? "Все эффекты" : $"Выбрано: {SelectedCount} из {TotalCount}";
    public string SelectionLabel => $"{SelectedCount} из {TotalCount}";
    private void RefreshSummary()
    {
        RaisePropertyChanged(nameof(Summary));
        RaisePropertyChanged(nameof(SelectedCount));
        RaisePropertyChanged(nameof(SelectionLabel));
    }
    public SafeZoneRule ToSettings() => new()
    {
        LevelName = LevelName, DisplayName = DisplayName, BlockAll = BlockAll,
        HandlerIds = Groups.SelectMany(group => group.Effects).Where(row => row.IsSelected).Select(row => row.HandlerId).ToList()
    };
    private static string CategoryName(string id) => id switch
    {
        "npc_mutants" => "NPC и мутанты", "simulation" => "Симуляция", "player_state" => "Состояния игрока",
        "metamorphosis" => "Метаморфоз", "inventory" => "Инвентарь", "world" => "Мировые события", _ => "Другие эффекты"
    };
    private static int CategoryOrder(string name) => name switch
    {
        "NPC и мутанты" => 0, "Симуляция" => 1, "Состояния игрока" => 2, "Метаморфоз" => 3,
        "Инвентарь" => 4, "Мировые события" => 5, _ => 6
    };
}

public sealed class SafeZoneEditorViewModel : ObservableObject
{
    private readonly List<SafeZoneLocationOption> _locations;
    private readonly IReadOnlyList<ActionDefinition> _effects;
    private bool _enabled;
    private bool _isAddingLocation;
    private SafeZoneRuleViewModel? _selectedRule;
    private SafeZoneLocationOption? _selectedLocation;
    private SafeZonePolicyOption _selectedPolicy;

    public SafeZoneEditorViewModel(SafeZoneSettings settings, IEnumerable<CatalogEntry> catalog,
        IEnumerable<ActionDefinition> effects)
    {
        var normalized = SafeZoneSettings.Normalize(settings);
        _enabled = normalized.Enabled;
        _effects = effects.GroupBy(effect => effect.HandlerId, StringComparer.Ordinal).Select(group => group.First()).ToList();
        _locations = catalog.Where(entry => entry.Kind == CatalogEntryKind.Level)
            .Select(entry => new SafeZoneLocationOption(string.IsNullOrWhiteSpace(entry.Faction)
                ? entry.SectionId.StartsWith("adb_level_", StringComparison.Ordinal) ? entry.SectionId[10..] : string.Empty
                : entry.Faction, entry.DisplayName))
            .Where(option => System.Text.RegularExpressions.Regex.IsMatch(option.LevelName, "^[A-Za-z0-9_.-]{1,100}$"))
            .Select(option => option with { LevelName = option.LevelName.ToLowerInvariant() })
            .GroupBy(option => option.LevelName, StringComparer.Ordinal).Select(group => group.First())
            .OrderBy(option => option.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        Rules = new(normalized.Rules.Select(rule => new SafeZoneRuleViewModel(rule, _effects)));
        Rules.CollectionChanged += (_, _) => RaisePropertyChanged(nameof(HasRules));
        _selectedPolicy = Policies.Single(option => option.Value == normalized.BlockedAction);
        AddLocationCommand = new RelayCommand(AddLocation, () => SelectedLocation is not null && Enabled);
        BeginAddLocationCommand = new RelayCommand(() => IsAddingLocation = true, () => HasLocations && Enabled);
        CancelAddLocationCommand = new RelayCommand(() => IsAddingLocation = false);
        RemoveLocationCommand = new RelayCommand(() => RemoveLocation(SelectedRule), () => SelectedRule is not null && Enabled);
        ToggleRuleCommand = new RelayCommand(parameter =>
        {
            if (parameter is SafeZoneRuleViewModel rule && Rules.Contains(rule))
                SelectedRule = ReferenceEquals(SelectedRule, rule) ? null : rule;
        }, parameter => parameter is SafeZoneRuleViewModel && Enabled);
        RemoveRuleCommand = new RelayCommand(parameter => RemoveLocation(parameter as SafeZoneRuleViewModel),
            parameter => parameter is SafeZoneRuleViewModel && Enabled);
        RefreshLocations();
    }

    public ObservableCollection<SafeZoneRuleViewModel> Rules { get; }
    public ObservableCollection<SafeZoneLocationOption> AvailableLocations { get; } = [];
    public bool HasRules => Rules.Count > 0;
    public bool IsAddingLocation { get => _isAddingLocation; set => SetProperty(ref _isAddingLocation, value); }
    public IReadOnlyList<SafeZonePolicyOption> Policies { get; } =
    [new(SafeZoneBlockedAction.Skip, "Пропускать с записью в историю"), new(SafeZoneBlockedAction.Wait, "Ждать выхода с локации")];
    public RelayCommand AddLocationCommand { get; }
    public RelayCommand BeginAddLocationCommand { get; }
    public RelayCommand CancelAddLocationCommand { get; }
    public RelayCommand RemoveLocationCommand { get; }
    public RelayCommand ToggleRuleCommand { get; }
    public RelayCommand RemoveRuleCommand { get; }
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!SetProperty(ref _enabled, value)) return;
            AddLocationCommand.RaiseCanExecuteChanged(); RemoveLocationCommand.RaiseCanExecuteChanged();
            BeginAddLocationCommand.RaiseCanExecuteChanged(); ToggleRuleCommand.RaiseCanExecuteChanged();
            RemoveRuleCommand.RaiseCanExecuteChanged();
            if (!value) IsAddingLocation = false;
        }
    }
    public SafeZoneRuleViewModel? SelectedRule
    {
        get => _selectedRule;
        set
        {
            if (ReferenceEquals(_selectedRule, value)) return;
            if (_selectedRule is { } previous) previous.IsExpanded = false;
            SetProperty(ref _selectedRule, value);
            if (value is not null) value.IsExpanded = true;
            RaisePropertyChanged(nameof(HasSelectedRule));
            RemoveLocationCommand.RaiseCanExecuteChanged();
        }
    }
    public bool HasSelectedRule => SelectedRule is not null;
    public bool HasLocations => AvailableLocations.Count > 0;
    public string CatalogHint => _locations.Count == 0
        ? "Список локаций появится после сканирования игры."
        : AvailableLocations.Count == 0 ? "Все локации каталога уже добавлены." : "Локации из каталога игры";
    public SafeZoneLocationOption? SelectedLocation
    {
        get => _selectedLocation;
        set { if (SetProperty(ref _selectedLocation, value)) AddLocationCommand.RaiseCanExecuteChanged(); }
    }
    public SafeZonePolicyOption SelectedPolicy { get => _selectedPolicy; set { if (value is not null) SetProperty(ref _selectedPolicy, value); } }
    private void AddLocation()
    {
        if (SelectedLocation is not { } location || Rules.Any(rule => rule.LevelName == location.LevelName)) return;
        var rule = new SafeZoneRuleViewModel(new SafeZoneRule { LevelName = location.LevelName, DisplayName = location.DisplayName }, _effects);
        Rules.Add(rule); SelectedRule = rule; RefreshLocations();
        IsAddingLocation = false;
    }
    private void RemoveLocation(SafeZoneRuleViewModel? rule)
    {
        if (rule is null || !Rules.Contains(rule)) return;
        if (ReferenceEquals(SelectedRule, rule)) SelectedRule = null;
        Rules.Remove(rule); RefreshLocations();
    }
    private void RefreshLocations()
    {
        AvailableLocations.Clear();
        foreach (var location in _locations.Where(location => Rules.All(rule => rule.LevelName != location.LevelName)))
            AvailableLocations.Add(location);
        SelectedLocation = AvailableLocations.FirstOrDefault();
        RaisePropertyChanged(nameof(HasLocations)); RaisePropertyChanged(nameof(CatalogHint));
        BeginAddLocationCommand.RaiseCanExecuteChanged();
    }
    public SafeZoneSettings ToSettings() => SafeZoneSettings.Normalize(new SafeZoneSettings
    {
        Enabled = Enabled, BlockedAction = SelectedPolicy.Value, Rules = Rules.Select(rule => rule.ToSettings()).ToList()
    });
}
