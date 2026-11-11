using System.IO;
using System.Reflection;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.AppTests;

internal static class HistorySelectionTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    internal static async Task RunAsync()
    {
        using var temporary = new Program.TemporaryDirectory();
        var paths = new AppPaths(temporary.Path);
        await using var runtime = new BridgeRuntime(paths); // Never InitializeAsync: isolated queue, no IPC or providers.
        var game = Path.Combine(temporary.Path, "game"); Directory.CreateDirectory(game);
        runtime.Settings.GamePath = game;
        await runtime.InstallEffectsAsync(game, "");
        runtime.Settings.Armed = false; runtime.Settings.GlobalCooldownSeconds = 500;
        var model = new MainViewModel(runtime);
        var action = runtime.Presets.Single(p => p.HandlerId == "change_player_name").Clone();
        action.Id = "history-queue-fixture"; action.IsActive = true;
        action.Triggers = [new() { Amount = 20, Currency = "RUB" }];
        foreach (var donor in new[] { "A", "B", "C" }) await runtime.RunAsDonorAsync(action, donor, "original " + donor);
        var history = await new JsonLineHistoryStore(paths).LoadRecentAsync(100);
        Check(history.Count == 3 && history.All(p => p.State == DispatchState.Queued), "Offline queue fixture was not queued.");
        var b = history.Single(p => p.Donor == "B"); var c = history.Single(p => p.Donor == "C");
        var completed = new EventHistoryEntry { CommandId = "already-done", DonationId = "done", State = DispatchState.Executed,
            ActionId = action.Id, ActionSnapshot = action.Clone(), ActionName = action.DisplayName };
        model.History.Add(completed);
        model.SetHistorySelection([b, c, completed]);
        Check(model.ApplyQueuedImmediatelyCommand.CanExecute(null) && model.RemoveSelectedQueuedCommand.CanExecute(null),
            "Multiple waiting selection disabled instant/remove actions.");
        Check(await runtime.ApplyQueuedImmediatelyAsync(model.SelectedHistoryEntries) == 2, "Selected waiting request included completed/unselected rows.");
        var pending = await new JsonPendingDispatchStore(paths).LoadAsync();
        Check(pending.Count == 3 && pending.Count(p => p.ImmediateRequested) == 2 &&
            pending.Where(p => p.ImmediateRequested).All(p => p.Command.CommandId == b.CommandId || p.Command.CommandId == c.CommandId),
            "Instant request replayed IDs, marked an unselected row, or dropped an offline event.");
        Check(pending.All(p => !p.InFlight) && !runtime.Settings.Armed && runtime.Settings.GlobalCooldownSeconds == 500,
            "Instant action enabled reception, rewrote settings, or sent while disconnected.");
        Check(await runtime.ApplyQueuedImmediatelyAsync([completed]) == 0, "Completed row became an immediate replay.");

        // Replace a status row while a range is selected, as real ACKs do.
        model.History.Clear(); foreach (var entry in history) model.History.Add(entry); model.History.Add(completed);
        model.SetHistorySelection([b, c]);
        var updated = new EventHistoryEntry { CommandId = b.CommandId, DonationId = b.DonationId, State = DispatchState.Executed,
            ActionName = b.ActionName, ActionSnapshot = action.Clone() };
        typeof(MainViewModel).GetMethod("AddHistory", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, [updated]);
        Check(model.SelectedHistoryEntries.Count == 2 && model.SelectedHistoryEntries.Contains(updated) && model.SelectedHistoryEntries.Contains(c),
            "A status update lost the selected range or kept a stale entry.");
        model.SetHistorySelection([completed]);
        Check(!model.ApplyQueuedImmediatelyCommand.CanExecute(null) && !model.RemoveSelectedQueuedCommand.CanExecute(null) && model.RetryCommand.CanExecute(null),
            "Completed-only selection has incorrect command enablement.");
        model.SetHistorySelection([b, c]);
        Check(!model.RetryCommand.CanExecute(null), "Repeat can duplicate a waiting selection instead of applying its originals.");
        await runtime.RemoveFromQueueAsync(b); await runtime.RemoveFromQueueAsync(c);
        Check(!model.ApplyQueuedImmediatelyCommand.CanExecute(null), "Stale queued history enabled an already cancelled immediate command.");
        pending = await new JsonPendingDispatchStore(paths).LoadAsync();
        Check(pending.Count == 1 && pending.Single().Donation.Username == "A" && !pending.Single().ImmediateRequested,
            "Selected queue removal affected unselected donor A.");
    }
}
