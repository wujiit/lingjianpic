namespace ModernImageViewer.Desktop.Services;

internal enum DesktopBatchWorkloadKind
{
    FileCopy,
    FileMove,
    RecycleToTrash,
    ExportPassthrough,
    ExportTranscode,
    ExportCompression,
    ExportWatermark,
    ExifEdit
}

public readonly record struct DesktopBatchExecutionPlan(
    int MaxDegreeOfParallelism,
    TimeSpan ProgressInterval,
    int ProgressStride,
    int YieldInterval,
    int MemoryTrimInterval,
    bool StopOnFailure = false);

internal static class DesktopBatchParallelismAdvisor
{
    private const long Megabyte = 1024L * 1024L;
    private const long LargeAverageInputBytes = 24L * Megabyte;
    private const long VeryLargeAverageInputBytes = 48L * Megabyte;
    private const long HugeAverageInputBytes = 96L * Megabyte;
    private const long LargeBatchInputBytes = 768L * Megabyte;
    private const long HugeBatchInputBytes = 1536L * Megabyte;
    private const long ExtremeBatchInputBytes = 6L * 1024L * Megabyte;

    public static int Calculate(
        DesktopBatchWorkloadKind workloadKind,
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

    public static DesktopBatchExecutionPlan CreateExecutionPlan(
        DesktopBatchWorkloadKind workloadKind,
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
        DesktopBatchWorkloadKind workloadKind,
        int totalCount,
        long totalInputBytes)
    {
        return CreateAdaptiveExecutionPlan(
            workloadKind,
            totalCount,
            totalInputBytes).MaxDegreeOfParallelism;
    }

    public static DesktopBatchExecutionPlan CreateAdaptiveExecutionPlan(
        DesktopBatchWorkloadKind workloadKind,
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
        DesktopBatchWorkloadKind workloadKind,
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

    internal static DesktopBatchExecutionPlan CreateExecutionPlan(
        DesktopBatchWorkloadKind workloadKind,
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
        return new DesktopBatchExecutionPlan(
            parallelism,
            GetProgressInterval(workloadKind, performanceMode),
            GetProgressStride(workloadKind, totalCount, performanceMode, parallelism, memoryPressureLevel),
            GetYieldInterval(workloadKind, performanceMode, totalCount, parallelism, totalInputBytes, memoryPressureLevel),
            GetMemoryTrimInterval(workloadKind, performanceMode, totalCount, parallelism, totalInputBytes, memoryPressureLevel));
    }

    private static int CalculateParallelismCore(
        DesktopBatchWorkloadKind workloadKind,
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

        if (UsesMagick(workloadKind))
        {
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

            if (workloadKind == DesktopBatchWorkloadKind.ExportCompression && totalInputBytes >= LargeBatchInputBytes)
            {
                limit = Math.Max(1, limit - 1);
            }

            if (workloadKind == DesktopBatchWorkloadKind.ExportCompression
                && totalCount >= 48
                && averageInputBytes >= LargeAverageInputBytes)
            {
                limit = 1;
            }

            if (workloadKind == DesktopBatchWorkloadKind.ExportWatermark)
            {
                if (totalCount >= 24)
                {
                    limit = Math.Min(limit, performanceMode == DesktopProcessingPerformanceMode.HighPerformance ? 2 : 1);
                }

                if (averageInputBytes >= LargeAverageInputBytes && totalInputBytes >= LargeBatchInputBytes)
                {
                    limit = 1;
                }
            }
        }
        else
        {
            if (averageInputBytes >= HugeAverageInputBytes)
            {
                limit = Math.Min(limit, performanceMode == DesktopProcessingPerformanceMode.HighPerformance ? 3 : 2);
            }
            else if (averageInputBytes >= VeryLargeAverageInputBytes)
            {
                limit = Math.Min(limit, performanceMode == DesktopProcessingPerformanceMode.HighPerformance ? 4 : 2);
            }
            else if (averageInputBytes >= LargeAverageInputBytes)
            {
                limit = Math.Max(1, limit - 1);
            }

            if (totalInputBytes >= HugeBatchInputBytes)
            {
                limit = Math.Max(1, limit - 1);
            }

            if (totalInputBytes >= ExtremeBatchInputBytes)
            {
                limit = Math.Max(1, limit - 1);
            }

            if (workloadKind == DesktopBatchWorkloadKind.FileMove && totalInputBytes >= LargeBatchInputBytes)
            {
                limit = Math.Min(limit, performanceMode == DesktopProcessingPerformanceMode.HighPerformance ? 2 : 1);
            }
        }

        limit = ApplyMemoryPressureLimit(limit, workloadKind, performanceMode, memoryPressureLevel);
        return Math.Clamp(limit, 1, totalCount);
    }

    private static TimeSpan GetProgressInterval(
        DesktopBatchWorkloadKind workloadKind,
        DesktopProcessingPerformanceMode performanceMode)
    {
        var milliseconds = workloadKind switch
        {
            DesktopBatchWorkloadKind.ExportCompression => performanceMode switch
            {
                DesktopProcessingPerformanceMode.Quiet => 220,
                DesktopProcessingPerformanceMode.HighPerformance => 160,
                _ => 190
            },
            DesktopBatchWorkloadKind.ExportTranscode
                or DesktopBatchWorkloadKind.ExportWatermark
                or DesktopBatchWorkloadKind.ExifEdit => performanceMode switch
            {
                DesktopProcessingPerformanceMode.Quiet => 200,
                DesktopProcessingPerformanceMode.HighPerformance => 140,
                _ => 170
            },
            DesktopBatchWorkloadKind.FileMove
                or DesktopBatchWorkloadKind.RecycleToTrash => performanceMode switch
            {
                DesktopProcessingPerformanceMode.Quiet => 160,
                DesktopProcessingPerformanceMode.HighPerformance => 120,
                _ => 140
            },
            _ => performanceMode switch
            {
                DesktopProcessingPerformanceMode.Quiet => 140,
                DesktopProcessingPerformanceMode.HighPerformance => 90,
                _ => 110
            }
        };

        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static int GetYieldInterval(
        DesktopBatchWorkloadKind workloadKind,
        DesktopProcessingPerformanceMode performanceMode,
        int totalCount,
        int parallelism,
        long totalInputBytes,
        DesktopMemoryPressureLevel memoryPressureLevel)
    {
        var baseInterval = workloadKind switch
        {
            DesktopBatchWorkloadKind.ExportCompression => performanceMode switch
            {
                DesktopProcessingPerformanceMode.Quiet => 1,
                DesktopProcessingPerformanceMode.HighPerformance => 3,
                _ => 2
            },
            DesktopBatchWorkloadKind.ExportTranscode
                or DesktopBatchWorkloadKind.ExportWatermark
                or DesktopBatchWorkloadKind.ExifEdit => performanceMode switch
            {
                DesktopProcessingPerformanceMode.Quiet => 1,
                DesktopProcessingPerformanceMode.HighPerformance => 3,
                _ => 2
            },
            DesktopBatchWorkloadKind.FileMove
                or DesktopBatchWorkloadKind.RecycleToTrash => performanceMode switch
            {
                DesktopProcessingPerformanceMode.Quiet => 2,
                DesktopProcessingPerformanceMode.HighPerformance => 4,
                _ => 3
            },
            _ => performanceMode switch
            {
                DesktopProcessingPerformanceMode.Quiet => 3,
                DesktopProcessingPerformanceMode.HighPerformance => 7,
                _ => 5
            }
        };

        return TightenCheckpointInterval(
            ScaleCheckpointInterval(baseInterval, totalCount, parallelism, totalInputBytes, UsesMagick(workloadKind)),
            memoryPressureLevel);
    }

    private static int GetMemoryTrimInterval(
        DesktopBatchWorkloadKind workloadKind,
        DesktopProcessingPerformanceMode performanceMode,
        int totalCount,
        int parallelism,
        long totalInputBytes,
        DesktopMemoryPressureLevel memoryPressureLevel)
    {
        var baseInterval = workloadKind switch
        {
            DesktopBatchWorkloadKind.ExportCompression => performanceMode switch
            {
                DesktopProcessingPerformanceMode.Quiet => 1,
                DesktopProcessingPerformanceMode.HighPerformance => 3,
                _ => 2
            },
            DesktopBatchWorkloadKind.ExportTranscode
                or DesktopBatchWorkloadKind.ExportWatermark
                or DesktopBatchWorkloadKind.ExifEdit => performanceMode switch
            {
                DesktopProcessingPerformanceMode.Quiet => 2,
                DesktopProcessingPerformanceMode.HighPerformance => 4,
                _ => 3
            },
            DesktopBatchWorkloadKind.FileMove
                or DesktopBatchWorkloadKind.RecycleToTrash => performanceMode switch
            {
                DesktopProcessingPerformanceMode.Quiet => 3,
                DesktopProcessingPerformanceMode.HighPerformance => 6,
                _ => 4
            },
            _ => performanceMode switch
            {
                DesktopProcessingPerformanceMode.Quiet => 4,
                DesktopProcessingPerformanceMode.HighPerformance => 8,
                _ => 6
            }
        };

        return TightenCheckpointInterval(
            ScaleCheckpointInterval(baseInterval, totalCount, parallelism, totalInputBytes, UsesMagick(workloadKind)),
            memoryPressureLevel);
    }

    private static int GetProgressStride(
        DesktopBatchWorkloadKind workloadKind,
        int totalCount,
        DesktopProcessingPerformanceMode performanceMode,
        int parallelism,
        DesktopMemoryPressureLevel memoryPressureLevel)
    {
        if (totalCount <= 48 || parallelism <= 1)
        {
            return 1;
        }

        var stride = UsesMagick(workloadKind) ? 1 : 2;
        if (totalCount >= 256)
        {
            stride++;
        }

        if (totalCount >= 1024)
        {
            stride++;
        }

        if (!UsesMagick(workloadKind) && totalCount >= 4096)
        {
            stride++;
        }

        if (performanceMode == DesktopProcessingPerformanceMode.HighPerformance && parallelism >= 4)
        {
            stride++;
        }

        stride = memoryPressureLevel switch
        {
            DesktopMemoryPressureLevel.Moderate => Math.Min(stride, 2),
            DesktopMemoryPressureLevel.High or DesktopMemoryPressureLevel.Critical => 1,
            _ => stride
        };

        return Math.Clamp(stride, 1, 8);
    }

    private static int ScaleCheckpointInterval(
        int baseInterval,
        int totalCount,
        int parallelism,
        long totalInputBytes,
        bool usesMagick)
    {
        var scaled = Math.Max(1, baseInterval);

        if (parallelism > 1 && totalCount >= 32)
        {
            scaled += Math.Max(1, parallelism / 2);
        }

        if (totalCount >= 64)
        {
            scaled *= 2;
        }

        if (totalCount >= 256)
        {
            scaled *= 2;
        }

        if (usesMagick && totalInputBytes >= HugeBatchInputBytes)
        {
            scaled += 2;
        }

        return Math.Clamp(scaled, 1, 96);
    }

    private static int GetBaseLimit(DesktopBatchWorkloadKind workloadKind, DesktopProcessingPerformanceMode performanceMode)
    {
        return workloadKind switch
        {
            DesktopBatchWorkloadKind.FileCopy or DesktopBatchWorkloadKind.ExportPassthrough => performanceMode switch
            {
                DesktopProcessingPerformanceMode.Quiet => 2,
                DesktopProcessingPerformanceMode.HighPerformance => 6,
                _ => 4
            },
            DesktopBatchWorkloadKind.FileMove => performanceMode switch
            {
                DesktopProcessingPerformanceMode.Quiet => 1,
                DesktopProcessingPerformanceMode.HighPerformance => 3,
                _ => 2
            },
            DesktopBatchWorkloadKind.RecycleToTrash => 1,
            DesktopBatchWorkloadKind.ExportCompression => performanceMode switch
            {
                DesktopProcessingPerformanceMode.HighPerformance => 2,
                _ => 1
            },
            _ => performanceMode switch
            {
                DesktopProcessingPerformanceMode.Quiet => 1,
                DesktopProcessingPerformanceMode.HighPerformance => 3,
                _ => 2
            }
        };
    }

    private static bool UsesMagick(DesktopBatchWorkloadKind workloadKind)
    {
        return workloadKind is
            DesktopBatchWorkloadKind.ExportTranscode or
            DesktopBatchWorkloadKind.ExportCompression or
            DesktopBatchWorkloadKind.ExportWatermark or
            DesktopBatchWorkloadKind.ExifEdit;
    }

    private static int ApplyMemoryPressureLimit(
        int limit,
        DesktopBatchWorkloadKind workloadKind,
        DesktopProcessingPerformanceMode performanceMode,
        DesktopMemoryPressureLevel memoryPressureLevel)
    {
        return memoryPressureLevel switch
        {
            DesktopMemoryPressureLevel.Critical => 1,
            DesktopMemoryPressureLevel.High => UsesMagick(workloadKind)
                ? 1
                : Math.Min(limit, performanceMode == DesktopProcessingPerformanceMode.HighPerformance ? 2 : 1),
            DesktopMemoryPressureLevel.Moderate => UsesMagick(workloadKind)
                ? Math.Max(1, limit - 1)
                : Math.Max(1, Math.Min(limit, performanceMode == DesktopProcessingPerformanceMode.HighPerformance ? 3 : 2)),
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
