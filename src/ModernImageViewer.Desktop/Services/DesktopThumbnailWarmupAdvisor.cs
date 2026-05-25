namespace ModernImageViewer.Desktop.Services;

internal readonly record struct DesktopThumbnailWarmupPlan(
    int MaxItemCount,
    bool PrioritizeSelection);

internal static class DesktopThumbnailWarmupAdvisor
{
    private const long Megabyte = 1024L * 1024L;
    private const double ApproxSidebarItemHeight = 96;
    private const double ContactSheetGap = 12;
    private const int SidebarMinWarmupItems = 16;
    private const int SidebarMaxWarmupItems = 72;
    private const int ContactSheetMinWarmupItems = 24;
    private const int ContactSheetMaxWarmupItems = 180;
    private const long LargeAverageInputBytes = 24L * Megabyte;
    private const long HugeAverageInputBytes = 64L * Megabyte;
    private const long LargeWarmupBatchBytes = 768L * Megabyte;
    private const long HugeWarmupBatchBytes = 1536L * Megabyte;

    public static DesktopThumbnailWarmupPlan CreatePlan(
        int totalCount,
        bool isContactSheetVisible,
        double sidebarViewportHeight,
        int contactSheetColumns,
        double contactSheetViewportHeight,
        double contactSheetTileHeight)
    {
        var memoryPressureLevel = DesktopImageProcessingPolicy.GetMemoryPressureLevel();
        return CreatePlan(
            totalCount,
            isContactSheetVisible,
            sidebarViewportHeight,
            contactSheetColumns,
            contactSheetViewportHeight,
            contactSheetTileHeight,
            DesktopImageProcessingPolicy.CurrentPerformanceMode,
            memoryPressureLevel);
    }

    internal static DesktopThumbnailWarmupPlan CreatePlan(
        int totalCount,
        bool isContactSheetVisible,
        double sidebarViewportHeight,
        int contactSheetColumns,
        double contactSheetViewportHeight,
        double contactSheetTileHeight,
        DesktopProcessingPerformanceMode performanceMode,
        DesktopMemoryPressureLevel memoryPressureLevel = DesktopMemoryPressureLevel.Low)
    {
        if (totalCount <= 0)
        {
            return new DesktopThumbnailWarmupPlan(0, PrioritizeSelection: true);
        }

        if (isContactSheetVisible)
        {
            return new DesktopThumbnailWarmupPlan(
                CalculateContactSheetItemLimit(
                    totalCount,
                    contactSheetColumns,
                    contactSheetViewportHeight,
                    contactSheetTileHeight,
                    performanceMode,
                    memoryPressureLevel),
                PrioritizeSelection: false);
        }

        return new DesktopThumbnailWarmupPlan(
            CalculateSidebarItemLimit(totalCount, sidebarViewportHeight, performanceMode, memoryPressureLevel),
            PrioritizeSelection: true);
    }

    public static int CalculateWorkerCount(
        int pendingItemCount,
        long totalInputBytes,
        int threadLimit,
        int thumbnailLoadGateCount)
    {
        var memoryPressureLevel = DesktopImageProcessingPolicy.GetMemoryPressureLevel();
        return CalculateWorkerCount(
            pendingItemCount,
            totalInputBytes,
            DesktopImageProcessingPolicy.CurrentPerformanceMode,
            threadLimit,
            thumbnailLoadGateCount,
            isForegroundPreviewLoading: false,
            isCollectionLoading: false,
            memoryPressureLevel);
    }

    internal static int CalculateWorkerCount(
        int pendingItemCount,
        long totalInputBytes,
        DesktopProcessingPerformanceMode performanceMode,
        int threadLimit,
        int thumbnailLoadGateCount,
        bool isForegroundPreviewLoading,
        bool isCollectionLoading,
        DesktopMemoryPressureLevel memoryPressureLevel = DesktopMemoryPressureLevel.Low)
    {
        if (pendingItemCount <= 0 || thumbnailLoadGateCount <= 0)
        {
            return 0;
        }

        if (pendingItemCount <= 12
            || performanceMode == DesktopProcessingPerformanceMode.Quiet
            || memoryPressureLevel >= DesktopMemoryPressureLevel.High)
        {
            return 1;
        }

        if (isForegroundPreviewLoading || isCollectionLoading)
        {
            return 1;
        }

        var foregroundReserve = 1;
        var maxBackgroundSlots = Math.Max(1, thumbnailLoadGateCount - foregroundReserve);
        var limit = Math.Clamp(Math.Min(threadLimit, maxBackgroundSlots), 1, Math.Min(pendingItemCount, maxBackgroundSlots));
        if (limit <= 1)
        {
            return 1;
        }

        var averageInputBytes = totalInputBytes > 0
            ? Math.Max(1L, totalInputBytes / pendingItemCount)
            : 0L;
        if (averageInputBytes >= HugeAverageInputBytes || totalInputBytes >= HugeWarmupBatchBytes)
        {
            return 1;
        }

        if (averageInputBytes >= LargeAverageInputBytes || totalInputBytes >= LargeWarmupBatchBytes)
        {
            return Math.Min(limit, performanceMode == DesktopProcessingPerformanceMode.HighPerformance ? 2 : 1);
        }

        return memoryPressureLevel == DesktopMemoryPressureLevel.Moderate
            ? Math.Min(limit, 2)
            : limit;
    }

