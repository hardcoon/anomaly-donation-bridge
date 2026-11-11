using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.Tests;

internal static class VotingTests
{
    public static async Task RunAsync()
    {
        Check(new VotingRound() is { Target: 1000, MoneyGoal: true, TimeGoal: false }, "New vote defaults are money 1000 ON, time OFF");
        Check(new VotingAutomation() is { Enabled: false, AutomaticDraw: true, AutomaticWinnerEffect: true, AutoNextCollection: true,
            Order: VotingSequenceOrder.InOrder, ShowGameHud: true, GameHudFormat: VotingHudFormat.Percent, ShowGameHudCommands: true, ShowGameHudTemplateNames: false },
            "Fresh voting defaults match requested automatic sequence/percentage HUD, without activating a vote");
        var savedManual = System.Text.Json.JsonSerializer.Deserialize<VotingAutomation>(
            "{\"AutomaticDraw\":false,\"AutomaticWinnerEffect\":false,\"AutoNextCollection\":false,\"ShowGameHud\":false,\"ShowGameHudCommands\":false}")!;
        Check(!savedManual.AutomaticDraw && !savedManual.AutomaticWinnerEffect && !savedManual.AutoNextCollection && !savedManual.ShowGameHud && !savedManual.ShowGameHudCommands && !savedManual.ShowGameHudTemplateNames,
            "New defaults must not overwrite explicit saved manual/OFF choices");
        Check(VotingRules.ContainsCommand("голосую за !ДОЛГ!", "!долг"), "Literal case-insensitive matching");
        Check(!VotingRules.ContainsCommand("!долгий", "!долг"), "No command prefix matches");
        Check(VotingRules.ContainsCommand("за красный отряд сегодня", "красный отряд"), "Arbitrary literal phrases");
        Check(!VotingRules.ContainsCommand("anything", ".*"), "Not a regex");
        await ConditionsAsync();
        await AccountingAsync();
        await AtomicityAsync();
        await PersistenceAsync();
        await OrdinaryDispatchAsync();
        await LiveEditingAsync();
        await TimerEditingAsync();
        await MultipleCollectionsAsync();
        await LegacyVotingMigrationAsync();
        await GamePresentationAsync();
    }

    private static async Task ConditionsAsync()
    {
        var f = await Fixture.CreateAsync(VotingCondition.All);
        f.Now = f.Now.AddMinutes(3); await f.Add("early", 1000, "!a");
        Check(f.Engine.Snapshot.Phase == VotingPhase.Collecting, "ALL must wait for full duration");
        await Throws(() => f.Engine.ChooseWinnerAsync());
        f.Now = f.Now.AddMinutes(7); await f.Engine.TickAsync();
        Check(f.Engine.Snapshot.Phase == VotingPhase.Ready, "ALL succeeds at the deadline");
        Check(f.Engine.Snapshot.Winner is null, "Timer does not auto-spin");

        f = await Fixture.CreateAsync(VotingCondition.Any);
        var successor = await f.Engine.AddCollectionAsync(); await ConfigureAsync(f.Engine, Draft(VotingCondition.Any), successor);
        await f.Add("or-money-before-time", 1000, "!a");
        var closed = f.Engine.Snapshot;
        Check(closed.Phase == VotingPhase.Ready && closed.ClosedAt == f.Now && closed.Deadline > f.Now,
            "OR money goal closes the vote immediately while its timer still has time left");
        var result = await f.Engine.ChooseWinnerAsync(requireGamePresentation: true);
        await f.Engine.CompleteGamePresentationAsync(result.Id); await f.Engine.MarkDeliveryAsync(result.Id, "queued");
        f.Now = f.Now.AddSeconds(8); await f.Engine.StartNextCollectionAsync("profile");
        Check(f.Engine.SnapshotFor(successor).IsActive && f.Engine.SnapshotFor(successor).Phase == VotingPhase.Collecting &&
            !f.Engine.SnapshotFor(closed.CollectionId).IsActive && f.Now < closed.Deadline,
            "After OR winner presentation/delivery, automatic queue advances without waiting for the old deadline");

        f = await Fixture.CreateAsync(VotingCondition.All); await f.Add("short", 200, "!b");
        f.Now = f.Now.AddMinutes(10); await f.Engine.TickAsync();
        Check(f.Engine.Snapshot.Phase == VotingPhase.Failed, "ALL fails on insufficient funds");
        Check(await f.Add("too-late", 1000, "!a") == "outside_collection", "Late donations do not reopen a round");
        // Provider recovery delivered after restart, but created inside the deadline.
        Check(await f.Add("recovered", 800, "!a", f.Now.AddMinutes(-1)) == "counted", "Recover in-window contribution");
        Check(f.Engine.Snapshot.Phase == VotingPhase.Ready, "Recovered contribution can satisfy ALL before drawing");

        f = await Fixture.CreateAsync(VotingCondition.Any); await f.Add("goal", 1200, "!a");
        Check(f.Engine.Snapshot.Phase == VotingPhase.Ready && f.Engine.Snapshot.Total == 1200, "ANY closes early; full overshoot counts");
        f = await Fixture.CreateAsync(VotingCondition.Any); await f.Add("partial", 200, "!a");
        f.Now = f.Now.AddMinutes(10); await f.Engine.TickAsync();
        Check(f.Engine.Snapshot.Phase == VotingPhase.Ready, "ANY accepts partial total at timeout");
        f = await Fixture.CreateAsync(VotingCondition.Any); f.Now = f.Now.AddMinutes(10); await f.Engine.TickAsync();
        Check(f.Engine.Snapshot.Phase == VotingPhase.Failed, "Zero paid votes cannot be drawn");

        f = await Fixture.CreateAsync(VotingCondition.All); f.Now = f.Now.AddMinutes(2);
        await f.Engine.SetEnabledAsync(false, "profile"); var remaining = f.Engine.Snapshot.Deadline - f.Engine.Snapshot.PausedAt;
        f.Now = f.Now.AddHours(1); await f.Engine.TickAsync();
        Check(f.Engine.Snapshot.Phase == VotingPhase.Paused, "Pause freezes timeout");
        Check(await f.Add("paused", 100, "!a") == "outside_collection", "Paused donations ignored");
        await f.Engine.SetEnabledAsync(true, "profile");
        Check(f.Engine.Snapshot.Deadline - f.Now == remaining, "Resume retains remaining duration");
    }

