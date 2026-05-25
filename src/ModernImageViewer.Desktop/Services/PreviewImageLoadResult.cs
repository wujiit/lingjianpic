using Avalonia.Media.Imaging;

namespace ModernImageViewer.Desktop.Services;

public sealed record PreviewImageLoadResult(
    Bitmap? Bitmap,
    string StatusText,
    string DetailsText);
