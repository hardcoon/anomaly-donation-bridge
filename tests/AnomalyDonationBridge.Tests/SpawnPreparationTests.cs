using System.Text.Json;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.Tests;

internal static class SpawnPreparationTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    private static ActionDefinition Spawn(string handler, string id = "spawn") => new()
    {
        Id = id, HandlerId = handler, DisplayName = id, IsActive = true,
        CooldownSeconds = 60, StrictSequence = true, GroupDelaySeconds = 60,
        SpawnGroups = [new() { VariantId = "test_spawn_section", Count = 1 }],
        Triggers = [new() { Amount = 100, Currency = "RUB" }]
    };

    public static async Task RunAsync()
    {
        var policy = new QueuePolicy();
        foreach (var handler in new[] { "spawn_mutants", "spawn_npcs", "spawn_random_mutants", "spawn_random_npcs", "hardcoon.adb.wandering_trader" })
        foreach (var reason in handler == "hardcoon.adb.wandering_trader"
            ? new[] { "trader_catalog_preparing", "trader_waiting_spawn_point", "trader_preparing" }
            : new[] { "spawn_point_search_running", "spawn_point_search_busy", "spawn_point_actor_moved" })
        {
            var now = DateTimeOffset.UtcNow;
            var pipe = new Transport(); var history = new InMemoryHistorySink();
            var dispatcher = new DonationDispatcher(new InMemoryProcessedDonationStore(), history, () => now)
                { GlobalCooldown = TimeSpan.FromSeconds(60) };
            var action = Spawn(handler);
            await dispatcher.EnqueueActionAsync(new() { Id = "first", Amount = 100, Currency = "RUB" }, action, policy);
            await dispatcher.EnqueueActionAsync(new() { Id = "second", Amount = 100, Currency = "RUB" }, action, policy);
            await dispatcher.DrainAsync(pipe, policy);
            Check(pipe.Commands.Count == 1, "First spawn was not sent.");
            var originalId = pipe.Commands[0].CommandId;
            await dispatcher.CompleteAsync(new() { CommandId = originalId, Status = GameResultStatus.Deferred, Reason = reason });
            Check((await dispatcher.GetTimingAsync(true, true)).SecondsUntilNext == 1,
                "Read-only point search paid the 60-second cooldown again.");
            await dispatcher.DrainAsync(pipe, policy);
            Check(pipe.Commands.Count == 1, "Search retries are spinning without a polling interval.");
            now += TimeSpan.FromMilliseconds(249);
            await dispatcher.DrainAsync(pipe, policy);
            Check(pipe.Commands.Count == 1, "Search was polled before 250ms.");
            now += TimeSpan.FromMilliseconds(1);
            await dispatcher.DrainAsync(pipe, policy);
            Check(pipe.Commands.Count == 2 && pipe.Commands.Last().CommandId == originalId,
                "Search did not resume the same command after 250ms.");
            await dispatcher.CompleteAsync(new() { CommandId = originalId, Status = GameResultStatus.Executed });
            now += TimeSpan.FromSeconds(59.9);
            await dispatcher.DrainAsync(pipe, policy);
            Check(pipe.Commands.Count == 2, "A new donation bypassed the normal gameplay pause.");
            now += TimeSpan.FromSeconds(.1);
            await dispatcher.DrainAsync(pipe, policy);
            Check(pipe.Commands.Count == 3 && pipe.Commands.Last().CommandId != originalId,
                "A new donation did not retain its full gameplay pause.");
        }

        // Strict group order and its custom pause still apply to real steps;
        // only the unfinished preparation of the same first step is polled.
        var clock = DateTimeOffset.UtcNow; var transport = new Transport();
        var store = new Store(); var ledger = new InMemoryProcessedDonationStore();
        var current = new DonationDispatcher(ledger, new InMemoryHistorySink(), () => clock, pendingStore: store)
            { GlobalCooldown = TimeSpan.FromSeconds(60) };
        var first = Spawn("spawn_mutants", "a"); var next = Spawn("change_money", "b");
        await current.EnqueueAsync(new() { Id = "group", Amount = 100, Currency = "RUB" }, [first, next], policy);
        await current.DrainAsync(transport, policy);
        var firstId = transport.Commands.Last().CommandId;
        await current.CompleteAsync(new() { CommandId = firstId, Status = GameResultStatus.Deferred, Reason = "spawn_point_search_running" });
        current = new(ledger, new InMemoryHistorySink(), () => clock, pendingStore: store)
            { GlobalCooldown = TimeSpan.FromSeconds(60) };
        await current.RestoreAsync();
        Check((await current.GetTimingAsync(true, true)).SecondsUntilNext == 1, "Restore lost navigation polling state.");
        clock += TimeSpan.FromMilliseconds(250);
        await current.DrainAsync(transport, policy);
        Check(transport.Commands.Count == 2 && transport.Commands.Last().CommandId == firstId,
            "Group retry paid its 60-second pause or let the second step overtake.");
        await current.CompleteAsync(new() { CommandId = firstId, Status = GameResultStatus.Executed });
        clock += TimeSpan.FromSeconds(59.9); await current.DrainAsync(transport, policy);
        Check(transport.Commands.Count == 2, "The real next group step bypassed the group pause.");
        clock += TimeSpan.FromSeconds(.1); await current.DrainAsync(transport, policy);
        Check(transport.Commands.Last().ActionName == "b", "The real next group step did not resume.");

        foreach (var (handler, reason) in new[] {
            ("spawn_mutants", "actor_unavailable"),
            ("spawn_mutants", "safe_zone_waiting:l01_escape"),
            ("change_money", "spawn_point_search_running") })
        {
            clock = DateTimeOffset.UtcNow; transport = new();
            current = new(new InMemoryProcessedDonationStore(), new InMemoryHistorySink(), () => clock)
                { GlobalCooldown = TimeSpan.FromSeconds(60) };
            await current.EnqueueActionAsync(new() { Id = "ordinary", Amount = 100, Currency = "RUB" }, Spawn(handler), policy);
            await current.DrainAsync(transport, policy);
            await current.CompleteAsync(new() { CommandId = transport.Commands[0].CommandId, Status = GameResultStatus.Deferred, Reason = reason });
            clock += TimeSpan.FromMilliseconds(250); await current.DrainAsync(transport, policy);
            Check(transport.Commands.Count == 1, "An unrelated deferred response gained the navigation bypass.");
        }

        // A long-running preparation must not keep extending the global clock
        // and starve other donations. A real intervening effect must, however,
        // retain its normal pause before the prepared spawn is finally sent.
        clock = DateTimeOffset.UtcNow; transport = new();
        current = new(new InMemoryProcessedDonationStore(), new InMemoryHistorySink(), () => clock)
            { GlobalCooldown = TimeSpan.FromSeconds(60) };
        await current.EnqueueActionAsync(new() { Id = "search", Amount = 100, Currency = "RUB" }, Spawn("spawn_mutants"), policy);
        await current.EnqueueActionAsync(new() { Id = "outside", Amount = 100, Currency = "RUB" }, Spawn("change_money", "outside"), policy);
        await current.DrainAsync(transport, policy);
        var searchingId = transport.Commands[0].CommandId;
        await current.CompleteAsync(new() { CommandId = searchingId, Status = GameResultStatus.Deferred, Reason = "spawn_point_search_running" });
        for (var poll = 1; poll <= 240; poll++)
        {
            clock += TimeSpan.FromMilliseconds(250);
            await current.DrainAsync(transport, policy);
            var command = transport.Commands.Last();
            if (command.ActionName == "outside") break;
            Check(command.CommandId == searchingId, "Unexpected command during navigation polling.");
            await current.CompleteAsync(new() { CommandId = command.CommandId, Status = GameResultStatus.Deferred, Reason = "spawn_point_search_running" });
        }
        Check(transport.Commands.Last().ActionName == "outside", "Repeated navigation polling indefinitely blocked another donation.");
        await current.CompleteAsync(new() { CommandId = transport.Commands.Last().CommandId, Status = GameResultStatus.Executed });
        var count = transport.Commands.Count;
        clock += TimeSpan.FromMilliseconds(250); await current.DrainAsync(transport, policy);
        Check(transport.Commands.Count == count, "Prepared spawn bypassed an intervening effect's global pause.");
        clock += TimeSpan.FromSeconds(59.75); await current.DrainAsync(transport, policy);
        Check(transport.Commands.Count == count + 1 && transport.Commands.Last().CommandId == searchingId,
            "Prepared spawn did not resume after the intervening effect's pause.");
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
