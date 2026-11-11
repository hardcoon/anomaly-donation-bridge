using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using Microsoft.Win32;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.DonationAlerts;
using AnomalyDonationBridge.DonateX;

namespace AnomalyDonationBridge.App;

public partial class SettingsWindow : Window
{
    private const string SupportCardNumber = "2200396126139183";
    private readonly GitHubReleaseUpdateChecker _updateChecker = new();
    private readonly SettingsViewModel _viewModel;
    private readonly System.Windows.Threading.DispatcherTimer _youTubeRefresh = new()
    { Interval = TimeSpan.FromMilliseconds(500) };
    public SettingsWindow(SettingsViewModel viewModel, bool openHotkeys = false)
    {
        InitializeComponent();
        DarkSystemFrame.Attach(this);
        _viewModel = viewModel;
        DataContext = viewModel;
        _youTubeRefresh.Tick += (_, _) => { if (YouTubeChatTab.IsSelected) _viewModel.RefreshYouTubeChat(); };
        Closed += (_, _) => { _youTubeRefresh.Stop(); YouTubeKeyBox.Clear(); };
        if (openHotkeys) SettingsTabs.SelectedIndex = 5;
        Loaded += async (_, _) =>
        {
            _youTubeRefresh.Start();
            await RunAsync(_viewModel.RefreshAddonStatusAsync, showDialog: false);
            await RunAsync(_viewModel.RefreshEffectsStatusAsync, showDialog: false);
        };
    }

