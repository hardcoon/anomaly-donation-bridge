using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AnomalyDonationBridge.App.ViewModels;

namespace AnomalyDonationBridge.App;

public partial class MainWindow
{
    private static readonly DependencyPropertyKey IsCompactLayoutPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsCompactLayout), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
    public static readonly DependencyProperty IsCompactLayoutProperty = IsCompactLayoutPropertyKey.DependencyProperty;
    public bool IsCompactLayout => (bool)GetValue(IsCompactLayoutProperty);
    private bool _responsiveLayoutReady;

    private void InitializeResponsiveLayout()
    {
        CompactShell.OwnerWindow = this;
        CompactShell.SetApplicationLogo(ApplicationLogo.Source);
        _responsiveLayoutReady = true;
        SizeChanged += (_, args) => UpdateResponsiveLayout(args.NewSize);
        UpdateResponsiveLayout(new Size(Width, Height));
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
        var compact = size.Width < 1080 || size.Height < 680;
        if (IsCompactLayout == compact) return;
        ThemePopup.IsOpen = false;
        CompactShell.CloseTransientViews();
        CloseTemplateContextMenu();
        SetValue(IsCompactLayoutPropertyKey, compact);
    }

    internal void ConfigureCompactAction(object sender, RoutedEventArgs e) => ConfigureAction_Click(sender, e);
    internal void OpenCompactTheme(object sender, RoutedEventArgs e) => ThemePicker_Click(sender, e);
    internal void OpenCompactProfiles(object sender, RoutedEventArgs e) => Profiles_Click(sender, e);
    internal void OpenCompactSettings(object sender, RoutedEventArgs e) => Settings_Click(sender, e);
    internal void OpenCompactVoting(object sender, RoutedEventArgs e) => Voting_Click(sender, e);
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
