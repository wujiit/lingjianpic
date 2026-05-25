using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Tests;

public sealed class DesktopThumbnailWarmupAdvisorTests
{
    private const long Megabyte = 1024L * 1024L;

    [Fact]
    public void CreatePlan_in_single_preview_mode_prioritizes_selection_and_uses_smaller_budget()
    {
        var plan = DesktopThumbnailWarmupAdvisor.CreatePlan(
            totalCount: 240,
            isContactSheetVisible: false,
            sidebarViewportHeight: 720,
            contactSheetColumns: 4,
            contactSheetViewportHeight: 620,
            contactSheetTileHeight: 132,
            performanceMode: DesktopProcessingPerformanceMode.Balanced);

        Assert.True(plan.PrioritizeSelection);
        Assert.InRange(plan.MaxItemCount, 18, 28);
    }

    [Fact]
    public void CreatePlan_in_contact_sheet_mode_warms_more_items_from_top_of_list()
    {
        var plan = DesktopThumbnailWarmupAdvisor.CreatePlan(
            totalCount: 240,
            isContactSheetVisible: true,
            sidebarViewportHeight: 720,
            contactSheetColumns: 5,
            contactSheetViewportHeight: 760,
            contactSheetTileHeight: 148,
            performanceMode: DesktopProcessingPerformanceMode.Balanced);

        Assert.False(plan.PrioritizeSelection);
        Assert.InRange(plan.MaxItemCount, 25, 40);
    }

    [Fact]
    public void CalculateWorkerCount_reduces_to_single_worker_for_huge_input_batches()
    {
        var actual = DesktopThumbnailWarmupAdvisor.CalculateWorkerCount(
            pendingItemCount: 48,
            totalInputBytes: 48L * 72L * Megabyte,
            performanceMode: DesktopProcessingPerformanceMode.HighPerformance,
            threadLimit: 6,
            thumbnailLoadGateCount: 3,
            isForegroundPreviewLoading: false,
            isCollectionLoading: false);

        Assert.Equal(1, actual);
    }

    [Fact]
    public void CreatePlan_high_memory_pressure_reduces_sidebar_warmup_count()
    {
        var plan = DesktopThumbnailWarmupAdvisor.CreatePlan(
            totalCount: 240,
            isContactSheetVisible: false,
            sidebarViewportHeight: 720,
            contactSheetColumns: 4,
            contactSheetViewportHeight: 620,
            contactSheetTileHeight: 132,
            performanceMode: DesktopProcessingPerformanceMode.HighPerformance,
            memoryPressureLevel: DesktopMemoryPressureLevel.High);

        Assert.True(plan.PrioritizeSelection);
        Assert.InRange(plan.MaxItemCount, 12, 18);
    }

    [Fact]
    public void CalculateWorkerCount_high_memory_pressure_forces_single_worker()
    {
        var actual = DesktopThumbnailWarmupAdvisor.CalculateWorkerCount(
            pendingItemCount: 48,
            totalInputBytes: 48L * 8L * Megabyte,
            performanceMode: DesktopProcessingPerformanceMode.HighPerformance,
            threadLimit: 6,
            thumbnailLoadGateCount: 3,
            isForegroundPreviewLoading: false,
            isCollectionLoading: false,
            memoryPressureLevel: DesktopMemoryPressureLevel.High);

        Assert.Equal(1, actual);
    }

    [Fact]
    public void CalculateWorkerCount_reduces_to_single_worker_while_foreground_preview_is_busy()
    {
        var actual = DesktopThumbnailWarmupAdvisor.CalculateWorkerCount(
            pendingItemCount: 48,
            totalInputBytes: 48L * 4L * Megabyte,
            performanceMode: DesktopProcessingPerformanceMode.HighPerformance,
            threadLimit: 6,
            thumbnailLoadGateCount: 3,
            isForegroundPreviewLoading: true,
            isCollectionLoading: false);

        Assert.Equal(1, actual);
    }

    [Fact]
    public void CalculateDeferredItemLimit_reduces_background_scope_during_collection_load()
    {
        var actual = DesktopThumbnailWarmupAdvisor.CalculateDeferredItemLimit(
            plannedItemCount: 30,
            isForegroundPreviewLoading: false,
            isCollectionLoading: true);

        Assert.InRange(actual, 8, 15);
    }
}
