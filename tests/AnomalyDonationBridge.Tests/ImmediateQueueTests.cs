using System.Text.Json;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.Tests;

internal static class ImmediateQueueTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    public static async Task RunAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var ledger = new InMemoryProcessedDonationStore(); var sink = new InMemoryHistorySink();
        var store = new Store(); var policy = new QueuePolicy(); var pipe = new Transport();
        var dispatcher = new DonationDispatcher(ledger, sink, () => now, pendingStore: store)
            { GlobalCooldown = TimeSpan.FromSeconds(500) };
        var action = new ActionDefinition { Id = "same-action", HandlerId = "change_money", DisplayName = "Fixture",
            CooldownSeconds = 500, IsActive = true, Triggers = [new() { Amount = 10, Currency = "RUB" }] };
        async Task Enqueue(string id) => await dispatcher.EnqueueActionAsync(
            new() { Id = id, Amount = 10, Currency = "RUB" }, action, policy);
        await Enqueue("finished"); await dispatcher.DrainAsync(pipe, policy);
        var finishedId = pipe.Commands.Single().CommandId;
        await dispatcher.CompleteAsync(new() { CommandId = finishedId, Status = GameResultStatus.Executed });
        await Enqueue("unselected"); await Enqueue("selected-b"); await Enqueue("selected-c");
        var before = await store.LoadAsync();
        var b = before.Single(p => p.Donation.Id == "selected-b").Command.CommandId;
        var c = before.Single(p => p.Donation.Id == "selected-c").Command.CommandId;
        Check(await dispatcher.RequestImmediateAsync([c, b, b, finishedId, "missing"]) == 2,
            "Immediate request accepted a completed/unknown command or duplicated selection.");
        Check((await dispatcher.GetTimingAsync(true, true)).SecondsUntilNext == 0, "Instant request still pays cooldown.");
        // Durable queue priority survives an app restart, without changing IDs.
        dispatcher = new(ledger, sink, () => now, pendingStore: store) { GlobalCooldown = TimeSpan.FromSeconds(500) };
        await dispatcher.RestoreAsync();
        pipe.Ready = false; await dispatcher.DrainAsync(pipe, policy);
        pipe.Ready = true; pipe.Connected = false; await dispatcher.DrainAsync(pipe, policy);
        pipe.Connected = true; pipe.Validation = CatalogCommandValidationResult.Awaiting("catalog_pending");
        await dispatcher.DrainAsync(pipe, policy);
        Check(pipe.Commands.Count == 1 && dispatcher.Count == 3, "Instant bypassed disconnected/readiness/catalog checks.");
        pipe.Validation = CatalogCommandValidationResult.Valid;
        await dispatcher.DrainAsync(pipe, policy);
        Check(pipe.Commands.Last().CommandId == b, "Selection was replayed or selected queue order changed.");
        Check(await dispatcher.RequestImmediateAsync([b]) == 0, "Already sent command was accepted again.");
        await dispatcher.DrainAsync(pipe, policy);
        Check(pipe.Commands.Count == 2, "Second effect was sent before first acknowledgement.");
        await dispatcher.CompleteAsync(new() { CommandId = b, Status = GameResultStatus.Executed });
        await dispatcher.DrainAsync(pipe, policy);
        Check(pipe.Commands.Count == 3 && pipe.Commands.Last().CommandId == c, "Second selected item paid global/action pause.");
        await dispatcher.CompleteAsync(new() { CommandId = c, Status = GameResultStatus.Executed });
        await dispatcher.DrainAsync(pipe, policy);
        Check(pipe.Commands.Count == 3 && dispatcher.Count == 1, "Unselected item received instant execution.");

        // A single selected later scenario step is an explicit manual override;
        // unselected siblings and their configured group pause remain intact.
        var one = action.Clone(); one.Id = "one"; one.StrictSequence = true; one.GroupDelaySeconds = 500;
        var two = one.Clone(); two.Id = "two";
        var groupSink = new InMemoryHistorySink();
        var group = new DonationDispatcher(new InMemoryProcessedDonationStore(), groupSink, () => now);
        await group.EnqueueAsync(new() { Id = "group", Amount = 10, Currency = "RUB" }, [one, two], policy);
        var secondId = groupSink.Entries.Last().CommandId;
        await group.RequestImmediateAsync([secondId]);
        pipe = new(); await group.DrainAsync(pipe, policy);
        Check(pipe.Commands.Single().CommandId == secondId && group.Count == 1, "Unselected scenario step was forced first.");
        await group.CompleteAsync(new() { CommandId = secondId, Status = GameResultStatus.Deferred, Reason = "safe_zone_waiting:l01_escape" });
        var groupTiming = await group.GetTimingAsync(true, true);
        Check(groupTiming.SecondsUntilNext == 500, "Native safety deferral kept a permanent cooldown bypass.");

        // The read-only spawn search keeps the same instant attempt, with its
        // finite polling interval instead of repeatedly paying a gameplay pause.
        var spawn = one.Clone(); spawn.Id = "spawn"; spawn.HandlerId = "spawn_mutants";
        spawn.SpawnGroups = [new() { VariantId = "test_mutant", Count = 1 }];
        var search = new DonationDispatcher(new InMemoryProcessedDonationStore(), new InMemoryHistorySink(), () => now,
            pendingStore: store) { GlobalCooldown = TimeSpan.FromSeconds(500) };
        await search.EnqueueActionAsync(new() { Id = "search", Amount = 10, Currency = "RUB" }, spawn, policy);
        var searchId = (await store.LoadAsync()).Single().Command.CommandId;
        await search.RequestImmediateAsync([searchId]); pipe = new(); await search.DrainAsync(pipe, policy);
        await search.CompleteAsync(new() { CommandId = searchId, Status = GameResultStatus.Deferred, Reason = "spawn_point_search_running" });
        await search.DrainAsync(pipe, policy);
        Check(pipe.Commands.Count == 1, "Instant search spins without polling interval.");
        now += TimeSpan.FromMilliseconds(250); await search.DrainAsync(pipe, policy);
        Check(pipe.Commands.Count == 2 && pipe.Commands.Last().CommandId == searchId, "Instant navigation preparation lost original attempt.");
        await search.CompleteAsync(new() { CommandId = searchId, Status = GameResultStatus.Executed });
        Check(await search.RequestImmediateAsync([searchId]) == 0, "Completed spawn was duplicated.");

        // Permanent preflight rejection remains terminal, never forced to pipe.
        var rejected = new DonationDispatcher(new InMemoryProcessedDonationStore(), new InMemoryHistorySink(), pendingStore: store);
        await rejected.EnqueueActionAsync(new() { Id = "reject", Amount = 10, Currency = "RUB" }, action, policy);
        await rejected.RequestImmediateAsync([(await store.LoadAsync()).Single().Command.CommandId]);
        pipe = new() { Validation = new(false, "invalid_section") };
        var result = await rejected.DrainAsync(pipe, policy);
        Check(result.Single().State == DispatchState.Rejected && pipe.Commands.Count == 0, "Instant bypassed terminal validation.");
    }

    private sealed class Transport : IGameCommandTransport, IGameCommandPreflight
    {
        public bool Connected = true, Ready = true;
        public bool IsConnected => Connected; public bool IsGameReady => Ready;
        public CatalogCommandValidationResult Validation = CatalogCommandValidationResult.Valid;
        public List<GameCommand> Commands { get; } = [];
        public CatalogCommandValidationResult Prepare(GameCommand command) => Validation;
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
