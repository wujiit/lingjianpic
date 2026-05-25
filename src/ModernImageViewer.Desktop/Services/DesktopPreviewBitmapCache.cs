using Avalonia.Media.Imaging;
using ModernImageViewer.Core;

namespace ModernImageViewer.Desktop.Services;

internal readonly record struct DesktopPreviewBitmapCacheHit(
    Bitmap Bitmap,
    int DecodeLongEdge,
    string StatusText,
    string DetailsText);

internal sealed class DesktopPreviewBitmapCache : IDisposable
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, CachedEntry> _entries = new(PathComparison.Comparer);
    private readonly int _capacity;
    private long _lastAccessSequence;
    private string? _pinnedPath;

    public DesktopPreviewBitmapCache(int capacity)
    {
        _capacity = Math.Max(1, capacity);
    }

    public bool TryGet(string path, int requestedLongEdge, out DesktopPreviewBitmapCacheHit hit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        lock (_syncRoot)
        {
            if (_entries.TryGetValue(path, out var entry) && entry.DecodeLongEdge >= requestedLongEdge)
            {
                entry.Touch(++_lastAccessSequence);
                hit = entry.ToHit();
                return true;
            }
        }

        hit = default;
        return false;
    }

    public bool TryGetBestAvailable(string path, out DesktopPreviewBitmapCacheHit hit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        lock (_syncRoot)
        {
            if (_entries.TryGetValue(path, out var entry))
            {
                entry.Touch(++_lastAccessSequence);
                hit = entry.ToHit();
                return true;
            }
        }

        hit = default;
        return false;
    }

    public DesktopPreviewBitmapCacheHit StoreOrUpdate(
        string path,
        int decodeLongEdge,
        Bitmap bitmap,
        string statusText,
        string detailsText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(bitmap);

        lock (_syncRoot)
        {
            if (_entries.TryGetValue(path, out var existingEntry))
            {
                if (existingEntry.DecodeLongEdge >= decodeLongEdge)
                {
                    if (!ReferenceEquals(existingEntry.Bitmap, bitmap))
                    {
                        bitmap.Dispose();
                    }

                    existingEntry.Touch(++_lastAccessSequence);
                    return existingEntry.ToHit();
                }

                _entries.Remove(path);
                if (!ReferenceEquals(existingEntry.Bitmap, bitmap)
                    && !string.Equals(path, _pinnedPath, PathComparison.Comparison))
                {
                    existingEntry.Bitmap.Dispose();
                }
            }

            var entry = new CachedEntry(
                bitmap,
                Math.Max(0, decodeLongEdge),
                statusText,
                detailsText,
                ++_lastAccessSequence);
            _entries[path] = entry;
            TrimToCapacityLocked();
            return entry.ToHit();
        }
    }

    public bool ContainsBitmap(Bitmap? bitmap)
    {
        if (bitmap is null)
        {
            return false;
        }

        lock (_syncRoot)
        {
            return _entries.Values.Any(entry => ReferenceEquals(entry.Bitmap, bitmap));
        }
    }

    public void SetPinnedPath(string? path)
    {
        lock (_syncRoot)
        {
            _pinnedPath = string.IsNullOrWhiteSpace(path) ? null : path;
        }
    }

    public void Clear()
    {
        lock (_syncRoot)
        {
            foreach (var entry in _entries.Values)
            {
                entry.Bitmap.Dispose();
            }

            _entries.Clear();
            _pinnedPath = null;
        }
    }

    public void Dispose()
    {
        Clear();
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

    private void TrimToCapacityLocked()
    {
        while (_entries.Count > _capacity)
        {
            var evictionCandidate = _entries
                .Where(pair => !string.Equals(pair.Key, _pinnedPath, PathComparison.Comparison))
                .OrderBy(pair => pair.Value.LastAccessSequence)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(evictionCandidate.Key))
            {
                break;
            }

            _entries.Remove(evictionCandidate.Key);
            evictionCandidate.Value.Bitmap.Dispose();
        }
    }

    private sealed class CachedEntry(
        Bitmap bitmap,
        int decodeLongEdge,
        string statusText,
        string detailsText,
        long lastAccessSequence)
    {
        public Bitmap Bitmap { get; } = bitmap;

        public int DecodeLongEdge { get; } = decodeLongEdge;

        public string StatusText { get; } = statusText;

        public string DetailsText { get; } = detailsText;

        public long LastAccessSequence { get; private set; } = lastAccessSequence;

        public void Touch(long accessSequence)
        {
            LastAccessSequence = accessSequence;
        }

        public DesktopPreviewBitmapCacheHit ToHit()
        {
            return new DesktopPreviewBitmapCacheHit(Bitmap, DecodeLongEdge, StatusText, DetailsText);
        }
    }
}
