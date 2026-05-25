using System.Collections.ObjectModel;
using ModernImageViewer.Desktop.ViewModels;

namespace ModernImageViewer.Tests;

public sealed class ObservableCollectionSyncTests
{
    [Fact]
    public void ReplaceObservableCollectionItemsIfChanged_returns_false_without_mutating_when_sequence_matches()
    {
        var collection = new ObservableCollection<string>(["全部", "JPG", "PNG"]);
        var collectionChangedCount = 0;
        collection.CollectionChanged += (_, _) => collectionChangedCount++;

        var changed = MainWindowViewModel.ReplaceObservableCollectionItemsIfChanged(
            collection,
            ["全部", "JPG", "PNG"],
            StringComparer.OrdinalIgnoreCase);

        Assert.False(changed);
        Assert.Equal(0, collectionChangedCount);
        Assert.Equal(["全部", "JPG", "PNG"], collection);
    }

    [Fact]
    public void ReplaceObservableCollectionItemsIfChanged_replaces_items_when_sequence_differs()
    {
        var collection = new ObservableCollection<string>(["全部", "JPG"]);
        var collectionChangedCount = 0;
        collection.CollectionChanged += (_, _) => collectionChangedCount++;

        var changed = MainWindowViewModel.ReplaceObservableCollectionItemsIfChanged(
            collection,
            ["全部", "JPG", "PNG"],
            StringComparer.OrdinalIgnoreCase);

        Assert.True(changed);
        Assert.True(collectionChangedCount > 0);
        Assert.Equal(["全部", "JPG", "PNG"], collection);
    }

    [Fact]
    public async Task Rename_with_same_extension_does_not_rebuild_format_filter_options()
    {
        using var paths = TestPaths.Create();
        var alphaPath = paths.Combine("alpha.jpg");
        var betaPath = paths.Combine("beta.jpg");
        await File.WriteAllBytesAsync(alphaPath, [1]);
        await File.WriteAllBytesAsync(betaPath, [2]);

        using var viewModel = new MainWindowViewModel();
        await viewModel.LoadInputsAsync([alphaPath, betaPath]);

        var collectionChangedCount = 0;
        viewModel.FormatFilterOptions.CollectionChanged += (_, _) => collectionChangedCount++;

        viewModel.RenameBaseName = "renamed";
        await viewModel.RenameSelectedAsync();

        Assert.Equal(0, collectionChangedCount);
        Assert.Equal(2, viewModel.FormatFilterOptions.Count);
        Assert.Equal("JPG", viewModel.FormatFilterOptions[1]);
    }

    [Fact]
    public void ReplaceObservableCollectionItemsIfChanged_replaces_reference_items_even_when_paths_match()
    {
        var oldFirst = CreateImageItem(@"E:\img\alpha.jpg");
        var oldSecond = CreateImageItem(@"E:\img\beta.jpg");
        var newFirst = CreateImageItem(@"E:\img\alpha.jpg");
        var newSecond = CreateImageItem(@"E:\img\beta.jpg");
        var collection = new ObservableCollection<ImageListItemViewModel>([oldFirst, oldSecond]);

        var changed = MainWindowViewModel.ReplaceObservableCollectionItemsIfChanged(
            collection,
            [newFirst, newSecond]);

        Assert.True(changed);
        Assert.Same(newFirst, collection[0]);
        Assert.Same(newSecond, collection[1]);
    }

    [Fact]
    public async Task ToggleRecentSessionPinned_with_single_item_does_not_rebuild_collection()
    {
        using var paths = TestPaths.Create();
        var alphaPath = paths.Combine("alpha.jpg");
        await File.WriteAllBytesAsync(alphaPath, [1]);

        using var viewModel = new MainWindowViewModel();
        await viewModel.LoadInputsAsync([alphaPath]);

        var collectionChangedCount = 0;
        viewModel.RecentSessions.CollectionChanged += (_, _) => collectionChangedCount++;

        viewModel.ToggleRecentSessionPinned(viewModel.RecentSessions[0]);

        Assert.Equal(0, collectionChangedCount);
        Assert.True(viewModel.RecentSessions[0].IsPinned);
    }

    private static ImageListItemViewModel CreateImageItem(string path)
    {
        return new ImageListItemViewModel(new ModernImageViewer.Core.ImageRecord(
            path,
            Path.GetFileName(path),
            1,
            new DateTimeOffset(2026, 4, 21, 10, 0, 0, TimeSpan.Zero)));
    }
}
