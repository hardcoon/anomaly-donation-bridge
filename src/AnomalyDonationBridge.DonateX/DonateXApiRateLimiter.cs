namespace AnomalyDonationBridge.DonateX;

public interface IDonateXApiRateLimiter
{
    ValueTask WaitAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Applies the three REST limits published by DonateX. SignalR events do not
/// pass through this limiter.
/// </summary>
public sealed class DonateXApiRateLimiter : IDonateXApiRateLimiter, IDisposable
{
    public const int PerSecond = 10;
    public const int PerMinute = 600;
    public const int PerHour = 10_000;

    private readonly Func<bool> _isEnabled;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<DateTimeOffset> _second = [];
    private readonly Queue<DateTimeOffset> _minute = [];
    private readonly Queue<DateTimeOffset> _hour = [];

    public DonateXApiRateLimiter(Func<bool> isEnabled)
        : this(isEnabled, () => DateTimeOffset.UtcNow,
            static (delay, cancellationToken) => Task.Delay(delay, cancellationToken))
    {
    }

    internal DonateXApiRateLimiter(
        Func<bool> isEnabled,
        Func<DateTimeOffset> utcNow,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
    }

    public async ValueTask WaitAsync(CancellationToken cancellationToken = default)
    {
        if (!_isEnabled()) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (_isEnabled())
            {
                var now = _utcNow();
                Trim(_second, now - TimeSpan.FromSeconds(1));
                Trim(_minute, now - TimeSpan.FromMinutes(1));
                Trim(_hour, now - TimeSpan.FromHours(1));

                var allowedAt = now;
                allowedAt = Later(allowedAt, LimitEnd(_second, PerSecond, TimeSpan.FromSeconds(1)));
                allowedAt = Later(allowedAt, LimitEnd(_minute, PerMinute, TimeSpan.FromMinutes(1)));
                allowedAt = Later(allowedAt, LimitEnd(_hour, PerHour, TimeSpan.FromHours(1)));
                if (allowedAt > now)
                {
                    await _delay(allowedAt - now, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                _second.Enqueue(now);
                _minute.Enqueue(now);
                _hour.Enqueue(now);
                return;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void Trim(Queue<DateTimeOffset> values, DateTimeOffset threshold)
    {
        while (values.TryPeek(out var value) && value <= threshold) values.Dequeue();
    }

    private static DateTimeOffset LimitEnd(
        Queue<DateTimeOffset> values, int maximum, TimeSpan window) =>
        values.Count < maximum ? DateTimeOffset.MinValue : values.Peek() + window;

    private static DateTimeOffset Later(DateTimeOffset left, DateTimeOffset right) =>
        left >= right ? left : right;

    public void Dispose() => _gate.Dispose();
}
