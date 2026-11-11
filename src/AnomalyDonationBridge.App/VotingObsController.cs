using System.Windows.Threading;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.App;

// Owned by the application window, not a voting editor or an individual vote.
// Keep the native window alive so an OBS capture survives round transitions.
public sealed class VotingObsController : IDisposable
{
    private readonly BridgeRuntime _runtime;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private VotingObsWindow? _window;
    private string? _announcedRoundId, _animatedRoundId;
    private int _refreshQueued;
    private volatile bool _disposed;

    public VotingObsController(BridgeRuntime runtime)
    {
        _runtime = runtime;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Refresh();
        runtime.Voting.Changed += VotingChanged;
        runtime.VotingDrawStarted += DrawStarted;
    }

    public void Show() => GetOrCreateWindow().Show();

    internal VotingObsWindow GetOrCreateWindow()
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_window is null)
        {
            var window = new VotingObsWindow { ShowActivated = false };
            _window = window;
            window.Closed += (_, _) =>
            {
                if (!ReferenceEquals(_window, window)) return;
                _timer.Stop(); _window = null; _animatedRoundId = null;
            };
            _timer.Start();
        }
        Refresh();
        return _window;
    }

    private void VotingChanged()
    {
        if (_disposed || _dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            Refresh();
        }));
    }

    private void DrawStarted(VotingRound round)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_disposed || _runtime.VotingDrawingRoundId != round.Id) return;
            _announcedRoundId = round.Id;
            Refresh();
        }));
    }

    private void Refresh()
    {
        if (_disposed || _window is null) return;
        // Resolve at display time, rather than retaining an event's old snapshot.
        // Use exactly the same active/profile selection as the in-game HUD.
        var round = _runtime.ActiveVotingRound?.ResultSnapshot();
        if (round is null)
        {
            _window.Clear(); _animatedRoundId = null;
            return;
        }
        var spinning = _runtime.VotingDrawingRoundId == round.Id;
        if (_window.Wheel.Round?.Id != round.Id) _animatedRoundId = null;
        _window.Update(round, spinning, _runtime.Voting.Automation);
        if (spinning && _announcedRoundId == round.Id && _animatedRoundId != round.Id)
        {
            _animatedRoundId = round.Id;
            _window.Animate(round);
        }
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _runtime.Voting.Changed -= VotingChanged;
        _runtime.VotingDrawStarted -= DrawStarted;
        _timer.Stop();
        _window?.Close();
    }
}
