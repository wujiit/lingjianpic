using ImageMagick;
using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Tests;

public sealed class DesktopExifEditServiceTests
{
    [Fact]
    public void CanStripMetadataWithoutReencode_returns_true_for_same_format_strip_all_request()
    {
        var actual = DesktopExifEditService.CanStripMetadataWithoutReencode(
            sourcePath: "source.jpg",
            outputFormat: ExportImageFormat.Jpeg,
            new DesktopExifEditRequest(
                StripAllMetadata: true,
                RemoveGps: true,
                Author: string.Empty,
                Copyright: string.Empty,
                Comment: string.Empty,
                ShiftDateTime: false,
                DateTimeOffsetMinutes: 0));

        Assert.True(actual);
    }

    [Fact]
    public void CanStripMetadataWithoutReencode_returns_false_when_new_metadata_is_requested()
    {
        var actual = DesktopExifEditService.CanStripMetadataWithoutReencode(
            sourcePath: "source.jpg",
            outputFormat: ExportImageFormat.Jpeg,
            new DesktopExifEditRequest(
                StripAllMetadata: true,
                RemoveGps: true,
                Author: "Author",
                Copyright: string.Empty,
                Comment: string.Empty,
                ShiftDateTime: false,
                DateTimeOffsetMinutes: 0));

        Assert.False(actual);
    }

    [Fact]
    public void Apply_updates_text_fields_removes_gps_and_shifts_dates()
    {
        var service = new DesktopExifEditService();
        using var paths = TestPaths.Create();
        var sourcePath = paths.Combine("source.jpg");
        var targetPath = paths.Combine("edited.jpg");
        WriteJpegWithExif(sourcePath);

        service.Apply(
            sourcePath,
            targetPath,
            ExportImageFormat.Jpeg,
            jpegQuality: 92,
            new DesktopExifEditRequest(
                StripAllMetadata: false,
                RemoveGps: true,
                Author: "New Author",
                Copyright: "New Copyright",
                Comment: "New Comment",
                ShiftDateTime: true,
                DateTimeOffsetMinutes: 90));

        using var output = new MagickImage(targetPath);
        var profile = output.GetExifProfile();

        Assert.NotNull(profile);
        Assert.Equal("New Author", profile.GetValue(ExifTag.Artist)?.Value);
        Assert.Equal("New Copyright", profile.GetValue(ExifTag.Copyright)?.Value);
        Assert.Equal("New Comment", profile.GetValue(ExifTag.ImageDescription)?.Value);
        Assert.Equal("2026:04:18 13:30:00", profile.GetValue(ExifTag.DateTimeOriginal)?.Value);
        Assert.Null(profile.GetValue(ExifTag.GPSLatitudeRef));
    }

    [Fact]
    public void Apply_strip_all_metadata_removes_existing_exif_when_no_new_fields_are_set()
    {
        var service = new DesktopExifEditService();
        using var paths = TestPaths.Create();
        var sourcePath = paths.Combine("source.jpg");
        var targetPath = paths.Combine("stripped.jpg");
        WriteJpegWithExif(sourcePath);

        service.Apply(
            sourcePath,
            targetPath,
            ExportImageFormat.Jpeg,
            jpegQuality: 92,
            new DesktopExifEditRequest(
                StripAllMetadata: true,
                RemoveGps: false,
                Author: string.Empty,
                Copyright: string.Empty,
                Comment: string.Empty,
                ShiftDateTime: false,
                DateTimeOffsetMinutes: 0));

        using var output = new MagickImage(targetPath);

        Assert.Null(output.GetExifProfile());
    }

    private static void WriteJpegWithExif(string sourcePath)
    {
        using var source = new MagickImage(MagickColors.White, 12, 12);
        var profile = new ExifProfile();
        profile.SetValue(ExifTag.Artist, "Old Author");
        profile.SetValue(ExifTag.Copyright, "Old Copyright");
        profile.SetValue(ExifTag.ImageDescription, "Old Comment");
        profile.SetValue(ExifTag.DateTimeOriginal, "2026:04:18 12:00:00");
        profile.SetValue(ExifTag.GPSLatitudeRef, "N");
        profile.Rewrite();
        source.SetProfile(profile);
        source.Write(sourcePath);
    }
}
