using System.IO;
using System.Text.Json;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.AppTests;

internal static class VotingProfileRuntimeTests
{
    internal static async Task RunAsync()
    {
        await ProfilesAsync();
        await CompactPersistenceAsync();
    }

    private static async Task ProfilesAsync()
    {
        using var directory = new Program.TemporaryDirectory();
        var paths = new AppPaths(directory.Path);
        // Disconnected runtime: no game pipe, network or real settings.
        await using var runtime = new BridgeRuntime(paths);
        await runtime.InitializeVotingModuleAsync();
        var originalProfile = runtime.Settings.ActiveProfileId;
        var action = new ActionDefinition { HandlerId = "add_radiation", DisplayName = "Original", IsActive = true,
            Triggers = [new TriggerRule { Amount = 25 }] };
        runtime.Settings.Actions.AddRange(Enumerable.Range(0, 50).Select(_ => action.CloneAsNewInstance()));
        runtime.Settings.UserPresets.Add(action.Clone());
        var draft = runtime.Voting.Snapshot;
        draft.TimeGoal = true; draft.MoneyGoal = false; draft.DurationSeconds = 600; draft.Condition = VotingCondition.All;
        draft.AcceptYouTubeVotes = true; draft.AllowYouTubeVoteChange = true; draft.YouTubeVoteWeight = 3;
        draft.Options = [new() { Command = "!a", Preset = action }, new() { Command = "!b", Preset = action }];
        await runtime.Voting.SaveSettingsAsync(draft);
        await runtime.Voting.SetCollectionActiveAsync(draft.CollectionId, true, originalProfile);
        var donation = new DonationEvent { Id = "disarmed-donation", Amount = 25, Currency = "RUB", Message = "!a", CreatedAt = DateTimeOffset.UtcNow };
        await runtime.ProcessDonationAsync(donation);
        await runtime.ProcessDonationAsync(donation);
        Check(!runtime.Settings.Armed && runtime.Settings.Actions.Count == 50 && runtime.Settings.Actions.All(item => item.IsActive), "OFF changed configured effects");
        Check(runtime.Voting.Snapshot.Total == 25 && (await new JsonPendingDispatchStore(paths).LoadAsync()).Count == 0,
            "OFF blocked active donation voting, duplicated a vote or enqueued an ordinary effect");
        Check((await new JsonLineHistoryStore(paths).LoadRecentAsync()).Single().State == DispatchState.Skipped,
            "Ordinary donation effect was not skipped");
        var round = runtime.Voting.Snapshot;
        await runtime.Voting.RecordYouTubeAsync(round.Id, originalProfile, [new("chat-original", "channel", "!b", DateTimeOffset.UtcNow)]);
        var total = runtime.Voting.Snapshot.Total;
        var copyProfile = await runtime.CreateProfileAsync("With votes", copyUserPresets: false, copyVotings: true);
        var copy = runtime.Voting.CollectionsFor(copyProfile.Id).Single();
        var old = runtime.Voting.SnapshotFor(draft.CollectionId);
        Check(copy is { IsActive: false, Phase: VotingPhase.Draft, MoneyGoal: false, TimeGoal: true, DurationSeconds: 600,
            Condition: VotingCondition.All, AcceptYouTubeVotes: true, AllowYouTubeVoteChange: true, YouTubeVoteWeight: 3 }
            && copy.Total == 0 && copy.YouTubeParticipants.Count == 0 && copy.SeenYouTubeMessageIds.Count == 0 && copy.Intervals.Count == 0 && copy.Deadline is null,
            "Profile copy lost settings or carried money/participants/timer/activation");
        Check(copy.Options.Select(item => item.Command).SequenceEqual(["!a", "!b"]) && copy.Options[0].Id != old.Options[0].Id &&
            copy.CollectionId != old.CollectionId && copy.Id != old.Id && copy.Options[0].Preset!.Id != old.Options[0].Preset!.Id,
            "Copied commands or IDs are not independent");
        Check(old.Phase == VotingPhase.Paused && !old.IsActive && old.Total == total && old.YouTubeParticipants.Count == 1,
            "Leaving profile did not preserve paused accounting");
        Check(!runtime.Voting.Automation.Enabled && !runtime.Settings.Armed && copyProfile.UserPresets.Count == 0,
            "Creating profile activated voting/ordinary effects or tied the independent copy checkboxes together");
        var blank = await runtime.CreateProfileAsync("Empty", copyUserPresets: true, copyVotings: false);
        Check(runtime.Voting.CollectionsFor(blank.Id).Count == 0, "Unchecked copy created votes");
        var blankVote = await runtime.Voting.AddCollectionAsync(profileId: blank.Id);
        Check(runtime.Voting.SnapshotFor(blankVote).ProfileId == blank.Id, "New vote not owned by profile");
        copy.Options[0].Command = "!different"; copy.Options[0].Preset!.DisplayName = "Changed copy";
        await runtime.Voting.SaveSettingsAsync(copy);
        await runtime.SwitchProfileAsync(originalProfile);
        Check(runtime.Voting.CollectionsFor(originalProfile).Single().Options[0].Command == "!a" &&
            runtime.Voting.CollectionsFor(originalProfile).Single().Options[0].Preset!.DisplayName == "Original" &&
            runtime.Voting.Snapshot.Total == total && !runtime.Voting.Automation.Enabled,
            "Copy editing leaked to source or switching back started voting");
        await runtime.Voting.SetCollectionActiveAsync(old.CollectionId, true, originalProfile);
        await runtime.Voting.RecordYouTubeAsync(old.Id, originalProfile, [new("chat-return", "channel", "!b", DateTimeOffset.UtcNow)]);
        Check(runtime.Voting.Snapshot.Total == total, "Profile switch cleared the participant registry");
        await runtime.SwitchProfileAsync(copyProfile.Id);
        await runtime.DeleteProfileAsync(copyProfile.Id);
        Check(runtime.Voting.CollectionsFor(copyProfile.Id).Count == 0 && runtime.Voting.CollectionsFor(originalProfile).Single().Total == total,
            "Deleting profile removed another profile's votes");
        var persisted = await new JsonSettingsStore(paths).LoadAsync();
        var restarted = new VotingCoordinator(new JsonVotingStore(paths)); await restarted.InitializeAsync();
        await restarted.EnterProfileAsync(persisted.ActiveProfileId);
        Check(restarted.CollectionsFor(originalProfile).Single().Total == total && restarted.CollectionsFor(blank.Id).Single().CollectionId == blankVote &&
            restarted.CollectionsFor(copyProfile.Id).Count == 0, "Restart lost profile bindings or resurrected a deleted profile");
    }

