using System.Diagnostics;
using ImageMagick;

namespace ModernImageViewer.Desktop.Services;

public static class DesktopImageProcessingPolicy
{
    private const long Megabyte = 1024L * 1024L;
    private static readonly long MinimumTrimIntervalTicks = Stopwatch.Frequency;
    private static readonly object SyncRoot = new();
    private static bool _isConfigured;
    private static long _totalAvailableMemory;
    private static long _lastTrimTimestamp;

    public static DesktopProcessingPerformanceMode CurrentPerformanceMode { get; private set; } = DesktopProcessingPerformanceMode.Balanced;

    public static bool UsesAggressiveParallelism => CurrentPerformanceMode == DesktopProcessingPerformanceMode.HighPerformance;

    public static int ThreadLimit { get; private set; } = CalculateThreadLimit(DesktopProcessingPerformanceMode.Balanced, Environment.ProcessorCount);

    public static int MagickOperationLimit { get; private set; } = CalculateMagickOperationLimit(DesktopProcessingPerformanceMode.Balanced, Environment.ProcessorCount);

    public static long PreviewCacheLimitBytes { get; private set; } = 64L * Megabyte;

    public static long PreviewDiskCacheLimitBytes { get; private set; } = 192L * Megabyte;

    public static long ThumbnailDiskCacheLimitBytes { get; private set; } = 128L * Megabyte;

    public static int ImageDimensionCacheEntryLimit { get; private set; } =
        CalculateImageDimensionCacheEntryLimit(DesktopProcessingPerformanceMode.Balanced);

    public static int FingerprintTextCacheEntryLimit { get; private set; } =
        CalculateFingerprintTextCacheEntryLimit(DesktopProcessingPerformanceMode.Balanced);

    public static int FingerprintDifferenceHashCacheEntryLimit { get; private set; } =
        CalculateFingerprintDifferenceHashCacheEntryLimit(DesktopProcessingPerformanceMode.Balanced);

    internal static DesktopMemoryPressureSnapshot GetMemoryPressureSnapshot()
    {
        return DesktopMemoryPressureMonitor.GetSnapshot();
    }

    internal static DesktopMemoryPressureLevel GetMemoryPressureLevel()
    {
        return GetMemoryPressureSnapshot().Level;
    }

    public static void Configure()
    {
        if (_isConfigured)
        {
            return;
        }

        lock (SyncRoot)
        {
            if (_isConfigured)
            {
                return;
            }

            _totalAvailableMemory = GetTotalAvailableMemoryBytes();

            try
            {
                var tempPath = Path.Combine(Path.GetTempPath(), "LingJianImageAssistant", "magick-cache");
                Directory.CreateDirectory(tempPath);
                MagickNET.SetTempDirectory(tempPath);
            }
            catch
            {
            }

            ApplyPerformanceModeCore(CurrentPerformanceMode);
            _isConfigured = true;
        }
    }

    public static void ApplyPerformanceMode(DesktopProcessingPerformanceMode mode)
    {
        lock (SyncRoot)
        {
            CurrentPerformanceMode = mode;
            if (!_isConfigured)
            {
                return;
            }

            ApplyPerformanceModeCore(mode);
        }
    }

    public static void TrimMemory()
    {
        var now = Stopwatch.GetTimestamp();
        if (!TryReserveTrimWindow(now, MinimumTrimIntervalTicks))
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: false);
                return;
            }

#pragma warning disable CA1416
            ResourceLimits.TrimMemory();
