using ImageMagick;
using ImageMagick.Drawing;
using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Tests;

public sealed class DesktopImageExportServiceTests
{
    [Fact]
    public void Export_original_without_transforms_copies_unsupported_format_bytes()
    {
        var service = new DesktopImageExportService();
        using var paths = TestPaths.Create();
        var sourcePath = paths.Combine("source.webp");
        var targetPath = paths.Combine("exported.webp");
        var bytes = new byte[] { 0x52, 0x49, 0x46, 0x46, 0x12, 0x34, 0x56, 0x78 };
        File.WriteAllBytes(sourcePath, bytes);

        service.Export(
            sourcePath,
            targetPath,
            new DesktopExportRequest(
                ExportImageFormat.Original,
                LongEdgePixels: null,
                JpegQuality: 92,
                StripMetadata: false));

        Assert.Equal(bytes, File.ReadAllBytes(targetPath));
    }

    [Fact]
    public void Export_target_size_original_under_budget_copies_source_without_reencoding()
    {
        var service = new DesktopImageExportService();
        using var paths = TestPaths.Create();
        var sourcePath = paths.Combine("source.webp");
        var targetPath = paths.Combine("compressed.webp");
        var bytes = new byte[] { 0x52, 0x49, 0x46, 0x46, 0x12, 0x34, 0x56, 0x78 };
        File.WriteAllBytes(sourcePath, bytes);

        service.Export(
            sourcePath,
            targetPath,
            new DesktopExportRequest(
                ExportImageFormat.Original,
                LongEdgePixels: null,
                JpegQuality: 92,
                StripMetadata: false,
                TargetFileSizeBytes: bytes.Length + 128L));

        Assert.Equal(bytes, File.ReadAllBytes(targetPath));
    }

    [Fact]
    public void PrepareOperation_original_without_transforms_uses_copy_source_mode()
    {
        var service = new DesktopImageExportService();
        using var paths = TestPaths.Create();
        var sourcePath = paths.Combine("source.webp");
        File.WriteAllBytes(sourcePath, [1, 2, 3, 4]);

        var operation = service.PrepareOperation(
            sourcePath,
            new DesktopExportRequest(
                ExportImageFormat.Original,
                LongEdgePixels: null,
                JpegQuality: 92,
                StripMetadata: false));

        Assert.Equal(DesktopPreparedExportMode.CopySource, operation.Mode);
        Assert.Equal(".webp", operation.TargetExtension);
    }

    [Fact]
    public void PrepareOperation_target_size_original_under_budget_uses_copy_mode()
    {
        var service = new DesktopImageExportService();
        using var paths = TestPaths.Create();
        var sourcePath = paths.Combine("source.webp");
        File.WriteAllBytes(sourcePath, new byte[1024]);

        var operation = service.PrepareOperation(
            sourcePath,
            new DesktopExportRequest(
                ExportImageFormat.Original,
                LongEdgePixels: null,
                JpegQuality: 92,
                StripMetadata: false,
                TargetFileSizeBytes: 2048));

        Assert.Equal(DesktopPreparedExportMode.TargetSizeCopy, operation.Mode);
        Assert.Equal(".webp", operation.TargetExtension);
    }

    [Fact]
    public void CanSatisfyTargetSizeByCopyingSource_returns_true_for_same_format_under_budget()
    {
        var sourceInfo = new DesktopImageSourceInfo(
            new DesktopFileSignature("source.jpg", 1024, 42),
            Dimensions: null,
            Exists: true);

        var actual = DesktopImageExportService.CanSatisfyTargetSizeByCopyingSource(
            "source.jpg",
            new DesktopExportRequest(
                ExportImageFormat.Jpeg,
                LongEdgePixels: null,
                JpegQuality: 82,
                StripMetadata: false,
                TargetFileSizeBytes: 2048),
            ExportImageFormat.Jpeg,
            sourceInfo);

        Assert.True(actual);
    }

    [Fact]
    public void CanSatisfyTargetSizeByCopyingSource_returns_false_when_transforms_are_requested()
    {
        var sourceInfo = new DesktopImageSourceInfo(
            new DesktopFileSignature("source.jpg", 1024, 42),
            Dimensions: null,
            Exists: true);

        var actual = DesktopImageExportService.CanSatisfyTargetSizeByCopyingSource(
            "source.jpg",
            new DesktopExportRequest(
                ExportImageFormat.Original,
                LongEdgePixels: 1600,
                JpegQuality: 82,
                StripMetadata: false,
                TargetFileSizeBytes: 2048),
            ExportImageFormat.Jpeg,
            sourceInfo);

        Assert.False(actual);
    }

