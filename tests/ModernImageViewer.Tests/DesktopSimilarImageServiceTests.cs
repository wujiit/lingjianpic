using ImageMagick;
using ImageMagick.Drawing;
using ModernImageViewer.Core;
using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Tests;

public sealed class DesktopSimilarImageServiceTests
{
    [Fact]
    public void FindSimilarImages_groups_identical_images_without_matching_different_image()
    {
        using var paths = TestPaths.Create();
        var firstPath = paths.Combine("first.png");
        var secondPath = paths.Combine("second.png");
        var differentPath = paths.Combine("different.png");

        WritePatternImage(firstPath, accentColor: MagickColors.RoyalBlue);
        File.Copy(firstPath, secondPath);
        WriteStripedImage(differentPath);

        var records = new[]
        {
            CreateRecord(firstPath),
            CreateRecord(secondPath),
            CreateRecord(differentPath)
        };

        var result = new DesktopSimilarImageService().FindSimilarImages(
            records,
            distanceThreshold: 0,
            CancellationToken.None);

        var group = Assert.Single(result.Groups);
        Assert.Equal(2, group.Paths.Count);
        Assert.Contains(firstPath, group.Paths);
        Assert.Contains(secondPath, group.Paths);
        Assert.DoesNotContain(differentPath, group.Paths);
    }

    [Fact]
    public void FindSimilarImages_populates_binary_fingerprint_cache_for_exact_duplicates()
    {
        using var paths = TestPaths.Create();
        var firstPath = paths.Combine("first.png");
        var secondPath = paths.Combine("second.png");
        var differentPath = paths.Combine("different.png");

        WritePatternImage(firstPath, accentColor: MagickColors.RoyalBlue);
        File.Copy(firstPath, secondPath);
        WriteStripedImage(differentPath);

        var records = new[]
        {
            CreateRecord(firstPath),
            CreateRecord(secondPath),
            CreateRecord(differentPath)
        };

        var cache = new DesktopImageFingerprintCacheStore(stateStore: null, maxTextEntries: 16, maxDifferenceHashEntries: 16);
        var dimensionCache = new DesktopImageDimensionCacheStore(maxEntries: 16);
        var service = new DesktopSimilarImageService(cache, dimensionCache);

        _ = service.FindSimilarImages(records, distanceThreshold: 0, CancellationToken.None);
        var counts = cache.GetEntryCounts();

        Assert.True(counts.TextEntryCount >= 2);
        Assert.Equal(3, counts.DifferenceHashEntryCount);
    }

    [Fact]
    public void ShouldUseExactContentPrefilter_returns_false_for_small_input()
    {
        using var paths = TestPaths.Create();
        var firstPath = paths.Combine("first.png");
        var secondPath = paths.Combine("second.png");

        WritePatternImage(firstPath, accentColor: MagickColors.RoyalBlue);
        File.Copy(firstPath, secondPath);

        var records = new[]
        {
            CreateRecord(firstPath),
            CreateRecord(secondPath)
        };

        Assert.False(DesktopSimilarImageService.ShouldUseExactContentPrefilter(records));
    }

    [Fact]
    public void ShouldUseExactContentPrefilter_returns_true_when_duplicate_size_candidates_exist()
    {
        using var paths = TestPaths.Create();
        var firstPath = paths.Combine("first.png");
        var secondPath = paths.Combine("second.png");
        var thirdPath = paths.Combine("third.png");

        WritePatternImage(firstPath, accentColor: MagickColors.RoyalBlue);
        File.Copy(firstPath, secondPath);
        WriteStripedImage(thirdPath);

        var records = new[]
        {
            CreateRecord(firstPath),
            CreateRecord(secondPath),
            CreateRecord(thirdPath)
        };

        Assert.True(DesktopSimilarImageService.ShouldUseExactContentPrefilter(records));
    }

    [Fact]
    public void CalculateHashParallelism_reduces_for_huge_input_batches()
    {
        var actual = DesktopSimilarImageService.CalculateHashParallelism(
            totalCount: 8,
            totalInputBytes: 8L * 120L * 1024L * 1024L);

        Assert.Equal(1, actual);
    }

