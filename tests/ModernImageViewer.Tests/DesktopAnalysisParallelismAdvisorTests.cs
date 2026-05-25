using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Tests;

public sealed class DesktopAnalysisParallelismAdvisorTests
{
    private const long Megabyte = 1024L * 1024L;

    [Fact]
    public void Calculate_returns_one_for_single_item()
    {
        var actual = DesktopAnalysisParallelismAdvisor.Calculate(
            DesktopAnalysisWorkloadKind.SimilarHash,
            totalCount: 1,
            totalInputBytes: 400 * Megabyte,
            DesktopProcessingPerformanceMode.HighPerformance,
            threadLimit: 6,
            magickOperationLimit: 3);

        Assert.Equal(1, actual);
    }

    [Fact]
    public void Calculate_similar_hash_huge_raw_batch_reduces_to_single_worker()
    {
        var actual = DesktopAnalysisParallelismAdvisor.Calculate(
            DesktopAnalysisWorkloadKind.SimilarHash,
            totalCount: 8,
            totalInputBytes: 8 * 120 * Megabyte,
            DesktopProcessingPerformanceMode.HighPerformance,
            threadLimit: 6,
            magickOperationLimit: 3);

        Assert.Equal(1, actual);
    }

    [Fact]
    public void Calculate_exact_duplicate_sample_hash_keeps_higher_parallelism_for_light_sampling()
    {
        var actual = DesktopAnalysisParallelismAdvisor.Calculate(
            DesktopAnalysisWorkloadKind.ExactDuplicateSampleHash,
            totalCount: 12,
            totalInputBytes: 12 * 180 * Megabyte,
            DesktopProcessingPerformanceMode.HighPerformance,
            threadLimit: 6,
            magickOperationLimit: 3);

        Assert.Equal(5, actual);
    }

    [Fact]
    public void Calculate_exact_duplicate_full_hash_large_batch_is_more_conservative()
    {
        var actual = DesktopAnalysisParallelismAdvisor.Calculate(
            DesktopAnalysisWorkloadKind.ExactDuplicateFullHash,
            totalCount: 10,
            totalInputBytes: 10 * 180 * Megabyte,
            DesktopProcessingPerformanceMode.HighPerformance,
            threadLimit: 6,
            magickOperationLimit: 3);

        Assert.Equal(1, actual);
    }

    [Fact]
    public void CreateExecutionPlan_similar_hash_uses_tighter_progress_and_trim_settings()
    {
        var plan = DesktopAnalysisParallelismAdvisor.CreateExecutionPlan(
            DesktopAnalysisWorkloadKind.SimilarHash,
            totalCount: 24,
            totalInputBytes: 24 * 12 * Megabyte,
            DesktopProcessingPerformanceMode.Balanced,
            threadLimit: 6,
            magickOperationLimit: 3);

        Assert.Equal(2, plan.MaxDegreeOfParallelism);
        Assert.Equal(6, plan.ProgressInterval);
        Assert.Equal(3, plan.YieldInterval);
        Assert.Equal(3, plan.MemoryTrimInterval);
    }

    [Fact]
    public void CreateExecutionPlan_sample_hash_keeps_looser_progress_interval_for_light_work()
    {
        var plan = DesktopAnalysisParallelismAdvisor.CreateExecutionPlan(
            DesktopAnalysisWorkloadKind.ExactDuplicateSampleHash,
            totalCount: 24,
            totalInputBytes: 24 * 80 * Megabyte,
            DesktopProcessingPerformanceMode.HighPerformance,
            threadLimit: 6,
            magickOperationLimit: 3);

        Assert.Equal(6, plan.MaxDegreeOfParallelism);
        Assert.Equal(20, plan.ProgressInterval);
        Assert.Equal(8, plan.YieldInterval);
        Assert.Equal(32, plan.MemoryTrimInterval);
    }

    [Theory]
    [InlineData(1, 24, 8, true)]
    [InlineData(6, 24, 8, false)]
    [InlineData(8, 24, 8, true)]
    [InlineData(24, 24, 8, true)]
    public void ShouldReportProgress_honors_first_last_and_interval(
        int completedCount,
        int totalCount,
        int progressInterval,
        bool expected)
    {
        var actual = DesktopAnalysisExecutionCoordinator.ShouldReportProgress(
            completedCount,
            totalCount,
            progressInterval);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CreateExecutionPlan_high_memory_pressure_serializes_similar_hash_and_tightens_checkpoints()
    {
        var plan = DesktopAnalysisParallelismAdvisor.CreateExecutionPlan(
            DesktopAnalysisWorkloadKind.SimilarHash,
            totalCount: 24,
            totalInputBytes: 24 * 12 * Megabyte,
            DesktopProcessingPerformanceMode.HighPerformance,
            threadLimit: 6,
            magickOperationLimit: 3,
            memoryPressureLevel: DesktopMemoryPressureLevel.High);

        Assert.Equal(1, plan.MaxDegreeOfParallelism);
        Assert.Equal(8, plan.ProgressInterval);
        Assert.Equal(2, plan.YieldInterval);
        Assert.Equal(2, plan.MemoryTrimInterval);
    }

    [Fact]
    public void CreateExecutionPlan_moderate_memory_pressure_reduces_sample_hash_parallelism()
    {
        var plan = DesktopAnalysisParallelismAdvisor.CreateExecutionPlan(
            DesktopAnalysisWorkloadKind.ExactDuplicateSampleHash,
            totalCount: 24,
            totalInputBytes: 24 * 80 * Megabyte,
            DesktopProcessingPerformanceMode.HighPerformance,
            threadLimit: 6,
            magickOperationLimit: 3,
            memoryPressureLevel: DesktopMemoryPressureLevel.Moderate);

        Assert.Equal(5, plan.MaxDegreeOfParallelism);
        Assert.Equal(7, plan.YieldInterval);
        Assert.Equal(31, plan.MemoryTrimInterval);
    }
}
