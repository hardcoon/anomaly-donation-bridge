using System.Text.Json;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.Tests;

internal static class NeedsQueueTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    public static async Task RunAsync()
    {
        foreach (var pause in new[] { 20, 60, 500 })
        {
            var now = DateTimeOffset.UtcNow; var policy = new QueuePolicy();
            var ledger = new InMemoryProcessedDonationStore(); var history = new InMemoryHistorySink();
            var store = new Store(); var pipe = new Transport();
            var dispatcher = new DonationDispatcher(ledger, history, () => now, pendingStore: store)
                { GlobalCooldown = TimeSpan.FromSeconds(pause) };
            var needs = new ActionDefinition { Id = "food", HandlerId = "change_needs", DisplayName = "Food",
                IsActive = true, CooldownSeconds = pause, Parameters = new() { ["hunger_percent"] = "-15", ["thirst_percent"] = "0", ["wait_if_insufficient"] = "true" } };
            await dispatcher.EnqueueActionAsync(new() { Id = "waiting-food", Amount = 15, Currency = "RUB" }, needs, policy);
            await dispatcher.DrainAsync(pipe, policy);
            var id = pipe.Commands.Single().CommandId;
            await dispatcher.CompleteAsync(new() { CommandId = id, Status = GameResultStatus.Deferred, Reason = "needs_waiting_food" });
            Check((await dispatcher.GetTimingAsync(true, true)).SecondsUntilNext == 5, "Waiting food paid global/template cooldown.");
            Check(history.Entries.Last().State == DispatchState.Queued && history.Entries.Last().Detail.Contains("5 секунд"), "Waiting state is not readable in history.");
            // Durable polling interval and exact command identity survive restart.
            dispatcher = new(ledger, history, () => now, pendingStore: store) { GlobalCooldown = TimeSpan.FromSeconds(pause) };
            await dispatcher.RestoreAsync();
            for (var index = 0; index < 8; index++)
            {
                now += TimeSpan.FromSeconds(4.9); await dispatcher.DrainAsync(pipe, policy);
                Check(pipe.Commands.Count == index + 1, "Needs poll ran more frequently than 5 seconds.");
                now += TimeSpan.FromSeconds(.1); await dispatcher.DrainAsync(pipe, policy);
                Check(pipe.Commands.Count == index + 2 && pipe.Commands.Last().CommandId == id, "Needs waiting lost original ID or paid unrelated delay.");
                await dispatcher.CompleteAsync(new() { CommandId = id, Status = GameResultStatus.Deferred, Reason = "needs_waiting_food" });
            }
            // Waiting does not occupy the pipe or reserve any gameplay pause.
            var other = needs.Clone(); other.Id = "other"; other.HandlerId = "change_money"; other.Parameters.Clear();
            await dispatcher.EnqueueActionAsync(new() { Id = "other-donation", Currency = "RUB" }, other, policy);
            await dispatcher.DrainAsync(pipe, policy);
            Check(pipe.Commands.Last().DonationId == "other-donation", "Read-only food waiting blocked unrelated queue events.");
            await dispatcher.CompleteAsync(new() { CommandId = pipe.Commands.Last().CommandId, Status = GameResultStatus.Executed });
            now += TimeSpan.FromSeconds(pause - .1); await dispatcher.DrainAsync(pipe, policy);
            Check(pipe.Commands.Last().DonationId == "other-donation", "Needs effect bypassed an intervening real effect's pause.");
            now += TimeSpan.FromSeconds(.1); await dispatcher.DrainAsync(pipe, policy);
            Check(pipe.Commands.Last().CommandId == id, "Needs did not resume after normal cooldown.");
            await dispatcher.CompleteAsync(new() { CommandId = id, Status = GameResultStatus.Executed });
            Check(!dispatcher.IsQueued("waiting-food", id), "Executed needs stayed queued.");
            await dispatcher.EnqueueActionAsync(new() { Id = "following", Currency = "RUB" }, other, policy);
            var count = pipe.Commands.Count;
            await dispatcher.DrainAsync(pipe, policy);
            Check(pipe.Commands.Count == count && (await dispatcher.GetTimingAsync(true, true)).SecondsUntilNext == pause,
                "Executed needs failed to start normal gameplay cooldown.");
            now += TimeSpan.FromSeconds(pause); await dispatcher.DrainAsync(pipe, policy);
            Check(pipe.Commands.Last().DonationId == "following" && pipe.Commands.Count(c => c.CommandId == id) == 10,
                "Waiting command was duplicated after success or next donation did not resume.");
        }

        foreach (var reason in new[] { "needs_waiting_water", "needs_waiting_food_and_water" })
        {
            var now = DateTimeOffset.UtcNow; var policy = new QueuePolicy(); var pipe = new Transport();
            var dispatcher = new DonationDispatcher(new InMemoryProcessedDonationStore(), new InMemoryHistorySink(), () => now)
                { GlobalCooldown = TimeSpan.FromSeconds(500) };
            await dispatcher.EnqueueActionAsync(new() { Id = reason, Currency = "RUB" },
                new() { Id = reason, HandlerId = "change_needs", DisplayName = reason }, policy);
            await dispatcher.DrainAsync(pipe, policy);
            var id = pipe.Commands.Single().CommandId;
            await dispatcher.CompleteAsync(new() { CommandId = id, Status = GameResultStatus.Deferred, Reason = reason });
            Check((await dispatcher.GetTimingAsync(true, true)).SecondsUntilNext == 5, "Water waiting lost polling interval.");
            await dispatcher.RequestImmediateAsync([id]); await dispatcher.DrainAsync(pipe, policy);
            Check(pipe.Commands.Count == 1, "Manual priority caused tight safety polling.");
            var cancelled = await dispatcher.CancelAllQueuedAsync();
            now += TimeSpan.FromSeconds(501); await dispatcher.DrainAsync(pipe, policy);
            Check(cancelled.AffectedCount == 1 && pipe.Commands.Count == 1, "Cancelled waiting needs came back.");
        }
        // Strict scenario order remains intact while other donations can run;
        // its own interval starts after actual execution, not failed probes.
        {
            var now = DateTimeOffset.UtcNow; var policy = new QueuePolicy(); var pipe = new Transport();
            var history = new InMemoryHistorySink();
            var dispatcher = new DonationDispatcher(new InMemoryProcessedDonationStore(), history, () => now)
                { GlobalCooldown = TimeSpan.FromSeconds(500) };
            var food = new ActionDefinition { Id = "food", HandlerId = "change_needs", DisplayName = "Food", StrictSequence = true,
                GroupDelaySeconds = 1, IsActive = true, Triggers = [new() { Amount = 15, Currency = "RUB" }] };
            var second = food.Clone(); second.Id = "second"; second.HandlerId = "change_money";
            await dispatcher.EnqueueAsync(new() { Id = "scenario", Amount = 15, Currency = "RUB" }, [food, second], policy);
            await dispatcher.DrainAsync(pipe, policy); var id = pipe.Commands.Single().CommandId;
            await dispatcher.CompleteAsync(new() { CommandId = id, Status = GameResultStatus.Deferred, Reason = "needs_waiting_food" });
            await dispatcher.DrainAsync(pipe, policy);
            Check(pipe.Commands.Count == 1, "Second strict step overtook waiting food.");
            var outside = second.Clone(); outside.Id = "outside";
            await dispatcher.EnqueueActionAsync(new() { Id = "outside", Currency = "RUB" }, outside, policy);
            await dispatcher.DrainAsync(pipe, policy);
            Check(pipe.Commands.Last().DonationId == "outside", "Waiting strict scenario blocked an independent donation.");
            await dispatcher.CompleteAsync(new() { CommandId = pipe.Commands.Last().CommandId, Status = GameResultStatus.Executed });
            now += TimeSpan.FromSeconds(500); await dispatcher.DrainAsync(pipe, policy);
            Check(pipe.Commands.Last().CommandId == id, "First scenario step lost original command ID.");
            await dispatcher.CompleteAsync(new() { CommandId = id, Status = GameResultStatus.Executed });
            var count = pipe.Commands.Count;
            now += TimeSpan.FromSeconds(.9); await dispatcher.DrainAsync(pipe, policy);
            Check(pipe.Commands.Count == count, "Actual needs execution lost configured group interval.");
            now += TimeSpan.FromSeconds(.1); await dispatcher.DrainAsync(pipe, policy);
            Check(pipe.Commands.Count == count + 1 && pipe.Commands.Last().ActionName == "Food" &&
                pipe.Commands.Last().ActionId == "change_money", "Next step did not use group interval instead of global.");
        }
        // An uncertain mutation is never converted into a retry.
        var uncertain = new DonationDispatcher(new InMemoryProcessedDonationStore(), new InMemoryHistorySink());
        var transport = new Transport(); var queuePolicy = new QueuePolicy();
        await uncertain.EnqueueActionAsync(new() { Id = "uncertain", Currency = "RUB" }, new() { Id = "needs", HandlerId = "change_needs", DisplayName = "Needs" }, queuePolicy);
        await uncertain.DrainAsync(transport, queuePolicy);
        await uncertain.CompleteAsync(new() { CommandId = transport.Commands.Single().CommandId, Status = GameResultStatus.Uncertain, Reason = "hunger_change_failed" });
        await uncertain.DrainAsync(transport, queuePolicy);
        Check(uncertain.Count == 0 && transport.Commands.Count == 1, "Uncertain needs auto-retried and could apply twice.");
    }

    private sealed class Transport : IGameCommandTransport
    {
        public bool IsConnected => true; public bool IsGameReady => true;
        public List<GameCommand> Commands { get; } = [];
        public Task SendAsync(GameCommand command, CancellationToken cancellationToken = default)
        { Commands.Add(command); return Task.CompletedTask; }
    }
    private sealed class Store : IPendingDispatchStore
    {
        private string _json = "[]";
        public Task<IReadOnlyList<PendingDispatchSnapshot>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PendingDispatchSnapshot>>(JsonSerializer.Deserialize<List<PendingDispatchSnapshot>>(_json)!);
        public Task SaveAsync(IReadOnlyList<PendingDispatchSnapshot> pending, CancellationToken cancellationToken = default)
        { _json = JsonSerializer.Serialize(pending); return Task.CompletedTask; }
    }
}
