using System.Buffers.Binary;
using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Tests;

public sealed class DesktopImageDimensionProbeTests
{
    [Fact]
    public void TryRead_jpeg_header_returns_dimensions()
    {
        using var stream = new MemoryStream(CreateJpegBytes(width: 4032, height: 3024));

        var actual = DesktopImageDimensionProbe.TryRead(stream, ".jpg", out var dimensions);

        Assert.True(actual);
        Assert.Equal(new DesktopImageDimensions(4032, 3024), dimensions);
    }

    [Fact]
    public void TryRead_jpeg_with_large_metadata_segment_returns_dimensions()
    {
        using var stream = new MemoryStream(CreateJpegBytes(width: 3000, height: 2000, appSegmentPayloadLength: 4096));

        var actual = DesktopImageDimensionProbe.TryRead(stream, ".jpg", out var dimensions);

        Assert.True(actual);
        Assert.Equal(new DesktopImageDimensions(3000, 2000), dimensions);
    }

    [Fact]
    public void TryRead_png_header_returns_dimensions()
    {
        using var stream = new MemoryStream(CreatePngBytes(width: 1600, height: 900));

        var actual = DesktopImageDimensionProbe.TryRead(stream, ".png", out var dimensions);

        Assert.True(actual);
        Assert.Equal(new DesktopImageDimensions(1600, 900), dimensions);
    }

    [Fact]
    public void TryRead_webp_vp8x_header_returns_dimensions()
    {
        using var stream = new MemoryStream(CreateWebPVp8XBytes(width: 2048, height: 1536));

        var actual = DesktopImageDimensionProbe.TryRead(stream, ".webp", out var dimensions);

        Assert.True(actual);
        Assert.Equal(new DesktopImageDimensions(2048, 1536), dimensions);
    }

    [Fact]
    public void TryRead_tiff_header_returns_dimensions()
    {
        using var stream = new MemoryStream(CreateLittleEndianTiffBytes(width: 1200, height: 800));

        var actual = DesktopImageDimensionProbe.TryRead(stream, ".tif", out var dimensions);

        Assert.True(actual);
        Assert.Equal(new DesktopImageDimensions(1200, 800), dimensions);
    }

    [Fact]
    public void TryRead_psd_header_returns_dimensions()
    {
        using var stream = new MemoryStream(CreatePsdBytes(width: 3000, height: 2000));

        var actual = DesktopImageDimensionProbe.TryRead(stream, ".psd", out var dimensions);

        Assert.True(actual);
        Assert.Equal(new DesktopImageDimensions(3000, 2000), dimensions);
    }

    [Fact]
    public void TryRead_can_fall_back_to_signature_when_extension_is_unknown()
    {
        using var stream = new MemoryStream(CreatePngBytes(width: 512, height: 256));

        var actual = DesktopImageDimensionProbe.TryRead(stream, ".bin", out var dimensions);

        Assert.True(actual);
        Assert.Equal(new DesktopImageDimensions(512, 256), dimensions);
    }

    [Fact]
    public void TryRead_returns_false_for_unknown_bytes()
    {
        using var stream = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]);

        var actual = DesktopImageDimensionProbe.TryRead(stream, ".dat", out var dimensions);

        Assert.False(actual);
        Assert.Equal(default, dimensions);
    }

    private static byte[] CreateJpegBytes(ushort width, ushort height, int appSegmentPayloadLength = 14)
    {
        appSegmentPayloadLength = Math.Max(14, appSegmentPayloadLength);
        var bytes = new List<byte>(appSegmentPayloadLength + 32)
        {
            0xFF, 0xD8,
            0xFF, 0xE0,
            (byte)((appSegmentPayloadLength + 2) >> 8),
            (byte)((appSegmentPayloadLength + 2) & 0xFF),
            (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00
        };

        for (var index = 14; index < appSegmentPayloadLength; index++)
        {
            bytes.Add((byte)(index % 251));
        }

        bytes.AddRange(
        [
            0xFF, 0xC0, 0x00, 0x11, 0x08,
            (byte)(height >> 8), (byte)(height & 0xFF),
            (byte)(width >> 8), (byte)(width & 0xFF),
            0x03,
            0x01, 0x11, 0x00,
            0x02, 0x11, 0x00,
            0x03, 0x11, 0x00,
            0xFF, 0xD9
        ]);

        return bytes.ToArray();
    }

    private static byte[] CreatePngBytes(uint width, uint height)
    {
        var bytes = new byte[33];
        bytes[0] = 137;
        bytes[1] = 80;
        bytes[2] = 78;
        bytes[3] = 71;
        bytes[4] = 13;
        bytes[5] = 10;
        bytes[6] = 26;
        bytes[7] = 10;
        bytes[11] = 13;
        bytes[12] = (byte)'I';
        bytes[13] = (byte)'H';
        bytes[14] = (byte)'D';
        bytes[15] = (byte)'R';
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), height);
        bytes[24] = 8;
        bytes[25] = 2;
        return bytes;
    }

    private static byte[] CreateWebPVp8XBytes(uint width, uint height)
    {
        var bytes = new byte[30];
        bytes[0] = (byte)'R';
        bytes[1] = (byte)'I';
        bytes[2] = (byte)'F';
        bytes[3] = (byte)'F';
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 22);
        bytes[8] = (byte)'W';
        bytes[9] = (byte)'E';
        bytes[10] = (byte)'B';
        bytes[11] = (byte)'P';
        bytes[12] = (byte)'V';
        bytes[13] = (byte)'P';
        bytes[14] = (byte)'8';
        bytes[15] = (byte)'X';
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), 10);

        WriteUInt24LittleEndian(bytes.AsSpan(24, 3), width - 1);
        WriteUInt24LittleEndian(bytes.AsSpan(27, 3), height - 1);
        return bytes;
    }

    private static byte[] CreateLittleEndianTiffBytes(uint width, uint height)
    {
        var bytes = new byte[38];
        bytes[0] = (byte)'I';
        bytes[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2, 2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), 2);

        WriteTiffEntry(bytes.AsSpan(10, 12), tag: 256, value: width);
        WriteTiffEntry(bytes.AsSpan(22, 12), tag: 257, value: height);
        return bytes;
    }

    private static byte[] CreatePsdBytes(uint width, uint height)
    {
        var bytes = new byte[26];
        bytes[0] = (byte)'8';
        bytes[1] = (byte)'B';
        bytes[2] = (byte)'P';
        bytes[3] = (byte)'S';
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12, 2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(14, 4), height);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(18, 4), width);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(22, 2), 8);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(24, 2), 3);
        return bytes;
    }

    private static void WriteUInt24LittleEndian(Span<byte> bytes, uint value)
    {
        bytes[0] = (byte)(value & 0xFF);
        bytes[1] = (byte)((value >> 8) & 0xFF);
        bytes[2] = (byte)((value >> 16) & 0xFF);
    }

    private static void WriteTiffEntry(Span<byte> bytes, ushort tag, uint value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[..2], tag);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[2..4], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..8], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[8..12], value);
    }
}
