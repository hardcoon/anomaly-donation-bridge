using System.Collections.ObjectModel;
using System.Globalization;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.App.ViewModels;

public sealed record ItemOptionViewModel(string Value, string Label);

public sealed class CatalogOptionViewModel : ObservableObject
{
    private readonly Action<CatalogOptionViewModel>? _selectionChanged;
    private bool _isSelected;

    public CatalogOptionViewModel(CatalogEntry entry, bool selected, Action<CatalogOptionViewModel>? selectionChanged)
    {
        Entry = entry;
        _isSelected = selected;
        _selectionChanged = selectionChanged;
    }

    public CatalogEntry Entry { get; }
    public string SectionId => Entry.SectionId;
    public string DisplayName => Entry.DisplayName;
    public string Category => string.IsNullOrWhiteSpace(Entry.Category) ? "Без категории" : Entry.Category;
    public string LocalizedAndTechnicalName => Entry.Kind == CatalogEntryKind.NpcVisual ? DisplayName :
        string.Equals(DisplayName, SectionId, StringComparison.OrdinalIgnoreCase)
        ? SectionId
        : $"{DisplayName}  ·  {SectionId}";
    public string Metadata => string.Join(" · ", new[]
    {
        StrengthLabel(Entry.Strength),
        string.IsNullOrWhiteSpace(Entry.Faction) ? null : Entry.Faction,
        Entry.IsQuarantined ? "Карантин: " + (Entry.IsNativeSpawnUnsafe
            ? "нет обязательной анимации для создания"
            : QuarantineLabel(Entry.QuarantineReason)) : null
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public bool IsAvailable => !Entry.IsQuarantined;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!IsAvailable && value) return;
            if (!SetProperty(ref _isSelected, value)) return;
            _selectionChanged?.Invoke(this);
        }
    }

    public void SetFromOwner(bool value)
    {
        if (_isSelected == value) return;
        _isSelected = value;
        RaisePropertyChanged(nameof(IsSelected));
    }

    private static string StrengthLabel(CatalogStrength strength) => strength switch
    {
        CatalogStrength.Lite => "lite",
        CatalogStrength.Normal => "normal",
        CatalogStrength.Strong => "strong",
        _ => "без классификации"
    };

    private static string QuarantineLabel(QuarantineReason reason) => reason switch
    {
        QuarantineReason.StoryOrUnique => "сюжетная или уникальная секция",
        QuarantineReason.NonTradeable => "неторгуемый объект",
        QuarantineReason.Scripted => "скриптовый объект",
        QuarantineReason.InvalidStructure => "небезопасная структура",
        QuarantineReason.Blacklisted => "чёрный список игры",
        QuarantineReason.MissingRequiredFields => "нет обязательных полей",
        QuarantineReason.UnsupportedClass => "неподдерживаемый класс",
        _ => "недоступно"
    };
}

public sealed class MultiChoiceOptionViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private bool _isSelected;

    public MultiChoiceOptionViewModel(string value, string label, bool isSelected, Action onChanged)
    {
        Value = value;
        Label = label;
        _isSelected = isSelected;
        _onChanged = onChanged;
    }

    public string Value { get; }
    public string Label { get; }
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetProperty(ref _isSelected, value)) return;
            _onChanged();
        }
    }

    public void SetFromOwner(bool value)
    {
        if (_isSelected == value) return;
        _isSelected = value;
        RaisePropertyChanged(nameof(IsSelected));
    }
}

public sealed class ParameterValueViewModel : ObservableObject
{
    private readonly ActionDefinition _action;
    private readonly Action _onChanged;
    private readonly bool _hideUnavailableCatalogSections;
    private string _selectedCategory = string.Empty;
    private string _catalogSearch = string.Empty;
    private string _selectedCatalogCategory = AllCategories;
    private CatalogOptionViewModel? _selectedCatalogOption;
    private const string AllCategories = "Все категории";

