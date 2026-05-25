using ModernImageViewer.Desktop.ViewModels;

namespace ModernImageViewer.Tests;

public sealed class ImageMatchItemLookupTests
{
    [Fact]
    public async Task ImageMatchItemLookupSnapshot_is_reused_until_visible_collection_changes()
    {
        using var paths = TestPaths.Create();
        var alphaPath = paths.Combine("alpha.jpg");
        var betaPath = paths.Combine("beta.png");
        await File.WriteAllBytesAsync(alphaPath, [1]);
        await File.WriteAllBytesAsync(betaPath, [2]);

        using var viewModel = new MainWindowViewModel();
        await viewModel.LoadInputsAsync([alphaPath, betaPath]);

        var firstSnapshot = viewModel.GetOrCreateImageMatchItemLookupSnapshot();
        var secondSnapshot = viewModel.GetOrCreateImageMatchItemLookupSnapshot();

        Assert.Same(firstSnapshot.VisibleItemsByPath, secondSnapshot.VisibleItemsByPath);
        Assert.Same(firstSnapshot.AllItemsByPath, secondSnapshot.AllItemsByPath);
        Assert.Equal(2, firstSnapshot.VisibleItemsByPath.Count);
        Assert.Equal(2, firstSnapshot.AllItemsByPath.Count);

        viewModel.SelectedFormatFilter = "JPG";

        var filteredSnapshot = viewModel.GetOrCreateImageMatchItemLookupSnapshot();
        var filteredSnapshotAgain = viewModel.GetOrCreateImageMatchItemLookupSnapshot();

        Assert.NotSame(firstSnapshot.VisibleItemsByPath, filteredSnapshot.VisibleItemsByPath);
        Assert.NotSame(firstSnapshot.AllItemsByPath, filteredSnapshot.AllItemsByPath);
        Assert.Same(filteredSnapshot.VisibleItemsByPath, filteredSnapshotAgain.VisibleItemsByPath);
        Assert.Same(filteredSnapshot.AllItemsByPath, filteredSnapshotAgain.AllItemsByPath);
        Assert.Single(filteredSnapshot.VisibleItemsByPath);
        Assert.Equal(2, filteredSnapshot.AllItemsByPath.Count);
        Assert.DoesNotContain(betaPath, filteredSnapshot.VisibleItemsByPath.Keys);
        Assert.Contains(betaPath, filteredSnapshot.AllItemsByPath.Keys);
    }

    [Fact]
    public async Task ResolveImageMatchPanelItem_falls_back_to_all_images_when_item_is_filtered_out()
    {
        using var paths = TestPaths.Create();
        var alphaPath = paths.Combine("alpha.jpg");
        var betaPath = paths.Combine("beta.png");
        await File.WriteAllBytesAsync(alphaPath, [1]);
        await File.WriteAllBytesAsync(betaPath, [2]);

        using var viewModel = new MainWindowViewModel();
        await viewModel.LoadInputsAsync([alphaPath, betaPath]);
        viewModel.SelectedFormatFilter = "JPG";

        var resolved = viewModel.ResolveImageMatchPanelItem(betaPath);

        Assert.NotNull(resolved);
        Assert.Equal(betaPath, resolved.FullPath);
        Assert.Equal("beta.png", resolved.FileName);
    }

    [Fact]
    public async Task FocusImageByPath_only_focuses_visible_items_in_current_collection()
    {
        using var paths = TestPaths.Create();
        var alphaPath = paths.Combine("alpha.jpg");
        var betaPath = paths.Combine("beta.png");
        await File.WriteAllBytesAsync(alphaPath, [1]);
        await File.WriteAllBytesAsync(betaPath, [2]);

        using var viewModel = new MainWindowViewModel();
        await viewModel.LoadInputsAsync([alphaPath, betaPath]);
        viewModel.SelectedFormatFilter = "JPG";

        var hiddenItemResult = viewModel.FocusImageByPath(betaPath);
        var visibleItemResult = viewModel.FocusImageByPath(alphaPath);

        Assert.False(hiddenItemResult);
        Assert.True(visibleItemResult);
        Assert.Equal(alphaPath, viewModel.SelectedImage?.FullPath);
    }

    [Fact]
    public async Task SelectImagesByPath_keeps_visible_order_for_sparse_result_selection()
    {
        using var paths = TestPaths.Create();
        var allPaths = Enumerable.Range(1, 10)
            .Select(index => paths.Combine($"item-{index:00}.jpg"))
            .ToArray();

        for (var index = 0; index < allPaths.Length; index++)
        {
            await File.WriteAllBytesAsync(allPaths[index], [(byte)(index + 1)]);
        }

        using var viewModel = new MainWindowViewModel();
        await viewModel.LoadInputsAsync(allPaths);

        var selected = viewModel.SelectImagesByPath([allPaths[8], allPaths[2]]);

        Assert.True(selected);
        Assert.Equal([allPaths[2], allPaths[8]], viewModel.SelectedImages.Select(static item => item.FullPath));
        Assert.Equal(allPaths[2], viewModel.SelectedImage?.FullPath);
    }
}
