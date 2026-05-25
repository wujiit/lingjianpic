using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Tests;

public sealed class PreviewImageDiskCacheStoreTests
{
    [Fact]
    public void TrySave_and_TryLoad_roundtrip_cached_preview_bytes()
    {
        using var paths = TestPaths.Create();
        var cacheDirectory = paths.Combine("preview-cache");
        var store = new PreviewImageDiskCacheStore(cacheDirectory, maxBytes: 1024);
        var key = new PreviewImageCacheKey(paths.Combine("a.jpg"), 120, 10, 640, PreferCompatibilityDecoder: false);
        var entry = new PreviewImageCacheEntry([1, 2, 3, 4], "ok", "memory");

        store.TrySave(key, entry);
        var loaded = store.TryLoad(key);

        Assert.NotNull(loaded);
        Assert.Equal(entry.EncodedBytes, loaded.EncodedBytes);
        Assert.Equal("\u9884\u89C8\u5DF2\u52A0\u8F7D", loaded.StatusText);
        Assert.Equal("\u5F53\u524D\u590D\u7528\u4E86\u672C\u5730\u78C1\u76D8\u7F13\u5B58\u3002", loaded.DetailsText);
    }

    [Fact]
    public void TrySave_trims_older_cache_files_when_budget_is_exceeded()
    {
        using var paths = TestPaths.Create();
        var cacheDirectory = paths.Combine("preview-cache");
        var store = new PreviewImageDiskCacheStore(cacheDirectory, maxBytes: 10);
        var firstKey = new PreviewImageCacheKey(paths.Combine("a.jpg"), 10, 1, 160, PreferCompatibilityDecoder: false);
        var secondKey = new PreviewImageCacheKey(paths.Combine("b.jpg"), 10, 2, 160, PreferCompatibilityDecoder: false);

        store.TrySave(firstKey, new PreviewImageCacheEntry(new byte[6], "ok", "first"));
        foreach (var file in Directory.GetFiles(cacheDirectory, "*.bin", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc));
        }

        store.TrySave(secondKey, new PreviewImageCacheEntry(new byte[6], "ok", "second"));

        Assert.Equal(1, store.GetCacheFileCount());
        Assert.True(store.GetCurrentBytes() <= 10);
        Assert.Null(store.TryLoad(firstKey));
        Assert.NotNull(store.TryLoad(secondKey));
    }

    [Fact]
    public void TrySave_skips_entry_that_is_larger_than_budget()
    {
        using var paths = TestPaths.Create();
        var cacheDirectory = paths.Combine("preview-cache");
        var store = new PreviewImageDiskCacheStore(cacheDirectory, maxBytes: 8);
        var key = new PreviewImageCacheKey(paths.Combine("huge.jpg"), 10, 1, 160, PreferCompatibilityDecoder: false);

        store.TrySave(key, new PreviewImageCacheEntry(new byte[12], "ok", "oversized"));

        Assert.Equal(0, store.GetCacheFileCount());
        Assert.Null(store.TryLoad(key));
    }

    [Fact]
    public void TryLoad_ignores_existing_cache_file_that_is_larger_than_budget()
    {
        using var paths = TestPaths.Create();
        var cacheDirectory = paths.Combine("preview-cache");
        var key = new PreviewImageCacheKey(paths.Combine("a.jpg"), 120, 10, 640, PreferCompatibilityDecoder: false);

        var writer = new PreviewImageDiskCacheStore(cacheDirectory, maxBytes: 1024);
        writer.TrySave(key, new PreviewImageCacheEntry(new byte[16], "ok", "memory"));
        Assert.Equal(1, writer.GetCacheFileCount());

        var reader = new PreviewImageDiskCacheStore(cacheDirectory, maxBytes: 8);
        var loaded = reader.TryLoad(key);

        Assert.Null(loaded);
        Assert.Equal(0, reader.GetCacheFileCount());
    }

    [Fact]
    public void TrySave_keeps_preview_and_thumbnail_entries_in_separate_buckets()
    {
        using var paths = TestPaths.Create();
        var cacheDirectory = paths.Combine("preview-cache");
        var store = new PreviewImageDiskCacheStore(cacheDirectory, previewMaxBytes: 10, thumbnailMaxBytes: 10);
        var previewKey = new PreviewImageCacheKey(paths.Combine("preview.jpg"), 10, 1, 1600, PreferCompatibilityDecoder: false);
        var thumbnailKey = new PreviewImageCacheKey(paths.Combine("thumb.jpg"), 10, 1, 160, PreferCompatibilityDecoder: true);

        store.TrySave(previewKey, new PreviewImageCacheEntry(new byte[6], "ok", "preview"));
        store.TrySave(thumbnailKey, new PreviewImageCacheEntry(new byte[6], "ok", "thumbnail"));

        Assert.Equal(2, store.GetCacheFileCount());
        Assert.NotNull(store.TryLoad(previewKey));
        Assert.NotNull(store.TryLoad(thumbnailKey));
    }

    [Fact]
    public void UpdateBudgets_can_disable_preview_bucket_without_removing_thumbnail_entries()
    {
        using var paths = TestPaths.Create();
        var cacheDirectory = paths.Combine("preview-cache");
        var store = new PreviewImageDiskCacheStore(cacheDirectory, previewMaxBytes: 1024, thumbnailMaxBytes: 1024);
        var previewKey = new PreviewImageCacheKey(paths.Combine("preview.jpg"), 10, 1, 1600, PreferCompatibilityDecoder: false);
        var thumbnailKey = new PreviewImageCacheKey(paths.Combine("thumb.jpg"), 10, 1, 160, PreferCompatibilityDecoder: true);

        store.TrySave(previewKey, new PreviewImageCacheEntry(new byte[32], "ok", "preview"));
        store.TrySave(thumbnailKey, new PreviewImageCacheEntry(new byte[32], "ok", "thumbnail"));

        store.UpdateBudgets(previewMaxBytes: 0, thumbnailMaxBytes: 1024);

        Assert.Null(store.TryLoad(previewKey));
        Assert.NotNull(store.TryLoad(thumbnailKey));
    }
}
