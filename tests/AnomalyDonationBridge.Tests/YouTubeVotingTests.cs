using System.Text.Json;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.GameIpc;

namespace AnomalyDonationBridge.Tests;

internal static class YouTubeVotingTests
{
    public static async Task RunAsync()
    {
        await SourcesAndCommands();
        await ChangesAndCurrencies();
        await Lifecycle();
        await AtomicBatchAndStaleEditor();
        await BoundariesAndMigration();
        DisplayFormats();
    }

    private static async Task SourcesAndCommands()
    {
        var f = await Fixture.Create();
        Check(f.Engine.Snapshot is { AcceptDonationVotes: true, AcceptYouTubeVotes: true, YouTubeVoteWeight: 1, AllowYouTubeVoteChange: false }, "Vote defaults");
        Check(await f.Chat("ambiguous", "c1", "!КРОВ !Псина") == 0, "Two distinct commands must reject the whole message");
        Check(await f.Chat("prefix", "c1", "!Кровь") == 0, "Command boundaries");
        Check(await f.Chat("first", "c1", "вот !КРОВ !кров !кров") == 1 && f.Engine.Snapshot.Total == 1, "Repetition in one message counts once, ignoring case");
        Check(await f.Chat("first", "c1", "!кров") == 0, "Duplicate ID");
        Check(await f.Chat("repeat", "c1", "!кров") == 0, "Same account cannot add weight");
        Check(await f.Chat("change-disabled", "c1", "!псина") == 0, "Change disabled");
        Check(await f.Chat("other-channel", "c2", "!псина") == 1, "Other channel can vote independently");
        Check(await f.Donate("d1", 50, "!кров") == "counted" && await f.Donate("d2", 50, "!кров") == "counted", "Every donation contributes its own amount");
        Check(await f.Donate("d1", 50, "!кров") == "duplicate" && f.Engine.Snapshot.Total == 102, "Donation dedupe unchanged");
        await f.Edit(r => r.AcceptDonationVotes = false);
        await f.Donate("d3", 500, "!кров");
        Check(f.Engine.Snapshot.Total == 102 && await f.Chat("chat-only", "c3", "!псина") == 1, "YT-only source still accepts chat, not donation weight");
        await f.Edit(r => { r.AcceptDonationVotes = true; r.AcceptYouTubeVotes = false; });
        Check(await f.Chat("chat-disabled", "c4", "!кров") == 0, "Donation-only excludes chat");
        Check(await f.Donate("d4", 10, "!кров") == "counted" && f.Engine.Snapshot.Total == 113, "Ordinary donation accounting preserved");
        await Throws(() => f.Edit(r => r.AcceptDonationVotes = false));
        await Throws(() => f.Edit(r => r.YouTubeVoteWeight = 0));
        await Throws(() => f.Edit(r => r.YouTubeVoteWeight = .001m));
    }

    private static async Task ChangesAndCurrencies()
    {
        var f = await Fixture.Create();
        await f.Edit(r => r.AllowYouTubeVoteChange = true);
        await f.Chat("first", "c1", "!кров");
        await f.Donate("money", 100, "!кров");
        await f.Edit(r => { r.YouTubeVoteWeight = 7; r.Currency = "EUR"; });
        await f.Chat("new-currency", "c2", "!псина");
        await f.Chat("transfer", "c1", "!псина");
        var round = f.Engine.Snapshot;
        Check(round.Options[0].AmountsByCurrency["RUB"] == 100 && round.Options[1].AmountsByCurrency["RUB"] == 1 &&
            round.Options[1].Amount == 7 && round.YouTubeParticipants["c1"].Weight == 1, "Transfer must keep original weight/currency and leave donor funds alone");
        var lastAt = round.YouTubeParticipants["c1"].LastMessageAt;
        Check(await f.Chat("out-of-order", "c1", "!кров", lastAt.AddMilliseconds(-10)) == 0, "Old change cannot override newer vote");
        await f.Edit(r => r.Options[1].Command = "!собаки");
        await f.Chat("renamed-option", "c1", "!собаки");
        Check(f.Engine.Snapshot.YouTubeParticipants["c1"].Weight == 1 && f.Engine.Snapshot.Total == 7, "Renaming commands does not mint votes");
        await f.Edit(r => { r.Currency = "RUB"; r.Options[0].Enabled = false; });
        Check(await f.Chat("disabled-option", "c1", "!кров") == 0 && f.Engine.Snapshot.Total == 1, "Disabled option excluded");
        await f.Edit(r => { r.Options[0].Enabled = true; r.Options.RemoveAt(1); r.Options.Add(new VotingOption { Command = "!новая", Preset = VotingTests.Draft(VotingCondition.All).Options[0].Preset }); });
        await f.Chat("removed-option-change", "c1", "!новая");
        Check(f.Engine.Snapshot.Total == 101, "Removing an option then moving a vote preserves original weight without touching donations");
    }