    internal static int CalculateDeferredItemLimit(
        int plannedItemCount,
        bool isForegroundPreviewLoading,
        bool isCollectionLoading,
        DesktopMemoryPressureLevel memoryPressureLevel = DesktopMemoryPressureLevel.Low)
    {
        if (plannedItemCount <= 0)
        {
            return 0;
        }

        var adjusted = plannedItemCount;
        if (isCollectionLoading)
        {
            adjusted = Math.Max(8, (int)Math.Ceiling(adjusted * 0.5));
        }

        if (isForegroundPreviewLoading)
        {
            adjusted = Math.Max(6, (int)Math.Ceiling(adjusted * 0.66));
        }

        adjusted = memoryPressureLevel switch
        {
            DesktopMemoryPressureLevel.Critical => Math.Max(6, Math.Min(adjusted, plannedItemCount / 3)),
            DesktopMemoryPressureLevel.High => Math.Max(8, Math.Min(adjusted, plannedItemCount / 2)),
            _ => adjusted
        };

        return Math.Clamp(adjusted, 1, plannedItemCount);
    }

    internal static int CalculateSidebarItemLimit(
        int totalCount,
        double viewportHeight,
        DesktopProcessingPerformanceMode performanceMode,
        DesktopMemoryPressureLevel memoryPressureLevel = DesktopMemoryPressureLevel.Low)
    {
        if (totalCount <= 0)
        {
            return 0;
        }

        var normalizedHeight = Math.Max(320, viewportHeight);
        var visibleItems = Math.Clamp((int)Math.Ceiling((normalizedHeight - 32) / ApproxSidebarItemHeight), 4, 14);
        var bufferScreens = performanceMode switch
        {
            DesktopProcessingPerformanceMode.Quiet => 1.5,
            DesktopProcessingPerformanceMode.HighPerformance => 2.5,
            _ => 2.0
        };
        var target = (int)Math.Ceiling(visibleItems * bufferScreens) + 4;
        target = ApplyMemoryPressureItemCap(target, memoryPressureLevel, SidebarMinWarmupItems);
        return Math.Min(totalCount, Math.Clamp(target, SidebarMinWarmupItems, SidebarMaxWarmupItems));
    }

    internal static int CalculateContactSheetItemLimit(
        int totalCount,
        int columns,
        double viewportHeight,
        double tileHeight,
        DesktopProcessingPerformanceMode performanceMode,
        DesktopMemoryPressureLevel memoryPressureLevel = DesktopMemoryPressureLevel.Low)
    {
        if (totalCount <= 0)
        {
            return 0;
        }

        var safeColumns = Math.Max(1, columns);
        var safeTileHeight = Math.Max(96, tileHeight);
        var normalizedHeight = Math.Max(safeTileHeight + ContactSheetGap, viewportHeight);
        var visibleRows = Math.Clamp(
            (int)Math.Ceiling((normalizedHeight + ContactSheetGap) / (safeTileHeight + ContactSheetGap)),
            1,
            8);
        var overscanRows = performanceMode switch
        {
            DesktopProcessingPerformanceMode.Quiet => 1,
            DesktopProcessingPerformanceMode.HighPerformance => 3,
            _ => 2
        };
        var target = safeColumns * (visibleRows + overscanRows);
        target = ApplyMemoryPressureItemCap(target, memoryPressureLevel, ContactSheetMinWarmupItems);
        return Math.Min(totalCount, Math.Clamp(target, ContactSheetMinWarmupItems, ContactSheetMaxWarmupItems));
    }

    private static int ApplyMemoryPressureItemCap(
        int target,
        DesktopMemoryPressureLevel memoryPressureLevel,
        int minimumItemCount)
    {
        return memoryPressureLevel switch
        {
            DesktopMemoryPressureLevel.Critical => Math.Max(Math.Max(8, minimumItemCount / 2), target / 3),
            DesktopMemoryPressureLevel.High => Math.Max(Math.Max(12, minimumItemCount / 2), target / 2),
            DesktopMemoryPressureLevel.Moderate => Math.Max(minimumItemCount, (int)Math.Ceiling(target * 0.75)),
            _ => target
        };
    }
}
