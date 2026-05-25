using ModernImageViewer.Core;

namespace ModernImageViewer.Desktop.ViewModels;

public sealed record SortModeOption(SortMode Value, string Label)
{
    public override string ToString()
    {
        return Label;
    }
}
