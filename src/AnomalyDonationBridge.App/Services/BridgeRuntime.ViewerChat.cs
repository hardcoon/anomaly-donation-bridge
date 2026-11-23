using System.Collections.Concurrent;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.ViewerChat;
using AnomalyDonationBridge.YouTubeChat;

namespace AnomalyDonationBridge.App.Services;

public sealed partial class BridgeRuntime
{
    private readonly SemaphoreSlim _viewerGate = new(1, 1);
    private readonly ConcurrentDictionary<ChatPlatform, ViewerChatSession> _viewerSessions = new();
    private readonly ConcurrentDictionary<ChatPlatform, ViewerConnectionSnapshot> _viewerIdle = new();
    public ViewerChatFeed ViewerChat { get; } = new();
    internal Func<ChatPlatform, IViewerChatTransport> ViewerTransportFactory { get; init; } = platform => platform switch
    {
        ChatPlatform.Twitch => new TwitchChatTransport(),
        ChatPlatform.VkVideoLive => new VkVideoLiveChatTransport(),
        _ => throw new ArgumentOutOfRangeException(nameof(platform))
    };

    public ViewerConnectionSnapshot ViewerConnection(ChatPlatform platform)
    {
        if (platform == ChatPlatform.YouTube)
        {
            var youtube = YouTubeChat;
            var state = youtube.State switch
            {
                YouTubeChatState.Connecting => ViewerChatState.Connecting,
                YouTubeChatState.Connected => ViewerChatState.Connected,
                YouTubeChatState.Retrying => ViewerChatState.Retrying,
                YouTubeChatState.Error => ViewerChatState.Error,
                _ => ViewerChatState.Disconnected
            };
            return new(state, youtube.Status + (state is ViewerChatState.Connected or ViewerChatState.Connecting or ViewerChatState.Retrying
                ? Settings.YouTubeChatAnonymous ? " · анонимно" : " · API" : ""), youtube.Broadcast?.VideoId ?? "");
        }
        return _viewerSessions.TryGetValue(platform, out var session) ? session.Snapshot :
            _viewerIdle.GetValueOrDefault(platform) ?? new(ViewerChatState.Disconnected, "Не подключён");
    }

    public async Task ConnectViewerChatAsync(ChatPlatform platform, string value, CancellationToken token = default)
    {
        if (platform == ChatPlatform.YouTube) { await ConnectYouTubeChatAsync(value, anonymous: true, token); return; }
        var channel = ViewerChatLinks.Parse(platform, value);
        await _viewerGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            var previousChannel = platform == ChatPlatform.Twitch ? Settings.TwitchChatChannel : Settings.VkVideoLiveChatChannel;
            var previousEnabled = platform == ChatPlatform.Twitch ? Settings.TwitchChatEnabled : Settings.VkVideoLiveChatEnabled;
            ConfigureViewerChat(platform, channel, true);
            try { await _settingsStore.SaveAsync(Settings, token).ConfigureAwait(false); }
            catch
            {
                ConfigureViewerChat(platform, previousChannel, previousEnabled);
                throw new InvalidOperationException("Не удалось сохранить подключение чата.");
            }
            await StopViewerSessionAsync(platform).ConfigureAwait(false);
            StartViewerSession(platform, channel);
        }
        finally { _viewerGate.Release(); }
    }

    public async Task DisconnectViewerChatAsync(ChatPlatform platform, CancellationToken token = default)
    {
        if (platform == ChatPlatform.YouTube) { await DisconnectYouTubeChatAsync(cancellationToken: token); return; }
        await _viewerGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var channel = platform == ChatPlatform.Twitch ? Settings.TwitchChatChannel : Settings.VkVideoLiveChatChannel;
            ConfigureViewerChat(platform, channel, false);
            if (!CanArmDonationProcessing) Settings.Armed = false;
            // Stop immediately even if persistence is unavailable.
            await StopViewerSessionAsync(platform).ConfigureAwait(false);
            await _settingsStore.SaveAsync(Settings, token).ConfigureAwait(false);
        }
        finally { _viewerGate.Release(); }
    }

    private void ConfigureViewerChat(ChatPlatform platform, string channel, bool enabled)
    {
        if (platform == ChatPlatform.Twitch) { Settings.TwitchChatChannel = channel; Settings.TwitchChatEnabled = enabled; }
        else if (platform == ChatPlatform.VkVideoLive) { Settings.VkVideoLiveChatChannel = channel; Settings.VkVideoLiveChatEnabled = enabled; }
        else throw new ArgumentOutOfRangeException(nameof(platform));
    }

    private void StartViewerSession(ChatPlatform platform, string channel)
    {
        var session = new ViewerChatSession(ViewerTransportFactory(platform), channel,
            async (change, token) =>
            {
                if (change.Message is { } message && (message.Platform != platform || message.ChannelId != channel)) return;
                // Rendering never waits for a voting write. This source's reader
                // then awaits the shared vote consumer, with cancellation and no
                // fire-and-forget callbacks or unbounded pending-message list.
                ViewerChat.Apply(platform, channel, change);
                if (change is { Kind: ChatEventKind.Message, Message: { } vote })
                    await AcceptChatVotesAsync([new(platform == ChatPlatform.Twitch ? VotingChatPlatform.Twitch : VotingChatPlatform.VkVideoLive,
                        channel, vote.Id, vote.UserId, vote.Text, vote.PublishedAt, vote.IsHistorical, vote.IsMirrored)], token).ConfigureAwait(false);
            }, _lifetime.Token);
        _viewerSessions[platform] = session;
        _viewerIdle.TryRemove(platform, out _);
        session.Start();
    }
    private async Task StopViewerSessionAsync(ChatPlatform platform)
    {
        if (_viewerSessions.TryRemove(platform, out var old)) await old.DisposeAsync().ConfigureAwait(false);
        _viewerIdle[platform] = new(ViewerChatState.Disconnected, "Отключён");
    }
    private void ResumeViewerChat()
    {
        foreach (var platform in new[] { ChatPlatform.Twitch, ChatPlatform.VkVideoLive })
        {
            var enabled = platform == ChatPlatform.Twitch ? Settings.TwitchChatEnabled : Settings.VkVideoLiveChatEnabled;
            if (!enabled) continue;
            try
            {
                var value = platform == ChatPlatform.Twitch ? Settings.TwitchChatChannel : Settings.VkVideoLiveChatChannel;
                StartViewerSession(platform, ViewerChatLinks.Parse(platform, value));
            }
            catch (ViewerChatException e) { _viewerIdle[platform] = new(ViewerChatState.Error, e.Message); }
        }
    }
    private async Task DisposeViewerChatAsync()
    {
        await _viewerGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var platform in _viewerSessions.Keys) await StopViewerSessionAsync(platform).ConfigureAwait(false);
        }
        finally { _viewerGate.Release(); }
    }
}
