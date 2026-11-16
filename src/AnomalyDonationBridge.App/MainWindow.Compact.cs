using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;

namespace AnomalyDonationBridge.App;

public partial class MainWindow
{
    private static readonly DependencyPropertyKey IsCompactLayoutPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsCompactLayout), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
    public static readonly DependencyProperty IsCompactLayoutProperty = IsCompactLayoutPropertyKey.DependencyProperty;
    public bool IsCompactLayout => (bool)GetValue(IsCompactLayoutProperty);
    private static readonly DependencyPropertyKey IsSidebarExpandedPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsSidebarExpanded), typeof(bool), typeof(MainWindow), new PropertyMetadata(true));
    public static readonly DependencyProperty IsSidebarExpandedProperty = IsSidebarExpandedPropertyKey.DependencyProperty;
    public bool IsSidebarExpanded => (bool)GetValue(IsSidebarExpandedProperty);

    private bool _responsiveLayoutReady;
    private bool? _sidebarExpandedOverride;
    private Size _minimumLayoutSize;
    internal double InterfaceScale => _viewModel.InterfaceScaleFactor;

    private void InitializeResponsiveLayout()
    {
        CompactShell.OwnerWindow = this;
        CompactShell.SetApplicationLogo(ApplicationLogo.Source);
        _minimumLayoutSize = new Size(MinWidth, MinHeight);
        Width = Math.Min(Width * InterfaceScale, InterfaceScaling.GetWorkArea(this).Width);
        _responsiveLayoutReady = true;
        SizeChanged += (_, args) => UpdateResponsiveLayout(args.NewSize);
        SourceInitialized += (_, _) => ApplyInterfaceScale();
        _viewModel.PropertyChanged += InterfaceScaleChanged;
        Closed += (_, _) => _viewModel.PropertyChanged -= InterfaceScaleChanged;
        ApplyInterfaceScale();
    }

    private void InterfaceScaleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.InterfaceScaleFactor)) ApplyInterfaceScale();
    }

    private void ApplyInterfaceScale()
    {
        InterfaceScaling.ConfigureRendering(this, InterfaceScale);
        InterfaceScaling.ConfigureRendering(ThemePopupSurface, InterfaceScale);
        var workArea = InterfaceScaling.GetWorkArea(this);
        MinWidth = Math.Min(_minimumLayoutSize.Width * InterfaceScale, workArea.Width);
        MinHeight = Math.Min(_minimumLayoutSize.Height * InterfaceScale, workArea.Height);
        UpdateResponsiveLayout(new Size(Math.Max(MinWidth, IsLoaded ? ActualWidth : Width),
            Math.Max(MinHeight, IsLoaded ? ActualHeight : Height)));
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_responsiveLayoutReady && !IsLoaded && (e.Property == WidthProperty || e.Property == HeightProperty))
            UpdateResponsiveLayout(new Size(Width, Height));
    }

    private void UpdateResponsiveLayout(Size size)
    {
        if (!_responsiveLayoutReady || !double.IsFinite(size.Width) || !double.IsFinite(size.Height)) return;
        var layoutWidth = size.Width / InterfaceScale;
        var layoutHeight = size.Height / InterfaceScale;
        var expanded = _sidebarExpandedOverride ?? layoutWidth >= 760;
        if (IsSidebarExpanded != expanded) SetValue(IsSidebarExpandedPropertyKey, expanded);

        var compact = layoutWidth < 1080 || layoutHeight < 680;
        if (IsCompactLayout == compact) return;
        CompactShell.CloseTransientViews();
        CloseTemplateContextMenu();
        SetValue(IsCompactLayoutPropertyKey, compact);
        if (ThemePopup.IsOpen) ThemePopup.PlacementTarget = compact ? CompactShell.ThemeTrigger : ThemeButton;
        PlaceEmbeddedViews();
    }

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e) => ToggleSidebar();

    internal void ToggleSidebar()
    {
        _sidebarExpandedOverride = !IsSidebarExpanded;
        SetValue(IsSidebarExpandedPropertyKey, _sidebarExpandedOverride.Value);
    }

    internal void ConfigureCompactAction(object sender, RoutedEventArgs e) => ConfigureAction_Click(sender, e);
    internal void OpenCompactTheme(object sender, RoutedEventArgs e) => ThemePicker_Click(sender, e);
    internal void OpenCompactProfiles(object sender, RoutedEventArgs e) => Profiles_Click(sender, e);
    internal void PrepareCompactVotingTargets(object sender, RoutedEventArgs e) => VotingTargets_SubmenuOpened(sender, e);
    internal void SetCompactStrictSequence(object sender, RoutedEventArgs e) => StrictSequence_Click(sender, e);
    internal void SaveCompactGroupDelay(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) => GroupDelay_LostKeyboardFocus(sender, e);
    internal void HandleCompactGroupDelayKey(object sender, System.Windows.Input.KeyEventArgs e) => GroupDelay_KeyDown(sender, e);

    internal void AttachCompactSequenceHandlers(Border row, FrameworkElement handle)
    {
        handle.PreviewMouseLeftButtonDown += SequenceHandle_MouseDown;
        handle.PreviewMouseMove += SequenceHandle_MouseMove;
        handle.PreviewMouseLeftButtonUp += SequenceHandle_MouseUp;
        handle.LostMouseCapture += SequenceHandle_LostCapture;
        row.DragOver += SequenceRow_DragOver;
        row.Drop += SequenceRow_Drop;
        row.DragLeave += SequenceRow_DragLeave;
    }
}
