namespace AnomalyDonationBridge.ViewerChat;

// One bounded feed for every source. This class has no reference to game IPC,
// donation processing, the UI dispatcher, disk or platform credentials.
public sealed class ViewerChatFeed
{
    private readonly object _gate = new();
    private readonly LinkedList<ViewerChatEntry> _entries = new();
    private readonly HashSet<(ChatPlatform, string, string)> _seen = [];
    private readonly Queue<(ChatPlatform, string, string)> _seenOrder = new();
    private readonly int _capacity;
    private readonly int _dedupCapacity;
    private long _sequence, _revision, _received;
    private ViewerChatSnapshot? _cached;

    public ViewerChatFeed(int capacity = 1500, int dedupCapacity = 20000)
    {
        if (capacity < 1 || dedupCapacity < capacity) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity; _dedupCapacity = dedupCapacity;
    }

    public ViewerChatSnapshot Snapshot
    {
        get { lock (_gate) return _cached ??= new(_revision, _entries.ToArray(), _received); }
    }

    public void Publish(ViewerMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Id) || string.IsNullOrWhiteSpace(message.ChannelId)) return;
        message = message with { Text = ViewerText.Clean(message.Text), Author = ViewerText.Clean(message.Author, 150) };
        if (message.Text.Length == 0) return;
        lock (_gate)
        {
            if (!Remember((message.Platform, message.ChannelId, message.Id))) return;
            _entries.AddLast(new ViewerChatEntry(++_sequence, message));
            if (_entries.Count > _capacity) _entries.RemoveFirst();
            _received++;
            Invalidate();
        }
    }

    public void Apply(ChatPlatform platform, string channel, ViewerChatEvent change)
    {
        if (change.Kind == ChatEventKind.Message)
        {
            if (change.Message is { } message) Publish(message);
            return;
        }
        lock (_gate)
        {
            if (change.Kind == ChatEventKind.DeleteMessage && change.Id.Length > 0)
                Remember((platform, channel, change.Id));
            var changed = false;
            for (var node = _entries.First; node is not null;)
            {
                var next = node.Next;
                var m = node.Value.Message;
                if (m.Platform == platform && m.ChannelId == channel &&
                    (change.Kind == ChatEventKind.ClearChannel ||
                     change.Kind == ChatEventKind.DeleteMessage && m.Id == change.Id ||
                     change.Kind == ChatEventKind.ClearUser && m.UserId == change.UserId))
                { _entries.Remove(node); changed = true; }
                node = next;
            }
            if (changed) Invalidate();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            // Clearing the display must not turn redelivered messages into new ones.
            Invalidate();
        }
    }

    private bool Remember((ChatPlatform, string, string) key)
    {
        if (!_seen.Add(key)) return false;
        _seenOrder.Enqueue(key);
        if (_seenOrder.Count > _dedupCapacity) _seen.Remove(_seenOrder.Dequeue());
        return true;
    }
    private void Invalidate() { _revision++; _cached = null; }
}