    private static async Task Lifecycle()
    {
        var f = await Fixture.Create();
        await f.Chat("first", "c1", "!кров");
        var id = f.Engine.Snapshot.Id;
        await f.Engine.SetEnabledAsync(false, "profile");
        var duringPause = f.Message("pause", "c2", "!кров");
        Check(await f.Engine.RecordYouTubeAsync(id, "profile", [duringPause]) == 0, "Pause ignores new votes");
        f.Now = f.Now.AddMinutes(1);
        await f.Engine.SetEnabledAsync(true, "profile");
        Check(await f.Engine.RecordYouTubeAsync(id, "profile", [duringPause]) == 0, "Delayed paused messages excluded on resume");
        Check(await f.Chat("re-enabled", "c1", "!кров") == 0 && f.Engine.Snapshot.Id == id, "Activation preserves ledger");
        f.Engine = new VotingCoordinator(f.Store, () => f.Now, _ => 0);
        await f.Engine.InitializeAsync();
        Check(await f.Chat("restart", "c1", "!кров") == 0 && f.Engine.Snapshot.Total == 1, "Restart/reconnect preserves participants");
        await f.Engine.ResetAmountsAsync();
        Check(f.Engine.Snapshot.YouTubeParticipants.Count == 0 && f.Engine.Snapshot.Id != id, "Reset starts a new voter round");
        Check(await f.Engine.RecordYouTubeAsync(id, "profile", [f.Message("late", "c2", "!кров")]) == 0, "Captured old round cannot fill reset round");
        Check(await f.Chat("new-round", "c1", "!псина") == 1, "Same account may vote after reset");
        await f.Edit(r => { r.TimeGoal = false; r.Target = 1; });
        Check(f.Engine.Snapshot.Phase == VotingPhase.Ready, "Money goal still evaluates");
        await f.Engine.ChooseWinnerAsync();
        var old = f.Engine.Snapshot.Id;
        f.Now = f.Now.AddSeconds(8);
        await f.Engine.StartNextCollectionAsync("profile");
        Check(f.Engine.Snapshot.Id != old && f.Engine.Snapshot.YouTubeParticipants.Count == 0, "Automatic same-vote repetition starts fresh round");
        Check(await f.Chat("repeat-round", "c1", "!кров") == 1, "Voting in automatically repeated round");
    }