    public ParameterValueViewModel(
        ActionDefinition action,
        ParameterDefinition definition,
        Action onChanged,
        CatalogRuntimeState? catalogState = null,
        bool hideUnavailableCatalogSections = true)
    {
        _action = action;
        _onChanged = onChanged;
        _hideUnavailableCatalogSections = hideUnavailableCatalogSections;
        Definition = definition;
        if (!_action.Parameters.ContainsKey(definition.Key)) _action.Parameters[definition.Key] = definition.DefaultValue;
        var selectedOptions = definition.SelectedOptions(Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        MultiChoiceOptions = new ObservableCollection<MultiChoiceOptionViewModel>(definition.Options.Select(option =>
            new MultiChoiceOptionViewModel(option, DisplayOption(option), selectedOptions.Contains(option), OnMultiChoiceChanged)));
        var catalogEntries = (catalogState?.Catalog?.Entries ?? [])
            .Where(entry => definition.CatalogKind == entry.Kind)
            .Where(entry => definition.CatalogNativeClasses.Count == 0 ||
                definition.CatalogNativeClasses.Contains(
                    entry.NativeClass, StringComparer.OrdinalIgnoreCase))
            .Where(entry => definition.CatalogCategories.Count == 0 ||
                definition.CatalogCategories.Contains(
                    entry.Category, StringComparer.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Category, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.SectionId, StringComparer.Ordinal)
            .ToList();
        if (definition.CatalogKind == CatalogEntryKind.Item)
        {
            AddSyntheticItemChoice(catalogEntries, definition,
                DefaultActionCatalog.RandomArtifactSectionId, "Случайный артефакт");
            AddSyntheticItemChoice(catalogEntries, definition,
                DefaultActionCatalog.RandomHelmetSectionId, "Случайный шлем");
            AddSyntheticItemChoice(catalogEntries, definition,
                DefaultActionCatalog.RandomOutfitSectionId, "Случайная броня");
            AddSyntheticItemChoice(catalogEntries, definition,
                DefaultActionCatalog.RandomWeaponSectionId, "Случайное оружие");
        }
        if (definition.CatalogKind == CatalogEntryKind.Anomaly &&
            definition.Options.Contains(
                DefaultActionCatalog.RandomAnomalySectionId,
                StringComparer.OrdinalIgnoreCase))
        {
            if (definition.Options.Contains(
                    DefaultActionCatalog.EachRandomAnomalySectionId,
                    StringComparer.OrdinalIgnoreCase))
            {
                catalogEntries.Insert(0, new CatalogEntry
                {
                    SectionId = DefaultActionCatalog.EachRandomAnomalySectionId,
                    Kind = CatalogEntryKind.Anomaly,
                    DisplayName = "Каждая аномалия будет случайной",
                    Category = "Случайная",
                    Strength = CatalogStrength.Unclassified,
                    IsAvailable = true,
                    QuarantineReason = QuarantineReason.None
                });
            }
            catalogEntries.Insert(0, new CatalogEntry
            {
                SectionId = DefaultActionCatalog.RandomAnomalySectionId,
                Kind = CatalogEntryKind.Anomaly,
                DisplayName = "Спавнить один случайный тип аномалии",
                Category = "Случайная",
                Strength = CatalogStrength.Unclassified,
                IsAvailable = true,
                QuarantineReason = QuarantineReason.None
            });
        }
        CatalogOptions = new ObservableCollection<CatalogOptionViewModel>(catalogEntries
            .Select(entry => new CatalogOptionViewModel(
                entry,
                selectedOptions.Contains(entry.SectionId),
                OnCatalogMultiChoiceChanged)));
        ClearCatalogSelectionCommand = new RelayCommand(_ => Value = string.Empty);
        SelectAllCatalogSelectionCommand = new RelayCommand(_ =>
        {
            var selected = Definition.SelectedOptions(Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var option in CatalogOptions.Where(option =>
                         option.IsAvailable && FilterCatalogOption(option)))
            {
                selected.Add(option.SectionId);
            }

            Value = string.Join(',', CatalogOptions
                .Where(option => selected.Contains(option.SectionId))
                .Select(option => option.SectionId)
                .Concat(Definition.SelectedOptions(Value).Where(section =>
                    CatalogOptions.All(option => !string.Equals(
                        option.SectionId, section, StringComparison.OrdinalIgnoreCase)))));
        });
        RemoveHiddenCatalogSelectionsCommand = new RelayCommand(_ =>
            Value = string.Join(',', CatalogOptions
                .Where(option => option.IsAvailable && option.IsSelected)
                .Select(option => option.SectionId)));
        CatalogCategories = new[] { AllCategories }
            .Concat(CatalogOptions
                .Where(option => !_hideUnavailableCatalogSections || option.IsAvailable)
                .Select(option => option.Category)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase))
            .ToList();
        CatalogOptionsView = CollectionViewSource.GetDefaultView(CatalogOptions);
        CatalogOptionsView.Filter = FilterCatalogOption;
        CatalogIsLive = catalogState?.IsLive == true;
        CatalogHint = definition.CatalogKind is null
            ? string.Empty
            : catalogState?.Catalog is null
                ? "Запустите игру и нажмите ↻ в карточке игры, чтобы загрузить каталог."
                : catalogState.IsLive
                    ? $"Живой каталог · ревизия {catalogState.Catalog.Revision}"
                    : $"Кэш от {catalogState.Catalog.ReceivedAt:dd.MM.yyyy HH:mm}; команды заблокированы до проверки или обновления каталога.";
        _selectedCatalogOption = CatalogOptions.FirstOrDefault(option =>
            option.IsAvailable && string.Equals(option.SectionId, Value, StringComparison.OrdinalIgnoreCase));
        if (IsGroupedItemChoice)
        {
            _selectedCategory = Definition.OptionGroups
                .FirstOrDefault(pair => pair.Value.Contains(Value, StringComparer.OrdinalIgnoreCase)).Key
                ?? Definition.OptionGroups.Keys.FirstOrDefault()
                ?? string.Empty;
        }
    }

    private static void AddSyntheticItemChoice(
        List<CatalogEntry> catalogEntries,
        ParameterDefinition definition,
        string sectionId,
        string label)
    {
        if (!definition.Options.Contains(sectionId, StringComparer.OrdinalIgnoreCase)) return;
        catalogEntries.Insert(0, new CatalogEntry
        {
            SectionId = sectionId,
            Kind = CatalogEntryKind.Item,
            DisplayName = label,
            Category = label,
            Strength = CatalogStrength.Unclassified,
            IsAvailable = true,
            QuarantineReason = QuarantineReason.None
        });
    }

    public ParameterDefinition Definition { get; }
    public string DisplayName => Definition.DisplayName;
    public bool IsSpawnPointMethod => Definition.Key == "spawn_point_method";
    public string SpawnPointMethodHint => Value switch
    {
        "boss_sample" => "Выбирает готовые точки AI-сетки. Запасной вариант — ближайшая к нужной дистанции точка, не ближе 10 м.",
        "legacy" => "Ищет по прямым направлениям. При неудаче может выбрать точку рядом с игроком.",
        "graph" => "Ищет через связанные точки AI-сетки, проходы и повороты.",
        "scan" => "Последовательно проверяет точки AI-сетки, пока не найдёт подходящие. Может работать дольше.",
        "coordinates" => "Выбирает координаты на заданной дистанции и привязывает их к AI-сетке.",
        _ => string.Empty
    };
    public bool IsKeyboardKey => Definition.Kind is ParameterKind.KeyboardKey or ParameterKind.GameInputKey;
    public bool AllowsMouseKey => Definition.Kind == ParameterKind.GameInputKey;
    public string GameKeyHint => $"Нажмите нужную игровую клавишу{(AllowsMouseKey ? " или кнопку мыши (после фокуса на поле)" : "")}. По умолчанию: {Definition.DefaultValue}. Esc, F5 и F9 зарезервированы.";
    public bool IsChoice => Definition.Kind is ParameterKind.Choice or ParameterKind.ItemChoice or ParameterKind.MultiChoice;
    public bool IsBooleanChoice =>
        Definition.Kind == ParameterKind.Choice &&
        Definition.Options.Count == 2 &&
        Definition.Options.Contains("false", StringComparer.OrdinalIgnoreCase) &&
        Definition.Options.Contains("true", StringComparer.OrdinalIgnoreCase);
    public bool IsFlatChoice =>
        (Definition.Kind is ParameterKind.Choice or ParameterKind.ItemChoice) &&
        !IsBooleanChoice && !IsGroupedItemChoice && !IsDynamicCatalogChoice;
    public bool IsGroupedItemChoice => !IsDynamicCatalogChoice && Definition.Kind == ParameterKind.ItemChoice && Definition.OptionGroups.Count > 0;
    public bool IsMultiChoice => !IsDynamicCatalogChoice && Definition.Kind == ParameterKind.MultiChoice;
    public bool IsActiveTemplateSelection => ActiveTemplateSelection.IsParameter(Definition);
    public bool IsWrappedMultiChoice => IsMultiChoice && !IsActiveTemplateSelection;
    public string ActiveTemplateSelectionHint => MultiChoiceOptions.Count == 0
        ? "В этом профиле пока нет других активных шаблонов. Сначала добавьте их в раздел «Активные»."
        : "Каждый пункт — отдельный настроенный вариант. Новые шаблоны не отмечаются автоматически.";
    public bool IsDynamicCatalogChoice => Definition.CatalogKind is not null;
    public bool IsDynamicCatalogMultiChoice => IsDynamicCatalogChoice && Definition.Kind == ParameterKind.MultiChoice;
    public bool IsDynamicCatalogSingleChoice => IsDynamicCatalogChoice && Definition.Kind != ParameterKind.MultiChoice;
    public IReadOnlyList<string> Options => Definition.Options;
    public IReadOnlyList<ItemOptionViewModel> OptionEntries => Options
        .Select(value => new ItemOptionViewModel(value, DisplayOption(value)))
        .ToList();
    public ObservableCollection<MultiChoiceOptionViewModel> MultiChoiceOptions { get; }
    public ObservableCollection<CatalogOptionViewModel> CatalogOptions { get; }
    public ICommand ClearCatalogSelectionCommand { get; }
    public ICommand SelectAllCatalogSelectionCommand { get; }
    public ICommand RemoveHiddenCatalogSelectionsCommand { get; }
    public ICollectionView CatalogOptionsView { get; }
    public IReadOnlyList<string> CatalogCategories { get; }
    public bool CatalogIsLive { get; }
    public string CatalogHint { get; }
    public string CatalogSearch
    {
        get => _catalogSearch;
        set
        {
            if (!SetProperty(ref _catalogSearch, value)) return;
            CatalogOptionsView.Refresh();
            // ComboBox clears its visual SelectedItem while the current option is
            // temporarily filtered out. Re-publish the model selection so clearing
            // the search restores the chosen value instead of leaving a blank row.
            RaisePropertyChanged(nameof(SelectedCatalogOption));
            RaisePropertyChanged(nameof(Value));
        }
    }
    public string SelectedCatalogCategory
    {
        get => _selectedCatalogCategory;
        set
        {
            if (!SetProperty(ref _selectedCatalogCategory, value)) return;
            CatalogOptionsView.Refresh();
            RaisePropertyChanged(nameof(SelectedCatalogOption));
            RaisePropertyChanged(nameof(Value));
        }
    }
    public CatalogOptionViewModel? SelectedCatalogOption
    {
        get => _selectedCatalogOption;
        set
        {
            if (value is null || !value.IsAvailable)
            {
                RaisePropertyChanged();
                return;
            }
            if (!SetProperty(ref _selectedCatalogOption, value)) return;
            Value = value.SectionId;
        }
    }
    public IReadOnlyList<string> Categories => Definition.OptionGroups.Keys.ToList();
    public IReadOnlyList<string> FilteredOptions =>
        Definition.OptionGroups.TryGetValue(SelectedCategory, out var options) ? options : [];
    public IReadOnlyList<ItemOptionViewModel> FilteredOptionEntries => FilteredOptions
        .Select(value => new ItemOptionViewModel(value, DisplayOption(value)))
        .ToList();
    public string DisplayValue
    {
        get
        {
            if (Definition.Kind != ParameterKind.MultiChoice)
            {
                return DisplayOption(Value);
            }

            var selected = IsDynamicCatalogMultiChoice
                ? CatalogOptions.Count(option => option.IsAvailable && option.IsSelected)
                : MultiChoiceOptions.Count(option => option.IsSelected);
            var total = IsDynamicCatalogMultiChoice
                ? CatalogOptions.Count(option => option.IsAvailable)
                : MultiChoiceOptions.Count;
            var hidden = IsDynamicCatalogMultiChoice ? HiddenCatalogSelections.Count : 0;
            return hidden > 0
                ? $"выбрано {selected} из {total} · скрыто {hidden}"
                : $"выбрано {selected} из {total}";
        }
    }
    private IReadOnlyList<string> HiddenCatalogSelections
    {
        get
        {
            if (!IsDynamicCatalogMultiChoice) return [];
            var available = CatalogOptions
                .Where(option => option.IsAvailable)
                .Select(option => option.SectionId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Definition.SelectedOptions(Value)
                .Where(section => !available.Contains(section))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }
    public bool HasHiddenCatalogSelections => HiddenCatalogSelections.Count > 0;
    public string HiddenCatalogSummary
    {
        get
        {
            var hidden = HiddenCatalogSelections;
            if (hidden.Count == 0) return string.Empty;
            var preview = string.Join(", ", hidden.Take(5));
            return $"Сохранено {hidden.Count} скрытых или недоступных секций: {preview}" +
                (hidden.Count > 5 ? "…" : string.Empty) +
                ". Они блокируют тест; удалите их или обновите выбор.";
        }
    }
    public string SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (!SetProperty(ref _selectedCategory, value)) return;
            RaisePropertyChanged(nameof(FilteredOptions));
            RaisePropertyChanged(nameof(FilteredOptionEntries));
            if (!FilteredOptions.Contains(Value, StringComparer.OrdinalIgnoreCase) && FilteredOptions.FirstOrDefault() is { } first)
            {
                Value = first;
            }
        }
    }
    public string Value
    {
        get => _action.Parameters.TryGetValue(Definition.Key, out var value) ? value : Definition.DefaultValue;
        set
        {
            // WPF momentarily clears SelectedValue while a grouped ComboBox
            // replaces its ItemsSource. An empty transient selection must not
            // overwrite the raw section ID stored in the action model.
            if (IsChoice && Definition.Kind != ParameterKind.MultiChoice && string.IsNullOrEmpty(value)) return;
            if (value == Value) return;
            _action.Parameters[Definition.Key] = value;
            if (IsMultiChoice)
            {
                var selected = Definition.SelectedOptions(value).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var option in MultiChoiceOptions)
                {
                    option.SetFromOwner(selected.Contains(option.Value));
                }
            }
            if (IsDynamicCatalogChoice)
            {
                var selected = Definition.SelectedOptions(value).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var option in CatalogOptions) option.SetFromOwner(selected.Contains(option.SectionId));
                _selectedCatalogOption = CatalogOptions.FirstOrDefault(option =>
                    option.IsAvailable && string.Equals(option.SectionId, value, StringComparison.OrdinalIgnoreCase));
                RaisePropertyChanged(nameof(SelectedCatalogOption));
            }
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(BooleanValue));
            RaisePropertyChanged(nameof(DisplayValue));
            RaisePropertyChanged(nameof(SpawnPointMethodHint));
            RaisePropertyChanged(nameof(HasHiddenCatalogSelections));
            RaisePropertyChanged(nameof(HiddenCatalogSummary));
            RaisePropertyChanged(nameof(ValidationMessage));
            if (IsGroupedItemChoice)
            {
                var category = Definition.OptionGroups
                    .FirstOrDefault(pair => pair.Value.Contains(value, StringComparer.OrdinalIgnoreCase)).Key;
                if (!string.IsNullOrEmpty(category) && !string.Equals(category, SelectedCategory, StringComparison.OrdinalIgnoreCase))
                {
                    _selectedCategory = category;
                    RaisePropertyChanged(nameof(SelectedCategory));
                    RaisePropertyChanged(nameof(FilteredOptions));
                    RaisePropertyChanged(nameof(FilteredOptionEntries));
                }
            }
            _onChanged();
        }
    }
    public bool BooleanValue
    {
        get => string.Equals(Value, "true", StringComparison.OrdinalIgnoreCase);
        set => Value = value ? "true" : "false";
    }
    public string ValidationMessage => Definition.Validate(Value) ?? string.Empty;

    public void RefreshFromOwner()
    {
        var selected = Definition.SelectedOptions(Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var option in MultiChoiceOptions)
            option.SetFromOwner(selected.Contains(option.Value));
        foreach (var option in CatalogOptions)
            option.SetFromOwner(selected.Contains(option.SectionId));
        _selectedCatalogOption = CatalogOptions.FirstOrDefault(option =>
            option.IsAvailable && string.Equals(option.SectionId, Value,
                StringComparison.OrdinalIgnoreCase));
        RaisePropertyChanged(nameof(Value));
        RaisePropertyChanged(nameof(BooleanValue));
        RaisePropertyChanged(nameof(SelectedCatalogOption));
        RaisePropertyChanged(nameof(DisplayValue));
        RaisePropertyChanged(nameof(HasHiddenCatalogSelections));
        RaisePropertyChanged(nameof(HiddenCatalogSummary));
        RaisePropertyChanged(nameof(ValidationMessage));
    }

    private void OnMultiChoiceChanged()
    {
        var selected = MultiChoiceOptions
            .Where(option => option.IsSelected)
            .Select(option => option.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Value = string.Join(',', Definition.Options.Where(selected.Contains));
    }

    private void OnCatalogMultiChoiceChanged(CatalogOptionViewModel changed)
    {
        if (!IsDynamicCatalogMultiChoice) return;
        var selected = CatalogOptions
            .Where(option => option.IsAvailable && option.IsSelected)
            .Select(option => option.SectionId)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        Value = string.Join(',', selected);
    }

    private bool FilterCatalogOption(object value)
    {
        if (value is not CatalogOptionViewModel option) return false;
        if (_hideUnavailableCatalogSections && !option.IsAvailable) return false;
        if (!string.Equals(SelectedCatalogCategory, AllCategories, StringComparison.CurrentCultureIgnoreCase) &&
            !string.Equals(option.Category, SelectedCatalogCategory, StringComparison.CurrentCultureIgnoreCase))
        {
            return false;
        }
        var search = (CatalogSearch ?? string.Empty).Trim();
        return search.Length == 0 ||
            option.DisplayName.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
            option.SectionId.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            option.Metadata.Contains(search, StringComparison.CurrentCultureIgnoreCase);
    }

    private string DisplayOption(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return Definition.OptionLabels.TryGetValue(value, out var label)
            ? label
            : AnomalyDonationBridge.App.FriendlyValueConverter.Display(value);
    }
}