    [Fact]
    public void GetFileExtension_uses_fallback_when_unsupported_original_requires_reencode()
    {
        var service = new DesktopImageExportService();

        var extension = service.GetFileExtension(
            "source.psd",
            new DesktopExportRequest(
                ExportImageFormat.Original,
                LongEdgePixels: 2048,
                JpegQuality: 92,
                StripMetadata: false,
                FallbackFormat: ExportImageFormat.Png));

        Assert.Equal(".png", extension);
    }

    [Theory]
    [InlineData(ExportImageFormat.Jpeg, ".jpg")]
    [InlineData(ExportImageFormat.Png, ".png")]
    [InlineData(ExportImageFormat.WebP, ".webp")]
    [InlineData(ExportImageFormat.Avif, ".avif")]
    [InlineData(ExportImageFormat.Tiff, ".tif")]
    [InlineData(ExportImageFormat.Bmp, ".bmp")]
    [InlineData(ExportImageFormat.Jxl, ".jxl")]
    [InlineData(ExportImageFormat.Heic, ".heic")]
    public void GetFileExtension_maps_explicit_output_formats(ExportImageFormat format, string expectedExtension)
    {
        var service = new DesktopImageExportService();

        var extension = service.GetFileExtension(
            "source.png",
            new DesktopExportRequest(
                format,
                LongEdgePixels: null,
                JpegQuality: 92,
                StripMetadata: false));

        Assert.Equal(expectedExtension, extension);
    }

    [Theory]
    [InlineData("source.webp", ".webp")]
    [InlineData("source.avif", ".avif")]
    [InlineData("source.tiff", ".tiff")]
    [InlineData("source.bmp", ".bmp")]
    [InlineData("source.jxl", ".jxl")]
    [InlineData("source.heif", ".heif")]
    public void GetFileExtension_keeps_supported_original_format_when_reencoding(string sourcePath, string expectedExtension)
    {
        var service = new DesktopImageExportService();

        var extension = service.GetFileExtension(
            sourcePath,
            new DesktopExportRequest(
                ExportImageFormat.Original,
                LongEdgePixels: 2048,
                JpegQuality: 92,
                StripMetadata: false,
                FallbackFormat: ExportImageFormat.Jpeg));

        Assert.Equal(expectedExtension, extension);
    }

    [Theory]
    [InlineData(ExportImageFormat.Jpeg, true)]
    [InlineData(ExportImageFormat.WebP, true)]
    [InlineData(ExportImageFormat.Avif, true)]
    [InlineData(ExportImageFormat.Jxl, true)]
    [InlineData(ExportImageFormat.Heic, true)]
    [InlineData(ExportImageFormat.Png, false)]
    [InlineData(ExportImageFormat.Tiff, false)]
    [InlineData(ExportImageFormat.Bmp, false)]
    [InlineData(ExportImageFormat.Original, false)]
    public void SupportsTargetSizeCompression_only_allows_quality_based_formats(ExportImageFormat format, bool expected)
    {
        Assert.Equal(expected, DesktopImageExportService.SupportsTargetSizeCompression(format));
    }

    [Fact]
    public void Export_target_size_webp_writes_webp_within_requested_budget()
    {
        var service = new DesktopImageExportService();
        using var paths = TestPaths.Create();
        var sourcePath = paths.Combine("source.png");
        var targetPath = paths.Combine("compressed.webp");
        const long targetBytes = 160 * 1024L;
        WritePatternImage(sourcePath, width: 640, height: 480);

        service.Export(
            sourcePath,
            targetPath,
            new DesktopExportRequest(
                ExportImageFormat.WebP,
                LongEdgePixels: null,
                JpegQuality: 82,
                StripMetadata: true,
                TargetFileSizeBytes: targetBytes));

        using var output = new MagickImage(targetPath);

        Assert.Equal(MagickFormat.WebP, output.Format);
        Assert.True(new FileInfo(targetPath).Length <= targetBytes);
    }

