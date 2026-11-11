using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AnomalyDonationBridge.App.ViewModels;

namespace AnomalyDonationBridge.App;

public partial class MainWindow
{
    private ContextMenu? _templateContextMenu;

    internal ContextMenu? GetTemplateContextMenu(DependencyObject source, out FrameworkElement? target)
    {
        target = null;
        ActionViewModel? action = null;
        FrameworkElement? card = null;
        ActivePriceGroupViewModel? group = null;
        FrameworkElement? groupTarget = null;
        for (DependencyObject? current = source; current is not null;)
        {
            if (current is TextBoxBase or PasswordBox or ComboBox or ComboBoxItem or MenuBase or MenuItem or Separator) return null;
            if (current is FrameworkElement { DataContext: ActionViewModel candidate } element)
            {
                action ??= candidate;
                if (ReferenceEquals(action, candidate)) { target ??= element; card = element; }
            }
            if (group is null && current is FrameworkElement { DataContext: ActivePriceGroupViewModel candidateGroup } groupElement)
            {
                group = candidateGroup;
                groupTarget = groupElement;
            }
            if (ReferenceEquals(current, this)) break;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : current is FrameworkContentElement content ? content.Parent : LogicalTreeHelper.GetParent(current);
        }
        if (action is null)
        {
            if (!IsCompactLayout || group is null) return null;
            target = groupTarget;
            return CompactShell.BuildGroupMenu(group);
        }
        if (IsCompactLayout) return CompactShell.BuildActionMenu(action);
        target = card is null ? null : FindTemplateMenuButton(card, action);
        return (target as Button)?.ContextMenu;
    }

    private static Button? FindTemplateMenuButton(DependencyObject root, ActionViewModel action)
    {
        if (root is Button { ContextMenu: not null } button && ReferenceEquals(button.DataContext, action)) return button;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindTemplateMenuButton(VisualTreeHelper.GetChild(root, index), action) is { } found) return found;
        return null;
    }

    private void TemplateContextMenu_Opening(object sender, ContextMenuEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || GetTemplateContextMenu(source, out var target) is not { } menu || target is null) return;
        e.Handled = true;
        var atPointer = e.CursorLeft != -1 || e.CursorTop != -1;
        if (IsCompactLayout) CompactShell.OpenTemplateMenu(target, menu, atPointer);
        else if (target is Button button) OpenWideTemplateMenu(button, atPointer);
    }

    private void OpenWideTemplateMenu(Button target, bool atPointer)
    {
        if (target.ContextMenu is not { } menu) return;
        CloseTemplateContextMenu();
        _templateContextMenu = menu;
        menu.Closed += TemplateMenu_Closed;
        menu.PlacementTarget = target;
        menu.Placement = atPointer ? PlacementMode.MousePoint : PlacementMode.Bottom;
        menu.VerticalOffset = atPointer ? 0 : 6;
        menu.IsOpen = true;
    }

    private void TemplateMenu_Closed(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        menu.Closed -= TemplateMenu_Closed;
        if (ReferenceEquals(_templateContextMenu, menu)) _templateContextMenu = null;
    }

    private void CloseTemplateContextMenu()
    {
        var menu = _templateContextMenu;
        _templateContextMenu = null;
        if (menu is null) return;
        menu.Closed -= TemplateMenu_Closed;
        menu.IsOpen = false;
    }
}