public sealed class ItemSpawnRowViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private string _countText;

    public ItemSpawnRowViewModel(
        ParameterDefinition definition,
        ItemSpawnEntry entry,
        CatalogRuntimeState catalogState,
        Action onChanged,
        bool hideUnavailableCatalogSections = true)
    {
        _onChanged = onChanged;
        _countText = entry.Count.ToString(CultureInfo.InvariantCulture);
        var rowAction = new ActionDefinition
        {
            HandlerId = ItemBundleCodec.HandlerId,
            DisplayName = "Строка предмета",
            ParameterDefinitions = [definition],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                [definition.Key] = string.IsNullOrWhiteSpace(entry.ItemId)
                    ? definition.DefaultValue
                    : entry.ItemId
            }
        };
        Picker = new ParameterValueViewModel(rowAction, definition, Changed, catalogState, hideUnavailableCatalogSections);
    }

    public ParameterValueViewModel Picker { get; }
    public string CountText
    {
        get => _countText;
        set
        {
            if (!SetProperty(ref _countText, value)) return;
            RaisePropertyChanged(nameof(ValidationMessage));
            _onChanged();
        }
    }
    public string DisplayItem => Picker.DisplayValue;
    public string ValidationMessage => TryBuild(out _, out var error) ? string.Empty : error;

    public bool TryBuild(out ItemSpawnEntry entry, out string error)
    {
        entry = new ItemSpawnEntry { ItemId = Picker.Value };
        error = Picker.Definition.Validate(Picker.Value) ?? string.Empty;
        if (error.Length > 0) return false;
        if (!int.TryParse(CountText, NumberStyles.Integer, CultureInfo.CurrentCulture, out var count) &&
            !int.TryParse(CountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out count) ||
            count is < 1 or > 50)
        {
            error = "Количество должно быть целым числом от 1 до 50.";
            return false;
        }
        entry.Count = count;
        return true;
    }

    private void Changed()
    {
        RaisePropertyChanged(nameof(DisplayItem));
        RaisePropertyChanged(nameof(ValidationMessage));
        _onChanged();
    }
}