    [Fact]
    public void CreateHashExecutionPlan_matches_similar_hash_execution_profile()
    {
        var plan = DesktopSimilarImageService.CreateHashExecutionPlan(
            totalCount: 18,
            totalInputBytes: 18L * 10L * 1024L * 1024L);

        Assert.Equal(2, plan.MaxDegreeOfParallelism);
        Assert.Equal(6, plan.ProgressInterval);
        Assert.Equal(3, plan.YieldInterval);
        Assert.Equal(3, plan.MemoryTrimInterval);
    }

    [Fact]
    public void CalculateHashDecodeLongEdge_reduces_large_square_image_budget()
    {
        var actual = DesktopSimilarImageService.CalculateHashDecodeLongEdge(
            new DesktopImageDimensions(8000, 8000),
            sourceSizeBytes: 40L * 1024L * 1024L);

        Assert.Equal(128, actual);
    }

    [Fact]
    public void CalculateHashDecodeLongEdge_preserves_extreme_long_image_budget()
    {
        var actual = DesktopSimilarImageService.CalculateHashDecodeLongEdge(
            new DesktopImageDimensions(1000, 9000),
            sourceSizeBytes: 40L * 1024L * 1024L);

        Assert.Equal(160, actual);
    }

    [Fact]
    public void CalculateUnknownDimensionHashReadLongEdge_uses_smaller_budget_for_large_source()
    {
        var actual = DesktopSimilarImageService.CalculateUnknownDimensionHashReadLongEdge(
            sourceSizeBytes: 40L * 1024L * 1024L);

        Assert.Equal(96, actual);
    }

    [Fact]
    public void CalculateUnknownDimensionHashReadLongEdge_tightens_further_for_huge_source()
    {
        var actual = DesktopSimilarImageService.CalculateUnknownDimensionHashReadLongEdge(
            sourceSizeBytes: 140L * 1024L * 1024L);

        Assert.Equal(80, actual);
    }

    [Fact]
    public void CreateHashReadSettings_uses_constrained_dimensions_for_large_square_input()
    {
        var settings = DesktopSimilarImageService.CreateHashReadSettings(
            new DesktopImageDimensions(8000, 8000),
            sourceSizeBytes: 40L * 1024L * 1024L);

        Assert.Equal(128U, settings.Width);
        Assert.Equal(128U, settings.Height);
    }

    [Fact]
    public void CreateHashReadSettings_uses_square_budget_when_dimensions_are_missing()
    {
        var settings = DesktopSimilarImageService.CreateHashReadSettings(
            dimensions: null,
            sourceSizeBytes: 140L * 1024L * 1024L);

        Assert.Equal(80U, settings.Width);
        Assert.Equal(80U, settings.Height);
    }

    private static ImageRecord CreateRecord(string path)
    {
        var info = new FileInfo(path);
        return new ImageRecord(path, Path.GetFileName(path), info.Length, info.LastWriteTimeUtc);
    }

    private static void WritePatternImage(string path, IMagickColor<byte> accentColor)
    {
        using var image = new MagickImage(MagickColors.White, 96, 72);
        var drawables = new Drawables()
            .FillColor(MagickColors.White)
            .Rectangle(0, 0, 95, 71)
            .FillColor(accentColor)
            .Rectangle(8, 8, 44, 60)
            .FillColor(MagickColors.Black)
            .Circle(64, 26, 84, 26)
            .FillColor(MagickColors.LightGray)
            .Rectangle(50, 44, 88, 62);

        drawables.Draw(image);
        image.Write(path);
    }

    private static void WriteStripedImage(string path)
    {
        using var image = new MagickImage(MagickColors.White, 96, 72);
        var drawables = new Drawables();

        for (var x = 0; x < 96; x += 12)
        {
            drawables
                .FillColor((x / 12) % 2 == 0 ? MagickColors.Black : MagickColors.White)
                .Rectangle(x, 0, Math.Min(95, x + 11), 71);
        }

        drawables
            .FillColor(MagickColors.Red)
            .Rectangle(18, 18, 78, 54)
            .Draw(image);
        image.Write(path);
    }
}
