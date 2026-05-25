using System.Collections.Concurrent;
using ModernImageViewer.Core;

namespace ModernImageViewer.Desktop.Services;

internal readonly record struct PreviewImageCacheKey(
    string Path,
    long SourceSizeBytes,
    long SourceStampTicks,
    int MaxLongEdgePixels,
    bool PreferCompatibilityDecoder);

internal sealed record PreviewImageCacheEntry(
    byte[]? EncodedBytes,
    string StatusText,
    string DetailsText,
    bool Cacheable = true)
{
    public int SizeBytes => EncodedBytes?.Length ?? 0;

    public bool HasBitmap => EncodedBytes is { Length: > 0 };

    public bool ShouldCache => Cacheable && HasBitmap;
}

internal enum PreviewCacheMaintenanceMode
{
    None = 0,
    Immediate = 1,
    Background = 2
}

internal sealed class PreviewImageCacheStore
{
    private long _maxBytes;
    private readonly PreviewImageDiskCacheStore? _diskCache;
    private readonly object _syncRoot = new();
    private readonly Dictionary<PreviewImageCacheKey, CachedEntry> _entries = new(PreviewImageCacheKeyComparer.Instance);
    private readonly ConcurrentDictionary<PreviewImageCacheKey, Lazy<PreviewImageCacheEntry>> _inflight = new(PreviewImageCacheKeyComparer.Instance);
    private long _currentBytes;
    private long _lastAccessSequence;

    public PreviewImageCacheStore(long maxBytes)
        : this(maxBytes, diskCache: null)
    {
    }

    internal PreviewImageCacheStore(long maxBytes, PreviewImageDiskCacheStore? diskCache)
    {
        _maxBytes = Math.Max(0, maxBytes);
        _diskCache = diskCache;
    }

    internal int EntryCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _entries.Count;
            }
        }
    }

    internal long CurrentBytes
    {
        get
        {
            lock (_syncRoot)
            {
                return _currentBytes;
            }
        }
    }

    public void UpdateBudgets(long maxBytes, long diskMaxBytes)
    {
        UpdateBudgets(maxBytes, diskMaxBytes, diskMaxBytes, PreviewCacheMaintenanceMode.Immediate);
    }

    public void UpdateBudgets(long maxBytes, long previewDiskMaxBytes, long thumbnailDiskMaxBytes)
    {
        UpdateBudgets(maxBytes, previewDiskMaxBytes, thumbnailDiskMaxBytes, PreviewCacheMaintenanceMode.Immediate);
    }

    internal void UpdateBudgets(
        long maxBytes,
        long previewDiskMaxBytes,
        long thumbnailDiskMaxBytes,
        PreviewCacheMaintenanceMode maintenanceMode)
    {
        lock (_syncRoot)
        {
            _maxBytes = Math.Max(0, maxBytes);
            TrimToBudgetLocked();
        }

        if (_diskCache is null)
        {
            return;
        }

        switch (maintenanceMode)
        {
            case PreviewCacheMaintenanceMode.None:
                _diskCache.ApplyBudgetsWithoutMaintenance(previewDiskMaxBytes, thumbnailDiskMaxBytes);
                break;
            case PreviewCacheMaintenanceMode.Background:
                _diskCache.UpdateBudgetsInBackground(previewDiskMaxBytes, thumbnailDiskMaxBytes);
                break;
            default:
                _diskCache.UpdateBudgets(previewDiskMaxBytes, thumbnailDiskMaxBytes);
                break;
        }
    }

    public PreviewImageCacheEntry GetOrLoad(PreviewImageCacheKey key, Func<PreviewImageCacheEntry> loader)
    {
        ArgumentNullException.ThrowIfNull(loader);

        lock (_syncRoot)
        {
            if (_entries.TryGetValue(key, out var cachedEntry))
            {
                cachedEntry.Touch(++_lastAccessSequence);
                return cachedEntry.Entry;
            }
        }

        if (_diskCache?.TryLoad(key) is { } diskEntry)
        {
            CacheEntryIfPossible(key, diskEntry);
            return diskEntry;
        }

        var lazyEntry = _inflight.GetOrAdd(
            key,
            static (_, state) => new Lazy<PreviewImageCacheEntry>(state, LazyThreadSafetyMode.ExecutionAndPublication),
            loader);

        try
        {
            var loadedEntry = lazyEntry.Value;
            CacheEntryIfPossible(key, loadedEntry);
            _diskCache?.TrySave(key, loadedEntry);
            return loadedEntry;
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }

    private void CacheEntryIfPossible(PreviewImageCacheKey key, PreviewImageCacheEntry loadedEntry)
    {
        if (!loadedEntry.ShouldCache || _maxBytes <= 0)
        {
            return;
        }

        lock (_syncRoot)
        {
            if (loadedEntry.SizeBytes > _maxBytes)
            {
                return;
            }

            if (_entries.TryGetValue(key, out var cachedEntry))
            {
                cachedEntry.Touch(++_lastAccessSequence);
                return;
            }

            var sequence = ++_lastAccessSequence;
            _entries[key] = new CachedEntry(loadedEntry, sequence);
            _currentBytes += loadedEntry.SizeBytes;
            TrimToBudgetLocked();
        }
    }

    private void TrimToBudgetLocked()
    {
        if (_currentBytes <= _maxBytes)
        {
            return;
        }

        foreach (var candidate in _entries
                     .OrderBy(static pair => pair.Value.LastAccessSequence)
                     .ToArray())
        {
            if (_currentBytes <= _maxBytes)
            {
                break;
            }

            if (_entries.Remove(candidate.Key, out var removedEntry))
            {
                _currentBytes -= removedEntry.Entry.SizeBytes;
            }
        }
    }

    private sealed class CachedEntry
    {
        public CachedEntry(PreviewImageCacheEntry entry, long lastAccessSequence)
        {
            Entry = entry;
            LastAccessSequence = lastAccessSequence;
        }

        public PreviewImageCacheEntry Entry { get; }

        public long LastAccessSequence { get; private set; }

        public void Touch(long sequence)
        {
            LastAccessSequence = sequence;
        }
    }

    private sealed class PreviewImageCacheKeyComparer : IEqualityComparer<PreviewImageCacheKey>
    {
        public static PreviewImageCacheKeyComparer Instance { get; } = new();

        public bool Equals(PreviewImageCacheKey x, PreviewImageCacheKey y)
        {
            return PathComparison.Comparer.Equals(x.Path, y.Path)
                && x.SourceSizeBytes == y.SourceSizeBytes
                && x.SourceStampTicks == y.SourceStampTicks
                && x.MaxLongEdgePixels == y.MaxLongEdgePixels
                && x.PreferCompatibilityDecoder == y.PreferCompatibilityDecoder;
        }

        public int GetHashCode(PreviewImageCacheKey obj)
        {
            var hash = new HashCode();
            hash.Add(obj.Path, PathComparison.Comparer);
            hash.Add(obj.SourceSizeBytes);
            hash.Add(obj.SourceStampTicks);
            hash.Add(obj.MaxLongEdgePixels);
            hash.Add(obj.PreferCompatibilityDecoder);
            return hash.ToHashCode();
        }
    }
}
