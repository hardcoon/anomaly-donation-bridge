using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.Persistence;
using AnomalyDonationBridge.YouTubeChat;

namespace AnomalyDonationBridge.AppTests;

internal static class YouTubeVotingRuntimeTests
{
    internal static async Task RunAsync()
    {
        using var directory = new Program.TemporaryDirectory();
        var paths = new AppPaths(Path.Combine(directory.Path, "voting-chat"));
        var transport = new Feed();
        // No full runtime initialization: never start game IPC or donation providers.
        var runtime = new BridgeRuntime(paths) { YouTubeTransportFactory = () => transport };
        await runtime.InitializeVotingModuleAsync();
        var draft = runtime.Voting.Snapshot;
        draft.AcceptYouTubeVotes = true; draft.AllowYouTubeVoteChange = true; draft.Target = 1000;
        draft.Options = [new VotingOption { Command = "!кров", Preset = new ActionDefinition { DisplayName = "A", HandlerId = "add_radiation" } },
            new VotingOption { Command = "!псина", Preset = new ActionDefinition { DisplayName = "B", HandlerId = "add_radiation" } }];
        await runtime.Voting.SaveSettingsAsync(draft);
        await runtime.Voting.SetEnabledAsync(true, runtime.Settings.ActiveProfileId);
        var history = 0; runtime.HistoryAdded += _ => history++;
        await runtime.SaveYouTubeApiKeyAsync("IsolatedFakeYouTubeVotingKey_12345");
        await runtime.ConnectYouTubeChatAsync("abc123_-XYZ");
        await Until(() => transport.Ready);
        await Send([Message("disarmed", "c0")], 1);
        Check(runtime.Voting.Snapshot.Total == 1 && !runtime.Settings.Armed, "Active chat vote depends on ordinary donation processing");
        Check(runtime.CanArmDonationProcessing && !runtime.DonationAccountConfigured, "YouTube-only voting cannot enable processing");
        var many = Enumerable.Range(0, 130).Select(i => Message("m" + i, "c" + i)).ToArray();
        await Send(many, 131);
        Check(runtime.YouTubeChat.Messages.Count == 50 && runtime.Voting.Snapshot.Total == 130 &&
              runtime.Voting.Snapshot.YouTubeParticipants.Count == 130, "Voting used the preview instead of the full stream");
        var repeat = Message("repeat", "c1") with { Author = "Changed nickname" };
        await Send([repeat, Message("ambiguous", "new", "!кров !псина"), Message("history", "old") with { PublishedAt = DateTimeOffset.UtcNow.AddHours(-1) }], 134);
        Check(runtime.Voting.Snapshot.Total == 130, "Repetition, nickname change, ambiguous or historical message added weight");
        var before = runtime.Voting.Snapshot;
        await Send([Message("change", "c1", "!ПСИНА"), Message("same-name", "new")], 136);
        Check(runtime.Voting.Snapshot.Total == 131 && runtime.Voting.Snapshot.Options[1].Amount == 1,
            "Different channels sharing a nickname or vote transfer failed");
        // A stale editor changes settings without erasing the messages just received.
        before.YouTubeVoteWeight = 3;
        await runtime.Voting.SaveSettingsAsync(before);
        Check(runtime.Voting.Snapshot.Total == 131 && runtime.Voting.Snapshot.YouTubeParticipants.Count == 131,
            "Stale settings save erased votes");
        await runtime.Voting.ResetAmountsAsync();
        await Send([Message("new-round", "c1")], 137);
        Check(runtime.Voting.Snapshot.Total == 3, "New round did not unlock voting or inherit edited weight");
        Check(history == 0 && !File.Exists(paths.PendingQueueFile), "Chat created an ordinary effect or donation history entry");
        await runtime.DisposeAsync();

        var restoredFeed = new Feed();
        await using var restored = new BridgeRuntime(paths) { YouTubeTransportFactory = () => restoredFeed };
        var persistedSettings = await new JsonSettingsStore(paths).LoadAsync();
        var persistedSecrets = await new DpapiSecretStore(paths).LoadAsync();
        typeof(BridgeRuntime).GetProperty(nameof(BridgeRuntime.Settings))!.SetValue(restored, persistedSettings);
        typeof(BridgeRuntime).GetProperty(nameof(BridgeRuntime.Secrets))!.SetValue(restored, persistedSecrets);
        Check(!restored.Settings.Armed && restored.CanArmDonationProcessing && restored.Settings.YouTubeChatEnabled,
            "Restart changed ordinary processing or lost YouTube configuration");
        await restored.InitializeVotingModuleAsync();
        await (Task)typeof(BridgeRuntime).GetMethod("ResumeYouTubeChatAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(restored, null)!;
        await Until(() => restoredFeed.Ready);
        restoredFeed.Push([Message("after-restart", "c1")]);
        await Until(() => restored.YouTubeChat.ReceivedCount == 1);
        Check(restored.Voting.Snapshot.Total == 3 && restored.Voting.Snapshot.YouTubeParticipants.Count == 1,
            "Reconnect/restart unlocked the same participant");
        Check(restored.VotingError.Length == 0, "Vote storage unexpectedly failed");
        await restored.Voting.SetEnabledAsync(false, restored.Settings.ActiveProfileId);
        restoredFeed.Push([Message("disabled-vote", "different")]);
        await Until(() => restored.YouTubeChat.ReceivedCount == 2);
        Check(restored.Voting.Snapshot.Total == 3, "Connected chat enabled a disabled vote");
        await restored.Voting.SetEnabledAsync(true, restored.Settings.ActiveProfileId);
        restoredFeed.Push([Message("enabled-vote", "different")]);
        await Until(() => restored.YouTubeChat.ReceivedCount == 3);
        Check(restored.Voting.Snapshot.Total == 6 && !restored.Settings.Armed, "Manual vote activation needs ordinary processing");

        async Task Send(IReadOnlyList<YouTubeChatMessage> messages, long expected)
        { transport.Push(messages); await Until(() => runtime.YouTubeChat.ReceivedCount == expected); }
    }

    private static YouTubeChatMessage Message(string id, string channel, string text = "!кров") =>
        new(id, channel, "Same nickname", text, DateTimeOffset.UtcNow, 1);
    private static async Task Until(Func<bool> ready)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); while (!ready()) await Task.Delay(10, timeout.Token); }
    private static void Check(bool ok, string reason) { if (!ok) throw new InvalidOperationException(reason); }
    private sealed class Feed : IYouTubeChatTransport
    {
        private readonly Channel<YouTubeChatBatch> _batches = Channel.CreateUnbounded<YouTubeChatBatch>();
        public volatile bool Ready;
        public void Push(IReadOnlyList<YouTubeChatMessage> messages) => _batches.Writer.TryWrite(new(messages, Guid.NewGuid().ToString("N")));
        public Task<YouTubeBroadcast> ResolveAsync(string key, string id, CancellationToken token) =>
            Task.FromResult(new YouTubeBroadcast(id, "chat", "Isolated stream", "Channel"));
        public async IAsyncEnumerable<YouTubeChatBatch> ReadAsync(string key, string id, string? cursor, [EnumeratorCancellation] CancellationToken token)
        {
            Ready = true;
            await foreach (var batch in _batches.Reader.ReadAllAsync(token)) yield return batch;
        }
    }
}
