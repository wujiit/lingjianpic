namespace ModernImageViewer.Desktop.ViewModels;

public sealed record ImageMatchThresholdOption(
    int DistanceThreshold,
    string Label,
    string Description);
