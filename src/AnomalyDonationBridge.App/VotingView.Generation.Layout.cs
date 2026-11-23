using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.App;

public sealed partial class VotingView
{
    private readonly Grid _generationToolbar = new(), _generationFields = new();
    private readonly Grid _generationSourceRow = new(), _generationCommandRow = new();
    private readonly StackPanel _generationDetails = new();
    private readonly Button _generationDisclosure = new() { Height = 30, MinHeight = 30, Padding = new Thickness(8, 3, 8, 3) };
    private readonly TextBlock _generationDisclosureLabel = new(), _generationSummary = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly VectorIcon _generationDisclosureChevron = new() { Width = 10, Height = 10, Margin = new Thickness(7, 0, 0, 0) };
    private FrameworkElement _generationSourceField = null!, _generationPoolField = null!, _generationCountField = null!,
        _generationPrefixField = null!, _generationFormatField = null!;
    private bool _generationCollapsed;
    private int _generationLayoutRows;

    private FrameworkElement BuildGenerationToolbar()
    {
        Grid.SetIsSharedSizeScope(_generationToolbar, true);
        _generationToolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto, SharedSizeGroup = "CommandToolbarButton" });
        _generationToolbar.ColumnDefinitions.Add(new() { MinWidth = 12 });
        _generationToolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _generationToolbar.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _generationToolbar.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _generationToolbar.Children.Add(_generationToggle);
        ButtonVisual.SetGlyph(_add, "\uE710");
        _add.ToolTip = "Добавить команду. Шаблон также можно добавить из его меню.";
        _add.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_add, 2); _generationToolbar.Children.Add(_add);
        var disclosure = new StackPanel { Orientation = Orientation.Horizontal };
        disclosure.Children.Add(_generationDisclosureLabel); disclosure.Children.Add(_generationDisclosureChevron);
        _generationDisclosure.Content = disclosure;
        Grid.SetColumn(_generationDisclosure, 2); _generationToolbar.Children.Add(_generationDisclosure);
        _generationSummary.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _generationSummary.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_generationSummary, 1); _generationToolbar.Children.Add(_generationSummary);
        _generationDisclosure.Click += async (_, _) =>
        {
            var collapsed = _generationCollapsed = !_generationCollapsed;
            _generationPopup.IsOpen = false; _generationTip.IsOpen = false;
            UpdateDisplay();
            try { await _runtime.UpdateUiPreferencesAsync(current => current with { VotingGenerationPanelCollapsed = collapsed }); }
            catch (Exception error) { _message.Text = "Не удалось сохранить вид окна: " + error.Message; }
        };
        _generationToolbar.SizeChanged += (_, e) => { if (e.WidthChanged) UpdateGenerationToolbar(); };
        var border = new Border { Child = _generationToolbar, BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 3, 0, 12), Margin = new Thickness(0, 0, 0, 8) };
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return border;
    }

    private void UpdateGenerationToolbar()
    {
        var automatic = _generationToggle.IsChecked == true;
        _generationDisclosure.Visibility = automatic ? Visibility.Visible : Visibility.Collapsed;
        _generationDisclosureLabel.Text = _generationCollapsed ? "Параметры" : "Свернуть";
        _generationDisclosureChevron.Glyph = _generationCollapsed ? "\uE70D" : "\uE70E";
        AutomationProperties.SetName(_generationDisclosure, _generationCollapsed ? "Показать параметры авто-формирования" : "Свернуть параметры авто-формирования");
        _generationSummary.Visibility = automatic && _generationCollapsed ? Visibility.Visible : Visibility.Collapsed;
        var source = _generationDraft.Source == VotingTemplateSource.Active ? "Активные" : "Пользовательские";
        var prefix = _generationPrefix.Text.Length == 0 ? "без символа" : _generationPrefix.Text;
        var format = _generationDraft.Format == VotingCommandFormat.Number ? "цифра" : "сокращение";
        _generationSummary.Text = $"{source} · команд: {_generationCount.Text} · {prefix} / {format}";
        _generationSummary.ToolTip = _generationSummary.Text;
        var narrow = _generationToolbar.ActualWidth < 540;
        Grid.SetRow(_generationSummary, narrow ? 1 : 0); Grid.SetColumn(_generationSummary, narrow ? 0 : 1);
        Grid.SetColumnSpan(_generationSummary, narrow ? 3 : 1);
        _generationSummary.Margin = narrow ? new Thickness(0, 8, 0, 0) : new Thickness(12, 0, 12, 0);
        var wrapAdd = !automatic && _generationToolbar.ActualWidth < 360;
        Grid.SetRow(_add, wrapAdd ? 1 : 0); Grid.SetColumn(_add, wrapAdd ? 0 : 2);
        _add.Margin = wrapAdd ? new Thickness(0, 8, 0, 0) : new Thickness(0);
        if (!automatic && !wrapAdd) _generationToolbar.ColumnDefinitions[2].SharedSizeGroup = "CommandToolbarButton";
        else _generationToolbar.ColumnDefinitions[2].ClearValue(ColumnDefinition.SharedSizeGroupProperty);
    }

    private FrameworkElement BuildGenerationFields()
    {
        foreach (var grid in new[] { _generationFields, _generationSourceRow, _generationCommandRow })
        {
            grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new() { Height = new GridLength(10) });
            grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        }
        _generationFields.Children.Add(_generationSourceRow);
        Grid.SetRow(_generationCommandRow, 2); _generationFields.Children.Add(_generationCommandRow);
        _generationSourceRow.ColumnDefinitions.Add(new());
        _generationSourceRow.ColumnDefinitions.Add(new() { Width = new GridLength(12) });
        _generationSourceRow.ColumnDefinitions.Add(new());
        foreach (var width in new[] { new GridLength(110), new GridLength(12), new GridLength(80), new GridLength(12), new GridLength(1, GridUnitType.Star) })
            _generationCommandRow.ColumnDefinitions.Add(new() { Width = width });

        _generationSourceField = GenerationField(SecondaryText("Источник"), GenerationPair(_generationActive, _generationUser, 0));
        _generationSourceRow.Children.Add(_generationSourceField);
        _generationActive.Click += (_, _) => SetGenerationSource(VotingTemplateSource.Active);
        _generationUser.Click += (_, _) => SetGenerationSource(VotingTemplateSource.UserPresets);
        _generationPicker.Style = (Style)FindResource("VotingPresetPickerStyle");
        AutomationProperties.SetName(_generationPicker, "Выбрать шаблоны для автоматического голосования");
        _generationPicker.Click += (_, _) => OpenGenerationPicker();
        _generationPoolField = GenerationField(SecondaryText("Шаблоны"), _generationPicker);
        _generationSourceRow.Children.Add(_generationPoolField);

        var countLabel = new DockPanel();
        var range = SecondaryText("2–99"); range.HorizontalAlignment = HorizontalAlignment.Right; range.FontSize = 11;
        DockPanel.SetDock(range, Dock.Right); countLabel.Children.Add(range); countLabel.Children.Add(SecondaryText("Команд"));
        CompactInput(_generationCount, "Количество автоматически формируемых команд");
        _generationCountField = GenerationField(countLabel, _generationCount);
        _generationCommandRow.Children.Add(_generationCountField);
        CompactInput(_generationPrefix, "Необязательный символ перед командой");
        _generationPrefix.ToolTip = "Один символ перед командой или пустое поле.";
        _generationPrefixField = GenerationField(SecondaryText("Символ"), _generationPrefix);
        Grid.SetColumn(_generationPrefixField, 2); _generationCommandRow.Children.Add(_generationPrefixField);

        var formatLabel = new DockPanel();
        var helpIcon = new VectorIcon { Glyph = "\uE897", Width = 14, Height = 14,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        helpIcon.SetResourceReference(VectorIcon.ForegroundProperty, "MutedBrush");
        _generationHelp.Width = _generationHelp.Height = 20; _generationHelp.Child = helpIcon;
        AutomationProperties.SetName(_generationHelp, "Примеры формирования команд");
        _generationTip.Content = _generationTipContent; _generationTip.PlacementTarget = _generationHelp;
        _generationHelp.ToolTip = _generationTip;
        ToolTipService.SetInitialShowDelay(_generationHelp, 200); ToolTipService.SetShowDuration(_generationHelp, 60000);
        DockPanel.SetDock(_generationHelp, Dock.Right); formatLabel.Children.Add(_generationHelp); formatLabel.Children.Add(SecondaryText("Формат"));
        _generationFormatField = GenerationField(formatLabel, GenerationPair(_generationNumbers, _generationInitials, 0));
        _generationCommandRow.Children.Add(_generationFormatField);
        _generationNumbers.Click += (_, _) => { _generationDraft.Format = VotingCommandFormat.Number; GenerationEdited(); };
        _generationInitials.Click += (_, _) => { _generationDraft.Format = VotingCommandFormat.Initials; GenerationEdited(); };
        _generationFields.SizeChanged += (_, e) => { if (e.WidthChanged && e.NewSize.Width > 0) UpdateGenerationFieldsLayout(e.NewSize.Width); };
        UpdateGenerationFieldsLayout(700);
        return _generationFields;
    }

    private static FrameworkElement GenerationField(FrameworkElement heading, FrameworkElement editor)
    {
        heading.Height = 20; heading.VerticalAlignment = VerticalAlignment.Center;
        editor.Margin = new Thickness(0, 4, 0, 0);
        var field = new StackPanel(); field.Children.Add(heading); field.Children.Add(editor);
        return field;
    }

    private void UpdateGenerationFieldsLayout(double width)
    {
        var rows = width >= 600 ? 2 : width >= 440 ? 3 : 4;
        if (_generationLayoutRows == rows) return;
        _generationLayoutRows = rows;
        var wide = rows == 2;
        _generationSourceRow.ColumnDefinitions[1].Width = new GridLength(wide ? 12 : 0);
        _generationSourceRow.ColumnDefinitions[2].Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        _generationSourceRow.RowDefinitions[1].Height = new GridLength(wide ? 0 : 10);
        _generationSourceRow.RowDefinitions[2].Height = wide ? new GridLength(0) : GridLength.Auto;
        Grid.SetColumnSpan(_generationSourceField, wide ? 1 : 3);
        Grid.SetRow(_generationPoolField, wide ? 0 : 2); Grid.SetColumn(_generationPoolField, wide ? 2 : 0);
        Grid.SetColumnSpan(_generationPoolField, wide ? 1 : 3);
        var stacked = rows == 4;
        _generationCommandRow.ColumnDefinitions[0].Width = new GridLength(stacked ? 80 : 110);
        _generationCommandRow.ColumnDefinitions[2].Width = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(80);
        _generationCommandRow.ColumnDefinitions[3].Width = new GridLength(stacked ? 0 : 12);
        _generationCommandRow.ColumnDefinitions[4].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        _generationCommandRow.RowDefinitions[1].Height = new GridLength(stacked ? 10 : 0);
        _generationCommandRow.RowDefinitions[2].Height = stacked ? GridLength.Auto : new GridLength(0);
        Grid.SetColumnSpan(_generationCountField, stacked ? 5 : 1);
        Grid.SetRow(_generationPrefixField, stacked ? 2 : 0); Grid.SetColumn(_generationPrefixField, stacked ? 0 : 2);
        Grid.SetRow(_generationFormatField, stacked ? 2 : 0); Grid.SetColumn(_generationFormatField, stacked ? 2 : 4);
        Grid.SetColumnSpan(_generationFormatField, stacked ? 3 : 1);
    }
}
