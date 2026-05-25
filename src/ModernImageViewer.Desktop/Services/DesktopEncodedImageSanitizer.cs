using System.Buffers;
using System.Buffers.Binary;

namespace ModernImageViewer.Desktop.Services;

internal static class DesktopEncodedImageSanitizer
{
    private const int CopyBufferSize = 131072;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private const uint PngChunkTypeExif = 0x65584966;
    private const uint PngChunkTypeText = 0x74455874;
    private const uint PngChunkTypeCompressedText = 0x7A545874;
    private const uint PngChunkTypeInternationalText = 0x69545874;
    private const uint PngChunkTypeTime = 0x74494D45;
    private const uint PngChunkTypeEnd = 0x49454E44;

    public static bool TryStripMetadataWithoutReencode(string sourcePath, string targetPath, ExportImageFormat format)
    {
        try
        {
            return format switch
            {
                ExportImageFormat.Jpeg => DesktopFileStreamFactory.TryWriteAtomically(
                    targetPath,
                    output => StripJpegMetadata(sourcePath, output)),
                ExportImageFormat.Png => DesktopFileStreamFactory.TryWriteAtomically(
                    targetPath,
                    output => StripPngMetadata(sourcePath, output)),
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    private static bool StripJpegMetadata(string sourcePath, Stream output)
    {
        using var input = DesktopFileStreamFactory.OpenReadShared(sourcePath);
        var copyBuffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);

        try
        {
            Span<byte> soi = stackalloc byte[2];
            ReadExact(input, soi);
            if (soi[0] != 0xFF || soi[1] != 0xD8)
            {
                return false;
            }

            output.Write(soi);
            Span<byte> lengthBytes = stackalloc byte[2];

            while (true)
            {
                var marker = ReadJpegMarker(input);
                if (marker < 0)
                {
                    return false;
                }

                if (marker == 0xD9)
                {
                    WriteJpegMarker(output, marker);
                    return true;
                }

                if (marker == 0xDA)
                {
                    WriteJpegMarker(output, marker);
                    ReadExact(input, lengthBytes);
                    var scanLength = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
                    if (scanLength < 2)
                    {
                        return false;
                    }

                    output.Write(lengthBytes);
                    CopyExact(input, output, scanLength - 2, copyBuffer);
                    CopyToEnd(input, output, copyBuffer);
                    return true;
                }

                if (IsStandaloneJpegMarker(marker))
                {
                    WriteJpegMarker(output, marker);
                    continue;
                }

                ReadExact(input, lengthBytes);
                var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
                if (segmentLength < 2)
                {
                    return false;
                }

                if (ShouldDropJpegSegment(marker))
                {
                    SkipExact(input, segmentLength - 2, copyBuffer);
                    continue;
                }

                WriteJpegMarker(output, marker);
                output.Write(lengthBytes);
                CopyExact(input, output, segmentLength - 2, copyBuffer);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(copyBuffer);
        }
    }

    private static bool StripPngMetadata(string sourcePath, Stream output)
    {
        using var input = DesktopFileStreamFactory.OpenReadShared(sourcePath);
        var copyBuffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);

        try
        {
            Span<byte> signature = stackalloc byte[PngSignature.Length];
            ReadExact(input, signature);
            if (!signature.SequenceEqual(PngSignature))
            {
                return false;
            }

            output.Write(signature);
            Span<byte> lengthBytes = stackalloc byte[4];
            Span<byte> chunkTypeBytes = stackalloc byte[4];
            Span<byte> crcBytes = stackalloc byte[4];

            while (true)
            {
                ReadExact(input, lengthBytes);
                ReadExact(input, chunkTypeBytes);

                var chunkLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(lengthBytes));
                var chunkType = BinaryPrimitives.ReadUInt32BigEndian(chunkTypeBytes);

                if (!ShouldDropPngChunk(chunkType))
                {
                    output.Write(lengthBytes);
                    output.Write(chunkTypeBytes);
                    CopyExact(input, output, chunkLength, copyBuffer);
                    ReadExact(input, crcBytes);
                    output.Write(crcBytes);
                }
                else
                {
                    SkipExact(input, chunkLength, copyBuffer);
                    ReadExact(input, crcBytes);
                }

                if (chunkType == PngChunkTypeEnd)
                {
                    return true;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(copyBuffer);
        }
    }

    private static bool ShouldDropJpegSegment(int marker)
    {
        return marker is 0xE1 or 0xED or 0xFE;
    }

    private static bool ShouldDropPngChunk(uint chunkType)
    {
        return chunkType is
            PngChunkTypeExif or
            PngChunkTypeText or
            PngChunkTypeCompressedText or
            PngChunkTypeInternationalText or
            PngChunkTypeTime;
    }

    private static bool IsStandaloneJpegMarker(int marker)
    {
        return marker is 0x01 or >= 0xD0 and <= 0xD7;
    }

    private static int ReadJpegMarker(Stream input)
    {
        int currentByte;
        do
        {
            currentByte = input.ReadByte();
            if (currentByte < 0)
            {
                return -1;
            }
        }
        while (currentByte != 0xFF);

        do
        {
            currentByte = input.ReadByte();
            if (currentByte < 0)
            {
                return -1;
            }
        }
        while (currentByte == 0xFF);

        return currentByte;
    }

    private static void WriteJpegMarker(Stream output, int marker)
    {
        output.WriteByte(0xFF);
        output.WriteByte((byte)marker);
    }

    private static void ReadExact(Stream stream, Span<byte> buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var bytesRead = stream.Read(buffer[offset..]);
            if (bytesRead <= 0)
            {
                throw new EndOfStreamException();
            }

            offset += bytesRead;
        }
    }

    private static void CopyExact(Stream input, Stream output, int count, byte[] buffer)
    {
        var remaining = count;
        while (remaining > 0)
        {
            var bytesToRead = Math.Min(buffer.Length, remaining);
            ReadExact(input, buffer.AsSpan(0, bytesToRead));
            output.Write(buffer.AsSpan(0, bytesToRead));
            remaining -= bytesToRead;
        }
    }

    private static void SkipExact(Stream input, int count, byte[] buffer)
    {
        var remaining = count;
        while (remaining > 0)
        {
            var bytesToRead = Math.Min(buffer.Length, remaining);
            ReadExact(input, buffer.AsSpan(0, bytesToRead));
            remaining -= bytesToRead;
        }
    }

    private static void CopyToEnd(Stream input, Stream output, byte[] buffer)
    {
        while (true)
        {
            var bytesRead = input.Read(buffer, 0, buffer.Length);
            if (bytesRead <= 0)
            {
                return;
            }

            output.Write(buffer.AsSpan(0, bytesRead));
        }
    }
}
