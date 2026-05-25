namespace ModernImageViewer.Desktop.Services;

internal enum DesktopMemoryPressureLevel
{
    Low,
    Moderate,
    High,
    Critical
}

internal readonly record struct DesktopMemoryPressureSnapshot(
    long TotalAvailableMemoryBytes,
    long HighMemoryLoadThresholdBytes,
    long MemoryLoadBytes,
    long HeapSizeBytes,
    long ProcessWorkingSetBytes,
    DesktopMemoryPressureLevel Level);

internal static class DesktopMemoryPressureMonitor
{
    public static DesktopMemoryPressureSnapshot GetSnapshot()
    {
        var memoryInfo = GC.GetGCMemoryInfo();
        var totalAvailableMemoryBytes = Math.Max(0, memoryInfo.TotalAvailableMemoryBytes);
        var highMemoryLoadThresholdBytes = Math.Max(0, memoryInfo.HighMemoryLoadThresholdBytes);
        var memoryLoadBytes = Math.Max(0, memoryInfo.MemoryLoadBytes);
        var heapSizeBytes = Math.Max(0, memoryInfo.HeapSizeBytes);
        var processWorkingSetBytes = Math.Max(0, Environment.WorkingSet);

        return new DesktopMemoryPressureSnapshot(
            totalAvailableMemoryBytes,
            highMemoryLoadThresholdBytes,
            memoryLoadBytes,
            heapSizeBytes,
            processWorkingSetBytes,
            ClassifyMemoryPressure(
                totalAvailableMemoryBytes,
                highMemoryLoadThresholdBytes,
                memoryLoadBytes,
                heapSizeBytes,
                processWorkingSetBytes));
    }

    internal static DesktopMemoryPressureLevel ClassifyMemoryPressure(
        long totalAvailableMemoryBytes,
        long highMemoryLoadThresholdBytes,
        long memoryLoadBytes,
        long heapSizeBytes,
        long processWorkingSetBytes)
    {
        var effectiveThresholdBytes = highMemoryLoadThresholdBytes > 0
            ? highMemoryLoadThresholdBytes
            : totalAvailableMemoryBytes > 0
                ? (long)(totalAvailableMemoryBytes * 0.85)
                : 0;

        var loadRatio = CalculateRatio(memoryLoadBytes, effectiveThresholdBytes);
        var heapRatio = CalculateRatio(heapSizeBytes, totalAvailableMemoryBytes);
        var workingSetRatio = CalculateRatio(processWorkingSetBytes, totalAvailableMemoryBytes);
        var pressureRatio = Math.Max(loadRatio, Math.Max(heapRatio, workingSetRatio));

        if (pressureRatio >= 0.92)
        {
            return DesktopMemoryPressureLevel.Critical;
        }

        if (pressureRatio >= 0.80)
        {
            return DesktopMemoryPressureLevel.High;
        }

        if (pressureRatio >= 0.65)
        {
            return DesktopMemoryPressureLevel.Moderate;
        }

        return DesktopMemoryPressureLevel.Low;
    }

    private static double CalculateRatio(long numerator, long denominator)
    {
        if (numerator <= 0 || denominator <= 0)
        {
            return 0;
        }

        return numerator / (double)denominator;
    }
}