    [Fact]
    public void Export_target_size_jpeg_writes_valid_output_within_requested_budget()
    {
        var service = new DesktopImageExportService();
        using var paths = TestPaths.Create();
        var sourcePath = paths.Combine("source-large.png");
        var targetPath = paths.Combine("compressed.jpg");
        const long targetBytes = 96 * 1024L;
        WritePatternImage(sourcePath, width: 1800, height: 1200);

        service.Export(
            sourcePath,
            targetPath,
            new DesktopExportRequest(
                ExportImageFormat.Jpeg,
                LongEdgePixels: null,
                JpegQuality: 92,
                StripMetadata: true,
                TargetFileSizeBytes: targetBytes));

        using var output = new MagickImage(targetPath);

        Assert.Equal(MagickFormat.Jpeg, output.Format);
        Assert.True(new FileInfo(targetPath).Length <= targetBytes);
        Assert.True(output.Width > 0U);
    }

    [Fact]
    public void CreateTargetCompressionAttemptImage_resizes_from_base_without_mutating_source()
    {
        using var baseImage = new MagickImage(MagickColors.White, 2000, 1000);
        using var firstAttempt = DesktopImageExportService.CreateTargetCompressionAttemptImage(baseImage, 1000);
        using var secondAttempt = DesktopImageExportService.CreateTargetCompressionAttemptImage(baseImage, 800);

        Assert.Equal(2000U, baseImage.Width);
        Assert.Equal(1000U, baseImage.Height);
        Assert.Equal(1000U, firstAttempt.Width);
        Assert.Equal(500U, firstAttempt.Height);
        Assert.Equal(800U, secondAttempt.Width);
        Assert.Equal(400U, secondAttempt.Height);
    }

    [Fact]
    public void CalculateInitialTargetCompressionLongEdge_keeps_full_size_when_target_is_close_to_source()
    {
        var actual = DesktopImageExportService.CalculateInitialTargetCompressionLongEdge(
            sourceLongEdge: 3200,
            sourceBytes: 1_000_000,
            targetBytes: 800_000);

        Assert.Equal(3200, actual);
    }

    [Fact]
    public void CalculateInitialTargetCompressionLongEdge_shrinks_first_pass_when_target_is_much_smaller()
    {
        var actual = DesktopImageExportService.CalculateInitialTargetCompressionLongEdge(
            sourceLongEdge: 4000,
            sourceBytes: 4_000_000,
            targetBytes: 240_000);

        Assert.InRange(actual, 1800, 2600);
        Assert.True(actual < 4000);
    }

    [Fact]
    public void CalculateInitialTargetCompressionLongEdge_respects_minimum_long_edge()
    {
        var actual = DesktopImageExportService.CalculateInitialTargetCompressionLongEdge(
            sourceLongEdge: 1000,
            sourceBytes: 5_000_000,
            targetBytes: 20_000);

        Assert.Equal(640, actual);
    }

    [Fact]
    public void CalculateAdaptiveReadLongEdge_keeps_full_size_when_target_budget_is_close_to_source()
    {
        var actual = DesktopImageExportService.CalculateAdaptiveReadLongEdge(
            sourceLongEdge: 3200,
            requestedLongEdgePixels: null,
            sourceBytes: 1_000_000,
            targetBytes: 800_000);

        Assert.Equal(3200U, actual);
    }

    [Fact]
    public void CalculateAdaptiveReadLongEdge_uses_explicit_long_edge_when_present()
    {
        var actual = DesktopImageExportService.CalculateAdaptiveReadLongEdge(
            sourceLongEdge: 3600,
            requestedLongEdgePixels: 1800,
            sourceBytes: 5_000_000,
            targetBytes: 240_000);

        Assert.Equal(1800U, actual);
    }

    [Fact]
    public void CalculateAdaptiveReadLongEdge_prescales_large_decode_for_target_size_export()
    {
        var actual = DesktopImageExportService.CalculateAdaptiveReadLongEdge(
            sourceLongEdge: 4000,
            requestedLongEdgePixels: null,
            sourceBytes: 4_000_000,
            targetBytes: 240_000);

        Assert.InRange(actual, 2000U, 3200U);
        Assert.True(actual < 4000U);
    }

