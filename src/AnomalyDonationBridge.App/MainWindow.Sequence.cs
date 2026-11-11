using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Globalization;
using AnomalyDonationBridge.App.ViewModels;

namespace AnomalyDonationBridge.App;

public partial class MainWindow
{
    private const string SequenceDragFormat = "AnomalyDonationBridge.SequenceStep";
    private sealed record SequenceDrag(string GroupKey, string ActionId);
    private Point _sequenceMouseDown;
    private SequenceDrag? _sequenceDrag;
    private bool _sequenceSaving;
    private SequenceInsertionAdorner? _sequenceMarker;
    private readonly HashSet<TextBox> _savingGroupDelays = [];

    private void SequenceHandle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_sequenceSaving || sender is not FrameworkElement { DataContext: ActionViewModel { IsSequenceStep: true } action }) return;
        _sequenceMouseDown = e.GetPosition(this);
        _sequenceDrag = new(action.SequenceGroupKey, action.Id);
        ((FrameworkElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void SequenceHandle_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _sequenceDrag = null;
        if (sender is FrameworkElement { IsMouseCaptured: true } handle) handle.ReleaseMouseCapture();
    }

    private void SequenceHandle_LostCapture(object sender, MouseEventArgs e) => _sequenceDrag = null;

    private void SequenceHandle_MouseMove(object sender, MouseEventArgs e)
    {
        if (_sequenceDrag is not { } drag || e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement handle) return;
        var offset = e.GetPosition(this) - _sequenceMouseDown;
        if (Math.Abs(offset.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(offset.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _sequenceDrag = null;
        handle.ReleaseMouseCapture();
        try { DragDrop.DoDragDrop(handle, new DataObject(SequenceDragFormat, drag), DragDropEffects.Move); }
        finally { ClearSequenceMarker(); }
        e.Handled = true;
    }

    private bool SequenceTarget(object sender, DragEventArgs e, out FrameworkElement row, out ActionViewModel action, out SequenceDrag drag)
    {
        row = sender as FrameworkElement ?? null!;
        action = row?.DataContext as ActionViewModel ?? null!;
        drag = e.Data.GetDataPresent(SequenceDragFormat) ? e.Data.GetData(SequenceDragFormat) as SequenceDrag ?? null! : null!;
        return !_sequenceSaving && action is { IsSequenceStep: true } && drag is not null &&
            action.SequenceGroupKey == drag.GroupKey && action.Id != drag.ActionId;
    }

    private void SequenceRow_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(SequenceDragFormat)) return;
        e.Handled = true;
        if (!SequenceTarget(sender, e, out var row, out _, out _))
        { e.Effects = DragDropEffects.None; ClearSequenceMarker(); return; }
        e.Effects = DragDropEffects.Move;
        var horizontal = IsSequenceCard(row);
        var after = SequenceDropAfter(row.RenderSize, e.GetPosition(row), horizontal);
        if (_sequenceMarker?.AdornedElement != row) { ClearSequenceMarker();
            if (AdornerLayer.GetAdornerLayer(row) is { } layer)
            { _sequenceMarker = new(row); layer.Add(_sequenceMarker); }
        }
        if (_sequenceMarker is { } marker) { marker.After = after; marker.Horizontal = horizontal; marker.InvalidateVisual(); }
        for (DependencyObject? current = row; current is Visual; current = VisualTreeHelper.GetParent(current))
            if (current is ScrollViewer scroll)
            {
                var y = e.GetPosition(scroll).Y;
                if (y < 28) scroll.ScrollToVerticalOffset(scroll.VerticalOffset - 12);
                else if (y > scroll.ActualHeight - 28) scroll.ScrollToVerticalOffset(scroll.VerticalOffset + 12);
                break;
            }
    }

    private void SequenceRow_DragLeave(object sender, DragEventArgs e) => ClearSequenceMarker();

    private async void SequenceRow_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(SequenceDragFormat)) return;
        e.Handled = true;
        if (!SequenceTarget(sender, e, out var row, out var target, out var drag)) return;
        var after = SequenceDropAfter(row.RenderSize, e.GetPosition(row), IsSequenceCard(row));
        ClearSequenceMarker(); _sequenceSaving = true;
        try { await _viewModel.MoveSequenceStepAsync(drag.GroupKey, drag.ActionId, target.Id, after); }
        finally { _sequenceSaving = false; }
    }

    private async void StrictSequence_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is CheckBox { DataContext: ActivePriceGroupViewModel group } box)
            await _viewModel.SetStrictSequenceAsync(group.Key, box.IsChecked == true);
    }

    private async void GroupDelay_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        await SaveGroupDelayAsync(sender);

    private async void GroupDelay_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SaveGroupDelayAsync(sender);
    }

    private async Task SaveGroupDelayAsync(object sender)
    {
        if (sender is not TextBox { DataContext: ActivePriceGroupViewModel group } box ||
            _savingGroupDelays.Contains(box)) return;
        if (!int.TryParse(box.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            seconds is < 0 or > 86_400)
        {
            box.Text = group.GroupDelaySeconds.ToString(CultureInfo.InvariantCulture);
            _viewModel.ReportGroupDelayInputError();
            return;
        }
        if (seconds == group.GroupDelaySeconds) return;
        _savingGroupDelays.Add(box);
        try { await _viewModel.SetGroupDelayAsync(group.Key, seconds); }
        finally { _savingGroupDelays.Remove(box); }
    }

    private void ClearSequenceMarker()
    {
        if (_sequenceMarker is { } marker) AdornerLayer.GetAdornerLayer(marker.AdornedElement)?.Remove(marker);
        _sequenceMarker = null;
    }

    private static bool IsSequenceCard(FrameworkElement row) => row.Name == "SequenceCardDropTarget";

    internal static bool SequenceDropAfter(Size size, Point pointer, bool horizontal) =>
        horizontal ? pointer.X >= size.Width / 2 : pointer.Y >= size.Height / 2;

    private sealed class SequenceInsertionAdorner : Adorner
    {
        public SequenceInsertionAdorner(UIElement target) : base(target) { IsHitTestVisible = false; }
        public bool After { get; set; }
        public bool Horizontal { get; set; }
        protected override void OnRender(DrawingContext context)
        {
            var brush = (Brush)FindResource("AccentBrush");
            if (Horizontal)
            {
                var x = After ? AdornedElement.RenderSize.Width : 0;
                context.DrawLine(new Pen(brush, 2), new Point(x, 0), new Point(x, AdornedElement.RenderSize.Height));
                context.DrawEllipse(brush, null, new Point(x, 3), 3, 3);
                return;
            }
            var y = After ? AdornedElement.RenderSize.Height : 0;
            context.DrawLine(new Pen(brush, 2), new Point(0, y), new Point(AdornedElement.RenderSize.Width, y));
            context.DrawEllipse(brush, null, new Point(3, y), 3, 3);
        }
    }
}
