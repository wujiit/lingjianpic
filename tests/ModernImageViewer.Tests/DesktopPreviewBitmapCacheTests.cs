using System.Runtime.Serialization;
using Avalonia.Media.Imaging;
using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Tests;

public sealed class DesktopPreviewBitmapCacheTests
{
    [Fact]
    public void TryGet_returns_cached_entry_when_decode_size_is_sufficient()
    {
        var cache = new DesktopPreviewBitmapCache(capacity: 4);
        var bitmap = CreateBitmapPlaceholder();

        var stored = cache.StoreOrUpdate(@"E:\images\a.jpg", 1600, bitmap, "ok", "details");
        var found = cache.TryGet(@"E:\images\a.jpg", 1200, out var cached);

        Assert.True(found);
        Assert.Same(stored.Bitmap, cached.Bitmap);
        Assert.Equal(1600, cached.DecodeLongEdge);
        Assert.Equal("ok", cached.StatusText);
        Assert.Equal("details", cached.DetailsText);
        Assert.True(cache.ContainsBitmap(bitmap));
    }

    [Fact]
    public void StoreOrUpdate_can_promote_decode_budget_for_same_path_without_replacing_bitmap_instance()
    {
        var cache = new DesktopPreviewBitmapCache(capacity: 4);
        var bitmap = CreateBitmapPlaceholder();

        var low = cache.StoreOrUpdate(@"E:\images\a.jpg", 1200, bitmap, "low", "first");
        var high = cache.StoreOrUpdate(@"E:\images\a.jpg", 2200, bitmap, "high", "second");

        Assert.Same(low.Bitmap, high.Bitmap);
        Assert.Equal(1, cache.EntryCount);
        Assert.True(cache.TryGet(@"E:\images\a.jpg", 1800, out var cached));
        Assert.Same(bitmap, cached.Bitmap);
        Assert.Equal(2200, cached.DecodeLongEdge);
    }

    [Fact]
    public void ContainsBitmap_returns_false_for_uncached_instance()
    {
        var cache = new DesktopPreviewBitmapCache(capacity: 2);
        var cachedBitmap = CreateBitmapPlaceholder();
        var uncachedBitmap = CreateBitmapPlaceholder();

        cache.StoreOrUpdate(@"E:\images\a.jpg", 1400, cachedBitmap, "cached", "entry");

        Assert.True(cache.ContainsBitmap(cachedBitmap));
        Assert.False(cache.ContainsBitmap(uncachedBitmap));
    }

    [Fact]
    public void TryGetBestAvailable_returns_lower_resolution_entry_when_exact_budget_is_not_met()
    {
        var cache = new DesktopPreviewBitmapCache(capacity: 2);
        var bitmap = CreateBitmapPlaceholder();

        cache.StoreOrUpdate(@"E:\images\a.jpg", 1400, bitmap, "warm", "preview");

        Assert.False(cache.TryGet(@"E:\images\a.jpg", 2200, out _));
        Assert.True(cache.TryGetBestAvailable(@"E:\images\a.jpg", out var cached));
        Assert.Same(bitmap, cached.Bitmap);
        Assert.Equal(1400, cached.DecodeLongEdge);
    }

    private static Bitmap CreateBitmapPlaceholder()
    {
#pragma warning disable SYSLIB0050
        return (Bitmap)FormatterServices.GetUninitializedObject(typeof(Bitmap));
#pragma warning restore SYSLIB0050
    }
}