    [Fact]
    public void CreateExportReadSettings_uses_adaptive_target_size_long_edge_when_no_explicit_resize()
    {
        using var paths = TestPaths.Create();
        var sourcePath = paths.Combine("source-large.bmp");
        using (var image = new MagickImage(MagickColors.White, 4000, 2000))
        {
            image.Format = MagickFormat.Bmp;
            image.Write(sourcePath);
        }

        var service = new DesktopImageExportService();

        var settings = service.CreateExportReadSettings(
            sourcePath,
            new DesktopExportRequest(
                ExportImageFormat.Jpeg,
                LongEdgePixels: null,
                JpegQuality: 92,
                StripMetadata: true,
                TargetFileSizeBytes: 240_000));

        Assert.True(settings.Width.HasValue);
        Assert.True(settings.Height.HasValue);
        Assert.InRange(settings.Width!.Value, 2000U, 3200U);
        Assert.InRange(settings.Height!.Value, 1000U, 1600U);
    }

    [Fact]
    public void SearchBestQualityForTarget_returns_max_quality_when_it_already_fits()
    {
        var search = DesktopImageExportService.SearchBestQualityForTarget(
            targetBytes: 10_000,
            minimumQuality: 40,
            maximumQuality: 92,
            sizeProbe: static _ => 8_000,
            cancellationToken: CancellationToken.None);

        Assert.True(search.WithinTarget);
        Assert.Equal(92, search.SelectedQuality);
        Assert.Equal([92], search.ProbedQualities);
    }

    [Fact]
    public void SearchBestQualityForTarget_returns_min_quality_when_even_minimum_is_too_large()
    {
        var search = DesktopImageExportService.SearchBestQualityForTarget(
            targetBytes: 3_000,
            minimumQuality: 40,
            maximumQuality: 92,
            sizeProbe: quality => quality == 92 ? 12_000 : 4_000,
            cancellationToken: CancellationToken.None);

        Assert.False(search.WithinTarget);
        Assert.Equal(40, search.SelectedQuality);
        Assert.Equal([92, 40], search.ProbedQualities);
    }

    [Fact]
    public void SearchBestQualityForTarget_finds_highest_quality_within_budget_using_bounded_probes()
    {
        var search = DesktopImageExportService.SearchBestQualityForTarget(
            targetBytes: 5_000,
            minimumQuality: 40,
            maximumQuality: 90,
            sizeProbe: quality => quality * 100L,
            cancellationToken: CancellationToken.None);

        Assert.True(search.WithinTarget);
        Assert.Equal(50, search.SelectedQuality);
        Assert.Equal([90, 40, 50], search.ProbedQualities);
    }

    [Fact]
    public void EstimateTargetCompressionQuality_prefers_interpolated_quality_inside_range()
    {
        var actual = DesktopImageExportService.EstimateTargetCompressionQuality(
            minimumQuality: 40,
            minimumQualityBytes: 4_000,
            maximumQuality: 90,
            maximumQualityBytes: 9_000,
            targetBytes: 5_000);

        Assert.Equal(50, actual);
    }

    [Fact]
    public void EstimateTargetCompressionQuality_falls_back_to_midpoint_when_byte_span_is_invalid()
    {
        var actual = DesktopImageExportService.EstimateTargetCompressionQuality(
            minimumQuality: 40,
            minimumQualityBytes: 5_000,
            maximumQuality: 90,
            maximumQualityBytes: 5_000,
            targetBytes: 5_000);

        Assert.Equal(65, actual);
    }

    [Fact]
    public void Export_with_image_watermark_composites_overlay()
    {
        var service = new DesktopImageExportService();
        using var paths = TestPaths.Create();
        var sourcePath = paths.Combine("source.png");
        var watermarkPath = paths.Combine("watermark.png");
        var targetPath = paths.Combine("watermarked.png");

        using (var source = new MagickImage(MagickColors.White, 40, 40))
        {
            source.Write(sourcePath);
        }

        using (var watermark = new MagickImage(MagickColors.Black, 10, 10))
        {
            watermark.Write(watermarkPath);
        }

        service.Export(
            sourcePath,
            targetPath,
            new DesktopExportRequest(
                ExportImageFormat.Png,
                LongEdgePixels: null,
                JpegQuality: 92,
                StripMetadata: false,
                Watermark: new DesktopWatermarkRequest(
                    DesktopWatermarkKind.Image,
                    DesktopWatermarkPlacement.BottomRight,
                    string.Empty,
                    watermarkPath,
                    OpacityPercent: 100,
                    MarginPixels: 0,
                    TextPointSize: 42,
                    TextColor: "#FFFFFF",
                    ImageScalePercent: 25)));

        using var output = new MagickImage(targetPath);
        var color = output.GetPixels().GetPixel(35, 35)?.ToColor();

        Assert.NotNull(color);
        Assert.True(color.R < 64 && color.G < 64 && color.B < 64);
    }

