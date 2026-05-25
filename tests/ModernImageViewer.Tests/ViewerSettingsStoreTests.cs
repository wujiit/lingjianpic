using ModernImageViewer.Core;
using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Tests;

public sealed class ViewerSettingsStoreTests
{
    [Fact]
    public void Save_and_load_roundtrip_preserves_settings()
    {
        using var paths = TestPaths.Create();
        var store = CreateStore(paths);
        var expected = new DesktopViewerSettings
        {
            ProcessingPerformanceMode = DesktopProcessingPerformanceMode.HighPerformance,
            IsSidebarVisible = false,
            IsInspectorVisible = true,
            IncludeSubfolders = true,
            ActiveSortMode = SortMode.Modified,
            SelectedExportFormat = ExportImageFormat.Png,
            ExportDestinationFolder = paths.Combine("exports"),
            LimitExportLongEdge = false,
            ExportLongEdgeText = "1920",
            ExportJpegQualityText = "88",
            TargetSizeKilobytesText = "512",
            UseTargetSizeCompression = true,
            SelectedCompressionPresetLabel = "custom",
            RenameExportOutputs = true,
            StripMetadataOnExport = true,
            PreserveEncodedDataWhenCleaning = false,
            ProcessCurrentCollection = true,
            RenameSequenceSeparator = "-",
            RenameStartNumberText = "7",
            RenameNumberDigitsText = "4",
            SlideshowIntervalSeconds = 6,
            IsSlideshowShuffleEnabled = true,
            IsSlideshowLoopEnabled = false,
            SimilarImageDistanceThreshold = 12,
            IsContactSheetVisible = true,
            IsFilmstripVisible = false
        };

        store.Save(expected);
        var actual = store.Load();

        Assert.Equal(expected.IsSidebarVisible, actual.IsSidebarVisible);
        Assert.Equal(expected.ProcessingPerformanceMode, actual.ProcessingPerformanceMode);
        Assert.Equal(expected.IsInspectorVisible, actual.IsInspectorVisible);
        Assert.Equal(expected.IncludeSubfolders, actual.IncludeSubfolders);
        Assert.Equal(expected.ActiveSortMode, actual.ActiveSortMode);
        Assert.Equal(expected.SelectedExportFormat, actual.SelectedExportFormat);
        Assert.Equal(expected.ExportDestinationFolder, actual.ExportDestinationFolder);
        Assert.Equal(expected.LimitExportLongEdge, actual.LimitExportLongEdge);
        Assert.Equal(expected.ExportLongEdgeText, actual.ExportLongEdgeText);
        Assert.Equal(expected.ExportJpegQualityText, actual.ExportJpegQualityText);
        Assert.Equal(expected.TargetSizeKilobytesText, actual.TargetSizeKilobytesText);
        Assert.Equal(expected.UseTargetSizeCompression, actual.UseTargetSizeCompression);
        Assert.Equal(expected.SelectedCompressionPresetLabel, actual.SelectedCompressionPresetLabel);
        Assert.Equal(expected.RenameExportOutputs, actual.RenameExportOutputs);
        Assert.Equal(expected.StripMetadataOnExport, actual.StripMetadataOnExport);
        Assert.Equal(expected.PreserveEncodedDataWhenCleaning, actual.PreserveEncodedDataWhenCleaning);
        Assert.Equal(expected.ProcessCurrentCollection, actual.ProcessCurrentCollection);
        Assert.Equal(expected.RenameSequenceSeparator, actual.RenameSequenceSeparator);
        Assert.Equal(expected.RenameStartNumberText, actual.RenameStartNumberText);
        Assert.Equal(expected.RenameNumberDigitsText, actual.RenameNumberDigitsText);
        Assert.Equal(expected.SlideshowIntervalSeconds, actual.SlideshowIntervalSeconds);
        Assert.Equal(expected.IsSlideshowShuffleEnabled, actual.IsSlideshowShuffleEnabled);
        Assert.Equal(expected.IsSlideshowLoopEnabled, actual.IsSlideshowLoopEnabled);
        Assert.Equal(expected.SimilarImageDistanceThreshold, actual.SimilarImageDistanceThreshold);
        Assert.Equal(expected.IsContactSheetVisible, actual.IsContactSheetVisible);
        Assert.Equal(expected.IsFilmstripVisible, actual.IsFilmstripVisible);
    }

    [Fact]
    public void Load_returns_defaults_when_json_is_invalid()
    {
        using var paths = TestPaths.Create();
        var settingsPath = paths.Combine("settings.json");
        File.WriteAllText(settingsPath, "{ invalid json");

        var store = CreateStore(paths);

        var settings = store.Load();

        Assert.True(settings.IsSidebarVisible);
        Assert.True(settings.IsInspectorVisible);
        Assert.Equal(DesktopProcessingPerformanceMode.Balanced, settings.ProcessingPerformanceMode);
        Assert.Equal(SortMode.Name, settings.ActiveSortMode);
        Assert.Equal(ExportImageFormat.Original, settings.SelectedExportFormat);
    }

    private static DesktopViewerSettingsStore CreateStore(TestPaths paths)
    {
        return new DesktopViewerSettingsStore(paths.Combine("settings.json"));
    }
}
