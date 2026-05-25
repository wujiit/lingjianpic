using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using ImageMagick;
using ModernImageViewer.Core;

namespace ModernImageViewer.Desktop.Services;

internal sealed record DesktopImageMetadataItem(string Label, string Value);

internal sealed record DesktopImageMetadataSummary(IReadOnlyList<DesktopImageMetadataItem> Items)
{
    public static DesktopImageMetadataSummary Empty { get; } =
        new(Array.Empty<DesktopImageMetadataItem>());
}

internal readonly record struct DesktopImageMetadataCacheKey(
    string Path,
    long SizeBytes,
    long SourceStampTicks);

internal sealed class DesktopImageMetadataService
{
    private const string CameraLabel = "\u76f8\u673a";
    private const string LensLabel = "\u955c\u5934";
    private const string CaptureTimeLabel = "\u62cd\u6444\u65f6\u95f4";
    private const string ExposureTimeLabel = "\u66dd\u5149\u65f6\u95f4";
    private const string ApertureLabel = "\u5149\u5708";
    private const string FocalLengthLabel = "\u7126\u8ddd";
    private const string EquivalentFocalLengthLabel = "\u7b49\u6548\u7126\u8ddd";
    private const string OrientationLabel = "\u65b9\u5411";
    private const string WhiteBalanceLabel = "\u767d\u5e73\u8861";
    private const string FlashLabel = "\u95ea\u5149\u706f";
    private const string ColorSpaceLabel = "\u8272\u5f69\u7a7a\u95f4";
    private const string GpsLabel = "\u62cd\u6444\u4f4d\u7f6e";
    private const string SoftwareLabel = "\u8f6f\u4ef6";
    private const string ArtistLabel = "\u4f5c\u8005";
    private const string CopyrightLabel = "\u7248\u6743";
    private const string DescriptionLabel = "\u8bf4\u660e";
    private const string SecondsSuffix = " \u79d2";
    private const string MillimeterSuffix = " mm";

    private readonly ConcurrentDictionary<DesktopImageMetadataCacheKey, DesktopImageMetadataSummary> _cache =
        new(DesktopImageMetadataCacheKeyComparer.Instance);

    public DesktopImageMetadataSummary GetOrLoad(
        DesktopFileSignature signature,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signature.Path);

