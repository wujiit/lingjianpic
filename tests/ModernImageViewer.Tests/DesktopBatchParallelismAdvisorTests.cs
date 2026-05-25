using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Tests;

public sealed class DesktopBatchParallelismAdvisorTests
{
    private const long Megabyte = 1024L * 1024L;

    [Fact]
    public void Calculate_returns_one_for_single_item()
    {
        var actual = DesktopBatchParallelismAdvisor.Calculate(
            DesktopBatchWorkloadKind.ExportTranscode,
            totalCount: 1,
            totalInputBytes: 200 * Megabyte,
            DesktopProcessingPerformanceMode.HighPerformance,
            threadLimit: 6,
            magickOperationLimit: 3);

        Assert.Equal(1, actual);
    }

    [Fact]
    public void Calculate_small_copy_batch_uses_copy_budget()
    {
        var actual = DesktopBatchParallelismAdvisor.Calculate(
            DesktopBatchWorkloadKind.FileCopy,
            totalCount: 12,
            totalInputBytes: 12 * 4 * Megabyte,
            DesktopProcessingPerformanceMode.Balanced,
            threadLimit: 6,
            magickOperationLimit: 2);

        Assert.Equal(4, actual);
    }

    [Fact]
    public void Calculate_magick_workload_is_capped_by_magick_limit()
    {
        var actual = DesktopBatchParallelismAdvisor.Calculate(
            DesktopBatchWorkloadKind.ExifEdit,
            totalCount: 8,
            totalInputBytes: 8 * 12 * Megabyte,
            DesktopProcessingPerformanceMode.HighPerformance,
            threadLimit: 6,
            magickOperationLimit: 2);

        Assert.Equal(2, actual);
    }

    [Fact]
    public void Calculate_huge_export_compression_batch_reduces_to_single_worker()
    {
        var actual = DesktopBatchParallelismAdvisor.Calculate(
            DesktopBatchWorkloadKind.ExportCompression,
            totalCount: 6,
            totalInputBytes: 6 * 120 * Megabyte,
            DesktopProcessingPerformanceMode.HighPerformance,
            threadLimit: 6,
            magickOperationLimit: 3);

        Assert.Equal(1, actual);
    }

    [Fact]
    public void Calculate_uses_supplied_performance_mode_instead_of_global_policy_state()
    {
        var quietLimit = DesktopBatchParallelismAdvisor.Calculate(
            DesktopBatchWorkloadKind.FileCopy,
            totalCount: 12,
            totalInputBytes: 12 * 4 * Megabyte,
            DesktopProcessingPerformanceMode.Quiet,
            threadLimit: 6,
            magickOperationLimit: 2);
        var highPerformanceLimit = DesktopBatchParallelismAdvisor.Calculate(
            DesktopBatchWorkloadKind.FileCopy,
            totalCount: 12,
            totalInputBytes: 12 * 4 * Megabyte,
            DesktopProcessingPerformanceMode.HighPerformance,
            threadLimit: 6,
            magickOperationLimit: 2);

        Assert.Equal(2, quietLimit);
        Assert.Equal(6, highPerformanceLimit);
    }

    [Fact]
    public void Calculate_file_move_is_more_conservative_than_copy()
    {
        var copyLimit = DesktopBatchParallelismAdvisor.Calculate(
            DesktopBatchWorkloadKind.FileCopy,
            totalCount: 10,
            totalInputBytes: 10 * 8 * Megabyte,
            DesktopProcessingPerformanceMode.HighPerformance,
            threadLimit: 6,
            magickOperationLimit: 3);
        var moveLimit = DesktopBatchParallelismAdvisor.Calculate(
            DesktopBatchWorkloadKind.FileMove,
            totalCount: 10,
            totalInputBytes: 10 * 8 * Megabyte,
            DesktopProcessingPerformanceMode.HighPerformance,
            threadLimit: 6,
            magickOperationLimit: 3);

        Assert.Equal(6, copyLimit);
        Assert.Equal(3, moveLimit);
    }

    [Fact]
    public void Calculate_large_copy_batch_scales_down_for_very_large_total_input()
    {
        var actual = DesktopBatchParallelismAdvisor.Calculate(
            DesktopBatchWorkloadKind.FileCopy,
            totalCount: 200,
            totalInputBytes: 7L * 1024L * Megabyte,
            DesktopProcessingPerformanceMode.HighPerformance,
            threadLimit: 6,
            magickOperationLimit: 3);

        Assert.Equal(3, actual);
    }

    [Fact]
    public void CreateExecutionPlan_heavy_magick_workload_tightens_progress_yield_and_trim_settings()
    {
        var plan = DesktopBatchParallelismAdvisor.CreateExecutionPlan(
            DesktopBatchWorkloadKind.ExportCompression,
            totalCount: 8,
            totalInputBytes: 8 * 32 * Megabyte,
            DesktopProcessingPerformanceMode.Balanced,
            threadLimit: 6,
            magickOperationLimit: 2);

        Assert.Equal(1, plan.MaxDegreeOfParallelism);
        Assert.Equal(TimeSpan.FromMilliseconds(190), plan.ProgressInterval);
        Assert.Equal(1, plan.ProgressStride);
        Assert.Equal(2, plan.YieldInterval);
        Assert.Equal(2, plan.MemoryTrimInterval);
    }

