using System.IO;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.GameIpc;

namespace AnomalyDonationBridge.App.Services;

public sealed partial class BridgeRuntime
{
    private readonly SemaphoreSlim _votingHudGate = new(1, 1);
    private string? _lastVotingHudFrame;
    private DateTimeOffset _lastVotingHudSent;
    internal Func<string, CancellationToken, Task>? VotingHudTransportForTests { get; set; }
    internal VotingRound? ActiveVotingRound => Voting.Collections.FirstOrDefault(item => item.IsActive && item.ProfileId == Settings.ActiveProfileId);

    internal async Task AdvanceVotingHudAsync(CancellationToken token = default)
    {
        if (!await _votingHudGate.WaitAsync(0, token).ConfigureAwait(false)) return;
        try
        {
            if (VotingHudTransportForTests is null && (!_gameServer.IsConnected || !_gameServer.Capabilities.SupportsCapability(VotingGameHud.Capability)))
            { _lastVotingHudFrame = null; return; }
            var now = DateTimeOffset.UtcNow;
            var round = ActiveVotingRound;
            var frame = VotingGameHud.Frame(round, Voting.Automation, now);
            if (frame == _lastVotingHudFrame && now - _lastVotingHudSent < TimeSpan.FromSeconds(2)) return;
            try
            {
                if (VotingHudTransportForTests is { } transport) await transport(frame, token).ConfigureAwait(false);
                else await _gameServer.SendVotingHudAsync(frame, token).ConfigureAwait(false);
                _lastVotingHudFrame = frame; _lastVotingHudSent = now;
            }
            // A cosmetic display cannot stop accounting or winner delivery.
            catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException) { _lastVotingHudFrame = null; }
        }
        finally { _votingHudGate.Release(); }
    }
}
