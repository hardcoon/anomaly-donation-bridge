using AnomalyDonationBridge.YouTubeChat;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.App.Services;

public sealed partial class BridgeRuntime
{
    private readonly SemaphoreSlim _youTubeGate = new(1, 1);
    private IYouTubeChatTransport? _youTubeTransport;
    internal Func<IYouTubeChatTransport> YouTubeTransportFactory { get; init; } = static () => new YouTubeChatTransport();
    private YouTubeChatSession? _youTubeSession;
    private YouTubeChatSnapshot _youTubeIdle = YouTubeChatSnapshot.Empty;
    private Task? _youTubeStartup;
    private DateTimeOffset _youTubeConnectAfter;
    private string _youTubeVotingError = string.Empty;

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

    public async Task ConnectYouTubeChatAsync(string videoUrl, CancellationToken cancellationToken = default)
    {
        var videoId = YouTubeVideoLink.Parse(videoUrl);
        await _youTubeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            if (!Secrets.HasYouTubeApiKey)
                throw new YouTubeChatException("key_missing", "Сначала сохраните ключ API в блоке «Ключ API YouTube».");
            var key = YouTubeVideoLink.NormalizeKey(Secrets.YouTubeApiKey);
            if (_youTubeSession?.RetryNotBefore is { } retry && retry > _youTubeConnectAfter)
                _youTubeConnectAfter = retry;
            if (DateTimeOffset.UtcNow < _youTubeConnectAfter)
                throw new YouTubeChatException("reconnect_wait",
                    $"Повторное подключение будет доступно через {Math.Ceiling((_youTubeConnectAfter - DateTimeOffset.UtcNow).TotalSeconds):0} с.");
            _youTubeConnectAfter = DateTimeOffset.UtcNow.AddSeconds(5);
            await StopYouTubeSessionAsync().ConfigureAwait(false);
            Settings.YouTubeChatVideoUrl = YouTubeVideoLink.Canonical(videoId);
            Settings.YouTubeChatEnabled = true;
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
            if (_youTubeSession?.RetryNotBefore is { } retry && retry > _youTubeConnectAfter)
                _youTubeConnectAfter = retry;
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
            StartYouTubeSession(YouTubeVideoLink.NormalizeKey(Secrets.YouTubeApiKey), videoId);
        }
        catch (YouTubeChatException error)
        { _youTubeIdle = YouTubeChatSnapshot.Empty with { State = YouTubeChatState.Error, Status = error.Message }; }
        finally { _youTubeGate.Release(); }
    }

    private void StartYouTubeSession(string key, string videoId)
    {
        _youTubeTransport ??= YouTubeTransportFactory();
        var session = new YouTubeChatSession(_youTubeTransport, key, videoId, _lifetime.Token, AcceptYouTubeVotesAsync);
        _youTubeSession = session;
        session.Start();
    }

    private async Task AcceptYouTubeVotesAsync(IReadOnlyList<YouTubeChatMessage> messages, CancellationToken token)
    {
        if (!_votingLoaded) return;
        var profileId = Settings.ActiveProfileId;
        var roundId = Voting.YouTubeRoundId(profileId);
        if (roundId is null) return;
        var votes = messages.Where(message => !message.IsHistorical)
            .Select(message => new YouTubeVotingMessage(message.Id, message.AuthorChannelId, message.Text, message.PublishedAt)).ToArray();
        if (votes.Length == 0) return;
        while (Settings.ActiveProfileId == profileId)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                await Voting.RecordYouTubeAsync(roundId, profileId, votes, token).ConfigureAwait(false);
                _youTubeVotingError = string.Empty;
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                // Retain this batch and its round ID. Do not advance the chat
                // cursor or turn a disk failure into an authenticated API error.
                _youTubeVotingError = "Не удалось сохранить голоса YouTube. Повтор через 5 с. Проверьте доступ к папке настроек.";
                await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            }
        }
        _youTubeVotingError = string.Empty;
    }

    private async Task StopYouTubeSessionAsync()
    {
        var session = _youTubeSession;
        _youTubeSession = null;
        _youTubeIdle = YouTubeChatSnapshot.Empty;
        if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
        _youTubeVotingError = string.Empty;
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
