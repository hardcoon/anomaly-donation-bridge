using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.App;

public partial class MainWindow
{
    private VotingView? _votingView;
    private SettingsView? _settingsView;
    private ViewerChatView? _viewerChatView;
    private int _previousSection, _sectionBeforeSettings;

    private void InitializeSections()
    {
        InitializeDetachedSections();
        _viewModel.PropertyChanged += SectionChanged;
        Closed += (_, _) =>
        {
            _viewModel.PropertyChanged -= SectionChanged;
            CloseDetachedSections();
            _votingView?.Dispose();
            _viewerChatView?.Dispose();
            _votingObs.Dispose();
            DiscardSettingsView();
        };
        UpdateEmbeddedSection();
    }

    private void SectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedSectionIndex)) UpdateEmbeddedSection();
    }

    private void UpdateEmbeddedSection()
    {
        var section = _viewModel.SelectedSectionIndex;
        if (section == MainViewModel.SettingsSectionIndex)
        {
            if (_previousSection is >= 0 and not MainViewModel.SettingsSectionIndex) _sectionBeforeSettings = _previousSection;
            if (_settingsView is null)
            {
                _settingsView = new SettingsView(new SettingsViewModel(_runtime));
                _settingsView.CloseRequested += (_, _) =>
                {
                    DiscardSettingsView();
                    _viewModel.SelectedSectionIndex = _sectionBeforeSettings;
                };
            }
        }
        else if (section == MainViewModel.VotingSectionIndex)
            _votingView ??= new VotingView(_runtime, _votingObs, hostPlayerSeparately: true);
        else if (section == MainViewModel.ViewerChatSectionIndex)
            _viewerChatView ??= new ViewerChatView(_runtime);
        _previousSection = section;
        PlaceEmbeddedViews();
    }

    private void PlaceEmbeddedViews()
    {
        PlaceView(WideSettingsHost, CompactShell.SettingsHost, _settingsView);
        PlaceSectionView(WideViewerChatHost, CompactShell.ViewerChatHost, _viewerChatView, MainViewModel.ViewerChatSectionIndex);
        PlaceHistorySection();
        if (_votingView is null)
        {
            PlaceView(WideVotingHost, CompactShell.VotingHost, null);
            return;
        }

        MainSections.ApplyTemplate();
        var playerHost = (ContentControl)MainSections.Template.FindName("WideVotingPlayerHost", MainSections);
        if (_detachedSections.ContainsKey(MainViewModel.VotingSectionIndex))
        {
            playerHost.Content = null; CompactShell.VotingPlayerHost.Content = null;
            PlaceSectionView(WideVotingHost, CompactShell.VotingHost, _votingView, MainViewModel.VotingSectionIndex, _votingView.Player);
        }
        else
        {
            PlaceView(WideVotingHost, CompactShell.VotingHost, _votingView);
            PlaceView(playerHost, CompactShell.VotingPlayerHost, _votingView.Player);
        }
    }

    private void PlaceView(ContentControl wide, ContentControl compact, FrameworkElement? view)
    {
        var current = IsCompactLayout ? compact : wide;
        var other = IsCompactLayout ? wide : compact;
        if (other.Content is not null) other.Content = null;
        if (!ReferenceEquals(current.Content, view)) current.Content = view;
    }

    private void DiscardSettingsView()
    {
        _settingsView?.Dispose();
        _settingsView = null;
        PlaceView(WideSettingsHost, CompactShell.SettingsHost, null);
    }

    private Task ShowSettingsAsync()
    {
        _viewModel.SelectedSectionIndex = MainViewModel.SettingsSectionIndex;
        return Task.CompletedTask;
    }

    private async Task ShowPresetHotkeySettingsAsync(int presetNumber)
    {
        await ShowSettingsAsync();
        _settingsView?.EditPresetHotkey(presetNumber);
    }

    private async Task ShowVotingAsync(ActionDefinition action, string collectionId)
    {
        _viewModel.SelectedSectionIndex = MainViewModel.VotingSectionIndex;
        if (_detachedSections.TryGetValue(MainViewModel.VotingSectionIndex, out var window)) window.ShowSection();
        if (_votingView is not null) await _votingView.AddTemplateAsync(action, collectionId);
    }

    internal async Task FlushEditorWritesAsync()
    {
        if (_settingsView is not null) await _settingsView.PendingApply;
        if (_votingView is not null) await _votingView.FlushPendingChangesAsync();
    }
}
