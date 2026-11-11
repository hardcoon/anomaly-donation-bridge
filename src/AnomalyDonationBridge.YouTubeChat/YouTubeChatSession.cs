using System.Diagnostics;

namespace AnomalyDonationBridge.YouTubeChat;

// A chat-only reader: no references to the donation dispatcher, game IPC or disk
// history. The preview is bounded; the async consumer receives the entire live
// batch before its continuation token is committed.
public sealed class YouTubeChatSession : IAsyncDisposable
{
    private readonly IYouTubeChatTransport _transport;
    private readonly string _apiKey;
    private readonly string _videoId;
    private readonly CancellationTokenSource _stop;
    private readonly object _sync = new();
    private readonly Queue<YouTubeChatMessage> _preview = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<IReadOnlyList<YouTubeChatMessage>, CancellationToken, Task>? _onMessages;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private YouTubeChatSnapshot _snapshot = YouTubeChatSnapshot.Empty;
    private string? _pageToken;
    private Task? _worker;
    private DateTimeOffset _retryNotBefore;
    public const int PreviewLimit = 50;
    internal const int DedupLimit = 10000;
    private static readonly TimeSpan MinimumStreamInterval = TimeSpan.FromSeconds(5);
    private const string ContinuingStatus = "Продолжаем чтение чата YouTube…";

    public YouTubeChatSession(IYouTubeChatTransport transport, string apiKey, string videoId,
        CancellationToken cancellationToken = default,
        Func<IReadOnlyList<YouTubeChatMessage>, CancellationToken, Task>? onMessages = null)
        : this(transport, apiKey, videoId, Task.Delay, cancellationToken, onMessages) { }

