using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModernImageViewer.Core;

namespace ModernImageViewer.Desktop.Services;

internal enum DesktopImageMatchCacheMode
{
    ExactDuplicates = 1,
    SimilarImages = 2
}

internal sealed record DesktopExactDuplicateCacheGroup(
    long SizeBytes,
    string Hash,
    IReadOnlyList<string> Paths);

internal sealed record DesktopSimilarImageCacheGroup(
    ulong Hash,
    IReadOnlyList<string> Paths);

internal sealed record DesktopImageMatchCacheEntry(
    string CollectionSignature,
    DesktopImageMatchCacheMode Mode,
    int DistanceThreshold,
    int ScannedCount,
    int FailedCount,
    IReadOnlyList<DesktopExactDuplicateCacheGroup> ExactGroups,
    IReadOnlyList<DesktopSimilarImageCacheGroup> SimilarGroups,
    long SavedAtUtcTicks);

internal sealed record DesktopImageMatchCachedResult(
    DesktopImageMatchCacheMode Mode,
    int DistanceThreshold,
    ExactDuplicateScanResult? ExactDuplicateResult,
    SimilarImageScanResult? SimilarImageResult);

internal sealed class DesktopImageMatchResultCacheStore
{
    private const int MaxEntries = 24;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions SerializerOptions = new();

    private readonly object _syncRoot = new();
    private readonly string _storePath;
    private readonly SemaphoreSlim _saveSemaphore = new(1, 1);
    private Dictionary<string, DesktopImageMatchCacheEntry>? _entriesByKey;

    public DesktopImageMatchResultCacheStore()
        : this(GetDefaultStorePath())
    {
    }

