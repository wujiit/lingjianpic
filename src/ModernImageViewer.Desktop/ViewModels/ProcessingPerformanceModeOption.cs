using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Desktop.ViewModels;

public sealed record ProcessingPerformanceModeOption(
    DesktopProcessingPerformanceMode Value,
    string Label,
    string Description)
{
    public override string ToString()
    {
        return Label;
    }
}
