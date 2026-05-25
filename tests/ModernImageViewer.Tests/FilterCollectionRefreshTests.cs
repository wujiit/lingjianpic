using System.Collections.Specialized;
using ModernImageViewer.Core;
using ModernImageViewer.Desktop.ViewModels;

namespace ModernImageViewer.Tests;

public sealed class FilterCollectionRefreshTests
{
    [Fact]
    public void HasActiveFilterCriteria_returns_false_when_all_filters_are_default()
    {
        using var viewModel = new MainWindowViewModel();

        var actual = MainWindowViewModel.HasActiveFilterCriteria(
            searchText: string.Empty,
            selectedFormatFilter: viewModel.SelectedFormatFilter,
            selectedSizeFilter: viewModel.SelectedSizeFilter,
            selectedReviewFilter: viewModel.SelectedReviewFilter,
            selectedRatingFilter: viewModel.SelectedRatingFilter);

        Assert.False(actual);
    }

    [Theory]
    [InlineData("search", false, false, false, false)]
    [InlineData("", true, false, false, false)]
    [InlineData("", false, true, false, false)]
    [InlineData("", false, false, true, false)]
    [InlineData("", false, false, false, true)]
    public void HasActiveFilterCriteria_returns_true_when_any_filter_is_active(
        string searchText,
        bool changeFormat,
        bool changeSize,
        bool changeReview,
        bool changeRating)
    {
        using var viewModel = new MainWindowViewModel();

        var actual = MainWindowViewModel.HasActiveFilterCriteria(
            searchText,
            changeFormat ? "JPG" : viewModel.SelectedFormatFilter,
            changeSize ? "Large" : viewModel.SelectedSizeFilter,
            changeReview ? "Reviewed" : viewModel.SelectedReviewFilter,
            changeRating ? "5 星" : viewModel.SelectedRatingFilter);

        Assert.True(actual);
    }

    [Fact]
    public void HasSameImageSequence_returns_true_when_paths_match_in_order()
    {
        var first = CreateItem(@"E:\img\alpha.jpg");
        var second = CreateItem(@"E:\img\beta.jpg");

        var actual = MainWindowViewModel.HasSameImageSequence(
            [first, second],
            [CreateItem(@"E:\img\alpha.jpg"), CreateItem(@"E:\img\beta.jpg")]);

        Assert.True(actual);
    }

    [Fact]
    public void HasSameImageSequence_returns_false_when_order_differs()
    {
        var actual = MainWindowViewModel.HasSameImageSequence(
            [CreateItem(@"E:\img\alpha.jpg"), CreateItem(@"E:\img\beta.jpg")],
            [CreateItem(@"E:\img\beta.jpg"), CreateItem(@"E:\img\alpha.jpg")]);

        Assert.False(actual);
    }

    [Fact]
    public async Task Format_filter_does_not_rebuild_visible_collection_when_results_are_unchanged()
    {
        using var paths = TestPaths.Create();
        var alphaPath = paths.Combine("alpha.jpg");
        var betaPath = paths.Combine("beta.jpg");
        await File.WriteAllBytesAsync(alphaPath, [1]);
        await File.WriteAllBytesAsync(betaPath, [2]);

        using var viewModel = new MainWindowViewModel();
        await viewModel.LoadInputsAsync([alphaPath, betaPath]);

        var collectionChangedCount = 0;
        viewModel.Images.CollectionChanged += (_, _) => collectionChangedCount++;

        viewModel.SelectedFormatFilter = "JPG";

        Assert.Equal(0, collectionChangedCount);
        Assert.Equal(["alpha.jpg", "beta.jpg"], viewModel.Images.Select(static item => item.FileName));
    }

    private static ImageListItemViewModel CreateItem(string path)
    {
        return new ImageListItemViewModel(new ImageRecord(
            path,
            Path.GetFileName(path),
            1,
            new DateTimeOffset(2026, 4, 21, 10, 0, 0, TimeSpan.Zero)));
    }
}
