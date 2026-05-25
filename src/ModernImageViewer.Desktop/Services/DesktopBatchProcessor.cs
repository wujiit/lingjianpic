using System.Collections.Concurrent;

namespace ModernImageViewer.Desktop.Services;

public sealed record DesktopBatchItem<T>(T Value, string DisplayName);

public sealed record DesktopBatchItemFailure(int Index, string DisplayName, string ErrorMessage);

public sealed record DesktopBatchProgress(int ProcessedCount, int TotalCount, string DisplayName);

public sealed record DesktopBatchResult(
    int TotalCount,
    int ProcessedCount,
    int SuccessCount,
    bool WasCanceled,
    IReadOnlyList<DesktopBatchItemFailure> Failures)
{
    public int FailureCount => Failures.Count;
}

public sealed class DesktopBatchProcessor
{
    private static readonly TimeSpan DefaultProgressInterval = TimeSpan.FromMilliseconds(120);
    private const int DefaultYieldInterval = 4;
    private const int DefaultMemoryTrimInterval = 0;

    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _defaultProgressInterval;
    private readonly int _defaultYieldInterval;
    private readonly int _defaultMemoryTrimInterval;

    public DesktopBatchProcessor()
        : this(TimeProvider.System, DefaultProgressInterval, DefaultYieldInterval)
    {
    }

