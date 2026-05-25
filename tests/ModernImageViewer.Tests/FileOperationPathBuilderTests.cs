using ModernImageViewer.Core;
using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Tests;

public sealed class ExportPathBuilderTests
{
    [Fact]
    public void BuildAvailableTargetPath_returns_original_name_when_destination_is_free()
    {
        using var paths = TestPaths.Create();
        var sourcePath = paths.Combine("photo.jpg");
        var destinationFolder = paths.Combine("exports");

        File.WriteAllBytes(sourcePath, [1]);
        Directory.CreateDirectory(destinationFolder);

        var targetPath = ExportPathBuilder.BuildAvailableTargetPath(
            sourcePath,
            destinationFolder,
            ".jpg",
            "export",
            reservedTargetPaths: new HashSet<string>(PathComparison.Comparer));

        Assert.Equal(Path.Combine(destinationFolder, "photo.jpg"), targetPath);
    }

    [Fact]
    public void BuildAvailableTargetPath_avoids_overwriting_the_source_file()
    {
        using var paths = TestPaths.Create();
        var sourcePath = paths.Combine("photo.jpg");

        File.WriteAllBytes(sourcePath, [1]);

        var targetPath = ExportPathBuilder.BuildAvailableTargetPath(
            sourcePath,
            Path.GetDirectoryName(sourcePath)!,
            ".jpg",
            "export",
            reservedTargetPaths: new HashSet<string>(PathComparison.Comparer));

        Assert.Equal(paths.Combine("photo_export01.jpg"), targetPath);
    }

    [Fact]
    public void BuildAvailableTargetPath_uses_base_name_override_and_skips_reserved_target()
    {
        using var paths = TestPaths.Create();
        var sourcePath = paths.Combine("source.bmp");
        var destinationFolder = paths.Combine("exports");
        var reservedTargetPath = Path.Combine(destinationFolder, "travel_003.png");

        File.WriteAllBytes(sourcePath, [1]);
        Directory.CreateDirectory(destinationFolder);

        var targetPath = ExportPathBuilder.BuildAvailableTargetPath(
            sourcePath,
            destinationFolder,
            ".png",
            "export",
            baseNameOverride: "travel_003",
            reservedTargetPaths: new HashSet<string>(PathComparison.Comparer) { reservedTargetPath });

        Assert.Equal(Path.Combine(destinationFolder, "travel_003_export01.png"), targetPath);
    }

    [Fact]
    public void BuildAvailableTargetPath_uses_custom_suffix_for_compression_conflicts()
    {
        using var paths = TestPaths.Create();
        var sourcePath = paths.Combine("source.jpg");
        var destinationFolder = paths.Combine("exports");
        var reservedTargetPath = Path.Combine(destinationFolder, "source.jpg");

        File.WriteAllBytes(sourcePath, [1]);
        Directory.CreateDirectory(destinationFolder);

        var targetPath = ExportPathBuilder.BuildAvailableTargetPath(
            sourcePath,
            destinationFolder,
            ".jpg",
            "compressed",
            reservedTargetPaths: new HashSet<string>(PathComparison.Comparer) { reservedTargetPath });

        Assert.Equal(Path.Combine(destinationFolder, "source_compressed01.jpg"), targetPath);
    }
}
