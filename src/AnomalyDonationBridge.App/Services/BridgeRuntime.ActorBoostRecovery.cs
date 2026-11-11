using System.Collections.Concurrent;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.App.Services;

public sealed partial class BridgeRuntime
{
    private readonly SemaphoreSlim _actorBoostResetGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<GameExecutionResult>> _actorBoostResetRequests = new(StringComparer.Ordinal);
    internal Func<GameCommand, CancellationToken, Task<GameExecutionResult>>? ActorBoostResetTransportForTests { get; set; }

    public async Task<string> ResetActorBoostAsync(CancellationToken cancellationToken = default)
    {
        var isolatedTransport = ActorBoostResetTransportForTests;
        if (isolatedTransport is null && (!_gameServer.IsConnected || !_gameServer.IsGameReady))
            throw new InvalidOperationException("Загрузите сохранение, снимите паузу и закройте игровые окна перед сбросом.");
        if (isolatedTransport is null && !_gameServer.Capabilities.SupportsCapability(ActorBoostRecovery.Capability))
            throw new InvalidOperationException("Обновите аддон и шаблоны, затем полностью перезапустите игру: установленный аддон ещё не поддерживает сброс параметров.");
        if (!await _actorBoostResetGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Сброс параметров уже выполняется.");
        var command = ActorBoostRecovery.CreateCommand();
        var completion = new TaskCompletionSource<GameExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            _actorBoostResetRequests[command.CommandId] = completion;
            // Explicit manual recovery bypasses donation matching, arming and
            // gameplay delays. The normal IPC readiness/probe checks still run.
            if (isolatedTransport is null) await _gameServer.SendAsync(command, linked.Token).ConfigureAwait(false);
            GameExecutionResult result;
            try
            {
                result = await (isolatedTransport is null ? completion.Task : isolatedTransport(command, linked.Token))
                    .WaitAsync(TimeSpan.FromSeconds(15), linked.Token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            { throw new InvalidOperationException("Игра не подтвердила сброс параметров. Проверьте состояние героя перед повтором."); }
            if (result.Status != GameResultStatus.Executed)
                throw new InvalidOperationException(result.Reason switch
                {
                    "actor_boost_reset_defaults_unavailable" => "Не удалось прочитать базовые параметры из конфигурации этой сборки. Сброс не выполнен.",
                    "actor_boost_reset_journal_clear_failed" => "Не удалось полностью очистить старые записи параметров ADB. Повторите сброс после проверки игры.",
                    "actor_boost_reset_session_clear_failed" => "Не удалось остановить временный эффект в памяти игры. Сброс не выполнен.",
                    "actor_boost_reset_apply_failed" => "Движок не подтвердил восстановление всех параметров. Проверьте героя перед повтором.",
                    _ => "Игра не выполнила сброс параметров: " + result.Reason
                });
            return "Параметры сброшены к базовым. Сохраните игру, чтобы закрепить очистку старых записей ADB.";
        }
        finally
        {
            _actorBoostResetRequests.TryRemove(command.CommandId, out _);
            _actorBoostResetGate.Release();
        }
    }

    private bool TryCompleteActorBoostReset(GameExecutionResult result) =>
        _actorBoostResetRequests.TryGetValue(result.CommandId, out var completion) && completion.TrySetResult(result);
}
