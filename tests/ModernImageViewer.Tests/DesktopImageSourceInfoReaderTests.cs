using ImageMagick;
using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Tests;

public sealed class DesktopImageSourceInfoReaderTests
{
    [Fact]
    public void Read_returns_signature_and_dimensions_for_existing_image()
    {
        using var paths = TestPaths.Create();
        var imagePath = paths.Combine("source.png");

        using (var image = new MagickImage(MagickColors.White, 640, 360))
        {
            image.Format = MagickFormat.Png;
            image.Write(imagePath);
        }

        File.SetLastWriteTimeUtc(imagePath, new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc));
        var fileInfo = new FileInfo(imagePath);
        var dimensionCache = new DesktopImageDimensionCacheStore(maxEntries: 8);

        var sourceInfo = DesktopImageSourceInfoReader.Read(imagePath, dimensionCache);

        Assert.True(sourceInfo.Exists);
        Assert.Equal(imagePath, sourceInfo.Path);
        Assert.Equal(fileInfo.Length, sourceInfo.SizeBytes);
        Assert.Equal(fileInfo.LastWriteTimeUtc.Ticks, sourceInfo.SourceStampTicks);
        Assert.Equal(new DesktopImageDimensions(640, 360), sourceInfo.Dimensions);
    }

    [Fact]
    public void Read_marks_missing_file_without_throwing()
    {
        using var paths = TestPaths.Create();
        var imagePath = paths.Combine("missing.png");

        var sourceInfo = DesktopImageSourceInfoReader.Read(
            imagePath,
            new DesktopImageDimensionCacheStore(maxEntries: 8));

        Assert.False(sourceInfo.Exists);
        Assert.Equal(imagePath, sourceInfo.Path);
        Assert.Equal(0, sourceInfo.SizeBytes);
        Assert.Equal(0, sourceInfo.SourceStampTicks);
        Assert.Null(sourceInfo.Dimensions);
    }

    [Fact]
    public void Read_with_explicit_signature_reuses_known_file_metadata()
    {
        using var paths = TestPaths.Create();
        var imagePath = paths.Combine("source.png");

        using (var image = new MagickImage(MagickColors.White, 800, 600))
        {
            image.Format = MagickFormat.Png;
            image.Write(imagePath);
        }

        File.SetLastWriteTimeUtc(imagePath, new DateTime(2026, 4, 21, 10, 5, 0, DateTimeKind.Utc));
        var fileInfo = new FileInfo(imagePath);
        var signature = new DesktopFileSignature(
            imagePath,
            fileInfo.Length,
            fileInfo.LastWriteTimeUtc.Ticks);
        var dimensionCache = new DesktopImageDimensionCacheStore(maxEntries: 8);

        var sourceInfo = DesktopImageSourceInfoReader.Read(signature, dimensionCache);

        Assert.True(sourceInfo.Exists);
        Assert.Equal(signature.Path, sourceInfo.Path);
        Assert.Equal(signature.SizeBytes, sourceInfo.SizeBytes);
        Assert.Equal(signature.SourceStampTicks, sourceInfo.SourceStampTicks);
        Assert.Equal(new DesktopImageDimensions(800, 600), sourceInfo.Dimensions);
    }
}
