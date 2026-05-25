using ImageMagick;
using ImageMagick.Drawing;
using ModernImageViewer.Core;

namespace ModernImageViewer.Desktop.Services;

public enum ExportImageFormat
{
    Original,
    Jpeg,
    Png,
    WebP,
    Avif,
    Tiff,
    Bmp,
    Jxl,
    Heic
}

public enum DesktopWatermarkKind
{
    Text,
    Image
}

public enum DesktopWatermarkPlacement
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    Center,
    Tiled
}

public sealed record DesktopWatermarkRequest(
    DesktopWatermarkKind Kind,
    DesktopWatermarkPlacement Placement,
    string Text,
    string ImagePath,
    int OpacityPercent,
    int MarginPixels,
    int TextPointSize,
    string TextColor,
    int ImageScalePercent);

public sealed record DesktopExportRequest(
    ExportImageFormat Format,
    int? LongEdgePixels,
    int JpegQuality,
    bool StripMetadata,
    bool PreserveEncodedData = false,
    ExportImageFormat FallbackFormat = ExportImageFormat.Jpeg,
    long? TargetFileSizeBytes = null,
    DesktopWatermarkRequest? Watermark = null);

internal enum DesktopPreparedExportMode
{
    CopySource,
    StripMetadataWithoutReencode,
    TargetSizeCopy,
    TargetSizeReencode,
    Reencode
}

internal sealed record DesktopPreparedExportOperation(
    string SourcePath,
    DesktopImageSourceInfo SourceInfo,
    DesktopExportRequest ExecutionRequest,
    ExportImageFormat OutputFormat,
    string TargetExtension,
    DesktopPreparedExportMode Mode);

public sealed class DesktopImageExportService
{
    private const int MinimumTargetCompressionQuality = 40;
    private const int MinimumTargetCompressionLongEdge = 640;
    private const int TargetCompressionMaxResizePasses = 6;
    private const int TargetCompressionMaxQualityProbes = 5;
    private const double TargetCompressionDecodeHeadroom = 1.18;
    private const double TargetCompressionInitialScaleFloor = 0.45;
    private const double TargetCompressionInitialScaleCeiling = 0.94;
    private const double TargetCompressionNearSourceRatioThreshold = 0.72;
    private const int WatermarkTemplateCacheEntryLimit = 4;
    private const int WatermarkVariantCacheEntryLimit = 12;

    private readonly object _watermarkCacheSyncRoot = new();
    private readonly Dictionary<string, CachedWatermarkTemplate> _watermarkTemplateCache = new(PathComparison.Comparer);
    private readonly Dictionary<string, CachedWatermarkVariant> _watermarkVariantCache = new(PathComparison.Comparer);
    private readonly DesktopImageDimensionCacheStore _dimensionCache;
    private long _watermarkCacheAccessStamp;

    private sealed class CachedWatermarkTemplate(byte[] preparedBytes, long sourceLength, long lastWriteUtcTicks, long accessStamp)
    {
        public byte[] PreparedBytes { get; } = preparedBytes;

        public long SourceLength { get; } = sourceLength;

        public long LastWriteUtcTicks { get; } = lastWriteUtcTicks;

        public long AccessStamp { get; set; } = accessStamp;
    }

    private sealed class CachedWatermarkVariant(byte[] preparedBytes, long accessStamp)
    {
        public byte[] PreparedBytes { get; } = preparedBytes;

        public long AccessStamp { get; set; } = accessStamp;
    }

    internal sealed record TargetCompressionQualitySearchResult(
        int SelectedQuality,
        bool WithinTarget,
        IReadOnlyList<int> ProbedQualities)
    {
        public int ProbeCount => ProbedQualities.Count;
    }

    public DesktopImageExportService()
        : this(DesktopImageDimensionCacheStore.Shared)
    {
    }

    internal DesktopImageExportService(DesktopImageDimensionCacheStore dimensionCache)
    {
        _dimensionCache = dimensionCache ?? throw new ArgumentNullException(nameof(dimensionCache));
    }

    public void Export(
        string sourcePath,
        string targetPath,
        DesktopExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        Export(PrepareOperation(sourcePath, request), targetPath, cancellationToken);
    }

    public string GetFileExtension(string sourcePath, DesktopExportRequest request)
    {
        return PrepareOperation(sourcePath, request).TargetExtension;
    }

    internal (int TemplateCount, int VariantCount) GetWatermarkCacheEntryCounts()
    {
        lock (_watermarkCacheSyncRoot)
        {
            return (_watermarkTemplateCache.Count, _watermarkVariantCache.Count);
        }
    }

