namespace ModernImageViewer.Desktop.Services;

internal enum DesktopAnalysisWorkloadKind
{
    SimilarHash,
    ExactDuplicateSampleHash,
    ExactDuplicateFullHash
}

internal readonly record struct DesktopAnalysisExecutionPlan(
    int MaxDegreeOfParallelism,
    int ProgressInterval,
    int YieldInterval,
    int MemoryTrimInterval);

internal static class DesktopAnalysisExecutionPlanExtensions
{
    public static DesktopBatchExecutionPlan ToBatchExecutionPlan(this DesktopAnalysisExecutionPlan plan)
    {
        return new DesktopBatchExecutionPlan(
            Math.Max(1, plan.MaxDegreeOfParallelism),
            TimeSpan.Zero,
            Math.Max(1, plan.ProgressInterval),
            Math.Max(0, plan.YieldInterval),
            Math.Max(0, plan.MemoryTrimInterval));
    }
}

internal static class DesktopAnalysisParallelismAdvisor
{
    private const long Megabyte = 1024L * 1024L;
    private const long LargeAverageInputBytes = 24L * Megabyte;
    private const long VeryLargeAverageInputBytes = 48L * Megabyte;
    private const long HugeAverageInputBytes = 96L * Megabyte;
    private const long MassiveAverageInputBytes = 256L * Megabyte;
    private const long LargeBatchInputBytes = 768L * Megabyte;
    private const long HugeBatchInputBytes = 1536L * Megabyte;
    private const long ExtremeBatchInputBytes = 6L * 1024L * Megabyte;

    public static int Calculate(
        DesktopAnalysisWorkloadKind workloadKind,
        int totalCount,
        long totalInputBytes)
    {
        return CreateExecutionPlan(
            workloadKind,
            totalCount,
            totalInputBytes,
            DesktopImageProcessingPolicy.CurrentPerformanceMode,
            DesktopImageProcessingPolicy.ThreadLimit,
            DesktopImageProcessingPolicy.MagickOperationLimit,
            DesktopMemoryPressureLevel.Low).MaxDegreeOfParallelism;
    }

    public static DesktopAnalysisExecutionPlan CreateExecutionPlan(
        DesktopAnalysisWorkloadKind workloadKind,
        int totalCount,
        long totalInputBytes)
    {
        return CreateExecutionPlan(
            workloadKind,
            totalCount,
            totalInputBytes,
            DesktopImageProcessingPolicy.CurrentPerformanceMode,
            DesktopImageProcessingPolicy.ThreadLimit,
            DesktopImageProcessingPolicy.MagickOperationLimit,
            DesktopMemoryPressureLevel.Low);
    }

    public static int CalculateAdaptive(
        DesktopAnalysisWorkloadKind workloadKind,
        int totalCount,
        long totalInputBytes)
    {
        return CreateAdaptiveExecutionPlan(
            workloadKind,
            totalCount,
            totalInputBytes).MaxDegreeOfParallelism;
    }

    public static DesktopAnalysisExecutionPlan CreateAdaptiveExecutionPlan(
        DesktopAnalysisWorkloadKind workloadKind,
        int totalCount,
        long totalInputBytes)
    {
        return CreateExecutionPlan(
            workloadKind,
            totalCount,
            totalInputBytes,
            DesktopImageProcessingPolicy.CurrentPerformanceMode,
            DesktopImageProcessingPolicy.ThreadLimit,
            DesktopImageProcessingPolicy.MagickOperationLimit,
            DesktopImageProcessingPolicy.GetMemoryPressureLevel());
    }

    internal static int Calculate(
        DesktopAnalysisWorkloadKind workloadKind,
        int totalCount,
        long totalInputBytes,
        DesktopProcessingPerformanceMode performanceMode,
        int threadLimit,
        int magickOperationLimit,
        DesktopMemoryPressureLevel memoryPressureLevel = DesktopMemoryPressureLevel.Low)
    {
        return CreateExecutionPlan(
            workloadKind,
            totalCount,
            totalInputBytes,
            performanceMode,
            threadLimit,
            magickOperationLimit,
            memoryPressureLevel).MaxDegreeOfParallelism;
    }

    internal static DesktopAnalysisExecutionPlan CreateExecutionPlan(
        DesktopAnalysisWorkloadKind workloadKind,
        int totalCount,
        long totalInputBytes,
        DesktopProcessingPerformanceMode performanceMode,
        int threadLimit,
        int magickOperationLimit,
        DesktopMemoryPressureLevel memoryPressureLevel = DesktopMemoryPressureLevel.Low)
    {
        var parallelism = CalculateParallelismCore(
            workloadKind,
            totalCount,
            totalInputBytes,
            performanceMode,
            threadLimit,
            magickOperationLimit,
            memoryPressureLevel);

        return new DesktopAnalysisExecutionPlan(
            parallelism,
            GetProgressInterval(workloadKind, performanceMode),
            TightenCheckpointInterval(GetYieldInterval(workloadKind, performanceMode), memoryPressureLevel),
            TightenCheckpointInterval(GetMemoryTrimInterval(workloadKind, performanceMode), memoryPressureLevel));
    }

