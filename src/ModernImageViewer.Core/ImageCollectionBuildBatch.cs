namespace ModernImageViewer.Core;

public sealed record ImageCollectionBuildBatch(
    IReadOnlyList<ImageRecord> Images,
    int TotalImageCount);
