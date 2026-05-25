using System.Buffers.Binary;
using System.Text;
using ImageMagick;

namespace ModernImageViewer.Desktop.Services;

internal static class DesktopImageDimensionReader
{
    public static DesktopImageDimensions? TryRead(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (DesktopImageDimensionProbe.TryRead(path, out var dimensions))
        {
            return dimensions;
        }

        try
        {
            return DesktopMagickOperationGate.Shared.Run<DesktopImageDimensions?>(() =>
            {
                var info = new MagickImageInfo(path);
                return info.Width > 0 && info.Height > 0
                    ? new DesktopImageDimensions(info.Width, info.Height)
                    : null;
            });
        }
        catch
        {
            return null;
        }
    }
}

internal static class DesktopImageDimensionProbe
{
    private const ushort TiffClassicVersion = 42;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static bool TryRead(string path, out DesktopImageDimensions dimensions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            using var stream = DesktopFileStreamFactory.OpenReadShared(path);
            return TryRead(stream, Path.GetExtension(path), out dimensions);
        }
        catch
        {
            dimensions = default;
            return false;
        }
    }

    internal static bool TryRead(Stream stream, string? extension, out DesktopImageDimensions dimensions)
    {
        ArgumentNullException.ThrowIfNull(stream);

        dimensions = default;
        if (!stream.CanRead || !stream.CanSeek)
        {
            return false;
        }

        var normalizedExtension = NormalizeExtension(extension);
        if (!string.IsNullOrEmpty(normalizedExtension)
            && TryReadByExtension(stream, normalizedExtension, out dimensions))
        {
            return true;
        }

        return TryReadBySignature(stream, out dimensions);
    }

    private static bool TryReadByExtension(Stream stream, string extension, out DesktopImageDimensions dimensions)
    {
        return extension switch
        {
            ".jpg" or ".jpeg" => TryReadJpeg(stream, out dimensions),
            ".png" => TryReadPng(stream, out dimensions),
            ".gif" => TryReadGif(stream, out dimensions),
            ".bmp" => TryReadBmp(stream, out dimensions),
            ".webp" => TryReadWebP(stream, out dimensions),
            ".tif" or ".tiff" => TryReadTiff(stream, out dimensions),
            ".psd" => TryReadPsd(stream, out dimensions),
            _ => Fail(out dimensions)
        };
    }

    private static bool TryReadBySignature(Stream stream, out DesktopImageDimensions dimensions)
    {
        Span<byte> header = stackalloc byte[16];
        if (!TryReadPrefix(stream, header, out var read))
        {
            dimensions = default;
            return false;
        }

        if (read >= 8 && header[0] == 137 && header[1] == 80 && header[2] == 78 && header[3] == 71)
        {
            return TryReadPng(stream, out dimensions);
        }

        if (read >= 6 && header[0] == 'G' && header[1] == 'I' && header[2] == 'F')
        {
            return TryReadGif(stream, out dimensions);
        }

        if (read >= 2 && header[0] == 0xFF && header[1] == 0xD8)
        {
            return TryReadJpeg(stream, out dimensions);
        }

        if (read >= 2 && header[0] == 'B' && header[1] == 'M')
        {
            return TryReadBmp(stream, out dimensions);
        }

        if (read >= 12
            && header[0] == 'R'
            && header[1] == 'I'
            && header[2] == 'F'
            && header[3] == 'F'
            && header[8] == 'W'
            && header[9] == 'E'
            && header[10] == 'B'
            && header[11] == 'P')
        {
            return TryReadWebP(stream, out dimensions);
        }

        if (read >= 4
            && ((header[0] == 'I' && header[1] == 'I')
                || (header[0] == 'M' && header[1] == 'M')))
        {
            return TryReadTiff(stream, out dimensions);
        }

        if (read >= 4
            && header[0] == '8'
            && header[1] == 'B'
            && header[2] == 'P'
            && header[3] == 'S')
        {
            return TryReadPsd(stream, out dimensions);
        }

        dimensions = default;
        return false;
    }

    private static bool TryReadPng(Stream stream, out DesktopImageDimensions dimensions)
    {
        Span<byte> buffer = stackalloc byte[24];
        if (!TryReadExactAtStart(stream, buffer))
        {
            return Fail(out dimensions);
        }

        if (!buffer[..8].SequenceEqual(PngSignature)
            || buffer[12] != (byte)'I'
            || buffer[13] != (byte)'H'
            || buffer[14] != (byte)'D'
            || buffer[15] != (byte)'R')
        {
            return Fail(out dimensions);
        }

        return TryCreateDimensions(
            BinaryPrimitives.ReadUInt32BigEndian(buffer[16..20]),
            BinaryPrimitives.ReadUInt32BigEndian(buffer[20..24]),
            out dimensions);
    }

    private static bool TryReadGif(Stream stream, out DesktopImageDimensions dimensions)
    {
        Span<byte> buffer = stackalloc byte[10];
        if (!TryReadExactAtStart(stream, buffer))
        {
            return Fail(out dimensions);
        }

        if (buffer[0] != 'G' || buffer[1] != 'I' || buffer[2] != 'F')
        {
            return Fail(out dimensions);
        }

        return TryCreateDimensions(
            BinaryPrimitives.ReadUInt16LittleEndian(buffer[6..8]),
            BinaryPrimitives.ReadUInt16LittleEndian(buffer[8..10]),
            out dimensions);
    }

    private static bool TryReadBmp(Stream stream, out DesktopImageDimensions dimensions)
    {
        Span<byte> buffer = stackalloc byte[26];
        if (!TryReadExactAtStart(stream, buffer))
        {
            return Fail(out dimensions);
        }

        if (buffer[0] != 'B' || buffer[1] != 'M')
        {
            return Fail(out dimensions);
        }

        var dibHeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer[14..18]);
        return dibHeaderSize switch
        {
            12 => TryCreateDimensions(
                BinaryPrimitives.ReadUInt16LittleEndian(buffer[18..20]),
                BinaryPrimitives.ReadUInt16LittleEndian(buffer[20..22]),
                out dimensions),
            >= 40 => TryCreateDimensions(
                BinaryPrimitives.ReadInt32LittleEndian(buffer[18..22]),
                BinaryPrimitives.ReadInt32LittleEndian(buffer[22..26]),
                out dimensions),
            _ => Fail(out dimensions)
        };
    }

    private static bool TryReadJpeg(Stream stream, out DesktopImageDimensions dimensions)
    {
        dimensions = default;
        Span<byte> markerHeader = stackalloc byte[2];
        if (!TryReadExactAtStart(stream, markerHeader)
            || markerHeader[0] != 0xFF
            || markerHeader[1] != 0xD8)
        {
            return false;
        }

        while (TryReadByte(stream, out var markerPrefix))
        {
            if (markerPrefix != 0xFF)
            {
                continue;
            }

            byte marker;
            do
            {
                if (!TryReadByte(stream, out marker))
                {
                    return false;
                }
            }
            while (marker == 0xFF);

            if (marker == 0xD9 || marker == 0xDA)
            {
                return false;
            }

            if (marker is 0x01 or >= 0xD0 and <= 0xD7)
            {
                continue;
            }

            Span<byte> lengthBytes = stackalloc byte[2];
            if (!TryReadExact(stream, lengthBytes))
            {
                return false;
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
            if (segmentLength < 2)
            {
                return false;
            }

            if (!IsJpegSizeMarker(marker))
            {
                if (!SkipBytes(stream, segmentLength - 2))
                {
                    return false;
                }

                continue;
            }

            if (segmentLength < 7)
            {
                return false;
            }

            Span<byte> sizeSegmentPrefix = stackalloc byte[5];
            if (!TryReadExact(stream, sizeSegmentPrefix))
            {
                return false;
            }

            if (!SkipBytes(stream, segmentLength - 7))
            {
                return false;
            }

            return TryCreateDimensions(
                BinaryPrimitives.ReadUInt16BigEndian(sizeSegmentPrefix[3..5]),
                BinaryPrimitives.ReadUInt16BigEndian(sizeSegmentPrefix[1..3]),
                out dimensions);
        }

        return false;
    }

    private static bool TryReadWebP(Stream stream, out DesktopImageDimensions dimensions)
    {
        dimensions = default;
        Span<byte> header = stackalloc byte[20];
        if (!TryReadExactAtStart(stream, header)
            || header[0] != 'R'
            || header[1] != 'I'
            || header[2] != 'F'
            || header[3] != 'F'
            || header[8] != 'W'
            || header[9] != 'E'
            || header[10] != 'B'
            || header[11] != 'P')
        {
            return false;
        }

        var chunkType = Encoding.ASCII.GetString(header[12..16]);
        var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(header[16..20]);
        if (chunkSize == 0)
        {
            return false;
        }

        return chunkType switch
        {
            "VP8X" => TryReadWebPVp8X(stream, chunkSize, out dimensions),
            "VP8L" => TryReadWebPVp8L(stream, chunkSize, out dimensions),
            "VP8 " => TryReadWebPVp8(stream, chunkSize, out dimensions),
            _ => false
        };
    }

    private static bool TryReadWebPVp8X(Stream stream, uint chunkSize, out DesktopImageDimensions dimensions)
    {
        dimensions = default;
        if (chunkSize < 10)
        {
            return false;
        }

        var payload = new byte[10];
        if (!TryReadExact(stream, payload))
        {
            return false;
        }

        var width = 1u + ReadUInt24LittleEndian(payload.AsSpan(4, 3));
        var height = 1u + ReadUInt24LittleEndian(payload.AsSpan(7, 3));
        return TryCreateDimensions(width, height, out dimensions);
    }

    private static bool TryReadWebPVp8L(Stream stream, uint chunkSize, out DesktopImageDimensions dimensions)
    {
        dimensions = default;
        if (chunkSize < 5)
        {
            return false;
        }

        var payload = new byte[5];
        if (!TryReadExact(stream, payload) || payload[0] != 0x2F)
        {
            return false;
        }

        var packed = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(1, 4));
        var width = 1u + (packed & 0x3FFFu);
        var height = 1u + ((packed >> 14) & 0x3FFFu);
        return TryCreateDimensions(width, height, out dimensions);
    }

    private static bool TryReadWebPVp8(Stream stream, uint chunkSize, out DesktopImageDimensions dimensions)
    {
        dimensions = default;
        if (chunkSize < 10)
        {
            return false;
        }

        var payload = new byte[10];
        if (!TryReadExact(stream, payload))
        {
            return false;
        }

        if (payload[3] != 0x9D || payload[4] != 0x01 || payload[5] != 0x2A)
        {
            return false;
        }

        var width = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(6, 2)) & 0x3FFF;
        var height = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(8, 2)) & 0x3FFF;
        return TryCreateDimensions(width, height, out dimensions);
    }

    private static bool TryReadTiff(Stream stream, out DesktopImageDimensions dimensions)
    {
        dimensions = default;
        Span<byte> header = stackalloc byte[8];
        if (!TryReadExactAtStart(stream, header))
        {
            return false;
        }

        var littleEndian = header[0] == 'I' && header[1] == 'I';
        var bigEndian = header[0] == 'M' && header[1] == 'M';
        if (!littleEndian && !bigEndian)
        {
            return false;
        }

        var version = ReadUInt16(header[2..4], littleEndian);
        if (version != TiffClassicVersion)
        {
            return false;
        }

        var ifdOffset = ReadUInt32(header[4..8], littleEndian);
        if (ifdOffset < 8 || !TrySeek(stream, ifdOffset))
        {
            return false;
        }

        Span<byte> countBuffer = stackalloc byte[2];
        if (!TryReadExact(stream, countBuffer))
        {
            return false;
        }

        var entryCount = ReadUInt16(countBuffer, littleEndian);
        uint width = 0;
        uint height = 0;
        Span<byte> entry = stackalloc byte[12];

        for (var index = 0; index < entryCount; index++)
        {
            if (!TryReadExact(stream, entry))
            {
                return false;
            }

            var tag = ReadUInt16(entry[..2], littleEndian);
            if (tag is not 256 and not 257)
            {
                continue;
            }

            var type = ReadUInt16(entry[2..4], littleEndian);
            var valueCount = ReadUInt32(entry[4..8], littleEndian);
            if (valueCount != 1)
            {
                continue;
            }

            if (!TryReadTiffInlineValue(entry[8..12], type, littleEndian, out var value))
            {
                continue;
            }

            if (tag == 256)
            {
                width = value;
            }
            else
            {
                height = value;
            }

            if (width > 0 && height > 0)
            {
                return TryCreateDimensions(width, height, out dimensions);
            }
        }

        return false;
    }

    private static bool TryReadPsd(Stream stream, out DesktopImageDimensions dimensions)
    {
        Span<byte> buffer = stackalloc byte[22];
        if (!TryReadExactAtStart(stream, buffer))
        {
            return Fail(out dimensions);
        }

        if (buffer[0] != '8'
            || buffer[1] != 'B'
            || buffer[2] != 'P'
            || buffer[3] != 'S')
        {
            return Fail(out dimensions);
        }

        var version = BinaryPrimitives.ReadUInt16BigEndian(buffer[4..6]);
        if (version is not 1 and not 2)
        {
            return Fail(out dimensions);
        }

        return TryCreateDimensions(
            BinaryPrimitives.ReadUInt32BigEndian(buffer[18..22]),
            BinaryPrimitives.ReadUInt32BigEndian(buffer[14..18]),
            out dimensions);
    }

    private static bool TryReadTiffInlineValue(
        ReadOnlySpan<byte> valueBytes,
        ushort type,
        bool littleEndian,
        out uint value)
    {
        value = 0;

        switch (type)
        {
            case 3:
            {
                var rawValue = ReadUInt32(valueBytes, littleEndian);
                value = littleEndian ? rawValue & 0xFFFFu : rawValue >> 16;
                return value > 0;
            }

            case 4:
                value = ReadUInt32(valueBytes, littleEndian);
                return value > 0;

            default:
                return false;
        }
    }

    private static bool TryCreateDimensions(uint width, uint height, out DesktopImageDimensions dimensions)
    {
        if (width == 0 || height == 0)
        {
            dimensions = default;
            return false;
        }

        dimensions = new DesktopImageDimensions(width, height);
        return true;
    }

    private static bool TryCreateDimensions(int width, int height, out DesktopImageDimensions dimensions)
    {
        var safeWidth = width == int.MinValue ? 0u : (uint)Math.Abs(width);
        var safeHeight = height == int.MinValue ? 0u : (uint)Math.Abs(height);
        return TryCreateDimensions(safeWidth, safeHeight, out dimensions);
    }

    private static bool TryReadPrefix(Stream stream, Span<byte> buffer, out int read)
    {
        Reset(stream);
        read = stream.Read(buffer);
        Reset(stream);
        return read > 0;
    }

    private static bool TryReadExactAtStart(Stream stream, Span<byte> buffer)
    {
        Reset(stream);
        return TryReadExact(stream, buffer);
    }

    private static bool TryReadExact(Stream stream, Span<byte> buffer)
    {
        var remaining = buffer;
        while (!remaining.IsEmpty)
        {
            var read = stream.Read(remaining);
            if (read <= 0)
            {
                return false;
            }

            remaining = remaining[read..];
        }

        return true;
    }

    private static bool TryReadExact(Stream stream, byte[] buffer)
    {
        return TryReadExact(stream, buffer.AsSpan());
    }

    private static bool TryReadByte(Stream stream, out byte value)
    {
        var next = stream.ReadByte();
        if (next < 0)
        {
            value = 0;
            return false;
        }

        value = (byte)next;
        return true;
    }

    private static bool SkipBytes(Stream stream, int count)
    {
        if (count < 0)
        {
            return false;
        }

        return TrySeek(stream, stream.Position + count);
    }

    private static bool TrySeek(Stream stream, long offset)
    {
        try
        {
            stream.Seek(offset, SeekOrigin.Begin);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, bool littleEndian)
    {
        return littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(bytes)
            : BinaryPrimitives.ReadUInt16BigEndian(bytes);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, bool littleEndian)
    {
        return littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
            : BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    private static uint ReadUInt24LittleEndian(ReadOnlySpan<byte> bytes)
    {
        return (uint)(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16));
    }

    private static bool IsJpegSizeMarker(byte marker)
    {
        return marker is
            0xC0 or 0xC1 or 0xC2 or 0xC3 or
            0xC5 or 0xC6 or 0xC7 or
            0xC9 or 0xCA or 0xCB or
            0xCD or 0xCE or 0xCF;
    }

    private static string NormalizeExtension(string? extension)
    {
        return string.IsNullOrWhiteSpace(extension)
            ? string.Empty
            : extension.StartsWith('.')
                ? extension.ToLowerInvariant()
                : $".{extension.ToLowerInvariant()}";
    }

    private static void Reset(Stream stream)
    {
        stream.Seek(0, SeekOrigin.Begin);
    }

    private static bool Fail(out DesktopImageDimensions dimensions)
    {
        dimensions = default;
        return false;
    }
}
