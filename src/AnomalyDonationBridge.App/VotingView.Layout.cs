using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace AnomalyDonationBridge.App;

public sealed partial class VotingView
{
    private bool _narrow;
    private ColumnDefinition _navigationColumn = null!, _playerMetricsColumn = null!;

    private UIElement Build(bool hostPlayerSeparately)
    {
        var shell = new Grid();
        shell.SetResourceReference(Panel.BackgroundProperty, "BackgroundBrush");
        _navigationColumn = new ColumnDefinition { Width = new GridLength(182) };
        shell.ColumnDefinitions.Add(_navigationColumn);
        shell.ColumnDefinitions.Add(new ColumnDefinition());
        shell.RowDefinitions.Add(new RowDefinition());
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.Children.Add(BuildNavigation());

        var main = new Grid();
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition());
        main.Children.Add(BuildHeader());
        _collectionPanel = BuildCollection();
        _generalPanel = BuildGeneral();
        _generalPanel.Margin = new Thickness(17, 14, 10, 12);
        foreach (var page in new[] { _collectionPanel, _generalPanel })
        {
            Grid.SetRow(page, 1);
            main.Children.Add(page);
        }
        Grid.SetColumn(main, 1);
        shell.Children.Add(main);

        _message.FontSize = 12;
        _message.Margin = new Thickness(15, 4, 15, 7);
        _message.SetResourceReference(StyleProperty, "VotingFeedbackStyle");
        _message.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        AutomationProperties.SetLiveSetting(_message, AutomationLiveSetting.Polite);
        _saveProblem.Margin = new Thickness(15, 5, 15, 5);
        _saveProblem.SetResourceReference(StyleProperty, "VotingFeedbackStyle");
        _saveProblem.SetResourceReference(TextBlock.ForegroundProperty, "BadBrush");
        AutomationProperties.SetLiveSetting(_saveProblem, AutomationLiveSetting.Polite);
        var feedback = new StackPanel();
        feedback.Children.Add(_saveProblem);
        feedback.Children.Add(_message);
        Grid.SetRow(feedback, 1);
        Grid.SetColumnSpan(feedback, 2);
        shell.Children.Add(feedback);

        BuildPlayer();
        shell.SizeChanged += (_, e) => UpdateWidth(e.NewSize.Width);
        if (hostPlayerSeparately) return shell;

