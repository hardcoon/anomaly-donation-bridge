using System.IO;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.GameIpc;

namespace AnomalyDonationBridge.App.Services;

public sealed partial class BridgeRuntime
{
    private long _battleHistoryRevision;
    private long _syncedBattleHistoryRevision;
    private BattleRoyaleStatus? _syncedBattle;
    private Task _battleUpdate = Task.CompletedTask;

    private void OnBattleHistoryAdded(EventHistoryEntry entry)
    {
        if (_gameServer.BattleStatus is { Recruiting: true } battle &&
            DonationHistory.WasReceivedDuringRecruitment(entry, battle))
            Interlocked.Increment(ref _battleHistoryRevision);
    }

    private void ScheduleBattleSync(CancellationToken token)
    {
        if (!_battleUpdate.IsCompleted) return;
        var battle = _gameServer.BattleStatus;
        if (!_gameServer.Capabilities.SupportsCapability(BattleRoyalePipe.Capability) ||
            battle is { Recruiting: false })
        {
            _syncedBattle = null;
            return;
        }
        var revision = Interlocked.Read(ref _battleHistoryRevision);
        if (battle is not null && ReferenceEquals(_syncedBattle, battle) && _syncedBattleHistoryRevision == revision) return;
        _battleUpdate = Task.Run(() => SyncBattleAsync(battle, revision, token), token);
    }

    private async Task SyncBattleAsync(BattleRoyaleStatus? battle, long revision, CancellationToken token)
    {
        try
        {
            if (battle is null)
            {
                _syncedBattle = null;
                await _gameServer.SendBattleUpdateAsync(BattleRoyalePipe.Query, token).ConfigureAwait(false);
                return;
            }
            var participants = await DonationHistory.RecruitAsync(_historyStore, battle, token).ConfigureAwait(false);
            if (!ReferenceEquals(battle, _gameServer.BattleStatus)) return;
            foreach (var chunk in participants.Chunk(BattleRoyalePipe.ParticipantsPerMessage))
                if (!await _gameServer.SendBattleUpdateAsync(BattleRoyalePipe.Roster(battle.MatchId, chunk), token)
                        .ConfigureAwait(false)) return;
            _syncedBattle = battle;
            _syncedBattleHistoryRevision = revision;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await _logger.WriteAsync("WARN", "Батлл-роял: " + error.Message, token).ConfigureAwait(false);
        }
    }

    internal bool CanCancelBattle => _gameServer.BattleStatus is { Recruiting: true };

    internal async Task CancelBattleAsync()
    {
        if (_gameServer.BattleStatus is not { Recruiting: true } battle ||
            !await _gameServer.SendBattleUpdateAsync(BattleRoyalePipe.Cancel(battle.MatchId), _lifetime.Token)
                .ConfigureAwait(false))
            throw new InvalidOperationException("В игре нет открытого набора.");
    }
}
