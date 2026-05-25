using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Tests;

public sealed class DesktopImageFingerprintPersistenceTests
{
    [Fact]
    public void Persist_roundtrips_text_and_difference_hash_entries()
    {
        using var paths = TestPaths.Create();
        var store = CreateStore(paths);
        var cache = new DesktopImageFingerprintCacheStore(store);

        var textValue = cache.GetOrAddText(
            DesktopImageFingerprintKind.SampleHash,
            path: paths.Combine("a.jpg"),
            sizeBytes: 1024,
            sourceStampTicks: 123,
            loader: static () => "ABC123");
        var differenceHash = cache.GetOrAddDifferenceHash(
            path: paths.Combine("b.jpg"),
            sizeBytes: 2048,
            sourceStampTicks: 456,
            loader: static () => 42UL);

        cache.Persist();

        var reloadedCache = new DesktopImageFingerprintCacheStore(store);
        var textLoadCount = 0;
        var differenceHashLoadCount = 0;

        var reloadedTextValue = reloadedCache.GetOrAddText(
            DesktopImageFingerprintKind.SampleHash,
            path: paths.Combine("a.jpg"),
            sizeBytes: 1024,
            sourceStampTicks: 123,
            loader: () =>
            {
                textLoadCount++;
                return "MISS";
            });
        var reloadedDifferenceHash = reloadedCache.GetOrAddDifferenceHash(
            path: paths.Combine("b.jpg"),
            sizeBytes: 2048,
            sourceStampTicks: 456,
            loader: () =>
            {
                differenceHashLoadCount++;
                return 99UL;
            });

        Assert.Equal(textValue, reloadedTextValue);
        Assert.Equal(differenceHash, reloadedDifferenceHash);
        Assert.Equal(0, textLoadCount);
        Assert.Equal(0, differenceHashLoadCount);
    }

    [Fact]
    public void Persist_keeps_only_latest_signature_for_same_path()
    {
        using var paths = TestPaths.Create();
        var targetPath = paths.Combine("a.jpg");
        var store = CreateStore(paths);
        var cache = new DesktopImageFingerprintCacheStore(store);

        _ = cache.GetOrAddText(
            DesktopImageFingerprintKind.FullHash,
            path: targetPath,
            sizeBytes: 100,
            sourceStampTicks: 111,
            loader: static () => "OLD");
        var latest = cache.GetOrAddText(
            DesktopImageFingerprintKind.FullHash,
            path: targetPath,
            sizeBytes: 101,
            sourceStampTicks: 222,
            loader: static () => "NEW");

        cache.Persist();

        var reloadedCache = new DesktopImageFingerprintCacheStore(store);
        var loadCount = 0;
        var reloadedLatest = reloadedCache.GetOrAddText(
            DesktopImageFingerprintKind.FullHash,
            path: targetPath,
            sizeBytes: 101,
            sourceStampTicks: 222,
            loader: () =>
            {
                loadCount++;
                return "MISS";
            });

        Assert.Equal(latest, reloadedLatest);
        Assert.Equal(0, loadCount);
        Assert.Single(store.LoadTextEntries());
    }

    [Fact]
    public void Persist_respects_cache_entry_limits()
    {
        using var paths = TestPaths.Create();
        var store = CreateStore(paths);
        var cache = new DesktopImageFingerprintCacheStore(store, maxTextEntries: 2, maxDifferenceHashEntries: 1);

        _ = cache.GetOrAddText(
            DesktopImageFingerprintKind.SampleHash,
            path: paths.Combine("a.jpg"),
            sizeBytes: 1,
            sourceStampTicks: 1,
            loader: static () => "A");
        _ = cache.GetOrAddText(
            DesktopImageFingerprintKind.SampleHash,
            path: paths.Combine("b.jpg"),
            sizeBytes: 2,
            sourceStampTicks: 2,
            loader: static () => "B");
        _ = cache.GetOrAddText(
            DesktopImageFingerprintKind.SampleHash,
            path: paths.Combine("c.jpg"),
            sizeBytes: 3,
            sourceStampTicks: 3,
            loader: static () => "C");
        _ = cache.GetOrAddDifferenceHash(
            path: paths.Combine("hash-a.jpg"),
            sizeBytes: 10,
            sourceStampTicks: 10,
            loader: static () => 10UL);
        _ = cache.GetOrAddDifferenceHash(
            path: paths.Combine("hash-b.jpg"),
            sizeBytes: 20,
            sourceStampTicks: 20,
            loader: static () => 20UL);

        cache.Persist();

        Assert.Equal(2, store.LoadTextEntries().Count);
        Assert.Single(store.LoadDifferenceHashEntries());
    }

    private static DesktopImageFingerprintStateStore CreateStore(TestPaths paths)
    {
        return new DesktopImageFingerprintStateStore(
            paths.Combine("image-fingerprint-text-cache.json"),
            paths.Combine("image-fingerprint-dhash-cache.json"));
    }
}