    private static int CalculateParallelismCore(
        DesktopAnalysisWorkloadKind workloadKind,
        int totalCount,
        long totalInputBytes,
        DesktopProcessingPerformanceMode performanceMode,
        int threadLimit,
        int magickOperationLimit,
        DesktopMemoryPressureLevel memoryPressureLevel)
    {
        if (totalCount <= 1)
        {
            return 1;
        }

        var safeThreadLimit = Math.Max(1, threadLimit);
        var safeMagickLimit = Math.Max(1, magickOperationLimit);
        var limit = Math.Min(GetBaseLimit(workloadKind, performanceMode), safeThreadLimit);
        if (UsesMagick(workloadKind))
        {
            limit = Math.Min(limit, safeMagickLimit);
        }

        var averageInputBytes = totalInputBytes > 0
            ? Math.Max(1L, totalInputBytes / totalCount)
            : 0L;

        switch (workloadKind)
        {
            case DesktopAnalysisWorkloadKind.SimilarHash:
                if (averageInputBytes >= HugeAverageInputBytes)
                {
                    limit = 1;
                }
                else if (averageInputBytes >= VeryLargeAverageInputBytes)
                {
                    limit = Math.Min(limit, performanceMode == DesktopProcessingPerformanceMode.HighPerformance ? 2 : 1);
                }
                else if (averageInputBytes >= LargeAverageInputBytes)
                {
                    limit = Math.Max(1, limit - 1);
                }

                if (totalInputBytes >= HugeBatchInputBytes)
                {
                    limit = Math.Max(1, limit - 1);
                }
                break;

            case DesktopAnalysisWorkloadKind.ExactDuplicateSampleHash:
                if (averageInputBytes >= MassiveAverageInputBytes)
                {
                    limit = Math.Min(limit, performanceMode == DesktopProcessingPerformanceMode.HighPerformance ? 4 : 2);
                }
                else if (averageInputBytes >= HugeAverageInputBytes)
                {
                    limit = Math.Min(limit, performanceMode == DesktopProcessingPerformanceMode.HighPerformance ? 5 : 3);
                }

                if (totalInputBytes >= ExtremeBatchInputBytes)
                {
                    limit = Math.Max(1, limit - 1);
                }
                break;

            case DesktopAnalysisWorkloadKind.ExactDuplicateFullHash:
                if (averageInputBytes >= MassiveAverageInputBytes)
                {
                    limit = 1;
                }
                else if (averageInputBytes >= HugeAverageInputBytes)
                {
                    limit = Math.Min(limit, performanceMode == DesktopProcessingPerformanceMode.HighPerformance ? 2 : 1);
                }
                else if (averageInputBytes >= VeryLargeAverageInputBytes)
                {
                    limit = Math.Max(1, limit - 1);
                }

                if (totalInputBytes >= LargeBatchInputBytes)
                {
                    limit = Math.Max(1, limit - 1);
                }

                if (totalInputBytes >= HugeBatchInputBytes)
                {
                    limit = 1;
                }
                break;
        }

        limit = ApplyMemoryPressureLimit(limit, workloadKind, performanceMode, memoryPressureLevel);
        return Math.Clamp(limit, 1, totalCount);
    }

    private static int GetProgressInterval(
        DesktopAnalysisWorkloadKind workloadKind,
        DesktopProcessingPerformanceMode performanceMode)
    {
        return workloadKind switch
        {
            DesktopAnalysisWorkloadKind.SimilarHash => performanceMode switch
            {
                DesktopProcessingPerformanceMode.Quiet => 4,
                DesktopProcessingPerformanceMode.HighPerformance => 8,
                _ => 6
            },
            DesktopAnalysisWorkloadKind.ExactDuplicateSampleHash => performanceMode switch
            {
                DesktopProcessingPerformanceMode.Quiet => 12,
                DesktopProcessingPerformanceMode.HighPerformance => 20,
                _ => 16
            },
            _ => performanceMode switch
            {
                DesktopProcessingPerformanceMode.Quiet => 6,
                DesktopProcessingPerformanceMode.HighPerformance => 12,
                _ => 8
            }
        };
    }

    private static int GetYieldInterval(
        DesktopAnalysisWorkloadKind workloadKind,
        DesktopProcessingPerformanceMode performanceMode)
    {
        return workloadKind switch
        {
            DesktopAnalysisWorkloadKind.SimilarHash => performanceMode switch
            {
                DesktopProcessingPerformanceMode.Quiet => 2,
                DesktopProcessingPerformanceMode.HighPerformance => 4,
                _ => 3
            },
            DesktopAnalysisWorkloadKind.ExactDuplicateSampleHash => performanceMode switch
            {
                DesktopProcessingPerformanceMode.Quiet => 4,
                DesktopProcessingPerformanceMode.HighPerformance => 8,
                _ => 6
            },
            _ => performanceMode switch
            {
                DesktopProcessingPerformanceMode.Quiet => 2,
                DesktopProcessingPerformanceMode.HighPerformance => 6,
                _ => 4
            }
        };
    }

