namespace AnomalyDonationBridge.ViewerChat;

public sealed class ViewerChatSession : IAsyncDisposable
{
    private readonly IViewerChatTransport _transport;
    private readonly Func<ViewerChatEvent, CancellationToken, Task> _publish;
    private readonly CancellationTokenSource _stop;
    private readonly string _channel;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private Task? _worker;
    private ViewerConnectionSnapshot _snapshot;

    public ViewerChatSession(IViewerChatTransport transport, string channel,
        Func<ViewerChatEvent, CancellationToken, Task> publish, CancellationToken token = default)
    {
        _transport = transport; _channel = channel;
        _publish = (change, cancellation) => publish(change.Message is { } message
            ? change with { Message = message with { IsHistorical = message.IsHistorical ||
                message.PublishedAt is null || message.PublishedAt < _startedAt } } : change, cancellation);
        _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        _snapshot = new(ViewerChatState.Disconnected, "Не подключён", channel);
    }
    public ViewerConnectionSnapshot Snapshot => Volatile.Read(ref _snapshot);
    private void State(ViewerChatState state, string text) =>
        Volatile.Write(ref _snapshot, new(state, text, _channel));
    public void Start()
    {
        if (_worker is not null) throw new InvalidOperationException("Chat already started.");
        State(ViewerChatState.Connecting, "Подключаемся…");
        _worker = Task.Run(RunAsync);
    }
    private async Task RunAsync()
    {
        var failures = 0;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await _transport.RunAsync(_channel, _publish, () =>
                    {
                        failures = 0; State(ViewerChatState.Connected, "Подключён · " + _channel);
                    }, _stop.Token).ConfigureAwait(false);
                    if (!_stop.IsCancellationRequested) throw new ViewerChatException("Соединение с чатом закрыто.");
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (ViewerChatException e) when (!e.Retryable)
                { State(ViewerChatState.Error, e.Message); return; }
                catch (Exception e)
                {
                    var wait = TimeSpan.FromSeconds(Math.Min(60, 3 * Math.Pow(2, Math.Min(failures++, 5))));
                    var hint = e is ViewerChatException known ? known.Message : "Нет связи с чатом.";
                    State(ViewerChatState.Retrying, $"{hint} Повтор через {wait.TotalSeconds:0} с.");
                    await Task.Delay(wait, _stop.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            if (_stop.IsCancellationRequested) State(ViewerChatState.Disconnected, "Отключён");
        }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_worker is not null) await _worker.ConfigureAwait(false);
        _transport.Dispose(); _stop.Dispose();
    }
}