public sealed class SpawnGroupRowViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private string _countText;

    public SpawnGroupRowViewModel(
        string handlerId,
        ParameterDefinition definition,
        SpawnGroupEntry entry,
        CatalogRuntimeState catalogState,
        Action onChanged,
        bool hideUnavailableCatalogSections = true)
    {
        _onChanged = onChanged;
        _countText = entry.Count.ToString(CultureInfo.InvariantCulture);
        var rowAction = new ActionDefinition
        {
            HandlerId = handlerId,
            DisplayName = "Строка группы",
            ParameterDefinitions = [definition],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                [definition.Key] = string.IsNullOrWhiteSpace(entry.VariantId)
                    ? definition.DefaultValue
                    : entry.VariantId
            }
        };
        Picker = new ParameterValueViewModel(rowAction, definition, Changed, catalogState, hideUnavailableCatalogSections);
    }

    public ParameterValueViewModel Picker { get; }
    public string CountText
    {
        get => _countText;
        set
        {
            if (!SetProperty(ref _countText, value)) return;
            RaisePropertyChanged(nameof(ValidationMessage));
            _onChanged();
        }
    }
    public string DisplayVariant => Picker.DisplayValue;
    public string ValidationMessage => TryBuild(out _, out var error) ? string.Empty : error;

    public bool TryBuild(out SpawnGroupEntry entry, out string error)
    {
        entry = new SpawnGroupEntry { VariantId = Picker.Value, Strength = "any" };
        error = Picker.Definition.Validate(Picker.Value) ?? string.Empty;
        if (error.Length > 0) return false;
        if (!int.TryParse(CountText, NumberStyles.Integer, CultureInfo.CurrentCulture, out var count) &&
            !int.TryParse(CountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out count) ||
            count is < 1 or > 99)
        {
            error = "Количество должно быть целым числом от 1 до 99.";
            return false;
        }
        entry.Count = count;
        return true;
    }

    private void Changed()
    {
        RaisePropertyChanged(nameof(DisplayVariant));
        RaisePropertyChanged(nameof(ValidationMessage));
        _onChanged();
    }
}

public sealed class TriggerRuleViewModel : ObservableObject
{
    public TriggerRuleViewModel(ActionViewModel owner, TriggerRule model)
    {
        Owner = owner;
        Model = model;
    }

    public ActionViewModel Owner { get; }
    public TriggerRule Model { get; }
    public string Label => $"{(Model.Comparator == TriggerComparator.Exact ? "=" : "≥")} {Model.Amount:0.##} {Model.Currency}";
    public void Refresh()
    {
        RaisePropertyChanged(nameof(Label));
        Owner.NotifyTriggersChanged();
    }
}

public sealed class ActionViewModel : ObservableObject
{
    private string _lastResult = "Ещё не запускалось";
    private string _cooldownText;
    private readonly CatalogRuntimeState _catalogState;
    private readonly bool _hideUnavailableCatalogSections;
    private readonly bool _manifestAvailable;
    private readonly RelayCommand _removeItemSpawnCommand;
    private readonly RelayCommand _removeSpawnGroupCommand;
    private int _visualDisplayOrdinal;
    public ActionViewModel(
        ActionDefinition model,
        bool isPreset = false,
        bool isUserPreset = false,
        CatalogRuntimeState? catalogState = null,
        string originLabel = "",
        bool manifestAvailable = true,
        int displayOrdinal = 0,
        bool hideUnavailableCatalogSections = true)
    {
        Model = model;
        _cooldownText = model.CooldownSeconds.ToString(CultureInfo.InvariantCulture);
        IsUserPreset = isUserPreset;
        IsPreset = isPreset || isUserPreset;
        _catalogState = catalogState ?? CatalogRuntimeState.Empty;
        _hideUnavailableCatalogSections = hideUnavailableCatalogSections;
        OriginLabel = originLabel;
        DisplayOrdinal = displayOrdinal;
        _visualDisplayOrdinal = displayOrdinal;
        _manifestAvailable = manifestAvailable;
        if (_catalogState.Catalog is { } currentCatalog)
        {
            // The catalog state is the source of truth for the editor. Rebind
            // the model here as well so a settings/catalog notification race
            // cannot show current checkboxes while validating stale options.
            DynamicCatalogBinding.Apply(currentCatalog, [Model]);
            if (!_catalogState.IsLive) Model.CatalogRevision = null;
        }
        Parameters = new ObservableCollection<ParameterValueViewModel>(model.ParameterDefinitions
            .Where(definition => !IsBundledParameter(definition.Key))
            .Select(definition => new ParameterValueViewModel(model, definition, OnParameterChanged, _catalogState, _hideUnavailableCatalogSections)));
        TraderStockRows = IsWanderingTrader
            ? TraderStockRowViewModel.Create(Parameters).ToArray() : [];
        ItemSpawnRows = [];
        if (IsItemBundle && ItemDefinition is { } itemDefinition)
        {
            var rows = model.ItemSpawns.Count > 0 ? model.ItemSpawns : [LegacyItemEntry(model)];
            foreach (var row in rows)
                ItemSpawnRows.Add(new ItemSpawnRowViewModel(itemDefinition, row, _catalogState, OnBundleChanged, _hideUnavailableCatalogSections));
        }
        AddItemSpawnCommand = new RelayCommand(AddItemSpawn, () => IsItemBundle);
        _removeItemSpawnCommand = new RelayCommand(
            parameter => RemoveItemSpawn((ItemSpawnRowViewModel)parameter!),
            _ => IsItemBundle && ItemSpawnRows.Count > 1);
        RemoveItemSpawnCommand = _removeItemSpawnCommand;

        SpawnGroupRows = [];
        if (IsSpawnGroupBundle && VariantDefinition is { } variantDefinition)
        {
            var rows = model.SpawnGroups.Count > 0 ? model.SpawnGroups : [LegacySpawnGroupEntry(model)];
            foreach (var row in rows)
                SpawnGroupRows.Add(new SpawnGroupRowViewModel(HandlerId, variantDefinition, row, _catalogState, OnBundleChanged, _hideUnavailableCatalogSections));
        }
        AddSpawnGroupCommand = new RelayCommand(AddSpawnGroup, () => IsSpawnGroupBundle);
        _removeSpawnGroupCommand = new RelayCommand(
            parameter => RemoveSpawnGroup((SpawnGroupRowViewModel)parameter!),
            _ => IsSpawnGroupBundle && SpawnGroupRows.Count > 1);
        RemoveSpawnGroupCommand = _removeSpawnGroupCommand;
        Triggers = new ObservableCollection<TriggerRuleViewModel>(model.Triggers.Select(rule => new TriggerRuleViewModel(this, rule)));
        Triggers.CollectionChanged += (_, _) =>
        {
            RaisePropertyChanged(nameof(PriceItems));
            RaisePropertyChanged(nameof(PrimaryPriceLabel));
            RaisePropertyChanged(nameof(FirstThreeTriggers));
            RaisePropertyChanged(nameof(HasTriggers));
            TriggersChanged?.Invoke();
        };
    }

