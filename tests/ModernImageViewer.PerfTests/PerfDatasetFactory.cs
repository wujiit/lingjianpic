using System.Text.Json;
using ImageMagick;
using ImageMagick.Drawing;

namespace ModernImageViewer.PerfTests;

internal static class PerfDatasetFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static PerfDatasetBundle Create(string rootPath)
    {
        Directory.CreateDirectory(rootPath);

        var scanSeedPath = Path.Combine(rootPath, "seed", "scan");
        var exportSeedPath = Path.Combine(rootPath, "seed", "export");
        Directory.CreateDirectory(scanSeedPath);
        Directory.CreateDirectory(exportSeedPath);

        var scanSeedFiles = CreateSeedImages(scanSeedPath, "scan", count: 12, width: 320, height: 220, formats: [MagickFormat.Jpeg, MagickFormat.Png, MagickFormat.WebP]);
        var exportSeedFiles = CreateSeedImages(exportSeedPath, "export", count: 20, width: 2200, height: 1400, formats: [MagickFormat.Png]);

        var scan100Path = Path.Combine(rootPath, "scan-100");
        var scan1000Path = Path.Combine(rootPath, "scan-1000");
        var scan5000Path = Path.Combine(rootPath, "scan-5000-subfolders");
        PopulateCopiedDataset(scanSeedFiles, scan100Path, count: 100, subfolderCount: 1);
        PopulateCopiedDataset(scanSeedFiles, scan1000Path, count: 1000, subfolderCount: 1);
        PopulateCopiedDataset(scanSeedFiles, scan5000Path, count: 5000, subfolderCount: 10);

        var previewLargePath = Path.Combine(rootPath, "preview-large");
        var previewLongPath = Path.Combine(rootPath, "preview-long");
        var exportSourcePath = Path.Combine(rootPath, "export-source");
        var exactDuplicatePath = Path.Combine(rootPath, "exact-duplicate");
        var similarPath = Path.Combine(rootPath, "similar");

        CreateLargePreviewDataset(previewLargePath);
        CreateLongPreviewDataset(previewLongPath);
        PopulateCopiedDataset(exportSeedFiles, exportSourcePath, count: 100, subfolderCount: 1);
        CreateExactDuplicateDataset(exactDuplicatePath);
        CreateSimilarDataset(similarPath);

        var manifest = new PerfDatasetManifest(
            rootPath,
            [
                new PerfDatasetEntry("scan-100", 100, "100 张常规图片，平铺目录"),
                new PerfDatasetEntry("scan-1000", 1000, "1000 张常规图片，平铺目录"),
                new PerfDatasetEntry("scan-5000-subfolders", 5000, "5000 张图片，10 个子目录"),
                new PerfDatasetEntry("preview-large", 12, "12 张大图预览样本"),
                new PerfDatasetEntry("preview-long", 6, "6 张超长图样本"),
                new PerfDatasetEntry("export-source", 100, "100 张导出/压缩样本"),
                new PerfDatasetEntry("exact-duplicate", CountFiles(exactDuplicatePath), "完全重复扫描样本"),
                new PerfDatasetEntry("similar", CountFiles(similarPath), "相似图扫描样本")
            ]);

        File.WriteAllText(Path.Combine(rootPath, "dataset-manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions));
        return PerfDatasetBundle.Load(rootPath);
    }

    private static string[] CreateSeedImages(
        string directoryPath,
        string prefix,
        int count,
        int width,
        int height,
        IReadOnlyList<MagickFormat> formats)
    {
        Directory.CreateDirectory(directoryPath);
        var files = new List<string>(count);

        for (var index = 0; index < count; index++)
        {
            var format = formats[index % formats.Count];
            var extension = GetExtension(format);
            var path = Path.Combine(directoryPath, $"{prefix}-{index:D2}{extension}");
            WritePatternImage(path, width, height, seed: index * 17 + 11, format, quality: 92, variantKind: index % 3);
            files.Add(path);
        }

        return files.ToArray();
    }

    private static void PopulateCopiedDataset(
        IReadOnlyList<string> sourceFiles,
        string targetDirectoryPath,
        int count,
        int subfolderCount)
    {
        ResetDirectory(targetDirectoryPath);
        subfolderCount = Math.Max(1, subfolderCount);

        var subfolders = new List<string>(subfolderCount);
        for (var index = 0; index < subfolderCount; index++)
        {
            var folderPath = subfolderCount == 1
                ? targetDirectoryPath
                : Path.Combine(targetDirectoryPath, $"part-{index:D2}");
            Directory.CreateDirectory(folderPath);
            subfolders.Add(folderPath);
        }

        for (var index = 0; index < count; index++)
        {
            var sourcePath = sourceFiles[index % sourceFiles.Count];
            var folderPath = subfolders[index % subfolders.Count];
            var extension = Path.GetExtension(sourcePath);
            var targetPath = Path.Combine(folderPath, $"img-{index:D5}{extension}");
            File.Copy(sourcePath, targetPath, overwrite: true);
        }
    }

    private static void CreateLargePreviewDataset(string targetDirectoryPath)
    {
        ResetDirectory(targetDirectoryPath);

        var sizes = new[]
        {
            (4800, 3200, MagickFormat.Jpeg),
            (4200, 2800, MagickFormat.Jpeg),
            (3600, 5400, MagickFormat.Png),
            (5200, 3000, MagickFormat.Jpeg),
            (4600, 3400, MagickFormat.WebP),
            (4000, 2600, MagickFormat.Jpeg),
            (3000, 4500, MagickFormat.Png),
            (5000, 3333, MagickFormat.Jpeg),
            (3400, 5100, MagickFormat.WebP),
            (5600, 3200, MagickFormat.Jpeg),
            (3800, 2500, MagickFormat.Png),
            (4400, 2900, MagickFormat.Jpeg)
        };

        for (var index = 0; index < sizes.Length; index++)
        {
            var (width, height, format) = sizes[index];
            var path = Path.Combine(targetDirectoryPath, $"preview-large-{index:D2}{GetExtension(format)}");
            WritePatternImage(path, width, height, seed: 500 + index * 31, format, quality: 90, variantKind: index % 3);
        }
    }

    private static void CreateLongPreviewDataset(string targetDirectoryPath)
    {
        ResetDirectory(targetDirectoryPath);

        var sizes = new[]
        {
            (1200, 7200),
            (1400, 8600),
            (1000, 6400),
            (1600, 9000),
            (900, 5800),
            (1280, 7600)
        };

        for (var index = 0; index < sizes.Length; index++)
        {
            var (width, height) = sizes[index];
            var format = index % 2 == 0 ? MagickFormat.Png : MagickFormat.Jpeg;
            var path = Path.Combine(targetDirectoryPath, $"preview-long-{index:D2}{GetExtension(format)}");
            WritePatternImage(path, width, height, seed: 900 + index * 37, format, quality: 92, variantKind: 4);
        }
    }

    private static void CreateExactDuplicateDataset(string targetDirectoryPath)
    {
        ResetDirectory(targetDirectoryPath);

        for (var groupIndex = 0; groupIndex < 32; groupIndex++)
        {
            var extension = groupIndex % 2 == 0 ? ".png" : ".jpg";
            var basePath = Path.Combine(targetDirectoryPath, $"dup-base-{groupIndex:D2}{extension}");
            WritePatternImage(
                basePath,
                width: 900 + (groupIndex % 4) * 120,
                height: 680 + (groupIndex % 3) * 80,
                seed: 1200 + groupIndex * 19,
                format: extension == ".png" ? MagickFormat.Png : MagickFormat.Jpeg,
                quality: 90,
                variantKind: groupIndex % 3);

            for (var copyIndex = 0; copyIndex < 3; copyIndex++)
            {
                var copyPath = Path.Combine(targetDirectoryPath, $"dup-{groupIndex:D2}-{copyIndex:D2}{extension}");
                File.Copy(basePath, copyPath, overwrite: true);
            }
        }

        for (var uniqueIndex = 0; uniqueIndex < 32; uniqueIndex++)
        {
            var format = uniqueIndex % 3 == 0 ? MagickFormat.WebP : uniqueIndex % 2 == 0 ? MagickFormat.Jpeg : MagickFormat.Png;
            var path = Path.Combine(targetDirectoryPath, $"unique-{uniqueIndex:D2}{GetExtension(format)}");
            WritePatternImage(
                path,
                width: 880 + (uniqueIndex % 5) * 90,
                height: 640 + (uniqueIndex % 4) * 70,
                seed: 2200 + uniqueIndex * 23,
                format,
                quality: 91,
                variantKind: uniqueIndex % 5);
        }
    }

    private static void CreateSimilarDataset(string targetDirectoryPath)
    {
        ResetDirectory(targetDirectoryPath);

        for (var groupIndex = 0; groupIndex < 36; groupIndex++)
        {
            using var baseImage = CreatePatternImage(
                width: 960,
                height: 640,
                seed: 3200 + groupIndex * 29,
                variantKind: groupIndex % 4);

            WriteVariantImage(baseImage, Path.Combine(targetDirectoryPath, $"similar-{groupIndex:D2}-a.jpg"), variantIndex: 0);
            WriteVariantImage(baseImage, Path.Combine(targetDirectoryPath, $"similar-{groupIndex:D2}-b.jpg"), variantIndex: 1);
            WriteVariantImage(baseImage, Path.Combine(targetDirectoryPath, $"similar-{groupIndex:D2}-c.jpg"), variantIndex: 2);
        }

        for (var uniqueIndex = 0; uniqueIndex < 24; uniqueIndex++)
        {
            var path = Path.Combine(targetDirectoryPath, $"different-{uniqueIndex:D2}.jpg");
            WritePatternImage(
                path,
                width: 960,
                height: 640,
                seed: 5200 + uniqueIndex * 41,
                format: MagickFormat.Jpeg,
                quality: 88,
                variantKind: 6 + (uniqueIndex % 3));
        }
    }

    private static void WriteVariantImage(MagickImage baseImage, string path, int variantIndex)
    {
        using var image = baseImage.Clone();
        image.AutoOrient();

        switch (variantIndex)
        {
            case 1:
                image.BrightnessContrast(new Percentage(4), new Percentage(0));
                image.GaussianBlur(0.6, 0.35);
                break;
            case 2:
                new Drawables()
                    .FillColor(new MagickColor(255, 255, 255, 120))
                    .Rectangle(40, 36, 170, 86)
                    .FillColor(new MagickColor(0, 0, 0, 200))
                    .Rectangle(720, 500, 900, 600)
                    .Draw(image);
                image.Modulate(new Percentage(102), new Percentage(97), new Percentage(100));
                break;
        }

        image.Format = MagickFormat.Jpeg;
        image.Quality = (uint)(variantIndex == 0 ? 90 : variantIndex == 1 ? 86 : 83);
        image.Write(path);
    }

    private static void WritePatternImage(
        string path,
        int width,
        int height,
        int seed,
        MagickFormat format,
        int quality,
        int variantKind)
    {
        using var image = CreatePatternImage(width, height, seed, variantKind);
        image.Format = format;
        image.Quality = (uint)Math.Clamp(quality, 1, 100);
        image.Write(path);
    }

    private static MagickImage CreatePatternImage(int width, int height, int seed, int variantKind)
    {
        var background = new MagickColor(
            (byte)((seed * 17) % 256),
            (byte)((seed * 43) % 256),
            (byte)((seed * 71) % 256));
        var image = new MagickImage(background, (uint)width, (uint)height);
        var drawables = new Drawables();
        var tileSize = Math.Max(32, Math.Min(width, height) / 12);

        for (var y = 0; y < height; y += tileSize)
        {
            for (var x = 0; x < width; x += tileSize)
            {
                var color = new MagickColor(
                    (byte)((x * 11 + y * 5 + seed * 13) % 256),
                    (byte)((x * 7 + y * 17 + seed * 3) % 256),
                    (byte)((x * 19 + y * 9 + seed * 23) % 256));
                drawables.FillColor(color)
                    .Rectangle(x, y, Math.Min(width - 1, x + tileSize - 2), Math.Min(height - 1, y + tileSize - 2));
            }
        }

        switch (variantKind)
        {
            case 1:
                drawables
                    .FillColor(MagickColors.White)
                    .Ellipse(width * 0.30, height * 0.35, width * 0.14, height * 0.20, 0, 360)
                    .FillColor(MagickColors.Black)
                    .Ellipse(width * 0.68, height * 0.60, width * 0.12, height * 0.16, 0, 360);
                break;
            case 2:
                drawables
                    .FillColor(MagickColors.Gold)
                    .Rectangle(width * 0.08, height * 0.12, width * 0.42, height * 0.82)
                    .FillColor(MagickColors.MediumPurple)
                    .Circle(width * 0.72, height * 0.30, width * 0.84, height * 0.30);
                break;
            case 3:
                for (var stripe = 0; stripe < width + height; stripe += tileSize)
                {
                    drawables
                        .StrokeColor(new MagickColor(255, 255, 255, 180))
                        .StrokeWidth(Math.Max(3, tileSize / 10.0))
                        .Line(stripe, 0, Math.Max(0, stripe - height), height);
                }

                break;
            case 4:
                drawables
                    .FillColor(new MagickColor(255, 255, 255, 140))
                    .Rectangle(width * 0.1, height * 0.08, width * 0.9, height * 0.18)
                    .FillColor(new MagickColor(0, 0, 0, 180))
                    .Rectangle(width * 0.16, height * 0.78, width * 0.84, height * 0.92);
                break;
            default:
                drawables
                    .FillColor(MagickColors.Cyan)
                    .Polygon(
                    [
                        new PointD(width * 0.18, height * 0.18),
                        new PointD(width * 0.50, height * 0.08),
                        new PointD(width * 0.82, height * 0.24),
                        new PointD(width * 0.70, height * 0.76),
                        new PointD(width * 0.24, height * 0.88)
                    ]);
                break;
        }

        drawables.Draw(image);
        return image;
    }

    private static string GetExtension(MagickFormat format)
    {
        return format switch
        {
            MagickFormat.Jpeg => ".jpg",
            MagickFormat.Png => ".png",
            MagickFormat.WebP => ".webp",
            _ => ".img"
        };
    }

    private static int CountFiles(string directoryPath)
    {
        return Directory.Exists(directoryPath)
            ? Directory.EnumerateFiles(directoryPath, "*", SearchOption.TopDirectoryOnly).Count()
            : 0;
    }

    private static void ResetDirectory(string directoryPath)
    {
        if (Directory.Exists(directoryPath))
        {
            Directory.Delete(directoryPath, recursive: true);
        }

        Directory.CreateDirectory(directoryPath);
    }
}

internal sealed record PerfDatasetManifest(
    string RootPath,
    IReadOnlyList<PerfDatasetEntry> Datasets);

internal sealed record PerfDatasetEntry(
    string Name,
    int FileCount,
    string Description);

internal sealed class PerfDatasetBundle
{
    public string RootPath { get; }

    public string Scan100Path => Path.Combine(RootPath, "scan-100");

    public string Scan1000Path => Path.Combine(RootPath, "scan-1000");

    public string Scan5000Path => Path.Combine(RootPath, "scan-5000-subfolders");

    public string PreviewLargePath => Path.Combine(RootPath, "preview-large");

    public string PreviewLongPath => Path.Combine(RootPath, "preview-long");

    public string ExportSourcePath => Path.Combine(RootPath, "export-source");

    public string ExactDuplicatePath => Path.Combine(RootPath, "exact-duplicate");

    public string SimilarPath => Path.Combine(RootPath, "similar");

    public PerfDatasetManifest Manifest { get; }

    private PerfDatasetBundle(string rootPath, PerfDatasetManifest manifest)
    {
        RootPath = rootPath;
        Manifest = manifest;
    }

    public static PerfDatasetBundle Load(string rootPath)
    {
        var manifestPath = Path.Combine(rootPath, "dataset-manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("Perf dataset manifest does not exist.", manifestPath);
        }

        var manifest = JsonSerializer.Deserialize<PerfDatasetManifest>(File.ReadAllText(manifestPath))
                       ?? throw new InvalidOperationException("Unable to load perf dataset manifest.");
        return new PerfDatasetBundle(rootPath, manifest);
    }
}
