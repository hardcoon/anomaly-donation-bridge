using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.App;

public partial class ProfilesWindow : Window
{
    private readonly BridgeRuntime _runtime;
    private readonly ObservableCollection<ProfileRow> _profiles = [];
    private bool _busy;

    public ProfilesWindow(BridgeRuntime runtime)
    {
        InitializeComponent();
        DarkSystemFrame.Attach(this);
        _runtime = runtime;
        ProfilesList.ItemsSource = _profiles;
        Reload(runtime.Settings.ActiveProfileId);
    }

    private ProfileRow? Selected => ProfilesList.SelectedItem as ProfileRow;

    private void Reload(string? selectedId)
    {
        _profiles.Clear();
        foreach (var profile in _runtime.Profiles)
        {
            _profiles.Add(new ProfileRow(
                profile.Id,
                profile.Name,
                profile.UserPresets.Count,
                profile.Actions.Count,
                string.Equals(profile.Id, _runtime.Settings.ActiveProfileId, StringComparison.Ordinal)));
        }
        ProfilesList.SelectedItem = _profiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, selectedId, StringComparison.Ordinal)) ?? _profiles.FirstOrDefault();
        UpdateButtons();
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new PresetNameWindow(
            "Название нового профиля",
            "Новый профиль",
            showCopyUserPresets: true, showCopyVotings: true) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await RunAsync(async () =>
        {
            var profile = await _runtime.CreateProfileAsync(dialog.PresetName, dialog.CopyUserPresets, copyVotings: dialog.CopyVotings);
            Reload(profile.Id);
        });
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } selected) return;
        var dialog = new PresetNameWindow("Новое название профиля", selected.Name) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await RunAsync(async () =>
        {
            await _runtime.RenameProfileAsync(selected.Id, dialog.PresetName);
            Reload(selected.Id);
        });
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } selected) return;
        if (!AppDialogs.Confirm(this, "Удаление профиля",
                $"Удалить профиль «{selected.Name}» вместе с его шаблонами и голосованиями?\n\nОжидающие результаты его голосований будут отменены. История и очередь донатов останутся без изменений.",
                "Удалить")) return;
        await RunAsync(async () =>
        {
            await _runtime.DeleteProfileAsync(selected.Id);
            Reload(_runtime.Settings.ActiveProfileId);
        });
    }

    private async void Use_Click(object sender, RoutedEventArgs e) => await UseSelectedAsync();

    private async void ProfilesList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => await UseSelectedAsync();

    private async Task UseSelectedAsync()
    {
        if (Selected is not { } selected || selected.IsActive) return;
        await RunAsync(async () =>
        {
            await _runtime.SwitchProfileAsync(selected.Id);
            Reload(selected.Id);
        });
    }

    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        var selected = Selected;
        RenameButton.IsEnabled = !_busy && selected is not null;
        DeleteButton.IsEnabled = !_busy && selected is not null && _profiles.Count > 1;
        UseButton.IsEnabled = !_busy && selected is { IsActive: false };
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        UpdateButtons();
        try { await action(); }
        catch (Exception exception)
        {
            AppDialogs.Info(this, "Профили", exception.Message);
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    private sealed record ProfileRow(
        string Id,
        string Name,
        int UserPresetCount,
        int ActiveActionCount,
        bool IsActive)
    {
        public Visibility ActiveVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;
    }
}
