using System.Collections.Concurrent;
using ModernImageViewer.Core;

namespace ModernImageViewer.Desktop.Services;

internal enum DesktopImageFingerprintKind
{
    SampleHash,
    FullHash
}

internal readonly record struct DesktopImageFingerprintKey(
    string Path,
    long SizeBytes,
    long SourceStampTicks);

internal sealed class DesktopImageFingerprintCacheStore
{
    private readonly DesktopImageFingerprintStateStore? _stateStore;
    private readonly ConcurrentDictionary<(DesktopImageFingerprintKind Kind, DesktopImageFingerprintKey Key), CacheEntry<string>> _textValues =
        new(ImageFingerprintTextKeyComparer.Instance);

    private readonly ConcurrentDictionary<DesktopImageFingerprintKey, CacheEntry<ulong>> _differenceHashes =
        new(ImageFingerprintKeyComparer.Instance);

    private int _maxTextEntries;
    private int _maxDifferenceHashEntries;
    private long _accessStamp;
    private int _textChangeVersion;
    private int _textSavedVersion;
    private int _differenceHashChangeVersion;
    private int _differenceHashSavedVersion;

    public DesktopImageFingerprintCacheStore()
        : this(
            new DesktopImageFingerprintStateStore(),
            DesktopImageProcessingPolicy.FingerprintTextCacheEntryLimit,
            DesktopImageProcessingPolicy.FingerprintDifferenceHashCacheEntryLimit)
    {
    }

    internal DesktopImageFingerprintCacheStore(DesktopImageFingerprintStateStore? stateStore)
        : this(
            stateStore,
            DesktopImageProcessingPolicy.FingerprintTextCacheEntryLimit,
            DesktopImageProcessingPolicy.FingerprintDifferenceHashCacheEntryLimit)
    {
    }

    internal DesktopImageFingerprintCacheStore(
        DesktopImageFingerprintStateStore? stateStore,
        int maxTextEntries,
        int maxDifferenceHashEntries)
    {
        _stateStore = stateStore;
        _maxTextEntries = Math.Max(1, maxTextEntries);
        _maxDifferenceHashEntries = Math.Max(1, maxDifferenceHashEntries);
        LoadPersistedEntries();
    }

    public string GetOrAddText(
        DesktopImageFingerprintKind kind,
        string path,
        long sizeBytes,
        long sourceStampTicks,
        Func<string> loader)
    {
        ArgumentNullException.ThrowIfNull(loader);

        var key = (kind, new DesktopImageFingerprintKey(path, sizeBytes, sourceStampTicks));
        var newEntry = new CacheEntry<string>(
            new Lazy<string>(loader, LazyThreadSafetyMode.ExecutionAndPublication),
            NextAccessStamp());
        var entry = _textValues.GetOrAdd(key, newEntry);
        var created = ReferenceEquals(entry, newEntry);
        Touch(entry);

        try
        {
            var value = entry.Value.Value;
            if (created)
            {
                RemoveStaleTextEntries(key);
                TrimTextEntries();
                Interlocked.Increment(ref _textChangeVersion);
            }

            return value;
        }
        catch
        {
            if (created)
            {
                _textValues.TryRemove(key, out _);
            }

            throw;
        }
    }

    public ulong GetOrAddDifferenceHash(
        string path,
        long sizeBytes,
        long sourceStampTicks,
        Func<ulong> loader)
    {
        ArgumentNullException.ThrowIfNull(loader);

        var key = new DesktopImageFingerprintKey(path, sizeBytes, sourceStampTicks);
        var newEntry = new CacheEntry<ulong>(
            new Lazy<ulong>(loader, LazyThreadSafetyMode.ExecutionAndPublication),
            NextAccessStamp());
        var entry = _differenceHashes.GetOrAdd(key, newEntry);
        var created = ReferenceEquals(entry, newEntry);
        Touch(entry);

        try
        {
            var value = entry.Value.Value;
            if (created)
            {
                RemoveStaleDifferenceHashes(key);
                TrimDifferenceHashes();
                Interlocked.Increment(ref _differenceHashChangeVersion);
            }

            return value;
        }
        catch
        {
            if (created)
            {
                _differenceHashes.TryRemove(key, out _);
            }

            throw;
        }
    }

