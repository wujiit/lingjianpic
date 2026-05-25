using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Desktop.ViewModels;

public sealed record ExportFormatOption(ExportImageFormat Value, string Label)
{
    public override string ToString()
    {
        return Label;
    }
}