    private static async Task AccountingAsync()
    {
        var f = await Fixture.CreateAsync(VotingCondition.All);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => f.Add("same", 100, "!a !a")));
        Check(f.Engine.Snapshot.Total == 100, "Concurrent duplicate and repeated command count once");
        Check(await f.Add("mixed", 100, "!a !b") == "ambiguous_commands", "Ambiguous message ignored");
        Check(await f.Add("prefix", 100, "!ab") == "no_command", "Prefix is not a command");
        Check(await f.Engine.RecordAsync(new DonationEvent { Id = "usd", Amount = 100, Currency = "USD", Message = "!a", CreatedAt = f.Now }, "profile") == "currency_mismatch", "No currency mixing");
        Check(await f.Engine.RecordAsync(new DonationEvent { Id = "profile", Amount = 100, Message = "!a", CreatedAt = f.Now }, "other") == "not_collecting", "Profile isolation");
        Check(f.Engine.Snapshot.Total == 100, "Ignored messages do not change totals");
        await f.Engine.ResetAmountsAsync(); await f.Engine.SetEnabledAsync(true, "profile");
        Check(await f.Add("same", 100, "!a") == "duplicate", "Dedup ledger survives new round");
        var draft = f.Engine.Snapshot;
        await f.Engine.SaveSettingsAsync(draft);
        Check(f.Engine.Snapshot.Phase == VotingPhase.Collecting, "Live save must not stop collection");
    }

    private static async Task AtomicityAsync()
    {
        var f = await Fixture.CreateAsync(VotingCondition.Any);
        f.Store.FailNext = true;
        await Throws(() => f.Add("disk", 100, "!a"));
        Check(f.Engine.Snapshot.Total == 0, "Disk error does not publish contribution");
        Check(await f.Add("disk", 100, "!a") == "counted", "Failed write does not consume donation ID");
        f.Store.FailNext = true; await Throws(() => f.Engine.SetCollectionActiveAsync(f.Engine.Snapshot.CollectionId, false, "profile"));
        Check(f.Engine.Automation.Enabled && f.Engine.Snapshot.IsActive, "Failed OFF must not publish a partial active state");
        await f.Add("rest", 900, "!b");
        f.Store.FailNext = true;
        await Throws(() => f.Engine.ChooseWinnerAsync());
        Check(f.Engine.Snapshot.Winner is null, "Never publish an unsaved winner");
        var first = await f.Engine.ChooseWinnerAsync(); var winner = first.WinnerOptionId;
        f.Store.FailNext = true; await Throws(() => f.Engine.ResetAmountsAsync());
        Check(f.Engine.PendingWinners.Single().Id == first.Id && f.Engine.Snapshot.Total == 1000, "Failed reset cannot cancel a winner or clear money");
        var clone = new VotingCoordinator(f.Store, () => f.Now, _ => throw new Exception("Reroll forbidden"));
        await clone.InitializeAsync();
        var second = await clone.ChooseWinnerAsync();
        Check(second.WinnerOptionId == winner && second.DeliveryId == first.DeliveryId, "Restart retains winner and outbox identity");
        await clone.ResetAmountsAsync();
        Check(clone.PendingWinners.Count == 0, "Reset cancels the unsent result");
        await Throws(() => clone.MarkDeliveryAsync(first.Id, "queued"));
        Check(clone.Snapshot.Total == 0 && clone.Snapshot.Id != first.Id && f.Store.Data.PreviousRounds.Count > 0, "New round archives previous result");
    }

    private static async Task PersistenceAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "work", "tests", "voting", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root); var store = new JsonVotingStore(paths);
            var now = DateTimeOffset.Parse("2026-10-22T10:00:00Z");
            var engine = new VotingCoordinator(store, () => now, _ => 0);
            await engine.InitializeAsync(); await ConfigureAsync(engine, Draft(VotingCondition.Any)); await engine.SetEnabledAsync(true, "profile");
            await engine.RecordAsync(new DonationEvent { Id = "real", Amount = 1000, Message = "!a", CreatedAt = now }, "profile");
            var winner = await engine.ChooseWinnerAsync();
            var restored = new VotingCoordinator(new JsonVotingStore(paths), () => now, _ => throw new Exception("No reroll"));
            await restored.InitializeAsync();
            Check(restored.Snapshot.WinnerOptionId == winner.WinnerOptionId && restored.Snapshot.Total == 1000, "JSON roundtrip");
            Check(File.Exists(store.FilePath + ".bak"), "Previous atomic snapshot retained");
            await File.WriteAllTextAsync(store.FilePath, "broken json");
            await Throws(() => new JsonVotingStore(paths).LoadAsync());
            Check(await File.ReadAllTextAsync(store.FilePath) == "broken json", "Corrupt accounting is not silently overwritten");
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task OrdinaryDispatchAsync()
    {
        var f = await Fixture.CreateAsync(VotingCondition.Any);
        var action = Draft(VotingCondition.Any).Options[0].Preset!;
        action.IsActive = true; action.Triggers = [new TriggerRule { Amount = 100, Currency = "RUB" }];
        var donation = new DonationEvent { Id = "both", Amount = 100, Currency = "RUB", Message = "!a", CreatedAt = f.Now };
        var ledger = new InMemoryProcessedDonationStore(); var dispatcher = new DonationDispatcher(ledger, new InMemoryHistorySink());
        await f.Engine.RecordAsync(donation, "profile");
        await dispatcher.EnqueueAsync(donation, [action], new QueuePolicy());
        Check(f.Engine.Snapshot.Total == 100 && dispatcher.Count == 1, "Ordinary effect and voting both occur");
        await f.Add("finish", 900, "!b"); var selected = await f.Engine.ChooseWinnerAsync();
        var synthetic = new DonationEvent { Id = selected.DeliveryId, Amount = selected.Total, Currency = selected.Currency };
        await dispatcher.EnqueueActionAsync(synthetic, selected.Winner!.Preset!, new QueuePolicy());
        var count = dispatcher.Count;
        Check(await dispatcher.EnqueueActionAsync(synthetic, selected.Winner.Preset!, new QueuePolicy()) == DispatchState.Duplicate && dispatcher.Count == count, "Winner outbox is idempotent in existing dispatcher");
    }

    internal static VotingRound Draft(VotingCondition condition) => new()
    {
        Condition = condition, TimeGoal = true, Options = [new VotingOption { Command = "!a", Preset = Action("first") }, new VotingOption { Command = "!b", Preset = Action("second") }]
    };
    private static async Task ConfigureAsync(VotingCoordinator engine, VotingRound settings, string? collectionId = null)
    {
        var current = engine.SnapshotFor(collectionId); settings.Id = current.Id; settings.CollectionId = current.CollectionId;
        await engine.SaveSettingsAsync(settings);
    }

    private static async Task LiveEditingAsync()
    {
        var f = await Fixture.CreateAsync(VotingCondition.All);
        var stale = f.Engine.Snapshot; var deadline = stale.Deadline;
        await f.Add("during-edit", 200, "!a");
        stale.Options[0].Command = "!новая"; stale.Options[0].Preset = Action("changed");
        stale.Options.Add(new VotingOption { Command = "!c", Preset = Action("third"), Amount = 99999 });
        await f.Engine.SaveSettingsAsync(stale);
        Check(f.Engine.Snapshot.Options[0].Amount == 200 && f.Engine.Snapshot.Options[2].Amount == 0, "Live edits merge latest balances; new choice starts at zero");
        Check(f.Engine.Snapshot.Phase == VotingPhase.Collecting && f.Engine.Snapshot.Deadline == deadline, "Saving commands does not restart timer");
        Check(await f.Add("old-command", 10, "!a") == "no_command" && await f.Add("new-command", 10, "!новая") == "counted", "Renamed command takes effect");
        var edit = f.Engine.Snapshot; edit.Currency = "USD"; await f.Engine.SaveSettingsAsync(edit);
        Check(f.Engine.Snapshot.Total == 0, "Currency change does not relabel existing RUB as USD");
        edit = f.Engine.Snapshot; edit.Currency = "RUB"; await f.Engine.SaveSettingsAsync(edit);
        Check(f.Engine.Snapshot.Total == 210, "Switching currency back retains balance");
        edit = f.Engine.Snapshot; edit.Options[1].Enabled = false; edit.Options.RemoveAt(2); await f.Engine.SaveSettingsAsync(edit);
        Check(f.Engine.Snapshot.Options.Count == 2 && !f.Engine.Snapshot.Options[1].Enabled, "Can disable and delete choices live");
        edit = f.Engine.Snapshot; edit.DurationSeconds += 60; edit.Target = 2000; await f.Engine.SaveSettingsAsync(edit);
        Check(f.Engine.Snapshot.Deadline == f.Now.AddSeconds(edit.DurationSeconds), "Duration edits restart the full countdown");
        var beforeReset = f.Engine.Snapshot; f.Now = f.Now.AddSeconds(1);
        await f.Engine.ResetAmountsAsync(); var reset = f.Engine.Snapshot;
        Check(reset.Total == 0 && reset.CollectionId == beforeReset.CollectionId && reset.Id != beforeReset.Id &&
            reset.Deadline == beforeReset.Deadline && reset.Target == 2000 && reset.Options[0].Command == "!новая" &&
            reset.Options[0].Preset!.DisplayName == "changed" && !reset.Options[1].Enabled, "Reset clears balances, not settings or timer");
        Check(await f.Add("recovered-old", 100, "!новая", f.Now.AddSeconds(-1)) == "outside_collection", "Recovery cannot refill amounts reset by user");
        Check(await f.Add("during-edit", 200, "!новая") == "duplicate", "Reset retains dedup ledger");
        f.Store.FailNext = true; edit = f.Engine.Snapshot; edit.Target = 3000;
        await Throws(() => f.Engine.SaveSettingsAsync(edit));
        Check(f.Engine.Snapshot.Target == 2000, "Failed live save is atomic");

        f = await Fixture.CreateAsync(VotingCondition.Any); await f.Add("closed", 1000, "!a");
        edit = f.Engine.Snapshot; edit.Target = 2000; await f.Engine.SaveSettingsAsync(edit);
        Check(f.Engine.Snapshot.Phase == VotingPhase.Collecting && f.Engine.Snapshot.Total == 1000, "Raising target reopens a completed collection without losing funds");
    }

    private static async Task TimerEditingAsync()
    {
        var f = await Fixture.CreateAsync(VotingCondition.All);
        await f.Add("before-timer-edit", 200, "!a");
        var original = f.Engine.Snapshot;
        f.Now = f.Now.AddMinutes(9);
        var edit = f.Engine.Snapshot; edit.DurationSeconds = 180;
        await f.Engine.SaveSettingsAsync(edit);
        var restarted = f.Engine.Snapshot;
        Check(restarted.Deadline == f.Now.AddMinutes(3) && restarted.Phase == VotingPhase.Collecting && restarted.Total == 200 &&
            restarted.Id == original.Id && restarted.Intervals.Count == original.Intervals.Count,
            "Editing 10 to 3 after 9 elapsed minutes grants 3 fresh minutes without resetting money or collection");
        Check(await f.Add("before-timer-edit", 200, "!a") == "duplicate", "Timer edit preserves donation deduplication");
        f.Now = f.Now.AddMinutes(1);
        edit = f.Engine.Snapshot; edit.Target = 2000; edit.Title = "Edited title";
        await f.Engine.SaveSettingsAsync(edit);
        Check(f.Engine.Snapshot.Deadline == restarted.Deadline, "Unchanged duration and other edits must not restart countdown");
        var restored = new VotingCoordinator(f.Store, () => f.Now, _ => 0); await restored.InitializeAsync();
        Check(restored.Snapshot.Deadline == restarted.Deadline && restored.Snapshot.Deadline - f.Now == TimeSpan.FromMinutes(2) &&
            restored.Snapshot.Total == 200, "Restart restores the edited deadline and balances without restarting time again");
        edit = f.Engine.Snapshot; edit.DurationSeconds = 1800;
        f.Store.FailNext = true; await Throws(() => f.Engine.SaveSettingsAsync(edit));
        Check(f.Engine.Snapshot.Deadline == restarted.Deadline && f.Engine.Snapshot.DurationSeconds == 180,
            "Disk failure cannot partially restart countdown");
        await f.Engine.SaveSettingsAsync(edit);
        Check(f.Engine.Snapshot.Deadline == f.Now.AddMinutes(30), "Increasing duration also starts the full new countdown");

        f = await Fixture.CreateAsync(VotingCondition.All); await f.Add("paused-funds", 200, "!b");
        f.Now = f.Now.AddMinutes(9); await f.Engine.SetEnabledAsync(false, "profile");
        f.Now = f.Now.AddHours(1); edit = f.Engine.Snapshot; edit.DurationSeconds = 180;
        await f.Engine.SaveSettingsAsync(edit);
        Check(f.Engine.Snapshot.Phase == VotingPhase.Paused && !f.Engine.Automation.Enabled &&
            f.Engine.Snapshot.Deadline - f.Engine.Snapshot.PausedAt == TimeSpan.FromMinutes(3),
            "Editing a paused timer resets remaining duration without activating the vote");
        f.Now = f.Now.AddHours(1); await f.Engine.SetEnabledAsync(true, "profile");
        Check(f.Engine.Snapshot.Deadline == f.Now.AddMinutes(3) && f.Engine.Snapshot.Total == 200,
            "Paused edited timer starts its full duration on resume, keeping funds");

        foreach (var condition in new[] { VotingCondition.All, VotingCondition.Any })
        {
            f = await Fixture.CreateAsync(condition); await f.Add("timeout-funds", 200, "!a");
            f.Now = f.Now.AddMinutes(10); await f.Engine.TickAsync();
            Check(f.Engine.Snapshot.Phase == (condition == VotingCondition.All ? VotingPhase.Failed : VotingPhase.Ready),
                "Fixture reached a closed, undrawn timeout");
            edit = f.Engine.Snapshot; edit.DurationSeconds = 180; await f.Engine.SaveSettingsAsync(edit);
            Check(f.Engine.Snapshot.Phase == VotingPhase.Collecting && f.Engine.Snapshot.ClosedAt is null &&
                f.Engine.Snapshot.Deadline == f.Now.AddMinutes(3) && f.Engine.Snapshot.Total == 200,
                "Duration edit reopens an undrawn timed-out vote without losing balances");
            f.Now = f.Now.AddMinutes(3); await f.Engine.TickAsync();
            Check(f.Engine.Snapshot.Phase == (condition == VotingCondition.All ? VotingPhase.Failed : VotingPhase.Ready),
                "Edited countdown expires normally under AND/OR rules");
        }

        f = await Fixture.CreateAsync(VotingCondition.Any); await f.Add("winner", 1000, "!a");
        var drawn = await f.Engine.ChooseWinnerAsync(); edit = f.Engine.Snapshot; edit.DurationSeconds = 180;
        await f.Engine.SaveSettingsAsync(edit);
        Check(f.Engine.Snapshot.Phase == VotingPhase.Drawn && f.Engine.PendingWinners.Single().WinnerOptionId == drawn.WinnerOptionId &&
            f.Engine.PendingWinners.Single().FrozenTotal == drawn.FrozenTotal, "Timer edit cannot reroll or cancel a frozen winner");
    }

    private static async Task MultipleCollectionsAsync()
    {
        var f = await Fixture.CreateAsync(VotingCondition.Any); var firstId = f.Engine.Snapshot.CollectionId;
        var secondId = await f.Engine.AddCollectionAsync(); var second = Draft(VotingCondition.Any);
        second.Title = "Второе"; second.Options[0].Command = "!c"; second.Options[1].Command = "!d";
        await ConfigureAsync(f.Engine, second, secondId);
        var thirdId = await f.Engine.AddCollectionAsync(); var third = Draft(VotingCondition.Any);
        third.Title = "Третье"; third.Options[0].Command = "!e"; third.Options[1].Command = "!f";
        await ConfigureAsync(f.Engine, third, thirdId);
        Check(f.Engine.Collections.Count(round => round.IsActive) == 1 && f.Engine.Collections.Count(round => round.Phase == VotingPhase.Collecting) == 1,
            "Adding and configuring votes must not start parallel collection");
        await f.Add("first", 100, "!a"); await f.Add("inactive", 200, "!c");
        Check(f.Engine.SnapshotFor(firstId).Total == 100 && f.Engine.SnapshotFor(secondId).Total == 0, "Only the active vote accepts money");
        f.Now = f.Now.AddSeconds(1); await f.Engine.SetCollectionActiveAsync(secondId, true, "profile");
        var firstRemaining = f.Engine.SnapshotFor(firstId).Deadline - f.Engine.SnapshotFor(firstId).PausedAt;
        Check(!f.Engine.SnapshotFor(firstId).IsActive && f.Engine.SnapshotFor(firstId).Phase == VotingPhase.Paused &&
            f.Engine.SnapshotFor(secondId).IsActive, "Activation atomically pauses the previous vote");
        await f.Add("second", 200, "!c"); await f.Add("shared", 50, "!a !c");
        Check(f.Engine.SnapshotFor(firstId).Total == 100 && f.Engine.SnapshotFor(secondId).Total == 250, "Shared text cannot fund inactive votes");
        Check(await f.Add("shared", 50, "!a !c") == "duplicate", "Dedup survives active switching");
        await f.Engine.SelectCollectionAsync(thirdId, "profile");
        Check(f.Engine.SnapshotFor(secondId).IsActive && f.Engine.SnapshotFor(thirdId).Phase == VotingPhase.Draft,
            "Navigation must not activate a page");
        await f.Engine.SetSequenceAsync([thirdId, secondId, firstId], new HashSet<string> { firstId });
        Check(f.Engine.SnapshotFor(secondId).IsActive && f.Engine.SnapshotFor(secondId).Phase == VotingPhase.Collecting &&
            !f.Engine.SnapshotFor(secondId).UseInSequence, "Queue checkboxes do not switch activation");
        f.Now = f.Now.AddSeconds(1); await f.Engine.SetCollectionActiveAsync(thirdId, true, "profile"); await f.Add("third", 300, "!e");
        await f.Engine.SetCollectionActiveAsync(thirdId, false, "profile");
        Check(!f.Engine.Automation.Enabled && f.Engine.Collections.All(round => !round.IsActive && round.Phase != VotingPhase.Collecting),
            "Turning the active vote off stops the whole collection");
        f.Now = f.Now.AddMinutes(10); await f.Engine.TickAsync();
        await f.Engine.SetCollectionActiveAsync(firstId, true, "profile");
        Check(f.Engine.SnapshotFor(firstId).Deadline - f.Now == firstRemaining, "Switching back retains remaining time");
        await f.Add("finish-first", 900, "!a"); var drawn = await f.Engine.ChooseWinnerAsync(collectionId: firstId);
        var frozen = drawn.Winner!.Clone(); var edit = f.Engine.SnapshotFor(firstId);
        edit.Title = "Правки после победы"; edit.Options[0].Command = "!changed"; edit.Options[0].Preset = Action("changed");
        await f.Engine.SaveSettingsAsync(edit);
        Check(f.Engine.PendingWinners.Single().Winner!.Command == frozen.Command, "Editing must not replace the frozen winner");
        await f.Engine.ResetAmountsAsync(firstId);
        Check(f.Engine.PendingWinners.Count == 0 && f.Engine.SnapshotFor(firstId).Total == 0 &&
            f.Engine.SnapshotFor(secondId).Total == 250 && f.Engine.SnapshotFor(thirdId).Total == 300, "Reset cancels only this vote's unsent winner");
        await Throws(() => f.Engine.MarkDeliveryAsync(drawn.Id, "queued"));
        var automation = f.Engine.Automation; automation.AutomaticDraw = true; automation.AutoNextCollection = true;
        automation.AutomaticWinnerEffect = false; await f.Engine.SaveAutomationAsync(automation);
        await f.Engine.SetSequenceAsync([firstId, secondId, thirdId], new HashSet<string> { secondId });
        await f.Engine.SetCollectionActiveAsync(thirdId, true, "profile");
        await f.Add("finish-third", 700, "!e"); drawn = await f.Engine.ChooseWinnerAsync(collectionId: thirdId);
        f.Now = f.Now.AddSeconds(8); await f.Engine.StartNextCollectionAsync("profile");
        Check(f.Engine.SnapshotFor(secondId).IsActive && f.Engine.SnapshotFor(secondId).Total == 250 &&
            !f.Engine.SnapshotFor(thirdId).IsActive && f.Engine.PendingWinners.Single().Id == drawn.Id,
            "Automatic queue starts one eligible vote and preserves unsent manual winners");
        await f.Engine.SetCollectionActiveAsync(secondId, false, "profile"); f.Now = f.Now.AddMinutes(1);
        await f.Engine.StartNextCollectionAsync("profile");
        Check(!f.Engine.Automation.Enabled && f.Engine.Collections.All(round => !round.IsActive), "Automatic logic cannot restart after explicit OFF");
        var restored = new VotingCoordinator(f.Store, () => f.Now, _ => 0); await restored.InitializeAsync();
        Check(restored.Collections.Count == 3 && !restored.Automation.Enabled && restored.PendingWinners.Single().Id == drawn.Id,
            "Restart retains the OFF state and pending winner");
        f.Store.FailNext = true; await Throws(() => f.Engine.DeleteCollectionAsync(secondId));
        Check(f.Engine.Collections.Count == 3 && f.Engine.SnapshotFor(secondId).Total == 250, "Failed deletion must be atomic");
        await f.Engine.DeleteCollectionAsync(thirdId);
        Check(f.Engine.Collections.Count == 2 && f.Engine.PendingWinners.Count == 0 && f.Engine.SnapshotFor(secondId).Total == 250,
            "Deletion cancels only the removed vote and keeps other amounts");
        await f.Engine.SetCollectionActiveAsync(secondId, true, "profile"); await f.Engine.DeleteCollectionAsync(secondId);
        Check(!f.Engine.Automation.Enabled && f.Engine.Collections.Count == 1, "Deleting the active vote stops collection");
        await f.Engine.DeleteCollectionAsync(firstId);
        Check(f.Engine.Collections.Count == 0 && !f.Engine.Automation.Enabled, "Deleting the last vote must leave an empty list");
        restored = new VotingCoordinator(f.Store, () => f.Now, _ => 0); await restored.InitializeAsync();
        Check(restored.Collections.Count == 0, "Deleted last vote reappeared after restart");
        var added = await restored.AddCollectionAsync();
        Check(restored.Collections.Count == 1 && restored.Collections[0].CollectionId == added && !restored.Collections[0].IsActive,
            "A vote can be added after deleting the last one");
        var inherited = restored.SnapshotFor(added); inherited.Target = 2300; await restored.SaveSettingsAsync(inherited);
        var nextId = await restored.AddCollectionAsync();
        Check(restored.SnapshotFor(nextId) is { Target: 2300, MoneyGoal: true, TimeGoal: false }, "New vote inherits current amount only");
        inherited = restored.SnapshotFor(nextId); inherited.Target = 5400; inherited.TimeGoal = true; inherited.MoneyGoal = false;
        inherited.DurationSeconds = 600; inherited.Condition = VotingCondition.All; inherited.Currency = "EUR";
        inherited.AcceptDonationVotes = false; inherited.AcceptYouTubeVotes = true; inherited.YouTubeVoteWeight = 7; inherited.AllowYouTubeVoteChange = true;
        await restored.SaveSettingsAsync(inherited);
        var selectedSourceId = await restored.AddCollectionAsync(sourceCollectionId: nextId);
        Check(restored.SnapshotFor(selectedSourceId) is { Target: 5400, MoneyGoal: false, TimeGoal: true, DurationSeconds: 600, Condition: VotingCondition.All,
            Currency: "EUR", AcceptDonationVotes: false, AcceptYouTubeVotes: true, YouTubeVoteWeight: 7, AllowYouTubeVoteChange: true, IsActive: false }
            && restored.SnapshotFor(selectedSourceId).Options.Count == 0,
            "Inheritance must copy every side setting from the selected page, without commands or activation");
    }

    private static async Task LegacyVotingMigrationAsync()
    {
        var store = new MemoryStore { Data = new VotingDocument { SchemaVersion = 1 } };
        var old = store.Data.Current; var now = DateTimeOffset.Parse("2026-10-22T10:00:00Z");
        old.ProfileId = "profile"; old.Phase = VotingPhase.Collecting; old.Deadline = now.AddMinutes(10);
        old.Options = Draft(VotingCondition.All).Options; old.Options[0].Amount = 123;
        old.Intervals = [new VotingInterval { Start = now.AddMinutes(-1) }];
        store.Data.SeenDonationIds.Add("legacy-counted");
        var engine = new VotingCoordinator(store, () => now); await engine.InitializeAsync();
        Check(engine.Snapshot.Total == 123 && engine.Automation.Enabled && engine.Collections.Count == 1,
            "Legacy preview migration lost active progress");
        var edit = engine.Snapshot; edit.Title = "Migrated"; await engine.SaveSettingsAsync(edit);
        Check(store.Data.SchemaVersion == 7 && store.Data.Current.Options[0].AmountsByCurrency["RUB"] == 123 &&
            store.Data.SeenDonationIds.Contains("legacy-counted"), "Migration did not preserve accounting or protect it from old previews");
        store.Data.SchemaVersion = 3;
        var parallel = store.Data.Current.Clone(); parallel.Id = Guid.NewGuid().ToString("N"); parallel.CollectionId = Guid.NewGuid().ToString("N");
        parallel.Options[0].Amount = 77; parallel.Title = "Параллельное"; store.Data.OtherCollections.Add(parallel);
        engine = new VotingCoordinator(store, () => now); await engine.InitializeAsync();
        Check(engine.Collections.Count(round => round.IsActive) == 1 && engine.Collections.Count(round => round.Phase == VotingPhase.Collecting) == 1 &&
            engine.SnapshotFor(parallel.CollectionId).Total == 77 && engine.SnapshotFor(parallel.CollectionId).Phase == VotingPhase.Paused,
            "Parallel preview migration must preserve money and pause the extra collector");
        var reset = await Fixture.CreateAsync(VotingCondition.Any); await reset.Add("winner", 1000, "!a");
        await reset.Engine.ChooseWinnerAsync(); await reset.Engine.ResetAmountsAsync();
        reset.Store.Data.SchemaVersion = 3; reset.Store.Data.PreviousRounds.Single().DeliveryCancelled = false;
        engine = new VotingCoordinator(reset.Store, () => reset.Now); await engine.InitializeAsync();
        Check(engine.PendingWinners.Count == 0 && engine.Snapshot.Total == 0, "Migration must remove the stale activation left by an earlier explicit reset");
    }
    private static ActionDefinition Action(string title) => new() { DisplayName = title, HandlerId = "add_radiation", IsActive = true };
    private static async Task GamePresentationAsync()
    {
        var f = await Fixture.CreateAsync(VotingCondition.Any); await f.Add("game-winner", 1000, "!a");
        var round = await f.Engine.ChooseWinnerAsync(requireGamePresentation: true, automaticEffect: false);
        Check(round.GamePresentationRequired && !round.GamePresentationCompleted && !round.GamePresentationAutomaticEffect,
            "New game presentation is frozen with its effect mode");
        await Throws(() => f.Engine.MarkDeliveryAsync(round.Id, "too early"));
        var restored = new VotingCoordinator(f.Store, () => f.Now, _ => throw new Exception("No reroll")); await restored.InitializeAsync();
        Check(restored.PendingWinners.Single().Id == round.Id && restored.Snapshot.GamePresentationRequired &&
            !restored.Snapshot.GamePresentationCompleted, "Presentation wait must survive restart without redrawing");
        var automation = restored.Automation; automation.AutoNextCollection = true; await restored.SaveAutomationAsync(automation);
        f.Now = f.Now.AddHours(1); await restored.StartNextCollectionAsync("profile");
        Check(restored.Snapshot.Id == round.Id, "Next vote cannot replace a still-open game presentation");
        f.Store.FailNext = true; await Throws(() => restored.CompleteGamePresentationAsync(round.Id));
        Check(!restored.Snapshot.GamePresentationCompleted, "Failed acknowledgement write must not allow delivery");
        await restored.CompleteGamePresentationAsync(round.Id); await restored.CompleteGamePresentationAsync(round.Id);
        await restored.MarkDeliveryAsync(round.Id, "queued");
        Check(restored.Snapshot.DeliveryAccepted && restored.Snapshot.GamePresentationCompleted, "Completed game acknowledgement permits exactly one frozen winner");
        await restored.ResetAmountsAsync();
        Check(!restored.Snapshot.GamePresentationRequired && !restored.Snapshot.GamePresentationCompleted, "Reset must not inherit presentation state");
        f = await Fixture.CreateAsync(VotingCondition.Any); await f.Add("cancel-game", 1000, "!a");
        round = await f.Engine.ChooseWinnerAsync(requireGamePresentation: true);
        await f.Engine.ResetAmountsAsync(); Check(f.Engine.CancelledGamePresentations.Single().Id == round.Id, "Reset retains a cancellation tombstone for an open game UI");
        await f.Engine.CompleteGamePresentationAsync(round.Id);
        Check(f.Engine.PendingWinners.Count == 0 && f.Engine.CancelledGamePresentations.Count == 0, "Late close acknowledgement cannot revive reset winner");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Throws(Func<Task> operation)
    { try { await operation(); } catch { return; } throw new InvalidOperationException("Expected failure"); }

    private sealed class MemoryStore : IVotingStore
    {
        public VotingDocument Data = new(); public bool FailNext;
        public Task<VotingDocument> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Data.Clone());
        public Task SaveAsync(VotingDocument document, CancellationToken cancellationToken = default)
        { if (FailNext) { FailNext = false; throw new IOException("Injected disk error"); } Data = document.Clone(); return Task.CompletedTask; }
    }
    private sealed class Fixture
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-22T10:00:00Z");
        public MemoryStore Store = new(); public VotingCoordinator Engine = null!;
        public static async Task<Fixture> CreateAsync(VotingCondition condition)
        {
            var result = new Fixture(); result.Engine = new VotingCoordinator(result.Store, () => result.Now, _ => 0);
            await result.Engine.InitializeAsync(); await ConfigureAsync(result.Engine, Draft(condition)); await result.Engine.SetEnabledAsync(true, "profile"); return result;
        }
        public Task<string> Add(string id, decimal amount, string message, DateTimeOffset? at = null) => Engine.RecordAsync(new DonationEvent { Id = id, Amount = amount, Message = message, CreatedAt = at ?? Now }, "profile");
    }
}
