using Avalonia;
using Avalonia.Media.Imaging;
using ImageMagick;

namespace ModernImageViewer.Desktop.Services;

internal readonly record struct PreviewImageLoadPlan(
    int InitialLongEdgePixels,
    int FinalLongEdgePixels)
{
    public bool RequiresFollowUpReload => FinalLongEdgePixels > InitialLongEdgePixels;
}

public sealed class PreviewImageService
{
    private const int PreviewJpegQuality = 88;
    private const long LargePreviewSourceSizeBytes = 24L * 1024L * 1024L;
    private const long HugePreviewSourceSizeBytes = 48L * 1024L * 1024L;
    private const uint LargePreviewLongEdgePixels = 7000;
    private const uint HugePreviewLongEdgePixels = 12000;
    private const double ExtremePreviewAspectRatioThreshold = 2.8;
    private const double QuickPreviewAspectRatioThreshold = 4.2;
    private const long ThumbnailDecodePixelBudget = 1_000_000L;
    private const long PreviewDecodePixelBudget = 9_000_000L;
    private const long LargeSourcePreviewDecodePixelBudget = 7_840_000L;
    private const long HugePreviewPixelCountThreshold = 42_000_000L;
    private const int ThumbnailUnknownDimensionReadLongEdge = 256;
    private const int LargePreviewUnknownDimensionReadLongEdge = 2048;
    private const int QuickPreviewMinimumRequestedLongEdge = 2200;
    private const int QuickPreviewMinimumLongEdge = 1800;
    private const int QuickPreviewHugeLongImageMinimumLongEdge = 2200;
    private const int QuickPreviewLongImageMinimumLongEdge = 2600;
    private readonly PreviewImageCacheStore _cache;
    private readonly DesktopImageDimensionCacheStore _dimensionCache;

    private enum PreviewImageLoadMode
    {
        Thumbnail,
        Preview
    }