    private static int GetMemoryTrimInterval(
        DesktopAnalysisWorkloadKind workloadKind,
        DesktopProcessingPerformanceMode performanceMode)
    {
        return workloadKind switch
        {
            DesktopAnalysisWorkloadKind.SimilarHash => performanceMode switch
            {
                DesktopProcessingPerformanceMode.Quiet => 2,
                DesktopProcessingPerformanceMode.HighPerformance => 4,
                _ => 3
            },
            DesktopAnalysisWorkloadKind.ExactDuplicateSampleHash => performanceMode switch
            {
                DesktopProcessingPerformanceMode.Quiet => 16,
                DesktopProcessingPerformanceMode.HighPerformance => 32,
                _ => 24
            },
            _ => performanceMode switch
            {
                DesktopProcessingPerformanceMode.Quiet => 4,
                DesktopProcessingPerformanceMode.HighPerformance => 8,
                _ => 6
            }
        };
    }

    private static int GetBaseLimit(DesktopAnalysisWorkloadKind workloadKind, DesktopProcessingPerformanceMode performanceMode)
    {
        return workloadKind switch
        {
            DesktopAnalysisWorkloadKind.SimilarHash => performanceMode switch
            {
                DesktopProcessingPerformanceMode.Quiet => 1,
                DesktopProcessingPerformanceMode.HighPerformance => 3,
                _ => 2
            },
            DesktopAnalysisWorkloadKind.ExactDuplicateSampleHash => performanceMode switch
            {
                DesktopProcessingPerformanceMode.Quiet => 2,
                DesktopProcessingPerformanceMode.HighPerformance => 6,
                _ => 4
            },
            _ => performanceMode switch
            {
                DesktopProcessingPerformanceMode.Quiet => 1,
                DesktopProcessingPerformanceMode.HighPerformance => 4,
                _ => 2
            }
        };
    }

    private static bool UsesMagick(DesktopAnalysisWorkloadKind workloadKind)
    {
        return workloadKind == DesktopAnalysisWorkloadKind.SimilarHash;
    }

    private static int ApplyMemoryPressureLimit(
        int limit,
        DesktopAnalysisWorkloadKind workloadKind,
        DesktopProcessingPerformanceMode performanceMode,
        DesktopMemoryPressureLevel memoryPressureLevel)
    {
        return memoryPressureLevel switch
        {
            DesktopMemoryPressureLevel.Critical => 1,
            DesktopMemoryPressureLevel.High => UsesMagick(workloadKind)
                ? 1
                : Math.Min(limit, performanceMode == DesktopProcessingPerformanceMode.HighPerformance ? 2 : 1),
            DesktopMemoryPressureLevel.Moderate => Math.Max(1, limit - 1),
            _ => limit
        };
    }

    private static int TightenCheckpointInterval(int interval, DesktopMemoryPressureLevel memoryPressureLevel)
    {
        return memoryPressureLevel switch
        {
            DesktopMemoryPressureLevel.Critical => 1,
            DesktopMemoryPressureLevel.High => Math.Max(1, interval / 2),
            DesktopMemoryPressureLevel.Moderate => Math.Max(1, interval - 1),
            _ => interval
        };
    }
}

internal sealed class DesktopAnalysisExecutionCoordinator
{
    private readonly IProgress<DesktopOperationProgress>? _progress;
    private readonly DesktopAnalysisExecutionPlan _plan;

    public DesktopAnalysisExecutionCoordinator(
        IProgress<DesktopOperationProgress>? progress,
        DesktopAnalysisExecutionPlan plan)
    {
        _progress = progress;
        _plan = plan;
    }

    public void ReportIfNeeded(int completedCount, int totalCount, string statusText)
    {
        if (_progress is null || !ShouldReportProgress(completedCount, totalCount, _plan.ProgressInterval))
        {
            return;
        }

        var safeTotal = Math.Max(1, totalCount);
        var safeCompleted = Math.Clamp(completedCount, 0, safeTotal);
        _progress.Report(new DesktopOperationProgress(safeCompleted, safeTotal, statusText));
    }

    public void OnItemCompleted(int processedCount)
    {
        if (_plan.MemoryTrimInterval > 0
            && processedCount > 0
            && processedCount % _plan.MemoryTrimInterval == 0)
        {
            DesktopImageProcessingPolicy.TrimMemory();
        }

        if (_plan.YieldInterval > 0
            && processedCount > 0
            && processedCount % _plan.YieldInterval == 0)
        {
            Thread.Yield();
        }
    }

    internal static bool ShouldReportProgress(int completedCount, int totalCount, int progressInterval)
    {
        var safeTotal = Math.Max(1, totalCount);
        var safeCompleted = Math.Clamp(completedCount, 0, safeTotal);
        return safeCompleted <= 1
            || safeCompleted >= safeTotal
            || progressInterval <= 1
            || safeCompleted % progressInterval == 0;
    }
}
