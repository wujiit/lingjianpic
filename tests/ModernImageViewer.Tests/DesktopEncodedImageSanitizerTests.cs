using System.Buffers.Binary;
using System.Text;
using ImageMagick;
using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Tests;

public sealed class DesktopEncodedImageSanitizerTests
{
    [Fact]
    public void TryStripMetadataWithoutReencode_removes_jpeg_exif_and_preserves_image()
    {
        using var paths = TestPaths.Create();
        var sourcePath = paths.Combine("source.jpg");
        var targetPath = paths.Combine("clean.jpg");
        WriteJpegWithExif(sourcePath);

        var stripped = DesktopEncodedImageSanitizer.TryStripMetadataWithoutReencode(
            sourcePath,
            targetPath,
            ExportImageFormat.Jpeg);

        Assert.True(stripped);

        using var output = new MagickImage(targetPath);
        Assert.Equal(MagickFormat.Jpeg, output.Format);
        Assert.Equal(24U, output.Width);
        Assert.Equal(16U, output.Height);
        Assert.Null(output.GetExifProfile());
    }

    [Fact]
    public void TryStripMetadataWithoutReencode_removes_png_text_chunks_and_keeps_image_valid()
    {
        using var paths = TestPaths.Create();
        var sourcePath = paths.Combine("source.png");
        var targetPath = paths.Combine("clean.png");
        WritePngWithTextChunk(sourcePath);

        Assert.True(HasPngChunk(sourcePath, "tEXt"));

        var stripped = DesktopEncodedImageSanitizer.TryStripMetadataWithoutReencode(
            sourcePath,
            targetPath,
            ExportImageFormat.Png);

        Assert.True(stripped);
        Assert.False(HasPngChunk(targetPath, "tEXt"));

        using var output = new MagickImage(targetPath);
        Assert.Equal(MagickFormat.Png, output.Format);
        Assert.Equal(32U, output.Width);
        Assert.Equal(18U, output.Height);
    }

    private static void WriteJpegWithExif(string sourcePath)
    {
        using var source = new MagickImage(MagickColors.White, 24, 16);
        var profile = new ExifProfile();
        profile.SetValue(ExifTag.Artist, "Sanitizer Test");
        profile.SetValue(ExifTag.ImageDescription, "Keep pixels, drop metadata");
        profile.Rewrite();
        source.SetProfile(profile);
        source.Write(sourcePath);
    }

    private static void WritePngWithTextChunk(string sourcePath)
    {
        using (var source = new MagickImage(MagickColors.DeepSkyBlue, 32, 18))
        {
            source.Write(sourcePath);
        }

        InjectPngChunkAfterIhdr(
            sourcePath,
            "tEXt",
            BuildPngTextChunkData("Comment", "Keep pixels, drop text metadata"));
    }

    private static byte[] BuildPngTextChunkData(string key, string value)
    {
        return Encoding.ASCII.GetBytes($"{key}\0{value}");
    }

    private static bool HasPngChunk(string path, string chunkType)
    {
        var bytes = File.ReadAllBytes(path);
        var expectedType = Encoding.ASCII.GetBytes(chunkType);
        var offset = 8;

        while (offset + 12 <= bytes.Length)
        {
            var chunkLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4)));
            var currentType = bytes.AsSpan(offset + 4, 4);
            if (currentType.SequenceEqual(expectedType))
            {
                return true;
            }

            offset += chunkLength + 12;
        }

        return false;
    }

    private static void InjectPngChunkAfterIhdr(string path, string chunkType, byte[] chunkData)
    {
        var bytes = File.ReadAllBytes(path);
        var signatureLength = 8;
        var ihdrLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(signatureLength, 4)));
        var ihdrTotalLength = ihdrLength + 12;

        using var output = new MemoryStream(bytes.Length + chunkData.Length + 64);
        output.Write(bytes.AsSpan(0, signatureLength + ihdrTotalLength));
        WritePngChunk(output, chunkType, chunkData);
        output.Write(bytes.AsSpan(signatureLength + ihdrTotalLength));
        File.WriteAllBytes(path, output.ToArray());
    }

    private static void WritePngChunk(Stream output, string chunkType, byte[] chunkData)
    {
        var chunkTypeBytes = Encoding.ASCII.GetBytes(chunkType);
        Span<byte> chunkLengthBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(chunkLengthBytes, (uint)chunkData.Length);
        output.Write(chunkLengthBytes);
        output.Write(chunkTypeBytes);
        output.Write(chunkData);

        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, ComputePngCrc(chunkTypeBytes, chunkData));
        output.Write(crcBytes);
    }

    private static uint ComputePngCrc(byte[] chunkTypeBytes, byte[] chunkData)
    {
        var crc = 0xFFFFFFFFu;
        UpdatePngCrc(ref crc, chunkTypeBytes);
        UpdatePngCrc(ref crc, chunkData);
        return ~crc;
    }

    private static void UpdatePngCrc(ref uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) == 0
                    ? crc >> 1
                    : 0xEDB88320u ^ (crc >> 1);
            }
        }
    }
}
