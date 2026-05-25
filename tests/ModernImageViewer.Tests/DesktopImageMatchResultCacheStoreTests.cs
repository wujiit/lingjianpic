using ModernImageViewer.Core;
using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Tests;

public sealed class DesktopImageMatchResultCacheStoreTests
{
    [Fact]
    public void SaveExact_and_try_load_exact_roundtrip_groups()
    {
        using var paths = TestPaths.Create();
        var store = new DesktopImageMatchResultCacheStore(paths.Combine("image-match-cache.json"));
        var images = CreateImageRecords();
        var expected = new ExactDuplicateScanResult(
            ScannedCount: 3,
            FailedCount: 1,
            Groups:
            [
                new ExactDuplicateGroup(
                    120,
                    "ABC123",
                    [images[0].FullPath, images[1].FullPath])
            ]);

        store.SaveExact(images, expected);

        var loaded = store.TryLoadExact(images, out var actual);

        Assert.True(loaded);
        Assert.Equal(expected.ScannedCount, actual.ScannedCount);
        Assert.Equal(expected.FailedCount, actual.FailedCount);
        Assert.Equal(expected.GroupCount, actual.GroupCount);
        Assert.Equal(expected.DuplicateCount, actual.DuplicateCount);
        Assert.Equal(expected.Groups[0].SizeBytes, actual.Groups[0].SizeBytes);
        Assert.Equal(expected.Groups[0].Hash, actual.Groups[0].Hash);
        Assert.Equal(expected.Groups[0].Paths, actual.Groups[0].Paths);
        Assert.Equal(1, store.GetEntryCount());
    }

    [Fact]
    public void SaveSimilar_and_try_load_similar_roundtrip_groups()
    {
        using var paths = TestPaths.Create();
        var store = new DesktopImageMatchResultCacheStore(paths.Combine("image-match-cache.json"));
        var images = CreateImageRecords();
        var expected = new SimilarImageScanResult(
            ScannedCount: 3,
            FailedCount: 0,
            DistanceThreshold: 8,
            Groups:
            [
                new SimilarImageGroup(
                    42UL,
                    [images[0].FullPath, images[2].FullPath])
            ]);

        store.SaveSimilar(images, expected);

        var loaded = store.TryLoadSimilar(images, 8, out var actual);

        Assert.True(loaded);
        Assert.Equal(expected.ScannedCount, actual.ScannedCount);
        Assert.Equal(expected.FailedCount, actual.FailedCount);
        Assert.Equal(expected.DistanceThreshold, actual.DistanceThreshold);
        Assert.Equal(expected.GroupCount, actual.GroupCount);
        Assert.Equal(expected.SimilarCount, actual.SimilarCount);
        Assert.Equal(expected.Groups[0].Hash, actual.Groups[0].Hash);
        Assert.Equal(expected.Groups[0].Paths, actual.Groups[0].Paths);
        Assert.Equal(1, store.GetEntryCount());
    }

    [Fact]
    public async Task SaveExactAsync_and_try_load_exact_roundtrip_groups()
    {
        using var paths = TestPaths.Create();
        var store = new DesktopImageMatchResultCacheStore(paths.Combine("image-match-cache.json"));
        var images = CreateImageRecords();
        var expected = new ExactDuplicateScanResult(
            ScannedCount: 3,
            FailedCount: 0,
            Groups:
            [
                new ExactDuplicateGroup(
                    120,
                    "XYZ789",
                    [images[0].FullPath, images[1].FullPath])
            ]);

        await store.SaveExactAsync(images, expected);

        var loaded = store.TryLoadExact(images, out var actual);

        Assert.True(loaded);
        Assert.Equal(expected.ScannedCount, actual.ScannedCount);
        Assert.Equal(expected.FailedCount, actual.FailedCount);
        Assert.Equal(expected.GroupCount, actual.GroupCount);
        Assert.Equal(expected.Groups[0].Hash, actual.Groups[0].Hash);
        Assert.Equal(expected.Groups[0].Paths, actual.Groups[0].Paths);
    }

    [Fact]
    public async Task SaveSimilarAsync_and_try_load_similar_roundtrip_groups()
    {
        using var paths = TestPaths.Create();
        var store = new DesktopImageMatchResultCacheStore(paths.Combine("image-match-cache.json"));
        var images = CreateImageRecords();
        var expected = new SimilarImageScanResult(
            ScannedCount: 3,
            FailedCount: 1,
            DistanceThreshold: 12,
            Groups:
            [
                new SimilarImageGroup(
                    77UL,
                    [images[1].FullPath, images[2].FullPath])
            ]);

        await store.SaveSimilarAsync(images, expected);

        var loaded = store.TryLoadSimilar(images, 12, out var actual);

        Assert.True(loaded);
        Assert.Equal(expected.ScannedCount, actual.ScannedCount);
        Assert.Equal(expected.FailedCount, actual.FailedCount);
        Assert.Equal(expected.DistanceThreshold, actual.DistanceThreshold);
        Assert.Equal(expected.GroupCount, actual.GroupCount);
        Assert.Equal(expected.Groups[0].Hash, actual.Groups[0].Hash);
        Assert.Equal(expected.Groups[0].Paths, actual.Groups[0].Paths);
    }