    private static readonly HashSet<string> CompatibilityFirstExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avif",
        ".arw",
        ".cr3",
        ".dng",
        ".heic",
        ".heif",
        ".jxl",
        ".nef",
        ".psd",
        ".svg"
    };

    public PreviewImageService()
        : this(new PreviewImageCacheStore(
            DesktopImageProcessingPolicy.PreviewCacheLimitBytes,
            new PreviewImageDiskCacheStore(
                DesktopImageProcessingPolicy.PreviewDiskCacheLimitBytes,
                DesktopImageProcessingPolicy.ThumbnailDiskCacheLimitBytes)),
            DesktopImageDimensionCacheStore.Shared)
    {
    }

    internal PreviewImageService(PreviewImageCacheStore cache)
        : this(cache, DesktopImageDimensionCacheStore.Shared)
    {
    }

    internal PreviewImageService(PreviewImageCacheStore cache, DesktopImageDimensionCacheStore dimensionCache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _dimensionCache = dimensionCache ?? throw new ArgumentNullException(nameof(dimensionCache));
    }

    public void RefreshProcessingPolicy()
    {
        RefreshProcessingPolicy(PreviewCacheMaintenanceMode.Immediate);
    }

    internal void RefreshProcessingPolicy(PreviewCacheMaintenanceMode maintenanceMode)
    {
        _cache.UpdateBudgets(
            DesktopImageProcessingPolicy.PreviewCacheLimitBytes,
            DesktopImageProcessingPolicy.PreviewDiskCacheLimitBytes,
            DesktopImageProcessingPolicy.ThumbnailDiskCacheLimitBytes,
            maintenanceMode);
        _dimensionCache.UpdateEntryLimit(DesktopImageProcessingPolicy.ImageDimensionCacheEntryLimit);
    }

    public PreviewImageLoadResult LoadThumbnail(string path, int maxLongEdgePixels)
    {
        return Load(path, maxLongEdgePixels, PreviewImageLoadMode.Thumbnail);
    }

    internal PreviewImageLoadResult LoadThumbnail(DesktopFileSignature signature, int maxLongEdgePixels)
    {
        return Load(signature, maxLongEdgePixels, PreviewImageLoadMode.Thumbnail);
    }

    public PreviewImageLoadResult LoadPreview(string path, int maxLongEdgePixels)
    {
        return Load(path, maxLongEdgePixels, PreviewImageLoadMode.Preview);
    }

    internal PreviewImageLoadResult LoadPreview(DesktopFileSignature signature, int maxLongEdgePixels)
    {
        return Load(signature, maxLongEdgePixels, PreviewImageLoadMode.Preview);
    }

    internal void WarmPreview(string path, int maxLongEdgePixels)
    {
        if (maxLongEdgePixels <= 0)
        {
            return;
        }

        var sourceInfo = DesktopImageSourceInfoReader.Read(path, _dimensionCache);
        if (!sourceInfo.Exists)
        {
            return;
        }

        var cacheKey = CreateCacheKey(sourceInfo, maxLongEdgePixels, PreviewImageLoadMode.Preview);
        _ = _cache.GetOrLoad(
            cacheKey,
            () => LoadCacheEntry(sourceInfo, maxLongEdgePixels, PreviewImageLoadMode.Preview));
    }

    internal void WarmPreview(DesktopFileSignature signature, int maxLongEdgePixels)
    {
        if (maxLongEdgePixels <= 0)
        {
            return;
        }

        var sourceInfo = DesktopImageSourceInfoReader.Read(signature, _dimensionCache);
        var cacheKey = CreateCacheKey(sourceInfo, maxLongEdgePixels, PreviewImageLoadMode.Preview);
        _ = _cache.GetOrLoad(
            cacheKey,
            () => LoadCacheEntry(sourceInfo, maxLongEdgePixels, PreviewImageLoadMode.Preview));
    }

    internal PreviewImageLoadPlan CreatePreviewLoadPlan(string path, int requestedLongEdgePixels)
    {
        var sourceInfo = DesktopImageSourceInfoReader.Read(path, _dimensionCache);
        return CreatePreviewLoadPlan(sourceInfo.Dimensions, sourceInfo.SizeBytes, requestedLongEdgePixels);
    }

    internal PreviewImageLoadPlan CreatePreviewLoadPlan(DesktopFileSignature signature, int requestedLongEdgePixels)
    {
        var sourceInfo = DesktopImageSourceInfoReader.Read(signature, _dimensionCache);
        return CreatePreviewLoadPlan(sourceInfo.Dimensions, sourceInfo.SizeBytes, requestedLongEdgePixels);
    }

    private PreviewImageLoadResult Load(string path, int maxLongEdgePixels, PreviewImageLoadMode loadMode)
    {
        var sourceInfo = DesktopImageSourceInfoReader.Read(path, _dimensionCache);
        return Load(sourceInfo, maxLongEdgePixels, loadMode);
    }

    private PreviewImageLoadResult Load(DesktopFileSignature signature, int maxLongEdgePixels, PreviewImageLoadMode loadMode)
    {
        var sourceInfo = DesktopImageSourceInfoReader.Read(signature, _dimensionCache);
        return Load(sourceInfo, maxLongEdgePixels, loadMode);
    }

    private PreviewImageLoadResult Load(
        DesktopImageSourceInfo sourceInfo,
        int maxLongEdgePixels,
        PreviewImageLoadMode loadMode)
    {
        if (!sourceInfo.Exists)
        {
            return new PreviewImageLoadResult(
                null,
                "文件不存在",
                "路径已失效，暂时无法加载预览。");
        }

        var cacheKey = CreateCacheKey(sourceInfo, maxLongEdgePixels, loadMode);
        var cacheEntry = _cache.GetOrLoad(
            cacheKey,
            () => LoadCacheEntry(sourceInfo, maxLongEdgePixels, loadMode));
        return CreateLoadResult(cacheEntry);
    }

    private static PreviewImageCacheEntry LoadCacheEntry(
        DesktopImageSourceInfo sourceInfo,
        int maxLongEdgePixels,
        PreviewImageLoadMode loadMode)
    {
        var preferCompatibilityDecoder = loadMode == PreviewImageLoadMode.Thumbnail;
        var extension = Path.GetExtension(sourceInfo.Path);
        var sourceSizeBytes = sourceInfo.SizeBytes;
        var dimensions = sourceInfo.Dimensions;
        var useCompatibilityFirst = ShouldPreferCompatibilityDecoder(
            extension,
            dimensions,
            sourceSizeBytes,
            maxLongEdgePixels,
            preferCompatibilityDecoder);

        var compatibilityResult = useCompatibilityFirst
            ? TryLoadWithMagick(sourceInfo.Path, maxLongEdgePixels, dimensions, sourceSizeBytes, loadMode)
            : null;
        if (compatibilityResult is not null)
        {
            return compatibilityResult;
        }

        var nativeResult = TryLoadWithAvalonia(sourceInfo.Path, maxLongEdgePixels, dimensions, sourceSizeBytes, loadMode);
        if (nativeResult is not null)
        {
            return nativeResult;
        }

        if (!useCompatibilityFirst)
        {
            compatibilityResult = TryLoadWithMagick(sourceInfo.Path, maxLongEdgePixels, dimensions, sourceSizeBytes, loadMode);
            if (compatibilityResult is not null)
            {
                return compatibilityResult;
            }
        }

        var extensionLabel = string.IsNullOrWhiteSpace(extension)
            ? "当前文件"
            : extension.TrimStart('.').ToUpperInvariant();
        return new PreviewImageCacheEntry(
            null,
            "暂时无法加载预览",
            $"{extensionLabel} 格式暂时没有可用的预览方式。",
            Cacheable: false);
    }

    private static PreviewImageCacheKey CreateCacheKey(
        DesktopImageSourceInfo sourceInfo,
        int maxLongEdgePixels,
        PreviewImageLoadMode loadMode)
    {
        var normalizedLongEdge = CalculateCacheDecodeLongEdge(
            Path.GetExtension(sourceInfo.Path),
            sourceInfo.Dimensions,
            sourceInfo.SizeBytes,
            maxLongEdgePixels,
            loadMode == PreviewImageLoadMode.Thumbnail);
        return new PreviewImageCacheKey(
            sourceInfo.Path,
            sourceInfo.SizeBytes,
            sourceInfo.SourceStampTicks,
            normalizedLongEdge,
            loadMode == PreviewImageLoadMode.Thumbnail);
    }

    internal static bool ShouldPreferCompatibilityDecoder(
        string? extension,
        DesktopImageDimensions? dimensions,
        long sourceSizeBytes,
        int maxLongEdgePixels,
        bool preferCompatibilityDecoder)
    {
        if (preferCompatibilityDecoder)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(extension) && CompatibilityFirstExtensions.Contains(extension))
        {
            return true;
        }

        if (sourceSizeBytes >= LargePreviewSourceSizeBytes)
        {
            return true;
        }

        if (dimensions is not { } imageSize)
        {
            return false;
        }

        var shortEdge = Math.Min(imageSize.Width, imageSize.Height);
        if (shortEdge > 0 && imageSize.LongEdge / (double)shortEdge >= ExtremePreviewAspectRatioThreshold)
        {
            return true;
        }

        var safetyThreshold = (uint)Math.Max(LargePreviewLongEdgePixels, Math.Max(1, maxLongEdgePixels) * 3);
        return imageSize.LongEdge >= safetyThreshold;
    }

    internal static PreviewImageLoadPlan CreatePreviewLoadPlan(
        DesktopImageDimensions? dimensions,
        long sourceSizeBytes,
        int requestedLongEdgePixels)
    {
        var normalizedRequestedLongEdge = Math.Max(0, requestedLongEdgePixels);
        if (!ShouldUseQuickPreviewPass(dimensions, sourceSizeBytes, normalizedRequestedLongEdge))
        {
            return new PreviewImageLoadPlan(normalizedRequestedLongEdge, normalizedRequestedLongEdge);
        }

        var quickLongEdge = CalculateQuickPreviewLongEdge(dimensions, sourceSizeBytes, normalizedRequestedLongEdge);
        return new PreviewImageLoadPlan(quickLongEdge, normalizedRequestedLongEdge);
    }

    internal static bool ShouldUseQuickPreviewPass(
        DesktopImageDimensions? dimensions,
        long sourceSizeBytes,
        int requestedLongEdgePixels)
    {
        if (requestedLongEdgePixels < QuickPreviewMinimumRequestedLongEdge)
        {
            return false;
        }

        if (sourceSizeBytes >= HugePreviewSourceSizeBytes)
        {
            return true;
        }

        if (dimensions is not { } imageSize)
        {
            return false;
        }

        var pixelCount = (long)imageSize.Width * imageSize.Height;
        var shortEdge = Math.Min(imageSize.Width, imageSize.Height);
        var aspectRatio = shortEdge == 0
            ? 0
            : imageSize.LongEdge / (double)shortEdge;

        return pixelCount >= HugePreviewPixelCountThreshold
            || imageSize.LongEdge >= HugePreviewLongEdgePixels
            || aspectRatio >= QuickPreviewAspectRatioThreshold;
    }

    internal static int CalculateQuickPreviewLongEdge(
        DesktopImageDimensions? dimensions,
        long sourceSizeBytes,
        int requestedLongEdgePixels)
    {
        var normalizedRequestedLongEdge = Math.Max(0, requestedLongEdgePixels);
        if (normalizedRequestedLongEdge <= QuickPreviewMinimumLongEdge)
        {
            return normalizedRequestedLongEdge;
        }

        var isLongImage = false;
        var isHugeImage = sourceSizeBytes >= HugePreviewSourceSizeBytes;
        if (dimensions is { } imageSize)
        {
            var shortEdge = Math.Min(imageSize.Width, imageSize.Height);
            var aspectRatio = shortEdge == 0
                ? 0
                : imageSize.LongEdge / (double)shortEdge;
            var pixelCount = (long)imageSize.Width * imageSize.Height;
            isLongImage = aspectRatio >= QuickPreviewAspectRatioThreshold;
            isHugeImage = isHugeImage
                || pixelCount >= HugePreviewPixelCountThreshold
                || imageSize.LongEdge >= HugePreviewLongEdgePixels;
        }

        var isHugeLongImage = isLongImage && sourceSizeBytes >= HugePreviewSourceSizeBytes;
        var scale = isLongImage
            ? isHugeLongImage ? 0.48 : 0.56
            : isHugeImage ? 0.55 : 0.65;
        var minimumLongEdge = isLongImage
            ? isHugeLongImage
                ? QuickPreviewHugeLongImageMinimumLongEdge
                : QuickPreviewLongImageMinimumLongEdge
            : QuickPreviewMinimumLongEdge;
        var quickLongEdge = (int)Math.Ceiling(normalizedRequestedLongEdge * scale);
        return Math.Clamp(quickLongEdge, minimumLongEdge, normalizedRequestedLongEdge - 1);
    }

    private static PreviewImageLoadResult CreateLoadResult(PreviewImageCacheEntry cacheEntry)
    {
        if (!cacheEntry.HasBitmap)
        {
            return new PreviewImageLoadResult(null, cacheEntry.StatusText, cacheEntry.DetailsText);
        }

        using var stream = new MemoryStream(cacheEntry.EncodedBytes!, writable: false);
        return new PreviewImageLoadResult(new Bitmap(stream), cacheEntry.StatusText, cacheEntry.DetailsText);
    }

    private static PreviewImageCacheEntry? TryLoadWithAvalonia(
        string path,
        int maxLongEdgePixels,
        DesktopImageDimensions? dimensions,
        long sourceSizeBytes,
        PreviewImageLoadMode loadMode)
    {
        try
        {
            var effectiveLongEdge = CalculateEffectiveDecodeLongEdge(
                dimensions,
                maxLongEdgePixels,
                sourceSizeBytes,
                loadMode == PreviewImageLoadMode.Thumbnail);
            using var stream = DesktopFileStreamFactory.OpenReadShared(path);
            using var bitmap = DecodeAvaloniaBitmap(stream, dimensions, effectiveLongEdge);
            var bitmapBytes = EncodeScaledBitmap(bitmap, effectiveLongEdge);

            return new PreviewImageCacheEntry(
                bitmapBytes,
                "预览已就绪",
                loadMode == PreviewImageLoadMode.Thumbnail
                    ? "当前缩略图已成功载入。"
                    : "当前预览已成功载入。");
        }
        catch
        {
            return null;
        }
    }

    private static PreviewImageCacheEntry? TryLoadWithMagick(
        string path,
        int maxLongEdgePixels,
        DesktopImageDimensions? dimensions,
        long sourceSizeBytes,
        PreviewImageLoadMode loadMode)
    {
        try
        {
            return DesktopMagickOperationGate.Shared.Run(() =>
            {
                var effectiveLongEdge = CalculateEffectiveDecodeLongEdge(
                    dimensions,
                    maxLongEdgePixels,
                    sourceSizeBytes,
                    loadMode == PreviewImageLoadMode.Thumbnail);
                var readSettings = CreateMagickReadSettings(dimensions, maxLongEdgePixels, sourceSizeBytes, loadMode);
                using var image = new MagickImage(path, readSettings);
                image.AutoOrient();
                ResizeMagickImageToLongEdge(image, effectiveLongEdge);
                var encodedBytes = EncodeMagickPreviewBytesForCache(image);

                return new PreviewImageCacheEntry(
                    encodedBytes,
                    "预览已就绪",
                    loadMode == PreviewImageLoadMode.Thumbnail
                        ? "当前缩略图已成功载入。"
                        : "当前预览已成功载入。");
            });
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap DecodeAvaloniaBitmap(Stream stream, DesktopImageDimensions? dimensions, int maxLongEdgePixels)
    {
        if (maxLongEdgePixels <= 0 || dimensions is not { } imageSize)
        {
            return new Bitmap(stream);
        }

        var targetLongEdge = (uint)Math.Max(1, maxLongEdgePixels);
        if (imageSize.LongEdge <= targetLongEdge)
        {
            return new Bitmap(stream);
        }

        return imageSize.Width >= imageSize.Height
            ? Bitmap.DecodeToWidth(stream, maxLongEdgePixels, BitmapInterpolationMode.HighQuality)
            : Bitmap.DecodeToHeight(stream, maxLongEdgePixels, BitmapInterpolationMode.HighQuality);
    }

    internal static MagickReadSettings CreateMagickReadSettings(
        DesktopImageDimensions? dimensions,
        int maxLongEdgePixels,
        long sourceSizeBytes,
        bool isThumbnail)
    {
        return CreateMagickReadSettings(
            dimensions,
            maxLongEdgePixels,
            sourceSizeBytes,
            isThumbnail ? PreviewImageLoadMode.Thumbnail : PreviewImageLoadMode.Preview);
    }

    private static MagickReadSettings CreateMagickReadSettings(
        DesktopImageDimensions? dimensions,
        int maxLongEdgePixels,
        long sourceSizeBytes,
        PreviewImageLoadMode loadMode)
    {
        var settings = new MagickReadSettings();
        var effectiveLongEdge = CalculateEffectiveDecodeLongEdge(
            dimensions,
            maxLongEdgePixels,
            sourceSizeBytes,
            loadMode == PreviewImageLoadMode.Thumbnail);

        if (effectiveLongEdge <= 0)
        {
            return settings;
        }

        if (dimensions is { } imageSize)
        {
            if (imageSize.LongEdge <= effectiveLongEdge)
            {
                return settings;
            }

            var scale = effectiveLongEdge / (double)imageSize.LongEdge;
            settings.Width = (uint)Math.Max(1, (int)Math.Round(imageSize.Width * scale));
            settings.Height = (uint)Math.Max(1, (int)Math.Round(imageSize.Height * scale));
            return settings;
        }

        var fallbackReadLongEdge = CalculateUnknownDimensionReadLongEdge(
            effectiveLongEdge,
            sourceSizeBytes,
            loadMode == PreviewImageLoadMode.Thumbnail);
        if (fallbackReadLongEdge > 0)
        {
            settings.Width = (uint)fallbackReadLongEdge;
            settings.Height = (uint)fallbackReadLongEdge;
        }

        return settings;
    }

    internal static int CalculateEffectiveDecodeLongEdge(
        DesktopImageDimensions? dimensions,
        int maxLongEdgePixels,
        long sourceSizeBytes,
        bool isThumbnail)
    {
        var normalizedLongEdge = Math.Max(0, maxLongEdgePixels);
        if (normalizedLongEdge <= 0 || dimensions is not { } imageSize || imageSize.LongEdge == 0)
        {
            return normalizedLongEdge;
        }

        var effectiveLongEdge = Math.Min(normalizedLongEdge, (int)Math.Min(int.MaxValue, imageSize.LongEdge));
        var pixelBudget = ResolveDecodePixelBudget(sourceSizeBytes, isThumbnail);
        if (pixelBudget <= 0)
        {
            return effectiveLongEdge;
        }

        if (CalculateScaledPixelCount(imageSize, effectiveLongEdge) <= pixelBudget)
        {
            return effectiveLongEdge;
        }

        var sourcePixelCount = (double)imageSize.Width * imageSize.Height;
        if (sourcePixelCount <= 0)
        {
            return effectiveLongEdge;
        }

        var scale = Math.Sqrt(pixelBudget / sourcePixelCount);
        var budgetedLongEdge = (int)Math.Floor(imageSize.LongEdge * scale);
        return Math.Clamp(budgetedLongEdge, 1, effectiveLongEdge);
    }

    internal static int CalculateUnknownDimensionReadLongEdge(
        int maxLongEdgePixels,
        long sourceSizeBytes,
        bool isThumbnail)
    {
        if (maxLongEdgePixels <= 0)
        {
            return 0;
        }

        if (isThumbnail)
        {
            return Math.Clamp(maxLongEdgePixels, 1, ThumbnailUnknownDimensionReadLongEdge);
        }

        if (sourceSizeBytes < LargePreviewSourceSizeBytes)
        {
            return 0;
        }

        return Math.Clamp(maxLongEdgePixels, 1, LargePreviewUnknownDimensionReadLongEdge);
    }

    private static long ResolveDecodePixelBudget(long sourceSizeBytes, bool isThumbnail)
    {
        if (isThumbnail)
        {
            return ThumbnailDecodePixelBudget;
        }

        return sourceSizeBytes >= LargePreviewSourceSizeBytes
            ? LargeSourcePreviewDecodePixelBudget
            : PreviewDecodePixelBudget;
    }

    internal static int CalculateCacheDecodeLongEdge(
        string? extension,
        DesktopImageDimensions? dimensions,
        long sourceSizeBytes,
        int maxLongEdgePixels,
        bool preferCompatibilityDecoder)
    {
        var normalizedLongEdge = Math.Max(0, maxLongEdgePixels);
        if (normalizedLongEdge <= 0)
        {
            return 0;
        }

        var effectiveLongEdge = CalculateEffectiveDecodeLongEdge(
            dimensions,
            normalizedLongEdge,
            sourceSizeBytes,
            preferCompatibilityDecoder);
        if (dimensions is not null)
        {
            return effectiveLongEdge;
        }

        if (!ShouldPreferCompatibilityDecoder(
                extension,
                dimensions,
                sourceSizeBytes,
                normalizedLongEdge,
                preferCompatibilityDecoder))
        {
            return effectiveLongEdge;
        }

        var fallbackReadLongEdge = CalculateUnknownDimensionReadLongEdge(
            normalizedLongEdge,
            sourceSizeBytes,
            preferCompatibilityDecoder);
        return fallbackReadLongEdge > 0
            ? Math.Min(effectiveLongEdge, fallbackReadLongEdge)
            : effectiveLongEdge;
    }

    private static long CalculateScaledPixelCount(DesktopImageDimensions dimensions, int longEdgePixels)
    {
        if (longEdgePixels <= 0 || dimensions.LongEdge == 0)
        {
            return 0;
        }

        var scale = longEdgePixels / (double)dimensions.LongEdge;
        return (long)Math.Ceiling(dimensions.Width * scale * dimensions.Height * scale);
    }

    private static void ResizeMagickImageToLongEdge(MagickImage image, int maxLongEdgePixels)
    {
        var longEdge = Math.Max(image.Width, image.Height);
        if (maxLongEdgePixels <= 0 || longEdge <= maxLongEdgePixels)
        {
            return;
        }

        var scale = maxLongEdgePixels / (double)longEdge;
        var targetWidth = (uint)Math.Max(1, (int)Math.Round(image.Width * scale));
        var targetHeight = (uint)Math.Max(1, (int)Math.Round(image.Height * scale));
        image.Resize(targetWidth, targetHeight);
    }

    private static byte[] EncodeScaledBitmap(Bitmap source, int maxLongEdgePixels)
    {
        if (maxLongEdgePixels <= 0)
        {
            return EncodeBitmap(source);
        }

        var pixelSize = source.PixelSize;
        var longEdge = Math.Max(pixelSize.Width, pixelSize.Height);
        if (longEdge <= maxLongEdgePixels)
        {
            return EncodeBitmap(source);
        }

        var scale = maxLongEdgePixels / (double)longEdge;
        var targetSize = new PixelSize(
            Math.Max(1, (int)Math.Round(pixelSize.Width * scale)),
            Math.Max(1, (int)Math.Round(pixelSize.Height * scale)));
        using var scaled = source.CreateScaledBitmap(targetSize, BitmapInterpolationMode.HighQuality);
        return EncodeBitmap(scaled);
    }

    private static byte[] EncodeBitmap(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return stream.ToArray();
    }

    internal static byte[] EncodeMagickPreviewBytesForCache(IMagickImage<byte> image)
    {
        ArgumentNullException.ThrowIfNull(image);

        image.Strip();
        if (ShouldEncodePreviewAsJpeg(image))
        {
            image.Format = MagickFormat.Jpeg;
            image.Quality = PreviewJpegQuality;
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaOption.Remove);
        }
        else
        {
            image.Format = MagickFormat.Png;
        }

        using var stream = new MemoryStream();
        image.Write(stream);
        return stream.ToArray();
    }

    private static bool ShouldEncodePreviewAsJpeg(IMagickImage<byte> image)
    {
        return !image.HasAlpha;
    }
}