    private static async Task CompactPersistenceAsync()
    {
        using var directory = new Program.TemporaryDirectory();
        var paths = new AppPaths(directory.Path); paths.EnsureExists();
        var choices = Enumerable.Range(0, 500).Select(i => "npc_" + i).ToList();
        var preset = new ActionDefinition { HandlerId = "spawn_named_companion", DisplayName = "Selected model",
            Parameters = new() { ["section_id"] = "npc_12" },
            ParameterDefinitions = [new() { Key = "section_id", Kind = ParameterKind.Choice, CatalogKind = CatalogEntryKind.Npc,
                Options = choices, OptionLabels = choices.ToDictionary(id => id, id => "Long catalog label " + id) }] };
        var legacy = new VotingDocument { SchemaVersion = 6 };
        legacy.Current.Options = [new() { Command = "!a", Preset = preset }, new() { Command = "!b", Preset = preset.Clone() }];
        legacy.Current.Options[0].Amount = 31; legacy.Current.Options[0].AmountsByCurrency["RUB"] = 31;
        legacy.Current.YouTubeParticipants["account"] = new() { OptionId = legacy.Current.Options[0].Id, Weight = 1, LastMessageId = "seen", LastMessageAt = DateTimeOffset.UtcNow };
        legacy.Current.SeenYouTubeMessageIds.Add("seen"); legacy.SeenDonationIds.Add("real");
        for (var i = 0; i < 20; i++) legacy.PreviousRounds.Add(JsonSerializer.Deserialize<VotingRound>(JsonSerializer.Serialize(legacy.Current, JsonDefaults.Options), JsonDefaults.Options)!);
        var file = Path.Combine(paths.RootDirectory, "voting.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(legacy, JsonDefaults.Options));
        var sizeBefore = new FileInfo(file).Length;
        var coordinator = new VotingCoordinator(new JsonVotingStore(paths)); await coordinator.InitializeAsync();
        await coordinator.EnterProfileAsync("migrated");
        var saved = await new JsonVotingStore(paths).LoadAsync();
        Check(new FileInfo(file).Length < sizeBefore / 5 && saved.PreviousRounds.Count == 20 && saved.Current.Total == 31 &&
            saved.Current.YouTubeParticipants.ContainsKey("account") && saved.SeenDonationIds.Contains("real"), "Compaction lost history/accounting or retained the whole catalog");
        Check(saved.Current.Options[0].Preset!.Validate().Count == 0 && preset.ParameterDefinitions[0].Options.Count == 500,
            "Compaction broke the configured effect or mutated the template catalog");
        var invalid = coordinator.Snapshot; invalid.Options[0].Preset!.Parameters["section_id"] = "unscanned";
        await coordinator.SaveSettingsAsync(invalid);
        var reload = new VotingCoordinator(new JsonVotingStore(paths)); await reload.InitializeAsync();
        Check(reload.Snapshot.Options[0].Preset!.Validate().Count > 0 && reload.CollectionsFor("migrated").Count == 1,
            "Compaction approved an unknown catalog selection or lost legacy profile migration");
    }

    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
}