    public void UpdateEntryLimits(int maxTextEntries, int maxDifferenceHashEntries)
    {
        Volatile.Write(ref _maxTextEntries, Math.Max(1, maxTextEntries));
        Volatile.Write(ref _maxDifferenceHashEntries, Math.Max(1, maxDifferenceHashEntries));

        var removedTextEntries = TrimTextEntries();
        var removedDifferenceHashes = TrimDifferenceHashes();
        if (removedTextEntries > 0)
        {
            Interlocked.Increment(ref _textChangeVersion);
        }

        if (removedDifferenceHashes > 0)
        {
            Interlocked.Increment(ref _differenceHashChangeVersion);
        }
    }

    internal (int TextEntryCount, int DifferenceHashEntryCount) GetEntryCounts()
    {
        return (_textValues.Count, _differenceHashes.Count);
    }

    public void Persist()
    {
        if (_stateStore is null)
        {
            return;
        }

        PersistTextEntries();
        PersistDifferenceHashes();
    }

    private void LoadPersistedEntries()
    {
        if (_stateStore is null)
        {
            return;
        }

        foreach (var entry in _stateStore.LoadTextEntries())
        {
            if (string.IsNullOrWhiteSpace(entry.Path) || string.IsNullOrWhiteSpace(entry.Value))
            {
                continue;
            }

            var key = (entry.Kind, new DesktopImageFingerprintKey(entry.Path, entry.SizeBytes, entry.SourceStampTicks));
            _textValues[key] = CreateCompletedEntry(entry.Value);
        }

        foreach (var entry in _stateStore.LoadDifferenceHashEntries())
        {
            if (string.IsNullOrWhiteSpace(entry.Path))
            {
                continue;
            }

            var key = new DesktopImageFingerprintKey(entry.Path, entry.SizeBytes, entry.SourceStampTicks);
            _differenceHashes[key] = CreateCompletedEntry(entry.Value);
        }

        var removedTextEntries = TrimTextEntries();
        var removedDifferenceHashes = TrimDifferenceHashes();
        if (removedTextEntries > 0)
        {
            Interlocked.Exchange(ref _textChangeVersion, 1);
        }

        if (removedDifferenceHashes > 0)
        {
            Interlocked.Exchange(ref _differenceHashChangeVersion, 1);
        }
    }

    private void PersistTextEntries()
    {
        var currentVersion = Volatile.Read(ref _textChangeVersion);
        if (currentVersion == Volatile.Read(ref _textSavedVersion))
        {
            return;
        }

        var snapshot = _textValues
            .Where(static pair => pair.Value.Value.IsValueCreated)
            .OrderByDescending(static pair => pair.Value.AccessStamp)
            .Select(static pair => new DesktopImageFingerprintTextEntry(
                pair.Key.Kind,
                pair.Key.Key.Path,
                pair.Key.Key.SizeBytes,
                pair.Key.Key.SourceStampTicks,
                pair.Value.Value.Value))
            .ToArray();

        if (_stateStore?.SaveTextEntries(snapshot) == true)
        {
            Interlocked.Exchange(ref _textSavedVersion, currentVersion);
        }
    }

    private void PersistDifferenceHashes()
    {
        var currentVersion = Volatile.Read(ref _differenceHashChangeVersion);
        if (currentVersion == Volatile.Read(ref _differenceHashSavedVersion))
        {
            return;
        }

        var snapshot = _differenceHashes
            .Where(static pair => pair.Value.Value.IsValueCreated)
            .OrderByDescending(static pair => pair.Value.AccessStamp)
            .Select(static pair => new DesktopImageFingerprintDifferenceHashEntry(
                pair.Key.Path,
                pair.Key.SizeBytes,
                pair.Key.SourceStampTicks,
                pair.Value.Value.Value))
            .ToArray();

        if (_stateStore?.SaveDifferenceHashEntries(snapshot) == true)
        {
            Interlocked.Exchange(ref _differenceHashSavedVersion, currentVersion);
        }
    }

    private void RemoveStaleTextEntries((DesktopImageFingerprintKind Kind, DesktopImageFingerprintKey Key) currentKey)
    {
        foreach (var entry in _textValues.Keys)
        {
            if (entry.Kind != currentKey.Kind)
            {
                continue;
            }

            if (ImageFingerprintKeyComparer.Instance.Equals(entry.Key, currentKey.Key))
            {
                continue;
            }

            if (PathComparison.Comparer.Equals(entry.Key.Path, currentKey.Key.Path))
            {
                _textValues.TryRemove(entry, out _);
            }
        }
    }

