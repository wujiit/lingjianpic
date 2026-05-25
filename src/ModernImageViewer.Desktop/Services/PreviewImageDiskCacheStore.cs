using System.Security.Cryptography;
using System.Text;
using ModernImageViewer.Core;

namespace ModernImageViewer.Desktop.Services;

internal sealed class PreviewImageDiskCacheStore
{
    private const string CacheFileExtension = ".bin";
    private readonly object _syncRoot = new();
    private readonly string _cacheDirectory;
    private long _previewMaxBytes;
    private long _thumbnailMaxBytes;

    private enum CacheBucket
    {
        Preview,
        Thumbnail
    }

    public PreviewImageDiskCacheStore(long maxBytes)
        : this(GetDefaultCacheDirectory(), maxBytes, maxBytes)
    {
    }

    public PreviewImageDiskCacheStore(long previewMaxBytes, long thumbnailMaxBytes)
        : this(GetDefaultCacheDirectory(), previewMaxBytes, thumbnailMaxBytes)
    {
    }

    internal PreviewImageDiskCacheStore(string cacheDirectory, long maxBytes)
        : this(cacheDirectory, maxBytes, maxBytes)
    {
    }

    internal PreviewImageDiskCacheStore(string cacheDirectory, long previewMaxBytes, long thumbnailMaxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        _cacheDirectory = cacheDirectory;
        _previewMaxBytes = Math.Max(0, previewMaxBytes);
        _thumbnailMaxBytes = Math.Max(0, thumbnailMaxBytes);
    }

    public PreviewImageCacheEntry? TryLoad(PreviewImageCacheKey key)
    {
        var bucket = ResolveBucket(key);
        var cachePath = GetCachePath(key, bucket);
        try
        {
            var maxBytes = GetBucketBudget(bucket);
            if (maxBytes <= 0 || !File.Exists(cachePath))
            {
                return null;
            }

            var fileInfo = new FileInfo(cachePath);
            if (fileInfo.Length <= 0)
            {
                TryDelete(cachePath);
                return null;
            }

            if (fileInfo.Length > maxBytes)
            {
                TryDelete(cachePath);
                return null;
            }

            var bytes = File.ReadAllBytes(cachePath);
            if (bytes.Length == 0)
            {
                TryDelete(cachePath);
                return null;
            }

            TryTouch(cachePath);
            return new PreviewImageCacheEntry(
                bytes,
                "\u9884\u89C8\u5DF2\u52A0\u8F7D",
                "\u5F53\u524D\u590D\u7528\u4E86\u672C\u5730\u78C1\u76D8\u7F13\u5B58\u3002");
        }
        catch
        {
            return null;
        }
    }

    public void TrySave(PreviewImageCacheKey key, PreviewImageCacheEntry entry)
    {
        var bucket = ResolveBucket(key);
        var maxBytes = GetBucketBudget(bucket);
        if (maxBytes <= 0 || !entry.ShouldCache || entry.EncodedBytes is not { Length: > 0 } encodedBytes)
        {
            return;
        }

        try
        {
            lock (_syncRoot)
            {
                maxBytes = GetBucketBudget(bucket);
                if (maxBytes <= 0 || encodedBytes.Length > maxBytes)
                {
                    return;
                }

                Directory.CreateDirectory(GetBucketDirectory(bucket));
                var cachePath = GetCachePath(key, bucket);
                DesktopFileStreamFactory.WriteAtomically(cachePath, stream =>
                {
                    stream.Write(encodedBytes, 0, encodedBytes.Length);
                });

                TrimToBudgetLocked(bucket, cachePath);
            }
        }
        catch
        {
        }
    }

    public void UpdateBudget(long maxBytes)
    {
        UpdateBudgets(maxBytes, maxBytes);
    }

    public void UpdateBudgets(long previewMaxBytes, long thumbnailMaxBytes)
    {
        SetBudgets(previewMaxBytes, thumbnailMaxBytes);
        RunBudgetMaintenance();
    }

    internal void ApplyBudgetsWithoutMaintenance(long previewMaxBytes, long thumbnailMaxBytes)
    {
        SetBudgets(previewMaxBytes, thumbnailMaxBytes);
    }

    internal void UpdateBudgetsInBackground(long previewMaxBytes, long thumbnailMaxBytes)
    {
        SetBudgets(previewMaxBytes, thumbnailMaxBytes);
        _ = Task.Run(() =>
        {
            try
            {
                RunBudgetMaintenance();
            }
            catch
            {
            }
        });
    }

    internal int GetCacheFileCount()
    {
        lock (_syncRoot)
        {
            return EnumerateCacheFiles(CacheBucket.Preview).Length
                + EnumerateCacheFiles(CacheBucket.Thumbnail).Length;
        }
    }

