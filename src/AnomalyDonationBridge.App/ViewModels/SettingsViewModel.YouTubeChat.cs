using System.Collections.ObjectModel;
using AnomalyDonationBridge.YouTubeChat;

namespace AnomalyDonationBridge.App.ViewModels;

public sealed record YouTubeChatPreviewRow(string Time, string Author, string Text, string Note);

public sealed partial class SettingsViewModel
{
    private string _youTubeVideoUrl = string.Empty;
    private YouTubeChatSnapshot? _lastYouTubeSnapshot;
    private bool _youTubeBusy;
    private string _youTubeStatus = YouTubeChatSnapshot.Empty.Status;
    private string _youTubeBroadcast = string.Empty;
    private string _youTubeMessageCount = "Сообщений пока нет";
    private YouTubeChatState _youTubeState;

    public string YouTubeVideoUrl { get => _youTubeVideoUrl; set => SetProperty(ref _youTubeVideoUrl, value); }
    public bool YouTubeHasApiKey => _runtime.Secrets.HasYouTubeApiKey;
    public string YouTubeKeySaveLabel => YouTubeHasApiKey ? "Заменить ключ" : "Сохранить ключ";
    public string YouTubeKeyHint => YouTubeHasApiKey
        ? "Ключ сохранён. Для нового эфира менять его не нужно."
        : "Ключ ещё не сохранён. Его можно настроить заранее, без трансляции.";
    public bool YouTubeBusy { get => _youTubeBusy; private set => SetProperty(ref _youTubeBusy, value); }
    public string YouTubeStatus { get => _youTubeStatus; private set => SetProperty(ref _youTubeStatus, value); }
    public string YouTubeBroadcastLabel { get => _youTubeBroadcast; private set => SetProperty(ref _youTubeBroadcast, value); }
    public string YouTubeMessageCount { get => _youTubeMessageCount; private set => SetProperty(ref _youTubeMessageCount, value); }
    public YouTubeChatState YouTubeState { get => _youTubeState; private set => SetProperty(ref _youTubeState, value); }
    public ObservableCollection<YouTubeChatPreviewRow> YouTubeMessages { get; } = [];

    private void InitializeYouTubeChat()
    {
        YouTubeVideoUrl = _runtime.Settings.YouTubeChatVideoUrl;
        RefreshYouTubeChat();
    }

    public void RefreshYouTubeChat()
    {
        var snapshot = _runtime.YouTubeChat;
        if (ReferenceEquals(snapshot, _lastYouTubeSnapshot)) return;
        var previous = _lastYouTubeSnapshot;
        _lastYouTubeSnapshot = snapshot;
        YouTubeStatus = snapshot.Status;
        YouTubeState = snapshot.State;
        YouTubeBroadcastLabel = snapshot.Broadcast is { } stream
            ? $"{stream.ChannelTitle} · {stream.Title}" : string.Empty;
        YouTubeMessageCount = snapshot.ReceivedCount == 0 ? "Сообщений пока нет"
            : $"Получено: {snapshot.ReceivedCount} · показаны последние {snapshot.Messages.Count}";
        if (previous is null || !ReferenceEquals(previous.Messages, snapshot.Messages))
        {
            YouTubeMessages.Clear();
            foreach (var message in snapshot.Messages)
                YouTubeMessages.Add(new(message.PublishedAt?.ToLocalTime().ToString("HH:mm:ss") ?? "—",
                    message.Author, message.Text, message.IsHistorical ? "До подключения" : string.Empty));
        }
    }

    public async Task SaveYouTubeApiKeyAsync(string key)
    {
        if (YouTubeBusy) return;
        YouTubeBusy = true;
        try
        {
            await _runtime.SaveYouTubeApiKeyAsync(key);
            NotifyYouTubeKeyChanged();
            Status = "Ключ API сохранён. Он будет использоваться при подключении к трансляции.";
        }
        finally { YouTubeBusy = false; }
    }

    public async Task ConnectYouTubeChatAsync()
    {
        if (YouTubeBusy) return;
        YouTubeBusy = true;
        try
        {
            await _runtime.ConnectYouTubeChatAsync(YouTubeVideoUrl);
            YouTubeVideoUrl = _runtime.Settings.YouTubeChatVideoUrl;
            RefreshYouTubeChat();
            Status = "Подключение YouTube сохранено.";
        }
        finally { YouTubeBusy = false; }
    }

    public async Task DisconnectYouTubeChatAsync(bool forgetKey = false)
    {
        if (YouTubeBusy) return;
        YouTubeBusy = true;
        try
        {
            await _runtime.DisconnectYouTubeChatAsync(forgetKey);
            NotifyYouTubeKeyChanged();
            RefreshYouTubeChat();
            Status = forgetKey ? "Ключ YouTube удалён." : "Чат YouTube отключён. Ключ и ссылка сохранены.";
        }
        finally { YouTubeBusy = false; }
    }

    private void NotifyYouTubeKeyChanged()
    {
        RaisePropertyChanged(nameof(YouTubeHasApiKey));
        RaisePropertyChanged(nameof(YouTubeKeySaveLabel));
        RaisePropertyChanged(nameof(YouTubeKeyHint));
    }
}