    internal DesktopPreparedExportOperation PrepareOperation(string sourcePath, DesktopExportRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var sourceInfo = DesktopImageSourceInfoReader.Read(sourcePath, _dimensionCache);
        if (CanCopyWithoutReencode(request))
        {
            return new DesktopPreparedExportOperation(
                sourcePath,
                sourceInfo,
                request,
                ExportImageFormat.Original,
                ResolveTargetExtension(sourcePath, request),
                DesktopPreparedExportMode.CopySource);
        }

        var outputFormat = ResolveOutputFormat(sourcePath, request);
        var executionRequest = request;
        var mode = DesktopPreparedExportMode.Reencode;

        if (request.PreserveEncodedData)
        {
            executionRequest = request with
            {
                Format = outputFormat,
                PreserveEncodedData = false
            };

            if (CanStripMetadataWithoutReencode(sourcePath, request, outputFormat))
            {
                mode = DesktopPreparedExportMode.StripMetadataWithoutReencode;
            }
        }

        if (mode != DesktopPreparedExportMode.StripMetadataWithoutReencode)
        {
            if (CanSatisfyTargetSizeByCopyingSource(sourcePath, executionRequest, outputFormat, sourceInfo))
            {
                mode = DesktopPreparedExportMode.TargetSizeCopy;
            }
            else if (UsesQualitySetting(outputFormat)
                && executionRequest.TargetFileSizeBytes is > 0
                && !executionRequest.PreserveEncodedData)
            {
                mode = DesktopPreparedExportMode.TargetSizeReencode;
            }
        }

        return new DesktopPreparedExportOperation(
            sourcePath,
            sourceInfo,
            executionRequest,
            outputFormat,
            ResolveTargetExtension(sourcePath, request, outputFormat),
            mode);
    }

    internal void Export(
        DesktopPreparedExportOperation operation,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        cancellationToken.ThrowIfCancellationRequested();

        EnsureTargetDirectory(targetPath);

        switch (operation.Mode)
        {
            case DesktopPreparedExportMode.CopySource:
            case DesktopPreparedExportMode.TargetSizeCopy:
                cancellationToken.ThrowIfCancellationRequested();
                DesktopFileStreamFactory.CopyFile(operation.SourcePath, targetPath, overwrite: true);
                return;

            case DesktopPreparedExportMode.StripMetadataWithoutReencode:
                cancellationToken.ThrowIfCancellationRequested();
                if (DesktopEncodedImageSanitizer.TryStripMetadataWithoutReencode(
                    operation.SourcePath,
                    targetPath,
                    operation.OutputFormat))
                {
                    return;
                }

                ExecuteReencodeExport(operation, targetPath, cancellationToken);
                return;

            case DesktopPreparedExportMode.TargetSizeReencode:
                ExportWithTargetSize(operation, targetPath, cancellationToken);
                return;

            default:
                ExecuteReencodeExport(operation, targetPath, cancellationToken);
                return;
        }
    }

    private static bool CanCopyWithoutReencode(DesktopExportRequest request)
    {
        return !request.StripMetadata
            && !request.PreserveEncodedData
            && request.Watermark is null
            && request.LongEdgePixels is not > 0
            && request.TargetFileSizeBytes is not > 0
            && request.Format == ExportImageFormat.Original;
    }

    private static void EnsureTargetDirectory(string targetPath)
    {
        var directoryPath = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new InvalidOperationException("Target path must include a parent directory.");
        }

