using System.Text.Json;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.Tests;

internal static class SequenceTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static ActionDefinition Action(string id, decimal amount, bool strict = true) => new()
    { Id = id, HandlerId = "change_money", DisplayName = id, IsActive = true, StrictSequence = strict,
        CooldownSeconds = 0, GroupDelaySeconds = 0, Triggers = [new() { Amount = amount, Currency = "RUB" }] };
    private static DonationEvent Donation(string id, decimal amount) => new() { Id = id, Amount = amount, Currency = "RUB" };
    public static async Task RunAsync()
    {
        var a = Action("a", 31); var b = Action("b", 31); var c = Action("c", 31); var other = Action("other", 99);
        var source = new[] { a, other, b, c };
        var moved = ActionPriceOrdering.MoveWithinGroup(source, ActionPriceOrdering.GroupKey(a)!, "c", "a", false);
        Check(moved.Select(action => action.Id).SequenceEqual(["c", "other", "a", "b"]), "Move changed unrelated slots or lost a step.");
        Check(source[0] == a && a.Clone().StrictSequence, "Move mutated input or clone lost strict mode.");
        var ledger = new InMemoryProcessedDonationStore(); var store = new Store();
        var dispatcher = new DonationDispatcher(ledger, new InMemoryHistorySink(), pendingStore: store) { GlobalCooldown = TimeSpan.Zero };
        var pipe = new Transport(); var policy = new QueuePolicy();
        await dispatcher.EnqueueAsync(Donation("chain", 31), [a, b], policy);
        await dispatcher.DrainAsync(pipe, policy); var first = pipe.Commands.Last();
        Check(first.ActionName == "a", "First step order is wrong.");
        await dispatcher.EnqueueAsync(Donation("unrelated", 99), [other], policy);
        await dispatcher.CompleteAsync(new() { CommandId = first.CommandId, Status = GameResultStatus.Deferred });
        await dispatcher.DrainAsync(pipe, policy);
        Check(pipe.Commands.Last().ActionName == "other", "Strict chain blocked unrelated effects or allowed its later step.");
        await dispatcher.CompleteAsync(new() { CommandId = pipe.Commands.Last().CommandId, Status = GameResultStatus.Executed });
        Check(store.Snapshots.Count == 2 && store.Snapshots.Select(item => item.SequenceId).Distinct().Count() == 1 &&
            store.Snapshots.All(item => item.SequenceId.Length > 0), "Deferred snapshot lost sequence identity.");
        var restored = new DonationDispatcher(ledger, new InMemoryHistorySink(), pendingStore: store) { GlobalCooldown = TimeSpan.Zero };
        Check(await restored.RestoreAsync() == 2, "Strict queue was lost on restart.");
        await restored.DrainAsync(pipe, policy);
        Check(pipe.Commands.Last().CommandId == first.CommandId, "Restart allowed the second step to overtake a deferred first step.");
        await restored.CompleteAsync(new() { CommandId = first.CommandId, Status = GameResultStatus.Executed });
        await restored.DrainAsync(pipe, policy);
        Check(pipe.Commands.Last().ActionName == "b", "Second step did not unlock after first result.");
        await restored.CompleteAsync(new() { CommandId = pipe.Commands.Last().CommandId, Status = GameResultStatus.Executed });
        var count = pipe.Commands.Count; await restored.DrainAsync(pipe, policy);
        Check(pipe.Commands.Count == count, "Completed sequence repeated.");

        a.StrictSequence = b.StrictSequence = false;
        dispatcher = new(new InMemoryProcessedDonationStore(), new InMemoryHistorySink()) { GlobalCooldown = TimeSpan.Zero };
        pipe = new(); await dispatcher.EnqueueAsync(Donation("ordinary", 31), [a, b], policy);
        await dispatcher.DrainAsync(pipe, policy);
        await dispatcher.CompleteAsync(new() { CommandId = pipe.Commands.Last().CommandId, Status = GameResultStatus.Deferred });
        await dispatcher.DrainAsync(pipe, policy);
        Check(pipe.Commands.Last().ActionName == "b", "Default non-strict queue no longer rotates deferred effects.");

        var time = DateTimeOffset.UtcNow; a.StrictSequence = b.StrictSequence = true; a.CooldownSeconds = 100;
        dispatcher = new(new InMemoryProcessedDonationStore(), new InMemoryHistorySink(), () => time) { GlobalCooldown = TimeSpan.Zero };
        pipe = new(); await dispatcher.EnqueueActionAsync(Donation("warmup", 1), a, policy);
        await dispatcher.DrainAsync(pipe, policy);
        await dispatcher.CompleteAsync(new() { CommandId = pipe.Commands.Last().CommandId, Status = GameResultStatus.Executed });
        await dispatcher.EnqueueAsync(Donation("cooling", 31), [a, b], policy);
        await dispatcher.DrainAsync(pipe, policy);
        Check(pipe.Commands.Count == 1, "Action cooldown allowed a later sequence step to overtake.");
        time += TimeSpan.FromSeconds(100); await dispatcher.DrainAsync(pipe, policy);
        Check(pipe.Commands.Last().ActionName == "a", "First step did not resume after cooldown.");
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
        public IReadOnlyList<PendingDispatchSnapshot> Snapshots => JsonSerializer.Deserialize<List<PendingDispatchSnapshot>>(_json)!;
        public Task<IReadOnlyList<PendingDispatchSnapshot>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshots);
        public Task SaveAsync(IReadOnlyList<PendingDispatchSnapshot> pending, CancellationToken cancellationToken = default)
        { _json = JsonSerializer.Serialize(pending); return Task.CompletedTask; }
    }
}