    public event Action? TriggersChanged;

    public ActionDefinition Model { get; }
    public string Id => Model.Id;
    public string HandlerId => Model.HandlerId;
    public bool IsPreset { get; }
    public bool IsUserPreset { get; }
    public bool IsBuiltInPreset => IsPreset && !IsUserPreset;
    public bool IsItemBundle => string.Equals(HandlerId, ItemBundleCodec.HandlerId, StringComparison.Ordinal);
    public bool IsSpawnGroupBundle => SpawnGroupBundleCodec.Supports(HandlerId);
    public bool HasRowBundle => IsItemBundle || IsSpawnGroupBundle;
    public string DisplayName => Model.DisplayName;
    public int DisplayOrdinal { get; }
    public string DisplayOrdinalLabel => DisplayOrdinal > 0 ? $"№{DisplayOrdinal}" : string.Empty;
    public int VisualDisplayOrdinal
    {
        get => _visualDisplayOrdinal;
        private set
        {
            if (!SetProperty(ref _visualDisplayOrdinal, value)) return;
            RaisePropertyChanged(nameof(VisualDisplayOrdinalLabel));
        }
    }
    public string VisualDisplayOrdinalLabel => VisualDisplayOrdinal > 0 ? $"№{VisualDisplayOrdinal}" : string.Empty;
    public string Description => Model.Description;
    public string OriginLabel { get; }
    public bool RequiresCatalog => CatalogRevisionEnforcement.RequiresCatalog(Model);
    public bool IsSupported => _manifestAvailable && (!_catalogState.IsLive ||
        ReferenceEquals(_catalogState.Capabilities, CapabilitySet.Empty) ||
        _catalogState.Capabilities.SupportsHandler(HandlerId));
    private bool HasFreshCatalog => !RequiresCatalog ||
        _catalogState.IsLive &&
        _catalogState.Catalog is { } catalog &&
        Model.CatalogRevision == catalog.Revision;
    private string EditorError => TryBuildEditedAction(out _, out var error) ? string.Empty : error;
    public bool CanTest => IsSupported && HasFreshCatalog && EditorError.Length == 0;
    public string AvailabilityHint => !_manifestAvailable
        ? "Manifest расширения отсутствует или отклонён; действие сохранено, но отключено."
        : !IsSupported
        ? "Установленная версия игрового аддона не объявила этот обработчик."
        : !HasFreshCatalog
            ? "Нужен свежий каталог из текущего сеанса игры. Офлайн-кэш используется только для просмотра."
            : EditorError.Length > 0
                ? "Исправьте параметры: " + EditorError
                : string.Empty;
    public string ParameterSummary => string.Join(" · ", ParameterSummaryItems);
    public IReadOnlyList<string> ParameterSummaryItems => IsItemBundle
        ? ItemSpawnRows.Select(row => $"{row.DisplayItem} × {row.CountText}")
            .Concat(Parameters.Select(parameter => $"{parameter.DisplayName}: {parameter.DisplayValue}"))
            .ToArray()
        : IsSpawnGroupBundle
            ? SpawnGroupRows.Select(row => $"{row.DisplayVariant} × {row.CountText}")
                .Concat(Parameters.Select(parameter => $"{parameter.DisplayName}: {parameter.DisplayValue}"))
                .ToArray()
            : Parameters.Select(parameter => $"{parameter.DisplayName}: {parameter.DisplayValue}").ToArray();
    public int CompactParameterColumns =>
        IsItemBundle ? 2
        : Parameters.Any(parameter => parameter.IsMultiChoice || parameter.IsDynamicCatalogChoice)
            ? 1
            : Parameters.Count == 3 && Parameters.All(parameter => !parameter.IsGroupedItemChoice) ? 3 : 2;
    public bool IsWanderingTrader => HandlerId == "hardcoon.adb.wandering_trader";
    public IReadOnlyList<TraderStockRowViewModel> TraderStockRows { get; }
    public IEnumerable<ParameterValueViewModel> RegularParameters => Parameters.Where(parameter =>
        !parameter.AllowsMouseKey && (!IsWanderingTrader || !TraderStockRowViewModel.IsStockKey(parameter.Definition.Key)));
    public IEnumerable<ParameterValueViewModel> AbilityKeyParameters => Parameters.Where(parameter => parameter.AllowsMouseKey);
    public string ShortInstanceId => IsPreset || Id.Length <= 8 ? string.Empty : Id[..8];
    public string CooldownLabel => "Пауза после срабатывания, сек.";
    public string CooldownHint => "Новый донат ждёт окончания паузы в очереди. Общий лимит ожидания и размер очереди задаются в настройках. Режим «Ждать без ограничения» не ограничивает ожидание по времени.";
    public string EditorUsageHint => IsBuiltInPreset
        ? "Сохраните настроенный вариант в пользовательские шаблоны. Сумма назначается уже там."
        : "Настройки пользовательского шаблона применятся при добавлении в активные.";
    public ActionViewModel CreateEditorDraft(IEnumerable<ActionDefinition>? activeTemplates = null)
    {
        var draft = Model.Clone();
        if (activeTemplates is not null) ActiveTemplateSelection.BindEditor(draft, activeTemplates);
        return new(draft, IsPreset, IsUserPreset, _catalogState, OriginLabel, _manifestAvailable, DisplayOrdinal, _hideUnavailableCatalogSections)
        { LastResult = LastResult };
    }
    public void ApplyEditorDraft(ActionViewModel draft)
    {
        if (!string.Equals(Id, draft.Id, StringComparison.Ordinal) ||
            !string.Equals(HandlerId, draft.HandlerId, StringComparison.Ordinal))
            throw new InvalidOperationException("Действие изменилось. Откройте редактор заново.");
        Model.Parameters = new Dictionary<string, string>(draft.Model.Parameters, StringComparer.OrdinalIgnoreCase);
        Model.ItemSpawns = draft.Model.ItemSpawns.Select(entry => entry.Clone()).ToList();
        Model.SpawnGroups = draft.Model.SpawnGroups.Select(entry => entry.Clone()).ToList();
        foreach (var parameter in Parameters) parameter.RefreshFromOwner();
        ReloadBundleRowsFromModel();
        CooldownSeconds = draft.Model.CooldownSeconds;
        RaiseEditorProperties();
    }
    public int CooldownSeconds
    {
        get => Model.CooldownSeconds;
        set
        {
            if (Model.CooldownSeconds == value) return;
            Model.CooldownSeconds = value;
            _cooldownText = value.ToString(CultureInfo.InvariantCulture);
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(CooldownText));
            RaisePropertyChanged(nameof(CooldownValidationMessage));
            RaisePropertyChanged(nameof(CanTest));
            RaisePropertyChanged(nameof(AvailabilityHint));
        }
    }
    public string CooldownText
    {
        get => _cooldownText;
        set
        {
            if (!SetProperty(ref _cooldownText, value)) return;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out var parsed) &&
                parsed is >= 0 and <= 86_400)
            {
                Model.CooldownSeconds = parsed;
                RaisePropertyChanged(nameof(CooldownSeconds));
            }
            RaisePropertyChanged(nameof(CooldownValidationMessage));
            RaisePropertyChanged(nameof(CanTest));
            RaisePropertyChanged(nameof(AvailabilityHint));
        }
    }
    public string CooldownValidationMessage =>
        !int.TryParse(CooldownText, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) ||
        value is < 0 or > 86_400
            ? "Пауза должна быть целым числом от 0 до 86 400 секунд."
            : string.Empty;
    public ObservableCollection<ParameterValueViewModel> Parameters { get; }
    public ObservableCollection<ItemSpawnRowViewModel> ItemSpawnRows { get; }
    public ObservableCollection<SpawnGroupRowViewModel> SpawnGroupRows { get; }
    public ObservableCollection<TriggerRuleViewModel> Triggers { get; }
    public ICommand AddItemSpawnCommand { get; }
    public ICommand RemoveItemSpawnCommand { get; }
    public ICommand AddSpawnGroupCommand { get; }
    public ICommand RemoveSpawnGroupCommand { get; }
    // The final item renders the add button in the same wrapping flow as the prices.
    public IReadOnlyList<object> PriceItems => Triggers.Cast<object>().Append(this).ToArray();
    public string PrimaryPriceLabel => Triggers.FirstOrDefault()?.Label ?? "—";
    public IReadOnlyList<TriggerRuleViewModel> FirstThreeTriggers => Triggers.Take(3).ToArray();
    public bool HasTriggers => Triggers.Count > 0;
    public string LastResult
    {
        get => _lastResult;
        set
        {
            if (!SetProperty(ref _lastResult, value)) return;
            RaisePropertyChanged(nameof(ResultSummary));
        }
    }
    public string ResultSummary
    {
        get
        {
            var separator = LastResult.IndexOf(':');
            return separator > 0 && Enum.TryParse<DispatchState>(LastResult[..separator], out var state)
                ? FriendlyValueConverter.Display(state)
                : LastResult;
        }
    }

    public bool TryCommitPendingEdits(out string error)
    {
        if (!TryBuildEditedAction(out var edited, out error)) return false;
        Model.Parameters = new Dictionary<string, string>(edited.Parameters, StringComparer.OrdinalIgnoreCase);
        Model.ItemSpawns = edited.ItemSpawns.Select(entry => entry.Clone()).ToList();
        Model.SpawnGroups = edited.SpawnGroups.Select(entry => entry.Clone()).ToList();
        Model.CooldownSeconds = edited.CooldownSeconds;
        foreach (var parameter in Parameters) parameter.RefreshFromOwner();
        RaiseEditorProperties();
        return true;
    }

    public bool TryValidatePendingEdits(out string error) => TryBuildEditedAction(out _, out error);

    private bool TryBuildEditedAction(out ActionDefinition edited, out string error)
    {
        edited = Model.Clone();
        error = CooldownValidationMessage;
        if (error.Length > 0) return false;
        edited.CooldownSeconds = int.Parse(CooldownText, NumberStyles.Integer, CultureInfo.CurrentCulture);
        if (IsItemBundle)
        {
            edited.ItemSpawns.Clear();
            foreach (var row in ItemSpawnRows)
            {
                if (!row.TryBuild(out var entry, out error)) return false;
                edited.ItemSpawns.Add(entry);
            }
            SyncLegacyItemParameters(edited);
        }
        if (IsSpawnGroupBundle)
        {
            edited.SpawnGroups.Clear();
            foreach (var row in SpawnGroupRows)
            {
                if (!row.TryBuild(out var entry, out error)) return false;
                edited.SpawnGroups.Add(entry);
            }
            SyncLegacySpawnParameters(edited);
        }
        error = string.Join(Environment.NewLine, edited.Validate());
        return error.Length == 0;
    }

    public void AddTrigger(TriggerRule rule)
    {
        Model.Triggers.Add(rule);
        Triggers.Add(new TriggerRuleViewModel(this, rule));
        RaisePropertyChanged(nameof(Triggers));
        RaisePropertyChanged(nameof(PrimaryPriceLabel));
    }

    public void RemoveTrigger(TriggerRuleViewModel trigger)
    {
        Model.Triggers.Remove(trigger.Model);
        Triggers.Remove(trigger);
    }

    public void ReplaceTriggers(IEnumerable<TriggerRule> rules)
    {
        var replacement = rules.Select(rule => rule.Clone()).ToList();
        Model.Triggers.Clear();
        Triggers.Clear();
        foreach (var rule in replacement)
        {
            Model.Triggers.Add(rule);
            Triggers.Add(new TriggerRuleViewModel(this, rule));
        }
        RaisePropertyChanged(nameof(Triggers));
        RaisePropertyChanged(nameof(PrimaryPriceLabel));
    }

    public void SetVisualDisplayOrdinal(int value) => VisualDisplayOrdinal = Math.Max(0, value);

    public int SequenceOrdinal { get; private set; }
    public bool IsSequenceStep => SequenceOrdinal > 0;
    public string SequenceGroupKey => ActionPriceOrdering.GroupKey(Model) ?? string.Empty;
    internal void SetSequenceOrdinal(int value)
    {
        SequenceOrdinal = value;
        RaisePropertyChanged(nameof(SequenceOrdinal));
        RaisePropertyChanged(nameof(IsSequenceStep));
    }

    internal void NotifyTriggersChanged() => TriggersChanged?.Invoke();

    private void OnParameterChanged()
    {
        RaiseEditorProperties();
    }

    private void OnBundleChanged()
    {
        RaiseEditorProperties();
    }

    private void ReloadBundleRowsFromModel()
    {
        if (IsItemBundle && ItemDefinition is { } itemDefinition)
        {
            ItemSpawnRows.Clear();
            var rows = Model.ItemSpawns.Count > 0 ? Model.ItemSpawns : [LegacyItemEntry(Model)];
            foreach (var row in rows)
            {
                ItemSpawnRows.Add(new ItemSpawnRowViewModel(
                    itemDefinition,
                    row,
                    _catalogState,
                    OnBundleChanged,
                    _hideUnavailableCatalogSections));
            }
            _removeItemSpawnCommand.RaiseCanExecuteChanged();
        }

        if (IsSpawnGroupBundle && VariantDefinition is { } variantDefinition)
        {
            SpawnGroupRows.Clear();
            var rows = Model.SpawnGroups.Count > 0 ? Model.SpawnGroups : [LegacySpawnGroupEntry(Model)];
            foreach (var row in rows)
            {
                SpawnGroupRows.Add(new SpawnGroupRowViewModel(
                    HandlerId,
                    variantDefinition,
                    row,
                    _catalogState,
                    OnBundleChanged,
                    _hideUnavailableCatalogSections));
            }
            _removeSpawnGroupCommand.RaiseCanExecuteChanged();
        }
    }

    private void RaiseEditorProperties()
    {
        RaisePropertyChanged(nameof(ParameterSummary));
        RaisePropertyChanged(nameof(ParameterSummaryItems));
        RaisePropertyChanged(nameof(CanTest));
        RaisePropertyChanged(nameof(AvailabilityHint));
    }

    private ParameterDefinition? ItemDefinition => Model.ParameterDefinitions.FirstOrDefault(definition =>
        string.Equals(definition.Key, "section_id", StringComparison.OrdinalIgnoreCase));

    private ParameterDefinition? VariantDefinition => IsSpawnGroupBundle
        ? Model.ParameterDefinitions.FirstOrDefault(definition =>
            string.Equals(definition.Key, "section_id", StringComparison.OrdinalIgnoreCase))
        : null;

    private bool IsBundledParameter(string key) =>
        IsItemBundle && (key.Equals("section_id", StringComparison.OrdinalIgnoreCase) || key.Equals("count", StringComparison.OrdinalIgnoreCase)) ||
        IsSpawnGroupBundle && (key.Equals("section_id", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("strength", StringComparison.OrdinalIgnoreCase) || key.Equals("count", StringComparison.OrdinalIgnoreCase));

    private static ItemSpawnEntry LegacyItemEntry(ActionDefinition model)
    {
        var section = model.Parameters.TryGetValue("section_id", out var value) ? value : string.Empty;
        var definition = model.ParameterDefinitions.FirstOrDefault(item => item.Key.Equals("section_id", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(section)) section = definition?.DefaultValue ?? string.Empty;
        var count = model.Parameters.TryGetValue("count", out var text) && int.TryParse(text, out var parsed)
            ? Math.Clamp(parsed, 1, 50)
            : 1;
        return new ItemSpawnEntry { ItemId = section, Count = count };
    }

    private static SpawnGroupEntry LegacySpawnGroupEntry(ActionDefinition model)
    {
        var section = model.Parameters.TryGetValue("section_id", out var value) ? value : string.Empty;
        var definition = model.ParameterDefinitions.FirstOrDefault(item => item.Key.Equals("section_id", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(section)) section = definition?.DefaultValue ?? string.Empty;
        var count = model.Parameters.TryGetValue("count", out var text) && int.TryParse(text, out var parsed)
            ? Math.Clamp(parsed, 1, 99)
            : model.HandlerId == "spawn_mutants" ? 3 : 1;
        return new SpawnGroupEntry { VariantId = section, Strength = "any", Count = count };
    }

    private void AddItemSpawn()
    {
        if (ItemDefinition is not { } definition) return;
        ItemSpawnRows.Add(new ItemSpawnRowViewModel(definition,
            new ItemSpawnEntry { ItemId = definition.DefaultValue, Count = 1 }, _catalogState, OnBundleChanged, _hideUnavailableCatalogSections));
        _removeItemSpawnCommand.RaiseCanExecuteChanged();
        OnBundleChanged();
    }

    private void RemoveItemSpawn(ItemSpawnRowViewModel row)
    {
        if (ItemSpawnRows.Count <= 1 || !ItemSpawnRows.Remove(row)) return;
        _removeItemSpawnCommand.RaiseCanExecuteChanged();
        OnBundleChanged();
    }

    private void AddSpawnGroup()
    {
        if (VariantDefinition is not { } definition) return;
        SpawnGroupRows.Add(new SpawnGroupRowViewModel(HandlerId, definition,
            new SpawnGroupEntry { VariantId = definition.DefaultValue, Strength = "any", Count = 1 },
            _catalogState, OnBundleChanged, _hideUnavailableCatalogSections));
        _removeSpawnGroupCommand.RaiseCanExecuteChanged();
        OnBundleChanged();
    }

    private void RemoveSpawnGroup(SpawnGroupRowViewModel row)
    {
        if (SpawnGroupRows.Count <= 1 || !SpawnGroupRows.Remove(row)) return;
        _removeSpawnGroupCommand.RaiseCanExecuteChanged();
        OnBundleChanged();
    }

    private static void SyncLegacyItemParameters(ActionDefinition action)
    {
        if (action.ItemSpawns.FirstOrDefault() is not { } first) return;
        action.Parameters["section_id"] = first.ItemId;
        action.Parameters["count"] = first.Count.ToString(CultureInfo.InvariantCulture);
    }

    private static void SyncLegacySpawnParameters(ActionDefinition action)
    {
        if (action.SpawnGroups.FirstOrDefault() is not { } first) return;
        action.Parameters["section_id"] = first.VariantId;
        action.Parameters["strength"] = "any";
        action.Parameters["count"] = first.Count.ToString(CultureInfo.InvariantCulture);
    }
}

public sealed record TriggerEditorResult(IReadOnlyList<TriggerRule> Rules, bool ReplaceAllPrices)
{
    public TriggerRule SingleRule => Rules.Count == 1
        ? Rules[0]
        : throw new InvalidOperationException("Результат содержит несколько ценовых условий.");
}

public sealed record CalculatedPriceViewModel(decimal Amount, string Currency)
{
    public string Label => $"{Amount:0.##} {Currency}";
}

public sealed class TriggerEditorViewModel : ObservableObject
{
    private TriggerComparator _comparator;
    private string _amountText;
    private string _currency;
    private string _error = string.Empty;
    private string _conversionStatus = string.Empty;
    private bool _isCalculating;
    private readonly IExchangeRateProvider _exchangeRateProvider;
    private readonly Func<IReadOnlyList<string>, Task>? _saveCalculationCurrencies;
    private IReadOnlyList<string> _calculationCurrencies;
    private int _currencySelectionRevision;
    private bool _isSavingCurrencySelection;

    public TriggerEditorViewModel(
        TriggerRule? existing,
        IEnumerable<string> currencies,
        IExchangeRateProvider? exchangeRateProvider = null,
        IEnumerable<string>? calculationCurrencies = null,
        Func<IReadOnlyList<string>, Task>? saveCalculationCurrencies = null)
    {
        Existing = existing;
        _comparator = existing?.Comparator ?? TriggerComparator.Exact;
        _amountText = (existing?.Amount ?? 1m).ToString("0.##", CultureInfo.CurrentCulture);
        _currency = existing?.Currency ?? "RUB";
        _exchangeRateProvider = exchangeRateProvider ?? new CbrExchangeRateProvider();
        _calculationCurrencies = DonationCurrencyPriceCalculator.NormalizeCurrencies(calculationCurrencies);
        _saveCalculationCurrencies = saveCalculationCurrencies;
        Currencies = new ObservableCollection<string>(currencies
            .Concat(DonationCurrencyPriceCalculator.SupportedCurrencies)
            .Select(value => Money.TryNormalizeCurrency(value, out var normalized) ? normalized : null)
            .Where(value => value is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal));
    }

    public TriggerRule? Existing { get; }
    public IReadOnlyList<TriggerComparator> Comparators { get; } = Enum.GetValues<TriggerComparator>();
    public ObservableCollection<string> Currencies { get; }
    public ObservableCollection<CalculatedPriceViewModel> CalculatedPrices { get; } = [];
    public IReadOnlyList<string> CalculationCurrencies => _calculationCurrencies;
    public bool CanConfigureCurrencies => !_isSavingCurrencySelection;
    public TriggerComparator Comparator
    {
        get => _comparator;
        set
        {
            if (SetProperty(ref _comparator, value)) ClearCalculatedPrices();
        }
    }
    public string AmountText
    {
        get => _amountText;
        set
        {
            if (SetProperty(ref _amountText, value)) ClearCalculatedPrices();
        }
    }
    public string Currency
    {
        get => _currency;
        set
        {
            if (SetProperty(ref _currency, value)) ClearCalculatedPrices();
        }
    }
    public string Error { get => _error; private set => SetProperty(ref _error, value); }
    public string ConversionStatus
    {
        get => _conversionStatus;
        private set => SetProperty(ref _conversionStatus, value);
    }
    public bool IsCalculating
    {
        get => _isCalculating;
        private set
        {
            if (!SetProperty(ref _isCalculating, value)) return;
            RaisePropertyChanged(nameof(CanCalculate));
            RaisePropertyChanged(nameof(CalculateButtonText));
        }
    }
    public bool CanCalculate => !IsCalculating && !_isSavingCurrencySelection && _calculationCurrencies.Count > 0;
    public bool HasCalculatedPrices => CalculatedPrices.Count > 0;
    public string CalculateButtonText => IsCalculating
        ? "Получаем актуальный курс…"
        : _calculationCurrencies.Count == 0 ? "Выберите валюты для расчёта →"
        : _calculationCurrencies.Count == DonationCurrencyPriceCalculator.SupportedCurrencies.Count
            ? "Рассчитать стоимость на все валюты"
            : "Рассчитать стоимость для выбранных валют";
    public string SaveButtonText => HasCalculatedPrices
        ? $"Заменить цены ({CalculatedPrices.Count})"
        : "Сохранить условие";

    public async Task CalculateAllAsync(CancellationToken cancellationToken = default)
    {
        if (IsCalculating || _isSavingCurrencySelection) return;
        if (_calculationCurrencies.Count == 0)
        {
            Error = "Выберите хотя бы одну валюту в настройках расчёта.";
            return;
        }
        var seed = BuildSeed();
        if (seed is null) return;
        var originalAmount = AmountText;
        var originalCurrency = Currency;
        var originalComparator = Comparator;
        var originalSelectionRevision = _currencySelectionRevision;
        var targetCurrencies = _calculationCurrencies.ToArray();
        ClearCalculatedPrices();
        IsCalculating = true;
        try
        {
            var snapshot = await _exchangeRateProvider.GetLatestAsync(cancellationToken);
            if (!string.Equals(originalAmount, AmountText, StringComparison.Ordinal) ||
                !string.Equals(originalCurrency, Currency, StringComparison.Ordinal) ||
                originalComparator != Comparator || originalSelectionRevision != _currencySelectionRevision)
            {
                Error = "Параметры расчёта изменились во время загрузки курса. Нажмите расчёт ещё раз.";
                return;
            }

            var rules = DonationCurrencyPriceCalculator.CalculateAll(seed, snapshot, targetCurrencies);
            foreach (var rule in rules)
            {
                CalculatedPrices.Add(new CalculatedPriceViewModel(rule.Amount, rule.Currency));
            }
            ConversionStatus = $"{snapshot.SourceName}, курс от {snapshot.EffectiveDate:dd.MM.yyyy}. " +
                               $"При сохранении прежние цены варианта будут заменены на {rules.Count} новых.";
            Error = string.Empty;
            RaiseCalculationProperties();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Error = "Получение курса отменено.";
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or InvalidOperationException or TaskCanceledException)
        {
            Error = "Не удалось рассчитать цены: " + exception.Message;
        }
        finally
        {
            IsCalculating = false;
        }
    }

    public async Task<bool> SetCalculationCurrenciesAsync(IEnumerable<string> currencies)
    {
        if (_isSavingCurrencySelection) return false;
        var selected = DonationCurrencyPriceCalculator.NormalizeCurrencies(currencies);
        if (_calculationCurrencies.SequenceEqual(selected, StringComparer.Ordinal)) return true;
        _isSavingCurrencySelection = true;
        RaisePropertyChanged(nameof(CanCalculate));
        RaisePropertyChanged(nameof(CanConfigureCurrencies));
        try
        {
            if (_saveCalculationCurrencies is not null) await _saveCalculationCurrencies(selected);
            _calculationCurrencies = selected;
            _currencySelectionRevision++;
            ClearCalculatedPrices();
            Error = string.Empty;
            RaisePropertyChanged(nameof(CalculationCurrencies));
            RaisePropertyChanged(nameof(CalculateButtonText));
            return true;
        }
        catch (Exception exception)
        {
            Error = "Не удалось сохранить валюты для расчёта: " + exception.Message;
            return false;
        }
        finally
        {
            _isSavingCurrencySelection = false;
            RaisePropertyChanged(nameof(CanCalculate));
            RaisePropertyChanged(nameof(CanConfigureCurrencies));
        }
    }

    public TriggerEditorResult? Build()
    {
        var seed = BuildSeed();
        if (seed is null) return null;
        if (!HasCalculatedPrices)
        {
            return new TriggerEditorResult([seed], ReplaceAllPrices: false);
        }

        var snapshot = CalculatedPrices
            .Select(price => new TriggerRule
            {
                Enabled = seed.Enabled,
                Comparator = seed.Comparator,
                Amount = price.Amount,
                Currency = price.Currency
            })
            .ToList();
        Error = string.Empty;
        return new TriggerEditorResult(snapshot, ReplaceAllPrices: true);
    }

    private TriggerRule? BuildSeed()
    {
        if (!decimal.TryParse(AmountText, NumberStyles.Number, CultureInfo.CurrentCulture, out var amount) &&
            !decimal.TryParse(AmountText, NumberStyles.Number, CultureInfo.InvariantCulture, out amount))
        {
            Error = "Введите корректную сумму.";
            return null;
        }
        string currency;
        try { currency = Money.NormalizeCurrency(Currency); }
        catch (ArgumentException exception) { Error = exception.Message; return null; }
        var result = Existing?.Clone() ?? new TriggerRule();
        result.Comparator = Comparator;
        result.Amount = amount;
        result.Currency = currency;
        var errors = Validation.ValidateTrigger(result);
        if (errors.Count > 0) { Error = string.Join(Environment.NewLine, errors); return null; }
        Error = string.Empty;
        return result;
    }

    private void ClearCalculatedPrices()
    {
        if (CalculatedPrices.Count == 0 && ConversionStatus.Length == 0) return;
        CalculatedPrices.Clear();
        ConversionStatus = string.Empty;
        RaiseCalculationProperties();
    }

    private void RaiseCalculationProperties()
    {
        RaisePropertyChanged(nameof(HasCalculatedPrices));
        RaisePropertyChanged(nameof(SaveButtonText));
    }
}