        Directory.CreateDirectory(directoryPath);
    }

    public static bool SupportsTargetSizeCompression(ExportImageFormat format)
    {
        return UsesQualitySetting(format);
    }

    private void ExportWithTargetSize(
        DesktopPreparedExportOperation operation,
        string targetPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = operation.ExecutionRequest;
        var targetBytes = request.TargetFileSizeBytes.GetValueOrDefault();
        if (targetBytes <= 0)
        {
            ExecuteReencodeExport(
                operation with
                {
                    ExecutionRequest = request with { TargetFileSizeBytes = null }
                },
                targetPath,
                cancellationToken);
            return;
        }

        DesktopMagickOperationGate.Shared.Run(() =>
        {
            using var baseImage = LoadPreparedImage(operation.SourcePath, operation.SourceInfo, request, operation.OutputFormat);
            var sourceLongEdge = (int)Math.Max(baseImage.Width, baseImage.Height);
            var sourceSizeBytes = operation.SourceInfo.SizeBytes;
            var currentLongEdge = CalculateInitialTargetCompressionLongEdge(sourceLongEdge, sourceSizeBytes, targetBytes);
            byte[]? bestBytes = null;
            long bestLength = long.MaxValue;

            for (var resizePass = 0; resizePass < TargetCompressionMaxResizePasses; resizePass++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var workingImage = CreateTargetCompressionAttemptImage(baseImage, currentLongEdge);
                var attemptBytes = FindBestBytesForTarget(
                    workingImage,
                    operation.OutputFormat,
                    targetBytes,
                    MinimumTargetCompressionQuality,
                    request.JpegQuality,
                    cancellationToken,
                    out var withinTarget);

                if (attemptBytes.LongLength < bestLength)
                {
                    bestBytes = attemptBytes;
                    bestLength = attemptBytes.LongLength;
                }

                if (withinTarget)
                {
                    bestBytes = attemptBytes;
                    break;
                }

                if (currentLongEdge <= MinimumTargetCompressionLongEdge)
                {
                    break;
                }

                var nextLongEdge = CalculateNextLongEdge(currentLongEdge, attemptBytes.LongLength, targetBytes);
                if (nextLongEdge >= currentLongEdge)
                {
                    break;
                }

                currentLongEdge = nextLongEdge;
                DesktopImageProcessingPolicy.TrimMemory();
            }

            cancellationToken.ThrowIfCancellationRequested();
            bestBytes ??= EncodeBytes(baseImage, operation.OutputFormat, request.JpegQuality);
            DesktopFileStreamFactory.WriteAtomically(targetPath, stream => stream.Write(bestBytes));
            DesktopImageProcessingPolicy.TrimMemory();
        }, cancellationToken);
    }

    private void ExecuteReencodeExport(
        DesktopPreparedExportOperation operation,
        string targetPath,
        CancellationToken cancellationToken)
    {
        DesktopMagickOperationGate.Shared.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var image = LoadPreparedImage(
                operation.SourcePath,
                operation.SourceInfo,
                operation.ExecutionRequest,
                operation.OutputFormat);
            cancellationToken.ThrowIfCancellationRequested();
            DesktopFileStreamFactory.WriteAtomically(
                targetPath,
                stream => WriteImage(image, stream, operation.ExecutionRequest, operation.OutputFormat));
        }, cancellationToken);
    }

    internal static IMagickImage<byte> CreateTargetCompressionAttemptImage(IMagickImage<byte> baseImage, int longEdgePixels)
    {
        ArgumentNullException.ThrowIfNull(baseImage);

        var attempt = baseImage.Clone();
        try
        {
            ResizeToLongEdge(attempt, longEdgePixels);
            return attempt;
        }
        catch
        {
            attempt.Dispose();
            throw;
        }
    }

    private static byte[] FindBestBytesForTarget(
        IMagickImage<byte> image,
        ExportImageFormat outputFormat,
        long targetBytes,
        int minimumQuality,
        int maximumQuality,
        CancellationToken cancellationToken,
        out bool withinTarget)
    {
        var encodedBytesByQuality = new Dictionary<int, byte[]>();
        long ProbeSize(int quality)
        {
            if (!encodedBytesByQuality.TryGetValue(quality, out var bytes))
            {
                bytes = EncodeBytesForTargetProbe(image, outputFormat, quality);
                encodedBytesByQuality[quality] = bytes;
            }

            return bytes.LongLength;
        }

        var searchResult = SearchBestQualityForTarget(
            targetBytes,
            minimumQuality,
            maximumQuality,
            ProbeSize,
            cancellationToken);

        withinTarget = searchResult.WithinTarget;
        return encodedBytesByQuality[searchResult.SelectedQuality];
    }

    internal static TargetCompressionQualitySearchResult SearchBestQualityForTarget(
        long targetBytes,
        int minimumQuality,
        int maximumQuality,
        Func<int, long> sizeProbe,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sizeProbe);

        var minQuality = Math.Clamp(minimumQuality, 1, 100);
        var maxQuality = Math.Clamp(maximumQuality, minQuality, 100);
        var probedQualities = new List<int>(TargetCompressionMaxQualityProbes + 2);

        long Probe(int quality)
        {
            cancellationToken.ThrowIfCancellationRequested();
            probedQualities.Add(quality);
            return sizeProbe(quality);
        }

        var highQualityBytes = Probe(maxQuality);
        if (highQualityBytes <= targetBytes)
        {
            return new TargetCompressionQualitySearchResult(maxQuality, WithinTarget: true, probedQualities);
        }

        if (minQuality == maxQuality)
        {
            return new TargetCompressionQualitySearchResult(minQuality, WithinTarget: false, probedQualities);
        }

        var lowQualityBytes = Probe(minQuality);
        if (lowQualityBytes > targetBytes)
        {
            return new TargetCompressionQualitySearchResult(minQuality, WithinTarget: false, probedQualities);
        }

        var left = minQuality;
        var right = maxQuality;
        var leftBytes = lowQualityBytes;
        var rightBytes = highQualityBytes;
        var bestQuality = minQuality;
        var probeCount = 0;
        while (right - left > 1 && probeCount < TargetCompressionMaxQualityProbes)
        {
            var candidateQuality = EstimateTargetCompressionQuality(
                left,
                leftBytes,
                right,
                rightBytes,
                targetBytes);
            if (candidateQuality <= left || candidateQuality >= right)
            {
                candidateQuality = left + ((right - left) / 2);
            }

            var currentBytes = Probe(candidateQuality);
            probeCount++;

            if (currentBytes <= targetBytes)
            {
                bestQuality = candidateQuality;
                left = candidateQuality;
                leftBytes = currentBytes;
                if (currentBytes == targetBytes)
                {
                    return new TargetCompressionQualitySearchResult(bestQuality, WithinTarget: true, probedQualities);
                }
            }
            else
            {
                right = candidateQuality;
                rightBytes = currentBytes;
            }
        }

        return new TargetCompressionQualitySearchResult(bestQuality, WithinTarget: true, probedQualities);
    }

    internal static int EstimateTargetCompressionQuality(
        int minimumQuality,
        long minimumQualityBytes,
        int maximumQuality,
        long maximumQualityBytes,
        long targetBytes)
    {
        var minQuality = Math.Clamp(minimumQuality, 1, 100);
        var maxQuality = Math.Clamp(maximumQuality, minQuality, 100);
        if (maxQuality - minQuality <= 1)
        {
            return minQuality;
        }

        if (maximumQualityBytes <= minimumQualityBytes)
        {
            return minQuality + ((maxQuality - minQuality) / 2);
        }

        var qualitySpan = maxQuality - minQuality;
        var clampedTargetBytes = Math.Clamp(targetBytes, minimumQualityBytes, maximumQualityBytes);
        var targetRatio = (clampedTargetBytes - minimumQualityBytes)
            / (double)(maximumQualityBytes - minimumQualityBytes);
        var estimatedQuality = minQuality + (int)Math.Round(
            qualitySpan * targetRatio,
            MidpointRounding.AwayFromZero);
        return Math.Clamp(estimatedQuality, minQuality + 1, maxQuality - 1);
    }

    private static byte[] EncodeBytesForTargetProbe(IMagickImage<byte> image, ExportImageFormat outputFormat, int quality)
    {
        image.Format = ToMagickFormat(outputFormat);
        image.Quality = (uint)Math.Clamp(quality, 1, 100);
        if (RequiresOpaqueBackground(outputFormat))
        {
            EnsureOpaqueBackgroundForJpeg(image);
        }

        using var stream = new MemoryStream();
        image.Write(stream);
        return stream.ToArray();
    }

    private static byte[] EncodeBytes(IMagickImage<byte> image, ExportImageFormat outputFormat, int quality)
    {
        using var clone = image.Clone();
        clone.Format = ToMagickFormat(outputFormat);
        clone.Quality = (uint)Math.Clamp(quality, 1, 100);
        if (RequiresOpaqueBackground(outputFormat))
        {
            EnsureOpaqueBackgroundForJpeg(clone);
        }

        using var stream = new MemoryStream();
        clone.Write(stream);
        return stream.ToArray();
    }

    private static int CalculateNextLongEdge(int currentLongEdge, long currentBytes, long targetBytes)
    {
        if (currentLongEdge <= MinimumTargetCompressionLongEdge
            || currentBytes <= 0
            || targetBytes <= 0)
        {
            return currentLongEdge;
        }

        var scale = Math.Sqrt((double)targetBytes / currentBytes) * 0.97;
        scale = Math.Clamp(scale, 0.70, 0.92);

        var nextLongEdge = (int)Math.Floor(currentLongEdge * scale);
        if (nextLongEdge >= currentLongEdge)
        {
            nextLongEdge = currentLongEdge - 1;
        }

        return Math.Max(MinimumTargetCompressionLongEdge, nextLongEdge);
    }

    internal static int CalculateInitialTargetCompressionLongEdge(int sourceLongEdge, long sourceBytes, long targetBytes)
    {
        if (sourceLongEdge <= MinimumTargetCompressionLongEdge
            || sourceBytes <= 0
            || targetBytes <= 0
            || targetBytes >= sourceBytes)
        {
            return Math.Max(MinimumTargetCompressionLongEdge, sourceLongEdge);
        }

        var ratio = targetBytes / (double)sourceBytes;
        if (ratio >= TargetCompressionNearSourceRatioThreshold)
        {
            return sourceLongEdge;
        }

        var scale = Math.Pow(ratio, 0.35) * 1.08;
        scale = Math.Clamp(scale, TargetCompressionInitialScaleFloor, TargetCompressionInitialScaleCeiling);

        var estimatedLongEdge = (int)Math.Floor(sourceLongEdge * scale);
        if (estimatedLongEdge >= sourceLongEdge)
        {
            return sourceLongEdge;
        }

        return Math.Clamp(estimatedLongEdge, MinimumTargetCompressionLongEdge, sourceLongEdge);
    }

    private IMagickImage<byte> LoadPreparedImage(
        string sourcePath,
        DesktopImageSourceInfo sourceInfo,
        DesktopExportRequest request,
        ExportImageFormat outputFormat)
    {
        var readSettings = CreateExportReadSettings(sourceInfo, request);
        var image = new MagickImage(sourcePath, readSettings);
        try
        {
            image.AutoOrient();
            if (request.StripMetadata)
            {
                image.Strip();
            }

            ResizeToLongEdge(image, request.LongEdgePixels);

            if (request.Watermark is not null)
            {
                ApplyWatermark(image, request.Watermark);
            }

            if (outputFormat == ExportImageFormat.Jpeg)
            {
                EnsureOpaqueBackgroundForJpeg(image);
            }

            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    internal MagickReadSettings CreateExportReadSettings(string sourcePath, DesktopExportRequest request)
    {
        var sourceInfo = DesktopImageSourceInfoReader.Read(sourcePath, _dimensionCache);
        return CreateExportReadSettings(sourceInfo, request);
    }

    internal static MagickReadSettings CreateExportReadSettings(
        DesktopImageSourceInfo sourceInfo,
        DesktopExportRequest request)
    {
        var settings = new MagickReadSettings();

        var dimensions = sourceInfo.Dimensions;
        if (dimensions is not { } imageSize)
        {
            return settings;
        }

        var readLongEdge = CalculateAdaptiveReadLongEdge(
            imageSize.LongEdge,
            request.LongEdgePixels,
            sourceInfo.SizeBytes,
            request.TargetFileSizeBytes);
        if (readLongEdge <= 0 || imageSize.LongEdge <= readLongEdge)
        {
            return settings;
        }

        var scale = readLongEdge / (double)imageSize.LongEdge;
        settings.Width = (uint)Math.Max(1, (int)Math.Round(imageSize.Width * scale));
        settings.Height = (uint)Math.Max(1, (int)Math.Round(imageSize.Height * scale));
        return settings;
    }

    internal static uint CalculateAdaptiveReadLongEdge(
        uint sourceLongEdge,
        int? requestedLongEdgePixels,
        long sourceBytes,
        long? targetBytes)
    {
        if (sourceLongEdge == 0)
        {
            return 0;
        }

        if (requestedLongEdgePixels is > 0)
        {
            return (uint)Math.Clamp(requestedLongEdgePixels.Value, 1, (int)sourceLongEdge);
        }

        if (targetBytes is not > 0
            || sourceBytes <= 0
            || targetBytes.Value >= sourceBytes
            || sourceLongEdge <= MinimumTargetCompressionLongEdge)
        {
            return sourceLongEdge;
        }

        var initialLongEdge = CalculateInitialTargetCompressionLongEdge((int)sourceLongEdge, sourceBytes, targetBytes.Value);
        if (initialLongEdge >= sourceLongEdge)
        {
            return sourceLongEdge;
        }

        var conservativeDecodeLongEdge = (int)Math.Ceiling(initialLongEdge * TargetCompressionDecodeHeadroom);
        return (uint)Math.Clamp(conservativeDecodeLongEdge, initialLongEdge, (int)sourceLongEdge);
    }

    private static void ResizeToLongEdge(IMagickImage<byte> image, int? longEdgePixels)
    {
        if (longEdgePixels is not > 0)
        {
            return;
        }

        var currentLongEdge = (int)Math.Max(image.Width, image.Height);
        if (currentLongEdge <= longEdgePixels.Value)
        {
            return;
        }

        var scale = longEdgePixels.Value / (double)currentLongEdge;
        var targetWidth = (uint)Math.Max(1, (int)Math.Round(image.Width * scale));
        var targetHeight = (uint)Math.Max(1, (int)Math.Round(image.Height * scale));
        image.Resize(targetWidth, targetHeight);
    }

    private static void EnsureOpaqueBackgroundForJpeg(IMagickImage<byte> image)
    {
        image.BackgroundColor = MagickColors.White;
        image.Alpha(AlphaOption.Remove);
    }

    private void ApplyWatermark(IMagickImage<byte> image, DesktopWatermarkRequest request)
    {
        if (request.Kind == DesktopWatermarkKind.Image)
        {
            ApplyImageWatermark(image, request);
            return;
        }

        ApplyTextWatermark(image, request);
    }

    private static void ApplyTextWatermark(IMagickImage<byte> image, DesktopWatermarkRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return;
        }

        var pointSize = Math.Clamp(request.TextPointSize, 8, 256);
        var margin = Math.Max(0, request.MarginPixels);
        var textWidth = Math.Max(pointSize, EstimateTextWidth(request.Text, pointSize));
        var textHeight = Math.Max(pointSize, (int)Math.Ceiling(pointSize * 1.25));
        var fillColor = CreateWatermarkColor(request.TextColor, request.OpacityPercent);
        var strokeColor = CreateWatermarkStrokeColor(request.OpacityPercent);

        void DrawAt(double x, double y)
        {
            new Drawables()
                .FontPointSize(pointSize)
                .FillColor(fillColor)
                .StrokeColor(strokeColor)
                .StrokeWidth(Math.Max(1, pointSize / 18.0))
                .Text(x, y, request.Text)
                .Draw(image);
        }

        if (request.Placement == DesktopWatermarkPlacement.Tiled)
        {
            var xStep = Math.Max(textWidth + margin * 3, pointSize * 6);
            var yStep = Math.Max(textHeight + margin * 3, pointSize * 3);
            for (var y = margin + textHeight; y < image.Height + textHeight; y += yStep)
            {
                for (var x = margin; x < image.Width + textWidth; x += xStep)
                {
                    DrawAt(x, y);
                }
            }

            return;
        }

        var (originX, originY) = ResolveWatermarkOrigin(
            image.Width,
            image.Height,
            textWidth,
            textHeight,
            margin,
            request.Placement);
        DrawAt(originX, originY + textHeight - Math.Max(2, pointSize / 5.0));
    }

    private void ApplyImageWatermark(IMagickImage<byte> image, DesktopWatermarkRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ImagePath) || !File.Exists(request.ImagePath))
        {
            throw new FileNotFoundException("Watermark image does not exist.", request.ImagePath);
        }

        using var watermark = LoadPreparedWatermarkVariant(
            request.ImagePath,
            image.Width,
            image.Height,
            request.ImageScalePercent,
            request.OpacityPercent);

        if (request.Placement == DesktopWatermarkPlacement.Tiled)
        {
            var margin = Math.Max(0, request.MarginPixels);
            var xStep = Math.Max((int)watermark.Width + margin * 3, 1);
            var yStep = Math.Max((int)watermark.Height + margin * 3, 1);
            for (var y = margin; y < image.Height; y += yStep)
            {
                for (var x = margin; x < image.Width; x += xStep)
                {
                    image.Composite(watermark, x, y, CompositeOperator.Over);
                }
            }

            return;
        }

        var (originX, originY) = ResolveWatermarkOrigin(
            image.Width,
            image.Height,
            (int)watermark.Width,
            (int)watermark.Height,
            Math.Max(0, request.MarginPixels),
            request.Placement);
        image.Composite(watermark, (int)Math.Round(originX), (int)Math.Round(originY), CompositeOperator.Over);
    }

    private IMagickImage<byte> LoadPreparedWatermarkVariant(
        string watermarkPath,
        uint canvasWidth,
        uint canvasHeight,
        int scalePercent,
        int opacityPercent)
    {
        var normalizedPath = Path.GetFullPath(watermarkPath);
        if (!DesktopFileSignatureReader.TryRead(normalizedPath, out var sourceSignature))
        {
            throw new FileNotFoundException("Watermark image does not exist.", normalizedPath);
        }

        var targetLongEdge = CalculateWatermarkTargetLongEdge(canvasWidth, canvasHeight, scalePercent);
        var safeOpacityPercent = Math.Clamp(opacityPercent, 1, 100);
        var variantCacheKey = CreateWatermarkVariantCacheKey(sourceSignature, targetLongEdge, safeOpacityPercent);
        if (TryGetCachedWatermarkVariant(variantCacheKey, out var cachedVariantBytes))
        {
            return new MagickImage(cachedVariantBytes);
        }

        var templateBytes = GetPreparedWatermarkTemplateBytes(sourceSignature);
        using var watermark = new MagickImage(templateBytes);
        ResizeToLongEdge(watermark, targetLongEdge);

        if (safeOpacityPercent < 100)
        {
            watermark.Evaluate(Channels.Alpha, EvaluateOperator.Multiply, safeOpacityPercent / 100.0);
        }

        var variantBytes = EncodeWatermarkBytes(watermark);
        StoreCachedWatermarkVariant(variantCacheKey, variantBytes);
        return new MagickImage(variantBytes);
    }

    private byte[] GetPreparedWatermarkTemplateBytes(DesktopFileSignature sourceSignature)
    {
        if (TryGetCachedWatermarkTemplate(sourceSignature, out var cachedBytes))
        {
            return cachedBytes;
        }

        using var watermark = new MagickImage(sourceSignature.Path);
        watermark.AutoOrient();
        watermark.Alpha(AlphaOption.Set);
        var preparedBytes = EncodeWatermarkBytes(watermark);
        StoreCachedWatermarkTemplate(sourceSignature, preparedBytes);
        return preparedBytes;
    }

    private bool TryGetCachedWatermarkTemplate(DesktopFileSignature sourceSignature, out byte[] preparedBytes)
    {
        lock (_watermarkCacheSyncRoot)
        {
            if (_watermarkTemplateCache.TryGetValue(sourceSignature.Path, out var entry)
                && entry.SourceLength == sourceSignature.SizeBytes
                && entry.LastWriteUtcTicks == sourceSignature.SourceStampTicks)
            {
                entry.AccessStamp = NextWatermarkAccessStampUnsafe();
                preparedBytes = entry.PreparedBytes;
                return true;
            }
        }

        preparedBytes = Array.Empty<byte>();
        return false;
    }

    private void StoreCachedWatermarkTemplate(DesktopFileSignature sourceSignature, byte[] preparedBytes)
    {
        lock (_watermarkCacheSyncRoot)
        {
            _watermarkTemplateCache[sourceSignature.Path] = new CachedWatermarkTemplate(
                preparedBytes,
                sourceSignature.SizeBytes,
                sourceSignature.SourceStampTicks,
                NextWatermarkAccessStampUnsafe());

            RemoveWatermarkVariantsForPathUnsafe(sourceSignature.Path);
            TrimWatermarkCacheUnsafe(_watermarkTemplateCache, WatermarkTemplateCacheEntryLimit, static entry => entry.AccessStamp);
        }
    }

    private bool TryGetCachedWatermarkVariant(string cacheKey, out byte[] preparedBytes)
    {
        lock (_watermarkCacheSyncRoot)
        {
            if (_watermarkVariantCache.TryGetValue(cacheKey, out var entry))
            {
                entry.AccessStamp = NextWatermarkAccessStampUnsafe();
                preparedBytes = entry.PreparedBytes;
                return true;
            }
        }

        preparedBytes = Array.Empty<byte>();
        return false;
    }

    private void StoreCachedWatermarkVariant(string cacheKey, byte[] preparedBytes)
    {
        lock (_watermarkCacheSyncRoot)
        {
            _watermarkVariantCache[cacheKey] = new CachedWatermarkVariant(preparedBytes, NextWatermarkAccessStampUnsafe());
            TrimWatermarkCacheUnsafe(_watermarkVariantCache, WatermarkVariantCacheEntryLimit, static entry => entry.AccessStamp);
        }
    }

    private void RemoveWatermarkVariantsForPathUnsafe(string normalizedPath)
    {
        if (_watermarkVariantCache.Count == 0)
        {
            return;
        }

        var prefix = normalizedPath + "|";
        var staleKeys = _watermarkVariantCache.Keys
            .Where(key => key.StartsWith(prefix, PathComparison.Comparison))
            .ToArray();

        foreach (var staleKey in staleKeys)
        {
            _watermarkVariantCache.Remove(staleKey);
        }
    }

    private long NextWatermarkAccessStampUnsafe()
    {
        _watermarkCacheAccessStamp++;
        return _watermarkCacheAccessStamp;
    }

    private static void TrimWatermarkCacheUnsafe<TValue>(
        Dictionary<string, TValue> cache,
        int maxEntries,
        Func<TValue, long> accessStampSelector)
    {
        while (cache.Count > maxEntries)
        {
            string? oldestKey = null;
            var oldestAccessStamp = long.MaxValue;

            foreach (var pair in cache)
            {
                var accessStamp = accessStampSelector(pair.Value);
                if (accessStamp >= oldestAccessStamp)
                {
                    continue;
                }

                oldestAccessStamp = accessStamp;
                oldestKey = pair.Key;
            }

            if (string.IsNullOrWhiteSpace(oldestKey))
            {
                break;
            }

            cache.Remove(oldestKey);
        }
    }

    private static byte[] EncodeWatermarkBytes(IMagickImage<byte> image)
    {
        image.Format = MagickFormat.Png;
        using var stream = new MemoryStream();
        image.Write(stream);
        return stream.ToArray();
    }

    private static int CalculateWatermarkTargetLongEdge(uint canvasWidth, uint canvasHeight, int scalePercent)
    {
        var safeScalePercent = Math.Clamp(scalePercent, 1, 100);
        return Math.Max(1, (int)Math.Round(Math.Min(canvasWidth, canvasHeight) * (safeScalePercent / 100.0)));
    }

    private static string CreateWatermarkVariantCacheKey(
        DesktopFileSignature sourceSignature,
        int targetLongEdge,
        int opacityPercent)
    {
        return $"{sourceSignature.Path}|{sourceSignature.SizeBytes}|{sourceSignature.SourceStampTicks}|{targetLongEdge}|{opacityPercent}";
    }

    private static (double X, double Y) ResolveWatermarkOrigin(
        uint canvasWidth,
        uint canvasHeight,
        int overlayWidth,
        int overlayHeight,
        int margin,
        DesktopWatermarkPlacement placement)
    {
        var maxX = Math.Max(0, canvasWidth - overlayWidth - margin);
        var maxY = Math.Max(0, canvasHeight - overlayHeight - margin);

        return placement switch
        {
            DesktopWatermarkPlacement.TopLeft => (margin, margin),
            DesktopWatermarkPlacement.TopRight => (maxX, margin),
            DesktopWatermarkPlacement.BottomLeft => (margin, maxY),
            DesktopWatermarkPlacement.Center => (
                Math.Max(0, (canvasWidth - overlayWidth) / 2.0),
                Math.Max(0, (canvasHeight - overlayHeight) / 2.0)),
            _ => (maxX, maxY)
        };
    }

    private static int EstimateTextWidth(string text, int pointSize)
    {
        var width = 0.0;
        foreach (var character in text)
        {
            width += character > 127 ? pointSize : pointSize * 0.58;
        }

        return (int)Math.Ceiling(width);
    }

    private static MagickColor CreateWatermarkColor(string colorText, int opacityPercent)
    {
        var color = string.IsNullOrWhiteSpace(colorText) ? "#FFFFFF" : colorText.Trim();
        if (!color.StartsWith('#'))
        {
            color = $"#{color}";
        }

        if (color.Length != 7)
        {
            color = "#FFFFFF";
        }

        var alpha = (byte)Math.Round(Math.Clamp(opacityPercent, 1, 100) / 100.0 * 255);
        return new MagickColor($"{color}{alpha:X2}");
    }

    private static MagickColor CreateWatermarkStrokeColor(int opacityPercent)
    {
        var alpha = (byte)Math.Round(Math.Clamp(opacityPercent, 1, 100) / 100.0 * 140);
        return new MagickColor($"#000000{alpha:X2}");
    }

    private static void WriteImage(IMagickImage<byte> image, Stream stream, DesktopExportRequest request, ExportImageFormat outputFormat)
    {
        using var output = image.Clone();
        output.Format = ToMagickFormat(outputFormat);

        if (UsesQualitySetting(outputFormat))
        {
            output.Quality = (uint)Math.Clamp(request.JpegQuality, 1, 100);
        }

        if (RequiresOpaqueBackground(outputFormat))
        {
            EnsureOpaqueBackgroundForJpeg(output);
        }

        output.Write(stream);
    }

    private static ExportImageFormat? TryGetSourceFormat(string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        return extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => ExportImageFormat.Jpeg,
            ".png" => ExportImageFormat.Png,
            ".webp" => ExportImageFormat.WebP,
            ".avif" => ExportImageFormat.Avif,
            ".tif" or ".tiff" => ExportImageFormat.Tiff,
            ".bmp" => ExportImageFormat.Bmp,
            ".jxl" => ExportImageFormat.Jxl,
            ".heic" or ".heif" => ExportImageFormat.Heic,
            _ => null
        };
    }

    private static bool UsesQualitySetting(ExportImageFormat outputFormat)
    {
        return outputFormat is ExportImageFormat.Jpeg
            or ExportImageFormat.WebP
            or ExportImageFormat.Avif
            or ExportImageFormat.Jxl
            or ExportImageFormat.Heic;
    }

    private static bool RequiresOpaqueBackground(ExportImageFormat outputFormat)
    {
        return outputFormat is ExportImageFormat.Jpeg or ExportImageFormat.Bmp;
    }

    private static MagickFormat ToMagickFormat(ExportImageFormat outputFormat)
    {
        return outputFormat switch
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
    }

    private static ExportImageFormat ResolveOutputFormat(string sourcePath, DesktopExportRequest request)
    {
        if (request.Format != ExportImageFormat.Original)
        {
            return request.Format;
        }

        var sourceFormat = TryGetSourceFormat(sourcePath);
        if (sourceFormat is not null)
        {
            return sourceFormat.Value;
        }

        if (request.FallbackFormat != ExportImageFormat.Original)
        {
            return request.FallbackFormat;
        }

        return TryGetSourceFormat(sourcePath)
            ?? throw new InvalidOperationException("Current image format is not supported for original-format export.");
    }

    private static string ResolveTargetExtension(
        string sourcePath,
        DesktopExportRequest request,
        ExportImageFormat? outputFormat = null)
    {
        if (request.Format == ExportImageFormat.Original)
        {
            var sourceExtension = Path.GetExtension(sourcePath);
            if (string.IsNullOrWhiteSpace(sourceExtension))
            {
                throw new InvalidOperationException("Current file does not have a usable extension.");
            }

            var sourceFormat = TryGetSourceFormat(sourcePath);
            if (sourceFormat is not null || CanCopyWithoutReencode(request))
            {
                return sourceExtension.ToLowerInvariant();
            }
        }

        return outputFormat switch
        {
            ExportImageFormat.Jpeg => ".jpg",
            ExportImageFormat.Png => ".png",
            ExportImageFormat.WebP => ".webp",
            ExportImageFormat.Avif => ".avif",
            ExportImageFormat.Tiff => ".tif",
            ExportImageFormat.Bmp => ".bmp",
            ExportImageFormat.Jxl => ".jxl",
            ExportImageFormat.Heic => ".heic",
            _ => throw new InvalidOperationException("Unsupported export format.")
        };
    }

    internal static bool CanSatisfyTargetSizeByCopyingSource(
        string sourcePath,
        DesktopExportRequest request,
        ExportImageFormat outputFormat,
        DesktopImageSourceInfo sourceInfo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        if (!sourceInfo.Exists
            || sourceInfo.SizeBytes <= 0
            || request.TargetFileSizeBytes is not > 0
            || sourceInfo.SizeBytes > request.TargetFileSizeBytes.Value
            || request.StripMetadata
            || request.Watermark is not null
            || request.LongEdgePixels is > 0
            || request.PreserveEncodedData)
        {
            return false;
        }

        if (request.Format == ExportImageFormat.Original)
        {
            return true;
        }

        var sourceFormat = TryGetSourceFormat(sourcePath);
        return sourceFormat is not null && sourceFormat.Value == outputFormat;
    }

    private static bool CanStripMetadataWithoutReencode(string sourcePath, DesktopExportRequest request, ExportImageFormat outputFormat)
    {
        if (!request.StripMetadata
            || !request.PreserveEncodedData
            || request.Watermark is not null
            || request.LongEdgePixels is > 0
            || request.TargetFileSizeBytes is > 0
            || request.Format != ExportImageFormat.Original)
        {
            return false;
        }

        var sourceFormat = TryGetSourceFormat(sourcePath);
        if (sourceFormat is null || sourceFormat != outputFormat)
        {
            return false;
        }

        return outputFormat switch
        {
            ExportImageFormat.Jpeg => HasNeutralOrientation(sourcePath),
            ExportImageFormat.Png => true,
            _ => false
        };
    }

    private static bool HasNeutralOrientation(string sourcePath)
    {
        try
        {
            return DesktopMagickOperationGate.Shared.Run(() =>
            {
                using var image = new MagickImage(sourcePath);
                return image.Orientation is OrientationType.TopLeft or OrientationType.Undefined;
            });
        }
        catch
        {
            return false;
        }
    }
}
