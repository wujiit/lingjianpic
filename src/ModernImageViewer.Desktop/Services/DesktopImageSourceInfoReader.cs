namespace ModernImageViewer.Desktop.Services;

internal readonly record struct DesktopFileSignature(
    string Path,
    long SizeBytes,
    long SourceStampTicks);

internal readonly record struct DesktopImageSourceInfo(
    DesktopFileSignature Signature,
    DesktopImageDimensions? Dimensions,
    bool Exists)
{
    public string Path => Signature.Path;

    public long SizeBytes => Signature.SizeBytes;

    public long SourceStampTicks => Signature.SourceStampTicks;
}

internal static class DesktopFileSignatureReader
{
    public static bool TryRead(string path, out DesktopFileSignature signature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists)
            {
                signature = default;
                return false;
            }

            signature = new DesktopFileSignature(
                path,
                fileInfo.Length,
                fileInfo.LastWriteTimeUtc.Ticks);
            return true;
        }
        catch
        {
            signature = default;
            return false;
        }
    }
}

internal static class DesktopImageSourceInfoReader
{
    public static DesktopImageSourceInfo Read(string path, DesktopImageDimensionCacheStore dimensionCache)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(dimensionCache);

        if (!DesktopFileSignatureReader.TryRead(path, out var signature))
        {
            return new DesktopImageSourceInfo(
                new DesktopFileSignature(path, 0, 0),
                Dimensions: null,
                Exists: File.Exists(path));
        }

        var dimensions = dimensionCache.GetOrLoad(signature, DesktopImageDimensionReader.TryRead);
        return new DesktopImageSourceInfo(signature, dimensions, Exists: true);
    }

    public static DesktopImageSourceInfo Read(
        DesktopFileSignature signature,
        DesktopImageDimensionCacheStore dimensionCache)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signature.Path);
        ArgumentNullException.ThrowIfNull(dimensionCache);

        var dimensions = dimensionCache.GetOrLoad(signature, DesktopImageDimensionReader.TryRead);
        return new DesktopImageSourceInfo(signature, dimensions, Exists: true);
    }
}
