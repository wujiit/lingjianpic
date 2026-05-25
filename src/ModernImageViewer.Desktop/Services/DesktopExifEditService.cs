using System.Globalization;
using System.Text;
using ImageMagick;

namespace ModernImageViewer.Desktop.Services;

public sealed record DesktopExifEditRequest(
    bool StripAllMetadata,
    bool RemoveGps,
    string Author,
    string Copyright,
    string Comment,
    bool ShiftDateTime,
    int DateTimeOffsetMinutes);

public sealed class DesktopExifEditService
{
    private static readonly ExifTag[] GpsTags =
    [
        ExifTag.GPSAltitude,
        ExifTag.GPSAltitudeRef,
        ExifTag.GPSAreaInformation,
        ExifTag.GPSDateStamp,
        ExifTag.GPSDestBearing,
        ExifTag.GPSDestBearingRef,
        ExifTag.GPSDestDistance,
        ExifTag.GPSDestDistanceRef,
        ExifTag.GPSDestLatitude,
        ExifTag.GPSDestLatitudeRef,
        ExifTag.GPSDestLongitude,
        ExifTag.GPSDestLongitudeRef,
        ExifTag.GPSDifferential,
        ExifTag.GPSDOP,
        ExifTag.GPSImgDirection,
        ExifTag.GPSImgDirectionRef,
        ExifTag.GPSLatitude,
        ExifTag.GPSLatitudeRef,
        ExifTag.GPSLongitude,
        ExifTag.GPSLongitudeRef,
        ExifTag.GPSMapDatum,
        ExifTag.GPSMeasureMode,
        ExifTag.GPSProcessingMethod,
        ExifTag.GPSSatellites,
        ExifTag.GPSSpeed,
        ExifTag.GPSSpeedRef,
        ExifTag.GPSStatus,
        ExifTag.GPSTimestamp,
        ExifTag.GPSTrack,
        ExifTag.GPSTrackRef,
        ExifTag.GPSVersionID,
        ExifTag.GPSIFDOffset
    ];

    public string GetFileExtension(string sourcePath, ExportImageFormat format)
    {
        return ResolveOutputFormat(sourcePath, format) switch
        {
            ExportImageFormat.Jpeg => ".jpg",
            ExportImageFormat.Png => ".png",
            ExportImageFormat.WebP => ".webp",
            ExportImageFormat.Avif => ".avif",
            ExportImageFormat.Tiff => ".tif",
            ExportImageFormat.Bmp => ".bmp",
            ExportImageFormat.Jxl => ".jxl",
            ExportImageFormat.Heic => ".heic",
            _ => ".jpg"
        };
    }

    public void Apply(
        string sourcePath,
        string targetPath,
        ExportImageFormat format,
        int jpegQuality,
        DesktopExifEditRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        cancellationToken.ThrowIfCancellationRequested();

        var directoryPath = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new InvalidOperationException("Target path must include a parent directory.");
        }

        Directory.CreateDirectory(directoryPath);

        var outputFormat = ResolveOutputFormat(sourcePath, format);
        if (CanStripMetadataWithoutReencode(sourcePath, outputFormat, request)
            && DesktopEncodedImageSanitizer.TryStripMetadataWithoutReencode(sourcePath, targetPath, outputFormat))
        {
            return;
        }

