using ModernImageViewer.Core;
using ModernImageViewer.Desktop.ViewModels;

namespace ModernImageViewer.Tests;

public sealed class ComparePreviewSchedulingTests
{
    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    public void CanQueueComparePreviewLoad_only_queues_when_item_has_no_preview_and_no_pending_work(
        bool hasPreview,
        bool isPreviewLoading,
        bool hasPendingRequest,
        bool expected)
    {
        var actual = MainWindowViewModel.CanQueueComparePreviewLoad(hasPreview, isPreviewLoading, hasPendingRequest);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(360, 260, 292, 220, 100, 1, 1200)]
    [InlineData(360, 260, 292, 220, 100, 4, 960)]
    [InlineData(360, 260, 292, 220, 250, 2, 2820)]
    [InlineData(360, 260, 292, 220, 300, 1, 3200)]
    public void CalculateComparePreviewDecodeLongEdge_adapts_to_zoom_and_compare_density(
        double mainItemWidth,
        double mainPreviewHeight,
        double toolboxItemWidth,
        double toolboxPreviewHeight,
        double zoomPercent,
        int compareItemCount,
        int expected)
    {
        var actual = MainWindowViewModel.CalculateComparePreviewDecodeLongEdge(
            mainItemWidth,
            mainPreviewHeight,
            toolboxItemWidth,
            toolboxPreviewHeight,
            zoomPercent,
            compareItemCount);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(1500, 1200, true)]
    [InlineData(1420, 1200, false)]
    [InlineData(960, 960, false)]
    public void ShouldReloadComparePreviewForGrowth_only_reloads_for_meaningful_growth(
        int desiredLongEdge,
        int loadedLongEdge,
        bool expected)
    {
        var actual = MainWindowViewModel.ShouldReloadComparePreviewForGrowth(desiredLongEdge, loadedLongEdge);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(1400, 1200, false)]
    [InlineData(1200, 1200, true)]
    [InlineData(900, 1200, true)]
    public void CanReuseComparePreview_requires_loaded_preview_to_be_large_enough(
        int requestedLongEdge,
        int loadedLongEdge,
        bool expected)
    {
        var actual = MainWindowViewModel.CanReuseComparePreview(loadedLongEdge, requestedLongEdge);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CompareImageItemViewModel_loading_state_resets_when_marked_unavailable()
    {
        var record = new ImageRecord(
            FullPath: "E:\\images\\a.jpg",
            FileName: "a.jpg",
            SizeBytes: 1024,
            ModifiedAt: DateTimeOffset.Parse("2025-01-01T10:00:00+08:00"));
        var source = new ImageListItemViewModel(record);
        var item = new CompareImageItemViewModel(source);

        item.MarkLoading();
        Assert.True(item.IsPreviewLoading);

        item.MarkUnavailable("无法预览", "测试");

        Assert.False(item.IsPreviewLoading);
        Assert.False(item.HasPreview);
    }
}