    internal DesktopImageMatchResultCacheStore(string storePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);
        _storePath = storePath;
    }

    internal int GetEntryCount()
    {
        lock (_syncRoot)
        {
            EnsureLoadedUnsafe();
            return _entriesByKey!.Count;
        }
    }

    public bool TryLoadExact(IReadOnlyList<ImageRecord> images, out ExactDuplicateScanResult result)
    {
        ArgumentNullException.ThrowIfNull(images);

        return TryLoadExact(CreateCollectionSignature(images), out result);
    }

    internal bool TryLoadExact(string collectionSignature, out ExactDuplicateScanResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionSignature);

        lock (_syncRoot)
        {
            EnsureLoadedUnsafe();
            var key = CreateEntryKey(collectionSignature, DesktopImageMatchCacheMode.ExactDuplicates, distanceThreshold: 0);
            if (!_entriesByKey!.TryGetValue(key, out var entry))
            {
                result = new ExactDuplicateScanResult(0, 0, []);
                return false;
            }

            result = CreateExactDuplicateScanResult(entry);
            return true;
        }
    }

    public bool TryLoadSimilar(IReadOnlyList<ImageRecord> images, int distanceThreshold, out SimilarImageScanResult result)
    {
        ArgumentNullException.ThrowIfNull(images);

        return TryLoadSimilar(CreateCollectionSignature(images), distanceThreshold, out result);
    }

    internal bool TryLoadSimilar(string collectionSignature, int distanceThreshold, out SimilarImageScanResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionSignature);

        lock (_syncRoot)
        {
            EnsureLoadedUnsafe();
            var key = CreateEntryKey(collectionSignature, DesktopImageMatchCacheMode.SimilarImages, distanceThreshold);
            if (!_entriesByKey!.TryGetValue(key, out var entry))
            {
                result = new SimilarImageScanResult(0, 0, distanceThreshold, []);
                return false;
            }

            result = CreateSimilarImageScanResult(entry);
            return true;
        }
    }

    public bool TryLoadLatest(IReadOnlyList<ImageRecord> images, out DesktopImageMatchCachedResult result)
    {
        ArgumentNullException.ThrowIfNull(images);

        return TryLoadLatest(CreateCollectionSignature(images), out result);
    }

    internal bool TryLoadLatest(string collectionSignature, out DesktopImageMatchCachedResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionSignature);

        lock (_syncRoot)
        {
            EnsureLoadedUnsafe();
            var entry = _entriesByKey!.Values
                .Where(candidate => string.Equals(candidate.CollectionSignature, collectionSignature, StringComparison.Ordinal))
                .OrderByDescending(static candidate => candidate.SavedAtUtcTicks)
                .FirstOrDefault();
            if (entry is null)
            {
                result = new DesktopImageMatchCachedResult(
                    DesktopImageMatchCacheMode.ExactDuplicates,
                    0,
                    ExactDuplicateResult: null,
                    SimilarImageResult: null);
                return false;
            }

            result = entry.Mode == DesktopImageMatchCacheMode.ExactDuplicates
                ? new DesktopImageMatchCachedResult(
                    entry.Mode,
                    entry.DistanceThreshold,
                    CreateExactDuplicateScanResult(entry),
                    SimilarImageResult: null)
                : new DesktopImageMatchCachedResult(
                    entry.Mode,
                    entry.DistanceThreshold,
                    ExactDuplicateResult: null,
                    CreateSimilarImageScanResult(entry));
            return true;
        }
    }

    public void SaveExact(IReadOnlyList<ImageRecord> images, ExactDuplicateScanResult result)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(result);

        SaveExact(CreateCollectionSignature(images), result);
    }

    public async Task SaveExactAsync(
        IReadOnlyList<ImageRecord> images,
        ExactDuplicateScanResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(result);

        await SaveExactAsync(CreateCollectionSignature(images), result, cancellationToken).ConfigureAwait(false);
    }

    public void SaveSimilar(IReadOnlyList<ImageRecord> images, SimilarImageScanResult result)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(result);

        SaveSimilar(CreateCollectionSignature(images), result);
    }

    public async Task SaveSimilarAsync(
        IReadOnlyList<ImageRecord> images,
        SimilarImageScanResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(result);

        await SaveSimilarAsync(CreateCollectionSignature(images), result, cancellationToken).ConfigureAwait(false);
    }

    internal void SaveExact(string collectionSignature, ExactDuplicateScanResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionSignature);
        ArgumentNullException.ThrowIfNull(result);

        SaveEntry(CreateExactDuplicateCacheEntry(collectionSignature, result));
    }

    internal Task SaveExactAsync(
        string collectionSignature,
        ExactDuplicateScanResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionSignature);
        ArgumentNullException.ThrowIfNull(result);

        return SaveEntryAsync(CreateExactDuplicateCacheEntry(collectionSignature, result), cancellationToken);
    }

    internal void SaveSimilar(string collectionSignature, SimilarImageScanResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionSignature);
        ArgumentNullException.ThrowIfNull(result);

        SaveEntry(CreateSimilarImageCacheEntry(collectionSignature, result));
    }

    internal Task SaveSimilarAsync(
        string collectionSignature,
        SimilarImageScanResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionSignature);
        ArgumentNullException.ThrowIfNull(result);

        return SaveEntryAsync(CreateSimilarImageCacheEntry(collectionSignature, result), cancellationToken);
    }

    internal static string CreateCollectionSignature(IReadOnlyList<ImageRecord> images)
    {
        ArgumentNullException.ThrowIfNull(images);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var image in images
                     .OrderBy(static item => NormalizePathForSignature(item.FullPath), PathComparison.Comparer)
                     .ThenBy(static item => item.SizeBytes)
                     .ThenBy(static item => item.ModifiedAt.ToUniversalTime().Ticks))
        {
            AppendUtf8(hash, NormalizePathForSignature(image.FullPath));
            AppendUtf8(hash, "\n");
            AppendUtf8(hash, image.SizeBytes.ToString());
            AppendUtf8(hash, "\n");
            AppendUtf8(hash, image.ModifiedAt.ToUniversalTime().Ticks.ToString());
            AppendUtf8(hash, "\n");
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private void SaveEntry(DesktopImageMatchCacheEntry entry)
    {
        lock (_syncRoot)
        {
            EnsureLoadedUnsafe();
            _entriesByKey![CreateEntryKey(entry.CollectionSignature, entry.Mode, entry.DistanceThreshold)] = entry;
            TrimEntriesUnsafe();
            PersistUnsafe();
        }
    }

    private async Task SaveEntryAsync(DesktopImageMatchCacheEntry entry, CancellationToken cancellationToken)
    {
        await _saveSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SaveEntry(entry);
        }
        finally
        {
            _saveSemaphore.Release();
        }
    }

    private static ExactDuplicateScanResult CreateExactDuplicateScanResult(DesktopImageMatchCacheEntry entry)
    {
        return new ExactDuplicateScanResult(
            entry.ScannedCount,
            entry.FailedCount,
            entry.ExactGroups
                .Select(static group => new ExactDuplicateGroup(group.SizeBytes, group.Hash, group.Paths.ToArray()))
                .ToArray());
    }

    private static SimilarImageScanResult CreateSimilarImageScanResult(DesktopImageMatchCacheEntry entry)
    {
        return new SimilarImageScanResult(
            entry.ScannedCount,
            entry.FailedCount,
            entry.DistanceThreshold,
            entry.SimilarGroups
                .Select(static group => new SimilarImageGroup(group.Hash, group.Paths.ToArray()))
                .ToArray());
    }

    private static DesktopImageMatchCacheEntry CreateExactDuplicateCacheEntry(
        string collectionSignature,
        ExactDuplicateScanResult result)
    {
        return new DesktopImageMatchCacheEntry(
            collectionSignature,
            DesktopImageMatchCacheMode.ExactDuplicates,
            DistanceThreshold: 0,
            result.ScannedCount,
            result.FailedCount,
            result.Groups
                .Select(static group => new DesktopExactDuplicateCacheGroup(group.SizeBytes, group.Hash, group.Paths.ToArray()))
                .ToArray(),
            SimilarGroups: [],
            SavedAtUtcTicks: DateTime.UtcNow.Ticks);
    }

    private static DesktopImageMatchCacheEntry CreateSimilarImageCacheEntry(
        string collectionSignature,
        SimilarImageScanResult result)
    {
        return new DesktopImageMatchCacheEntry(
            collectionSignature,
            DesktopImageMatchCacheMode.SimilarImages,
            result.DistanceThreshold,
            result.ScannedCount,
            result.FailedCount,
            ExactGroups: [],
            result.Groups
                .Select(static group => new DesktopSimilarImageCacheGroup(group.Hash, group.Paths.ToArray()))
                .ToArray(),
            SavedAtUtcTicks: DateTime.UtcNow.Ticks);
    }

    private void EnsureLoadedUnsafe()
    {
        if (_entriesByKey is not null)
        {
            return;
        }

        _entriesByKey = [];
        foreach (var entry in LoadEntries(_storePath))
        {
            _entriesByKey[CreateEntryKey(entry.CollectionSignature, entry.Mode, entry.DistanceThreshold)] = entry;
        }
    }

    private void TrimEntriesUnsafe()
    {
        while (_entriesByKey!.Count > MaxEntries)
        {
            var staleEntry = _entriesByKey.Values
                .OrderBy(static item => item.SavedAtUtcTicks)
                .FirstOrDefault();
            if (staleEntry is null)
            {
                break;
            }

            _entriesByKey.Remove(CreateEntryKey(staleEntry.CollectionSignature, staleEntry.Mode, staleEntry.DistanceThreshold));
        }
    }

    private void PersistUnsafe()
    {
        try
        {
            var directory = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(
                _entriesByKey!.Values
                    .OrderByDescending(static item => item.SavedAtUtcTicks)
                    .ToArray(),
                SerializerOptions);
            DesktopFileStreamFactory.WriteAtomically(_storePath, stream =>
            {
                using var writer = new StreamWriter(stream, StrictUtf8, bufferSize: 1024, leaveOpen: true);
                writer.Write(json);
            });
        }
        catch
        {
        }
    }

    private static string GetDefaultStorePath()
    {
        return Path.Combine(GetDefaultRootDirectory(), "image-match-results-cache.json");
    }

    private static string GetDefaultRootDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ModernImageViewer.Desktop");
    }

    private static IReadOnlyList<DesktopImageMatchCacheEntry> LoadEntries(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            using var stream = File.OpenRead(path);
            using var reader = new StreamReader(stream, StrictUtf8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
            var json = reader.ReadToEnd();
            return JsonSerializer.Deserialize<List<DesktopImageMatchCacheEntry>>(json, SerializerOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string CreateEntryKey(string collectionSignature, DesktopImageMatchCacheMode mode, int distanceThreshold)
    {
        return $"{collectionSignature}|{(int)mode}|{distanceThreshold}";
    }

    private static string NormalizePathForSignature(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return OperatingSystem.IsWindows()
            ? fullPath.ToUpperInvariant()
            : fullPath;
    }

    private static void AppendUtf8(IncrementalHash hash, string text)
    {
        hash.AppendData(StrictUtf8.GetBytes(text));
    }
}
