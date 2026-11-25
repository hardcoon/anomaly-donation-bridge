using System.Diagnostics;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.Tests;

internal static class ChatVotingFloodTests
{
    internal static async Task RunAsync()
    {
        var store = new CountingStore();
        var now = DateTimeOffset.Parse("2026-11-25T10:00:00Z");
        var engine = await Create(store, () => now);
        for (var i = 0; i < 100; i++)
        {
            var old = store.Data.Current.Clone(); old.Id = Guid.NewGuid().ToString("N");
            old.IsActive = false; old.Phase = VotingPhase.Paused; old.Notice = new string('x', 10000);
            store.Data.PreviousRounds.Add(old);
        }
        engine = new VotingCoordinator(store, () => now); await engine.InitializeAsync();
        var roundId = engine.Snapshot.Id;
        var changes = 0; engine.Changed += () => changes++;
        var fullWrites = store.FullWrites;
        var timer = Stopwatch.StartNew();
        for (var page = 0; page < 800; page++)
        {
            var batch = Enumerable.Range(page * 250, 250).Select(i => Message("plain-" + i, "user-" + i,
                i % 2 == 0 ? "Привет, как дела?" : "!да !нет", now)).ToArray();
            Check(await engine.RecordChatAsync(roundId, "profile", batch) == 0, "Plain or ambiguous chat counted");
        }
        timer.Stop();
        Check(store.FullWrites == fullWrites && store.ChatWrites == 0 && changes == 0 &&
            engine.Snapshot.SeenYouTubeMessageIds.Count == 0 && engine.Snapshot.YouTubeParticipants.Count == 0,
            "Plain chat copied/saved voting state or retained unbounded IDs");
        Console.WriteLine($"  200000 ordinary/ambiguous messages: {timer.ElapsedMilliseconds} ms, 0 saves, 0 retained IDs.");

        var votes = Enumerable.Range(0, 5000).Select(i => Message("vote-" + i, "user-" + i, "!да", now)).ToArray();
        Check(await engine.RecordChatAsync(roundId, "profile", votes) == votes.Length, "Batch lost real votes");
        var round = engine.Snapshot;
        Check(round.Total == 5000 && round.YouTubeParticipants.Count == 5000 &&
            round.SeenYouTubeMessageIds.Count == VotingChatReplay.Capacity, "Replay cache is not bounded independently of the voter ledger");
        Check(round.SeenYouTubeMessageIds.Contains("twitch:4:room:vote-4999") && !round.SeenYouTubeMessageIds.Contains("twitch:4:room:vote-0"),
            "Replay cache does not keep recent IDs");
        var chatWrites = store.ChatWrites; var changeCount = changes;
        now = now.AddSeconds(1);
        var repeat = votes.Select((vote, i) => vote with { Id = "repeat-" + i, Text = "!нет", PublishedAt = now }).ToArray();
        Check(await engine.RecordChatAsync(roundId, "profile", repeat) == 0 && store.ChatWrites == chatWrites && changes == changeCount,
            "Repeated voters without change permission still write data");
        Check(await engine.RecordChatAsync(roundId, "profile", votes) == 0, "Evicting replay IDs minted extra votes");

        var settings = engine.Snapshot; settings.AllowYouTubeVoteChange = true; await engine.SaveSettingsAsync(settings);
        now = now.AddSeconds(2);
        await engine.RecordChatAsync(roundId, "profile", [Message("latest", "user-0", "!да", now)]);
        Check(changes == changeCount + 1, "Same-choice watermark unnecessarily refreshes the UI"); // settings event only
        var restored = new VotingCoordinator(store, () => now); await restored.InitializeAsync();
        Check(await restored.RecordChatAsync(roundId, "profile", [Message("delayed", "user-0", "!нет", now.AddSeconds(-1))]) == 0,
            "Restart lost the same-choice watermark");
        now = now.AddSeconds(1);
        Check(await restored.RecordChatAsync(roundId, "profile", [Message("newer", "user-0", "!нет", now)]) == 1 && restored.Snapshot.Total == 5000,
            "Changing a vote minted or lost weight");

        var ignoredAt = now;
        await restored.RecordChatAsync(roundId, "profile", [Message("ignored", "new-user", "привет", ignoredAt)]);
        now = now.AddSeconds(1);
        settings = restored.Snapshot; settings.Options[0].Command = "привет"; await restored.SaveSettingsAsync(settings);
        Check(await restored.RecordChatAsync(roundId, "profile", [Message("ignored", "new-user", "привет", ignoredAt)]) == 0,
            "Changing commands reinterpreted previously ignored chat as new votes");
        Check(await restored.RecordChatAsync(roundId, "profile", [Message("fresh", "new-user", "привет", now.AddMilliseconds(1))]) == 1,
            "New command does not accept fresh messages");

        var before = restored.Snapshot;
        store.FailNext = true;
        try { await restored.RecordChatAsync(roundId, "profile", [Message("retry", "retry-user", "привет", now.AddMilliseconds(2))]); throw new InvalidOperationException("Write should fail"); }
        catch (IOException) { }
        Check(restored.Snapshot.Total == before.Total && restored.Snapshot.ChatRevision == before.ChatRevision,
            "Failed journal write published live accounting");
        Check(await restored.RecordChatAsync(roundId, "profile", [Message("retry", "retry-user", "привет", now.AddMilliseconds(2))]) == 1,
            "Failed journal write consumed a vote");

        store.Data.SchemaVersion = 7;
        store.Data.Current.SeenYouTubeMessageIds.UnionWith(Enumerable.Range(0, 50000).Select(i => "legacy-plain-" + i));
        var archived = store.Data.Current.Clone(); archived.Id = Guid.NewGuid().ToString("N");
        store.Data.PreviousRounds.Clear(); store.Data.PreviousRounds.Add(archived);
        store.Data.SeenDonationIds.Add("actual-donation");
        var total = store.Data.Current.Total; var participants = store.Data.Current.YouTubeParticipants.Count;
        var migrated = new VotingCoordinator(store, () => now.AddSeconds(10)); await migrated.InitializeAsync();
        Check(store.Data.SchemaVersion == 8 && store.Data.Current.SeenYouTubeMessageIds.Count <= VotingChatReplay.Capacity &&
            store.Data.PreviousRounds.Single().SeenYouTubeMessageIds.Count == 0 &&
            store.Data.PreviousRounds.Single().YouTubeParticipants.Count == participants &&
            migrated.Snapshot.Total == total && migrated.Snapshot.YouTubeParticipants.Count == participants &&
            store.Data.SeenDonationIds.Contains("actual-donation"), "Migration lost accounting or retained archived replay noise");
    }

