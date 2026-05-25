using ModernImageViewer.Desktop.Services;
using ModernImageViewer.Desktop.ViewModels;

namespace ModernImageViewer.Tests;

public sealed class ThumbnailWarmupSchedulingTests
{
    [Fact]
    public void BuildThumbnailWarmupIndices_prioritizes_selected_item_and_neighbors()
    {
        var indices = MainWindowViewModel.BuildThumbnailWarmupIndices(totalCount: 8, selectedIndex: 4, maxCount: 6);

        Assert.Equal([4, 5, 3, 6, 2, 7], indices);
    }

    [Fact]
    public void BuildThumbnailWarmupIndices_falls_back_to_leading_items_when_selection_is_invalid()
    {
        var indices = MainWindowViewModel.BuildThumbnailWarmupIndices(totalCount: 6, selectedIndex: -1, maxCount: 4);

        Assert.Equal([0, 1, 2, 3], indices);
    }

    [Fact]
    public void CalculateThumbnailWarmupWorkerCount_preserves_foreground_capacity()
    {
        var actual = MainWindowViewModel.CalculateThumbnailWarmupWorkerCount(
            itemCount: 80,
            totalInputBytes: 0,
            DesktopProcessingPerformanceMode.Balanced,
            threadLimit: 4,
            thumbnailLoadGateCount: 3);

        Assert.Equal(2, actual);
    }

    [Fact]
    public void CalculateThumbnailWarmupWorkerCount_reduces_for_heavy_files()
    {
        var actual = MainWindowViewModel.CalculateThumbnailWarmupWorkerCount(
            itemCount: 40,
            totalInputBytes: 40L * 80L * 1024L * 1024L,
            DesktopProcessingPerformanceMode.HighPerformance,
            threadLimit: 6,
            thumbnailLoadGateCount: 3);

        Assert.Equal(1, actual);
    }

    [Fact]
    public void CalculateThumbnailWarmupWorkerCount_throttles_background_workers_while_preview_is_loading()
    {
        var actual = MainWindowViewModel.CalculateThumbnailWarmupWorkerCount(
            itemCount: 80,
            totalInputBytes: 0,
            DesktopProcessingPerformanceMode.HighPerformance,
            threadLimit: 6,
            thumbnailLoadGateCount: 3,
            isForegroundPreviewLoading: true);

        Assert.Equal(1, actual);
    }

    [Fact]
    public void CalculateDeferredThumbnailWarmupItemCount_reduces_scope_while_collection_is_loading()
    {
        var actual = MainWindowViewModel.CalculateDeferredThumbnailWarmupItemCount(
            plannedItemCount: 24,
            isForegroundPreviewLoading: false,
            isCollectionLoading: true,
            memoryPressureLevel: DesktopMemoryPressureLevel.Low);

        Assert.InRange(actual, 8, 12);
    }

    [Fact]
    public void BuildThumbnailWarmupItems_skips_items_that_are_already_loading()
    {
        var items = CreateItems("one.jpg", "two.jpg", "three.jpg", "four.jpg");
        items[1].SetThumbnailLoading(true);

        var actual = MainWindowViewModel.BuildThumbnailWarmupItems(items, items[0], maxCount: 3);

        Assert.Equal(["one.jpg", "three.jpg", "four.jpg"], actual.Select(static item => item.FileName));
    }

    [Fact]
    public void BuildThumbnailWarmupItems_keeps_searching_until_budget_is_filled()
    {
        var items = CreateItems("one.jpg", "two.jpg", "three.jpg", "four.jpg", "five.jpg", "six.jpg");
        items[0].SetThumbnailLoading(true);
        items[1].SetThumbnailLoading(true);
        items[3].SetThumbnailLoading(true);

        var actual = MainWindowViewModel.BuildThumbnailWarmupItems(items, items[2], maxCount: 3);

        Assert.Equal(["three.jpg", "five.jpg", "six.jpg"], actual.Select(static item => item.FileName));
    }

    [Fact]
    public void NeedsThumbnailWarmup_returns_false_for_loaded_or_loading_items()
    {
        Assert.False(MainWindowViewModel.NeedsThumbnailWarmup(hasThumbnail: true, isThumbnailLoading: false));
        Assert.False(MainWindowViewModel.NeedsThumbnailWarmup(hasThumbnail: false, isThumbnailLoading: true));
        Assert.True(MainWindowViewModel.NeedsThumbnailWarmup(hasThumbnail: false, isThumbnailLoading: false));
    }

    [Fact]
    public void BuildPreviewWarmupItems_skips_current_item_and_orders_neighbors()
    {
        var items = CreateItems("one.jpg", "two.jpg", "three.jpg", "four.jpg", "five.jpg");

        var actual = MainWindowViewModel.BuildPreviewWarmupItems(items, items[2], maxCount: 3);

        Assert.Equal(["four.jpg", "two.jpg", "five.jpg"], actual.Select(static item => item.FileName));
    }

    private static ImageListItemViewModel[] CreateItems(params string[] fileNames)
    {
        return fileNames
            .Select((fileName, index) => new ImageListItemViewModel(new ModernImageViewer.Core.ImageRecord(
                $@"E:\img\{fileName}",
                fileName,
                index + 1,
                new DateTimeOffset(2026, 4, 21, 10, 0, 0, TimeSpan.Zero).AddMinutes(index))))
            .ToArray();
    }
}
