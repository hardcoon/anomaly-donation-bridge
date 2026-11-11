using System.Text.Json;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.Tests;

internal static class GroupDelayTests
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static ActionDefinition Action(string id, decimal amount, int delay, bool strict = true) => new()
    {
        Id = id, HandlerId = "change_money", DisplayName = id, IsActive = true,
        StrictSequence = strict, GroupDelaySeconds = delay, CooldownSeconds = 0,
        Triggers = [new() { Amount = amount, Currency = "RUB" }]
    };

    public static async Task RunAsync()
    {
        Check(ActionPriceOrdering.ResolveGroupDelay([new ActionDefinition()]) == 1 &&
            ActionPriceOrdering.ResolveGroupDelay([JsonSerializer.Deserialize<ActionDefinition>("{}")!]) == 1,
            "Default or migrated group delay is not one second.");
        Check(ActionPriceOrdering.ResolveGroupDelay([new() { GroupDelaySeconds = 0 }, new()]) == 0,
            "Adding an unconfigured member reset an explicit zero group pause.");
        foreach (var strict in new[] { false, true })
        foreach (var delay in new[] { 0, 2, 20 })
        {
            var now = DateTimeOffset.UtcNow;
            var a = Action("a", 31, delay, strict); var b = Action("b", 31, delay, strict);
            var outside = Action("outside", 31, 0);
            outside.Triggers.Add(new() { Amount = 1, Currency = "USD" });
            var ledger = new InMemoryProcessedDonationStore(); var store = new Store(); var pipe = new Transport();
            var policy = new QueuePolicy();
            var dispatcher = new DonationDispatcher(ledger, new InMemoryHistorySink(), () => now, pendingStore: store)
                { GlobalCooldown = TimeSpan.FromSeconds(10) };
            async Task Ack() => await dispatcher.CompleteAsync(new()
                { CommandId = pipe.Commands.Last().CommandId, Status = GameResultStatus.Executed });
            await dispatcher.EnqueueActionAsync(new() { Id = "warmup", Amount = 99, Currency = "RUB" }, outside, policy);
            await dispatcher.DrainAsync(pipe, policy); await Ack();
            await dispatcher.EnqueueAsync(new() { Id = "group", Amount = 31, Currency = "RUB" }, [a, outside, b], policy);
            await dispatcher.DrainAsync(pipe, policy);
            Check(pipe.Commands.Count == 1, "First group effect bypassed the preceding donation's global pause.");
            Check((await dispatcher.GetTimingAsync(true, true)).SecondsUntilNext == 10, "First group effect has the wrong timer.");
            now += TimeSpan.FromSeconds(10);
            await dispatcher.DrainAsync(pipe, policy);
            Check(pipe.Commands.Last().ActionName == "a", "First group member did not start.");
            await Ack();
            Check((await dispatcher.GetTimingAsync(true, true)).SecondsUntilNext == Math.Min(10, delay), "Timer did not consider the group pause independently.");
            if (delay > 0)
            {
                now += TimeSpan.FromSeconds(Math.Min(delay, 10) - 0.1);
                await dispatcher.DrainAsync(pipe, policy);
                Check(pipe.Commands.Count == 2, "An effect started before its pause expired.");
                now += TimeSpan.FromSeconds(0.1);
            }
            if (delay > 10)
            {
                await dispatcher.DrainAsync(pipe, policy);
                Check(pipe.Commands.Last().ActionName == "outside", "A long group pause blocked an unrelated effect.");
                await Ack();
                now += TimeSpan.FromSeconds(delay - 10);
            }
            // Restore from a JSON round-trip: the remaining group clock must survive.
            dispatcher = new DonationDispatcher(ledger, new InMemoryHistorySink(), () => now, pendingStore: store)
                { GlobalCooldown = TimeSpan.FromSeconds(10) };
            await dispatcher.RestoreAsync();
            if (delay < 10)
            {
                // Restore also resets the old global clock. An unrelated sent action
                // must not overwrite the per-donation group clock.
                await dispatcher.DrainAsync(pipe, policy);
                Check(pipe.Commands.Last().ActionName == "outside", "Unrelated queue entry was lost on restore.");
                await Ack();
            }
            await dispatcher.DrainAsync(pipe, policy);
            Check(pipe.Commands.Last().ActionName == "b", "Group continuation did not replace the global pause.");
            await Ack();
            Check(pipe.Commands.Count == 4, "Group execution duplicated or lost a command.");
            await dispatcher.EnqueueAsync(new() { Id = "next-donation", Amount = 31, Currency = "RUB" }, [a, b], policy);
            await dispatcher.DrainAsync(pipe, policy);
            Check(pipe.Commands.Count == 4, "A new donation incorrectly inherited the previous group's bypass.");
            Check(a.Clone().GroupDelaySeconds == delay, "Cloning lost the group pause.");
        }

        // A reload partway through the group delay must retain the remaining seconds.
        var clock = DateTimeOffset.UtcNow;
        var pendingStore = new Store(); var processed = new InMemoryProcessedDonationStore(); var transport = new Transport();
        var queuePolicy = new QueuePolicy();
        var current = new DonationDispatcher(processed, new InMemoryHistorySink(), () => clock, pendingStore: pendingStore)
            { GlobalCooldown = TimeSpan.FromSeconds(60) };
        await current.EnqueueAsync(new() { Id = "restart", Amount = 31, Currency = "RUB" },
            [Action("first", 31, 5), Action("second", 31, 5)], queuePolicy);
        await current.DrainAsync(transport, queuePolicy);
        await current.CompleteAsync(new() { CommandId = transport.Commands.Last().CommandId, Status = GameResultStatus.Executed });
        clock += TimeSpan.FromSeconds(2);
        current = new(processed, new InMemoryHistorySink(), () => clock, pendingStore: pendingStore)
            { GlobalCooldown = TimeSpan.FromSeconds(60) };
        await current.RestoreAsync();
        Check((await current.GetTimingAsync(true, true)).SecondsUntilNext == 3, "Reload reset the group clock.");
        await current.DrainAsync(transport, queuePolicy);
        Check(transport.Commands.Count == 1, "Reload skipped the remaining group pause.");
        clock += TimeSpan.FromSeconds(3);
        await current.DrainAsync(transport, queuePolicy);
        Check(transport.Commands.Last().ActionName == "second", "Reload lost the group continuation.");

        // The pre-send race and a Deferred response must retain both group timing
        // and strict order, without allowing the second member to overtake.
        clock = DateTimeOffset.UtcNow;
        transport = new() { NotReadyOnce = true };
        current = new(new InMemoryProcessedDonationStore(), new InMemoryHistorySink(), () => clock)
            { GlobalCooldown = TimeSpan.FromSeconds(60) };
        await current.EnqueueAsync(new() { Id = "retry", Amount = 31, Currency = "RUB" },
            [Action("first", 31, 3), Action("second", 31, 3)], queuePolicy);
        await current.DrainAsync(transport, queuePolicy);
        Check(transport.Commands.Count == 0, "A failed pre-send readiness check sent a command.");
        await current.DrainAsync(transport, queuePolicy);
        Check(transport.Commands.Last().ActionName == "first", "Readiness race lost strict order.");
        await current.CompleteAsync(new() { CommandId = transport.Commands.Last().CommandId, Status = GameResultStatus.Deferred });
        clock += TimeSpan.FromSeconds(2); await current.DrainAsync(transport, queuePolicy);
        Check(transport.Commands.Count == 1, "Deferred retry discarded the group pause.");
        clock += TimeSpan.FromSeconds(1); await current.DrainAsync(transport, queuePolicy);
        Check(transport.Commands.Count == 2 && transport.Commands.Last().ActionName == "first", "Deferred retry used the global pause or lost order.");
        await current.CompleteAsync(new() { CommandId = transport.Commands.Last().CommandId, Status = GameResultStatus.Executed });
        clock += TimeSpan.FromSeconds(3); await current.DrainAsync(transport, queuePolicy);
        Check(transport.Commands.Last().ActionName == "second", "Group did not resume after the deferred member executed.");
    }

    private sealed class Transport : IGameCommandTransport
    {
        public bool IsConnected => true; public bool IsGameReady => true;
        public bool NotReadyOnce { get; set; }
        public List<GameCommand> Commands { get; } = [];
        public Task SendAsync(GameCommand command, CancellationToken cancellationToken = default)
        {
            if (NotReadyOnce) { NotReadyOnce = false; throw new GameNotReadyBeforeSendException("test readiness race"); }
            Commands.Add(command); return Task.CompletedTask;
        }
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
