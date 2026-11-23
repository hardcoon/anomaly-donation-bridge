using AnomalyDonationBridge.YouTubeChat;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.App.Services;

public sealed partial class BridgeRuntime
{
    private readonly SemaphoreSlim _youTubeGate = new(1, 1);
    private IYouTubeChatTransport? _youTubeTransport;
    internal Func<IYouTubeChatTransport> YouTubeTransportFactory { get; init; } = static () => new YouTubeChatTransport();
    internal Func<IYouTubeChatTransport> AnonymousYouTubeTransportFactory { get; init; } = static () => new AnonymousYouTubeChatTransport();
    private bool _youTubeTransportAnonymous;
    private YouTubeChatSession? _youTubeSession;
    private YouTubeChatSnapshot _youTubeIdle = YouTubeChatSnapshot.Empty;
    private Task? _youTubeStartup;
    private DateTimeOffset _youTubeConnectAfter;
    private DateTimeOffset _youTubeAnonymousConnectAfter;

    public YouTubeChatSnapshot YouTubeChat => _youTubeSession?.Snapshot ?? _youTubeIdle;

    public async Task SaveYouTubeApiKeyAsync(string newKey, CancellationToken cancellationToken = default)
    {
        var key = YouTubeVideoLink.NormalizeKey(newKey);
        await _youTubeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            var previousKey = Secrets.YouTubeApiKey;
            Secrets.YouTubeApiKey = key;
            try
            {
                // Credential setup does not require a broadcast or start a connection.
                // An existing session keeps its key until the next explicit connection.
                await _secretStore.SaveAsync(Secrets, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Secrets.YouTubeApiKey = previousKey;
                throw;
            }
            catch
            {
                Secrets.YouTubeApiKey = previousKey;
                throw new InvalidOperationException("Не удалось сохранить ключ YouTube. Проверьте доступ к папке настроек.");
            }
        }
        finally { _youTubeGate.Release(); }
    }

    public Task ConnectYouTubeChatAsync(string videoUrl, CancellationToken cancellationToken = default) =>
        ConnectYouTubeChatAsync(videoUrl, Settings.YouTubeChatAnonymous, cancellationToken);

    public async Task ConnectYouTubeChatAsync(string videoUrl, bool anonymous, CancellationToken cancellationToken = default)
    {
        var videoId = YouTubeVideoLink.Parse(videoUrl);
        await _youTubeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            if (!anonymous && !Secrets.HasYouTubeApiKey)
                throw new YouTubeChatException("key_missing", "Сначала сохраните ключ API в блоке «Ключ API YouTube».");
            var key = anonymous ? string.Empty : YouTubeVideoLink.NormalizeKey(Secrets.YouTubeApiKey);
            var connectAfter = anonymous ? _youTubeAnonymousConnectAfter : _youTubeConnectAfter;
            if (_youTubeTransportAnonymous == anonymous && _youTubeSession?.RetryNotBefore is { } retry && retry > connectAfter)
                connectAfter = retry;
            if (DateTimeOffset.UtcNow < connectAfter)
                throw new YouTubeChatException("reconnect_wait",
                    $"Повторное подключение будет доступно через {Math.Ceiling((connectAfter - DateTimeOffset.UtcNow).TotalSeconds):0} с.");
            if (anonymous) _youTubeAnonymousConnectAfter = DateTimeOffset.UtcNow.AddSeconds(5);
            else _youTubeConnectAfter = DateTimeOffset.UtcNow.AddSeconds(5);
            await StopYouTubeSessionAsync().ConfigureAwait(false);
            Settings.YouTubeChatVideoUrl = YouTubeVideoLink.Canonical(videoId);
            Settings.YouTubeChatEnabled = true;
            Settings.YouTubeChatAnonymous = anonymous;
            try
            {
                await _settingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                Settings.YouTubeChatEnabled = false;
                throw new InvalidOperationException("Не удалось сохранить подключение YouTube. Проверьте доступ к папке настроек.");
            }
            StartYouTubeSession(key, videoId);
        }
        finally { _youTubeGate.Release(); }
    }

    public async Task DisconnectYouTubeChatAsync(bool forgetKey = false, CancellationToken cancellationToken = default)
    {
        await _youTubeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_youTubeSession?.RetryNotBefore is { } retry)
            {
                if (_youTubeTransportAnonymous && retry > _youTubeAnonymousConnectAfter) _youTubeAnonymousConnectAfter = retry;
                else if (!_youTubeTransportAnonymous && retry > _youTubeConnectAfter) _youTubeConnectAfter = retry;
            }
            await StopYouTubeSessionAsync().ConfigureAwait(false);
            Settings.YouTubeChatEnabled = false;
            if (!CanArmDonationProcessing) Settings.Armed = false;
            if (forgetKey)
            {
                Secrets.YouTubeApiKey = string.Empty;
                await _secretStore.SaveAsync(Secrets, cancellationToken).ConfigureAwait(false);
            }
            await _settingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
        }
        finally { _youTubeGate.Release(); }
    }

    private async Task ResumeYouTubeChatAsync()
    {
        await _youTubeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_lifetime.IsCancellationRequested || !Settings.YouTubeChatEnabled) return;
            var videoId = YouTubeVideoLink.Parse(Settings.YouTubeChatVideoUrl);
            StartYouTubeSession(Settings.YouTubeChatAnonymous ? string.Empty : YouTubeVideoLink.NormalizeKey(Secrets.YouTubeApiKey), videoId);
        }
        catch (YouTubeChatException error)
        { _youTubeIdle = YouTubeChatSnapshot.Empty with { State = YouTubeChatState.Error, Status = error.Message }; }
        finally { _youTubeGate.Release(); }
    }

    private void StartYouTubeSession(string key, string videoId)
    {
        if (_youTubeTransport is null || _youTubeTransportAnonymous != Settings.YouTubeChatAnonymous)
        {
            (_youTubeTransport as IDisposable)?.Dispose();
            _youTubeTransportAnonymous = Settings.YouTubeChatAnonymous;
            _youTubeTransport = _youTubeTransportAnonymous ? AnonymousYouTubeTransportFactory() : YouTubeTransportFactory();
        }
        var session = new YouTubeChatSession(_youTubeTransport, key, videoId, _lifetime.Token,
            async (messages, token) =>
            {
                // Display is independent of a voting write retry. The existing
                // consumer still owns acknowledgement/cursor advancement.
                foreach (var message in messages)
                    ViewerChat.Publish(new(AnomalyDonationBridge.ViewerChat.ChatPlatform.YouTube,
                        videoId, message.Id, message.AuthorChannelId, message.Author, message.Text,
                        message.PublishedAt, message.IsHistorical, message.IsOwner, message.IsModerator));
                await AcceptChatVotesAsync(messages.Select(message => new ChatVotingMessage(VotingChatPlatform.YouTube,
                    videoId, message.Id, message.AuthorChannelId, message.Text, message.PublishedAt,
                    message.IsHistorical)).ToArray(), token).ConfigureAwait(false);
            });
        _youTubeSession = session;
        session.Start();
    }

    private async Task StopYouTubeSessionAsync()
    {
        var session = _youTubeSession;
        _youTubeSession = null;
        _youTubeIdle = YouTubeChatSnapshot.Empty;
        if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
    }

    private async Task DisposeYouTubeChatAsync()
    {
        if (_youTubeStartup is not null) await _youTubeStartup.ConfigureAwait(false);
        await _youTubeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopYouTubeSessionAsync().ConfigureAwait(false);
            (_youTubeTransport as IDisposable)?.Dispose();
        }
        finally { _youTubeGate.Release(); }
    }
}
