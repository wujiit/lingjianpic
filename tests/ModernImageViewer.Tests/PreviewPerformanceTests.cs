using ModernImageViewer.Desktop.ViewModels;

namespace ModernImageViewer.Tests;

public sealed class PreviewPerformanceTests
{
    [Theory]
    [InlineData(640, 420, true, 100, 900)]
    [InlineData(1200, 800, true, 100, 1620)]
    [InlineData(1800, 1100, true, 100, 2200)]
    [InlineData(900, 600, false, 100, 1600)]
    [InlineData(900, 600, false, 300, 3200)]
    public void CalculatePreviewDecodeLongEdge_adapts_to_viewport_and_zoom(
        double width,
        double height,
        bool isFitMode,
        double zoomPercent,
        int expected)
    {
        var actual = MainWindowViewModel.CalculatePreviewDecodeLongEdge(width, height, isFitMode, zoomPercent);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(900, 620, true, 100, 900, 3600, true, 4248)]
    [InlineData(900, 620, true, 100, 900, 9000, true, 6400)]
    [InlineData(900, 620, true, 100, 1200, 1800, true, 1215)]
    public void CalculatePreviewDecodeLongEdge_expands_budget_for_long_image_width_fit(
        double viewportWidth,
        double viewportHeight,
        bool isFitMode,
        double zoomPercent,
        double contentWidth,
        double contentHeight,
        bool preferWidthFit,
        int expected)
    {
        var actual = MainWindowViewModel.CalculatePreviewDecodeLongEdge(
            viewportWidth,
            viewportHeight,
            isFitMode,
            zoomPercent,
            contentWidth,
            contentHeight,
            preferWidthFit);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(900, 2500, true)]
    [InlineData(1200, 3100, false)]
    [InlineData(1200, 3200, true)]
    public void IsLongImageCandidate_matches_vertical_ratio_threshold(
        double width,
        double height,
        bool expected)
    {
        var actual = MainWindowViewModel.IsLongImageCandidate(width, height);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CanReuseSelectedPreview_returns_true_for_same_path_when_loaded_preview_is_large_enough()
    {
        var actual = MainWindowViewModel.CanReuseSelectedPreview(
            requestedPath: "E:\\images\\a.jpg",
            loadedPath: "E:\\images\\a.jpg",
            requestedLongEdge: 1600,
            loadedLongEdge: 1800);

        Assert.True(actual);
    }

    [Fact]
    public void CanReuseSelectedPreview_returns_false_for_different_path_or_smaller_preview()
    {
        Assert.False(MainWindowViewModel.CanReuseSelectedPreview(
            requestedPath: "E:\\images\\a.jpg",
            loadedPath: "E:\\images\\b.jpg",
            requestedLongEdge: 1600,
            loadedLongEdge: 1800));

        Assert.False(MainWindowViewModel.CanReuseSelectedPreview(
            requestedPath: "E:\\images\\a.jpg",
            loadedPath: "E:\\images\\a.jpg",
            requestedLongEdge: 2000,
            loadedLongEdge: 1600));
    }

    [Fact]
    public void CanReusePendingPreviewRequest_returns_true_for_same_path_when_pending_request_is_large_enough()
    {
        var actual = MainWindowViewModel.CanReusePendingPreviewRequest(
            requestedPath: "E:\\images\\a.jpg",
            pendingPath: "E:\\images\\a.jpg",
            requestedLongEdge: 1600,
            pendingLongEdge: 2200);

        Assert.True(actual);
    }

    [Theory]
    [InlineData(1800, 1400, true)]
    [InlineData(1680, 1400, false)]
    [InlineData(1200, 1200, false)]
    public void ShouldReloadSelectedPreviewForGrowth_only_reloads_when_growth_is_large_enough(
        int desiredLongEdge,
        int loadedLongEdge,
        bool expected)
    {
        var actual = MainWindowViewModel.ShouldReloadSelectedPreviewForGrowth(desiredLongEdge, loadedLongEdge);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildPreviewWarmupIndices_prioritizes_next_then_previous_neighbors()
    {
        var actual = MainWindowViewModel.BuildPreviewWarmupIndices(totalCount: 8, selectedIndex: 3, maxCount: 4);

        Assert.Equal([4, 2, 5, 1], actual);
    }

    [Fact]
    public void CalculatePreviewWarmupLongEdge_clamps_to_background_budget()
    {
        Assert.Equal(1400, MainWindowViewModel.CalculatePreviewWarmupLongEdge(900));
        Assert.Equal(2000, MainWindowViewModel.CalculatePreviewWarmupLongEdge(2000));
        Assert.Equal(2200, MainWindowViewModel.CalculatePreviewWarmupLongEdge(3200));
    }

    [Theory]
    [InlineData(2200, 8L * 1024 * 1024, 2200)]
    [InlineData(2200, 30L * 1024 * 1024, 1800)]
    [InlineData(2200, 60L * 1024 * 1024, 1400)]
    public void CalculatePreviewWarmupLongEdge_reduces_neighbor_budget_for_large_sources(
        int selectedPreviewLongEdge,
        long sourceSizeBytes,
        int expected)
    {
        var actual = MainWindowViewModel.CalculatePreviewWarmupLongEdge(selectedPreviewLongEdge, sourceSizeBytes);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(1, 120, false, true)]
    [InlineData(3, 320, false, true)]
    [InlineData(3, 1200, false, false)]
    [InlineData(6, 1200, false, false)]
    [InlineData(8, 2600, false, true)]
    [InlineData(9, 2600, false, false)]
    [InlineData(4, 1200, true, false)]
    public void ShouldRefreshProgressiveThumbnails_adapts_interval_for_large_lists_and_foreground_load(
        int batchCount,
        int totalImageCount,
        bool isForegroundPreviewLoading,
        bool expected)
    {
        var actual = MainWindowViewModel.ShouldRefreshProgressiveThumbnails(
            batchCount,
            totalImageCount,
            isForegroundPreviewLoading);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(false, 400, false)]
    [InlineData(true, 120, false)]
    [InlineData(true, 180, true)]
    [InlineData(true, 640, true)]
    public void ShouldDeferPreviewWarmup_only_defers_for_large_collections_still_loading(
        bool isCollectionLoading,
        int totalImageCount,
        bool expected)
    {
        var actual = MainWindowViewModel.ShouldDeferPreviewWarmup(isCollectionLoading, totalImageCount);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0, 8L * 1024 * 1024, true)]
    [InlineData(1, 8L * 1024 * 1024, true)]
    [InlineData(2, 8L * 1024 * 1024, false)]
    [InlineData(0, 30L * 1024 * 1024, true)]
    [InlineData(1, 30L * 1024 * 1024, false)]
    [InlineData(0, 60L * 1024 * 1024, false)]
    public void ShouldWarmPreviewBitmapCache_limits_bitmap_warmup_for_large_sources(
        int warmupIndex,
        long sourceSizeBytes,
        bool expected)
    {
        var actual = MainWindowViewModel.ShouldWarmPreviewBitmapCache(warmupIndex, sourceSizeBytes);

        Assert.Equal(expected, actual);
    }
}
