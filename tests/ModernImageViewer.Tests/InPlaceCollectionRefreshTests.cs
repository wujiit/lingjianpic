using ModernImageViewer.Core;
using ModernImageViewer.Desktop.ViewModels;

namespace ModernImageViewer.Tests;

public sealed class InPlaceCollectionRefreshTests
{
    [Fact]
    public async Task Changing_sort_mode_reorders_loaded_items_without_reloading_from_disk()
    {
        using var paths = TestPaths.Create();
        var alphaPath = paths.Combine("alpha.jpg");
        var betaPath = paths.Combine("beta.jpg");
        await File.WriteAllBytesAsync(alphaPath, new byte[10]);
        await File.WriteAllBytesAsync(betaPath, new byte[20]);

        using var viewModel = new MainWindowViewModel();
        await viewModel.LoadInputsAsync([alphaPath, betaPath]);
        viewModel.SelectedSortMode = viewModel.SortModes.Single(static mode => mode.Value == SortMode.Name);
        await Task.Delay(200);

        Assert.Equal(["alpha.jpg", "beta.jpg"], viewModel.Images.Select(static item => item.FileName));

        DeleteFileWithRetry(alphaPath);
        DeleteFileWithRetry(betaPath);

        viewModel.SelectedSortMode = viewModel.SortModes.Single(static mode => mode.Value == SortMode.Size);
        await Task.Delay(200);

        Assert.Equal(2, viewModel.Images.Count);
        Assert.Equal(["beta.jpg", "alpha.jpg"], viewModel.Images.Select(static item => item.FileName));
    }

    [Fact]
    public void CanRefreshCollectionInPlaceFromExplicitFileInputs_returns_true_for_explicit_files()
    {
        using var paths = TestPaths.Create();
        var folder = paths.Combine("source");
        Directory.CreateDirectory(folder);
        var firstFile = Path.Combine(folder, "one.jpg");
        var secondFile = Path.Combine(folder, "two.png");
        File.WriteAllBytes(firstFile, [1]);
        File.WriteAllBytes(secondFile, [2]);

        var actual = MainWindowViewModel.CanRefreshCollectionInPlaceFromExplicitFileInputs([firstFile, secondFile]);

        Assert.True(actual);
    }

    [Fact]
    public void CanRefreshCollectionInPlaceFromExplicitFileInputs_returns_false_when_directory_input_exists()
    {
        using var paths = TestPaths.Create();
        var folder = paths.Combine("source");
        Directory.CreateDirectory(folder);
        var explicitFile = Path.Combine(folder, "one.jpg");
        File.WriteAllBytes(explicitFile, [1]);

        var actual = MainWindowViewModel.CanRefreshCollectionInPlaceFromExplicitFileInputs([folder, explicitFile]);

        Assert.False(actual);
    }

    [Fact]
    public void IsPathVisibleForCurrentInputs_returns_true_for_explicit_file_match()
    {
        using var paths = TestPaths.Create();
        var filePath = paths.Combine("single.jpg");
        File.WriteAllBytes(filePath, [1]);

        var actual = MainWindowViewModel.IsPathVisibleForCurrentInputs([filePath], filePath, includeSubfolders: false);

        Assert.True(actual);
    }

    [Fact]
    public void IsPathVisibleForCurrentInputs_returns_true_for_directory_root_file()
    {
        using var paths = TestPaths.Create();
        var folder = paths.Combine("images");
        Directory.CreateDirectory(folder);
        var filePath = Path.Combine(folder, "root.jpg");
        File.WriteAllBytes(filePath, [1]);

        var actual = MainWindowViewModel.IsPathVisibleForCurrentInputs([folder], filePath, includeSubfolders: false);

        Assert.True(actual);
    }

    [Fact]
    public void IsPathVisibleForCurrentInputs_respects_includeSubfolders_for_nested_paths()
    {
        using var paths = TestPaths.Create();
        var folder = paths.Combine("images");
        var nestedFolder = Path.Combine(folder, "nested");
        Directory.CreateDirectory(nestedFolder);
        var nestedPath = Path.Combine(nestedFolder, "nested.jpg");
        File.WriteAllBytes(nestedPath, [1]);

        Assert.False(MainWindowViewModel.IsPathVisibleForCurrentInputs([folder], nestedPath, includeSubfolders: false));
        Assert.True(MainWindowViewModel.IsPathVisibleForCurrentInputs([folder], nestedPath, includeSubfolders: true));
    }

