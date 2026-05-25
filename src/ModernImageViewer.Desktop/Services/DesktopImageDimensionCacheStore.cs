using System.Collections.Concurrent;
using ModernImageViewer.Core;

namespace ModernImageViewer.Desktop.Services;

public readonly record struct DesktopImageDimensions(uint Width, uint Height)
{
    public uint LongEdge => Math.Max(Width, Height);
}

internal readonly record struct DesktopImageDimensionCacheKey(
    string Path,
    long SizeBytes,
    long SourceStampTicks);

internal sealed class DesktopImageDimensionCacheStore
{
    private const int PersistMutationInterval = 16;
    private const int DefaultPersistDelayMilliseconds = 300;

    public static DesktopImageDimensionCacheStore Shared { get; } =
        new(new DesktopImageDimensionStateStore(), DesktopImageProcessingPolicy.ImageDimensionCacheEntryLimit);

    private readonly DesktopImageDimensionStateStore? _stateStore;
    private readonly int _persistDelayMilliseconds;
    private readonly object _persistSync = new();
    private readonly object _persistExecutionSync = new();
    private readonly ConcurrentDictionary<DesktopImageDimensionCacheKey, CacheEntry> _entries =
        new(ImageDimensionCacheKeyComparer.Instance);

    private int _maxEntries;
    private long _accessStamp;
    private int _changeVersion;
    private int _savedVersion;
    private CancellationTokenSource? _pendingPersistCancellationTokenSource;
    private Task _pendingPersistTask = Task.CompletedTask;

    public DesktopImageDimensionCacheStore()
        : this(
            new DesktopImageDimensionStateStore(),
            DesktopImageProcessingPolicy.ImageDimensionCacheEntryLimit,
            DefaultPersistDelayMilliseconds)
    {
    }

    internal DesktopImageDimensionCacheStore(int maxEntries)
        : this(stateStore: null, maxEntries, DefaultPersistDelayMilliseconds)
    {
    }

    internal DesktopImageDimensionCacheStore(DesktopImageDimensionStateStore? stateStore, int maxEntries)
        : this(stateStore, maxEntries, DefaultPersistDelayMilliseconds)
    {
    }

    internal DesktopImageDimensionCacheStore(
        DesktopImageDimensionStateStore? stateStore,
        int maxEntries,
        int persistDelayMilliseconds)
    {
        _stateStore = stateStore;
        _maxEntries = Math.Max(1, maxEntries);
        _persistDelayMilliseconds = Math.Max(0, persistDelayMilliseconds);
        LoadPersistedEntries();
    }

    public DesktopImageDimensions? GetOrLoad(string path, Func<string, DesktopImageDimensions?> loader)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(loader);

        FileInfo? fileInfo;
        try
        {
            fileInfo = new FileInfo(path);
            if (!fileInfo.Exists)
            {
                return null;
            }
        }
        catch
        {
            return loader(path);
        }