    [Fact]
    public void Export_with_image_watermark_reuses_cached_template_and_variant_between_runs()
    {
        var service = new DesktopImageExportService();
        using var paths = TestPaths.Create();
        var sourcePath = paths.Combine("source.png");
        var watermarkPath = paths.Combine("watermark.png");
        var firstTargetPath = paths.Combine("watermarked-first.png");
        var secondTargetPath = paths.Combine("watermarked-second.png");

        using (var source = new MagickImage(MagickColors.White, 40, 40))
        {
            source.Write(sourcePath);
        }

        using (var watermark = new MagickImage(MagickColors.Black, 10, 10))
        {
            watermark.Write(watermarkPath);
        }

        var request = new DesktopExportRequest(
            ExportImageFormat.Png,
            LongEdgePixels: null,
            JpegQuality: 92,
            StripMetadata: false,
            Watermark: new DesktopWatermarkRequest(
                DesktopWatermarkKind.Image,
                DesktopWatermarkPlacement.BottomRight,
                string.Empty,
                watermarkPath,
                OpacityPercent: 100,
                MarginPixels: 0,
                TextPointSize: 42,
                TextColor: "#FFFFFF",
                ImageScalePercent: 25));

        service.Export(sourcePath, firstTargetPath, request);
        service.Export(sourcePath, secondTargetPath, request);
        var firstCacheSnapshot = service.GetWatermarkCacheEntryCounts();
        service.Export(sourcePath, paths.Combine("watermarked-third.png"), request);
        var secondCacheSnapshot = service.GetWatermarkCacheEntryCounts();

        Assert.Equal((1, 1), firstCacheSnapshot);
        Assert.Equal(firstCacheSnapshot, secondCacheSnapshot);
    }

    [Fact]
    public void Export_with_text_watermark_creates_output()
    {
        var service = new DesktopImageExportService();
        using var paths = TestPaths.Create();
        var sourcePath = paths.Combine("source.png");
        var targetPath = paths.Combine("text-watermarked.png");

        using (var source = new MagickImage(MagickColors.White, 80, 50))
        {
            source.Write(sourcePath);
        }

        service.Export(
            sourcePath,
            targetPath,
            new DesktopExportRequest(
                ExportImageFormat.Png,
                LongEdgePixels: null,
                JpegQuality: 92,
                StripMetadata: false,
                Watermark: new DesktopWatermarkRequest(
                    DesktopWatermarkKind.Text,
                    DesktopWatermarkPlacement.Center,
                    "TEST",
                    string.Empty,
                    OpacityPercent: 80,
                    MarginPixels: 0,
                    TextPointSize: 16,
                    TextColor: "#000000",
                    ImageScalePercent: 18)));

        using var output = new MagickImage(targetPath);

        Assert.Equal(80U, output.Width);
        Assert.Equal(50U, output.Height);
    }

    [Fact]
    public void Export_prepared_copy_operation_writes_source_bytes()
    {
        var service = new DesktopImageExportService();
        using var paths = TestPaths.Create();
        var sourcePath = paths.Combine("source.webp");
        var targetPath = paths.Combine("prepared.webp");
        var bytes = new byte[] { 3, 1, 4, 1, 5, 9 };
        File.WriteAllBytes(sourcePath, bytes);

        var operation = service.PrepareOperation(
            sourcePath,
            new DesktopExportRequest(
                ExportImageFormat.Original,
                LongEdgePixels: null,
                JpegQuality: 92,
                StripMetadata: false));

        service.Export(operation, targetPath);

        Assert.Equal(bytes, File.ReadAllBytes(targetPath));
    }

    private static void WritePatternImage(string path, int width, int height)
    {
        using var image = new MagickImage(MagickColors.White, (uint)width, (uint)height);
        var drawables = new Drawables();

        for (var y = 0; y < height; y += 16)
        {
            for (var x = 0; x < width; x += 16)
            {
                var color = new MagickColor(
                    (byte)((x * 17 + y * 3) % 256),
                    (byte)((x * 5 + y * 19) % 256),
                    (byte)((x * 11 + y * 7) % 256));
                drawables
                    .FillColor(color)
                    .Rectangle(x, y, Math.Min(width - 1, x + 15), Math.Min(height - 1, y + 15));
            }
        }

        drawables.Draw(image);
        image.Write(path);
    }
}
