namespace AnomalyDonationBridge.Core;

public static class EventHistoryProjection
{
    public static IReadOnlyList<EventHistoryEntry> LatestPerCommand(
        IEnumerable<EventHistoryEntry> entries,
        int maximum = 500)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (maximum <= 0) return [];

        var latest = new Dictionary<string, IndexedEntry>(StringComparer.Ordinal);
        var index = 0;
        foreach (var entry in entries)
        {
            KeepLatest(latest, entry, index++);
        }

        return Order(latest.Values, maximum);
    }

    public static async Task<IReadOnlyList<EventHistoryEntry>> LatestPerCommandAsync(
        IAsyncEnumerable<EventHistoryEntry> entriesInAppendOrder, int maximum, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entriesInAppendOrder);
        if (maximum <= 0) return [];

        var latest = new Dictionary<string, IndexedEntry>(StringComparer.Ordinal);
        var index = 0;
        await foreach (var entry in entriesInAppendOrder.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            // The history screen loads newest file rows first, including when timestamps tie.
            KeepLatest(latest, entry, index--);
        }

        return Order(latest.Values, maximum);
    }

    private static void KeepLatest(Dictionary<string, IndexedEntry> latest, EventHistoryEntry entry, int index)
    {
        var key = LogicalKey(entry);
        if (!latest.TryGetValue(key, out var current) || entry.Timestamp > current.Entry.Timestamp ||
            entry.Timestamp == current.Entry.Timestamp && index < current.Index)
        {
            latest[key] = new IndexedEntry(entry, index);
        }
    }

    private static IReadOnlyList<EventHistoryEntry> Order(IEnumerable<IndexedEntry> entries, int maximum) =>
        entries
            .OrderByDescending(item => SortTime(item.Entry))
            .ThenByDescending(item => item.Entry.Timestamp)
            .ThenBy(item => item.Index)
            .Take(maximum)
            .Select(item => item.Entry)
            .ToList();

    public static bool IsSameLogicalEvent(EventHistoryEntry? left, EventHistoryEntry? right)
    {
        if (left is null || right is null) return false;
        return string.Equals(LogicalKey(left), LogicalKey(right), StringComparison.Ordinal);
    }

    public static DateTimeOffset SortTime(EventHistoryEntry entry) =>
        entry.DonationCreatedAt == default ? entry.Timestamp : entry.DonationCreatedAt;

    private static string LogicalKey(EventHistoryEntry entry) =>
        string.IsNullOrWhiteSpace(entry.CommandId)
            ? "event:" + entry.EventId
            : "command:" + entry.CommandId;

    private sealed record IndexedEntry(EventHistoryEntry Entry, int Index);
}
