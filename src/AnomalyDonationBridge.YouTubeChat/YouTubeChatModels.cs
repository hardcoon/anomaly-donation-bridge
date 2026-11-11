namespace AnomalyDonationBridge.YouTubeChat;

public sealed record YouTubeBroadcast(string VideoId, string ChatId, string Title, string ChannelTitle);

public sealed record YouTubeChatMessage(string Id, string AuthorChannelId, string Author, string Text,
    DateTimeOffset? PublishedAt, int EventType, bool IsOwner = false, bool IsModerator = false,
    bool IsHistorical = false);

public sealed record YouTubeChatBatch(IReadOnlyList<YouTubeChatMessage> Messages, string NextPageToken,
    bool Ended = false);

public enum YouTubeChatState { Disconnected, Connecting, Connected, Retrying, Ended, Error }

public sealed record YouTubeChatSnapshot(YouTubeChatState State, string Status,
    YouTubeBroadcast? Broadcast, long ReceivedCount, IReadOnlyList<YouTubeChatMessage> Messages,
    long Revision)
{
    public static YouTubeChatSnapshot Empty { get; } = new(YouTubeChatState.Disconnected,
        "Чат YouTube не подключён", null, 0, [], 0);
}

// Only safe, predefined messages reach the UI. Never attach raw server responses,
// request headers, keys or transport exceptions to this exception.
public sealed class YouTubeChatException(string code, string message, bool retryable = false,
    TimeSpan? retryAfter = null) : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public interface IYouTubeChatTransport
{
    Task<YouTubeBroadcast> ResolveAsync(string apiKey, string videoId, CancellationToken cancellationToken);
    IAsyncEnumerable<YouTubeChatBatch> ReadAsync(string apiKey, string chatId, string? pageToken,
        CancellationToken cancellationToken);
}

internal static class ChatText
{
    internal static string Clean(string? value, int maximum = 2000)
    {
        var text = new string((value ?? string.Empty).Take(maximum)
            .Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        if (text.Length > 0 && char.IsHighSurrogate(text[^1])) text = text[..^1];
        return text;
    }
}
