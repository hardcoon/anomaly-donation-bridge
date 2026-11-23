using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Documents;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.App;

public sealed class VotingTemplateChoice(string id, string name) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    private bool _selected;
    public bool Selected { get => _selected; set => SetProperty(ref _selected, value); }
}

public sealed partial class VotingView
{
    private readonly CheckBox _generationToggle = new();
    private readonly Button _generationActive = new() { Content = "Активные" };
    private readonly Button _generationUser = new() { Content = "Пользовательские" };
    private readonly Button _generationPicker = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _generationCount = new() { Text = "2", MaxLength = 2 };
    private readonly TextBox _generationPrefix = new() { Text = "!", MaxLength = 2, Tag = "Нет" };
    private readonly Button _generationNumbers = new() { Content = "Цифра" };
    private readonly Button _generationInitials = new() { Content = "Сокращение" };
    private readonly Border _generationHelp = new() { Width = 24, Height = 24, Padding = new Thickness(2), Background = Brushes.Transparent };
    private readonly TextBlock _generationNote = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _generationProblem = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly Popup _generationPopup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom };
    private readonly CheckBox _generationAll = new() { Content = "Выбрать все", Margin = new Thickness(8, 7, 8, 8) };
    private readonly ObservableCollection<VotingTemplateChoice> _generationChoices = [];
    private readonly ListBox _generationChoicesList = new() { MaxHeight = 310, BorderThickness = new Thickness(0) };
    private readonly ToolTip _generationTip = new() { Placement = PlacementMode.Top, MaxWidth = 420, Padding = new Thickness(12), Focusable = false, IsHitTestVisible = false };
    private readonly StackPanel _generationTipContent = new();
    private VotingGenerationSettings _generationDraft = new();
    private FrameworkElement _generationForm = null!, _commandHeading = null!, _commandLabels = null!, _commandTools = null!;
    private bool _updatingGenerationChoices;
    private string _generationChoiceSignature = "";

    private FrameworkElement BuildGenerationToggle()
    {
        _generationToggle.SetResourceReference(StyleProperty, "VotingAutoFormationButtonStyle");
        _generationToggle.Content = "Авто-формирование";
        AutomationProperties.SetName(_generationToggle, "Авто-формирование");
        _generationToggle.Checked += (_, _) => GenerationEdited();
        _generationToggle.Unchecked += (_, _) => GenerationEdited();
        return BuildGenerationToolbar();
    }

    private FrameworkElement BuildGenerationForm()
    {
        var form = new StackPanel { Margin = new Thickness(0, 2, 0, 12), HorizontalAlignment = HorizontalAlignment.Stretch };
        _generationDetails.Children.Add(BuildGenerationFields());
        _generationNote.Margin = new Thickness(0, 10, 0, 0); _generationNote.SetResourceReference(ForegroundProperty, "MutedBrush");
        _generationProblem.Margin = new Thickness(0, 8, 0, 0); _generationProblem.SetResourceReference(ForegroundProperty, "BadBrush");
        _generationDetails.Children.Add(_generationNote);
        form.Children.Add(_generationDetails); form.Children.Add(_generationProblem);
        _generationCount.TextChanged += (_, _) => GenerationEdited();
        _generationPrefix.TextChanged += (_, _) => GenerationEdited();

        _generationChoicesList.Style = (Style)FindResource("VotingCommandListStyle");
        _generationChoicesList.ItemsSource = _generationChoices;
        _generationChoicesList.ItemTemplate = (DataTemplate)FindResource("VotingGenerationChoiceTemplate");
        _generationChoicesList.SetResourceReference(BackgroundProperty, "PanelBrush");
        _generationAll.Click += (_, _) =>
        {
            var all = _generationChoices.Count > 0 && _generationChoices.All(choice => choice.Selected);
            SetGenerationSelection(all ? new HashSet<string>(StringComparer.Ordinal) : null);
            RefreshGenerationChoices(); GenerationEdited();
        };
        var popupBody = new DockPanel();
        DockPanel.SetDock(_generationAll, Dock.Top); popupBody.Children.Add(_generationAll); popupBody.Children.Add(_generationChoicesList);
        var popupBorder = new Border { Child = popupBody, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(3) };
        popupBorder.SetResourceReference(BackgroundProperty, "PanelBrush"); popupBorder.SetResourceReference(Border.BorderBrushProperty, "BorderStrongBrush");
        _generationChoicesList.SetResourceReference(ForegroundProperty, "TextBrush");
        popupBorder.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
        _generationPopup.Child = popupBorder; _generationPopup.PlacementTarget = _generationPicker;
        _generationForm = form;
        return form;
    }

    private static Grid GenerationPair(Button left, Button right, double top)
    {
        var grid = new Grid { Margin = new Thickness(0, top, 0, 0) };
        grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new());
        foreach (var button in new[] { left, right }) { button.Height = button.MinHeight = 31; button.Padding = new Thickness(6, 0, 6, 0); }
        Grid.SetColumn(right, 1); grid.Children.Add(left); grid.Children.Add(right);
        return grid;
    }

    private void SetGenerationSource(VotingTemplateSource source)
    {
        _generationDraft.Source = source; _generationChoiceSignature = "";
        RefreshGenerationChoices(); GenerationEdited();
    }
    private void SetGenerationSelection(HashSet<string>? selected)
    {
        if (_generationDraft.Source == VotingTemplateSource.Active) _generationDraft.ActiveTemplateIds = selected;
        else _generationDraft.UserTemplateIds = selected;
    }
    private void GenerationEdited()
    {
        if (_loading || _closed || _updatingGenerationChoices) return;
        Dirty();
    }
    private VotingGenerationSettings CaptureGeneration()
    {
        var settings = _generationDraft.Clone(); settings.Enabled = _generationToggle.IsChecked == true;
        if (settings.Enabled)
        {
            if (!int.TryParse(_generationCount.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
                throw new InvalidOperationException("Количество команд: целое число от 2 до 99.");
            settings.Count = count; settings.Prefix = _generationPrefix.Text;
        }
        VotingGeneration.ValidateConfiguration(settings);
        return settings;
    }
    private void LoadGeneration(VotingRound round)
    {
        _generationPopup.IsOpen = false; _generationTip.IsOpen = false;
        _generationDraft = round.Generation.Clone();
        _generationToggle.IsChecked = _generationDraft.Enabled;
        _generationCount.Text = _generationDraft.Count.ToString(CultureInfo.InvariantCulture);
        _generationPrefix.Text = _generationDraft.Prefix;
        _generationChoiceSignature = ""; RefreshGenerationChoices();
    }
    private void OpenGenerationPicker()
    {
        RefreshGenerationChoices();
        if (_generationPopup.Child is FrameworkElement body) body.Width = Math.Max(200, _generationPicker.ActualWidth);
        _generationPopup.IsOpen = !_generationPopup.IsOpen;
    }
    private void RefreshGenerationChoices()
    {
        var templates = _runtime.VotingTemplates(_profileId, _generationDraft.Source);
        var signature = _profileId + _generationDraft.Source + string.Join("|", templates.Select(template => template.Id + ":" + template.DisplayName));
        _updatingGenerationChoices = true;
        try
        {
            if (signature != _generationChoiceSignature)
            {
                _generationChoices.Clear();
                foreach (var template in templates.DistinctBy(template => template.Id))
                {
                    var choice = new VotingTemplateChoice(template.Id, template.DisplayName);
                    choice.PropertyChanged += GenerationChoiceChanged; _generationChoices.Add(choice);
                }
                _generationChoiceSignature = signature;
            }
            foreach (var choice in _generationChoices) choice.Selected = _generationDraft.SelectedIds is null || _generationDraft.SelectedIds.Contains(choice.Id);
            var count = _generationChoices.Count(choice => choice.Selected);
            _generationAll.IsChecked = count == _generationChoices.Count && count > 0 ? true : count == 0 ? false : null;
            _generationAll.IsEnabled = _generationChoices.Count > 0;
            _generationPicker.Content = $"{(count == _generationChoices.Count ? "Все шаблоны" : "Выбрано шаблонов")} · {count} из {_generationChoices.Count}";
        }
        finally { _updatingGenerationChoices = false; }
    }
    private void GenerationChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_updatingGenerationChoices || e.PropertyName != nameof(VotingTemplateChoice.Selected)) return;
        var ids = _generationChoices.Where(choice => choice.Selected).Select(choice => choice.Id).ToHashSet(StringComparer.Ordinal);
        SetGenerationSelection(ids.Count == _generationChoices.Count ? null : ids);
        RefreshGenerationChoices(); GenerationEdited();
    }
    private void UpdateGeneration(VotingRound round, bool canEdit)
    {
        var automatic = _generationToggle.IsChecked == true;
        _generationDetails.Visibility = _generationCollapsed ? Visibility.Collapsed : Visibility.Visible;
        _add.Visibility = automatic ? Visibility.Collapsed : Visibility.Visible;
        var showRows = !automatic || round.Phase != VotingPhase.Draft && round.Options.Count > 0;
        _commandHeading.Visibility = _commandLabels.Visibility = _commandList.Visibility = _commandTools.Visibility = showRows ? Visibility.Visible : Visibility.Collapsed;
        _generationToggle.IsEnabled = _generationForm.IsEnabled = canEdit;
        HighlightGenerationButton(_generationActive, _generationDraft.Source == VotingTemplateSource.Active);
        HighlightGenerationButton(_generationUser, _generationDraft.Source == VotingTemplateSource.UserPresets);
        HighlightGenerationButton(_generationNumbers, _generationDraft.Format == VotingCommandFormat.Number);
        HighlightGenerationButton(_generationInitials, _generationDraft.Format == VotingCommandFormat.Initials);
        var error = "";
        if (automatic)
        {
            try
            {
                var settings = CaptureGeneration();
                var n = settings.Count % 100; var last = settings.Count % 10;
                var words = n is >= 11 and <= 14 ? "случайных шаблонов" : last == 1 ? "случайный шаблон" :
                    last is >= 2 and <= 4 ? "случайных шаблона" : "случайных шаблонов";
                _generationNote.Text = $"При каждом старте: {settings.Count} {words} из отмеченных.";
                VotingGeneration.ValidateAvailability(settings, _runtime.VotingTemplates(_profileId, settings.Source));
            }
            catch (InvalidOperationException problem) { error = problem.Message; }
        }
        _generationProblem.Text = error; _generationProblem.Visibility = error.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _generationForm.Visibility = automatic && (!_generationCollapsed || error.Length > 0) ? Visibility.Visible : Visibility.Collapsed;
        UpdateGenerationToolbar();
        if (automatic && error.Length > 0 && round.Phase != VotingPhase.Paused) _collectionToggle.IsEnabled = false;
        if (automatic && !_rows.Select(row => row.Model.Id).SequenceEqual(round.Options.Select(option => option.Id)))
        {
            var loading = _loading; _loading = true;
            _rows.Clear(); foreach (var option in round.Options) AddRow(option);
            _loading = loading;
        }
        _commandList.ItemTemplate = (DataTemplate)FindResource(automatic ? "VotingGeneratedCommandRowTemplate" : "VotingCommandRowTemplate");
        UpdateGenerationHelp();
    }
    private static void HighlightGenerationButton(Button button, bool selected)
    {
        foreach (var property in new[] { BackgroundProperty, ForegroundProperty, BorderBrushProperty }) button.ClearValue(property);
        if (!selected) return;
        button.SetResourceReference(BackgroundProperty, "WarningPanelBrush");
        button.SetResourceReference(ForegroundProperty, "AccentForegroundBrush");
        button.SetResourceReference(BorderBrushProperty, "AccentBrush");
    }
    private string _generationHelpSignature = "";
    private void UpdateGenerationHelp()
    {
        var settings = _generationDraft.Clone(); settings.Prefix = _generationPrefix.Text;
        var signature = settings.Format + settings.Prefix;
        if (_generationHelpSignature == signature) return;
        try { VotingGeneration.ValidateConfiguration(settings); } catch (InvalidOperationException) { return; }
        _generationHelpSignature = signature;
        _generationTipContent.Children.Clear();
        _generationTipContent.Children.Add(new TextBlock { Text = "Пример формирования команд", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        var names = new[] { "БТР атакует", "Напарник с именем донатчика", "Случайный телепорт", "Странствующий торговец" };
        var commands = VotingGeneration.Commands(names, settings);
        for (var i = 0; i < names.Length; i++)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(86) });
            row.Children.Add(new TextBlock { Text = names[i], TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
            var code = new TextBlock { Text = commands[i], Margin = new Thickness(7, 5, 7, 5) };
            code.SetResourceReference(ForegroundProperty, "AccentForegroundBrush");
            var surface = new Border { Child = code, CornerRadius = new CornerRadius(2) };
            surface.SetResourceReference(BackgroundProperty, "InputBrush"); Grid.SetColumn(surface, 1); row.Children.Add(surface);
            _generationTipContent.Children.Add(row);
        }
        var note = SecondaryText(settings.Format == VotingCommandFormat.Initials ?
            "Первые буквы слов. При совпадении добавляется номер." : "Порядковый номер команды в сформированном списке.");
        note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(0, 9, 0, 0);
        _generationTipContent.Children.Add(note);
    }
}
