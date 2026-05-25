using System.Collections.Concurrent;
using ModernImageViewer.Desktop.ViewModels;

namespace ModernImageViewer.Tests;

public sealed class UiContextDispatchTests
{
    [Fact]
    public async Task RunOnSynchronizationContextAsync_runs_inline_when_contexts_match()
    {
        var context = new RecordingSynchronizationContext();
        var executed = false;

        var task = MainWindowViewModel.RunOnSynchronizationContextAsync(
            context,
            context,
            () => executed = true);

        await task;

        Assert.True(executed);
        Assert.Equal(0, context.PendingCount);
    }

    [Fact]
    public async Task RunOnSynchronizationContextAsync_posts_back_when_contexts_differ()
    {
        var uiContext = new RecordingSynchronizationContext();
        var currentContext = new RecordingSynchronizationContext();
        var executed = false;

        var task = MainWindowViewModel.RunOnSynchronizationContextAsync(
            uiContext,
            currentContext,
            () => executed = true);

        Assert.False(executed);
        Assert.False(task.IsCompleted);
        Assert.Equal(1, uiContext.PendingCount);

        uiContext.ExecuteAll();
        await task;

        Assert.True(executed);
        Assert.Equal(0, uiContext.PendingCount);
    }

    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();

        public int PendingCount => _callbacks.Count;

        public override void Post(SendOrPostCallback d, object? state)
        {
            _callbacks.Enqueue((d, state));
        }

        public void ExecuteAll()
        {
            while (_callbacks.TryDequeue(out var callback))
            {
                callback.Callback(callback.State);
            }
        }
    }
}
