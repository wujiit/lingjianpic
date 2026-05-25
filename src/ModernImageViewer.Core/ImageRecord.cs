namespace ModernImageViewer.Core;

public sealed record ImageRecord(
    string FullPath,
    string FileName,
    long SizeBytes,
    DateTimeOffset ModifiedAt);
