using System.IO;
using System.Reflection;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.GameIpc;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.AppTests;

internal static class VotingGameRuntimeTests
{
    public static async Task RunAsync()
    {
        await PdaWinnerMessageAsync();
        using var temporary = new Program.TemporaryDirectory();
        var paths = new AppPaths(temporary.Path);
        await using var runtime = new BridgeRuntime(paths);
        var preset = new ActionDefinition { DisplayName = "Замороженный победитель", HandlerId = "add_radiation", IsActive = true,
            ParameterDefinitions = [new ParameterDefinition { Key = "dose", Kind = ParameterKind.Integer, DefaultValue = "7" }],
            Parameters = new() { ["dose"] = "7" } };
        runtime.Settings.ActiveProfileId = "presentation-test";
        typeof(BridgeRuntime).GetField("_presets", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(runtime, new List<ActionDefinition> { preset });
        await runtime.InitializeVotingModuleAsync();
        var draft = runtime.Voting.Snapshot; draft.TimeGoal = false;
        draft.Options = [new VotingOption { Command = "!a", Preset = preset }, new VotingOption { Command = "!b", Preset = preset }];
        await runtime.Voting.SaveSettingsAsync(draft); await runtime.Voting.SetEnabledAsync(true, runtime.Settings.ActiveProfileId);
        await runtime.Voting.RecordAsync(new DonationEvent { Id = "presentation-funds", Amount = 1000, Currency = "RUB", Message = "!a", CreatedAt = DateTimeOffset.UtcNow }, runtime.Settings.ActiveProfileId);
        var round = await runtime.BeginVotingDrawAsync();
        await runtime.FinishVotingDrawAsync(); await runtime.SubmitVotingWinnerAsync(roundId: round.Id);
        Check(!runtime.Voting.Snapshot.DeliveryAccepted && !File.Exists(paths.PendingQueueFile), "Offline game must not allow a timer/program click to bypass native roulette.");
        var snapshot = runtime.Voting.Snapshot;
        Check(snapshot.GamePresentationRequired && !snapshot.GamePresentationCompleted && runtime.VotingIsSpinning,
            "Offline presentation remains persisted and waiting.");
        var requests = new List<VotingGamePresentation?>(); var replyState = "busy";
        runtime.VotingPresentationTransportForTests = (request, id, cancel, token) =>
        { requests.Add(request); return Task.FromResult(new VotingGameReply(id, cancel ? "done" : replyState, "isolated")); };
        await runtime.AdvanceVotingPresentationAsync(); replyState = "active";
        await runtime.AdvanceVotingPresentationAsync();
        Check(requests[0]?.RoundId == round.Id && requests[1] is null && !File.Exists(paths.PendingQueueFile),
            "A staged snapshot is queried rather than reselected; active UI still prevents delivery.");
        var restored = new VotingCoordinator(new JsonVotingStore(paths)); await restored.InitializeAsync();
        Check(restored.Snapshot.Id == round.Id && restored.Snapshot.WinnerOptionId == round.WinnerOptionId && !restored.Snapshot.GamePresentationCompleted,
            "Restart cannot lose native presentation or redraw winner.");
        replyState = "unknown"; await runtime.AdvanceVotingPresentationAsync(); replyState = "active";
        await runtime.AdvanceVotingPresentationAsync();
        Check(requests[^1]?.RoundId == round.Id && requests[^1]!.WinnerIndex == requests[0]!.WinnerIndex,
            "Lost native staging resends the same frozen winner, never draws again.");
        runtime.Settings.ActiveProfileId = "other-profile"; replyState = "done";
        await runtime.AdvanceVotingPresentationAsync(); Check(!runtime.Voting.Snapshot.GamePresentationCompleted, "Wrong profile cannot acknowledge/send a winner.");
        runtime.Settings.ActiveProfileId = "presentation-test";
        var edited = runtime.Voting.Snapshot; edited.Title = "Заголовок изменён после выбора"; edited.Currency = "USD";
        await runtime.Voting.SaveSettingsAsync(edited);
        await runtime.AdvanceVotingPresentationAsync();
        var queue = await new JsonPendingDispatchStore(paths).LoadAsync();
        Check(runtime.Voting.Snapshot is { GamePresentationCompleted: true, DeliveryAccepted: true } && queue.Count == 1 && queue[0].Donation.Id == round.DeliveryId,
            "Closed native UI should enqueue precisely the saved winning effect.");
        var winnerHistory = (await new JsonLineHistoryStore(paths).LoadRecentAsync()).Single();
        Check(winnerHistory.DisplayDonor == "Голосование" && winnerHistory.Donor == round.Title &&
            winnerHistory.Amount == 1000 && winnerHistory.Currency == "RUB" && winnerHistory.ActionName == preset.DisplayName &&
            winnerHistory.ActionSnapshot?.Parameters["dose"] == "7" && BridgeRuntime.CanRetryHistoryEntry(winnerHistory),
            "Voting history must retain frozen title, amount, currency and repeatable winning preset despite live edits.");
        Check(BridgeRuntime.SelectLastDonationForRepeat([winnerHistory]) is null &&
            new EventHistoryEntry { DonationId = "real-donation", Donor = "Имя" }.DisplayDonor == "Имя",
            "Voting result must not replace the last real donation or relabel ordinary donors.");
        edited = runtime.Voting.Snapshot; edited.Currency = "RUB"; await runtime.Voting.SaveSettingsAsync(edited);
        await runtime.AdvanceVotingPresentationAsync(); await runtime.SubmitVotingWinnerAsync(roundId: round.Id);
        Check((await new JsonPendingDispatchStore(paths).LoadAsync()).Count == 1, "Repeated done acknowledgement duplicated winning effect.");
        await runtime.ResetVotingAsync(round.CollectionId);
        await runtime.Voting.SetEnabledAsync(true, runtime.Settings.ActiveProfileId);
        await runtime.Voting.RecordAsync(new DonationEvent { Id = "presentation-second", Amount = 1000, Currency = "RUB", Message = "!b", CreatedAt = DateTimeOffset.UtcNow }, runtime.Settings.ActiveProfileId);
        var automation = runtime.Voting.Automation; automation.AutomaticWinnerEffect = false; await runtime.Voting.SaveAutomationAsync(automation);
        var manual = await runtime.BeginVotingDrawAsync(); replyState = "active"; await runtime.FinishVotingDrawAsync();
        Check(!runtime.Voting.Snapshot.GamePresentationAutomaticEffect && !runtime.Voting.Snapshot.DeliveryAccepted,
            "Manual presentation must retain its mode and wait for game Apply.");
        await runtime.ResetVotingAsync(manual.CollectionId); replyState = "done"; await runtime.AdvanceVotingPresentationAsync();
        Check(runtime.Voting.PendingWinners.Count == 0 && runtime.Voting.CancelledGamePresentations.Count == 0 &&
            (await new JsonPendingDispatchStore(paths).LoadAsync()).Count == 1,
            "Reset tombstone cancels native UI; a late done acknowledgement cannot send its winner.");
        preset.Parameters["dose"] = "99";
        await runtime.RetryManuallyAsync(winnerHistory);
        queue = await new JsonPendingDispatchStore(paths).LoadAsync();
        Check(queue.Count == 2 && queue[1].Donation.Id.StartsWith("manual:" + round.DeliveryId + ":", StringComparison.Ordinal) &&
            queue[1].Donation.Amount == 1000 && queue[1].Donation.Currency == "RUB" && queue[1].Action.Parameters["dose"] == "7" &&
            runtime.Voting.PendingWinners.Count == 0,
            "History repeat must enqueue exactly the frozen winning effect, without rerolling or repeating current donation rules.");
        await runtime.Voting.RecordAsync(new DonationEvent { Id = "hud-only-funds", Amount = 250, Currency = "RUB", Message = "!a", CreatedAt = DateTimeOffset.UtcNow }, runtime.Settings.ActiveProfileId);
        var hudFrames = new List<string>();
        runtime.VotingHudTransportForTests = (line, token) => { hudFrames.Add(line); return Task.CompletedTask; };
        automation = runtime.Voting.Automation; automation.ShowGameHud = true; automation.GameHudFormat = VotingHudFormat.Amount;
        automation.GameHudX = .1m; automation.GameHudY = .7m; await runtime.Voting.SaveAutomationAsync(automation);
        await runtime.AdvanceVotingHudAsync(); await runtime.AdvanceVotingHudAsync();
        Check(hudFrames.Count == 1 && PipeProtocol.PercentDecode(hudFrames[0].Split('\t')[2]) == "xy,0.1,0.7" && PipeProtocol.PercentDecode(hudFrames[0].Split('\t')[3]).Contains("250 руб."),
            "HUD transport must show current funds and coalesce unchanged frames without a second effect.");
        automation.GameHudFormat = VotingHudFormat.Percent;
        automation.ShowGameHudTemplateNames = true;
        await runtime.Voting.SaveAutomationAsync(automation); await runtime.AdvanceVotingHudAsync();
        await runtime.Voting.SaveHudPositionAsync(.4m,.2m); await runtime.AdvanceVotingHudAsync();
        var savedHudSettings = (await new JsonVotingStore(paths).LoadAsync()).Automation;
        Check(hudFrames.Count == 3 && PipeProtocol.PercentDecode(hudFrames[^1].Split('\t')[2]) == "xy,0.4,0.2" && savedHudSettings.GameHudX == .4m && savedHudSettings.GameHudY == .2m && savedHudSettings.GameHudFormat == VotingHudFormat.Percent &&
            savedHudSettings.ShowGameHud && savedHudSettings.ShowGameHudTemplateNames && runtime.Voting.Snapshot.Total == 250 && (await new JsonPendingDispatchStore(paths).LoadAsync()).Count == 2,
            "Global HUD preferences survive restart and never change funds or pending effects.");
        await runtime.Voting.SetCollectionActiveAsync(runtime.Voting.Snapshot.CollectionId, false, runtime.Settings.ActiveProfileId);
        await runtime.AdvanceVotingHudAsync();
        Check(hudFrames[^1].Split('\t')[2] == "hidden", "Turning off the active vote must clear the in-game progress.");
    }
    private static async Task PdaWinnerMessageAsync()
    {
        using var temporary = new Program.TemporaryDirectory();
        var paths = new AppPaths(temporary.Path);
        await using var runtime = new BridgeRuntime(paths);
        var preset = new ActionDefinition { HandlerId = DonationDispatcher.PdaMessageHandlerId,
            DisplayName = "Ничего не делать", IsActive = true };
        runtime.Settings.ActiveProfileId = "pda-test";
        typeof(BridgeRuntime).GetField("_presets", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(runtime, new List<ActionDefinition> { preset });
        await runtime.InitializeVotingModuleAsync();
        var draft = runtime.Voting.Snapshot;
        draft.Title = "Следующий эффект"; draft.TimeGoal = false; draft.Target = 1;
        draft.Options = [new VotingOption { Command = "!ничего", Preset = preset },
            new VotingOption { Command = "!другое", Preset = preset }];
        await runtime.Voting.SaveSettingsAsync(draft);
        await runtime.Voting.SetEnabledAsync(true, runtime.Settings.ActiveProfileId);
        Check(await runtime.Voting.RecordAsync(new DonationEvent { Id = "pda-vote", Amount = 1,
            Currency = "RUB", Message = "!ничего", CreatedAt = DateTimeOffset.UtcNow }, runtime.Settings.ActiveProfileId) == "counted",
            "PDA option must accept a normal voting contribution.");
        var round = await runtime.BeginVotingDrawAsync();
        await runtime.FinishVotingDrawAsync();
        runtime.VotingPresentationTransportForTests = (request, id, cancel, token) =>
            Task.FromResult(new VotingGameReply(id, "done", "isolated"));
        // Edits after the draw must not change the frozen announcement.
        draft = runtime.Voting.Snapshot; draft.Title = "Следующее голосование";
        draft.Options[0].Preset!.DisplayName = "Другое название";
        await runtime.Voting.SaveSettingsAsync(draft);
        await runtime.AdvanceVotingPresentationAsync();
        var queue = await new JsonPendingDispatchStore(paths).LoadAsync();
        Check(queue.Count == 1 && queue[0].Command.Parameters["donor_name"] == "Следующий эффект" &&
            queue[0].Command.Parameters["donation_message"] == "Ничего не делать" &&
            !queue[0].Command.ShowPdaEffectMessage,
            "Voting PDA announcement must use the frozen vote and preset titles, not the command or latest edits.");
        var history = (await new JsonLineHistoryStore(paths).LoadRecentAsync()).Single();
        await runtime.RetryManuallyAsync(history);
        queue = await new JsonPendingDispatchStore(paths).LoadAsync();
        Check(queue.Count == 2 && queue[1].Command.Parameters["donor_name"] == "Следующий эффект" &&
            queue[1].Command.Parameters["donation_message"] == "Ничего не делать",
            "Manual repeat must retain both original PDA message fields.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