    private static async Task AtomicBatchAndStaleEditor()
    {
        var f = await Fixture.Create();
        var stale = f.Engine.Snapshot;
        var batch = Enumerable.Range(0, 130).Select(i => f.Message("m" + i, "c" + i, i % 2 == 0 ? "!кров" : "!псина")).ToArray();
        f.Store.FailNext = true;
        await Throws(() => f.Engine.RecordYouTubeAsync(stale.Id, "profile", batch));
        Check(f.Engine.Snapshot.Total == 0 && f.Engine.Snapshot.YouTubeParticipants.Count == 0 && f.Engine.Snapshot.SeenYouTubeMessageIds.Count == 0, "Failed save must not consume any messages or publish totals");
        var writes = f.Store.Writes;
        Check(await f.Engine.RecordYouTubeAsync(stale.Id, "profile", batch) == 130 && f.Store.Writes == writes + 1, "Whole batch, not a 50-message preview, in one atomic write");
        stale.YouTubeVoteWeight = 2;
        await f.Engine.SaveSettingsAsync(stale);
        Check(f.Engine.Snapshot.Total == 130 && f.Engine.Snapshot.YouTubeParticipants.Count == 130, "Stale editor must preserve newly counted votes");
        Check(await f.Engine.RecordYouTubeAsync(stale.Id, "profile", batch) == 0 && f.Engine.Snapshot.Total == 130, "Batch redelivery");
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => f.Engine.RecordYouTubeAsync(stale.Id, "profile", [f.Message("parallel" + i, "same", "!кров")])));
        Check(f.Engine.Snapshot.Total == 132 && f.Engine.Snapshot.YouTubeParticipants.Count == 131, "Concurrent delivery cannot duplicate a participant");
        await f.Edit(r => { r.AcceptDonationVotes = false; r.AllowYouTubeVoteChange = true; });
        var round = f.Engine.Snapshot;
        var inheritedId = await f.Engine.AddCollectionAsync(sourceCollectionId: round.CollectionId);
        var inherited = f.Engine.SnapshotFor(inheritedId);
        Check(inherited.AcceptYouTubeVotes && !inherited.AcceptDonationVotes && inherited.AllowYouTubeVoteChange && inherited.YouTubeVoteWeight == 2 &&
            inherited.YouTubeParticipants.Count == 0 && inherited.Total == 0, "Inherit settings, never participants or money");
    }

    private static async Task BoundariesAndMigration()
    {
        var f = await Fixture.Create();
        await f.Edit(r => { r.TimeGoal = false; r.Target = 2; });
        var roundId = f.Engine.Snapshot.Id;
        var batch = Enumerable.Range(0, 10).Select(i => f.Message("goal" + i, "c" + i, "!кров")).ToArray();
        Check(await f.Engine.RecordYouTubeAsync(roundId, "profile", batch) == 2 && f.Engine.Snapshot.Total == 2 && f.Engine.Snapshot.Phase == VotingPhase.Ready,
            "Money completion stops batch exactly at goal");
        Check(await f.Chat("completed", "late", "!кров") == 0, "Finished rounds reject chat");
        f = await Fixture.Create();
        var historical = f.Message("history", "old", "!кров") with { IsHistorical = true };
        var undated = historical with { Id = "undated", IsHistorical = false, PublishedAt = null };
        var old = historical with { Id = "before-round", IsHistorical = false, PublishedAt = f.Now.AddHours(-1) };
        Check(await f.Engine.RecordYouTubeAsync(f.Engine.Snapshot.Id, "profile", [historical, undated, old]) == 0, "History, unknown time and prior-round time excluded");
        Check(await f.Engine.RecordYouTubeAsync(f.Engine.Snapshot.Id, "other-profile", [f.Message("profile", "c", "!кров")]) == 0, "Profile isolation");
        await f.Edit(r => { r.Target = 2; r.TimeGoal = true; r.DurationSeconds = 60; r.Condition = VotingCondition.All; });
        await f.Chat("and1", "c1", "!кров"); await f.Chat("and2", "c2", "!псина");
        Check(f.Engine.Snapshot.Phase == VotingPhase.Collecting, "ALL waits for timer despite chat goal");
        f.Now = f.Now.AddSeconds(61);
        Check(await f.Chat("expired", "c3", "!кров") == 0 && f.Engine.Snapshot.Phase == VotingPhase.Ready, "Timer completion checked before accepting batch");
        f = await Fixture.Create();
        await f.Edit(r => { r.Currency = "EUR"; r.YouTubeVoteWeight = 1.235m; });
        await f.Chat("eur", "c1", "!кров");
        Check(f.Engine.Snapshot.Total == 1.24m, "Currency minor unit rounding");
        var legacy = JsonSerializer.Deserialize<VotingDocument>("{\"SchemaVersion\":5}")!;
        var store = new MemoryStore { Data = legacy };
        var migrated = new VotingCoordinator(store); await migrated.InitializeAsync();
        Check(migrated.Snapshot is { AcceptDonationVotes: true, AcceptYouTubeVotes: false, YouTubeVoteWeight: 1, AllowYouTubeVoteChange: false } && store.Data.SchemaVersion == 7,
            "Legacy defaults and migration");
    }

    private static void DisplayFormats()
    {
        var now = DateTimeOffset.UtcNow;
        var round = VotingTests.Draft(VotingCondition.All); round.Currency = "EUR"; round.Options[0].Amount = 250;
        round.TimeGoal = true; round.Phase = VotingPhase.Collecting; round.Deadline = now.AddSeconds(90);
        Check(VotingProgressDisplay.Value(round, VotingHudFormat.Percent, now) == "25%" &&
            VotingProgressDisplay.OptionValue(round, round.Options[0], VotingHudFormat.Percent) == "100%", "Percent display has no money");
        Check(VotingProgressDisplay.Value(round, VotingHudFormat.Amount, now).Contains("EUR") &&
            !VotingProgressDisplay.Value(round, VotingHudFormat.Amount, now).Contains("руб"), "Actual voting currency");
        Check(VotingProgressDisplay.Value(round, VotingHudFormat.Timer, now) == "01:30", "Timer formatting");
        round.Phase = VotingPhase.Paused; round.PausedAt = now;
        Check(VotingProgressDisplay.Timer(round, now.AddHours(1)) == "01:30", "Paused countdown frozen");
        var lines = VotingGameHud.ContentLines(round, new VotingAutomation { GameHudFormat = VotingHudFormat.Timer }, now);
        Check(lines.Contains("01:30") && !lines.Any(line => line.Contains("EUR") || line.Contains("руб")), "Game HUD uses the same timer formatter as OBS");
    }

    private static void Check(bool ok, string reason) { if (!ok) throw new InvalidOperationException(reason); }
    private static async Task Throws(Func<Task> operation)
    { try { await operation(); } catch (Exception) { return; } throw new InvalidOperationException("Expected failure"); }
    private sealed class MemoryStore : IVotingStore
    {
        public VotingDocument Data = new(); public bool FailNext; public int Writes;
        public Task<VotingDocument> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(RoundTrip(Data));
        public Task SaveAsync(VotingDocument document, CancellationToken cancellationToken = default)
        { if (FailNext) { FailNext = false; throw new IOException("Test disk failure"); } Writes++; Data = RoundTrip(document); return Task.CompletedTask; }
        private static VotingDocument RoundTrip(VotingDocument data) => JsonSerializer.Deserialize<VotingDocument>(JsonSerializer.Serialize(data))!;
    }
    private sealed class Fixture
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-07T10:00:00Z");
        public MemoryStore Store = new(); public VotingCoordinator Engine = null!;
        public static async Task<Fixture> Create()
        {
            var f = new Fixture(); f.Engine = new VotingCoordinator(f.Store, () => f.Now, _ => 0); await f.Engine.InitializeAsync();
            var draft = f.Engine.Snapshot; draft.Options = VotingTests.Draft(VotingCondition.All).Options;
            draft.Options[0].Command = "!кров"; draft.Options[1].Command = "!псина"; draft.AcceptYouTubeVotes = true;
            await f.Engine.SaveSettingsAsync(draft); await f.Engine.SetEnabledAsync(true, "profile"); return f;
        }
        public async Task Edit(Action<VotingRound> edit) { var draft = Engine.Snapshot; edit(draft); await Engine.SaveSettingsAsync(draft); }
        public YouTubeVotingMessage Message(string id, string channel, string text) => new(id, channel, text, Now = Now.AddMilliseconds(1));
        public Task<int> Chat(string id, string channel, string text, DateTimeOffset? time = null) => Engine.RecordYouTubeAsync(Engine.Snapshot.Id, "profile", [Message(id, channel, text) with { PublishedAt = time ?? Now }]);
        public Task<string> Donate(string id, decimal amount, string text) => Engine.RecordAsync(new DonationEvent { Id = id, Amount = amount, Message = text, Currency = "RUB", CreatedAt = Now }, "profile");
    }
}
