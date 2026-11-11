using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.App.Services;

public sealed partial class BridgeRuntime
{
    public bool CanApplyQueuedImmediately(EventHistoryEntry? entry) =>
        entry is { State: DispatchState.Queued } &&
        _dispatcher.IsQueued(entry.DonationId, entry.CommandId);

    public async Task<int> ApplyQueuedImmediatelyAsync(
        IEnumerable<EventHistoryEntry> entries, CancellationToken cancellationToken = default)
    {
        var ids = entries.Where(CanApplyQueuedImmediately).Select(entry => entry.CommandId)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return 0;
        var count = await _dispatcher.RequestImmediateAsync(ids, cancellationToken).ConfigureAwait(false);
        // Normal transport/preflight still owns readiness, catalog and Safe Zone
        // checks. Neither donation reception nor game state is forced on here.
        if (count > 0)
            await _dispatcher.DrainAsync(_gameServer, Settings.QueuePolicy, cancellationToken).ConfigureAwait(false);
        return count;
    }
}
