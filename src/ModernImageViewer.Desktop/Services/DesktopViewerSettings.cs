using ModernImageViewer.Core;

namespace ModernImageViewer.Desktop.Services;

public sealed class DesktopViewerSettings
{
    public DesktopProcessingPerformanceMode ProcessingPerformanceMode { get; set; } = DesktopProcessingPerformanceMode.Balanced;

    public SortMode ActiveSortMode { get; set; } = SortMode.Name;

    public bool IncludeSubfolders { get; set; }

    public ExportImageFormat SelectedExportFormat { get; set; } = ExportImageFormat.Original;

    public string ExportDestinationFolder { get; set; } = string.Empty;

    public bool LimitExportLongEdge { get; set; } = true;

    public string ExportLongEdgeText { get; set; } = "2560";

    public string ExportJpegQualityText { get; set; } = "92";

    public string TargetSizeKilobytesText { get; set; } = "700";

    public bool UseTargetSizeCompression { get; set; }

    public string SelectedCompressionPresetLabel { get; set; } = "均衡压缩";

    public bool RenameExportOutputs { get; set; }

    public bool StripMetadataOnExport { get; set; }

    public bool PreserveEncodedDataWhenCleaning { get; set; } = true;

    public bool ProcessCurrentCollection { get; set; }

    public string RenameSequenceSeparator { get; set; } = "_";

    public string RenameStartNumberText { get; set; } = "1";

    public string RenameNumberDigitsText { get; set; } = "3";

    public double SlideshowIntervalSeconds { get; set; } = 4;

    public bool IsSlideshowShuffleEnabled { get; set; }

    public bool IsSlideshowLoopEnabled { get; set; } = true;

    public int SimilarImageDistanceThreshold { get; set; } = 8;

    public bool IsContactSheetVisible { get; set; }

    public bool IsSidebarVisible { get; set; } = true;

    public bool IsInspectorVisible { get; set; } = true;

    public bool IsFilmstripVisible { get; set; } = true;
}

