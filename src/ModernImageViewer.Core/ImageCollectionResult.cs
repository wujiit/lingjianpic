namespace ModernImageViewer.Core;

public sealed record ImageCollectionResult(
    IReadOnlyList<ImageRecord> Images,
    string? FocusPath,
    string SourceLabel);
