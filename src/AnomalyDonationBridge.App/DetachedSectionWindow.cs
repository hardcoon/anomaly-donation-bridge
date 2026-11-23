using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;

namespace AnomalyDonationBridge.App;

internal sealed class DetachedSectionWindow : Window
{
    private readonly MainViewModel _model;
    private readonly Grid _surface = new();
    private readonly ContentControl _body = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly ContentControl _player = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(8, 8, 8, 0) };
    private FrameworkElement? _view, _compactView;
    private WindowState _restoreState = WindowState.Normal;
    internal int SectionIndex { get; }
    internal double InterfaceScale => _model.InterfaceScaleFactor;

    internal DetachedSectionWindow(int sectionIndex, string name, MainViewModel model, ResourceDictionary mainResources, ResourceDictionary compactResources)
    {
        SectionIndex = sectionIndex; _model = model; DataContext = model;
        Title = name + " — Anomaly Donation Bridge";
        // No WPF/Win32 owner: minimizing or hiding the main window must not hide this one.
        ShowInTaskbar = true; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var size = sectionIndex switch { MainViewModel.ViewerChatSectionIndex => new Size(760, 680), 3 => new Size(1280, 720), _ => new Size(1120, 800) };
        var area = InterfaceScaling.GetWorkArea(this);
        Width = Math.Min(size.Width * InterfaceScale, area.Width); Height = Math.Min(size.Height * InterfaceScale, area.Height);
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13; UseLayoutRounding = true;
        Resources.MergedDictionaries.Add(mainResources); Resources.MergedDictionaries.Add(compactResources);
        SetResourceReference(BackgroundProperty, "BackgroundBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        DarkSystemFrame.Attach(this);
        _surface.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _surface.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _surface.RowDefinitions.Add(new());
        _surface.SetResourceReference(Panel.BackgroundProperty, "BackgroundBrush");
        _surface.Children.Add(_player);
        var queue = new ContentPresenter { Content = model, ContentTemplate = (DataTemplate)FindResource("QueueStatusTemplate"), Margin = new Thickness(8, 6, 8, 0) };
        queue.SetBinding(VisibilityProperty, new Binding(nameof(MainViewModel.HasQueueStatus)) { Converter = (IValueConverter)FindResource("BoolToVisibility") });
        Grid.SetRow(queue, 1); _surface.Children.Add(queue);
        Grid.SetRow(_body, 2); _surface.Children.Add(_body);
        Content = _surface;
        SizeChanged += (_, _) => UpdateView();
        _body.SizeChanged += (_, _) => UpdateView();
        SourceInitialized += (_, _) => ApplyScale();
        StateChanged += (_, _) => { if (WindowState != WindowState.Minimized) _restoreState = WindowState; };
        _model.PropertyChanged += ModelChanged;
        Closed += (_, _) => { _model.PropertyChanged -= ModelChanged; ReleaseViews(); };
        ApplyScale();
    }

    internal void SetViews(FrameworkElement view, FrameworkElement? compactView = null, FrameworkElement? player = null)
    {
        if (!ReferenceEquals(_view, view) || !ReferenceEquals(_compactView, compactView))
        { _body.Content = null; _view = view; _compactView = compactView; }
        if (!ReferenceEquals(_player.Content, player)) _player.Content = player;
        _player.Visibility = player is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateView();
    }

    internal void ReleaseViews()
    {
        _body.Content = null; _player.Content = null; _view = _compactView = null;
    }

    internal void ShowSection()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = _restoreState;
        Activate();
    }

    private void UpdateView()
    {
        var width = _body.ActualWidth > 0 ? _body.ActualWidth : Width / InterfaceScale;
        var current = _compactView is not null && width < 1080 ? _compactView : _view;
        if (!ReferenceEquals(_body.Content, current)) _body.Content = current;
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(MainViewModel.InterfaceScaleFactor)) ApplyScale(); }

    private void ApplyScale()
    {
        var scale = InterfaceScale;
        InterfaceScaling.ConfigureRendering(_surface, scale);
        _surface.LayoutTransform = scale == 1 ? Transform.Identity : new ScaleTransform(scale, scale);
        var area = InterfaceScaling.GetWorkArea(this);
        MinWidth = Math.Min((SectionIndex == MainViewModel.ViewerChatSectionIndex ? 360 : 520) * scale, area.Width);
        MinHeight = Math.Min(360 * scale, area.Height);
        UpdateView();
    }
}
