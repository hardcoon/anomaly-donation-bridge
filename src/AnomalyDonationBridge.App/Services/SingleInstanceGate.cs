namespace AnomalyDonationBridge.App.Services;

internal sealed class SingleInstanceGate : IDisposable
{
    internal const string DefaultName = @"Local\AnomalyDonationBridge.App.v1";

    private Mutex? _mutex;
    private bool _ownsMutex;

    private SingleInstanceGate(Mutex mutex)
    {
        _mutex = mutex;
        _ownsMutex = true;
    }

    public static SingleInstanceGate? TryAcquire(string? name = null)
    {
        var mutex = new Mutex(true, name ?? DefaultName, out var createdNew);
        if (createdNew) return new SingleInstanceGate(mutex);
        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        var mutex = Interlocked.Exchange(ref _mutex, null);
        if (mutex is null) return;
        if (_ownsMutex)
        {
            _ownsMutex = false;
            mutex.ReleaseMutex();
        }
        mutex.Dispose();
    }
}
