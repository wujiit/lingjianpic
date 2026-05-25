using ImageMagick;
using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Tests;

public sealed class PreviewImageServiceTests
{
    [Theory]
    [InlineData(".jpg", 2L * 1024 * 1024, 1800, 1200, 2200, false, false)]
    [InlineData(".psd", 2L * 1024 * 1024, 1800, 1200, 2200, false, true)]
    [InlineData(".jpg", 32L * 1024 * 1024, 1800, 1200, 2200, false, true)]
    [InlineData(".jpg", 2L * 1024 * 1024, 1200, 4200, 2200, false, true)]
    [InlineData(".jpg", 2L * 1024 * 1024, 9000, 6000, 2200, false, true)]
    [InlineData(".jpg", 2L * 1024 * 1024, 1800, 1200, 2200, true, true)]
    public void ShouldPreferCompatibilityDecoder_matches_large_preview_guard_rules(
        string extension,
        long sourceSizeBytes,
        uint width,
        uint height,
        int maxLongEdgePixels,
        bool preferCompatibilityDecoder,
        bool expected)
    {
        var actual = PreviewImageService.ShouldPreferCompatibilityDecoder(
            extension,
            new DesktopImageDimensions(width, height),
            sourceSizeBytes,
            maxLongEdgePixels,
            preferCompatibilityDecoder);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ShouldPreferCompatibilityDecoder_returns_false_when_dimensions_are_missing_and_file_is_small()
    {
        var actual = PreviewImageService.ShouldPreferCompatibilityDecoder(
            ".jpg",
            dimensions: null,
            sourceSizeBytes: 2L * 1024 * 1024,
            maxLongEdgePixels: 2200,
            preferCompatibilityDecoder: false);

        Assert.False(actual);
    }

    [Fact]
    public void ShouldPreferCompatibilityDecoder_returns_true_when_dimensions_are_missing_but_file_is_large()
    {
        var actual = PreviewImageService.ShouldPreferCompatibilityDecoder(
            ".jpg",
            dimensions: null,
            sourceSizeBytes: 36L * 1024 * 1024,
            maxLongEdgePixels: 2200,
            preferCompatibilityDecoder: false);

        Assert.True(actual);
    }

    [Fact]
    public void CalculateUnknownDimensionReadLongEdge_uses_thumbnail_budget_for_thumbnails()
    {
        var actual = PreviewImageService.CalculateUnknownDimensionReadLongEdge(
            maxLongEdgePixels: 160,
            sourceSizeBytes: 2L * 1024 * 1024,
            isThumbnail: true);

        Assert.Equal(160, actual);
    }

    [Fact]
    public void CalculateUnknownDimensionReadLongEdge_caps_large_thumbnail_budget()
    {
        var actual = PreviewImageService.CalculateUnknownDimensionReadLongEdge(
            maxLongEdgePixels: 640,
            sourceSizeBytes: 40L * 1024 * 1024,
            isThumbnail: true);

        Assert.Equal(256, actual);
    }

    [Fact]
    public void CalculateUnknownDimensionReadLongEdge_returns_zero_for_small_preview_without_dimensions()
    {
        var actual = PreviewImageService.CalculateUnknownDimensionReadLongEdge(
            maxLongEdgePixels: 1600,
            sourceSizeBytes: 2L * 1024 * 1024,
            isThumbnail: false);

        Assert.Equal(0, actual);
    }

    [Fact]
    public void CalculateUnknownDimensionReadLongEdge_uses_preview_budget_for_large_unknown_preview()
    {
        var actual = PreviewImageService.CalculateUnknownDimensionReadLongEdge(
            maxLongEdgePixels: 2400,
            sourceSizeBytes: 40L * 1024 * 1024,
            isThumbnail: false);

        Assert.Equal(2048, actual);
    }

    [Fact]
    public void CalculateCacheDecodeLongEdge_reuses_effective_budget_for_large_square_preview()
    {
        var first = PreviewImageService.CalculateCacheDecodeLongEdge(
            ".jpg",
            new DesktopImageDimensions(8000, 8000),
            sourceSizeBytes: 40L * 1024 * 1024,
            maxLongEdgePixels: 3200,
            preferCompatibilityDecoder: false);
        var second = PreviewImageService.CalculateCacheDecodeLongEdge(
            ".jpg",
            new DesktopImageDimensions(8000, 8000),
            sourceSizeBytes: 40L * 1024 * 1024,
            maxLongEdgePixels: 6400,
            preferCompatibilityDecoder: false);

        Assert.Equal(2800, first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void CalculateCacheDecodeLongEdge_uses_unknown_dimension_fallback_for_large_compatibility_preview()
    {
        var actual = PreviewImageService.CalculateCacheDecodeLongEdge(
            ".psd",
            dimensions: null,
            sourceSizeBytes: 40L * 1024 * 1024,
            maxLongEdgePixels: 5000,
            preferCompatibilityDecoder: false);

        Assert.Equal(2048, actual);
    }

    [Fact]
    public void CalculateEffectiveDecodeLongEdge_reduces_large_square_preview_for_large_source()
    {
        var actual = PreviewImageService.CalculateEffectiveDecodeLongEdge(
            new DesktopImageDimensions(8000, 8000),
            maxLongEdgePixels: 3200,
            sourceSizeBytes: 40L * 1024 * 1024,
            isThumbnail: false);

        Assert.Equal(2800, actual);
    }

    [Fact]
    public void CalculateEffectiveDecodeLongEdge_preserves_extreme_long_image_budget()
    {
        var actual = PreviewImageService.CalculateEffectiveDecodeLongEdge(
            new DesktopImageDimensions(1000, 9000),
            maxLongEdgePixels: 6400,
            sourceSizeBytes: 40L * 1024 * 1024,
            isThumbnail: false);

        Assert.Equal(6400, actual);
    }

    [Fact]
    public void CalculateEffectiveDecodeLongEdge_reduces_small_preview_budget_for_large_square_preview()
    {
        var actual = PreviewImageService.CalculateEffectiveDecodeLongEdge(
            new DesktopImageDimensions(8000, 8000),
            maxLongEdgePixels: 3200,
            sourceSizeBytes: 4L * 1024 * 1024,
            isThumbnail: false);

        Assert.Equal(3000, actual);
    }

    [Fact]
    public void CreatePreviewLoadPlan_uses_quick_first_pass_for_huge_square_image()
    {
        var actual = PreviewImageService.CreatePreviewLoadPlan(
            new DesktopImageDimensions(9000, 9000),
            sourceSizeBytes: 60L * 1024 * 1024,
            requestedLongEdgePixels: 3200);

        Assert.True(actual.RequiresFollowUpReload);
        Assert.Equal(1800, actual.InitialLongEdgePixels);
        Assert.Equal(3200, actual.FinalLongEdgePixels);
    }

    [Fact]
    public void CreatePreviewLoadPlan_uses_quick_first_pass_for_extreme_long_image()
    {
        var actual = PreviewImageService.CreatePreviewLoadPlan(
            new DesktopImageDimensions(1200, 12000),
            sourceSizeBytes: 18L * 1024 * 1024,
            requestedLongEdgePixels: 4200);

        Assert.True(actual.RequiresFollowUpReload);
        Assert.Equal(2600, actual.InitialLongEdgePixels);
        Assert.Equal(4200, actual.FinalLongEdgePixels);
    }

    [Fact]
    public void CreatePreviewLoadPlan_uses_more_aggressive_first_pass_for_huge_long_image()
    {
        var actual = PreviewImageService.CreatePreviewLoadPlan(
            new DesktopImageDimensions(1200, 18000),
            sourceSizeBytes: 64L * 1024 * 1024,
            requestedLongEdgePixels: 4200);

        Assert.True(actual.RequiresFollowUpReload);
        Assert.Equal(2200, actual.InitialLongEdgePixels);
        Assert.Equal(4200, actual.FinalLongEdgePixels);
    }

    [Fact]
    public void CreatePreviewLoadPlan_returns_single_pass_for_normal_preview()
    {
        var actual = PreviewImageService.CreatePreviewLoadPlan(
            new DesktopImageDimensions(2400, 1600),
            sourceSizeBytes: 4L * 1024 * 1024,
            requestedLongEdgePixels: 1800);

        Assert.False(actual.RequiresFollowUpReload);
        Assert.Equal(1800, actual.InitialLongEdgePixels);
        Assert.Equal(1800, actual.FinalLongEdgePixels);
    }

    [Fact]
    public void CreatePreviewLoadPlan_signature_overload_matches_path_based_result()
    {
        using var paths = TestPaths.Create();
        var imagePath = paths.Combine("signature-source.png");

        using (var image = new MagickImage(MagickColors.White, 9000, 9000))
        {
            image.Format = MagickFormat.Png;
            image.Write(imagePath);
        }

        File.SetLastWriteTimeUtc(imagePath, new DateTime(2026, 4, 22, 8, 0, 0, DateTimeKind.Utc));
        Assert.True(DesktopFileSignatureReader.TryRead(imagePath, out var signature));

        var service = new PreviewImageService(
            new PreviewImageCacheStore(16L * 1024L * 1024L),
            new DesktopImageDimensionCacheStore(maxEntries: 8));

        var fromPath = service.CreatePreviewLoadPlan(imagePath, 3200);
        var fromSignature = service.CreatePreviewLoadPlan(signature, 3200);

        Assert.Equal(fromPath, fromSignature);
    }

    [Fact]
    public void CreateMagickReadSettings_uses_square_budget_when_dimensions_are_missing_for_thumbnail()
    {
        var settings = PreviewImageService.CreateMagickReadSettings(
            dimensions: null,
            maxLongEdgePixels: 160,
            sourceSizeBytes: 8L * 1024 * 1024,
            isThumbnail: true);

        Assert.Equal(160U, settings.Width);
        Assert.Equal(160U, settings.Height);
    }

    [Fact]
    public void CreateMagickReadSettings_scales_known_dimensions_to_requested_long_edge()
    {
        var settings = PreviewImageService.CreateMagickReadSettings(
            new DesktopImageDimensions(4000, 2000),
            maxLongEdgePixels: 1000,
            sourceSizeBytes: 16L * 1024 * 1024,
            isThumbnail: false);

        Assert.Equal(1000U, settings.Width);
        Assert.Equal(500U, settings.Height);
    }

    [Fact]
    public void CreateMagickReadSettings_uses_constrained_long_edge_for_large_square_preview()
    {
        var settings = PreviewImageService.CreateMagickReadSettings(
            new DesktopImageDimensions(8000, 8000),
            maxLongEdgePixels: 3200,
            sourceSizeBytes: 40L * 1024 * 1024,
            isThumbnail: false);

        Assert.Equal(2800U, settings.Width);
        Assert.Equal(2800U, settings.Height);
    }

    [Fact]
    public void EncodeMagickPreviewBytesForCache_uses_jpeg_for_opaque_image_and_strips_metadata()
    {
        using var image = new MagickImage(MagickColors.White, 120, 80);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Artist, "Preview Test");
        exif.Rewrite();
        image.SetProfile(exif);

        var encodedBytes = PreviewImageService.EncodeMagickPreviewBytesForCache(image);

        using var output = new MagickImage(encodedBytes);
        Assert.Equal(MagickFormat.Jpeg, output.Format);
        Assert.Null(output.GetExifProfile());
    }

    [Fact]
    public void EncodeMagickPreviewBytesForCache_keeps_png_for_transparent_image()
    {
        using var image = new MagickImage(MagickColors.Transparent, 64, 64);
        image.Alpha(AlphaOption.Set);

        var encodedBytes = PreviewImageService.EncodeMagickPreviewBytesForCache(image);

        using var output = new MagickImage(encodedBytes);
        Assert.Equal(MagickFormat.Png, output.Format);
    }
}
