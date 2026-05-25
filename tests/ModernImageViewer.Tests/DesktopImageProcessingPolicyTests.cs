using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Tests;

public sealed class DesktopImageProcessingPolicyTests
{
    [Theory]
    [InlineData(DesktopProcessingPerformanceMode.Quiet, 8, 2)]
    [InlineData(DesktopProcessingPerformanceMode.Balanced, 8, 4)]
    [InlineData(DesktopProcessingPerformanceMode.HighPerformance, 8, 6)]
    [InlineData(DesktopProcessingPerformanceMode.Quiet, 4, 1)]
    [InlineData(DesktopProcessingPerformanceMode.Balanced, 4, 1)]
    [InlineData(DesktopProcessingPerformanceMode.HighPerformance, 4, 3)]
    public void CalculateThreadLimit_varies_by_mode(
        DesktopProcessingPerformanceMode mode,
        int processorCount,
        int expected)
    {
        var actual = DesktopImageProcessingPolicy.CalculateThreadLimit(mode, processorCount);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(DesktopProcessingPerformanceMode.Quiet, 8, 1)]
    [InlineData(DesktopProcessingPerformanceMode.Balanced, 8, 2)]
    [InlineData(DesktopProcessingPerformanceMode.HighPerformance, 8, 3)]
    [InlineData(DesktopProcessingPerformanceMode.Quiet, 4, 1)]
    [InlineData(DesktopProcessingPerformanceMode.Balanced, 4, 1)]
    [InlineData(DesktopProcessingPerformanceMode.HighPerformance, 4, 2)]
    public void CalculateMagickOperationLimit_varies_by_mode(
        DesktopProcessingPerformanceMode mode,
        int processorCount,
        int expected)
    {
        var actual = DesktopImageProcessingPolicy.CalculateMagickOperationLimit(mode, processorCount);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(DesktopProcessingPerformanceMode.Quiet, 2048, 4096)]
    [InlineData(DesktopProcessingPerformanceMode.Balanced, 4096, 8192)]
    [InlineData(DesktopProcessingPerformanceMode.HighPerformance, 8192, 16384)]
    public void Fingerprint_cache_limits_vary_by_mode(
        DesktopProcessingPerformanceMode mode,
        int expectedTextEntries,
        int expectedDifferenceHashEntries)
    {
        var actualTextEntries = DesktopImageProcessingPolicy.CalculateFingerprintTextCacheEntryLimit(mode);
        var actualDifferenceHashEntries = DesktopImageProcessingPolicy.CalculateFingerprintDifferenceHashCacheEntryLimit(mode);

        Assert.Equal(expectedTextEntries, actualTextEntries);
        Assert.Equal(expectedDifferenceHashEntries, actualDifferenceHashEntries);
    }

    [Theory]
    [InlineData(DesktopProcessingPerformanceMode.Quiet, 2048)]
    [InlineData(DesktopProcessingPerformanceMode.Balanced, 4096)]
    [InlineData(DesktopProcessingPerformanceMode.HighPerformance, 8192)]
    public void Image_dimension_cache_limit_varies_by_mode(
        DesktopProcessingPerformanceMode mode,
        int expectedEntries)
    {
        var actual = DesktopImageProcessingPolicy.CalculateImageDimensionCacheEntryLimit(mode);

        Assert.Equal(expectedEntries, actual);
    }

    [Fact]
    public void ShouldTrimMemory_allows_first_trim_when_no_previous_timestamp_exists()
    {
        Assert.True(DesktopImageProcessingPolicy.ShouldTrimMemory(
            nowTimestamp: 100,
            lastTrimTimestamp: 0,
            minimumIntervalTicks: 50));
    }

    [Fact]
    public void ShouldTrimMemory_blocks_calls_within_minimum_interval()
    {
        Assert.False(DesktopImageProcessingPolicy.ShouldTrimMemory(
            nowTimestamp: 149,
            lastTrimTimestamp: 100,
            minimumIntervalTicks: 50));
    }

    [Fact]
    public void ShouldTrimMemory_allows_calls_after_minimum_interval()
    {
        Assert.True(DesktopImageProcessingPolicy.ShouldTrimMemory(
            nowTimestamp: 150,
            lastTrimTimestamp: 100,
            minimumIntervalTicks: 50));
    }

    [Theory]
    [InlineData(1024, 900, 320, 280, "Low")]
    [InlineData(1024, 900, 610, 280, "Moderate")]
    [InlineData(1024, 900, 735, 280, "High")]
    [InlineData(1024, 900, 840, 280, "Critical")]
    public void ClassifyMemoryPressure_uses_highest_observed_ratio(
        long totalAvailableMemoryBytes,
        long highMemoryLoadThresholdBytes,
        long memoryLoadBytes,
        long processWorkingSetBytes,
        string expected)
    {
        var actual = DesktopMemoryPressureMonitor.ClassifyMemoryPressure(
            totalAvailableMemoryBytes,
            highMemoryLoadThresholdBytes,
            memoryLoadBytes,
            heapSizeBytes: 320,
            processWorkingSetBytes);

        Assert.Equal(expected, actual.ToString());
    }

    [Fact]
    public void ClassifyMemoryPressure_falls_back_to_heap_and_working_set_when_threshold_is_missing()
    {
        var actual = DesktopMemoryPressureMonitor.ClassifyMemoryPressure(
            totalAvailableMemoryBytes: 1024,
            highMemoryLoadThresholdBytes: 0,
            memoryLoadBytes: 0,
            heapSizeBytes: 860,
            processWorkingSetBytes: 720);

        Assert.Equal(DesktopMemoryPressureLevel.High, actual);
    }
}
