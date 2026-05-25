using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private static Task RunBatchSynchronousWorkAsync(
        Action action,
        DesktopBatchExecutionPlan executionPlan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        cancellationToken.ThrowIfCancellationRequested();
        if (executionPlan.MaxDegreeOfParallelism > 1)
        {
            action();
            return Task.CompletedTask;
        }

        return Task.Run(action, cancellationToken);
    }
}
