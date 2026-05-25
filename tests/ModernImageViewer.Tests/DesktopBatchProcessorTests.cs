using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Tests;

public class DesktopBatchProcessorTests
{
    [Fact]
    public async Task RunAsync_ContinuesAfterItemFailure()
    {
        var processor = new DesktopBatchProcessor();
        var items = new[]
        {
            new DesktopBatchItem<int>(1, "one"),
            new DesktopBatchItem<int>(2, "two"),
            new DesktopBatchItem<int>(3, "three")
        };
        var processed = new List<int>();

        var result = await processor.RunAsync(
            items,
            (item, _, _) =>
            {
                processed.Add(item.Value);
                if (item.Value == 2)
                {
                    throw new InvalidOperationException("broken");
                }

                return Task.CompletedTask;
            });

        Assert.False(result.WasCanceled);
        Assert.Equal(3, result.ProcessedCount);
        Assert.Equal(2, result.SuccessCount);
        Assert.Single(result.Failures);
        Assert.Equal(1, result.Failures[0].Index);
        Assert.Equal("two", result.Failures[0].DisplayName);
        Assert.Equal([1, 2, 3], processed);
    }

    [Fact]
    public async Task RunAsync_StopsWhenCanceled()
    {
        var processor = new DesktopBatchProcessor();
        using var cts = new CancellationTokenSource();
        var items = new[]
        {
            new DesktopBatchItem<int>(1, "one"),
            new DesktopBatchItem<int>(2, "two"),
            new DesktopBatchItem<int>(3, "three")
        };
        var processed = new List<int>();

        var result = await processor.RunAsync(
            items,
            (item, _, _) =>
            {
                processed.Add(item.Value);
                if (item.Value == 1)
                {
                    cts.Cancel();
                }

                return Task.CompletedTask;
            },
            cancellationToken: cts.Token);

        Assert.True(result.WasCanceled);
        Assert.Equal(1, result.ProcessedCount);
        Assert.Equal(1, result.SuccessCount);
        Assert.Empty(result.Failures);
        Assert.Equal([1], processed);
    }

    [Fact]
    public async Task RunAsync_ReportsProgressForProcessedItems()
    {
        var processor = new DesktopBatchProcessor();
        var progressValues = new List<int>();
        var progress = new InlineProgress<DesktopBatchProgress>(item => progressValues.Add(item.ProcessedCount));
        var items = new[]
        {
            new DesktopBatchItem<int>(1, "one"),
            new DesktopBatchItem<int>(2, "two")
        };

        var result = await processor.RunAsync(
            items,
            (_, _, _) => Task.CompletedTask,
            progress);

        Assert.Equal(2, result.ProcessedCount);
        Assert.Equal(2, result.SuccessCount);
        Assert.Equal([1, 2], progressValues);
    }

    [Fact]
    public async Task RunAsync_CanProcessWithBoundedParallelism()
    {
        var processor = new DesktopBatchProcessor();
        var items = Enumerable.Range(1, 6)
            .Select(value => new DesktopBatchItem<int>(value, value.ToString()))
            .ToArray();
        var activeCount = 0;
        var maxObservedActiveCount = 0;

        var result = await processor.RunAsync(
            items,
            async (_, _, token) =>
            {
                var active = Interlocked.Increment(ref activeCount);
                UpdateMax(ref maxObservedActiveCount, active);
                await Task.Delay(35, token);
                Interlocked.Decrement(ref activeCount);
            },
            maxDegreeOfParallelism: 3);

        Assert.False(result.WasCanceled);
        Assert.Equal(items.Length, result.ProcessedCount);
        Assert.Equal(items.Length, result.SuccessCount);
        Assert.True(maxObservedActiveCount > 1);
        Assert.True(maxObservedActiveCount <= 3);
    }

