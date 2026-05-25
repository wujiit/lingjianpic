using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Desktop.ViewModels;

public sealed record WatermarkPlacementOption(DesktopWatermarkPlacement Value, string Label)
{
    public override string ToString()
    {
        return Label;
    }
}