    private void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Выберите папку игры", Multiselect = false };
        if (Directory.Exists(_viewModel.GamePath)) dialog.InitialDirectory = _viewModel.GamePath;
        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.GamePath = dialog.FolderName;
            _ = RunAsync(_viewModel.RefreshAddonStatusAsync, showDialog: false);
            _ = RunAsync(_viewModel.RefreshEffectsStatusAsync, showDialog: false);
        }
    }

    private void BrowseEditor_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Выберите редактор", Filter = "Программы (*.exe)|*.exe|Все файлы (*.*)|*.*" };
        if (dialog.ShowDialog(this) == true) _viewModel.EditorPath = dialog.FileName;
    }

    private void OpenDataDirectory_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_viewModel.DataDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = _viewModel.DataDirectory,
            UseShellExecute = true
        });
    }

    private void BrowseEffectMod_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Выберите корень мода MO2 для эффектов", Multiselect = false };
        if (Directory.Exists(_viewModel.EffectMo2ModPath)) dialog.InitialDirectory = _viewModel.EffectMo2ModPath;
        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.EffectMo2ModPath = dialog.FolderName;
            _ = RunAsync(_viewModel.RefreshEffectsStatusAsync, showDialog: false);
        }
    }

    private void OpenEffectsDirectory_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_viewModel.EffectsDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = _viewModel.EffectsDirectory,
            UseShellExecute = true
        });
    }

    private async void RefreshEffects_Click(object sender, RoutedEventArgs e)
        => await RunAsync(_viewModel.RefreshEffectsStatusAsync, showDialog: false);

    private void OpenApplications_Click(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo(DonationAlertsEndpoints.Applications) { UseShellExecute = true });

    private void OpenApiDocumentation_Click(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo(DonationAlertsEndpoints.ApiDocumentation) { UseShellExecute = true });

    private void OpenDonateXCabinet_Click(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo(DonateXEndpoints.Cabinet) { UseShellExecute = true });

    private void OpenDonateXApiDocumentation_Click(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo(DonateXEndpoints.ApiDocumentation) { UseShellExecute = true });

    private void RepositoryLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void CopySupportCard_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(SupportCardNumber);
            _viewModel.Status = "Номер карты Т-Банка скопирован.";
        }
        catch
        {
            _viewModel.Status = "Не удалось скопировать номер. Выделите и скопируйте его вручную.";
        }
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckForUpdatesButton.IsEnabled = false;
        CheckForUpdatesButton.Content = "Проверка…";
        try
        {
            var result = await _updateChecker.CheckAsync();
            if (!result.IsUpdateAvailable)
            {
                AppDialogs.Info(this, "Проверка обновлений", $"У вас установлена актуальная версия {result.CurrentVersion}.");
                return;
            }

            if (AppDialogs.Confirm(this, "Доступно обновление",
                $"Доступна новая версия {result.LatestVersion}.\n" +
                $"Установлена версия {result.CurrentVersion}.\n\n" +
                "Открыть страницу релиза в браузере?",
                "Открыть", destructive: false))
            {
                Process.Start(new ProcessStartInfo(result.ReleaseUri.AbsoluteUri) { UseShellExecute = true });
            }
        }
        catch (Exception exception)
        {
            AppDialogs.Info(this, "Проверка обновлений",
                "Не удалось проверить обновления. Проверьте подключение к интернету.\n\n" + exception.Message);
        }
        finally
        {
            CheckForUpdatesButton.Content = "Проверить обновление";
            CheckForUpdatesButton.IsEnabled = true;
        }
    }

    private void Hotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (GlobalHotkeyGesture.IsModifierKey(key)) return;
        var value = GlobalHotkeyGesture.Format(key, Keyboard.Modifiers);
        if (textBox.Name == nameof(ToggleDonationProcessingHotkeyBox))
            _viewModel.ToggleDonationProcessingHotkey = value;
        else if (textBox.Name == nameof(RepeatLastDonationHotkeyBox))
            _viewModel.RepeatLastDonationHotkey = value;
        else if (textBox.DataContext is ActivePresetHotkeyViewModel presetHotkey)
            presetHotkey.Hotkey = value;
    }

    private void AddPresetHotkey_Click(object sender, RoutedEventArgs e) =>
        _viewModel.AddActivePresetHotkey();

    private void ClearPresetHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ActivePresetHotkeyViewModel binding })
            binding.Hotkey = string.Empty;
    }

    private void RemovePresetHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ActivePresetHotkeyViewModel binding })
            _viewModel.RemoveActivePresetHotkey(binding);
    }

    private void ClearHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string target }) return;
        if (target == "toggle") _viewModel.ToggleDonationProcessingHotkey = string.Empty;
        else if (target == "repeat") _viewModel.RepeatLastDonationHotkey = string.Empty;
    }

    private void AddManualExclusion_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ManualCatalogExclusionWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            _viewModel.AddManualCatalogExclusion(
                dialog.SelectedKind, dialog.SectionId, dialog.DisplayName);
        }
        catch (Exception exception)
        {
            _viewModel.Status = exception.Message;
            AppDialogs.Info(this, "Anomaly Donation Bridge", exception.Message);
        }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
        => await RunAsync(() => _viewModel.ConnectAsync(ClientSecretBox.Password, LoopbackOAuthFlow.OpenSystemBrowser));

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
        => await RunAsync(() => _viewModel.DisconnectAsync(false));

    private async void Forget_Click(object sender, RoutedEventArgs e)
    {
        if (AppDialogs.Confirm(this, "Забыть подключение", "Удалить токены, Client ID и сохранённый Client Secret?", "Удалить"))
        {
            await RunAsync(() => _viewModel.DisconnectAsync(true));
            ClientSecretBox.Clear();
        }
    }

    private async void ConnectDonateX_Click(object sender, RoutedEventArgs e)
        => await RunAsync(() => _viewModel.ConnectDonateXAsync(DonateXTokenBox.Password));

    private void OpenYouTubeApi_Click(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo("https://console.cloud.google.com/apis/library/youtube.googleapis.com") { UseShellExecute = true });

    private void OpenYouTubeCredentials_Click(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo("https://console.cloud.google.com/apis/credentials") { UseShellExecute = true });

    private async void SaveYouTubeKey_Click(object sender, RoutedEventArgs e)
    {
        if (await RunAsync(() => _viewModel.SaveYouTubeApiKeyAsync(YouTubeKeyBox.Password))) YouTubeKeyBox.Clear();
    }

    private async void ConnectYouTube_Click(object sender, RoutedEventArgs e)
        => await RunAsync(() => _viewModel.ConnectYouTubeChatAsync());

    private async void DisconnectYouTube_Click(object sender, RoutedEventArgs e)
        => await RunAsync(() => _viewModel.DisconnectYouTubeChatAsync());

    private async void ForgetYouTube_Click(object sender, RoutedEventArgs e)
    {
        if (!AppDialogs.Confirm(this, "Удалить ключ YouTube", "Отключить чат YouTube и удалить сохранённый ключ API?", "Удалить")) return;
        if (await RunAsync(() => _viewModel.DisconnectYouTubeChatAsync(forgetKey: true))) YouTubeKeyBox.Clear();
    }

    private async void DisconnectDonateX_Click(object sender, RoutedEventArgs e)
    {
        if (!AppDialogs.Confirm(this, "Отключить DonateX",
                "Отключить DonateX и удалить сохранённый внешний токен?",
                "Отключить")) return;
        await RunAsync(_viewModel.DisconnectDonateXAsync);
        DonateXTokenBox.Clear();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (await RunAsync(_viewModel.SaveAsync)) DialogResult = true;
    }

    private async void InstallAddon_Click(object sender, RoutedEventArgs e)
        => await RunAsync(_viewModel.InstallBridgeAsync);

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "Экспорт правил", Filter = "JSON (*.json)|*.json", FileName = "AnomalyDonationBridge.rules.json" };
        if (dialog.ShowDialog(this) == true) await RunAsync(() => _viewModel.ExportAsync(dialog.FileName));
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Импорт правил", Filter = "JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) == true) await RunAsync(() => _viewModel.ImportAsync(dialog.FileName));
    }

    private async Task<bool> RunAsync(Func<Task> operation, bool showDialog = true)
    {
        try { await operation(); return true; }
        catch (Exception exception)
        {
            _viewModel.Status = exception.Message;
            if (showDialog)
            {
                AppDialogs.Info(this, "Anomaly Donation Bridge", exception.Message);
            }
            return false;
        }
    }
}