        DockPanel.SetDock(_player, Dock.Top);
        _player.Margin = new Thickness(0, 0, 0, 8);
        return new DockPanel { Children = { _player, shell } };
    }

    private void UpdateWidth(double width)
    {
        var narrow = width < 800;
        _navigationColumn.Width = new GridLength(width < 515 ? 124 : narrow ? 148 : 182);
        ButtonVisual.SetShowLabel(_generalButton, width >= 515);
        if (_narrow == narrow) return;

        _narrow = narrow;
        DockPanel.SetDock(_headerActions, narrow ? Dock.Bottom : Dock.Right);
        _headerActions.Margin = narrow ? new Thickness(0, 6, 0, 0) : new Thickness(10, 0, 0, 0);
        _headerActions.HorizontalAlignment = narrow ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        ApplyPanelLayout();
    }

    private FrameworkElement BuildNavigation()
    {
        var add = MakeButton("Добавить голосование", async (_, _) => await RunAsync(async () =>
        {
            await SaveSettingsAsync();
            _selectedId = await _runtime.Voting.AddCollectionAsync(sourceCollectionId: _selectedId, profileId: _profileId);
            _generalSelected = false;
            LoadDraft();
        }));
        ButtonVisual.SetGlyph(add, "\uE710");
        ButtonVisual.SetShowLabel(add, false);
        add.Width = add.Height = 30;
        add.MinHeight = 30;
        add.Padding = new Thickness(0);
        add.ToolTip = "Добавить голосование";
        AutomationProperties.SetName(add, "Добавить голосование");

        var caption = new DockPanel { Margin = new Thickness(6, 0, 0, 9) };
        DockPanel.SetDock(add, Dock.Right);
        caption.Children.Add(add);
        var listTitle = SecondaryText("Голосования");
        listTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        listTitle.VerticalAlignment = VerticalAlignment.Center;
        caption.Children.Add(listTitle);

        ButtonVisual.SetGlyph(_generalButton, "\uE713");
        _generalButton.FontSize = 12;
        _generalButton.Padding = new Thickness(6, 5, 6, 5);
        _generalButton.Margin = new Thickness(0, 8, 0, 0);
        _generalButton.Click += async (_, _) => await RunAsync(() => SelectPageAsync(null));

        var navigation = new DockPanel { Margin = new Thickness(10, 13, 10, 13) };
        DockPanel.SetDock(caption, Dock.Top);
        DockPanel.SetDock(_generalButton, Dock.Bottom);
        navigation.Children.Add(caption);
        navigation.Children.Add(_generalButton);
        navigation.Children.Add(VerticalScroll(_sidebar));
        var border = new Border { Child = navigation, BorderThickness = new Thickness(0, 0, 1, 0) };
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        border.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        return border;
    }

    private FrameworkElement BuildHeader()
    {
        ButtonVisual.SetGlyph(_collectionToggle, "\uE768");
        _collectionToggle.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        SetDisclosureIcon(_panelToggle, "\uE9E9");
        ButtonVisual.SetGlyph(_reset, "\uE7A7");
        _reset.ToolTip = "Сбросить прогресс этого голосования";
        _delete.SetResourceReference(ContentControl.ContentTemplateProperty, "VectorSymbolContentTemplate");
        _delete.SetResourceReference(StyleProperty, "DangerButtonStyle");
        _delete.SetResourceReference(ForegroundProperty, "BadBrush");
        foreach (var button in new[] { _collectionToggle, _panelToggle, _reset, _delete })
            _headerActions.Children.Add(button);

        _rename.Content = "\uE70F";
        _rename.SetResourceReference(ContentControl.ContentTemplateProperty, "VectorSymbolContentTemplate");
        _rename.FontSize = 13;
        _rename.ToolTip = "Переименовать голосование";
        AutomationProperties.SetName(_rename, "Переименовать голосование");
        _rename.SetResourceReference(StyleProperty, "GhostButtonStyle");
        _rename.Margin = new Thickness(3, 0, 0, 0);
        _rename.Width = 30;
        _rename.MinHeight = _rename.Height = 29;
        _rename.Padding = new Thickness(0);
        var titleRow = new DockPanel { HorizontalAlignment = HorizontalAlignment.Left };
        DockPanel.SetDock(_rename, Dock.Right);
        titleRow.Children.Add(_rename);
        titleRow.Children.Add(_title);

        _statusDot.Margin = new Thickness(0, 0, 5, 0);
        _statusDot.FontSize = 9;
        _statusDot.SetResourceReference(TextBlock.ForegroundProperty, "GoodBrush");
        _compactSummary.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var meta = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        meta.Children.Add(_statusDot);
        meta.Children.Add(_collectionToggleLabel);
        meta.Children.Add(_compactSummary);
        var heading = new StackPanel();
        heading.Children.Add(titleRow);
        heading.Children.Add(meta);

        var header = new DockPanel();
        DockPanel.SetDock(_headerActions, Dock.Right);
        header.Children.Add(_headerActions);
        header.Children.Add(heading);
        var border = new Border
        {
            Child = header, Padding = new Thickness(17, 13, 16, 12), MinHeight = 78,
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return border;
    }

    private FrameworkElement BuildCollection()
    {
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        _settingsColumn = new ColumnDefinition();
        columns.ColumnDefinitions.Add(_settingsColumn);
        columns.Children.Add(BuildCommands());

        _settings.Children.Add(VerticalScroll(BuildSettings()));
        _settingsCard = new Border { Child = _settings, BorderThickness = new Thickness(1, 0, 0, 0) };
        _settingsCard.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        _settingsCard.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        Grid.SetColumn(_settingsCard, 1);
        columns.Children.Add(_settingsCard);
        return columns;
    }

    private FrameworkElement BuildCommands()
    {
        var commands = _commandsPanel;
        commands.Margin = new Thickness(16, 12, 15, 10);
        commands.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        commands.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        commands.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        commands.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        commands.RowDefinitions.Add(new RowDefinition());
        commands.Children.Add(BuildGenerationToggle());
        var generation = BuildGenerationForm();
        Grid.SetRow(generation, 1); commands.Children.Add(generation);

        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var title = Heading("Команды");
        title.VerticalAlignment = VerticalAlignment.Center;
        heading.Children.Add(title);
        _commandHeading = heading;
        Grid.SetRow(heading, 2); commands.Children.Add(heading);

        var labels = (Grid)FindResource("VotingCommandHeadings");
        _commandLabels = labels;
        Grid.SetRow(labels, 3);
        commands.Children.Add(labels);

        _commandList.Style = (Style)FindResource("VotingCommandListStyle");
        _commandList.ItemsSource = _rows;
        _commandList.ItemTemplate = (DataTemplate)FindResource("VotingCommandRowTemplate");
        AutomationProperties.SetName(_commandList, "Команды голосования");
        var tools = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        _selectedSpin.SetResourceReference(StyleProperty, "AccentOutlineButtonStyle");
        ButtonVisual.SetGlyph(_selectedSpin, "\uE768");
        DockPanel.SetDock(_selectedSpin, Dock.Right);
        tools.Children.Add(_selectedSpin);
        _testPanelToggle.SetResourceReference(StyleProperty, "GhostButtonStyle");
        SetDisclosureIcon(_testPanelToggle, "\uE9F5");
        _testPanelToggle.HorizontalAlignment = HorizontalAlignment.Left;
        _testPanelToggle.ToolTip = "Тестовый взнос без запуска обычного эффекта доната";
        AutomationProperties.SetName(_testPanelToggle, "Показать тестовый взнос");
        _testPanelToggle.Tag = "\uE70D";
        _testPanelToggle.Click += (_, _) =>
        {
            var open = _testFields.Visibility != Visibility.Visible;
            _testFields.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            _testPanelToggle.Tag = open ? "\uE70E" : "\uE70D";
            AutomationProperties.SetName(_testPanelToggle, open ? "Скрыть тестовый взнос" : "Показать тестовый взнос");
            if (open && string.IsNullOrWhiteSpace(_testMessage.Text))
                _testMessage.Text = _rows.FirstOrDefault(row => row.Enabled && row.Command.Length > 0)?.Command ?? "";
        };
        tools.Children.Add(_testPanelToggle);
        _commandTools = tools;
        _testFields = BuildTestFields();
        _testFields.Visibility = Visibility.Collapsed;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _status.SetResourceReference(StyleProperty, "VotingFeedbackStyle");
        _status.Margin = new Thickness(0, 6, 0, 0);
        var entries = new DockPanel { VerticalAlignment = VerticalAlignment.Top };
        // Reserve the controls below the list before measuring its scroll viewport.
        foreach (var child in new UIElement[] { _status, _testFields, tools })
        {
            DockPanel.SetDock(child, Dock.Bottom);
            entries.Children.Add(child);
        }
        entries.Children.Add(_commandList);
        System.Windows.Input.KeyboardNavigation.SetTabNavigation(entries, System.Windows.Input.KeyboardNavigationMode.Local);
        System.Windows.Input.KeyboardNavigation.SetTabIndex(_commandList, 0);
        System.Windows.Input.KeyboardNavigation.SetTabIndex(tools, 1);
        System.Windows.Input.KeyboardNavigation.SetTabIndex(_testFields, 2);
        Grid.SetRow(entries, 4);
        commands.Children.Add(entries);
        return commands;
    }

    private FrameworkElement BuildTestFields()
    {
        var testRow = new Grid();
        testRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        testRow.ColumnDefinitions.Add(new ColumnDefinition());
        testRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _testMessage.Margin = new Thickness(7, 0, 7, 0);
        _test.Margin = new Thickness(0);
        _test.Padding = new Thickness(10, 3, 10, 3);
        CompactInput(_testAmount, "Сумма тестового взноса");
        CompactInput(_testMessage, "Команда тестового взноса");
        testRow.Children.Add(_testAmount);
        Grid.SetColumn(_testMessage, 1);
        testRow.Children.Add(_testMessage);
        Grid.SetColumn(_test, 2);
        testRow.Children.Add(_test);
        return Card(testRow, 7);
    }

    private FrameworkElement BuildSettings()
    {
        var fields = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
        fields.Children.Add(Heading("Настройки голосования"));
        fields.Children.Add(_autosaveStatus);
        fields.Children.Add(SectionHeading("Условия завершения"));

        CompactInput(_target, "Целевая сумма");
        CompactInput(_minutes, "Длительность в минутах");
        fields.Children.Add(GoalLine(_money, _target, _targetCurrency));

        _conditionRow.Children.Add(SecondaryText("Когда завершать"));
        _condition.Margin = new Thickness(0, 3, 0, 6);
        _condition.Height = _condition.MinHeight = 31;
        AutomationProperties.SetName(_condition, "Когда завершать голосование");
        _conditionRow.Children.Add(_condition);
        fields.Children.Add(_conditionRow);
        fields.Children.Add(GoalLine(_time, _minutes, SecondaryText("мин")));

        fields.Children.Add(SecondaryText("Валюта"));
        _currency.Margin = new Thickness(0, 3, 0, 0);
        _currency.Height = _currency.MinHeight = 31;
        _currency.ItemsSource = new[] { "RUB", "USD", "EUR" }.Concat(_runtime.Settings.ObservedCurrencies).Distinct()
            .Select(code => new { Code = code, Label = code switch { "RUB" => "Рубли (RUB)", "USD" => "Доллары (USD)", "EUR" => "Евро (EUR)", _ => code } }).ToList();
        AutomationProperties.SetName(_currency, "Валюта голосования");
        fields.Children.Add(_currency);

        var divider = new Border { Height = 1, Margin = new Thickness(0, 12, 0, 9) };
        divider.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
        fields.Children.Add(divider);
        fields.Children.Add(SectionHeading("Источники голосов"));
        _donationVotes.MinHeight = _youTubeVotes.MinHeight = 26;
        _donationVotes.ToolTip = "Голоса из описаний донатов. Обычные эффекты донатов работают отдельно.";
        fields.Children.Add(_donationVotes);
        fields.Children.Add(_youTubeVotes);

        var weightLabel = SecondaryText("Вес голоса чата");
        weightLabel.Margin = new Thickness(0, 6, 0, 3);
        fields.Children.Add(weightLabel);
        CompactInput(_youTubeWeight, "Вес голоса чата");
        var weight = new StackPanel { Orientation = Orientation.Horizontal };
        weight.Children.Add(_youTubeWeight);
        weight.Children.Add(_youTubeCurrency);
        fields.Children.Add(weight);
        _youTubeChange.Content = "Разрешить менять голос";
        _youTubeChange.Margin = new Thickness(0, 4, 0, 0);
        _youTubeChange.ToolTip = "Переносит уже учтённый голос на другую команду, не добавляя вес.";
        fields.Children.Add(_youTubeChange);
        return fields;
    }

    private void BuildPlayer()
    {
        _player.Tag = "VotingPlayer";
        _player.Style = (Style)FindResource("StatusCardStyle");
        _player.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        AutomationProperties.SetName(_player, "Сейчас в эфире");

        var layout = new Grid { VerticalAlignment = VerticalAlignment.Center };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.05, GridUnitType.Star) });
        _playerMetricsColumn = new ColumnDefinition();
        layout.ColumnDefinitions.Add(_playerMetricsColumn);
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _player.Child = layout;

        var identity = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
        var icon = Card((UIElement)FindResource("VotingBroadcastIcon"), 0);
        icon.Width = icon.Height = 36;
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(icon, Dock.Left);
        icon.SetResourceReference(Border.BackgroundProperty, "WarningPanelBrush");
        icon.SetResourceReference(Border.BorderBrushProperty, "WarningBorderBrush");
        identity.Children.Add(icon);
        _playerTitle.Style = (Style)FindResource("VotingListButtonStyle");
        _playerTitle.MinHeight = 21;
        _playerTitle.Padding = new Thickness(0);
        _playerTitle.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _playerTitle.Content = _playerName;
        _playerEmpty.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(_playerState);
        labels.Children.Add(_playerTitle);
        labels.Children.Add(_playerEmpty);
        identity.Children.Add(labels);
        layout.Children.Add(identity);

        _progress.Style = (Style)FindResource("VotingTrackStyle");
        _total.FontSize = _remaining.FontSize = 12;
        _amountMetric = PlayerMetric(_total, "M 2,4 A 5,2 0 0 0 12,4 A 5,2 0 0 0 2,4 Z M 2,4 L 2,10 A 5,2 0 0 0 12,10 L 12,4 M 2,7 A 5,2 0 0 0 12,7");
        _timeMetric = PlayerMetric(_remaining, "M 7,1 A 6,6 0 1 1 7,13 A 6,6 0 1 1 7,1 M 7,4 L 7,7 L 9.5,7");
        _timeMetric.Margin = new Thickness(9, 0, 0, 0);
        var metrics = new DockPanel();
        DockPanel.SetDock(_timeMetric, Dock.Right);
        metrics.Children.Add(_timeMetric);
        metrics.Children.Add(_amountMetric);
        var progress = new StackPanel { Margin = new Thickness(15, 0, 15, 0), VerticalAlignment = VerticalAlignment.Center };
        _progress.Margin = new Thickness(0, 4, 0, 0);
        progress.Children.Add(metrics);
        progress.Children.Add(_progress);
        _playerProgress = progress;
        Grid.SetColumn(progress, 1);
        layout.Children.Add(progress);

        _spin.Height = 36;
        _spin.Padding = new Thickness(8, 4, 8, 4);
        ButtonVisual.SetGlyph(_spin, "\uE768");
        _playerToggle.SetResourceReference(ContentControl.ContentTemplateProperty, "VectorSymbolContentTemplate");
        _playerToggle.SetResourceReference(StyleProperty, "AccentOutlineButtonStyle");
        var nextIcon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 1,1 L 10,7 L 1,13 Z M 11,1 H 13 V 13 H 11 Z"),
            Width = 14, Height = 14, Stretch = Stretch.Uniform
        };
        nextIcon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentForegroundBrush");
        _playerNext.Content = nextIcon;
        _playerNext.SetResourceReference(StyleProperty, "AccentOutlineButtonStyle");
        AutomationProperties.SetName(_playerNext, "Следующее голосование");
        ToolTipService.SetShowOnDisabled(_playerNext, true);

        _playerActions.VerticalAlignment = VerticalAlignment.Center;
        foreach (var action in new[] { _spin, _playerToggle, _playerNext })
        {
            action.Margin = new Thickness(6, 0, 0, 0);
            _playerActions.Children.Add(action);
        }
        Grid.SetColumn(_playerActions, 2);
        layout.Children.Add(_playerActions);
        _player.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged) UpdatePlayerLayout(e.NewSize.Width);
        };
    }

    private void UpdatePlayerLayout(double width)
    {
        var narrow = width < 800;
        var iconOnly = width < 550;
        _player.Padding = narrow ? new Thickness(10, 5, 10, 5) : new Thickness(12, 8, 12, 8);
        _playerMetricsColumn.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetRow(_playerProgress, narrow ? 1 : 0);
        Grid.SetColumn(_playerProgress, narrow ? 0 : 1);
        Grid.SetColumnSpan(_playerProgress, narrow ? 3 : 1);
        _playerProgress.Margin = narrow ? new Thickness(0, 4, 0, 0) : new Thickness(15, 0, 15, 0);
        _spin.MinWidth = iconOnly ? 36 : 90;
        ButtonVisual.SetShowLabel(_spin, !iconOnly);
    }

    private static FrameworkElement PlayerMetric(TextBlock text, string iconData)
    {
        var icon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(iconData),
            Width = 13, Height = 13, Stretch = Stretch.Uniform, StrokeThickness = 1.1,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0)
        };
        icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "MutedBrush");
        var metric = new DockPanel();
        metric.Children.Add(icon);
        metric.Children.Add(text);
        return metric;
    }

    private static Grid GoalLine(CheckBox enabled, TextBox field, TextBlock unit)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        enabled.FontSize = 12;
        enabled.VerticalAlignment = VerticalAlignment.Center;
        unit.VerticalAlignment = VerticalAlignment.Center;
        unit.Margin = new Thickness(7, 0, 0, 0);
        unit.FontSize = 11;
        unit.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        row.Children.Add(enabled);
        Grid.SetColumn(field, 1);
        row.Children.Add(field);
        Grid.SetColumn(unit, 2);
        row.Children.Add(unit);
        return row;
    }

    private static void CompactInput(TextBox input, string name)
    {
        input.SetResourceReference(StyleProperty, "VotingInputStyle");
        AutomationProperties.SetName(input, name);
    }

    private void SetDisclosureIcon(Button button, string glyph)
    {
        ButtonVisual.SetGlyph(button, glyph);
        button.ContentTemplate = (DataTemplate)FindResource("VotingDisclosureContentTemplate");
    }

    private static TextBlock SectionHeading(string text)
    {
        var heading = SecondaryText(text);
        heading.FontWeight = FontWeights.SemiBold;
        heading.Margin = new Thickness(0, 0, 0, 5);
        return heading;
    }

    private static TextBlock SecondaryText(string text)
    {
        var label = new TextBlock { Text = text, FontSize = 12 };
        label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        return label;
    }

    private static ScrollViewer VerticalScroll(UIElement content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
}