    [Fact]
    public void IsPathVisibleForCurrentInputs_returns_false_for_path_outside_loaded_inputs()
    {
        using var paths = TestPaths.Create();
        var sourceFolder = paths.Combine("source");
        var targetFolder = paths.Combine("target");
        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(targetFolder);
        var targetPath = Path.Combine(targetFolder, "moved.jpg");
        File.WriteAllBytes(targetPath, [1]);

        var actual = MainWindowViewModel.IsPathVisibleForCurrentInputs([sourceFolder], targetPath, includeSubfolders: true);

        Assert.False(actual);
    }

    [Fact]
    public void SortImageItemsForMode_sorts_by_name_case_insensitive()
    {
        var items = new[]
        {
            CreateItem(@"E:\img\zeta.jpg", 3, new DateTimeOffset(2026, 4, 20, 10, 0, 0, TimeSpan.Zero)),
            CreateItem(@"E:\img\Alpha.jpg", 2, new DateTimeOffset(2026, 4, 19, 10, 0, 0, TimeSpan.Zero)),
            CreateItem(@"E:\img\beta.jpg", 1, new DateTimeOffset(2026, 4, 18, 10, 0, 0, TimeSpan.Zero))
        };

        var sorted = MainWindowViewModel.SortImageItemsForMode(items, SortMode.Name);

        Assert.Equal(["Alpha.jpg", "beta.jpg", "zeta.jpg"], sorted.Select(static item => item.FileName));
    }

    [Fact]
    public void SortImageItemsForMode_sorts_by_modified_descending_then_name()
    {
        var timestamp = new DateTimeOffset(2026, 4, 20, 10, 0, 0, TimeSpan.Zero);
        var items = new[]
        {
            CreateItem(@"E:\img\beta.jpg", 3, timestamp),
            CreateItem(@"E:\img\alpha.jpg", 2, timestamp),
            CreateItem(@"E:\img\older.jpg", 1, timestamp.AddDays(-1))
        };

        var sorted = MainWindowViewModel.SortImageItemsForMode(items, SortMode.Modified);

        Assert.Equal(["alpha.jpg", "beta.jpg", "older.jpg"], sorted.Select(static item => item.FileName));
    }

    [Fact]
    public void SortImageItemsForMode_sorts_by_size_descending_then_name()
    {
        var items = new[]
        {
            CreateItem(@"E:\img\gamma.jpg", 10, new DateTimeOffset(2026, 4, 20, 10, 0, 0, TimeSpan.Zero)),
            CreateItem(@"E:\img\alpha.jpg", 20, new DateTimeOffset(2026, 4, 19, 10, 0, 0, TimeSpan.Zero)),
            CreateItem(@"E:\img\beta.jpg", 20, new DateTimeOffset(2026, 4, 18, 10, 0, 0, TimeSpan.Zero))
        };

        var sorted = MainWindowViewModel.SortImageItemsForMode(items, SortMode.Size);

        Assert.Equal(["alpha.jpg", "beta.jpg", "gamma.jpg"], sorted.Select(static item => item.FileName));
    }

    [Fact]
    public void SortImageItemsForMode_supports_case_sensitive_name_ordering_for_non_windows_behavior()
    {
        var items = new[]
        {
            CreateItem(@"E:\img\cat.jpg", 10, new DateTimeOffset(2026, 4, 20, 10, 0, 0, TimeSpan.Zero)),
            CreateItem(@"E:\img\Cat.jpg", 10, new DateTimeOffset(2026, 4, 20, 10, 0, 0, TimeSpan.Zero))
        };

        var sorted = MainWindowViewModel.SortImageItemsForMode(items, SortMode.Name, StringComparer.Ordinal);

        Assert.Equal(["Cat.jpg", "cat.jpg"], sorted.Select(static item => item.FileName));
    }

    private static ImageListItemViewModel CreateItem(string path, long sizeBytes, DateTimeOffset modifiedAt)
    {
        return new ImageListItemViewModel(new ImageRecord(path, Path.GetFileName(path), sizeBytes, modifiedAt));
    }

    private static void DeleteFileWithRetry(string path)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return;
            }
            catch (IOException) when (attempt < 11)
            {
                System.Threading.Thread.Sleep((attempt + 1) * 30);
            }
            catch (UnauthorizedAccessException) when (attempt < 11)
            {
                System.Threading.Thread.Sleep((attempt + 1) * 30);
            }
        }
    }
}
