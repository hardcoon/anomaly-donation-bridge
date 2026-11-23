using System.Windows;
using System.Windows.Controls;
using AnomalyDonationBridge.App.ViewModels;

namespace AnomalyDonationBridge.App;

public partial class MainWindow
{
    private readonly Dictionary<int, DetachedSectionWindow> _detachedSections = [];
    private readonly Dictionary<(int Section, bool Compact), FrameworkElement> _detachedPlaceholders = [];
    private ContentControl? _wideHistoryHost, _compactHistoryHost;
    private FrameworkElement? _wideHistoryView, _compactHistoryView;
    private bool _closingDetachedSections;
    private Window SectionDialogOwner => _detachedSections.Values.FirstOrDefault(window => window.IsVisible && window.IsActive) ?? (Window)this;

    private void InitializeDetachedSections()
    {
        var historyTab = (TabItem)MainSections.Items[3];
        _wideHistoryView = (FrameworkElement)historyTab.Content;
        historyTab.Content = null;
        _wideHistoryHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, Content = _wideHistoryView };
        historyTab.Content = _wideHistoryHost;
        (_compactHistoryHost, _compactHistoryView) = CompactShell.CreateHistoryHost();
        _wideHistoryView.DataContext = _compactHistoryView.DataContext = _viewModel;
        foreach (var (tab, section) in new (FrameworkElement, int)[]
        {
            (historyTab, 3), (VotingNavigationTab, MainViewModel.VotingSectionIndex),
            (ViewerChatNavigationTab, MainViewModel.ViewerChatSectionIndex),
            (CompactShell.HistoryTab, 3), (CompactShell.VotingNavigationTab, MainViewModel.VotingSectionIndex),
            (CompactShell.ViewerChatNavigationTab, MainViewModel.ViewerChatSectionIndex)
        })
        {
            var menu = new ContextMenu();
            var item = new MenuItem { Header = "Открыть в новом окне" };
            item.Click += (_, e) => { e.Handled = true; OpenDetachedSection(section); };
            menu.Items.Add(item); tab.ContextMenu = menu;
        }
    }

    private static string SectionTitle(int section) => section switch
    { 3 => "История", MainViewModel.VotingSectionIndex => "Голосования", MainViewModel.ViewerChatSectionIndex => "Чат зрителей", _ => throw new ArgumentOutOfRangeException(nameof(section)) };

    internal DetachedSectionWindow GetOrCreateDetachedSection(int section)
    {
        var title = SectionTitle(section);
        if (_detachedSections.TryGetValue(section, out var existing)) return existing;
        if (section == MainViewModel.VotingSectionIndex) _votingView ??= new VotingView(_runtime, _votingObs, hostPlayerSeparately: true);
        if (section == MainViewModel.ViewerChatSectionIndex) _viewerChatView ??= new ViewerChatView(_runtime);
        var window = new DetachedSectionWindow(section, title, _viewModel, Resources, CompactShell.Resources);
        _detachedSections.Add(section, window);
        window.Closed += (_, _) =>
        {
            _detachedSections.Remove(section);
            if (!_closingDetachedSections) PlaceEmbeddedViews();
        };
        PlaceEmbeddedViews();
        return window;
    }

    internal void OpenDetachedSection(int section)
    {
        try { GetOrCreateDetachedSection(section).ShowSection(); }
        catch (Exception error) { AppDialogs.Info(SectionDialogOwner, "Отдельное окно", error.Message); }
    }

    private FrameworkElement DetachedPlaceholder(int section, bool compact)
    {
        if (_detachedPlaceholders.TryGetValue((section, compact), out var cached)) return cached;
        var text = new TextBlock { Text = "Раздел открыт в отдельном окне", FontSize = 16, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var button = new Button { Content = "Перейти к окну", Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        button.Click += (_, _) => OpenDetachedSection(section);
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20), Children = { text, button } };
        _detachedPlaceholders.Add((section, compact), panel);
        return panel;
    }

    private void PlaceSectionView(ContentControl wide, ContentControl compact, FrameworkElement? view, int section, FrameworkElement? player = null)
    {
        if (!_detachedSections.TryGetValue(section, out var window)) { PlaceView(wide, compact, view); return; }
        if (ReferenceEquals(wide.Content, view)) wide.Content = null;
        if (ReferenceEquals(compact.Content, view)) compact.Content = null;
        window.SetViews(view!, player: player);
        var widePlaceholder = DetachedPlaceholder(section, false); var compactPlaceholder = DetachedPlaceholder(section, true);
        if (!ReferenceEquals(wide.Content, widePlaceholder)) wide.Content = widePlaceholder;
        if (!ReferenceEquals(compact.Content, compactPlaceholder)) compact.Content = compactPlaceholder;
    }

    private void PlaceHistorySection()
    {
        if (_wideHistoryHost is null || _compactHistoryHost is null) return;
        if (_detachedSections.TryGetValue(3, out var window))
        {
            if (ReferenceEquals(_wideHistoryHost.Content, _wideHistoryView)) _wideHistoryHost.Content = null;
            if (ReferenceEquals(_compactHistoryHost.Content, _compactHistoryView)) _compactHistoryHost.Content = null;
            window.SetViews(_wideHistoryView!, _compactHistoryView);
            _wideHistoryHost.Content = DetachedPlaceholder(3, false); _compactHistoryHost.Content = DetachedPlaceholder(3, true);
        }
        else
        {
            _wideHistoryHost.Content = _wideHistoryView; _compactHistoryHost.Content = _compactHistoryView;
        }
    }

    private void CloseDetachedSections()
    {
        _closingDetachedSections = true;
        foreach (var window in _detachedSections.Values.ToArray()) window.Close();
        _detachedSections.Clear();
    }
}
