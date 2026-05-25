using ModernImageViewer.Desktop.ViewModels;

namespace ModernImageViewer.Tests;

public sealed class ShutdownDrainTests
{
    [Fact]
    public async Task PrepareForShutdownAsync_waits_for_background_tasks_to_complete()
    {
        using var viewModel = new MainWindowViewModel();
        var backgroundTaskSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.RegisterBackgroundTaskForTesting(backgroundTaskSignal.Task);

        var shutdownTask = viewModel.PrepareForShutdownAsync();

        await Task.Delay(20);
        Assert.False(shutdownTask.IsCompleted);
        Assert.Equal(1, viewModel.GetPendingBackgroundTaskCount());

        backgroundTaskSignal.TrySetResult();

        await shutdownTask;
        Assert.Equal(0, viewModel.GetPendingBackgroundTaskCount());
    }

    [Fact]
    public async Task PrepareForShutdownAsync_waits_for_tracked_operations_to_finish()
    {
        using var viewModel = new MainWindowViewModel();
        var operationLease = viewModel.BeginTrackedOperationForTesting();

        var shutdownTask = viewModel.PrepareForShutdownAsync();

        await Task.Delay(20);
        Assert.False(shutdownTask.IsCompleted);
        Assert.Equal(1, viewModel.GetTrackedOperationCount());

        operationLease.Dispose();

        await shutdownTask;
        Assert.Equal(0, viewModel.GetTrackedOperationCount());
    }

    [Fact]
    public async Task PrepareForShutdownAsync_can_be_called_multiple_times()
    {
        using var viewModel = new MainWindowViewModel();

        await viewModel.PrepareForShutdownAsync();
        await viewModel.PrepareForShutdownAsync();

        Assert.Equal(0, viewModel.GetPendingBackgroundTaskCount());
        Assert.Equal(0, viewModel.GetTrackedOperationCount());
    }
}
