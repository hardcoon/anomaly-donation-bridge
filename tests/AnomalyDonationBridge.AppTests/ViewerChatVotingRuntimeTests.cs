using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.Persistence;
using AnomalyDonationBridge.ViewerChat;
using AnomalyDonationBridge.YouTubeChat;

namespace AnomalyDonationBridge.AppTests;

internal static class ViewerChatVotingRuntimeTests
{
    internal static async Task RunAsync()
    {
        using var temporary = new Program.TemporaryDirectory();
        var paths = new AppPaths(Path.Combine(temporary.Path, "shared-votes"));
        var twitch = new Source(ChatPlatform.Twitch);
        var vk = new Source(ChatPlatform.VkVideoLive);
        var youtube = new YouTubeSource();
        // Initialize voting alone. Never start game IPC or real providers.
        await using var runtime = new BridgeRuntime(paths)
        {
            ViewerTransportFactory = platform => platform == ChatPlatform.Twitch ? twitch : vk,
            AnonymousYouTubeTransportFactory = () => youtube,
            YouTubeTransportFactory = () => throw new InvalidOperationException("Viewer chat must use anonymous YouTube")
        };
        await runtime.InitializeVotingModuleAsync();
        var draft = runtime.Voting.Snapshot;
        draft.AcceptYouTubeVotes = true; draft.AllowYouTubeVoteChange = true; draft.Target = 1000;
        draft.Options = [new VotingOption { Command = "!да", Preset = new ActionDefinition { DisplayName = "A", HandlerId = "add_radiation" } },
            new VotingOption { Command = "!нет", Preset = new ActionDefinition { DisplayName = "B", HandlerId = "add_radiation" } }];
        await runtime.Voting.SaveSettingsAsync(draft);
        await runtime.Voting.SetEnabledAsync(true, runtime.Settings.ActiveProfileId);
        var history = 0; runtime.HistoryAdded += _ => history++;
        await runtime.ConnectViewerChatAsync(ChatPlatform.Twitch, "sample");
        Check(runtime.CanArmDonationProcessing && !runtime.DonationAccountConfigured, "Twitch-only chat cannot configure processing");
        await runtime.ConnectViewerChatAsync(ChatPlatform.VkVideoLive, "sample");
        await runtime.ConnectViewerChatAsync(ChatPlatform.YouTube, "abc123_-XYZ");
        await Until(() => twitch.Ready && vk.Ready && youtube.Ready);
        Check(runtime.Settings.YouTubeChatAnonymous && !runtime.Secrets.HasYouTubeApiKey, "Viewer YouTube required a mode selector or API key");
        await Task.WhenAll(twitch.Send("native-message"), vk.Send("native-message"), youtube.Send("native-message"));
        Check(runtime.Voting.Snapshot.Total == 3 && runtime.Voting.Snapshot.YouTubeParticipants.Count == 3,
            "Three connected sources did not vote in the same round or their native account IDs collided");
        await Task.WhenAll(twitch.Send("native-message"), vk.Send("native-message"), youtube.Send("native-message"));
        Check(runtime.Voting.Snapshot.Total == 3, "Redelivery from the shared chat counted again");
        await Task.WhenAll(twitch.Send("repeat"), vk.Send("repeat"), youtube.Send("repeat"));
        Check(runtime.Voting.Snapshot.Total == 3, "Repeating a command from the same platform account added weight");
        await twitch.Send("transfer", "!нет");
        Check(runtime.Voting.Snapshot.Total == 3 && runtime.Voting.Snapshot.Options[1].Amount == 1,
            "Twitch vote transfer changed another source or minted weight");
        await vk.Send("mirrored", mirrored: true, user: "different-user");
        await vk.Send("history", historical: true, user: "history-user");
        Check(runtime.Voting.Snapshot.Total == 3 && runtime.ViewerChat.Snapshot.Messages.Any(entry => entry.Message.Id == "history"),
            "Displayed history or cross-platform mirror reached voting");
        runtime.ViewerChat.Clear();
        await twitch.Send("transfer", "!нет");
        Check(runtime.Voting.Snapshot.Total == 3, "Clearing the display reset vote deduplication");
        await runtime.Voting.SetEnabledAsync(false, runtime.Settings.ActiveProfileId);
        await Task.WhenAll(twitch.Send("paused", user: "pause-user"), vk.Send("paused", user: "pause-user"), youtube.Send("paused", "pause-user"));
        Check(runtime.Voting.Snapshot.Total == 3 && !runtime.Settings.Armed && history == 0 && !File.Exists(paths.PendingQueueFile),
            "A paused chat vote created a vote, donation history or a game effect");
        Check(runtime.VotingError.Length == 0, "Shared voting reported a storage failure");
    }

    private static async Task Until(Func<bool> ready)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); while (!ready()) await Task.Delay(10, timeout.Token); }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private sealed record Delivery(ViewerChatEvent Event, TaskCompletionSource Done);
    private sealed class Source(ChatPlatform platform) : IViewerChatTransport
    {
        private readonly Channel<Delivery> _messages = Channel.CreateUnbounded<Delivery>();
        public volatile bool Ready;
        public Task Send(string id, string text = "!да", bool historical = false, bool mirrored = false, string user = "native-user")
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _messages.Writer.TryWrite(new(new(ChatEventKind.Message, new(platform, "sample", id, user,
                "Same nickname", text, DateTimeOffset.UtcNow, IsHistorical: historical, IsMirrored: mirrored)), done));
            return done.Task.WaitAsync(TimeSpan.FromSeconds(8));
        }
        public async Task RunAsync(string channel, Func<ViewerChatEvent, CancellationToken, Task> publish, Action connected, CancellationToken token)
        {
            connected(); Ready = true;
            await foreach (var message in _messages.Reader.ReadAllAsync(token))
            {
                await publish(message.Event, token);
                message.Done.TrySetResult();
            }
        }
        public void Dispose() { }
    }
    private sealed class YouTubeSource : IYouTubeChatTransport
    {
        private readonly Channel<(YouTubeChatBatch Batch, TaskCompletionSource Done)> _messages = Channel.CreateUnbounded<(YouTubeChatBatch, TaskCompletionSource)>();
        public volatile bool Ready;
        public bool RequiresApiKey => false;
        public Task Send(string id, string user = "native-user")
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _messages.Writer.TryWrite((new([new(id, user, "Same nickname", "!да", DateTimeOffset.UtcNow, 1)], id), done));
            return done.Task.WaitAsync(TimeSpan.FromSeconds(8));
        }
        public Task<YouTubeBroadcast> ResolveAsync(string key, string id, CancellationToken token) =>
            Task.FromResult(new YouTubeBroadcast(id, "chat", "Test stream", "Test channel"));
        public async IAsyncEnumerable<YouTubeChatBatch> ReadAsync(string key, string chat, string? cursor,
            [EnumeratorCancellation] CancellationToken token)
        {
            Ready = true;
            await foreach (var delivery in _messages.Reader.ReadAllAsync(token))
            {
                yield return delivery.Batch;
                delivery.Done.TrySetResult();
            }
        }
    }
}