    internal static async Task<VotingCoordinator> Create(IVotingStore store, Func<DateTimeOffset> clock)
    {
        var engine = new VotingCoordinator(store, clock, _ => 0); await engine.InitializeAsync();
        var draft = engine.Snapshot; draft.Options = VotingTests.Draft(VotingCondition.Any).Options;
        draft.Options[0].Command = "!да"; draft.Options[1].Command = "!нет";
        draft.AcceptYouTubeVotes = true; draft.MoneyGoal = false; draft.TimeGoal = true; draft.DurationSeconds = 86400;
        await engine.SaveSettingsAsync(draft); await engine.SetEnabledAsync(true, "profile");
        return engine;
    }
    internal static ChatVotingMessage Message(string id, string user, string text, DateTimeOffset at) =>
        new(VotingChatPlatform.Twitch, "room", id, user, text, at);
    internal static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class CountingStore : IChatVotingStore
    {
        public VotingDocument Data = new();
        public int FullWrites, ChatWrites; public bool FailNext;
        public Task<VotingDocument> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Data.Clone());
        public Task SaveAsync(VotingDocument document, CancellationToken cancellationToken = default)
        { FullWrites++; Data = document.Clone(); return Task.CompletedTask; }
        public Task SaveChatAsync(VotingDocument document, VotingChatCommit commit, CancellationToken token = default)
        {
            if (FailNext) { FailNext = false; throw new IOException("Simulated disk failure"); }
            commit.Apply(Data); ChatWrites++; return Task.CompletedTask;
        }
    }
}
