using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.Tests;

internal static class BtrQueueTests
{
    public static async Task RunAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var policy = new QueuePolicy();
        var pipe = new Transport();
        var dispatcher = new DonationDispatcher(new InMemoryProcessedDonationStore(),
            new InMemoryHistorySink(), () => now) { GlobalCooldown = TimeSpan.FromSeconds(60) };
        await dispatcher.EnqueueActionAsync(new() { Id = "blocked", Currency = "RUB" },
            new() { Id = "money", HandlerId = "change_money", DisplayName = "Money" }, policy);
        await dispatcher.EnqueueActionAsync(new() { Id = "allowed", Currency = "RUB" },
            new() { Id = "squads", HandlerId = "hardcoon.adb.show_squads", DisplayName = "Squads" }, policy);

        // The player can enter after the last ready status arrived.
        await dispatcher.DrainAsync(pipe, policy);
        var id = pipe.Commands.Single().CommandId;
        await dispatcher.CompleteAsync(new() { CommandId = id, Status = GameResultStatus.Deferred,
            Reason = BtrPassengerCommands.WaitingReason });
        pipe.Passenger = true;
        await dispatcher.RequestImmediateAsync([id]);
        await dispatcher.DrainAsync(pipe, policy);
        Check(pipe.Commands.Count == 2 && pipe.Commands.Last().DonationId == "allowed",
            "Passenger deferral consumed the global cooldown or blocked the permitted command.");
        await dispatcher.CompleteAsync(new() { CommandId = pipe.Commands.Last().CommandId,
            Status = GameResultStatus.Executed });

        now += TimeSpan.FromSeconds(61);
        await dispatcher.DrainAsync(pipe, policy);
        Check(pipe.Commands.Count == 2, "Immediate mode bypassed the passenger restriction.");
        pipe.Passenger = false;
        await dispatcher.DrainAsync(pipe, policy);
        Check(pipe.Commands.Count == 3 && pipe.Commands.Last().CommandId == id,
            "The original deferred command did not resume after exiting the BTR.");

        var extension = new GameCommand { ActionId = BtrPassengerCommands.Handler };
        Check(BtrPassengerCommands.IsAllowed(extension), "Default extension was blocked.");
        extension.Parameters["extend_current"] = "false";
        Check(!BtrPassengerCommands.IsAllowed(extension), "New vehicle was allowed while riding.");
    }

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    private sealed class Transport : IGameCommandTransport
    {
        public bool Passenger { get; set; }
        public bool IsConnected => true;
        public bool IsGameReady => true;
        public List<GameCommand> Commands { get; } = [];
        public bool CanAttempt(GameCommand command) => !Passenger || BtrPassengerCommands.IsAllowed(command);

        public Task SendAsync(GameCommand command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return Task.CompletedTask;
        }
    }
}
