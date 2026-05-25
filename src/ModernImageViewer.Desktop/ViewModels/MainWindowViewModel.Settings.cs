using Avalonia.Threading;
using ModernImageViewer.Core;
using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly DesktopViewerSettingsStore _viewerSettingsStore = new();
    private readonly DispatcherTimer _settingsPersistTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(500)
    };

    private bool _hasLoadedViewerSettings;
    private bool _isApplyingViewerSettings;

    private void InitializeViewerSettings()
    {
        _settingsPersistTimer.Tick += SettingsPersistTimer_OnTick;
        ApplyViewerSettings(_viewerSettingsStore.Load());
        _hasLoadedViewerSettings = true;
    }

    private void DisposeViewerSettings()
    {
        FlushViewerSettings();
        _settingsPersistTimer.Stop();
        _settingsPersistTimer.Tick -= SettingsPersistTimer_OnTick;
    }

    private void ScheduleViewerSettingsSave()
    {
        if (_isApplyingViewerSettings || !_hasLoadedViewerSettings)
        {
            return;
        }

        _settingsPersistTimer.Stop();
        _settingsPersistTimer.Start();
    }

    private void FlushViewerSettings()
    {
        if (_isApplyingViewerSettings || !_hasLoadedViewerSettings)
        {
            return;
        }

        _settingsPersistTimer.Stop();
        _viewerSettingsStore.Save(BuildViewerSettingsSnapshot());
    }

    private void SettingsPersistTimer_OnTick(object? sender, EventArgs e)
    {
        FlushViewerSettings();
    }

    private DesktopViewerSettings BuildViewerSettingsSnapshot()
    {
        return new DesktopViewerSettings
        {
            ProcessingPerformanceMode = SelectedProcessingPerformanceModeOption?.Value ?? DesktopProcessingPerformanceMode.Balanced,
            ActiveSortMode = SelectedSortMode.Value,
            IncludeSubfolders = IncludeSubfolders,
            SelectedExportFormat = SelectedExportFormat?.Value ?? ExportImageFormat.Original,
            ExportDestinationFolder = ExportDestinationFolder,
            LimitExportLongEdge = LimitExportLongEdge,
            ExportLongEdgeText = ExportLongEdgeText,
            ExportJpegQualityText = ExportJpegQualityText,
            TargetSizeKilobytesText = TargetSizeKilobytesText,
            UseTargetSizeCompression = UseTargetSizeCompression,
            SelectedCompressionPresetLabel = SelectedCompressionPreset?.Label ?? "均衡压缩",
            RenameExportOutputs = RenameExportOutputs,
            StripMetadataOnExport = StripMetadataOnExport,
            PreserveEncodedDataWhenCleaning = PreserveEncodedDataWhenCleaning,
            ProcessCurrentCollection = ProcessCurrentCollection,
            RenameSequenceSeparator = RenameSequenceSeparator,
            RenameStartNumberText = RenameStartNumberText,
            RenameNumberDigitsText = RenameNumberDigitsText,
            SlideshowIntervalSeconds = SlideshowIntervalSeconds,
            IsSlideshowShuffleEnabled = IsSlideshowShuffleEnabled,
            IsSlideshowLoopEnabled = IsSlideshowLoopEnabled,
            SimilarImageDistanceThreshold = SelectedImageMatchThresholdOption?.DistanceThreshold ?? 8,
            IsContactSheetVisible = IsContactSheetVisible,
            IsSidebarVisible = IsSidebarVisible,
            IsInspectorVisible = IsInspectorVisible,
            IsFilmstripVisible = IsFilmstripVisible
        };
    }

    private void ApplyViewerSettings(DesktopViewerSettings settings)
    {
        _isApplyingViewerSettings = true;
        try
        {
            _selectedProcessingPerformanceModeOption = _processingPerformanceModeOptions.FirstOrDefault(
                option => option.Value == settings.ProcessingPerformanceMode)
                ?? _processingPerformanceModeOptions[1];
            ApplySelectedProcessingPerformanceMode(scheduleSave: false);

            _includeSubfolders = settings.IncludeSubfolders;
            _selectedSortMode = _sortModes.FirstOrDefault(option => option.Value == settings.ActiveSortMode) ?? _sortModes[0];

            _selectedExportFormat = _exportFormats.FirstOrDefault(option => option.Value == settings.SelectedExportFormat) ?? _exportFormats[0];
            _exportDestinationFolder = settings.ExportDestinationFolder ?? string.Empty;
            _limitExportLongEdge = settings.LimitExportLongEdge;
            _exportLongEdgeText = string.IsNullOrWhiteSpace(settings.ExportLongEdgeText) ? "2560" : settings.ExportLongEdgeText;
            _exportJpegQualityText = string.IsNullOrWhiteSpace(settings.ExportJpegQualityText) ? "92" : settings.ExportJpegQualityText;
            _targetSizeKilobytesText = string.IsNullOrWhiteSpace(settings.TargetSizeKilobytesText) ? "700" : settings.TargetSizeKilobytesText;
            _useTargetSizeCompression = settings.UseTargetSizeCompression;
            _selectedCompressionPreset = _compressionPresets.FirstOrDefault(
                option => string.Equals(option.Label, settings.SelectedCompressionPresetLabel, StringComparison.Ordinal))
                ?? (_compressionPresets.Count > 1 ? _compressionPresets[1] : _compressionPresets.FirstOrDefault());
            _renameExportOutputs = settings.RenameExportOutputs;
            _stripMetadataOnExport = settings.StripMetadataOnExport;
            _preserveEncodedDataWhenCleaning = settings.PreserveEncodedDataWhenCleaning;
            _processCurrentCollection = settings.ProcessCurrentCollection;

            _renameSequenceSeparator = settings.RenameSequenceSeparator ?? "_";
            _renameStartNumberText = string.IsNullOrWhiteSpace(settings.RenameStartNumberText) ? DefaultRenameStartNumber.ToString() : settings.RenameStartNumberText;
            _renameNumberDigitsText = string.IsNullOrWhiteSpace(settings.RenameNumberDigitsText) ? DefaultRenameNumberDigits.ToString() : settings.RenameNumberDigitsText;

            _isSlideshowShuffleEnabled = settings.IsSlideshowShuffleEnabled;
            _isSlideshowLoopEnabled = settings.IsSlideshowLoopEnabled;
            _slideshowIntervalSeconds = Math.Clamp(Math.Round(settings.SlideshowIntervalSeconds), 2, 12);
            _slideshowTimer.Interval = TimeSpan.FromSeconds(_slideshowIntervalSeconds);
            _isContactSheetVisible = settings.IsContactSheetVisible;
            _isSidebarVisible = settings.IsSidebarVisible;
            _isInspectorVisible = settings.IsInspectorVisible;
            _isFilmstripVisible = settings.IsFilmstripVisible;

            _selectedImageMatchThresholdOption = _imageMatchThresholdOptions.FirstOrDefault(
                option => option.DistanceThreshold == settings.SimilarImageDistanceThreshold)
                ?? _imageMatchThresholdOptions[1];

            OnPropertyChanged(nameof(IncludeSubfolders));
            OnPropertyChanged(nameof(SelectedProcessingPerformanceModeOption));
            OnPropertyChanged(nameof(ProcessingPerformanceModeSummaryText));
            OnPropertyChanged(nameof(SelectedSortMode));
            OnPropertyChanged(nameof(SelectedExportFormat));
            OnPropertyChanged(nameof(ExportDestinationFolder));
            OnPropertyChanged(nameof(LimitExportLongEdge));
            OnPropertyChanged(nameof(ExportLongEdgeText));
            OnPropertyChanged(nameof(ExportJpegQualityText));
            OnPropertyChanged(nameof(TargetSizeKilobytesText));
            OnPropertyChanged(nameof(UseTargetSizeCompression));
            OnPropertyChanged(nameof(SelectedCompressionPreset));
            OnPropertyChanged(nameof(ShowCompressionPresetSettings));
            OnPropertyChanged(nameof(ShowTargetSizeCompressionSettings));
            OnPropertyChanged(nameof(CompressionModeSummaryText));
            OnPropertyChanged(nameof(RenameExportOutputs));
            OnPropertyChanged(nameof(StripMetadataOnExport));
            OnPropertyChanged(nameof(PreserveEncodedDataWhenCleaning));
            OnPropertyChanged(nameof(ProcessCurrentCollection));
            OnPropertyChanged(nameof(RenameSequenceSeparator));
            OnPropertyChanged(nameof(RenameStartNumberText));
            OnPropertyChanged(nameof(RenameNumberDigitsText));
            OnPropertyChanged(nameof(IsSlideshowShuffleEnabled));
            OnPropertyChanged(nameof(IsSlideshowLoopEnabled));
            OnPropertyChanged(nameof(SlideshowIntervalSeconds));
            OnPropertyChanged(nameof(SelectedImageMatchThresholdOption));
            OnPropertyChanged(nameof(SimilarThresholdHintText));
            OnPropertyChanged(nameof(IsContactSheetVisible));
            OnPropertyChanged(nameof(IsSidebarVisible));
            OnPropertyChanged(nameof(SidebarColumnWidth));
            OnPropertyChanged(nameof(SidebarGapColumnWidth));
            OnPropertyChanged(nameof(SidebarToggleText));
            OnPropertyChanged(nameof(IsInspectorVisible));
            OnPropertyChanged(nameof(InspectorToggleText));
            OnPropertyChanged(nameof(IsFilmstripVisible));

            if (string.IsNullOrWhiteSpace(_exportDestinationFolder))
            {
                UpdateSuggestedExportDestinationFolder();
            }

            NotifyOperationStateChanged();
            NotifyRenameStateChanged();
            NotifySlideshowStateChanged();
            NotifyImageMatchStateChanged();
            NotifyReviewStateChanged();
            NotifyFilterStateChanged();
            NotifyNavigationStateChanged();
            NotifyLayoutStateChanged();
        }
        finally
        {
            _isApplyingViewerSettings = false;
        }
    }
}