        var key = new DesktopImageMetadataCacheKey(
            signature.Path,
            signature.SizeBytes,
            signature.SourceStampTicks);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var metadata = LoadCore(signature, cancellationToken);
        RemoveStaleEntries(key);
        _cache[key] = metadata;
        return metadata;
    }

    private DesktopImageMetadataSummary LoadCore(
        DesktopFileSignature signature,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(signature.Path))
        {
            return DesktopImageMetadataSummary.Empty;
        }

        try
        {
            return DesktopMagickOperationGate.Shared.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var image = new MagickImage(signature.Path);
                    cancellationToken.ThrowIfCancellationRequested();

                    var profile = image.GetExifProfile();
                    if (profile?.Values is null)
                    {
                        return DesktopImageMetadataSummary.Empty;
                    }

                    var values = profile.Values
                        .Where(static value => value is not null)
                        .ToDictionary(static value => value.Tag.ToString(), StringComparer.OrdinalIgnoreCase);
                    if (values.Count == 0)
                    {
                        return DesktopImageMetadataSummary.Empty;
                    }

                    var items = BuildItems(values, image.Orientation);
                    return items.Count == 0
                        ? DesktopImageMetadataSummary.Empty
                        : new DesktopImageMetadataSummary(items);
                },
                cancellationToken);
        }
        catch
        {
            return DesktopImageMetadataSummary.Empty;
        }
    }

    private void RemoveStaleEntries(DesktopImageMetadataCacheKey currentKey)
    {
        foreach (var key in _cache.Keys)
        {
            if (DesktopImageMetadataCacheKeyComparer.Instance.Equals(key, currentKey))
            {
                continue;
            }

            if (PathComparison.Comparer.Equals(key.Path, currentKey.Path))
            {
                _cache.TryRemove(key, out _);
            }
        }
    }

    private static List<DesktopImageMetadataItem> BuildItems(
        IReadOnlyDictionary<string, IExifValue> values,
        object? orientationFallback)
    {
        var items = new List<DesktopImageMetadataItem>(14);

        TryAddItem(items, CameraLabel, FormatCamera(values));
        TryAddItem(items, LensLabel, GetStringValue(values, "LensModel"));
        TryAddItem(
            items,
            CaptureTimeLabel,
            GetDateTimeValue(values, "DateTimeOriginal")
            ?? GetDateTimeValue(values, "DateTimeDigitized")
            ?? GetDateTimeValue(values, "DateTime"));
        TryAddItem(items, ExposureTimeLabel, GetExposureTime(values));
        TryAddItem(items, ApertureLabel, GetAperture(values));
        TryAddItem(items, "ISO", GetIso(values));
        TryAddItem(items, FocalLengthLabel, GetFocalLength(values, "FocalLength"));
        TryAddItem(items, EquivalentFocalLengthLabel, GetFocalLength(values, "FocalLengthIn35mmFilm"));
        TryAddItem(items, OrientationLabel, GetOrientation(values, orientationFallback));
        TryAddItem(items, WhiteBalanceLabel, GetWhiteBalance(values));
        TryAddItem(items, FlashLabel, GetFlash(values));
        TryAddItem(items, ColorSpaceLabel, GetColorSpace(values));
        TryAddItem(items, GpsLabel, GetGps(values));
        TryAddItem(items, SoftwareLabel, GetStringValue(values, "Software"));
        TryAddItem(items, ArtistLabel, GetStringValue(values, "Artist"));
        TryAddItem(items, CopyrightLabel, GetStringValue(values, "Copyright"));
        TryAddItem(
            items,
            DescriptionLabel,
            GetStringValue(values, "ImageDescription")
            ?? GetUserComment(values));

        return items;
    }

    private static void TryAddItem(
        ICollection<DesktopImageMetadataItem> items,
        string label,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        items.Add(new DesktopImageMetadataItem(label, value.Trim()));
    }

    private static string? FormatCamera(IReadOnlyDictionary<string, IExifValue> values)
    {
        var make = GetStringValue(values, "Make");
        var model = GetStringValue(values, "Model");

        if (string.IsNullOrWhiteSpace(make))
        {
            return model;
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            return make;
        }

        return string.Equals(make, model, StringComparison.OrdinalIgnoreCase)
            ? model
            : $"{make} {model}";
    }

    private static string? GetDateTimeValue(
        IReadOnlyDictionary<string, IExifValue> values,
        string tagName)
    {
        var text = GetStringValue(values, tagName);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return DateTime.TryParseExact(
            text.Trim(),
            "yyyy:MM:dd HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var dateTime)
            ? dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : text.Trim();
    }

    private static string? GetExposureTime(IReadOnlyDictionary<string, IExifValue> values)
    {
        return TryGetRawValue(values, "ExposureTime", out var value)
            ? FormatExposureValue(value)
            : null;
    }

    private static string? GetAperture(IReadOnlyDictionary<string, IExifValue> values)
    {
        return TryGetRawValue(values, "FNumber", out var value)
            ? FormatApertureValue(value)
            : null;
    }

    private static string? GetIso(IReadOnlyDictionary<string, IExifValue> values)
    {
        if (!TryGetRawValue(values, "ISOSpeedRatings", out var value))
        {
            return null;
        }

        var numeric = FormatNumericValue(value);
        return string.IsNullOrWhiteSpace(numeric)
            ? null
            : $"ISO {numeric}";
    }

    private static string? GetFocalLength(
        IReadOnlyDictionary<string, IExifValue> values,
        string tagName)
    {
        if (!TryGetRawValue(values, tagName, out var value))
        {
            return null;
        }

        var numeric = FormatNumericValue(value);
        if (string.IsNullOrWhiteSpace(numeric))
        {
            return null;
        }

        return numeric.EndsWith("mm", StringComparison.OrdinalIgnoreCase)
            ? numeric
            : $"{numeric}{MillimeterSuffix}";
    }

    private static string? GetOrientation(
        IReadOnlyDictionary<string, IExifValue> values,
        object? orientationFallback)
    {
        var orientationCode = TryGetRawValue(values, "Orientation", out var value)
            ? ConvertToInt32(value)
            : null;
        if (orientationCode is null or <= 0)
        {
            orientationCode = ConvertToInt32(orientationFallback);
        }

        if (orientationCode is null or <= 0)
        {
            return null;
        }

        return orientationCode switch
        {
            1 => "\u6b63\u5e38",
            2 => "\u6c34\u5e73\u955c\u50cf",
            3 => "\u65cb\u8f6c 180\u00b0",
            4 => "\u5782\u76f4\u955c\u50cf",
            5 => "\u5de6\u4e0a\u955c\u50cf",
            6 => "\u987a\u65f6\u9488\u65cb\u8f6c 90\u00b0",
            7 => "\u53f3\u4e0a\u955c\u50cf",
            8 => "\u9006\u65f6\u9488\u65cb\u8f6c 90\u00b0",
            _ => null
        };
    }

    private static string? GetWhiteBalance(IReadOnlyDictionary<string, IExifValue> values)
    {
        if (!TryGetRawValue(values, "WhiteBalance", out var value))
        {
            return null;
        }

        return ConvertToInt32(value) switch
        {
            0 => "\u81ea\u52a8",
            1 => "\u624b\u52a8",
            _ => NormalizeText(FormatExifValue(value))
        };
    }

    private static string? GetFlash(IReadOnlyDictionary<string, IExifValue> values)
    {
        if (!TryGetRawValue(values, "Flash", out var value))
        {
            return null;
        }

        return ConvertToInt32(value) switch
        {
            0 => "\u672a\u95ea\u5149",
            int flashValue when flashValue > 0 && (flashValue & 1) == 1 => "\u5df2\u95ea\u5149",
            _ => NormalizeText(FormatExifValue(value))
        };
    }

    private static string? GetColorSpace(IReadOnlyDictionary<string, IExifValue> values)
    {
        if (!TryGetRawValue(values, "ColorSpace", out var value))
        {
            return null;
        }

        return ConvertToInt32(value) switch
        {
            1 => "sRGB",
            65535 => "\u672a\u6821\u51c6",
            _ => NormalizeText(FormatExifValue(value))
        };
    }

    private static string? GetGps(IReadOnlyDictionary<string, IExifValue> values)
    {
        if (!TryGetRawValue(values, "GPSLatitude", out var latitudeValue)
            || !TryGetRawValue(values, "GPSLongitude", out var longitudeValue))
        {
            return null;
        }

        var latitude = FormatGpsCoordinate(latitudeValue, GetStringValue(values, "GPSLatitudeRef"));
        var longitude = FormatGpsCoordinate(longitudeValue, GetStringValue(values, "GPSLongitudeRef"));
        if (string.IsNullOrWhiteSpace(latitude) || string.IsNullOrWhiteSpace(longitude))
        {
            return null;
        }

        return $"{latitude}, {longitude}";
    }

    private static string? GetUserComment(IReadOnlyDictionary<string, IExifValue> values)
    {
        if (!TryGetRawValue(values, "UserComment", out var value))
        {
            return null;
        }

        return value is byte[] bytes
            ? DecodeUserComment(bytes)
            : NormalizeText(FormatExifValue(value));
    }

    private static string? GetStringValue(
        IReadOnlyDictionary<string, IExifValue> values,
        string tagName)
    {
        return TryGetRawValue(values, tagName, out var value)
            ? NormalizeText(FormatExifValue(value))
            : null;
    }

    private static bool TryGetRawValue(
        IReadOnlyDictionary<string, IExifValue> values,
        string tagName,
        out object? value)
    {
        if (!values.TryGetValue(tagName, out var exifValue))
        {
            value = null;
            return false;
        }

        value = exifValue.GetValue();
        return value is not null;
    }

    private static string? FormatExifValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        return value switch
        {
            string text => NormalizeText(text),
            byte[] bytes => DecodeUserComment(bytes),
            Array array => FormatArray(array),
            DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            _ => FormatScalarValue(value)
        };
    }

    private static string? FormatArray(Array values)
    {
        var parts = new List<string>(values.Length);
        foreach (var value in values)
        {
            var formatted = FormatScalarValue(value);
            if (!string.IsNullOrWhiteSpace(formatted))
            {
                parts.Add(formatted);
            }
        }

        return parts.Count == 0
            ? null
            : string.Join(", ", parts);
    }

    private static string? FormatScalarValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (TryFormatRational(value, out var rationalText))
        {
            return rationalText;
        }

        if (value is IFormattable formattable)
        {
            return formattable.ToString(null, CultureInfo.InvariantCulture);
        }

        return NormalizeText(value.ToString());
    }

    private static string? FormatNumericValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is Array array && array.Length > 0)
        {
            return FormatNumericValue(array.GetValue(0));
        }

        if (TryFormatRational(value, out var rationalText))
        {
            return rationalText;
        }

        if (value is IConvertible convertible)
        {
            try
            {
                var number = convertible.ToDouble(CultureInfo.InvariantCulture);
                if (!double.IsFinite(number))
                {
                    return null;
                }

                return number % 1d == 0d
                    ? number.ToString("0", CultureInfo.InvariantCulture)
                    : number.ToString("0.##", CultureInfo.InvariantCulture);
            }
            catch
            {
            }
        }

        return NormalizeText(value.ToString());
    }

    private static string? FormatExposureValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (TryGetRationalParts(value, out var numerator, out var denominator))
        {
            if (numerator == 0d || denominator == 0d)
            {
                return null;
            }

            var seconds = numerator / denominator;
            if (seconds <= 0d)
            {
                return null;
            }

            if (seconds >= 1d)
            {
                return $"{seconds.ToString("0.##", CultureInfo.InvariantCulture)}{SecondsSuffix}";
            }

            var inverse = Math.Round(1d / seconds);
            return inverse > 0d && Math.Abs((1d / inverse) - seconds) < 0.0005d
                ? $"1/{inverse.ToString("0", CultureInfo.InvariantCulture)}{SecondsSuffix}"
                : $"{seconds.ToString("0.###", CultureInfo.InvariantCulture)}{SecondsSuffix}";
        }

        return NormalizeText(FormatExifValue(value));
    }

    private static string? FormatApertureValue(object? value)
    {
        var numeric = FormatNumericValue(value);
        return string.IsNullOrWhiteSpace(numeric)
            ? null
            : $"f/{numeric}";
    }

    private static string? FormatGpsCoordinate(object? value, string? reference)
    {
        if (value is not Array array || array.Length < 3)
        {
            return NormalizeText(FormatExifValue(value));
        }

        var degrees = GetRationalDouble(array.GetValue(0));
        var minutes = GetRationalDouble(array.GetValue(1));
        var seconds = GetRationalDouble(array.GetValue(2));
        if (!degrees.HasValue || !minutes.HasValue || !seconds.HasValue)
        {
            return NormalizeText(FormatExifValue(value));
        }

        var prefix = string.IsNullOrWhiteSpace(reference)
            ? string.Empty
            : $"{reference!.Trim().ToUpperInvariant()} ";
        return FormattableString.Invariant(
            $"{prefix}{degrees.Value:0}\u00b0 {minutes.Value:0}\u2032 {seconds.Value:0.##}\u2033");
    }

    private static string? DecodeUserComment(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return null;
        }

        if (bytes.Length >= 8)
        {
            var prefix = Encoding.ASCII.GetString(bytes, 0, 8);
            var payload = bytes[8..];
            if (prefix.StartsWith("UNICODE", StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeText(Encoding.BigEndianUnicode.GetString(payload).TrimEnd('\0'));
            }

            if (prefix.StartsWith("ASCII", StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeText(Encoding.ASCII.GetString(payload).TrimEnd('\0'));
            }
        }

        return NormalizeText(Encoding.UTF8.GetString(bytes).TrimEnd('\0'));
    }

    private static string? NormalizeText(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value
                .Replace("\0", string.Empty, StringComparison.Ordinal)
                .Trim();
    }

    private static bool TryFormatRational(object value, out string? text)
    {
        if (!TryGetRationalParts(value, out var numerator, out var denominator) || denominator == 0d)
        {
            text = null;
            return false;
        }

        var number = numerator / denominator;
        text = number % 1d == 0d
            ? number.ToString("0", CultureInfo.InvariantCulture)
            : number.ToString("0.##", CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryGetRationalParts(
        object value,
        out double numerator,
        out double denominator)
    {
        numerator = 0d;
        denominator = 0d;

        var type = value.GetType();
        var numeratorProperty = type.GetProperty("Numerator");
        var denominatorProperty = type.GetProperty("Denominator");
        if (numeratorProperty is null || denominatorProperty is null)
        {
            return false;
        }

        var rawNumerator = numeratorProperty.GetValue(value);
        var rawDenominator = denominatorProperty.GetValue(value);
        if (rawNumerator is null || rawDenominator is null)
        {
            return false;
        }

        try
        {
            numerator = Convert.ToDouble(rawNumerator, CultureInfo.InvariantCulture);
            denominator = Convert.ToDouble(rawDenominator, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            numerator = 0d;
            denominator = 0d;
            return false;
        }
    }

    private static double? GetRationalDouble(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (TryGetRationalParts(value, out var numerator, out var denominator) && denominator != 0d)
        {
            return numerator / denominator;
        }

        if (value is IConvertible convertible)
        {
            try
            {
                return convertible.ToDouble(CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static int? ConvertToInt32(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is Array array && array.Length > 0)
        {
            return ConvertToInt32(array.GetValue(0));
        }

        if (value is IConvertible convertible)
        {
            try
            {
                return convertible.ToInt32(CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private sealed class DesktopImageMetadataCacheKeyComparer : IEqualityComparer<DesktopImageMetadataCacheKey>
    {
        public static DesktopImageMetadataCacheKeyComparer Instance { get; } = new();

        public bool Equals(DesktopImageMetadataCacheKey x, DesktopImageMetadataCacheKey y)
        {
            return PathComparison.Comparer.Equals(x.Path, y.Path)
                && x.SizeBytes == y.SizeBytes
                && x.SourceStampTicks == y.SourceStampTicks;
        }

        public int GetHashCode(DesktopImageMetadataCacheKey obj)
        {
            var hash = new HashCode();
            hash.Add(obj.Path, PathComparison.Comparer);
            hash.Add(obj.SizeBytes);
            hash.Add(obj.SourceStampTicks);
            return hash.ToHashCode();
        }
    }
}
