using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Tests;

public sealed class DesktopImageFingerprintCacheStoreTests
{
    [Fact]
    public void GetOrAddText_reuses_cached_value_for_same_file_signature()
    {
        var store = new DesktopImageFingerprintCacheStore();
        var loadCount = 0;

        var first = store.GetOrAddText(
            DesktopImageFingerprintKind.SampleHash,
            path: "E:\\images\\a.jpg",
            sizeBytes: 1024,
            sourceStampTicks: 123,
            loader: () =>
            {
                loadCount++;
                return "ABC";
            });

        var second = store.GetOrAddText(
            DesktopImageFingerprintKind.SampleHash,
            path: "E:\\images\\a.jpg",
            sizeBytes: 1024,
            sourceStampTicks: 123,
            loader: () =>
            {
                loadCount++;
                return "DEF";
            });

        Assert.Equal("ABC", first);
        Assert.Equal("ABC", second);
        Assert.Equal(1, loadCount);
    }

    [Fact]
    public void GetOrAddText_invalidates_when_file_signature_changes()
    {
        var store = new DesktopImageFingerprintCacheStore();
        var loadCount = 0;

        _ = store.GetOrAddText(
            DesktopImageFingerprintKind.FullHash,
            path: "E:\\images\\a.jpg",
            sizeBytes: 1024,
            sourceStampTicks: 123,
            loader: () =>
            {
                loadCount++;
                return "ABC";
            });

        var updated = store.GetOrAddText(
            DesktopImageFingerprintKind.FullHash,
            path: "E:\\images\\a.jpg",
            sizeBytes: 1024,
            sourceStampTicks: 456,
            loader: () =>
            {
                loadCount++;
                return "DEF";
            });

        Assert.Equal("DEF", updated);
        Assert.Equal(2, loadCount);
    }

    [Fact]
    public async Task GetOrAddDifferenceHash_coalesces_concurrent_requests()
    {
        var store = new DesktopImageFingerprintCacheStore();
        var loadCount = 0;

        var tasks = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() => store.GetOrAddDifferenceHash(
                path: "E:\\images\\a.jpg",
                sizeBytes: 1024,
                sourceStampTicks: 123,
                loader: () =>
                {
                    Interlocked.Increment(ref loadCount);
                    Thread.Sleep(60);
                    return 42UL;
                })))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(1, loadCount);
        Assert.All(tasks, task => Assert.Equal(42UL, task.Result));
    }

    [Fact]
    public void UpdateEntryLimits_trims_least_recent_entries()
    {
        var store = new DesktopImageFingerprintCacheStore(stateStore: null, maxTextEntries: 3, maxDifferenceHashEntries: 3);

        _ = store.GetOrAddText(
            DesktopImageFingerprintKind.SampleHash,
            path: "E:\\images\\a.jpg",
            sizeBytes: 100,
            sourceStampTicks: 1,
            loader: static () => "A");
        _ = store.GetOrAddText(
            DesktopImageFingerprintKind.SampleHash,
            path: "E:\\images\\b.jpg",
            sizeBytes: 100,
            sourceStampTicks: 2,
            loader: static () => "B");
        _ = store.GetOrAddText(
            DesktopImageFingerprintKind.SampleHash,
            path: "E:\\images\\a.jpg",
            sizeBytes: 100,
            sourceStampTicks: 1,
            loader: static () => "A2");
        _ = store.GetOrAddText(
            DesktopImageFingerprintKind.SampleHash,
            path: "E:\\images\\c.jpg",
            sizeBytes: 100,
            sourceStampTicks: 3,
            loader: static () => "C");

        store.UpdateEntryLimits(maxTextEntries: 2, maxDifferenceHashEntries: 3);

        var counts = store.GetEntryCounts();
        var reloadedBCount = 0;
        var reloadedACount = 0;

        var valueA = store.GetOrAddText(
            DesktopImageFingerprintKind.SampleHash,
            path: "E:\\images\\a.jpg",
            sizeBytes: 100,
            sourceStampTicks: 1,
            loader: () =>
            {
                reloadedACount++;
                return "MISS-A";
            });
        var valueB = store.GetOrAddText(
            DesktopImageFingerprintKind.SampleHash,
            path: "E:\\images\\b.jpg",
            sizeBytes: 100,
            sourceStampTicks: 2,
            loader: () =>
            {
                reloadedBCount++;
                return "MISS-B";
            });

        Assert.Equal((2, 0), counts);
        Assert.Equal("A", valueA);
        Assert.Equal("MISS-B", valueB);
        Assert.Equal(0, reloadedACount);
        Assert.Equal(1, reloadedBCount);
    }
}
