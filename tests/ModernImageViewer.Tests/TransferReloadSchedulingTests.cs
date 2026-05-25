using ModernImageViewer.Desktop.ViewModels;

namespace ModernImageViewer.Tests;

public sealed class TransferReloadSchedulingTests
{
    [Fact]
    public void ShouldReloadAfterCopyTransfer_returns_true_when_target_stays_in_loaded_folder()
    {
        using var paths = TestPaths.Create();
        var folder = paths.Combine("images");
        Directory.CreateDirectory(folder);
        var targetPath = Path.Combine(folder, "copy.jpg");

        var actual = MainWindowViewModel.ShouldReloadAfterCopyTransfer([folder], [targetPath], includeSubfolders: false);

        Assert.True(actual);
    }

    [Fact]
    public void ShouldReloadAfterCopyTransfer_returns_false_when_target_is_outside_loaded_folder()
    {
        using var paths = TestPaths.Create();
        var sourceFolder = paths.Combine("source");
        var destinationFolder = paths.Combine("exports");
        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(destinationFolder);
        var targetPath = Path.Combine(destinationFolder, "copy.jpg");

        var actual = MainWindowViewModel.ShouldReloadAfterCopyTransfer([sourceFolder], [targetPath], includeSubfolders: false);

        Assert.False(actual);
    }

    [Fact]
    public void ShouldReloadAfterCopyTransfer_respects_includeSubfolders_for_nested_targets()
    {
        using var paths = TestPaths.Create();
        var sourceFolder = paths.Combine("source");
        var nestedFolder = Path.Combine(sourceFolder, "nested");
        Directory.CreateDirectory(nestedFolder);
        var targetPath = Path.Combine(nestedFolder, "copy.jpg");

        Assert.False(MainWindowViewModel.ShouldReloadAfterCopyTransfer([sourceFolder], [targetPath], includeSubfolders: false));
        Assert.True(MainWindowViewModel.ShouldReloadAfterCopyTransfer([sourceFolder], [targetPath], includeSubfolders: true));
    }

    [Fact]
    public void ShouldReloadAfterCopyTransfer_returns_false_for_explicit_file_inputs()
    {
        using var paths = TestPaths.Create();
        var sourceFolder = paths.Combine("source");
        Directory.CreateDirectory(sourceFolder);
        var explicitFile = Path.Combine(sourceFolder, "photo.jpg");
        File.WriteAllBytes(explicitFile, [1, 2, 3]);
        var targetPath = Path.Combine(sourceFolder, "photo_copy.jpg");

        var actual = MainWindowViewModel.ShouldReloadAfterCopyTransfer([explicitFile], [targetPath], includeSubfolders: true);

        Assert.False(actual);
    }

    [Fact]
    public void IsPathNestedUnderRoot_requires_real_child_path()
    {
        using var paths = TestPaths.Create();
        var root = paths.Combine("root");
        var nested = Path.Combine(root, "nested", "child");
        var sibling = paths.Combine("root-elsewhere");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(sibling);

        Assert.True(MainWindowViewModel.IsPathNestedUnderRoot(nested, root));
        Assert.False(MainWindowViewModel.IsPathNestedUnderRoot(root, root));
        Assert.False(MainWindowViewModel.IsPathNestedUnderRoot(sibling, root));
    }

    [Fact]
    public void CanRefreshCollectionInPlaceAfterRemovingPaths_returns_true_for_explicit_file_inputs()
    {
        using var paths = TestPaths.Create();
        var folder = paths.Combine("source");
        Directory.CreateDirectory(folder);
        var firstFile = Path.Combine(folder, "one.jpg");
        var secondFile = Path.Combine(folder, "two.png");
        File.WriteAllBytes(firstFile, [1]);
        File.WriteAllBytes(secondFile, [2]);

        var actual = MainWindowViewModel.CanRefreshCollectionInPlaceAfterRemovingPaths([firstFile, secondFile]);

        Assert.True(actual);
    }

    [Fact]
    public void CanRefreshCollectionInPlaceAfterRemovingPaths_returns_false_when_directory_input_exists()
    {
        using var paths = TestPaths.Create();
        var folder = paths.Combine("source");
        Directory.CreateDirectory(folder);
        var explicitFile = Path.Combine(folder, "one.jpg");
        File.WriteAllBytes(explicitFile, [1]);

        var actual = MainWindowViewModel.CanRefreshCollectionInPlaceAfterRemovingPaths([folder, explicitFile]);

        Assert.False(actual);
    }

    [Fact]
    public void CanRefreshCollectionInPlaceAfterRemovingPaths_allows_empty_remaining_inputs()
    {
        var actual = MainWindowViewModel.CanRefreshCollectionInPlaceAfterRemovingPaths([]);

        Assert.True(actual);
    }
}
