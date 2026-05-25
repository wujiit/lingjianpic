namespace ModernImageViewer.Desktop.Services;

internal sealed class DesktopMagickOperationGate
{
    private const int WaitIntervalMilliseconds = 50;

    private readonly object _syncRoot = new();
    private readonly AsyncLocal<int> _reentrancyDepth = new();
    private int _activeCount;
    private int _limit;

    public DesktopMagickOperationGate(int limit)
    {
        _limit = Math.Max(1, limit);
    }

    public static DesktopMagickOperationGate Shared { get; } =
        new(DesktopImageProcessingPolicy.MagickOperationLimit);

    internal int ActiveCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _activeCount;
            }
        }
    }

    internal int CurrentLimit
    {
        get
        {
            lock (_syncRoot)
            {
                return _limit;
            }
        }
    }

    public void UpdateLimit(int limit)
    {
        lock (_syncRoot)
        {
            _limit = Math.Max(1, limit);
            Monitor.PulseAll(_syncRoot);
        }
    }

    public void Run(Action operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        using var lease = Enter(cancellationToken);
        operation();
    }

    public T Run<T>(Func<T> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        using var lease = Enter(cancellationToken);
        return operation();
    }

    private IDisposable Enter(CancellationToken cancellationToken)
    {
        if (_reentrancyDepth.Value > 0)
        {
            _reentrancyDepth.Value++;
            return new Lease(this, acquiredSlot: false);
        }

        lock (_syncRoot)
        {
            while (_activeCount >= _limit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Monitor.Wait(_syncRoot, WaitIntervalMilliseconds);
            }

            _activeCount++;
            _reentrancyDepth.Value = 1;
            return new Lease(this, acquiredSlot: true);
        }
    }

    private void Release(bool acquiredSlot)
    {
        var depth = _reentrancyDepth.Value;
        if (depth > 1)
        {
            _reentrancyDepth.Value = depth - 1;
            return;
        }

        _reentrancyDepth.Value = 0;
        if (!acquiredSlot)
        {
            return;
        }

        lock (_syncRoot)
        {
            if (_activeCount > 0)
            {
                _activeCount--;
            }

            Monitor.PulseAll(_syncRoot);
        }
    }

    private sealed class Lease(DesktopMagickOperationGate owner, bool acquiredSlot) : IDisposable
    {
        private DesktopMagickOperationGate? _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null)
            {
                return;
            }

            owner.Release(acquiredSlot);
        }
    }
}