    [Fact]
    public void TryLoadExact_accepts_precomputed_collection_signature()
    {
        using var paths = TestPaths.Create();
        var store = new DesktopImageMatchResultCacheStore(paths.Combine("image-match-cache.json"));
        var images = CreateImageRecords();
        var signature = DesktopImageMatchResultCacheStore.CreateCollectionSignature(images);
        var expected = new ExactDuplicateScanResult(
            ScannedCount: 3,
            FailedCount: 0,
            Groups:
            [
                new ExactDuplicateGroup(
                    120,
                    "PRECOMPUTED",
                    [images[0].FullPath, images[1].FullPath])
            ]);

        store.SaveExact(signature, expected);

        var loaded = store.TryLoadExact(signature, out var actual);

        Assert.True(loaded);
        Assert.Equal(expected.Groups[0].Hash, actual.Groups[0].Hash);
        Assert.Equal(expected.Groups[0].Paths, actual.Groups[0].Paths);
    }

    [Fact]
    public async Task SaveSimilarAsync_accepts_precomputed_collection_signature()
    {
        using var paths = TestPaths.Create();
        var store = new DesktopImageMatchResultCacheStore(paths.Combine("image-match-cache.json"));
        var images = CreateImageRecords();
        var signature = DesktopImageMatchResultCacheStore.CreateCollectionSignature(images);
        var expected = new SimilarImageScanResult(
            ScannedCount: 3,
            FailedCount: 0,
            DistanceThreshold: 4,
            Groups:
            [
                new SimilarImageGroup(
                    11UL,
                    [images[0].FullPath, images[2].FullPath])
            ]);

        await store.SaveSimilarAsync(signature, expected);

        var loaded = store.TryLoadSimilar(signature, 4, out var actual);

        Assert.True(loaded);
        Assert.Equal(expected.Groups[0].Hash, actual.Groups[0].Hash);
        Assert.Equal(expected.Groups[0].Paths, actual.Groups[0].Paths);
    }

    [Fact]
    public void TryLoadExact_returns_false_when_collection_signature_changes()
    {
        using var paths = TestPaths.Create();
        var store = new DesktopImageMatchResultCacheStore(paths.Combine("image-match-cache.json"));
        var images = CreateImageRecords();
        store.SaveExact(images, new ExactDuplicateScanResult(3, 0, []));

        var changedImages = CreateImageRecords(modifiedOffset: TimeSpan.FromMinutes(1));
        var loaded = store.TryLoadExact(changedImages, out _);

        Assert.False(loaded);
    }

    [Fact]
    public void TryLoadSimilar_returns_false_when_threshold_changes()
    {
        using var paths = TestPaths.Create();
        var store = new DesktopImageMatchResultCacheStore(paths.Combine("image-match-cache.json"));
        var images = CreateImageRecords();
        store.SaveSimilar(images, new SimilarImageScanResult(3, 0, 8, []));

        var loaded = store.TryLoadSimilar(images, 12, out _);

        Assert.False(loaded);
    }

    [Fact]
    public void TryLoadLatest_returns_most_recent_entry_for_same_collection()
    {
        using var paths = TestPaths.Create();
        var store = new DesktopImageMatchResultCacheStore(paths.Combine("image-match-cache.json"));
        var images = CreateImageRecords();

        store.SaveExact(images, new ExactDuplicateScanResult(3, 0, []));
        Thread.Sleep(20);
        var similar = new SimilarImageScanResult(
            ScannedCount: 3,
            FailedCount: 0,
            DistanceThreshold: 12,
            Groups:
            [
                new SimilarImageGroup(
                    99UL,
                    [images[1].FullPath, images[2].FullPath])
            ]);
        store.SaveSimilar(images, similar);

        var loaded = store.TryLoadLatest(images, out var latest);

        Assert.True(loaded);
        Assert.Equal(DesktopImageMatchCacheMode.SimilarImages, latest.Mode);
        Assert.Equal(12, latest.DistanceThreshold);
        Assert.Null(latest.ExactDuplicateResult);
        Assert.NotNull(latest.SimilarImageResult);
        Assert.Equal(similar.ScannedCount, latest.SimilarImageResult!.ScannedCount);
        Assert.Equal(similar.FailedCount, latest.SimilarImageResult.FailedCount);
        Assert.Equal(similar.DistanceThreshold, latest.SimilarImageResult.DistanceThreshold);
        Assert.Equal(similar.GroupCount, latest.SimilarImageResult.GroupCount);
        Assert.Equal(similar.SimilarCount, latest.SimilarImageResult.SimilarCount);
        Assert.Equal(similar.Groups[0].Hash, latest.SimilarImageResult.Groups[0].Hash);
        Assert.Equal(similar.Groups[0].Paths, latest.SimilarImageResult.Groups[0].Paths);
    }

    private static IReadOnlyList<ImageRecord> CreateImageRecords(TimeSpan? modifiedOffset = null)
    {
        var offset = modifiedOffset ?? TimeSpan.Zero;
        var baseTime = new DateTimeOffset(2026, 4, 21, 10, 0, 0, TimeSpan.Zero) + offset;

        return
        [
            new ImageRecord(@"E:\images\a.jpg", "a.jpg", 120, baseTime),
            new ImageRecord(@"E:\images\b.jpg", "b.jpg", 120, baseTime.AddSeconds(1)),
            new ImageRecord(@"E:\images\c.jpg", "c.jpg", 240, baseTime.AddSeconds(2))
        ];
    }
}