    internal DesktopBatchProcessor(
        TimeProvider timeProvider,
        TimeSpan progressInterval,
        int yieldInterval,
        int memoryTrimInterval = DefaultMemoryTrimInterval)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _defaultProgressInterval = progressInterval < TimeSpan.Zero ? TimeSpan.Zero : progressInterval;
        _defaultYieldInterval = Math.Max(0, yieldInterval);
        _defaultMemoryTrimInterval = Math.Max(0, memoryTrimInterval);
    }

    public async Task<DesktopBatchResult> RunAsync<T>(
        IReadOnlyList<DesktopBatchItem<T>> items,
        Func<DesktopBatchItem<T>, int, CancellationToken, Task> processItemAsync,
        IProgress<DesktopBatchProgress>? progress = null,
        int maxDegreeOfParallelism = 1,
        DesktopBatchExecutionPlan? executionPlan = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(processItemAsync);

        var effectivePlan = executionPlan ?? new DesktopBatchExecutionPlan(
            Math.Max(1, maxDegreeOfParallelism),
            _defaultProgressInterval,
            ProgressStride: 1,
            _defaultYieldInterval,
            _defaultMemoryTrimInterval);

        if (effectivePlan.MaxDegreeOfParallelism > 1)
        {
            return await RunParallelAsync(
                items,
                processItemAsync,
                progress,
                effectivePlan,
                cancellationToken);
        }

        var failures = new List<DesktopBatchItemFailure>();
        var successCount = 0;
        var wasCanceled = false;
        var processedCount = 0;
        var progressReporter = new BatchProgressReporter(
            progress,
            _timeProvider,
            effectivePlan.ProgressInterval,
            effectivePlan.ProgressStride);

        for (var index = 0; index < items.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                wasCanceled = true;
                break;
            }

            var item = items[index];
            var stopAfterCurrentItem = false;
            try
            {
                await processItemAsync(item, index, cancellationToken);
                successCount++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                wasCanceled = true;
                break;
            }
            catch (Exception ex)
            {
                failures.Add(new DesktopBatchItemFailure(index, item.DisplayName, ex.Message));
                stopAfterCurrentItem = effectivePlan.StopOnFailure;
            }
            finally
            {
                processedCount = index + 1;
                progressReporter.Report(processedCount, items.Count, item.DisplayName);
                await OnItemCompletedAsync(processedCount, effectivePlan, cancellationToken);
            }

            if (stopAfterCurrentItem)
            {
                break;
            }
        }

        return new DesktopBatchResult(items.Count, processedCount, successCount, wasCanceled, failures);
    }

    private async Task<DesktopBatchResult> RunParallelAsync<T>(
        IReadOnlyList<DesktopBatchItem<T>> items,
        Func<DesktopBatchItem<T>, int, CancellationToken, Task> processItemAsync,
        IProgress<DesktopBatchProgress>? progress,
        DesktopBatchExecutionPlan executionPlan,
        CancellationToken cancellationToken)
    {
        var failures = new ConcurrentBag<DesktopBatchItemFailure>();
        var successCount = 0;
        var processedCount = 0;
        var wasCanceled = false;
        var progressReporter = new BatchProgressReporter(
            progress,
            _timeProvider,
            executionPlan.ProgressInterval,
            executionPlan.ProgressStride);

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Clamp(executionPlan.MaxDegreeOfParallelism, 1, Math.Max(1, items.Count))
        };
        using var stopOnFailureCts = executionPlan.StopOnFailure
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        if (stopOnFailureCts is not null)
        {
            parallelOptions.CancellationToken = stopOnFailureCts.Token;
        }

        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, items.Count), parallelOptions, async (index, token) =>
            {
                var item = items[index];
                try
                {
                    await processItemAsync(item, index, token);
                    Interlocked.Increment(ref successCount);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    wasCanceled = true;
                    throw;
                }
                catch (Exception ex)
                {
                    failures.Add(new DesktopBatchItemFailure(index, item.DisplayName, ex.Message));
                    if (stopOnFailureCts is not null && !stopOnFailureCts.IsCancellationRequested)
                    {
                        stopOnFailureCts.Cancel();
                    }
                }
                finally
                {
                    var completed = Interlocked.Increment(ref processedCount);
                    progressReporter.Report(completed, items.Count, item.DisplayName);
                    await OnItemCompletedAsync(completed, executionPlan, token);
                }
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || stopOnFailureCts?.IsCancellationRequested == true)
        {
            wasCanceled = cancellationToken.IsCancellationRequested;
        }

        return new DesktopBatchResult(
            items.Count,
            processedCount,
            successCount,
            wasCanceled,
            failures.OrderBy(static failure => failure.Index).ToArray());
    }

    private async ValueTask OnItemCompletedAsync(
        int processedCount,
        DesktopBatchExecutionPlan executionPlan,
        CancellationToken cancellationToken)
    {
        MaybeTrimMemory(processedCount, executionPlan.MemoryTrimInterval);
        await YieldIfNeededAsync(processedCount, executionPlan.YieldInterval, cancellationToken);
    }

    private static void MaybeTrimMemory(int processedCount, int memoryTrimInterval)
    {
        if (memoryTrimInterval <= 0 || processedCount <= 0 || processedCount % memoryTrimInterval != 0)
        {
            return;
        }

        DesktopImageProcessingPolicy.TrimMemory();
    }

    private static async ValueTask YieldIfNeededAsync(int processedCount, int yieldInterval, CancellationToken cancellationToken)
    {
        if (yieldInterval <= 0 || processedCount <= 0 || processedCount % yieldInterval != 0)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
    }

    private sealed class BatchProgressReporter
    {
        private readonly object _syncRoot = new();
        private readonly IProgress<DesktopBatchProgress>? _progress;
        private readonly TimeProvider _timeProvider;
        private readonly TimeSpan _progressInterval;
        private readonly int _progressStride;
        private DateTimeOffset _lastReportedAt;
        private int _lastReportedProcessedCount;
        private int _maxObservedProcessedCount;

        public BatchProgressReporter(
            IProgress<DesktopBatchProgress>? progress,
            TimeProvider timeProvider,
            TimeSpan progressInterval,
            int progressStride)
        {
            _progress = progress;
            _timeProvider = timeProvider;
            _progressInterval = progressInterval;
            _progressStride = Math.Max(1, progressStride);
        }

        public void Report(int processedCount, int totalCount, string displayName)
        {
            if (_progress is null)
            {
                return;
            }

            var safeTotal = Math.Max(1, totalCount);
            var safeProcessed = Math.Clamp(processedCount, 0, safeTotal);
            var now = _timeProvider.GetUtcNow();
            var reportStartExclusive = 0;
            var reportEndInclusive = 0;
            var reportFinalOnly = false;

            lock (_syncRoot)
            {
                _maxObservedProcessedCount = Math.Max(_maxObservedProcessedCount, safeProcessed);
                var pendingReport = CollectPendingReport(safeTotal, now);
                if (!pendingReport.HasValue)
                {
                    return;
                }

                reportStartExclusive = _lastReportedProcessedCount;
                reportEndInclusive = pendingReport.Value.EndInclusive;
                reportFinalOnly = pendingReport.Value.EmitFinalOnly;
                _lastReportedProcessedCount = reportEndInclusive;
                _lastReportedAt = now;
            }

            if (reportFinalOnly)
            {
                _progress.Report(new DesktopBatchProgress(reportEndInclusive, safeTotal, displayName));
                return;
            }

            var current = reportStartExclusive;
            while (true)
            {
                var nextMilestone = GetNextProgressMilestone(current, safeTotal);
                if (nextMilestone <= current || nextMilestone > reportEndInclusive)
                {
                    break;
                }

                _progress.Report(new DesktopBatchProgress(nextMilestone, safeTotal, displayName));
                current = nextMilestone;
            }
        }

        private PendingReport? CollectPendingReport(int safeTotal, DateTimeOffset now)
        {
            if (_maxObservedProcessedCount <= _lastReportedProcessedCount)
            {
                return null;
            }

            var intervalBlocked = _progressInterval > TimeSpan.Zero
                && _lastReportedAt != default
                && now - _lastReportedAt < _progressInterval;
            var finalAvailable = _maxObservedProcessedCount >= safeTotal;
            if (intervalBlocked && !finalAvailable)
            {
                return null;
            }

            if (intervalBlocked && finalAvailable)
            {
                return safeTotal > _lastReportedProcessedCount
                    ? new PendingReport(safeTotal, EmitFinalOnly: true)
                    : null;
            }

            var current = _lastReportedProcessedCount;
            var lastMilestone = current;
            while (true)
            {
                var nextMilestone = GetNextProgressMilestone(current, safeTotal);
                if (nextMilestone <= current || nextMilestone > _maxObservedProcessedCount)
                {
                    break;
                }

                lastMilestone = nextMilestone;
                current = nextMilestone;
            }

            return lastMilestone > _lastReportedProcessedCount
                ? new PendingReport(lastMilestone, EmitFinalOnly: false)
                : null;
        }

        private int GetNextProgressMilestone(int current, int safeTotal)
        {
            if (current <= 0)
            {
                return 1;
            }

            var nextStride = ((current / _progressStride) + 1) * _progressStride;
            return nextStride < safeTotal ? nextStride : safeTotal;
        }

        private readonly record struct PendingReport(int EndInclusive, bool EmitFinalOnly);
    }
}