    private int TrimTextEntries()
    {
        return TrimEntries(
            _textValues,
            Volatile.Read(ref _maxTextEntries),
            static pair => pair.Value.AccessStamp);
    }

    private void RemoveStaleDifferenceHashes(DesktopImageFingerprintKey currentKey)
    {
        foreach (var entry in _differenceHashes.Keys)
        {
            if (ImageFingerprintKeyComparer.Instance.Equals(entry, currentKey))
            {
                continue;
            }

            if (PathComparison.Comparer.Equals(entry.Path, currentKey.Path))
            {
                _differenceHashes.TryRemove(entry, out _);
            }
        }
    }

    private int TrimDifferenceHashes()
    {
        return TrimEntries(
            _differenceHashes,
            Volatile.Read(ref _maxDifferenceHashEntries),
            static pair => pair.Value.AccessStamp);
    }

    private int TrimEntries<TKey, TValue>(
        ConcurrentDictionary<TKey, CacheEntry<TValue>> entries,
        int maxEntries,
        Func<KeyValuePair<TKey, CacheEntry<TValue>>, long> accessStampSelector)
        where TKey : notnull
    {
        var overflow = entries.Count - Math.Max(1, maxEntries);
        if (overflow <= 0)
        {
            return 0;
        }

        var removedCount = 0;
        var oldestKeys = entries
            .OrderBy(accessStampSelector)
            .Take(overflow)
            .Select(static pair => pair.Key)
            .ToArray();

        foreach (var key in oldestKeys)
        {
            if (entries.TryRemove(key, out _))
            {
                removedCount++;
            }
        }

        return removedCount;
    }

    private CacheEntry<T> CreateCompletedEntry<T>(T value)
    {
        var lazyValue = new Lazy<T>(() => value, LazyThreadSafetyMode.ExecutionAndPublication);
        _ = lazyValue.Value;
        return new CacheEntry<T>(lazyValue, NextAccessStamp());
    }

    private long NextAccessStamp()
    {
        return Interlocked.Increment(ref _accessStamp);
    }

    private void Touch<T>(CacheEntry<T> entry)
    {
        Interlocked.Exchange(ref entry.AccessStamp, NextAccessStamp());
    }

    private sealed class CacheEntry<T>(Lazy<T> value, long accessStamp)
    {
        public Lazy<T> Value { get; } = value;

        public long AccessStamp = accessStamp;
    }

    private sealed class ImageFingerprintKeyComparer : IEqualityComparer<DesktopImageFingerprintKey>
    {
        public static ImageFingerprintKeyComparer Instance { get; } = new();

        public bool Equals(DesktopImageFingerprintKey x, DesktopImageFingerprintKey y)
        {
            return PathComparison.Comparer.Equals(x.Path, y.Path)
                && x.SizeBytes == y.SizeBytes
                && x.SourceStampTicks == y.SourceStampTicks;
        }

        public int GetHashCode(DesktopImageFingerprintKey obj)
        {
            var hash = new HashCode();
            hash.Add(obj.Path, PathComparison.Comparer);
            hash.Add(obj.SizeBytes);
            hash.Add(obj.SourceStampTicks);
            return hash.ToHashCode();
        }
    }

    private sealed class ImageFingerprintTextKeyComparer : IEqualityComparer<(DesktopImageFingerprintKind Kind, DesktopImageFingerprintKey Key)>
    {
        public static ImageFingerprintTextKeyComparer Instance { get; } = new();

        public bool Equals(
            (DesktopImageFingerprintKind Kind, DesktopImageFingerprintKey Key) x,
            (DesktopImageFingerprintKind Kind, DesktopImageFingerprintKey Key) y)
        {
            return x.Kind == y.Kind && ImageFingerprintKeyComparer.Instance.Equals(x.Key, y.Key);
        }

        public int GetHashCode((DesktopImageFingerprintKind Kind, DesktopImageFingerprintKey Key) obj)
        {
            var hash = new HashCode();
            hash.Add(obj.Kind);
            hash.Add(ImageFingerprintKeyComparer.Instance.GetHashCode(obj.Key));
            return hash.ToHashCode();
        }
    }
}
