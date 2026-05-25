using ModernImageViewer.Core;
using ModernImageViewer.Desktop.Services;
using ModernImageViewer.Desktop.ViewModels;

namespace ModernImageViewer.Tests;

public sealed class DesktopRenameServiceTests
{
    [Fact]
    public void TryBuildPlan_allows_swapping_selected_file_names()
    {
        using var paths = TestPaths.Create();
        var firstPath = paths.Combine("photo_002.jpg");
        var secondPath = paths.Combine("photo_001.jpg");
        File.WriteAllText(firstPath, "first");
        File.WriteAllText(secondPath, "second");

        var plan = DesktopRenameService.TryBuildPlan(
            [firstPath, secondPath],
            new SequentialRenamePattern("photo", "_", 1, 3),
            out var previewBaseName,
            out var validationMessage);

        Assert.NotNull(plan);
        Assert.Null(validationMessage);
        Assert.Equal("photo_001", previewBaseName);
        Assert.Equal(4, plan.TotalMoveCount);
        Assert.Collection(
            plan.Items,
            item =>
            {
                Assert.Equal(firstPath, item.SourcePath);
                Assert.Equal(secondPath, item.TargetPath);
                Assert.True(item.RequiresRename);
                Assert.NotEqual(item.SourcePath, item.TemporaryPath);
                Assert.NotEqual(item.TargetPath, item.TemporaryPath);
            },
            item =>
            {
                Assert.Equal(secondPath, item.SourcePath);
                Assert.Equal(firstPath, item.TargetPath);
                Assert.True(item.RequiresRename);
                Assert.NotEqual(item.SourcePath, item.TemporaryPath);
                Assert.NotEqual(item.TargetPath, item.TemporaryPath);
            });
        Assert.Equal(2, plan.Items.Select(static item => item.TemporaryPath).Distinct(PathComparison.Comparer).Count());
    }

    [Fact]
    public void TryBuildPlan_rejects_external_existing_target()
    {
        using var paths = TestPaths.Create();
        var sourcePath = paths.Combine("source.jpg");
        var externalTargetPath = paths.Combine("photo_001.jpg");
        File.WriteAllText(sourcePath, "source");
        File.WriteAllText(externalTargetPath, "external");

        var plan = DesktopRenameService.TryBuildPlan(
            [sourcePath],
            new SequentialRenamePattern("photo_001", "_", 1, 3),
            out _,
            out var validationMessage);

        Assert.Null(plan);
        Assert.Equal("目标文件已存在：photo_001.jpg", validationMessage);
    }

    [Fact]
    public async Task ExecuteAsync_swaps_file_contents_without_leaving_temporary_files()
    {
        using var paths = TestPaths.Create();
        var firstPath = paths.Combine("photo_002.jpg");
        var secondPath = paths.Combine("photo_001.jpg");
        File.WriteAllText(firstPath, "first");
        File.WriteAllText(secondPath, "second");

        var plan = DesktopRenameService.TryBuildPlan(
            [firstPath, secondPath],
            new SequentialRenamePattern("photo", "_", 1, 3),
            out _,
            out var validationMessage);

        Assert.NotNull(plan);
        Assert.Null(validationMessage);

        var result = await DesktopRenameService.ExecuteAsync(plan, CancellationToken.None);

        Assert.False(result.WasCanceled);
        Assert.Null(result.Failure);
        Assert.Null(result.RollbackMessage);
        Assert.Equal(4, result.CompletedMoveCount);
        Assert.Equal(4, result.TotalMoveCount);
        Assert.Equal(secondPath, result.CompletedRenameMap[firstPath]);
        Assert.Equal(firstPath, result.CompletedRenameMap[secondPath]);
        Assert.Equal("second", File.ReadAllText(firstPath));
        Assert.Equal("first", File.ReadAllText(secondPath));
        Assert.Equal(
            ["photo_001.jpg", "photo_002.jpg"],
            Directory.GetFiles(paths.RootPath)
                .Select(static filePath => Path.GetFileName(filePath)!)
                .OrderBy(static fileName => fileName, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }
}