    internal long GetCurrentBytes()
    {
        lock (_syncRoot)
        {
            return EnumerateCacheFiles(CacheBucket.Preview).Sum(static file => SafeGetLength(file))
                + EnumerateCacheFiles(CacheBucket.Thumbnail).Sum(static file => SafeGetLength(file));
        }
    }

    private static string GetDefaultCacheDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ModernImageViewer.Desktop",
            "preview-cache");
    }

    private void SetBudgets(long previewMaxBytes, long thumbnailMaxBytes)
    {
        lock (_syncRoot)
        {
            _previewMaxBytes = Math.Max(0, previewMaxBytes);
            _thumbnailMaxBytes = Math.Max(0, thumbnailMaxBytes);
        }
    }

    private void RunBudgetMaintenance()
    {
        lock (_syncRoot)
        {
            ApplyBudgetUpdateLocked(CacheBucket.Preview);
            ApplyBudgetUpdateLocked(CacheBucket.Thumbnail);
        }
    }

    private void ApplyBudgetUpdateLocked(CacheBucket bucket)
    {
        if (GetBucketBudget(bucket) <= 0)
        {
            foreach (var file in EnumerateCacheFiles(bucket))
            {
                TryDelete(file.FullName);
            }

            return;
        }

        TrimToBudgetLocked(bucket, string.Empty);
    }

    private string GetCachePath(PreviewImageCacheKey key, CacheBucket bucket)
    {
        var keyText = string.Create(
            key.Path.Length + 96,
            key,
            static (buffer, state) =>
            {
                var written = 0;
                state.Path.AsSpan().CopyTo(buffer);
                written += state.Path.Length;
                buffer[written++] = '|';
                written += state.SourceSizeBytes.TryFormat(buffer[written..], out var sizeWritten) ? sizeWritten : 0;
                buffer[written++] = '|';
                written += state.SourceStampTicks.TryFormat(buffer[written..], out var stampWritten) ? stampWritten : 0;
                buffer[written++] = '|';
                written += state.MaxLongEdgePixels.TryFormat(buffer[written..], out var longEdgeWritten) ? longEdgeWritten : 0;
                buffer[written++] = '|';
                buffer[written] = state.PreferCompatibilityDecoder ? '1' : '0';
            });

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(keyText));
        var fileName = Convert.ToHexString(hashBytes) + CacheFileExtension;
        return Path.Combine(GetBucketDirectory(bucket), fileName);
    }

    private void TrimToBudgetLocked(CacheBucket bucket, string preferredPath)
    {
        var files = EnumerateCacheFiles(bucket);
        if (files.Length == 0)
        {
            return;
        }

        var budget = GetBucketBudget(bucket);
        if (budget <= 0)
        {
            foreach (var file in files)
            {
                TryDelete(file.FullName);
            }

            return;
        }

        var currentBytes = files.Sum(static file => SafeGetLength(file));
        if (currentBytes <= budget)
        {
            return;
        }

        foreach (var file in files.OrderBy(static file => file.LastWriteTimeUtc))
        {
            if (currentBytes <= budget)
            {
                break;
            }

            var isPreferred = string.Equals(file.FullName, preferredPath, PathComparison.Comparison);
            if (isPreferred && files.Length > 1 && currentBytes - SafeGetLength(file) > 0)
            {
                continue;
            }

            var fileLength = SafeGetLength(file);
            TryDelete(file.FullName);
            currentBytes -= fileLength;
        }
    }

    private FileInfo[] EnumerateCacheFiles(CacheBucket bucket)
    {
        var cacheDirectory = GetBucketDirectory(bucket);
        try
        {
            if (!Directory.Exists(cacheDirectory))
            {
                return [];
            }

            return new DirectoryInfo(cacheDirectory)
                .EnumerateFiles($"*{CacheFileExtension}", SearchOption.TopDirectoryOnly)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private long GetBucketBudget(CacheBucket bucket)
    {
        return bucket == CacheBucket.Thumbnail
            ? Volatile.Read(ref _thumbnailMaxBytes)
            : Volatile.Read(ref _previewMaxBytes);
    }

    private string GetBucketDirectory(CacheBucket bucket)
    {
        return Path.Combine(
            _cacheDirectory,
            bucket == CacheBucket.Thumbnail ? "thumbnail" : "preview");
    }

    private static CacheBucket ResolveBucket(PreviewImageCacheKey key)
    {
        return key.PreferCompatibilityDecoder ? CacheBucket.Thumbnail : CacheBucket.Preview;
    }

    private static long SafeGetLength(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch
        {
            return 0;
        }
    }

    private static void TryTouch(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
