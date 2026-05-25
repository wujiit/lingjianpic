namespace ModernImageViewer.Desktop.Services;

public sealed record DesktopOperationProgress(int CompletedCount, int TotalCount, string StatusText);
