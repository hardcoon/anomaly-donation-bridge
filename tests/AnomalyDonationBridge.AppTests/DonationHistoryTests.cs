using System.Globalization;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.GameIpc;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.AppTests;

internal static class DonationHistoryTests
{
    public static async Task RunAsync()
    {
        using var directory = new Program.TemporaryDirectory();
        var history = new JsonLineHistoryStore(new AppPaths(directory.Path));
        var start = DateTimeOffset.FromUnixTimeSeconds(1_795_610_000);
        var sourceAction = new ActionDefinition { HandlerId = DonationDispatcher.PdaMessageHandlerId, DisplayName = "Старое действие" };
        var repeat = new ActionDefinition
        {
            HandlerId = DonationDispatcher.RepeatPastDonationHandlerId, DisplayName = "Повторить прошлый донат",
            ParameterDefinitions = [new() { Key = "donations_back", Kind = ParameterKind.Integer, Minimum = 1, Maximum = 999, DefaultValue = "2" }]
        };
        var capabilities = CapabilitySet.Create(EngineProfile.Anomaly153Vanilla, null, [], [sourceAction.HandlerId]);
        var catalogState = new CatalogRuntimeState(capabilities, null, true, "");
        Check(new ActionViewModel(repeat, catalogState: catalogState).CanTest,
            "The application-only template required a Lua handler to enable Test.");

        var test = Paid("test:200", "Локальный тест", start.AddSeconds(1), sourceAction);
        var manual = Paid("manual:300", "Ручной повтор", start.AddSeconds(2), sourceAction);
        var completedTest = Paid(test.DonationId, test.Donor, start.AddSeconds(1), sourceAction,
            DispatchState.Executed, start.AddSeconds(3));
        test.CommandId = completedTest.CommandId = "test-command";
        test.Amount = completedTest.Amount = 0;
        await history.AppendAsync(Paid("100", "Первый", start, sourceAction));
        await history.AppendAsync(test);
        await history.AppendAsync(manual);
        await history.AppendAsync(completedTest);
        await history.AppendAsync(Paid("test:350", "Неудачная попытка", start.AddSeconds(4), repeat, DispatchState.Rejected));
        await history.AppendAsync(Paid("375", "Без правила", start.AddSeconds(4), null, DispatchState.NoRule));

        var trigger = new DonationEvent
        {
            Id = "400", Username = "Новый донатер", Amount = 200, Currency = "RUB",
            ReceivedAt = start.AddSeconds(5), CreatedAt = start.AddSeconds(5)
        };
        await history.AppendAsync(Paid(trigger.Id, trigger.Username, trigger.ReceivedAt, sourceAction));
        await history.AppendAsync(Paid("425", "Следующий донат", trigger.ReceivedAt.AddMilliseconds(100), sourceAction));
        var latest = await DonationHistory.FindReplayTargetAsync(history, trigger, 1, default);
        Check(latest?.DonationId == manual.DonationId, "A manual replay was missing from the recorded action order.");
        var earlier = await DonationHistory.FindReplayTargetAsync(history, trigger, 2, default);
        Check(earlier is { State: DispatchState.Executed, Amount: 0 } && earlier.DonationId == test.DonationId,
            "Replay skipped a zero-amount test or counted its status updates as separate actions.");
        Check(await DonationHistory.FindReplayTargetAsync(history, trigger, 4, default) is null,
            "A row without a recorded action or outside the trigger boundary was counted.");

        var dispatcherHistory = new InMemoryHistorySink();
        var dispatcher = new DonationDispatcher(new InMemoryProcessedDonationStore(), dispatcherHistory)
            { GlobalCooldown = TimeSpan.Zero };
        var options = new EffectExecutionOptions
        {
            ShowPdaEffectMessage = true,
            ResolveHistoryReplay = async (eventSource, count, token) =>
            {
                var entry = await DonationHistory.FindReplayTargetAsync(history, eventSource, count, token);
                return entry?.ActionSnapshot is { } action ? new HistoryReplayEffect(action, new DonationEvent
                {
                    Amount = entry.Amount, Currency = entry.Currency, Username = entry.Donor, Message = entry.DonationMessage
                }, entry.Donor) : null;
            }
        };
        Check(await dispatcher.EnqueueActionAsync(trigger, repeat, new(), executionOptions: options) == DispatchState.Queued,
            "Repeat did not enter the normal effects queue.");
        var transport = new CaptureTransport();
        await dispatcher.DrainAsync(transport, new());
        var command = transport.Commands.Single();
        Check(command.ActionId == sourceAction.HandlerId && command.DonationId == trigger.Id &&
            command.SourceHandlerId == repeat.HandlerId && command.AmountMinor == 0,
            "Repeat lost the recorded action or the origin of the new payment.");
        Check(command.Parameters["adb_replay_by"] == trigger.Username && command.Parameters["adb_replay_from"] == test.Donor &&
            command.Parameters["donation_message"] == test.DonationMessage && !command.ShowPdaEffectMessage,
            "Repeat lost its PDA names, original message or suppressed the wrong notice.");
        Check(dispatcherHistory.Entries.All(row => row.DonationId == trigger.Id),
            "Replay created another paid donation entry.");

        var matchId = Guid.NewGuid().ToString("N");
        var opened = trigger.ReceivedAt.AddSeconds(1);
        var fields = new[] { "BATTLE_STATUS", "recruiting", matchId, trigger.Id,
            opened.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), "3", "0", "" };
        var battle = (BattleRoyaleStatus)PipeProtocol.ParseInbound(string.Join('\t', fields.Select(PipeProtocol.PercentEncode)));
        fields[5] = "32";
        Check(((BattleRoyaleStatus)PipeProtocol.ParseInbound(string.Join('\t', fields.Select(PipeProtocol.PercentEncode)))).Slots == 32,
            "Battle status rejected the maximum opponent count.");
        await history.AppendAsync(Paid(trigger.Id, trigger.Username, trigger.ReceivedAt, sourceAction, DispatchState.Executed, opened));
        await history.AppendAsync(Paid("450", "Донат до набора", opened.AddMilliseconds(-100), null, DispatchState.NoRule));
        await history.AppendAsync(Paid("manual:460", "Пропустить", opened.AddMilliseconds(200), null, DispatchState.NoRule));
        await history.AppendAsync(Paid("470", trigger.Username.ToUpperInvariant(), opened.AddMilliseconds(500), null, DispatchState.NoRule));
        await history.AppendAsync(Paid("500", "Алиса", opened.AddSeconds(1), null, DispatchState.NoRule));
        await history.AppendAsync(Paid("500", "Изменённый ник в повторе статуса", opened.AddSeconds(1), null, DispatchState.Skipped));
        await history.AppendAsync(Paid("donatex:600", "АЛИСА", opened.AddSeconds(2), null, DispatchState.Skipped));
        await history.AppendAsync(Paid("700", "Борис", opened.AddSeconds(3), null, DispatchState.NoRule));
        var participants = await DonationHistory.RecruitAsync(history, battle, default);
        Check(participants.Select(entry => entry.Name).SequenceEqual([trigger.Username, "Алиса", "Борис"]),
            "Battle enrollment did not put the paying opener first or enforce the payment and nickname filters.");
        var restored = battle with { Participants = new HashSet<string> { participants[0].Key, participants[1].Key } };
        Check((await DonationHistory.RecruitAsync(history, restored, default)).Single().Name == "Борис",
            "History reload offered an already-confirmed participant.");
        var testOpener = battle with { Slots = 1, OpeningDonationId = test.DonationId };
        Check((await DonationHistory.RecruitAsync(history, testOpener, default)).Single().Name == trigger.Username.ToUpperInvariant(),
            "A local test occupied a paying opponent's slot.");