#pragma warning restore CA1416
        }
        catch
        {
        }
    }

    internal static bool ShouldTrimMemory(long nowTimestamp, long lastTrimTimestamp, long minimumIntervalTicks)
    {
        if (minimumIntervalTicks <= 0 || lastTrimTimestamp == 0)
        {
            return true;
        }

        return nowTimestamp - lastTrimTimestamp >= minimumIntervalTicks;
    }

    internal static int CalculateThreadLimit(DesktopProcessingPerformanceMode mode, int processorCount)
    {
        processorCount = Math.Max(1, processorCount);
        return mode switch
        {
            DesktopProcessingPerformanceMode.Quiet => processorCount <= 4
                ? 1
                : Math.Clamp(processorCount / 4, 1, 2),
            DesktopProcessingPerformanceMode.HighPerformance => processorCount <= 3
                ? 1
                : Math.Clamp((int)Math.Ceiling(processorCount * 0.75), 2, 6),
            _ => processorCount <= 4
                ? 1
                : Math.Clamp(processorCount / 2, 2, 4)
        };
    }

    internal static int CalculateMagickOperationLimit(DesktopProcessingPerformanceMode mode, int processorCount)
    {
        processorCount = Math.Max(1, processorCount);
        return mode switch
        {
            DesktopProcessingPerformanceMode.Quiet => 1,
            DesktopProcessingPerformanceMode.HighPerformance => processorCount <= 4 ? 2 : 3,
            _ => processorCount <= 4 ? 1 : 2
        };
    }

    internal static int CalculateFingerprintTextCacheEntryLimit(DesktopProcessingPerformanceMode mode)
    {
        return mode switch
        {
            DesktopProcessingPerformanceMode.Quiet => 2048,
            DesktopProcessingPerformanceMode.HighPerformance => 8192,
            _ => 4096
        };
    }

    internal static int CalculateFingerprintDifferenceHashCacheEntryLimit(DesktopProcessingPerformanceMode mode)
    {
        return mode switch
        {
            DesktopProcessingPerformanceMode.Quiet => 4096,
            DesktopProcessingPerformanceMode.HighPerformance => 16384,
            _ => 8192
        };
    }

    internal static int CalculateImageDimensionCacheEntryLimit(DesktopProcessingPerformanceMode mode)
    {
        return mode switch
        {
            DesktopProcessingPerformanceMode.Quiet => 2048,
            DesktopProcessingPerformanceMode.HighPerformance => 8192,
            _ => 4096
        };
    }

    private static long GetTotalAvailableMemoryBytes()
    {
        return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
    }

    private static long CalculateMemoryLimitBytes(long totalAvailableMemory, DesktopProcessingPerformanceMode mode)
    {
        if (totalAvailableMemory <= 0)
        {
            return mode switch
            {
                DesktopProcessingPerformanceMode.Quiet => 512L * Megabyte,
                DesktopProcessingPerformanceMode.HighPerformance => 1024L * Megabyte,
                _ => 768L * Megabyte
            };
        }

        var target = mode switch
        {
            DesktopProcessingPerformanceMode.Quiet => totalAvailableMemory / 6,
            DesktopProcessingPerformanceMode.HighPerformance => totalAvailableMemory / 3,
            _ => totalAvailableMemory / 4
        };

        return mode switch
        {
            DesktopProcessingPerformanceMode.Quiet => Math.Clamp(target, 320L * Megabyte, 1024L * Megabyte),
            DesktopProcessingPerformanceMode.HighPerformance => Math.Clamp(target, 512L * Megabyte, 2048L * Megabyte),
            _ => Math.Clamp(target, 384L * Megabyte, 1536L * Megabyte)
        };
    }

    private static long CalculatePreviewCacheLimitBytes(long totalAvailableMemory, DesktopProcessingPerformanceMode mode)
    {
        if (totalAvailableMemory <= 0)
        {
            return mode switch
            {
                DesktopProcessingPerformanceMode.Quiet => 24L * Megabyte,
                DesktopProcessingPerformanceMode.HighPerformance => 96L * Megabyte,
                _ => 64L * Megabyte
            };
        }

        var target = mode switch
        {
            DesktopProcessingPerformanceMode.Quiet => totalAvailableMemory / 48,
            DesktopProcessingPerformanceMode.HighPerformance => totalAvailableMemory / 24,
            _ => totalAvailableMemory / 32
        };

        return mode switch
        {
            DesktopProcessingPerformanceMode.Quiet => Math.Clamp(target, 16L * Megabyte, 64L * Megabyte),
            DesktopProcessingPerformanceMode.HighPerformance => Math.Clamp(target, 48L * Megabyte, 192L * Megabyte),
            _ => Math.Clamp(target, 24L * Megabyte, 128L * Megabyte)
        };
    }

    private static long CalculatePreviewDiskCacheLimitBytes(long totalAvailableMemory, DesktopProcessingPerformanceMode mode)
    {
        if (totalAvailableMemory <= 0)
        {
            return mode switch
            {
                DesktopProcessingPerformanceMode.Quiet => 128L * Megabyte,
                DesktopProcessingPerformanceMode.HighPerformance => 320L * Megabyte,
                _ => 192L * Megabyte
            };
        }

        var target = mode switch
        {
            DesktopProcessingPerformanceMode.Quiet => totalAvailableMemory / 16,
            DesktopProcessingPerformanceMode.HighPerformance => totalAvailableMemory / 8,
            _ => totalAvailableMemory / 12
        };

        return mode switch
        {
            DesktopProcessingPerformanceMode.Quiet => Math.Clamp(target, 64L * Megabyte, 256L * Megabyte),
            DesktopProcessingPerformanceMode.HighPerformance => Math.Clamp(target, 128L * Megabyte, 640L * Megabyte),
            _ => Math.Clamp(target, 96L * Megabyte, 384L * Megabyte)
        };
    }

    private static long CalculateThumbnailDiskCacheLimitBytes(long totalAvailableMemory, DesktopProcessingPerformanceMode mode)
    {
        if (totalAvailableMemory <= 0)
        {
            return mode switch
            {
                DesktopProcessingPerformanceMode.Quiet => 64L * Megabyte,
                DesktopProcessingPerformanceMode.HighPerformance => 192L * Megabyte,
                _ => 128L * Megabyte
            };
        }

        var target = mode switch
        {
            DesktopProcessingPerformanceMode.Quiet => totalAvailableMemory / 32,
            DesktopProcessingPerformanceMode.HighPerformance => totalAvailableMemory / 14,
            _ => totalAvailableMemory / 20
        };

        return mode switch
        {
            DesktopProcessingPerformanceMode.Quiet => Math.Clamp(target, 48L * Megabyte, 160L * Megabyte),
            DesktopProcessingPerformanceMode.HighPerformance => Math.Clamp(target, 128L * Megabyte, 512L * Megabyte),
            _ => Math.Clamp(target, 96L * Megabyte, 256L * Megabyte)
        };
    }

    private static void ApplyPerformanceModeCore(DesktopProcessingPerformanceMode mode)
    {
        ThreadLimit = CalculateThreadLimit(mode, Environment.ProcessorCount);
        MagickOperationLimit = CalculateMagickOperationLimit(mode, Environment.ProcessorCount);
        var memoryLimit = CalculateMemoryLimitBytes(_totalAvailableMemory, mode);
        PreviewCacheLimitBytes = CalculatePreviewCacheLimitBytes(_totalAvailableMemory, mode);
        PreviewDiskCacheLimitBytes = CalculatePreviewDiskCacheLimitBytes(_totalAvailableMemory, mode);
        ThumbnailDiskCacheLimitBytes = CalculateThumbnailDiskCacheLimitBytes(_totalAvailableMemory, mode);
        ImageDimensionCacheEntryLimit = CalculateImageDimensionCacheEntryLimit(mode);
        FingerprintTextCacheEntryLimit = CalculateFingerprintTextCacheEntryLimit(mode);
        FingerprintDifferenceHashCacheEntryLimit = CalculateFingerprintDifferenceHashCacheEntryLimit(mode);
        DesktopImageDimensionCacheStore.Shared.UpdateEntryLimit(ImageDimensionCacheEntryLimit);
        DesktopMagickOperationGate.Shared.UpdateLimit(MagickOperationLimit);

        try
        {
            ResourceLimits.Thread = (uint)ThreadLimit;
            ResourceLimits.Throttle = mode == DesktopProcessingPerformanceMode.Quiet ? 4U : mode == DesktopProcessingPerformanceMode.HighPerformance ? 1U : 2U;
            ResourceLimits.Memory = (ulong)memoryLimit;
            ResourceLimits.Area = (ulong)Math.Min(memoryLimit * 4, 6L * 1024L * Megabyte);
            ResourceLimits.Disk = (ulong)Math.Max(memoryLimit * 4, 4L * 1024L * Megabyte);
            ResourceLimits.MaxMemoryRequest = (ulong)Math.Min(memoryLimit / 2, 512L * Megabyte);
            ResourceLimits.MaxProfileSize = 64UL * 1024UL * 1024UL;
            ResourceLimits.ListLength = 16;
        }
        catch
        {
        }
    }

    private static bool TryReserveTrimWindow(long nowTimestamp, long minimumIntervalTicks)
    {
        while (true)
        {
            var lastTrimTimestamp = Volatile.Read(ref _lastTrimTimestamp);
            if (!ShouldTrimMemory(nowTimestamp, lastTrimTimestamp, minimumIntervalTicks))
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _lastTrimTimestamp, nowTimestamp, lastTrimTimestamp) == lastTrimTimestamp)
            {
                return true;
            }
        }
    }
}
