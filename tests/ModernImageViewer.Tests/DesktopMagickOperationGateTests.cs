using System.Diagnostics;

namespace ModernImageViewer.Tests;

public sealed class DesktopMagickOperationGateTests
{
    [Fact]
    public async Task Run_serializes_parallel_calls_when_limit_is_one()
    {
        var gate = new ModernImageViewer.Desktop.Services.DesktopMagickOperationGate(limit: 1);
        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        var activeCount = 0;
        var maxActiveCount = 0;

        var firstTask = Task.Run(() => gate.Run(() =>
        {
            var current = Interlocked.Increment(ref activeCount);
            UpdateMax(ref maxActiveCount, current);
            firstEntered.Set();

            try
            {
                Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(5)));
            }
            finally
            {
                Interlocked.Decrement(ref activeCount);
            }
        }));

        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(5)));

        var secondTask = Task.Run(() => gate.Run(() =>
        {
            var current = Interlocked.Increment(ref activeCount);
            UpdateMax(ref maxActiveCount, current);
            secondEntered.Set();

            try
            {
                Thread.Sleep(20);
            }
            finally
            {
                Interlocked.Decrement(ref activeCount);
            }
        }));

        await Task.Delay(150);
        Assert.False(secondEntered.IsSet);

        releaseFirst.Set();
        await Task.WhenAll(firstTask, secondTask);

        Assert.True(secondEntered.IsSet);
        Assert.Equal(1, maxActiveCount);
        Assert.Equal(0, gate.ActiveCount);
    }

    [Fact]
    public void Run_allows_reentrant_calls_without_consuming_additional_slot()
    {
        var gate = new ModernImageViewer.Desktop.Services.DesktopMagickOperationGate(limit: 1);

        gate.Run(() =>
        {
            Assert.Equal(1, gate.ActiveCount);

            gate.Run(() =>
            {
                Assert.Equal(1, gate.ActiveCount);
            });

            Assert.Equal(1, gate.ActiveCount);
        });

        Assert.Equal(0, gate.ActiveCount);
    }

    [Fact]
    public async Task UpdateLimit_unblocks_waiting_work()
    {
        var gate = new ModernImageViewer.Desktop.Services.DesktopMagickOperationGate(limit: 1);
        using var firstEntered = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        using var releaseBoth = new ManualResetEventSlim();

        var firstTask = Task.Run(() => gate.Run(() =>
        {
            firstEntered.Set();
            Assert.True(releaseBoth.Wait(TimeSpan.FromSeconds(5)));
        }));

        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(5)));

        var secondTask = Task.Run(() => gate.Run(() =>
        {
            secondEntered.Set();
            Assert.True(releaseBoth.Wait(TimeSpan.FromSeconds(5)));
        }));

        await Task.Delay(150);
        Assert.False(secondEntered.IsSet);

        gate.UpdateLimit(2);

        var stopwatch = Stopwatch.StartNew();
        Assert.True(secondEntered.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));

        releaseBoth.Set();
        await Task.WhenAll(firstTask, secondTask);
    }

    private static void UpdateMax(ref int target, int candidate)
    {
        while (true)
        {
            var snapshot = target;
            if (snapshot >= candidate)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref target, candidate, snapshot) == snapshot)
            {
                return;
            }
        }
    }
}