        DesktopMagickOperationGate.Shared.Run(() =>
        {
            using var image = new MagickImage(sourcePath);
            cancellationToken.ThrowIfCancellationRequested();
            image.AutoOrient();

            if (request.StripAllMetadata)
            {
                image.Strip();
            }

            cancellationToken.ThrowIfCancellationRequested();
            IExifProfile exifProfile = request.StripAllMetadata ? new ExifProfile() : image.GetExifProfile() ?? new ExifProfile();
            ApplyExifChanges(exifProfile, request);

            if (exifProfile.Values.Any())
            {
                exifProfile.Rewrite();
                image.SetProfile(exifProfile);
            }
            else
            {
                image.RemoveProfile("exif");
            }

            ApplyOutputFormat(image, outputFormat, jpegQuality);
            cancellationToken.ThrowIfCancellationRequested();
            DesktopFileStreamFactory.WriteAtomically(targetPath, image.Write);
        }, cancellationToken);
    }

    internal static bool CanStripMetadataWithoutReencode(
        string sourcePath,
        ExportImageFormat outputFormat,
        DesktopExifEditRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(request);

        if (!request.StripAllMetadata
            || !string.IsNullOrWhiteSpace(request.Author)
            || !string.IsNullOrWhiteSpace(request.Copyright)
            || !string.IsNullOrWhiteSpace(request.Comment)
            || (request.ShiftDateTime && request.DateTimeOffsetMinutes != 0))
        {
            return false;
        }

        var sourceFormat = ResolveOutputFormat(sourcePath, ExportImageFormat.Original);
        return sourceFormat == outputFormat
            && outputFormat is ExportImageFormat.Jpeg or ExportImageFormat.Png;
    }

    private static void ApplyExifChanges(IExifProfile exifProfile, DesktopExifEditRequest request)
    {
        if (request.RemoveGps)
        {
            foreach (var tag in GpsTags)
            {
                exifProfile.RemoveValue(tag);
            }
        }

        SetStringIfPresent(exifProfile, ExifTag.Artist, request.Author);
        SetStringIfPresent(exifProfile, ExifTag.Copyright, request.Copyright);
        SetStringIfPresent(exifProfile, ExifTag.ImageDescription, request.Comment);
        SetUserCommentIfPresent(exifProfile, request.Comment);

        if (request.ShiftDateTime && request.DateTimeOffsetMinutes != 0)
        {
            ShiftDateTime(exifProfile, ExifTag.DateTime, request.DateTimeOffsetMinutes);
            ShiftDateTime(exifProfile, ExifTag.DateTimeOriginal, request.DateTimeOffsetMinutes);
            ShiftDateTime(exifProfile, ExifTag.DateTimeDigitized, request.DateTimeOffsetMinutes);
        }
    }

    private static void SetStringIfPresent(IExifProfile exifProfile, ExifTag<string> tag, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            exifProfile.SetValue(tag, value.Trim());
        }
    }

    private static void SetUserCommentIfPresent(IExifProfile exifProfile, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var textBytes = Encoding.BigEndianUnicode.GetBytes(value.Trim());
        var prefixBytes = Encoding.ASCII.GetBytes("UNICODE\0");
        var commentBytes = new byte[8 + textBytes.Length];
        prefixBytes.CopyTo(commentBytes, 0);
        textBytes.CopyTo(commentBytes, 8);
        exifProfile.SetValue(ExifTag.UserComment, commentBytes);
    }

    private static void ShiftDateTime(IExifProfile exifProfile, ExifTag<string> tag, int offsetMinutes)
    {
        var exifValue = exifProfile.GetValue(tag);
        if (exifValue?.Value is not string value || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!DateTime.TryParseExact(
            value.Trim(),
            "yyyy:MM:dd HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var dateTime))
        {
            return;
        }

        exifProfile.SetValue(tag, dateTime.AddMinutes(offsetMinutes).ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture));
    }

    private static ExportImageFormat ResolveOutputFormat(string sourcePath, ExportImageFormat format)
    {
        if (format != ExportImageFormat.Original)
        {
            return format;
        }

        return Path.GetExtension(sourcePath).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => ExportImageFormat.Jpeg,
            ".png" => ExportImageFormat.Png,
            ".webp" => ExportImageFormat.WebP,
            ".avif" => ExportImageFormat.Avif,
            ".tif" or ".tiff" => ExportImageFormat.Tiff,
            ".bmp" => ExportImageFormat.Bmp,
            ".jxl" => ExportImageFormat.Jxl,
            ".heic" or ".heif" => ExportImageFormat.Heic,
            _ => ExportImageFormat.Jpeg
        };
    }

    private static void ApplyOutputFormat(IMagickImage<byte> image, ExportImageFormat outputFormat, int jpegQuality)
    {
        image.Format = outputFormat switch
        {
            ExportImageFormat.Jpeg => MagickFormat.Jpeg,
            ExportImageFormat.Png => MagickFormat.Png,
            ExportImageFormat.WebP => MagickFormat.WebP,
            ExportImageFormat.Avif => MagickFormat.Avif,
            ExportImageFormat.Tiff => MagickFormat.Tiff,
            ExportImageFormat.Bmp => MagickFormat.Bmp,
            ExportImageFormat.Jxl => MagickFormat.Jxl,
            ExportImageFormat.Heic => MagickFormat.Heic,
            _ => MagickFormat.Jpeg
        };

        if (outputFormat is ExportImageFormat.Jpeg or ExportImageFormat.WebP or ExportImageFormat.Avif or ExportImageFormat.Jxl or ExportImageFormat.Heic)
        {
            image.Quality = (uint)Math.Clamp(jpegQuality, 1, 100);
        }

        if (outputFormat is ExportImageFormat.Jpeg or ExportImageFormat.Bmp)
        {
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaOption.Remove);
        }
    }
}