    internal YouTubeChatSession(IYouTubeChatTransport transport, string apiKey, string videoId,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken cancellationToken = default,
        Func<IReadOnlyList<YouTubeChatMessage>, CancellationToken, Task>? onMessages = null)
    {
        _transport = transport;
        _apiKey = YouTubeVideoLink.NormalizeKey(apiKey);
        _videoId = YouTubeVideoLink.Parse(videoId);
        _delay = delay;
        _onMessages = onMessages;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    public YouTubeChatSnapshot Snapshot { get { lock (_sync) return _snapshot; } }
    public DateTimeOffset RetryNotBefore { get { lock (_sync) return _retryNotBefore; } }

    public void Start()
    {
        lock (_sync)
        {
            if (_worker is not null) throw new InvalidOperationException("Chat session already started.");
            SetState(YouTubeChatState.Connecting, "Подключение к чату YouTube…");
            _worker = Task.Run(RunAsync);
        }
    }

    private async Task RunAsync()
    {
        YouTubeBroadcast? broadcast = null;
        var failures = 0;
        var continuing = false;
        var cancellationToken = _stop.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var connectedAt = Stopwatch.GetTimestamp();
                try
                {
                    broadcast ??= await _transport.ResolveAsync(_apiKey, _videoId, cancellationToken).ConfigureAwait(false);
                    lock (_sync) _snapshot = _snapshot with { Broadcast = broadcast, Revision = _snapshot.Revision + 1 };
                    SetState(YouTubeChatState.Connecting, continuing ? ContinuingStatus : "Подключение к чату YouTube…");
                    await foreach (var batch in _transport.ReadAsync(_apiKey, broadcast.ChatId, _pageToken, cancellationToken)
                                       .WithCancellation(cancellationToken).ConfigureAwait(false))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var messages = batch.Messages.Where(message => message.Id.Length > 0 && !_seen.Contains(message.Id))
                            .DistinctBy(message => message.Id, StringComparer.Ordinal)
                            .Select(message => message with { IsHistorical = message.IsHistorical || message.PublishedAt is null || message.PublishedAt < _startedAt })
                            .ToArray();
                        // Await outside the snapshot lock; storage pressure must
                        // not freeze the UI or advance past an unpersisted batch.
                        if (_onMessages is not null && messages.Length > 0)
                            await _onMessages(messages, cancellationToken).ConfigureAwait(false);
                        lock (_sync)
                        {
                            foreach (var message in messages)
                            {
                                if (message.Id.Length == 0 || !_seen.Add(message.Id)) continue;
                                _seenOrder.Enqueue(message.Id);
                                if (_seenOrder.Count > DedupLimit) _seen.Remove(_seenOrder.Dequeue());
                                _preview.Enqueue(message);
                                if (_preview.Count > PreviewLimit) _preview.Dequeue();
                                _snapshot = _snapshot with { ReceivedCount = _snapshot.ReceivedCount + 1 };
                            }
                            if (batch.NextPageToken.Length > 0) _pageToken = batch.NextPageToken;
                            _snapshot = _snapshot with
                            {
                                State = batch.Ended ? YouTubeChatState.Ended : YouTubeChatState.Connected,
                                Status = batch.Ended ? "Трансляция завершена" : "Подключено · сообщения поступают напрямую из YouTube",
                                Messages = _preview.Reverse().ToArray(), Revision = _snapshot.Revision + 1
                            };
                            _retryNotBefore = default;
                        }
                        if (batch.Ended) return;
                    }
                    // A successful StreamList can finish and supply a continuation
                    // token. Google's sample opens the next RPC with that token;
                    // this is not a failed connection and must not grow backoff.
                    // A quiet chat can also finish without another response: keep
                    // the last confirmed cursor. Without ANY cursor we cannot
                    // resume safely, so back off instead of replaying history.
                    if (string.IsNullOrEmpty(_pageToken))
                        throw new YouTubeChatException("continuation_missing",
                            "YouTube завершил ответ без позиции продолжения чата.", true);
                    failures = 0;
                    continuing = true;
                    var continuationWait = ContinuationDelay(Stopwatch.GetElapsedTime(connectedAt));
                    lock (_sync) _retryNotBefore = DateTimeOffset.UtcNow + continuationWait;
                    SetState(YouTubeChatState.Connecting, ContinuingStatus);
                    // Bound even immediate successful EOFs. Long-lived streams
                    // resume immediately; no extra REST discovery or polling.
                    if (continuationWait > TimeSpan.Zero)
                        await _delay(continuationWait, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (YouTubeChatException error)
                {
                    continuing = false;
                    if (!error.Retryable)
                    {
                        if (error.RetryAfter is { } cooldown)
                            lock (_sync) _retryNotBefore = DateTimeOffset.UtcNow + cooldown;
                        SetState(error.Code is "ended" or "ended_or_disabled" ? YouTubeChatState.Ended : YouTubeChatState.Error,
                            error.Message);
                        return;
                    }
                    if (Stopwatch.GetElapsedTime(connectedAt) >= TimeSpan.FromMinutes(1)) failures = 0;
                    var seconds = Math.Min(120, 5 * Math.Pow(2, Math.Min(failures++, 5))) + Random.Shared.NextDouble() * 2;
                    var wait = TimeSpan.FromSeconds(seconds);
                    if (error.RetryAfter is { } minimum && minimum > wait) wait = minimum;
                    lock (_sync) _retryNotBefore = DateTimeOffset.UtcNow + wait;
                    SetState(YouTubeChatState.Retrying, $"{error.Message} Повтор через {Math.Ceiling(wait.TotalSeconds):0} с.");
                    await _delay(wait, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception)
        {
            // Never surface a raw exception from an authenticated transport.
            SetState(YouTubeChatState.Error, "Не удалось прочитать чат YouTube. Отключите и подключите его заново.");
        }
    }

    internal static TimeSpan ContinuationDelay(TimeSpan elapsed) =>
        elapsed >= MinimumStreamInterval ? TimeSpan.Zero : MinimumStreamInterval - elapsed;

    private void SetState(YouTubeChatState state, string text)
    {
        lock (_sync) _snapshot = _snapshot with { State = state, Status = text, Revision = _snapshot.Revision + 1 };
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_worker is not null) await _worker.ConfigureAwait(false);
        SetState(YouTubeChatState.Disconnected, "Чат YouTube отключён");
        _stop.Dispose();
    }
}
