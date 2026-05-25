using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Tests;

public sealed class PreviewImageCacheStoreTests
{
    [Fact]
    public void GetOrLoad_reuses_cached_entry_for_same_key()
    {
        var store = new PreviewImageCacheStore(maxBytes: 1024);
        var key = new PreviewImageCacheKey("E:\\images\\a.jpg", 100, 10, 160, PreferCompatibilityDecoder: false);
        var loadCount = 0;

        var first = store.GetOrLoad(key, () =>
        {
            loadCount++;
            return CreateEntry(sizeBytes: 128);
        });

        var second = store.GetOrLoad(key, () =>
        {
            loadCount++;
            return CreateEntry(sizeBytes: 128);
        });

        Assert.Same(first, second);
        Assert.Equal(1, loadCount);
        Assert.Equal(1, store.EntryCount);
        Assert.Equal(128, store.CurrentBytes);
    }

    [Fact]
    public void GetOrLoad_evicts_least_recently_used_entry_when_budget_is_exceeded()
    {
        var store = new PreviewImageCacheStore(maxBytes: 10);
        var firstKey = new PreviewImageCacheKey("E:\\images\\a.jpg", 10, 1, 160, PreferCompatibilityDecoder: false);
        var secondKey = new PreviewImageCacheKey("E:\\images\\b.jpg", 10, 2, 160, PreferCompatibilityDecoder: false);
        var firstLoadCount = 0;

        _ = store.GetOrLoad(firstKey, () =>
        {
            firstLoadCount++;
            return CreateEntry(sizeBytes: 6);
        });

        _ = store.GetOrLoad(secondKey, () => CreateEntry(sizeBytes: 6));

        Assert.Equal(1, store.EntryCount);
        Assert.Equal(6, store.CurrentBytes);

        _ = store.GetOrLoad(firstKey, () =>
        {
            firstLoadCount++;
            return CreateEntry(sizeBytes: 6);
        });

        Assert.Equal(2, firstLoadCount);
    }

    [Fact]
    public void GetOrLoad_does_not_cache_non_cacheable_entries()
    {
        var store = new PreviewImageCacheStore(maxBytes: 1024);
        var key = new PreviewImageCacheKey("E:\\images\\a.jpg", 10, 1, 160, PreferCompatibilityDecoder: false);
        var loadCount = 0;

        _ = store.GetOrLoad(key, () =>
        {
            loadCount++;
            return new PreviewImageCacheEntry(null, "miss", "details", Cacheable: false);
        });

        _ = store.GetOrLoad(key, () =>
        {
            loadCount++;
            return new PreviewImageCacheEntry(null, "miss", "details", Cacheable: false);
        });

        Assert.Equal(2, loadCount);
        Assert.Equal(0, store.EntryCount);
        Assert.Equal(0, store.CurrentBytes);
    }

    [Fact]
    public async Task GetOrLoad_coalesces_concurrent_requests()
    {
        var store = new PreviewImageCacheStore(maxBytes: 1024);
        var key = new PreviewImageCacheKey("E:\\images\\a.jpg", 10, 1, 160, PreferCompatibilityDecoder: false);
        var loadCount = 0;

        var tasks = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() => store.GetOrLoad(key, () =>
            {
                Interlocked.Increment(ref loadCount);
                Thread.Sleep(80);
                return CreateEntry(sizeBytes: 64);
            })))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(1, loadCount);
        Assert.Equal(1, store.EntryCount);
        Assert.All(tasks, task => Assert.Equal(64, task.Result.SizeBytes));
    }

    [Fact]
    public void GetOrLoad_reuses_disk_cached_entry_after_memory_cache_is_recreated()
    {
        using var paths = TestPaths.Create();
        var cacheDirectory = paths.Combine("preview-cache");
        var diskStore = new PreviewImageDiskCacheStore(cacheDirectory, maxBytes: 1024);
        var key = new PreviewImageCacheKey(paths.Combine("a.jpg"), 10, 1, 160, PreferCompatibilityDecoder: false);
        var loadCount = 0;

        var firstStore = new PreviewImageCacheStore(maxBytes: 0, diskStore);
        var first = firstStore.GetOrLoad(key, () =>
        {
            loadCount++;
            return CreateEntry(sizeBytes: 32);
        });

        var secondStore = new PreviewImageCacheStore(maxBytes: 0, diskStore);
        var second = secondStore.GetOrLoad(key, () =>
        {
            loadCount++;
            return CreateEntry(sizeBytes: 64);
        });

        Assert.Equal(1, loadCount);
        Assert.Equal(first.EncodedBytes, second.EncodedBytes);
        Assert.Equal("\u9884\u89C8\u5DF2\u52A0\u8F7D", second.StatusText);
    }

    [Fact]
    public void GetOrLoad_does_not_keep_single_entry_that_exceeds_memory_budget()
    {
        var store = new PreviewImageCacheStore(maxBytes: 64);
        var key = new PreviewImageCacheKey("E:\\images\\huge.jpg", 10, 1, 1600, PreferCompatibilityDecoder: false);
        var loadCount = 0;

        var first = store.GetOrLoad(key, () =>
        {
            loadCount++;
            return CreateEntry(sizeBytes: 96);
        });

        var second = store.GetOrLoad(key, () =>
        {
            loadCount++;
            return CreateEntry(sizeBytes: 96);
        });

        Assert.Equal(2, loadCount);
        Assert.Equal(96, first.SizeBytes);
        Assert.Equal(96, second.SizeBytes);
        Assert.Equal(0, store.EntryCount);
        Assert.Equal(0, store.CurrentBytes);
    }

    [Fact]
    public void UpdateBudgets_evicts_existing_entry_when_budget_shrinks_below_single_item_size()
    {
        var store = new PreviewImageCacheStore(maxBytes: 128);
        var key = new PreviewImageCacheKey("E:\\images\\preview.jpg", 10, 1, 1600, PreferCompatibilityDecoder: false);

        _ = store.GetOrLoad(key, () => CreateEntry(sizeBytes: 96));
        Assert.Equal(1, store.EntryCount);
        Assert.Equal(96, store.CurrentBytes);

        store.UpdateBudgets(maxBytes: 48, diskMaxBytes: 0);

        Assert.Equal(0, store.EntryCount);
        Assert.Equal(0, store.CurrentBytes);
    }

    private static PreviewImageCacheEntry CreateEntry(int sizeBytes)
    {
        return new PreviewImageCacheEntry(new byte[sizeBytes], "ok", "cached");
    }
}