        await history.AppendAsync(Paid("donatex:430", "ALIce", opened.AddMilliseconds(-200), null, DispatchState.NoRule));
        await history.AppendAsync(Paid("donatex:431", "Alice", opened.AddMilliseconds(-500), null, DispatchState.NoRule));
        var traitor = (TraitorRecruitmentStatus)PipeProtocol.ParseInbound(string.Join('\t',
            new[] { "TRAITOR_STATUS", "recruiting", matchId, trigger.Id, fields[4], "35", "" }.Select(PipeProtocol.PercentEncode)));
        var candidates = await DonationHistory.TraitorCandidatesAsync(history, traitor, default);
        Check(traitor.Needed == 35 && PipeProtocol.ParseInbound("TRAITOR_STATUS\tidle") == TraitorRecruitmentStatus.Idle,
            "Traitor status did not accept a large roster or the idle reply.");
        Check(candidates.Select(candidate => candidate.DonationId)
                .SequenceEqual([trigger.Id, "450", "donatex:430", "425", "375", "100"]),
            "The traitor did not prioritize the opener, then older distinct donors before the round boundary.");
        Check(TraitorPipe.Roster(traitor with { Cursor = candidates[^1].DonationId }, [], last: true)
                == $"TRAITOR_ROSTER\t{matchId}\t100\tlast",
            "An exhausted history could not be reported without waiting for a new payment.");
    }

    private static EventHistoryEntry Paid(string id, string name, DateTimeOffset time, ActionDefinition? action,
        DispatchState state = DispatchState.Queued, DateTimeOffset? writtenAt = null) => new()
    {
        DonationId = id, Donor = name, Currency = "RUB", Amount = 75, DonationMessage = "Текст " + id,
        DonationCreatedAt = time, DonationReceivedAt = time, Timestamp = writtenAt ?? time,
        ActionSnapshot = action, State = state
    };

    private static void Check(bool condition, string error)
    {
        if (!condition) throw new InvalidOperationException(error);
    }

    private sealed class CaptureTransport : IGameCommandTransport
    {
        public bool IsConnected => true;
        public bool IsGameReady => true;
        public List<GameCommand> Commands { get; } = [];
        public Task SendAsync(GameCommand command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return Task.CompletedTask;
        }
    }
}
