using ModernImageViewer.Desktop.ViewModels;

namespace ModernImageViewer.Tests;

public sealed class SearchFilterDebounceTests
{
    [Fact]
    public async Task SearchText_does_not_rebuild_filtered_list_immediately()
    {
        using var paths = TestPaths.Create();
        var alphaPath = paths.Combine("alpha.jpg");
        var betaPath = paths.Combine("beta.jpg");
        await File.WriteAllBytesAsync(alphaPath, [1]);
        await File.WriteAllBytesAsync(betaPath, [2]);

        using var viewModel = new MainWindowViewModel();
        await viewModel.LoadInputsAsync([alphaPath, betaPath]);

        viewModel.SearchText = "alpha";

        Assert.Equal(2, viewModel.Images.Count);

        await WaitForImageCountAsync(viewModel, expectedCount: 1);

        Assert.Single(viewModel.Images);
        Assert.Equal("alpha.jpg", viewModel.Images[0].FileName);
    }

    [Fact]
    public async Task SearchText_only_applies_latest_pending_query()
    {
        using var paths = TestPaths.Create();
        var alphaPath = paths.Combine("alpha.jpg");
        var betaPath = paths.Combine("beta.jpg");
        await File.WriteAllBytesAsync(alphaPath, [1]);
        await File.WriteAllBytesAsync(betaPath, [2]);

        using var viewModel = new MainWindowViewModel();
        await viewModel.LoadInputsAsync([alphaPath, betaPath]);

        viewModel.SearchText = "alpha";
        await Task.Delay(40);
        viewModel.SearchText = "beta";

        Assert.Equal(2, viewModel.Images.Count);

        await WaitForImageCountAsync(viewModel, expectedCount: 1);

        Assert.Single(viewModel.Images);
        Assert.Equal("beta.jpg", viewModel.Images[0].FileName);
    }

    private static async Task WaitForImageCountAsync(MainWindowViewModel viewModel, int expectedCount)
    {
        const int maxAttempts = 60;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (viewModel.Images.Count == expectedCount)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Equal(expectedCount, viewModel.Images.Count);
    }
}
