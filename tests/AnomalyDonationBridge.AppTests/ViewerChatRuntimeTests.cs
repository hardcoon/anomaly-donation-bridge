using System.Reflection;
using System.IO;
using System.Runtime.CompilerServices;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.Persistence;
using AnomalyDonationBridge.ViewerChat;
using AnomalyDonationBridge.YouTubeChat;

namespace AnomalyDonationBridge.AppTests;

internal static class ViewerChatRuntimeTests
{
    internal static async Task RunAsync()
    {
        using var temporary = new Program.TemporaryDirectory();
        var paths = new AppPaths(Path.Combine(temporary.Path, "data"));
        var readers = new List<FakeReader>();
        var runtime = new BridgeRuntime(paths) { ViewerTransportFactory = platform =>
        {
            var reader = new FakeReader(platform); readers.Add(reader); return reader;
        }};
        await runtime.ConnectViewerChatAsync(ChatPlatform.Twitch, "https://twitch.tv/sample");
        await runtime.ConnectViewerChatAsync(ChatPlatform.VkVideoLive, "https://vkplay.live/sample");
        await Until(() => runtime.ViewerChat.Snapshot.Messages.Count == 2);
        Check(runtime.ViewerChat.Snapshot.Messages.Select(e => e.Message.Platform).Distinct().Count() == 2, "Sources did not share one feed");
        Check(!runtime.Settings.Armed && !File.Exists(paths.HistoryFile) && !File.Exists(paths.PendingQueueFile),
            "Reading chat created donation/game work");
        var saved = await new JsonSettingsStore(paths).LoadAsync();
        Check(saved.TwitchChatEnabled && saved.VkVideoLiveChatEnabled && saved.TwitchChatChannel == "sample" &&
            saved.VkVideoLiveChatChannel == "sample", "Settings projection dropped chat connection preferences");
        await runtime.DisconnectViewerChatAsync(ChatPlatform.Twitch);
        Check(readers[0].Disposed && !runtime.Settings.TwitchChatEnabled && !readers[1].Disposed, "Disconnect affected another source");
        await runtime.DisposeAsync();
        Check(readers.All(r => r.Cancelled && r.Disposed), "Shutdown leaked a source");
        saved = await new JsonSettingsStore(paths).LoadAsync();
        await using var restored = new BridgeRuntime(paths) { ViewerTransportFactory = p => new FakeReader(p) };
        typeof(BridgeRuntime).GetProperty(nameof(BridgeRuntime.Settings))!.SetValue(restored, saved);
        typeof(BridgeRuntime).GetMethod("ResumeViewerChat", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(restored, null);
        await Until(() => restored.ViewerConnection(ChatPlatform.VkVideoLive).State == ViewerChatState.Connected);
        Check(restored.ViewerConnection(ChatPlatform.Twitch).State == ViewerChatState.Disconnected, "Restart enabled a source that the user disconnected");
        await AnonymousYouTubeAsync(temporary.Path);
    }
    private static async Task AnonymousYouTubeAsync(string temporary)
    {
        var paths = new AppPaths(Path.Combine(temporary, "anonymous-data"));
        var guest = new FakeGuest();
        var runtime = new BridgeRuntime(paths)
        {
            AnonymousYouTubeTransportFactory = () => guest,
            YouTubeTransportFactory = () => throw new InvalidOperationException("Official API selected for guest mode")
        };
        runtime.Secrets.YouTubeApiKey = "SavedOfficialKey_MustRemainUnchanged_12345";
        await runtime.ConnectViewerChatAsync(ChatPlatform.YouTube, "abc123_-XYZ");
        await Until(() => runtime.YouTubeChat.State == YouTubeChatState.Connected);
        Check(guest.Key == "" && runtime.Settings.YouTubeChatAnonymous && runtime.YouTubeChatConfigured &&
            runtime.ViewerChat.Snapshot.Messages.Single().Message.Platform == ChatPlatform.YouTube,
            "Anonymous source did not reach the common feed without a key");
        Check(runtime.Secrets.YouTubeApiKey == "SavedOfficialKey_MustRemainUnchanged_12345" && !File.Exists(paths.PendingQueueFile),
            "Guest mode modified a saved key or sent an effect");
        await runtime.DisposeAsync();
        Check(guest.Cancelled && guest.Disposed, "Guest reader leaked on shutdown");
        var saved = await new JsonSettingsStore(paths).LoadAsync();
        Check(saved.YouTubeChatAnonymous && saved.YouTubeChatEnabled, "Guest mode was not persisted");
        var resumed = new FakeGuest();
        await using var restored = new BridgeRuntime(paths) { AnonymousYouTubeTransportFactory = () => resumed };
        typeof(BridgeRuntime).GetProperty(nameof(BridgeRuntime.Settings))!.SetValue(restored, saved);
        await (Task)typeof(BridgeRuntime).GetMethod("ResumeYouTubeChatAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(restored, null)!;
        await Until(() => restored.YouTubeChat.State == YouTubeChatState.Connected);
        Check(!restored.Secrets.HasYouTubeApiKey && restored.YouTubeChatConfigured, "Restart incorrectly requires a key for anonymous mode");
        var switchGuest = new FakeGuest();
        var switchOfficial = new FakeGuest(requiresApiKey: true);
        await using var switching = new BridgeRuntime(new AppPaths(Path.Combine(temporary, "switch-data")))
        { YouTubeTransportFactory = () => switchOfficial, AnonymousYouTubeTransportFactory = () => switchGuest };
        switching.Secrets.YouTubeApiKey = "SavedOfficialKey_ForModeSwitch_12345";
        await switching.ConnectYouTubeChatAsync("abc123_-XYZ", anonymous: false);
        await Until(() => switching.YouTubeChat.State == YouTubeChatState.Connected);
        await switching.ConnectYouTubeChatAsync("abc123_-XYZ", anonymous: true);
        await Until(() => switching.YouTubeChat.State == YouTubeChatState.Connected);
        Check(switchOfficial.Disposed && switchGuest.Key == "", "Mode switch reused an authenticated transport or its cooldown");
    }
    private static async Task Until(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!condition()) await Task.Delay(10, timeout.Token); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class FakeReader(ChatPlatform platform) : IViewerChatTransport
    {
        public bool Cancelled, Disposed;
        public async Task RunAsync(string channel, Func<ViewerChatEvent, CancellationToken, Task> publish, Action connected, CancellationToken token)
        {
            connected();
            await publish(new(ChatEventKind.Message, new(platform, channel, "native-id", "user-id", "Зритель",
                "!spawn_btr — обычное сообщение", DateTimeOffset.UtcNow)), token);
            try { await Task.Delay(Timeout.Infinite, token); } finally { Cancelled = token.IsCancellationRequested; }
        }
        public void Dispose() => Disposed = true;
    }
    private sealed class FakeGuest(bool requiresApiKey = false) : IYouTubeChatTransport, IDisposable
    {
        public bool RequiresApiKey => requiresApiKey;
        public string? Key;
        public bool Cancelled, Disposed;
        public Task<YouTubeBroadcast> ResolveAsync(string key, string video, CancellationToken token)
        { Key = key; return Task.FromResult(new YouTubeBroadcast(video, "guest-chat", "Guest stream", "Channel")); }
        public async IAsyncEnumerable<YouTubeChatBatch> ReadAsync(string key, string chat, string? cursor, [EnumeratorCancellation] CancellationToken token)
        {
            yield return new([new("guest-message", "native-author", "Viewer", "Guest text", DateTimeOffset.UtcNow, 1)], "cursor");
            try { await Task.Delay(Timeout.Infinite, token); } finally { Cancelled = token.IsCancellationRequested; }
        }
        public void Dispose() => Disposed = true;
    }
}
