using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Tests;

public sealed class DesktopImageDimensionCacheStoreTests
{
    [Fact]
    public void GetOrLoad_reuses_cached_dimensions_for_same_file_signature()
    {
        using var paths = TestPaths.Create();
        var imagePath = paths.Combine("a.jpg");
        File.WriteAllBytes(imagePath, [1, 2, 3]);
        File.SetLastWriteTimeUtc(imagePath, new DateTime(2026, 4, 21, 9, 0, 0, DateTimeKind.Utc));

        var store = new DesktopImageDimensionCacheStore(maxEntries: 8);
        var loadCount = 0;

        var first = store.GetOrLoad(imagePath, _ =>
        {
            loadCount++;
            return new DesktopImageDimensions(1200, 800);
        });

        var second = store.GetOrLoad(imagePath, _ =>
        {
            loadCount++;
            return new DesktopImageDimensions(1600, 900);
        });

        Assert.Equal(new DesktopImageDimensions(1200, 800), first);
        Assert.Equal(first, second);
        Assert.Equal(1, loadCount);
        Assert.Equal(1, store.EntryCount);
    }

    [Fact]
    public void GetOrLoad_invalidates_cache_when_file_signature_changes()
    {
        using var paths = TestPaths.Create();
        var imagePath = paths.Combine("a.jpg");
        File.WriteAllBytes(imagePath, [1, 2, 3]);
        File.SetLastWriteTimeUtc(imagePath, new DateTime(2026, 4, 21, 9, 0, 0, DateTimeKind.Utc));

        var store = new DesktopImageDimensionCacheStore(maxEntries: 8);
        var loadCount = 0;

        var first = store.GetOrLoad(imagePath, _ =>
        {
            loadCount++;
            return new DesktopImageDimensions(1200, 800);
        });

        File.WriteAllBytes(imagePath, [1, 2, 3, 4]);
        File.SetLastWriteTimeUtc(imagePath, new DateTime(2026, 4, 21, 9, 1, 0, DateTimeKind.Utc));

        var second = store.GetOrLoad(imagePath, _ =>
        {
            loadCount++;
            return new DesktopImageDimensions(2400, 1600);
        });

        Assert.Equal(new DesktopImageDimensions(1200, 800), first);
        Assert.Equal(new DesktopImageDimensions(2400, 1600), second);
        Assert.Equal(2, loadCount);
        Assert.Equal(1, store.EntryCount);
    }

    [Fact]
    public void GetOrLoad_reuses_cached_dimensions_for_explicit_signature()
    {
        using var paths = TestPaths.Create();
        var imagePath = paths.Combine("a.jpg");
        File.WriteAllBytes(imagePath, [1, 2, 3]);
        File.SetLastWriteTimeUtc(imagePath, new DateTime(2026, 4, 21, 9, 0, 0, DateTimeKind.Utc));

        var store = new DesktopImageDimensionCacheStore(maxEntries: 8);
        Assert.True(DesktopFileSignatureReader.TryRead(imagePath, out var signature));

        var loadCount = 0;
        var first = store.GetOrLoad(signature, _ =>
        {
            loadCount++;
            return new DesktopImageDimensions(1200, 800);
        });

        var second = store.GetOrLoad(signature, _ =>
        {
            loadCount++;
            return new DesktopImageDimensions(1600, 900);
        });

        Assert.Equal(new DesktopImageDimensions(1200, 800), first);
        Assert.Equal(first, second);
        Assert.Equal(1, loadCount);
        Assert.Equal(1, store.EntryCount);
    }

    [Fact]
    public void UpdateEntryLimit_trims_least_recent_entry()
    {
        using var paths = TestPaths.Create();
        var firstPath = paths.Combine("a.jpg");
        var secondPath = paths.Combine("b.jpg");
        var thirdPath = paths.Combine("c.jpg");
        File.WriteAllBytes(firstPath, [1]);
        File.WriteAllBytes(secondPath, [2]);
        File.WriteAllBytes(thirdPath, [3]);

        var store = new DesktopImageDimensionCacheStore(maxEntries: 8);

        _ = store.GetOrLoad(firstPath, _ => new DesktopImageDimensions(100, 100));
        _ = store.GetOrLoad(secondPath, _ => new DesktopImageDimensions(200, 200));
        _ = store.GetOrLoad(firstPath, _ => new DesktopImageDimensions(300, 300));
        _ = store.GetOrLoad(thirdPath, _ => new DesktopImageDimensions(400, 400));

        store.UpdateEntryLimit(2);

        var reloadedSecondCount = 0;
        var reusedFirst = store.GetOrLoad(firstPath, _ => new DesktopImageDimensions(999, 999));
        var reloadedSecond = store.GetOrLoad(secondPath, _ =>
        {
            reloadedSecondCount++;
            return new DesktopImageDimensions(555, 555);
        });

        Assert.Equal(2, store.EntryCount);
        Assert.Equal(new DesktopImageDimensions(100, 100), reusedFirst);
        Assert.Equal(new DesktopImageDimensions(555, 555), reloadedSecond);
        Assert.Equal(1, reloadedSecondCount);
    }

