using ModernImageViewer.Desktop.ViewModels;

namespace ModernImageViewer.Tests;

public sealed class ImageMatchCollectionSnapshotTests
{
    [Fact]
    public async Task ImageMatchCollectionSnapshot_is_reused_until_visible_collection_changes()
    {
        using var paths = TestPaths.Create();
        var alphaPath = paths.Combine("alpha.jpg");
        var betaPath = paths.Combine("beta.png");
        await File.WriteAllBytesAsync(alphaPath, [1]);
        await File.WriteAllBytesAsync(betaPath, [2]);

        using var viewModel = new MainWindowViewModel();
        await viewModel.LoadInputsAsync([alphaPath, betaPath]);

        var firstSnapshot = viewModel.GetOrCreateImageMatchCollectionSnapshot();
        var secondSnapshot = viewModel.GetOrCreateImageMatchCollectionSnapshot();

        Assert.Same(firstSnapshot.Records, secondSnapshot.Records);
        Assert.Equal(firstSnapshot.Signature, secondSnapshot.Signature);
        Assert.Equal(2, firstSnapshot.Records.Count);

        viewModel.SelectedFormatFilter = "JPG";

        var filteredSnapshot = viewModel.GetOrCreateImageMatchCollectionSnapshot();
        var filteredSnapshotAgain = viewModel.GetOrCreateImageMatchCollectionSnapshot();

        Assert.NotSame(firstSnapshot.Records, filteredSnapshot.Records);
        Assert.Same(filteredSnapshot.Records, filteredSnapshotAgain.Records);
        Assert.NotEqual(firstSnapshot.Signature, filteredSnapshot.Signature);
        Assert.Single(filteredSnapshot.Records);
        Assert.Equal("alpha.jpg", filteredSnapshot.Records[0].FileName);
    }
}
