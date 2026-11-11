using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.App;

// The table and compact list share selection, menu commands and enablement.
public static class HistoryListBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(HistoryListBehavior), new PropertyMetadata(false, EnabledChanged));
    public static bool GetIsEnabled(DependencyObject target) => (bool)target.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject target, bool value) => target.SetValue(IsEnabledProperty, value);
    private static readonly DependencyProperty ControllerProperty = DependencyProperty.RegisterAttached(
        "Controller", typeof(Controller), typeof(HistoryListBehavior));

    private static void EnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not Selector selector) return;
        if ((bool)args.NewValue) selector.SetValue(ControllerProperty, new Controller(selector));
        else if (selector.GetValue(ControllerProperty) is Controller controller)
        { controller.Detach(); selector.ClearValue(ControllerProperty); }
    }

    internal static ContextMenu BuildMenu(MainViewModel model)
    {
        var menu = new ContextMenu();
        void Add(string title, ICommand command, string? style = null, string? hint = null)
        {
            var item = new MenuItem { Header = title, Command = command, ToolTip = hint };
            if (style is not null) item.SetResourceReference(FrameworkElement.StyleProperty, style);
            menu.Items.Add(item);
        }
        Add("Повторить", model.RetryCommand);
        Add("Применить мгновенно", model.ApplyQueuedImmediatelyCommand, "SuccessMenuItemStyle",
            "Только выделенные события в очереди. Пропускает порядок и паузы; проверки готовности игры сохраняются.");
        menu.Items.Add(new Separator());
        Add("В исключения", model.AddHistoryExclusionCommand);
        Add("Обновить", model.RefreshEventsCommand);
        menu.Items.Add(new Separator());
        Add("Удалить выбранное из очереди", model.RemoveSelectedQueuedCommand, "DangerMenuItemStyle");
        Add("Очистить очередь", model.ClearQueueCommand, "DangerMenuItemStyle");
        return menu;
    }

    private sealed class Controller
    {
        private readonly Selector _selector;
        private MainViewModel? _model;
        private bool _synchronizing;
        private IList SelectedItems => _selector switch
        { DataGrid grid => grid.SelectedItems, ListBox list => list.SelectedItems, _ => throw new NotSupportedException() };

        internal Controller(Selector selector)
        {
            _selector = selector;
            selector.ContextMenu = new ContextMenu();
            selector.DataContextChanged += ContextChanged;
            selector.SelectionChanged += SelectionChanged;
            selector.PreviewMouseRightButtonDown += RightButtonDown;
            selector.ContextMenuOpening += OpenMenu;
            selector.IsVisibleChanged += VisibilityChanged;
            SetModel(selector.DataContext as MainViewModel);
        }

        internal void Detach()
        {
            SetModel(null);
            _selector.DataContextChanged -= ContextChanged;
            _selector.SelectionChanged -= SelectionChanged;
            _selector.PreviewMouseRightButtonDown -= RightButtonDown;
            _selector.ContextMenuOpening -= OpenMenu;
            _selector.IsVisibleChanged -= VisibilityChanged;
            if (_selector.ContextMenu is { } menu) menu.IsOpen = false;
            _selector.ContextMenu = null;
        }

        private void ContextChanged(object sender, DependencyPropertyChangedEventArgs e) => SetModel(e.NewValue as MainViewModel);
        private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_selector.ContextMenu is { } menu) menu.IsOpen = false;
            Synchronize();
        }
        private void SetModel(MainViewModel? model)
        {
            if (_model is not null) PropertyChangedEventManager.RemoveHandler(_model, ModelChanged, nameof(MainViewModel.SelectedHistoryEntries));
            _model = model;
            if (model is not null) PropertyChangedEventManager.AddHandler(model, ModelChanged, nameof(MainViewModel.SelectedHistoryEntries));
            Synchronize();
        }
        private void ModelChanged(object? sender, PropertyChangedEventArgs e) => Synchronize();
        private void Synchronize()
        {
            if (_model is null || _synchronizing) return;
            _synchronizing = true;
            try
            {
                var wanted = _model.SelectedHistoryEntries.Where(_selector.Items.Contains).ToArray();
                foreach (var old in SelectedItems.Cast<EventHistoryEntry>().Except(wanted).ToArray()) SelectedItems.Remove(old);
                foreach (var entry in wanted.Where(entry => !SelectedItems.Contains(entry))) SelectedItems.Add(entry);
            }
            finally { _synchronizing = false; }
        }
        private void SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_synchronizing || _model is null || _model.IsUpdatingHistory) return;
            _model.SetHistorySelection(SelectedItems.Cast<EventHistoryEntry>());
        }
        private void RightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject source ||
                ItemsControl.ContainerFromElement(_selector, source) is not FrameworkElement { DataContext: EventHistoryEntry entry }) return;
            _selector.Focus();
            if (!SelectedItems.Contains(entry))
            {
                SelectedItems.Clear();
                SelectedItems.Add(entry);
            }
            // Native right-click selection otherwise collapses an existing range.
            e.Handled = true;
        }
        private void OpenMenu(object sender, ContextMenuEventArgs e)
        {
            if (_model is null) return;
            e.Handled = true;
            if (_selector.ContextMenu is { } previous) previous.IsOpen = false;
            var menu = BuildMenu(_model);
            _selector.ContextMenu = menu;
            menu.PlacementTarget = _selector;
            menu.Placement = e.CursorLeft < 0 && e.CursorTop < 0 ? PlacementMode.Center : PlacementMode.MousePoint;
            menu.IsOpen = true;
        }
    }
}
