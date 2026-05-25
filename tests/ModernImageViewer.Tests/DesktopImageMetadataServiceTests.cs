using ImageMagick;
using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Tests;

public sealed class DesktopImageMetadataServiceTests
{
    [Fact]
    public void GetOrLoad_returns_rich_photography_metadata()
    {
        var service = new DesktopImageMetadataService();
        using var paths = TestPaths.Create();
        var imagePath = paths.Combine("metadata.jpg");
        WriteJpegWithExif(imagePath);

        Assert.True(DesktopFileSignatureReader.TryRead(imagePath, out var signature));

        var metadata = service.GetOrLoad(signature);
        var values = metadata.Items.ToDictionary(static item => item.Label, static item => item.Value);

        Assert.Equal("Canon EOS R6", values["\u76f8\u673a"]);
        Assert.Equal("RF24-70mm F2.8 L IS USM", values["\u955c\u5934"]);
        Assert.Equal("2026-04-22 10:15:30", values["\u62cd\u6444\u65f6\u95f4"]);
        Assert.Equal("1/125 \u79d2", values["\u66dd\u5149\u65f6\u95f4"]);
        Assert.Equal("f/2.8", values["\u5149\u5708"]);
        Assert.Equal("ISO 200", values["ISO"]);
        Assert.Equal("35 mm", values["\u7126\u8ddd"]);
        Assert.Equal("50 mm", values["\u7b49\u6548\u7126\u8ddd"]);
        Assert.Equal("\u624b\u52a8", values["\u767d\u5e73\u8861"]);
        Assert.Equal("\u5df2\u95ea\u5149", values["\u95ea\u5149\u706f"]);
        Assert.Equal("sRGB", values["\u8272\u5f69\u7a7a\u95f4"]);
        Assert.Equal("N 31\u00b0 13\u2032 19.2\u2033, E 121\u00b0 28\u2032 40.08\u2033", values["\u62cd\u6444\u4f4d\u7f6e"]);
        Assert.Equal("LingJian 1.5", values["\u8f6f\u4ef6"]);
        Assert.Equal("\u542f\u7075\u751f\u6001", values["\u4f5c\u8005"]);
        Assert.Equal("qiling.jingxialai.com", values["\u7248\u6743"]);
        Assert.Equal("\u4ea7\u54c1\u6837\u5f20", values["\u8bf4\u660e"]);
    }

    [Fact]
    public void GetOrLoad_returns_empty_when_exif_is_missing()
    {
        var service = new DesktopImageMetadataService();
        using var paths = TestPaths.Create();
        var imagePath = paths.Combine("plain.jpg");

        using (var image = new MagickImage(MagickColors.White, 16, 16))
        {
            image.Write(imagePath);
        }

        Assert.True(DesktopFileSignatureReader.TryRead(imagePath, out var signature));

        var metadata = service.GetOrLoad(signature);

        Assert.Empty(metadata.Items);
    }

    private static void WriteJpegWithExif(string path)
    {
        using var image = new MagickImage(MagickColors.LightBlue, 24, 18);
        var profile = new ExifProfile();
        profile.SetValue(ExifTag.Make, "Canon");
        profile.SetValue(ExifTag.Model, "EOS R6");
        profile.SetValue(ExifTag.LensModel, "RF24-70mm F2.8 L IS USM");
        profile.SetValue(ExifTag.DateTimeOriginal, "2026:04:22 10:15:30");
        profile.SetValue(ExifTag.ExposureTime, new Rational(1, 125));
        profile.SetValue(ExifTag.FNumber, new Rational(28, 10));
        profile.SetValue(ExifTag.ISOSpeedRatings, new ushort[] { 200 });
        profile.SetValue(ExifTag.FocalLength, new Rational(35, 1));
        profile.SetValue(ExifTag.FocalLengthIn35mmFilm, (ushort)50);
        profile.SetValue(ExifTag.Orientation, (ushort)6);
        profile.SetValue(ExifTag.WhiteBalance, (ushort)1);
        profile.SetValue(ExifTag.Flash, (ushort)1);
        profile.SetValue(ExifTag.ColorSpace, (ushort)1);
        profile.SetValue(ExifTag.GPSLatitudeRef, "N");
        profile.SetValue(ExifTag.GPSLongitudeRef, "E");
        profile.SetValue(
            ExifTag.GPSLatitude,
            new Rational[] { new(31, 1), new(13, 1), new(192, 10) });
        profile.SetValue(
            ExifTag.GPSLongitude,
            new Rational[] { new(121, 1), new(28, 1), new(4008, 100) });
        profile.SetValue(ExifTag.Software, "LingJian 1.5");
        profile.SetValue(ExifTag.Artist, "\u542f\u7075\u751f\u6001");
        profile.SetValue(ExifTag.Copyright, "qiling.jingxialai.com");
        profile.SetValue(ExifTag.ImageDescription, "\u4ea7\u54c1\u6837\u5f20");
        profile.Rewrite();
        image.SetProfile(profile);
        image.Write(path);
    }
}
