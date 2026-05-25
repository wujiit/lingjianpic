using ModernImageViewer.Core;

namespace ModernImageViewer.Tests;

public sealed class ImageCollectionBuilderTests
{
    [Fact]
    public void Build_returns_prompt_when_inputs_are_empty()
    {
        var builder = new ImageCollectionBuilder();

        var result = builder.Build([], SortMode.Name);

        Assert.Empty(result.Images);
        Assert.Null(result.FocusPath);
    }

    [Fact]
    public void Build_from_single_file_only_loads_the_selected_image()
    {
        var builder = new ImageCollectionBuilder();
        using var paths = TestPaths.Create();

        {
            var selectedPath = paths.Combine("b-image.png");
            var siblingPath = paths.Combine("a-image.jpg");
            var ignoredPath = paths.Combine("notes.txt");

            File.WriteAllBytes(selectedPath, [1, 2, 3]);
            File.WriteAllBytes(siblingPath, [4, 5, 6]);
            File.WriteAllText(ignoredPath, "ignore");

            var result = builder.Build([selectedPath], SortMode.Name);

            Assert.Equal(selectedPath, result.FocusPath);
            Assert.Equal([selectedPath], result.Images.Select(image => image.FullPath).ToArray());
            Assert.Equal(selectedPath, result.SourceLabel);
        }
    }

    [Fact]
    public void Build_includeSubfolders_adds_nested_supported_images()
    {
        var builder = new ImageCollectionBuilder();
        using var paths = TestPaths.Create();

        {
            var rootFolder = paths.Combine("collection");
            var nestedFolder = Path.Combine(rootFolder, "nested");
            Directory.CreateDirectory(nestedFolder);

            File.WriteAllBytes(Path.Combine(rootFolder, "root.png"), [1]);
            File.WriteAllBytes(Path.Combine(nestedFolder, "nested.webp"), [2]);
            File.WriteAllText(Path.Combine(nestedFolder, "nested.txt"), "ignore");

            var result = builder.Build([rootFolder], SortMode.Name, includeSubfolders: true);

            Assert.Equal(new[] { "nested.webp", "root.png" }, result.Images.Select(image => image.FileName).ToArray());
        }
    }

    [Fact]
    public void Sort_supports_case_sensitive_file_name_ordering_for_non_windows_behavior()
    {
        var records = new List<ImageRecord>
        {
            new(@"E:\img\cat.jpg", "cat.jpg", 1, new DateTime(2026, 4, 22, 10, 0, 0)),
            new(@"E:\img\Cat.jpg", "Cat.jpg", 1, new DateTime(2026, 4, 22, 10, 0, 0))
        };

        var sorted = ImageCollectionBuilder.Sort(records, SortMode.Name, StringComparer.Ordinal);

        Assert.Equal(["Cat.jpg", "cat.jpg"], sorted.Select(static item => item.FileName));
    }

    [Fact]
    public void Sort_reuses_input_list_instance()
    {
        var records = new List<ImageRecord>
        {
            new(@"E:\img\b.jpg", "b.jpg", 1, new DateTime(2026, 4, 22, 10, 0, 0)),
            new(@"E:\img\a.jpg", "a.jpg", 1, new DateTime(2026, 4, 22, 10, 0, 0))
        };

        var sorted = ImageCollectionBuilder.Sort(records, SortMode.Name, StringComparer.OrdinalIgnoreCase);

        Assert.Same(records, sorted);
        Assert.Equal(["a.jpg", "b.jpg"], sorted.Select(static item => item.FileName));
    }

    [Fact]
    public void BuildProgressive_reports_batches_while_preserving_final_sorted_output()
    {
        var builder = new ImageCollectionBuilder();
        using var paths = TestPaths.Create();
        var folder = paths.Combine("progressive");
        Directory.CreateDirectory(folder);

        File.WriteAllBytes(Path.Combine(folder, "gamma.jpg"), [1]);
        File.WriteAllBytes(Path.Combine(folder, "alpha.jpg"), [2]);
        File.WriteAllBytes(Path.Combine(folder, "epsilon.jpg"), [3]);
        File.WriteAllBytes(Path.Combine(folder, "beta.jpg"), [4]);
        File.WriteAllBytes(Path.Combine(folder, "delta.jpg"), [5]);

        var batches = new List<ImageCollectionBuildBatch>();

        var result = builder.BuildProgressive(
            [folder],
            SortMode.Name,
            onBatchDiscovered: batch => batches.Add(batch),
            progressBatchSize: 2);

        Assert.Equal([2, 4, 5], batches.Select(static batch => batch.TotalImageCount));
        Assert.Equal([2, 2, 1], batches.Select(static batch => batch.Images.Count));
        Assert.Equal(["alpha.jpg", "beta.jpg", "delta.jpg", "epsilon.jpg", "gamma.jpg"], result.Images.Select(static image => image.FileName));
    }

    [Fact]
    public void BuildProgressive_flushes_root_folder_images_before_scanning_nested_folders()
    {
        var builder = new ImageCollectionBuilder();
        using var paths = TestPaths.Create();
        var folder = paths.Combine("progressive-root-first");
        var nested = Path.Combine(folder, "nested");
        Directory.CreateDirectory(nested);

        File.WriteAllBytes(Path.Combine(folder, "root-a.jpg"), [1]);
        File.WriteAllBytes(Path.Combine(folder, "root-b.jpg"), [2]);
        File.WriteAllBytes(Path.Combine(nested, "nested-a.jpg"), [3]);
        File.WriteAllBytes(Path.Combine(nested, "nested-b.jpg"), [4]);

        var batches = new List<ImageCollectionBuildBatch>();

        var result = builder.BuildProgressive(
            [folder],
            SortMode.Name,
            includeSubfolders: true,
            onBatchDiscovered: batch => batches.Add(batch),
            progressBatchSize: 160);

        Assert.NotEmpty(batches);
        Assert.Equal(
            ["root-a.jpg", "root-b.jpg"],
            batches[0].Images.Select(static image => image.FileName).OrderBy(static name => name));
        Assert.Equal(["nested-a.jpg", "nested-b.jpg", "root-a.jpg", "root-b.jpg"], result.Images.Select(static image => image.FileName));
    }

    [Fact]
    public void BuildProgressive_flushes_smaller_first_batch_for_large_root_directory()
    {
        var builder = new ImageCollectionBuilder();
        using var paths = TestPaths.Create();
        var folder = paths.Combine("progressive-large-root");
        Directory.CreateDirectory(folder);

        for (var index = 0; index < 30; index++)
        {
            File.WriteAllBytes(Path.Combine(folder, $"image-{index:000}.jpg"), [1, 2, 3]);
        }

        var batches = new List<ImageCollectionBuildBatch>();

        var result = builder.BuildProgressive(
            [folder],
            SortMode.Name,
            onBatchDiscovered: batch => batches.Add(batch),
            progressBatchSize: 160);

        Assert.NotEmpty(batches);
        Assert.Equal(24, batches[0].Images.Count);
        Assert.Equal(24, batches[0].TotalImageCount);
        Assert.Equal(30, result.Images.Count);
    }
}
