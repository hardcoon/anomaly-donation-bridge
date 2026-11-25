namespace AnomalyDonationBridge.ViewerChat;

// Each window browses its own pages over the shared, bounded live buffer.
public sealed class ViewerChatPageWindow
{
    public const int PageSize = 150;
    private long _first, _last;
    private bool _initialized;

    public IReadOnlyList<ViewerChatEntry> Select(ViewerChatSnapshot snapshot, bool followTail)
    {
        var messages = snapshot.Messages;
        if (messages.Count == 0) { _initialized = false; _first = _last = 0; return []; }
        if (!_initialized || followTail)
        {
            _first = messages[Math.Max(0, messages.Count - PageSize)].Sequence;
            _last = messages[^1].Sequence; _initialized = true;
        }
        else if (_last < messages[0].Sequence)
        {
            // The buffer has overtaken a reader who stayed far behind. Keep
            // the earliest available page rather than jumping to the live end.
            _first = messages[0].Sequence;
            _last = messages[Math.Min(PageSize, messages.Count) - 1].Sequence;
        }
        return messages.Where(item => item.Sequence >= _first && item.Sequence <= _last).ToArray();
    }

    public IReadOnlyList<ViewerChatEntry> LoadOlder(ViewerChatSnapshot snapshot)
    {
        Select(snapshot, followTail: false);
        var before = 0;
        while (before < snapshot.Messages.Count && snapshot.Messages[before].Sequence < _first) before++;
        if (before > 0) _first = snapshot.Messages[Math.Max(0, before - PageSize)].Sequence;
        return Select(snapshot, followTail: false);
    }

    public bool HasOlder(ViewerChatSnapshot snapshot) => _initialized && snapshot.Messages.Count > 0 && snapshot.Messages[0].Sequence < _first;
    public bool HasNewer(ViewerChatSnapshot snapshot) => _initialized && snapshot.Messages.Count > 0 && snapshot.Messages[^1].Sequence > _last;
}