    [Fact]
    public void Calculate_large_watermark_batch_is_more_conservative_than_plain_transcode()
    {
        var watermarkLimit = DesktopBatchParallelismAdvisor.Calculate(
            DesktopBatchWorkloadKind.ExportWatermark,
            totalCount: 24,
            totalInputBytes: 24 * 8 * Megabyte,
            DesktopProcessingPerformanceMode.HighPerformance,
            threadLimit: 6,
            magickOperationLimit: 3);
        var transcodeLimit = DesktopBatchParallelismAdvisor.Calculate(
            DesktopBatchWorkloadKind.ExportTranscode,
            totalCount: 24,
            totalInputBytes: 24 * 8 * Megabyte,
            DesktopProcessingPerformanceMode.HighPerformance,
            threadLimit: 6,
            magickOperationLimit: 3);

        Assert.Equal(2, watermarkLimit);
        Assert.Equal(3, transcodeLimit);
    }

    [Fact]
    public void CreateExecutionPlan_light_copy_workload_keeps_wider_yield_and_trim_interval()
    {
        var plan = DesktopBatchParallelismAdvisor.CreateExecutionPlan(
            DesktopBatchWorkloadKind.FileCopy,
            totalCount: 24,
            totalInputBytes: 24 * 2 * Megabyte,
            DesktopProcessingPerformanceMode.HighPerformance,
            threadLimit: 6,
            magickOperationLimit: 3);

        Assert.Equal(6, plan.MaxDegreeOfParallelism);
        Assert.Equal(TimeSpan.FromMilliseconds(90), plan.ProgressInterval);
        Assert.Equal(1, plan.ProgressStride);
        Assert.Equal(7, plan.YieldInterval);
        Assert.Equal(8, plan.MemoryTrimInterval);
    }

    [Fact]
    public void CreateExecutionPlan_recycle_to_trash_stays_serial_but_keeps_ui_responsive()
    {
        var plan = DesktopBatchParallelismAdvisor.CreateExecutionPlan(
            DesktopBatchWorkloadKind.RecycleToTrash,
            totalCount: 18,
            totalInputBytes: 18 * 6 * Megabyte,
            DesktopProcessingPerformanceMode.Balanced,
            threadLimit: 6,
            magickOperationLimit: 3);

        Assert.Equal(1, plan.MaxDegreeOfParallelism);
        Assert.Equal(TimeSpan.FromMilliseconds(140), plan.ProgressInterval);
        Assert.Equal(1, plan.ProgressStride);
        Assert.Equal(3, plan.YieldInterval);
        Assert.Equal(4, plan.MemoryTrimInterval);
    }

    [Fact]
    public void CreateExecutionPlan_huge_parallel_copy_batch_samples_progress_and_relaxes_checkpoints()
    {
        var plan = DesktopBatchParallelismAdvisor.CreateExecutionPlan(
            DesktopBatchWorkloadKind.FileCopy,
            totalCount: 2048,
            totalInputBytes: 2048L * 2 * Megabyte,
            DesktopProcessingPerformanceMode.HighPerformance,
            threadLimit: 6,
            magickOperationLimit: 3);

        Assert.Equal(5, plan.MaxDegreeOfParallelism);
        Assert.Equal(TimeSpan.FromMilliseconds(90), plan.ProgressInterval);
        Assert.Equal(5, plan.ProgressStride);
        Assert.Equal(36, plan.YieldInterval);
        Assert.Equal(40, plan.MemoryTrimInterval);
    }

    [Fact]
    public void CreateExecutionPlan_huge_magick_batch_keeps_progress_granular_but_spaces_checkpoints()
    {
        var plan = DesktopBatchParallelismAdvisor.CreateExecutionPlan(
            DesktopBatchWorkloadKind.ExportTranscode,
            totalCount: 512,
            totalInputBytes: 512L * 6 * Megabyte,
            DesktopProcessingPerformanceMode.HighPerformance,
            threadLimit: 6,
            magickOperationLimit: 3);

        Assert.Equal(2, plan.MaxDegreeOfParallelism);
        Assert.Equal(TimeSpan.FromMilliseconds(140), plan.ProgressInterval);
        Assert.Equal(2, plan.ProgressStride);
        Assert.Equal(18, plan.YieldInterval);
        Assert.Equal(22, plan.MemoryTrimInterval);
    }

    [Fact]
    public void CreateExecutionPlan_high_memory_pressure_serializes_magick_batch_and_tightens_checkpoints()
    {
        var plan = DesktopBatchParallelismAdvisor.CreateExecutionPlan(
            DesktopBatchWorkloadKind.ExportTranscode,
            totalCount: 24,
            totalInputBytes: 24L * 8 * Megabyte,
            DesktopProcessingPerformanceMode.HighPerformance,
            threadLimit: 6,
            magickOperationLimit: 3,
            memoryPressureLevel: DesktopMemoryPressureLevel.High);

        Assert.Equal(1, plan.MaxDegreeOfParallelism);
        Assert.Equal(1, plan.ProgressStride);
        Assert.Equal(1, plan.YieldInterval);
        Assert.Equal(2, plan.MemoryTrimInterval);
    }

    [Fact]
    public void CreateExecutionPlan_moderate_memory_pressure_caps_copy_batch_parallelism()
    {
        var plan = DesktopBatchParallelismAdvisor.CreateExecutionPlan(
            DesktopBatchWorkloadKind.FileCopy,
            totalCount: 24,
            totalInputBytes: 24 * 2 * Megabyte,
            DesktopProcessingPerformanceMode.HighPerformance,
            threadLimit: 6,
            magickOperationLimit: 3,
            memoryPressureLevel: DesktopMemoryPressureLevel.Moderate);

        Assert.Equal(3, plan.MaxDegreeOfParallelism);
        Assert.Equal(1, plan.ProgressStride);
        Assert.Equal(6, plan.YieldInterval);
        Assert.Equal(7, plan.MemoryTrimInterval);
    }
}