    [Fact]
    public void Persist_and_reload_reuses_dimensions_without_reinvoking_loader()
    {
        using var paths = TestPaths.Create();
        var imagePath = paths.Combine("a.jpg");
        File.WriteAllBytes(imagePath, [1, 2, 3]);
        File.SetLastWriteTimeUtc(imagePath, new DateTime(2026, 4, 21, 9, 0, 0, DateTimeKind.Utc));

        var stateStore = new DesktopImageDimensionStateStore(paths.Combine("image-dimension-cache.json"));
        var firstStore = new DesktopImageDimensionCacheStore(stateStore, maxEntries: 8);
        var firstLoadCount = 0;

        var first = firstStore.GetOrLoad(imagePath, _ =>
        {
            firstLoadCount++;
            return new DesktopImageDimensions(1200, 800);
        });
        firstStore.Persist();

        var secondStore = new DesktopImageDimensionCacheStore(stateStore, maxEntries: 8);
        var secondLoadCount = 0;
        var second = secondStore.GetOrLoad(imagePath, _ =>
        {
            secondLoadCount++;
            return new DesktopImageDimensions(2400, 1600);
        });

        Assert.Equal(new DesktopImageDimensions(1200, 800), first);
        Assert.Equal(first, second);
        Assert.Equal(1, firstLoadCount);
        Assert.Equal(0, secondLoadCount);
        Assert.Equal(1, secondStore.EntryCount);
    }

    [Fact]
    public void Persisted_dimensions_are_invalidated_when_file_signature_changes()
    {
        using var paths = TestPaths.Create();
        var imagePath = paths.Combine("a.jpg");
        File.WriteAllBytes(imagePath, [1, 2, 3]);
        File.SetLastWriteTimeUtc(imagePath, new DateTime(2026, 4, 21, 9, 0, 0, DateTimeKind.Utc));

        var stateStore = new DesktopImageDimensionStateStore(paths.Combine("image-dimension-cache.json"));
        var firstStore = new DesktopImageDimensionCacheStore(stateStore, maxEntries: 8);
        _ = firstStore.GetOrLoad(imagePath, _ => new DesktopImageDimensions(1200, 800));
        firstStore.Persist();

        File.WriteAllBytes(imagePath, [1, 2, 3, 4]);
        File.SetLastWriteTimeUtc(imagePath, new DateTime(2026, 4, 21, 9, 5, 0, DateTimeKind.Utc));

        var secondStore = new DesktopImageDimensionCacheStore(stateStore, maxEntries: 8);
        var loadCount = 0;
        var reloaded = secondStore.GetOrLoad(imagePath, _ =>
        {
            loadCount++;
            return new DesktopImageDimensions(2400, 1600);
        });

        Assert.Equal(new DesktopImageDimensions(2400, 1600), reloaded);
        Assert.Equal(1, loadCount);
        Assert.Equal(1, secondStore.EntryCount);
    }

    [Fact]
    public async Task GetOrLoad_persists_dimensions_in_background_without_explicit_flush()
    {
        using var paths = TestPaths.Create();
        var imagePath = paths.Combine("a.jpg");
        File.WriteAllBytes(imagePath, [1, 2, 3]);
        File.SetLastWriteTimeUtc(imagePath, new DateTime(2026, 4, 21, 9, 0, 0, DateTimeKind.Utc));

        var stateStore = new DesktopImageDimensionStateStore(paths.Combine("image-dimension-cache.json"));
        var firstStore = new DesktopImageDimensionCacheStore(stateStore, maxEntries: 8, persistDelayMilliseconds: 15);
        var firstLoadCount = 0;

        var first = firstStore.GetOrLoad(imagePath, _ =>
        {
            firstLoadCount++;
            return new DesktopImageDimensions(1200, 800);
        });

        await firstStore.WaitForPendingPersistenceAsync();

        var secondStore = new DesktopImageDimensionCacheStore(stateStore, maxEntries: 8, persistDelayMilliseconds: 15);
        var secondLoadCount = 0;
        var second = secondStore.GetOrLoad(imagePath, _ =>
        {
            secondLoadCount++;
            return new DesktopImageDimensions(2400, 1600);
        });

        Assert.Equal(new DesktopImageDimensions(1200, 800), first);
        Assert.Equal(first, second);
        Assert.Equal(1, firstLoadCount);
        Assert.Equal(0, secondLoadCount);
    }
}