    [Fact]
    public async Task RunAsync_ThrottlesIntermediateProgressUpdates()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 4, 21, 12, 0, 0, TimeSpan.Zero));
        var processor = new DesktopBatchProcessor(timeProvider, TimeSpan.FromSeconds(30), yieldInterval: 0);
        var progressValues = new List<int>();
        var progress = new InlineProgress<DesktopBatchProgress>(item => progressValues.Add(item.ProcessedCount));
        var items = new[]
        {
            new DesktopBatchItem<int>(1, "one"),
            new DesktopBatchItem<int>(2, "two"),
            new DesktopBatchItem<int>(3, "three")
        };

        var result = await processor.RunAsync(
            items,
            (_, _, _) => Task.CompletedTask,
            progress);

        Assert.Equal(3, result.ProcessedCount);
        Assert.Equal(3, result.SuccessCount);
        Assert.Equal([1, 3], progressValues);
    }

    [Fact]
    public async Task RunAsync_ExecutionPlan_can_override_parallelism_and_progress_interval()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 4, 21, 12, 0, 0, TimeSpan.Zero));
        var processor = new DesktopBatchProcessor(timeProvider, TimeSpan.Zero, yieldInterval: 0);
        var progressValues = new List<int>();
        var progress = new InlineProgress<DesktopBatchProgress>(item => progressValues.Add(item.ProcessedCount));
        var items = Enumerable.Range(1, 4)
            .Select(value => new DesktopBatchItem<int>(value, value.ToString()))
            .ToArray();
        var activeCount = 0;
        var maxObservedActiveCount = 0;
        var executionPlan = new DesktopBatchExecutionPlan(
            MaxDegreeOfParallelism: 2,
            ProgressInterval: TimeSpan.FromSeconds(30),
            ProgressStride: 4,
            YieldInterval: 0,
            MemoryTrimInterval: 0);

        var result = await processor.RunAsync(
            items,
            async (_, _, token) =>
            {
                var active = Interlocked.Increment(ref activeCount);
                UpdateMax(ref maxObservedActiveCount, active);
                await Task.Delay(35, token);
                Interlocked.Decrement(ref activeCount);
            },
            progress,
            executionPlan: executionPlan);

        Assert.False(result.WasCanceled);
        Assert.Equal(items.Length, result.ProcessedCount);
        Assert.Equal(items.Length, result.SuccessCount);
        Assert.Equal([1, 4], progressValues);
        Assert.True(maxObservedActiveCount > 1);
        Assert.True(maxObservedActiveCount <= 2);
    }

    [Fact]
    public async Task RunAsync_ExecutionPlan_takes_precedence_over_max_degree_argument()
    {
        var processor = new DesktopBatchProcessor();
        var items = Enumerable.Range(1, 5)
            .Select(value => new DesktopBatchItem<int>(value, value.ToString()))
            .ToArray();
        var activeCount = 0;
        var maxObservedActiveCount = 0;
        var executionPlan = new DesktopBatchExecutionPlan(
            MaxDegreeOfParallelism: 1,
            ProgressInterval: TimeSpan.Zero,
            ProgressStride: 1,
            YieldInterval: 0,
            MemoryTrimInterval: 0);

        var result = await processor.RunAsync(
            items,
            async (_, _, token) =>
            {
                var active = Interlocked.Increment(ref activeCount);
                UpdateMax(ref maxObservedActiveCount, active);
                await Task.Delay(20, token);
                Interlocked.Decrement(ref activeCount);
            },
            maxDegreeOfParallelism: 4,
            executionPlan: executionPlan);

        Assert.False(result.WasCanceled);
        Assert.Equal(items.Length, result.ProcessedCount);
        Assert.Equal(items.Length, result.SuccessCount);
        Assert.Equal(1, maxObservedActiveCount);
    }

    [Fact]
    public async Task RunAsync_ExecutionPlan_progress_stride_samples_intermediate_updates()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 4, 21, 12, 0, 0, TimeSpan.Zero));
        var processor = new DesktopBatchProcessor(timeProvider, TimeSpan.Zero, yieldInterval: 0);
        var progressValues = new List<int>();
        var progress = new InlineProgress<DesktopBatchProgress>(item => progressValues.Add(item.ProcessedCount));
        var items = Enumerable.Range(1, 6)
            .Select(value => new DesktopBatchItem<int>(value, value.ToString()))
            .ToArray();
        var executionPlan = new DesktopBatchExecutionPlan(
            MaxDegreeOfParallelism: 3,
            ProgressInterval: TimeSpan.Zero,
            ProgressStride: 3,
            YieldInterval: 0,
            MemoryTrimInterval: 0);

        var result = await processor.RunAsync(
            items,
            (_, _, _) => Task.CompletedTask,
            progress,
            executionPlan: executionPlan);

        Assert.False(result.WasCanceled);
        Assert.Equal(items.Length, result.ProcessedCount);
        Assert.Equal(items.Length, result.SuccessCount);
        Assert.Equal([1, 3, 6], progressValues.OrderBy(static value => value));
    }

    [Fact]
    public async Task RunAsync_ExecutionPlan_can_stop_on_first_failure()
    {
        var processor = new DesktopBatchProcessor();
        var items = Enumerable.Range(1, 4)
            .Select(value => new DesktopBatchItem<int>(value, value.ToString()))
            .ToArray();
        var processed = new List<int>();
        var executionPlan = new DesktopBatchExecutionPlan(
            MaxDegreeOfParallelism: 1,
            ProgressInterval: TimeSpan.Zero,
            ProgressStride: 1,
            YieldInterval: 0,
            MemoryTrimInterval: 0,
            StopOnFailure: true);

        var result = await processor.RunAsync(
            items,
            (item, _, _) =>
            {
                processed.Add(item.Value);
                if (item.Value == 2)
                {
                    throw new InvalidOperationException("broken");
                }

                return Task.CompletedTask;
            },
            executionPlan: executionPlan);

        Assert.False(result.WasCanceled);
        Assert.Equal(2, result.ProcessedCount);
        Assert.Equal(1, result.SuccessCount);
        Assert.Single(result.Failures);
        Assert.Equal([1, 2], processed);
    }

    private static void UpdateMax(ref int target, int value)
    {
        while (true)
        {
            var current = target;
            if (value <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value)
        {
            report(value);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtcNow;

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }
    }
}