        var key = new DesktopImageDimensionCacheKey(path, fileInfo.Length, fileInfo.LastWriteTimeUtc.Ticks);
        return GetOrLoadCore(key, loader);
    }

    public DesktopImageDimensions? GetOrLoad(DesktopFileSignature signature, Func<string, DesktopImageDimensions?> loader)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signature.Path);
        ArgumentNullException.ThrowIfNull(loader);

        var key = new DesktopImageDimensionCacheKey(signature.Path, signature.SizeBytes, signature.SourceStampTicks);
        return GetOrLoadCore(key, loader);
    }

    private DesktopImageDimensions? GetOrLoadCore(
        DesktopImageDimensionCacheKey key,
        Func<string, DesktopImageDimensions?> loader)
    {
        var newEntry = new CacheEntry(
            new Lazy<DesktopImageDimensions?>(() => loader(key.Path), LazyThreadSafetyMode.ExecutionAndPublication),
            NextAccessStamp());
        var entry = _entries.GetOrAdd(key, newEntry);
        var created = ReferenceEquals(entry, newEntry);
        Touch(entry);

        try
        {
            var value = entry.Value.Value;
            if (value is null)
            {
                if (created)
                {
                    _entries.TryRemove(key, out _);
                }

                return null;
            }

            if (created)
            {
                RemoveStaleEntries(key);
                var removedCount = TrimEntries();
                MarkDirty(forcePersist: removedCount > 0);
            }

            return value;
        }
        catch
        {
            if (created)
            {
                _entries.TryRemove(key, out _);
            }

            throw;
        }
    }

    public void UpdateEntryLimit(int maxEntries)
    {
        Volatile.Write(ref _maxEntries, Math.Max(1, maxEntries));
        if (TrimEntries() > 0)
        {
            MarkDirty(forcePersist: true);
        }
    }

    public void Persist()
    {
        CancelPendingPersist();
        PersistCore();
    }

    internal int EntryCount => _entries.Count;

    internal Task WaitForPendingPersistenceAsync()
    {
        lock (_persistSync)
        {
            return _pendingPersistTask;
        }
    }

    private void LoadPersistedEntries()
    {
        if (_stateStore is null)
        {
            return;
        }

        foreach (var entry in _stateStore.LoadEntries().Reverse())
        {
            if (string.IsNullOrWhiteSpace(entry.Path) || entry.Width == 0 || entry.Height == 0)
            {
                continue;
            }

            var key = new DesktopImageDimensionCacheKey(entry.Path, entry.SizeBytes, entry.SourceStampTicks);
            _entries[key] = CreateCompletedEntry(new DesktopImageDimensions(entry.Width, entry.Height));
        }

        if (TrimEntries() > 0)
        {
            Interlocked.Exchange(ref _changeVersion, 1);
            SchedulePersist(immediate: true);
        }
    }

    private void RemoveStaleEntries(DesktopImageDimensionCacheKey currentKey)
    {
        var removedAny = false;
        foreach (var entry in _entries.Keys)
        {
            if (ImageDimensionCacheKeyComparer.Instance.Equals(entry, currentKey))
            {
                continue;
            }

            if (PathComparison.Comparer.Equals(entry.Path, currentKey.Path))
            {
                removedAny |= _entries.TryRemove(entry, out _);
            }
        }

        if (removedAny)
        {
            MarkDirty();
        }
    }

    private int TrimEntries()
    {
        var overflow = _entries.Count - Math.Max(1, Volatile.Read(ref _maxEntries));
        if (overflow <= 0)
        {
            return 0;
        }

        var oldestKeys = _entries
            .OrderBy(static pair => pair.Value.AccessStamp)
            .Take(overflow)
            .Select(static pair => pair.Key)
            .ToArray();

        var removedCount = 0;
        foreach (var key in oldestKeys)
        {
            if (_entries.TryRemove(key, out _))
            {
                removedCount++;
            }
        }

        return removedCount;
    }

    private void MarkDirty(bool forcePersist = false)
    {
        var currentVersion = Interlocked.Increment(ref _changeVersion);
        if (_stateStore is null)
        {
            return;
        }

        var savedVersion = Volatile.Read(ref _savedVersion);
        if (forcePersist
            || _entries.Count <= Math.Min(32, Volatile.Read(ref _maxEntries))
            || currentVersion - savedVersion >= PersistMutationInterval)
        {
            SchedulePersist(forcePersist);
        }
    }

    private void SchedulePersist(bool immediate)
    {
        if (_stateStore is null)
        {
            return;
        }

        CancellationTokenSource? previousCancellationTokenSource = null;
        lock (_persistSync)
        {
            previousCancellationTokenSource = _pendingPersistCancellationTokenSource;
            var cancellationTokenSource = new CancellationTokenSource();
            _pendingPersistCancellationTokenSource = cancellationTokenSource;
            _pendingPersistTask = RunPersistAsync(cancellationTokenSource, immediate);
        }

        if (previousCancellationTokenSource is not null)
        {
            previousCancellationTokenSource.Cancel();
            previousCancellationTokenSource.Dispose();
        }
    }

    private async Task RunPersistAsync(CancellationTokenSource cancellationTokenSource, bool immediate)
    {
        try
        {
            if (!immediate && _persistDelayMilliseconds > 0)
            {
                await Task.Delay(_persistDelayMilliseconds, cancellationTokenSource.Token).ConfigureAwait(false);
            }

            var persistedVersion = PersistCore();
            if (persistedVersion >= 0
                && Volatile.Read(ref _changeVersion) != persistedVersion
                && !cancellationTokenSource.IsCancellationRequested)
            {
                SchedulePersist(immediate: false);
            }
        }
        catch (OperationCanceledException) when (cancellationTokenSource.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_persistSync)
            {
                if (ReferenceEquals(_pendingPersistCancellationTokenSource, cancellationTokenSource))
                {
                    _pendingPersistCancellationTokenSource = null;
                    _pendingPersistTask = Task.CompletedTask;
                }
            }

            cancellationTokenSource.Dispose();
        }
    }

    private void CancelPendingPersist()
    {
        CancellationTokenSource? cancellationTokenSource = null;
        lock (_persistSync)
        {
            cancellationTokenSource = _pendingPersistCancellationTokenSource;
            _pendingPersistCancellationTokenSource = null;
            _pendingPersistTask = Task.CompletedTask;
        }

        if (cancellationTokenSource is not null)
        {
            cancellationTokenSource.Cancel();
            cancellationTokenSource.Dispose();
        }
    }

    private int PersistCore()
    {
        if (_stateStore is null)
        {
            return -1;
        }

        lock (_persistExecutionSync)
        {
            var currentVersion = Volatile.Read(ref _changeVersion);
            if (currentVersion == Volatile.Read(ref _savedVersion))
            {
                return currentVersion;
            }

            var snapshot = _entries
                .Where(static pair => pair.Value.Value.IsValueCreated && pair.Value.Value.Value is not null)
                .OrderByDescending(static pair => pair.Value.AccessStamp)
                .Select(static pair => new DesktopImageDimensionStateEntry(
                    pair.Key.Path,
                    pair.Key.SizeBytes,
                    pair.Key.SourceStampTicks,
                    pair.Value.Value.Value!.Value.Width,
                    pair.Value.Value.Value.Value.Height))
                .ToArray();

            if (!_stateStore.SaveEntries(snapshot))
            {
                return -1;
            }

            Interlocked.Exchange(ref _savedVersion, currentVersion);
            return currentVersion;
        }
    }

    private long NextAccessStamp()
    {
        return Interlocked.Increment(ref _accessStamp);
    }

    private void Touch(CacheEntry entry)
    {
        Interlocked.Exchange(ref entry.AccessStamp, NextAccessStamp());
    }

    private CacheEntry CreateCompletedEntry(DesktopImageDimensions dimensions)
    {
        return new CacheEntry(
            new Lazy<DesktopImageDimensions?>(() => dimensions, LazyThreadSafetyMode.ExecutionAndPublication),
            NextAccessStamp());
    }

    private sealed class CacheEntry(Lazy<DesktopImageDimensions?> value, long accessStamp)
    {
        public Lazy<DesktopImageDimensions?> Value { get; } = value;

        public long AccessStamp = accessStamp;
    }

    private sealed class ImageDimensionCacheKeyComparer : IEqualityComparer<DesktopImageDimensionCacheKey>
    {
        public static ImageDimensionCacheKeyComparer Instance { get; } = new();

        public bool Equals(DesktopImageDimensionCacheKey x, DesktopImageDimensionCacheKey y)
        {
            return PathComparison.Comparer.Equals(x.Path, y.Path)
                && x.SizeBytes == y.SizeBytes
                && x.SourceStampTicks == y.SourceStampTicks;
        }

        public int GetHashCode(DesktopImageDimensionCacheKey obj)
        {
            var hash = new HashCode();
            hash.Add(obj.Path, PathComparison.Comparer);
            hash.Add(obj.SizeBytes);
            hash.Add(obj.SourceStampTicks);
            return hash.ToHashCode();
        }
    }
}
