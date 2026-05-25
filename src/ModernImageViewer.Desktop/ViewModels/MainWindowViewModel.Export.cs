using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using ModernImageViewer.Core;
using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Desktop.ViewModels;

internal sealed record DesktopExportExecutionPhasePlan(
    DesktopBatchWorkloadKind WorkloadKind,
    DesktopBatchExecutionPlan ExecutionPlan,
    int[] StepOrdinals);

public sealed partial class MainWindowViewModel
{
    private const int OperationUiUpdateIntervalMilliseconds = 120;
    private readonly DesktopImageExportService _desktopImageExportService = new();
    private ReadOnlyCollection<ExportFormatOption> _exportFormats = [];
    private ReadOnlyCollection<CompressionPresetOption> _compressionPresets = [];
    private ReadOnlyCollection<WatermarkPlacementOption> _watermarkPlacements = [];
    private ExportFormatOption? _selectedExportFormat;
    private CompressionPresetOption? _selectedCompressionPreset;
    private WatermarkPlacementOption? _selectedWatermarkPlacement;
    private string _exportDestinationFolder = string.Empty;
    private string _suggestedExportDestinationFolder = string.Empty;
    private string _exportLongEdgeText = "2560";
    private string _exportJpegQualityText = "92";
    private string _targetSizeKilobytesText = "700";
    private string _watermarkText = "LingJian";
    private string _watermarkImagePath = string.Empty;
    private string _watermarkOpacityText = "45";
    private string _watermarkMarginText = "32";
    private string _watermarkTextSizeText = "42";
    private string _watermarkTextColor = "#FFFFFF";
    private string _watermarkImageScaleText = "18";
    private string _operationStatusText = "选择图片后，可以导出、压缩或清理图片信息。";
    private bool _limitExportLongEdge = true;
    private bool _renameExportOutputs;
    private bool _stripMetadataOnExport;
    private bool _preserveEncodedDataWhenCleaning = true;
    private bool _useTargetSizeCompression;
    private bool _useImageWatermark;
    private bool _isExportProcessing;
    private bool _processCurrentCollection;
    private int _operationProgressValue;
    private int _operationProgressMaximum = 1;
    private CancellationTokenSource? _currentOperationCts;
    private string _batchResultSummaryText = "处理结果会显示在这里。";
    private DateTime _lastOperationUiUpdateUtc = DateTime.MinValue;

    public IReadOnlyList<ExportFormatOption> ExportFormats => _exportFormats;

    public IReadOnlyList<CompressionPresetOption> CompressionPresetOptions => _compressionPresets;

    public IReadOnlyList<WatermarkPlacementOption> WatermarkPlacementOptions => _watermarkPlacements;

    public ObservableCollection<BatchFailureViewModel> BatchFailures { get; } = [];

    public ExportFormatOption? SelectedExportFormat
    {
        get => _selectedExportFormat;
        set
        {
            if (!SetProperty(ref _selectedExportFormat, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanRunExport));
            OnPropertyChanged(nameof(CanRunCompress));
            OnPropertyChanged(nameof(CanRunWatermark));
            OnPropertyChanged(nameof(CanRunExifEdit));
            OnPropertyChanged(nameof(CompressionModeSummaryText));
            OnPropertyChanged(nameof(CompressButtonText));
            ScheduleViewerSettingsSave();
        }
    }

    public CompressionPresetOption? SelectedCompressionPreset
    {
        get => _selectedCompressionPreset;
        set
        {
            if (!SetProperty(ref _selectedCompressionPreset, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanRunCompress));
            OnPropertyChanged(nameof(CompressionModeSummaryText));
            OnPropertyChanged(nameof(CompressActionText));
            OnPropertyChanged(nameof(CompressButtonText));
            ScheduleViewerSettingsSave();
        }
    }

    public WatermarkPlacementOption? SelectedWatermarkPlacement
    {
        get => _selectedWatermarkPlacement;
        set
        {
            if (!SetProperty(ref _selectedWatermarkPlacement, value))
            {
                return;
            }

            NotifyWatermarkStateChanged();
            ScheduleViewerSettingsSave();
        }
    }

    public string ExportDestinationFolder
    {
        get => _exportDestinationFolder;
        set
        {
            if (!SetProperty(ref _exportDestinationFolder, value))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                UpdateSuggestedExportDestinationFolder();
            }

            OnPropertyChanged(nameof(ResolvedExportDestinationFolder));
            ScheduleViewerSettingsSave();
        }
    }

    public string ExportLongEdgeText
    {
        get => _exportLongEdgeText;
        set
        {
            if (!SetProperty(ref _exportLongEdgeText, value))
            {
                return;
            }

            ScheduleViewerSettingsSave();
        }
    }

    public string ExportJpegQualityText
    {
        get => _exportJpegQualityText;
        set
        {
            if (!SetProperty(ref _exportJpegQualityText, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanRunCompress));
            OnPropertyChanged(nameof(CompressionModeSummaryText));
            ScheduleViewerSettingsSave();
        }
    }

    public string TargetSizeKilobytesText
    {
        get => _targetSizeKilobytesText;
        set
        {
            if (!SetProperty(ref _targetSizeKilobytesText, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanRunCompress));
            OnPropertyChanged(nameof(CompressionModeSummaryText));
            OnPropertyChanged(nameof(CompressActionText));
            OnPropertyChanged(nameof(CompressButtonText));
            ScheduleViewerSettingsSave();
        }
    }

    public string WatermarkText
    {
        get => _watermarkText;
        set
        {
            if (!SetProperty(ref _watermarkText, value))
            {
                return;
            }

            NotifyWatermarkStateChanged();
            ScheduleViewerSettingsSave();
        }
    }

    public string WatermarkImagePath
    {
        get => _watermarkImagePath;
        set
        {
            if (!SetProperty(ref _watermarkImagePath, value))
            {
                return;
            }

            NotifyWatermarkStateChanged();
            ScheduleViewerSettingsSave();
        }
    }

    public string WatermarkOpacityText
    {
        get => _watermarkOpacityText;
        set
        {
            if (!SetProperty(ref _watermarkOpacityText, value))
            {
                return;
            }

            NotifyWatermarkStateChanged();
            ScheduleViewerSettingsSave();
        }
    }

    public string WatermarkMarginText
    {
        get => _watermarkMarginText;
        set
        {
            if (!SetProperty(ref _watermarkMarginText, value))
            {
                return;
            }

            NotifyWatermarkStateChanged();
            ScheduleViewerSettingsSave();
        }
    }

    public string WatermarkTextSizeText
    {
        get => _watermarkTextSizeText;
        set
        {
            if (!SetProperty(ref _watermarkTextSizeText, value))
            {
                return;
            }

            NotifyWatermarkStateChanged();
            ScheduleViewerSettingsSave();
        }
    }

    public string WatermarkTextColor
    {
        get => _watermarkTextColor;
        set
        {
            if (!SetProperty(ref _watermarkTextColor, value))
            {
                return;
            }

            NotifyWatermarkStateChanged();
            ScheduleViewerSettingsSave();
        }
    }

    public string WatermarkImageScaleText
    {
        get => _watermarkImageScaleText;
        set
        {
            if (!SetProperty(ref _watermarkImageScaleText, value))
            {
                return;
            }

            NotifyWatermarkStateChanged();
            ScheduleViewerSettingsSave();
        }
    }

    public string OperationStatusText
    {
        get => _operationStatusText;
        private set => SetProperty(ref _operationStatusText, value);
    }

    public string BatchResultSummaryText
    {
        get => _batchResultSummaryText;
        private set => SetProperty(ref _batchResultSummaryText, value);
    }

    public string BatchPerformanceGuardText => IsExportProcessing
        ? $"性能保护已开启：后台预览暂停，图像线程限制为 {DesktopImageProcessingPolicy.ThreadLimit}；批量导出、EXIF、复制、移动和回收站会按任务类型与文件体积自动收敛并发。"
        : $"批处理低占用模式：图像线程最多 {DesktopImageProcessingPolicy.ThreadLimit} 个；开始处理后会自动暂停缩略图和大图预览，并按任务类型与文件体积自动调整并发，避免 CPU/内存冲高。";

    public bool HasBatchFailures => BatchFailures.Count > 0;

    public bool CanCancelOperation => IsExportProcessing
        && _currentOperationCts is not null
        && !_currentOperationCts.IsCancellationRequested;

    public bool RenameExportOutputs
    {
        get => _renameExportOutputs;
        set
        {
            if (!SetProperty(ref _renameExportOutputs, value))
            {
                return;
            }

            NotifyOperationStateChanged();
            ScheduleViewerSettingsSave();
        }
    }

    public bool LimitExportLongEdge
    {
        get => _limitExportLongEdge;
        set
        {
            if (!SetProperty(ref _limitExportLongEdge, value))
            {
                return;
            }

            ScheduleViewerSettingsSave();
        }
    }

    public bool StripMetadataOnExport
    {
        get => _stripMetadataOnExport;
        set
        {
            if (!SetProperty(ref _stripMetadataOnExport, value))
            {
                return;
            }

            ScheduleViewerSettingsSave();
        }
    }

    public bool PreserveEncodedDataWhenCleaning
    {
        get => _preserveEncodedDataWhenCleaning;
        set
        {
            if (!SetProperty(ref _preserveEncodedDataWhenCleaning, value))
            {
                return;
            }

            ScheduleViewerSettingsSave();
        }
    }

    public bool UseTargetSizeCompression
    {
        get => _useTargetSizeCompression;
        set
        {
            if (!SetProperty(ref _useTargetSizeCompression, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ShowCompressionPresetSettings));
            OnPropertyChanged(nameof(ShowTargetSizeCompressionSettings));
            OnPropertyChanged(nameof(CanRunCompress));
            OnPropertyChanged(nameof(CompressionModeSummaryText));
            OnPropertyChanged(nameof(CompressActionText));
            OnPropertyChanged(nameof(CompressButtonText));
            ScheduleViewerSettingsSave();
        }
    }

    public bool UseImageWatermark
    {
        get => _useImageWatermark;
        set
        {
            if (!SetProperty(ref _useImageWatermark, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ShowTextWatermarkSettings));
            OnPropertyChanged(nameof(ShowImageWatermarkSettings));
            NotifyWatermarkStateChanged();
            ScheduleViewerSettingsSave();
        }
    }

    public bool ProcessCurrentCollection
    {
        get => _processCurrentCollection;
        set
        {
            if (!SetProperty(ref _processCurrentCollection, value))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(ExportDestinationFolder))
            {
                UpdateSuggestedExportDestinationFolder();
            }

            NotifyOperationStateChanged();
            ScheduleViewerSettingsSave();
        }
    }

    public bool IsExportProcessing
    {
        get => _isExportProcessing;
        private set
        {
            if (!SetProperty(ref _isExportProcessing, value))
            {
                return;
            }

            if (value && IsSlideshowPlaying)
            {
                StopSlideshow("处理开始后，幻灯片已自动暂停。");
            }

            OnPropertyChanged(nameof(CanRunExport));
            OnPropertyChanged(nameof(CanRunCompress));
            OnPropertyChanged(nameof(CanRunCleanMetadata));
            OnPropertyChanged(nameof(CanRunWatermark));
            OnPropertyChanged(nameof(CanRunExifEdit));
            OnPropertyChanged(nameof(CanCancelOperation));
            OnPropertyChanged(nameof(CanRunExactDuplicateScan));
            OnPropertyChanged(nameof(CanRunSimilarImageScan));
            OnPropertyChanged(nameof(CanCopyOperationPaths));
            OnPropertyChanged(nameof(CanCopyToFolder));
            OnPropertyChanged(nameof(CanMoveToFolder));
            OnPropertyChanged(nameof(CanRecycleToTrash));
            OnPropertyChanged(nameof(CanOpenContainingFolder));
            OnPropertyChanged(nameof(CanAddCurrentToCompare));
            OnPropertyChanged(nameof(CanRemoveCurrentFromCompare));
            OnPropertyChanged(nameof(CanClearCompare));
            NotifySlideshowStateChanged();
            NotifyNavigationStateChanged();
            NotifyReviewStateChanged();
            NotifyPreviewToolStateChanged();

            if (value)
            {
                PauseBackgroundImageLoading();
            }
            else
            {
                ResumeBackgroundImageLoading();
            }

            OnPropertyChanged(nameof(BatchPerformanceGuardText));
        }
    }

    public int OperationProgressValue
    {
        get => _operationProgressValue;
        private set
        {
            if (!SetProperty(ref _operationProgressValue, value))
            {
                return;
            }

            OnPropertyChanged(nameof(OperationProgressCountText));
        }
    }

    public int OperationProgressMaximum
    {
        get => _operationProgressMaximum;
        private set
        {
            if (!SetProperty(ref _operationProgressMaximum, value))
            {
                return;
            }

            OnPropertyChanged(nameof(OperationProgressCountText));
        }
    }

    public string ResolvedExportDestinationFolder
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ExportDestinationFolder))
            {
                return ExportDestinationFolder;
            }

            if (!string.IsNullOrWhiteSpace(_suggestedExportDestinationFolder))
            {
                return _suggestedExportDestinationFolder;
            }

            var picturesFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            return string.IsNullOrWhiteSpace(picturesFolder) ? Environment.CurrentDirectory : picturesFolder;
        }
    }

    public bool CanRunExport => SelectedExportFormat is not null
        && GetOperationTargetCount() > 0
        && !IsExportProcessing
        && (!RenameExportOutputs || !string.IsNullOrWhiteSpace(NormalizeRenameBaseName(RenameBaseName)));

    public bool CanRunCompress => !IsExportProcessing
        && GetOperationTargetCount() > 0
        && (UseTargetSizeCompression
            ? TryParsePositiveLong(TargetSizeKilobytesText, out _) && TryParseJpegQuality(ExportJpegQualityText, out _)
            : SelectedCompressionPreset is not null)
        && (!RenameExportOutputs || !string.IsNullOrWhiteSpace(NormalizeRenameBaseName(RenameBaseName)));

    public bool CanRunCleanMetadata => GetOperationTargetCount() > 0
        && !IsExportProcessing
        && (!RenameExportOutputs || !string.IsNullOrWhiteSpace(NormalizeRenameBaseName(RenameBaseName)));

    public bool CanRunWatermark => SelectedExportFormat is not null
        && SelectedWatermarkPlacement is not null
        && GetOperationTargetCount() > 0
        && !IsExportProcessing
        && IsWatermarkInputValid()
        && (!RenameExportOutputs || !string.IsNullOrWhiteSpace(NormalizeRenameBaseName(RenameBaseName)));

    public string OperationScopeSummaryText
    {
        get
        {
            var targetCount = GetOperationTargetCount();
            if (ProcessCurrentCollection)
            {
                return targetCount switch
                {
                    <= 0 => "当前列表里还没有可处理图片。",
                    1 => "当前会处理列表中的 1 张图片。",
                    _ => $"当前会处理列表中的 {targetCount} 张图片。"
                };
            }

            return SelectedImage is null
                ? "当前只会处理右侧选中的那一张图片。"
                : $"当前只处理：{SelectedImage.FileName}";
        }
    }

    public string ExportRenamePreviewText
    {
        get
        {
            if (!RenameExportOutputs)
            {
                return "关闭后默认保留原文件名。";
            }

            var pattern = BuildRenamePatternOrNull(showValidationMessage: false);
            if (pattern is null)
            {
                return "请先填写有效的基础名称、起始编号和编号位数。";
            }

            return BuildRenamePreviewText(pattern, GetPreviewExportExtension(), GetOperationTargetCount());
        }
    }

    public string ExportActionText => ProcessCurrentCollection
        ? "批量导出"
        : HasBatchSelection ? "导出所选" : "导出当前图";

    public bool ShowCompressionPresetSettings => !UseTargetSizeCompression;

    public bool ShowTargetSizeCompressionSettings => UseTargetSizeCompression;

    public string CompressionModeSummaryText
    {
        get
        {
            var formatPrefix = BuildCompressionFormatSummary();
            if (UseTargetSizeCompression)
            {
                var hasTargetKilobytes = TryParsePositiveLong(TargetSizeKilobytesText, out var targetKilobytes);
                var hasJpegQuality = TryParseJpegQuality(ExportJpegQualityText, out var jpegQuality);
                if (hasTargetKilobytes && hasJpegQuality)
                {
                    return $"{formatPrefix}；目标体积：约 {targetKilobytes} KB；质量上限：{jpegQuality}；会在需要时缩小尺寸并清理拍摄信息。";
                }

                if (hasTargetKilobytes)
                {
                    return $"{formatPrefix}；目标体积：约 {targetKilobytes} KB；会自动清理拍摄信息。";
                }

                return $"{formatPrefix}；按目标体积压缩，并自动清理拍摄信息。";
            }

            if (SelectedCompressionPreset is null)
            {
                return "请先选择一个压缩档位。";
            }

            return $"{formatPrefix}，{SelectedCompressionPreset.Label}：长边 {SelectedCompressionPreset.LongEdgePixels}px，质量 {SelectedCompressionPreset.JpegQuality}，{SelectedCompressionPreset.Description}。";
        }
    }

    public string CompressButtonText
    {
        get
        {
            if (ProcessCurrentCollection)
            {
                return "批量压缩";
            }

            if (HasBatchSelection)
            {
                return "压缩所选";
            }

            if (UseTargetSizeCompression)
            {
                return "压缩到目标体积";
            }

            return SelectedCompressionPreset is null ? "开始压缩" : $"开始压缩 {SelectedCompressionPreset.Label}";
        }
    }

    public string CompressActionText => ProcessCurrentCollection
        ? "批量压缩"
        : HasBatchSelection ? "压缩所选" : "压缩到目标体积";

    public string CleanMetadataActionText => ProcessCurrentCollection
        ? "批量清理信息"
        : HasBatchSelection ? "清理所选信息" : "清理信息副本";

    public string WatermarkActionText => ProcessCurrentCollection
        ? "批量添加水印"
        : HasBatchSelection ? "给所选添加水印" : "给当前图添加水印";

    public bool ShowTextWatermarkSettings => !UseImageWatermark;

    public bool ShowImageWatermarkSettings => UseImageWatermark;

    public string WatermarkSummaryText
    {
        get
        {
            var kind = UseImageWatermark ? "图片水印" : "文字水印";
            var placement = SelectedWatermarkPlacement?.Label ?? "未选择位置";
            return $"{kind}，位置：{placement}，透明度：{WatermarkOpacityText}%";
        }
    }

    public string OperationProgressCountText => $"{Math.Min(OperationProgressValue, OperationProgressMaximum)} / {Math.Max(1, OperationProgressMaximum)}";

    private void InitializeExportSettings()
    {
        _compressionPresets =
        [
            new CompressionPresetOption("轻度压缩", 3840, 88, "更适合保留细节"),
            new CompressionPresetOption("均衡压缩", 2560, 82, "兼顾体积和清晰度"),
            new CompressionPresetOption("高压缩", 1920, 74, "更偏向减小体积")
        ];
        _exportFormats =
        [
            new ExportFormatOption(ExportImageFormat.Original, "原格式"),
            new ExportFormatOption(ExportImageFormat.Jpeg, "JPG"),
            new ExportFormatOption(ExportImageFormat.Png, "PNG"),
            new ExportFormatOption(ExportImageFormat.WebP, "WEBP"),
            new ExportFormatOption(ExportImageFormat.Avif, "AVIF"),
            new ExportFormatOption(ExportImageFormat.Tiff, "TIFF"),
            new ExportFormatOption(ExportImageFormat.Bmp, "BMP"),
            new ExportFormatOption(ExportImageFormat.Jxl, "JXL"),
            new ExportFormatOption(ExportImageFormat.Heic, "HEIC")
        ];
        _watermarkPlacements =
        [
            new WatermarkPlacementOption(DesktopWatermarkPlacement.BottomRight, "右下角"),
            new WatermarkPlacementOption(DesktopWatermarkPlacement.BottomLeft, "左下角"),
            new WatermarkPlacementOption(DesktopWatermarkPlacement.TopRight, "右上角"),
            new WatermarkPlacementOption(DesktopWatermarkPlacement.TopLeft, "左上角"),
            new WatermarkPlacementOption(DesktopWatermarkPlacement.Center, "居中"),
            new WatermarkPlacementOption(DesktopWatermarkPlacement.Tiled, "平铺")
        ];
        SelectedExportFormat = _exportFormats[0];
        SelectedCompressionPreset = _compressionPresets.Count > 1 ? _compressionPresets[1] : _compressionPresets.FirstOrDefault();
        SelectedWatermarkPlacement = _watermarkPlacements[0];
        UpdateSuggestedExportDestinationFolder();
    }

    private void OnExportSelectionChanged(ImageListItemViewModel? value)
    {
        if (value is not null && string.IsNullOrWhiteSpace(ExportDestinationFolder))
        {
            UpdateSuggestedExportDestinationFolder();
        }

        NotifyOperationStateChanged();
    }

    private void OnImageCollectionChanged()
    {
        if (ProcessCurrentCollection && _allImages.Count == 0)
        {
            ProcessCurrentCollection = false;
            return;
        }

        RefreshSelectedImagesAfterCollectionChange();

         if (string.IsNullOrWhiteSpace(ExportDestinationFolder))
         {
             UpdateSuggestedExportDestinationFolder();
         }

         OnSlideshowCollectionChanged();
          OnContactSheetCollectionChanged();
          OnImageMatchTargetCollectionChanged();
          OnRenameTargetCollectionChanged();
          NotifyLayoutStateChanged();
          NotifyOperationStateChanged();
      }

    public void SetExportDestinationFolder(string folderPath)
    {
        ExportDestinationFolder = folderPath;
    }

    public void SetWatermarkImagePath(string imagePath)
    {
        WatermarkImagePath = imagePath;
        UseImageWatermark = true;
    }

    public void CancelCurrentOperation()
    {
        if (_currentOperationCts is null || _currentOperationCts.IsCancellationRequested)
        {
            return;
        }

        _currentOperationCts.Cancel();
        OperationStatusText = "正在取消当前批处理...";
        OnPropertyChanged(nameof(CanCancelOperation));
    }

    private void BeginOperationProgress(int totalCount, string statusText, string summaryText)
    {
        _lastOperationUiUpdateUtc = DateTime.MinValue;
        OperationProgressMaximum = Math.Max(1, totalCount);
        OperationProgressValue = 0;
        OperationStatusText = statusText;
        BatchResultSummaryText = summaryText;
    }

    private void UpdateOperationProgress(int completedCount, int totalCount, string? statusText = null, bool force = false)
    {
        var safeTotal = Math.Max(1, totalCount);
        var safeCompleted = Math.Clamp(completedCount, 0, safeTotal);
        var now = DateTime.UtcNow;
        if (!force
            && safeCompleted < safeTotal
            && (now - _lastOperationUiUpdateUtc).TotalMilliseconds < OperationUiUpdateIntervalMilliseconds)
        {
            return;
        }

        _lastOperationUiUpdateUtc = now;
        OperationProgressMaximum = safeTotal;
        OperationProgressValue = safeCompleted;
        if (!string.IsNullOrWhiteSpace(statusText))
        {
            OperationStatusText = statusText;
        }

        BatchResultSummaryText = $"已处理 {safeCompleted}/{safeTotal}。";
    }
    public async Task ExportSelectedAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRunExport || SelectedExportFormat is null)
        {
            return;
        }

        var request = BuildExportRequest(
            SelectedExportFormat.Value,
            stripMetadata: StripMetadataOnExport,
            preserveEncodedData: StripMetadataOnExport && PreserveEncodedDataWhenCleaning);
        if (request is null)
        {
            return;
        }

        await RunExportAsync(
            request,
            collisionSuffixLabel: "导出",
            singleBusyText: "正在导出当前图片...",
            singleSuccessPrefix: "已导出",
            batchBusyText: "正在批量导出...",
            batchSuccessPrefix: "批量导出完成",
            cancellationToken);
    }

    public async Task CompressSelectedAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRunCompress)
        {
            return;
        }

        var compressionFormat = ResolveCompressionOutputFormat();
        if (!UseTargetSizeCompression)
        {
            if (SelectedCompressionPreset is null)
            {
                OperationStatusText = "请先选择一个压缩档位。";
                return;
            }

            var presetRequest = BuildExportRequest(
                compressionFormat,
                stripMetadata: true,
                preserveEncodedData: false,
                overrideLongEdge: SelectedCompressionPreset.LongEdgePixels,
                overrideJpegQuality: SelectedCompressionPreset.JpegQuality);
            if (presetRequest is null)
            {
                return;
            }

            await RunExportAsync(
                presetRequest,
                collisionSuffixLabel: "压缩",
                singleBusyText: "正在压缩当前图片...",
                singleSuccessPrefix: "已压缩",
                batchBusyText: "正在批量压缩...",
                batchSuccessPrefix: "批量压缩完成",
                cancellationToken);
            return;
        }

        if (!TryParsePositiveLong(TargetSizeKilobytesText, out var targetKilobytes))
        {
            OperationStatusText = "目标体积请填一个正整数，单位 KB。";
            return;
        }

        var request = BuildExportRequest(
            compressionFormat,
            stripMetadata: true,
            preserveEncodedData: false,
            targetFileSizeBytes: targetKilobytes * 1024L);
        if (request is null)
        {
            return;
        }

        await RunExportAsync(
            request,
            collisionSuffixLabel: "压缩",
            singleBusyText: "正在压缩当前图片...",
            singleSuccessPrefix: "已压缩",
            batchBusyText: "正在批量压缩...",
            batchSuccessPrefix: "批量压缩完成",
            cancellationToken);
    }

    public async Task CleanMetadataSelectedAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRunCleanMetadata)
        {
            return;
        }

        var request = BuildExportRequest(
            ExportImageFormat.Original,
            stripMetadata: true,
            preserveEncodedData: PreserveEncodedDataWhenCleaning,
            overrideLongEdge: null);
        if (request is null)
        {
            return;
        }

        await RunExportAsync(
            request,
            collisionSuffixLabel: "清理信息",
            singleBusyText: "正在生成清理信息副本...",
            singleSuccessPrefix: "已生成",
            batchBusyText: "正在批量生成清理信息副本...",
            batchSuccessPrefix: "批量清理信息完成",
            cancellationToken);
    }

    public async Task AddWatermarkSelectedAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRunWatermark || SelectedExportFormat is null)
        {
            return;
        }

        var watermarkRequest = BuildWatermarkRequest();
        if (watermarkRequest is null)
        {
            return;
        }

        var request = BuildExportRequest(
            SelectedExportFormat.Value,
            stripMetadata: StripMetadataOnExport,
            preserveEncodedData: false,
            watermark: watermarkRequest);
        if (request is null)
        {
            return;
        }

        await RunExportAsync(
            request,
            collisionSuffixLabel: "水印",
            singleBusyText: "正在给当前图片添加水印...",
            singleSuccessPrefix: "已生成水印图",
            batchBusyText: "正在批量添加水印...",
            batchSuccessPrefix: "批量添加水印完成",
            cancellationToken);
    }

    private async Task RunExportAsync(
        DesktopExportRequest request,
        string collisionSuffixLabel,
        string singleBusyText,
        string singleSuccessPrefix,
        string batchBusyText,
        string batchSuccessPrefix,
        CancellationToken cancellationToken)
    {
        using var trackedOperation = BeginTrackedOperation();
        var targets = ResolveOperationTargets();
        if (targets.Count == 0)
        {
            return;
        }

        var destinationFolder = ResolveDestinationFolderForRun();
        Directory.CreateDirectory(destinationFolder);
        ClearBatchFailures();

        var failureMessages = new List<string>();
        var reservedTargetPaths = new HashSet<string>(PathComparison.Comparer);
        var exportRenamePattern = GetExportRenamePatternOrNull();
        if (RenameExportOutputs && exportRenamePattern is null)
        {
            return;
        }

        string? lastTargetPath = null;
        var hasMultipleTargets = targets.Count > 1;

        IsExportProcessing = true;
        BeginOperationProgress(
            targets.Count,
            hasMultipleTargets ? batchBusyText : singleBusyText,
            hasMultipleTargets ? $"准备处理 {targets.Count} 张图片。" : "准备处理当前图片。");

        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _currentOperationCts = operationCts;
        OnPropertyChanged(nameof(CanCancelOperation));
        using var trace = BeginDiagnosticsOperation(
            "batch",
            "export",
            ("targetCount", targets.Count.ToString()),
            ("destinationFolder", destinationFolder),
            ("outputFormat", request.Format.ToString()),
            ("stripMetadata", request.StripMetadata.ToString()),
            ("limitLongEdge", request.LongEdgePixels?.ToString()),
            ("targetSizeBytes", request.TargetFileSizeBytes?.ToString()),
            ("watermarkEnabled", (request.Watermark is not null).ToString()));

        try
        {
            var plan = new List<(int Index, DesktopPreparedExportOperation Operation, string TargetPath)>(targets.Count);
            for (var index = 0; index < targets.Count; index++)
            {
                operationCts.Token.ThrowIfCancellationRequested();

                var sourcePath = targets[index].FullPath;
                var preparedOperation = _desktopImageExportService.PrepareOperation(sourcePath, request);
                string? targetBaseName = null;
                if (exportRenamePattern is not null)
                {
                    try
                    {
                        targetBaseName = exportRenamePattern.BuildFileBaseName(index, targets.Count);
                    }
                    catch (OverflowException)
                    {
                        OperationStatusText = "起始编号过大，无法继续导出重命名。";
                        return;
                    }
                }

                var targetPath = BuildTargetPath(
                    sourcePath,
                    preparedOperation.TargetExtension,
                    collisionSuffixLabel,
                    destinationFolder,
                    targetBaseName,
                    reservedTargetPaths);

                plan.Add((index, preparedOperation, targetPath));
            }

            var successfulTargets = new ConcurrentDictionary<int, string>();

            var executionPhases = BuildExportExecutionPhases(
                plan.Select(static step => step.Operation).ToArray());
            var processedCount = 0;
            var wasCanceled = false;

            foreach (var phase in executionPhases)
            {
                if (operationCts.Token.IsCancellationRequested)
                {
                    wasCanceled = true;
                    break;
                }

                var phaseItems = phase.StepOrdinals
                    .Select(stepOrdinal => plan[stepOrdinal])
                    .Select(static step => new DesktopBatchItem<(int Index, DesktopPreparedExportOperation Operation, string TargetPath)>(
                        step,
                        Path.GetFileName(step.Operation.SourcePath)))
                    .ToArray();
                if (phaseItems.Length == 0)
                {
                    continue;
                }

                var phaseOffset = processedCount;
                IProgress<DesktopBatchProgress> phaseProgress = new Progress<DesktopBatchProgress>(update =>
                {
                    UpdateOperationProgress(
                        phaseOffset + update.ProcessedCount,
                        targets.Count,
                        hasMultipleTargets
                            ? $"正在处理 {phaseOffset + update.ProcessedCount}/{targets.Count}：{update.DisplayName}"
                            : null,
                        force: phaseOffset + update.ProcessedCount >= targets.Count);
                });

                var phaseResult = await _desktopBatchProcessor.RunAsync(
                    phaseItems,
                    async (item, _, token) =>
                    {
                        var step = item.Value;
                        await RunBatchSynchronousWorkAsync(
                            () => _desktopImageExportService.Export(step.Operation, step.TargetPath, token),
                            phase.ExecutionPlan,
                            token);
                        successfulTargets[step.Index] = step.TargetPath;
                    },
                    phaseProgress,
                    executionPlan: phase.ExecutionPlan,
                    cancellationToken: operationCts.Token);

                foreach (var failure in phaseResult.Failures)
                {
                    var failedStep = phaseItems[failure.Index].Value;
                    AddBatchFailure(failedStep.Index, failure.DisplayName, failure.ErrorMessage);
                    failureMessages.Add($"{failure.DisplayName}：{failure.ErrorMessage}");
                }

                processedCount += phaseResult.ProcessedCount;
                if (phaseResult.WasCanceled)
                {
                    wasCanceled = true;
                    break;
                }
            }

            var batchFailures = BatchFailures
                .Select(static failure => new DesktopBatchItemFailure(failure.Index, failure.DisplayName, failure.ErrorMessage))
                .ToArray();
            WriteBatchDiagnosticsReport(
                "export",
                targets.Count,
                processedCount,
                successfulTargets.Count,
                wasCanceled,
                batchFailures,
                ("destinationFolder", destinationFolder),
                ("outputFormat", request.Format.ToString()));

            lastTargetPath = successfulTargets
                .OrderBy(static item => item.Key)
                .Select(static item => item.Value)
                .LastOrDefault();
            UpdateOperationProgress(processedCount, targets.Count, force: true);

            if (wasCanceled)
            {
                OperationStatusText = successfulTargets.Count == 0
                    ? "本次处理已取消。"
                    : $"本次处理已取消：已完成 {successfulTargets.Count}/{targets.Count}。";
                trace.Canceled(CreateDiagnosticsProperties(("processedCount", processedCount.ToString())));
                return;
            }

            var successCount = successfulTargets.Count;
            if (!hasMultipleTargets)
            {
                OperationStatusText = failureMessages.Count == 0 && !string.IsNullOrWhiteSpace(lastTargetPath)
                    ? $"{singleSuccessPrefix}：{Path.GetFileName(lastTargetPath)}"
                    : $"处理失败：{failureMessages.FirstOrDefault() ?? "未能生成输出文件"}";
                if (failureMessages.Count == 0)
                {
                    trace.Success(CreateDiagnosticsProperties(("successCount", successCount.ToString())));
                }
                else
                {
                    trace.Fail(new InvalidOperationException(failureMessages.FirstOrDefault() ?? "导出失败"));
                }
                return;
            }

            if (failureMessages.Count == 0)
            {
                OperationStatusText = $"{batchSuccessPrefix}：共 {successCount} 张，输出：{destinationFolder}";
                trace.Success(CreateDiagnosticsProperties(("successCount", successCount.ToString())));
                return;
            }

            if (successCount <= 0)
            {
                OperationStatusText = $"{batchSuccessPrefix}失败：{failureMessages[0]}";
                trace.Fail(
                    new InvalidOperationException(failureMessages[0]),
                    CreateDiagnosticsProperties(("failureCount", failureMessages.Count.ToString())));
                return;
            }

            OperationStatusText = $"{batchSuccessPrefix}：成功 {successCount}，失败 {failureMessages.Count}。首个失败：{failureMessages[0]}";
            trace.Fail(
                new InvalidOperationException(failureMessages[0]),
                CreateDiagnosticsProperties(
                    ("successCount", successCount.ToString()),
                    ("failureCount", failureMessages.Count.ToString())));
        }
        catch (OperationCanceledException)
        {
            OperationStatusText = "本次处理已取消。";
            trace.Canceled();
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(OperationStatusText))
            {
                BatchResultSummaryText = OperationStatusText;
            }

            if (ReferenceEquals(_currentOperationCts, operationCts))
            {
                _currentOperationCts = null;
                OnPropertyChanged(nameof(CanCancelOperation));
            }

            DesktopImageProcessingPolicy.TrimMemory();
            IsExportProcessing = false;
        }
    }

    internal static IReadOnlyList<DesktopExportExecutionPhasePlan> BuildExportExecutionPhases(
        IReadOnlyList<DesktopPreparedExportOperation> operations)
    {
        return BuildExportExecutionPhases(
            operations,
            DesktopImageProcessingPolicy.CurrentPerformanceMode,
            DesktopImageProcessingPolicy.ThreadLimit,
            DesktopImageProcessingPolicy.MagickOperationLimit,
            DesktopImageProcessingPolicy.GetMemoryPressureLevel());
    }

    internal static IReadOnlyList<DesktopExportExecutionPhasePlan> BuildExportExecutionPhases(
        IReadOnlyList<DesktopPreparedExportOperation> operations,
        DesktopProcessingPerformanceMode performanceMode,
        int threadLimit,
        int magickOperationLimit)
    {
        return BuildExportExecutionPhases(
            operations,
            performanceMode,
            threadLimit,
            magickOperationLimit,
            DesktopMemoryPressureLevel.Low);
    }

    private static IReadOnlyList<DesktopExportExecutionPhasePlan> BuildExportExecutionPhases(
        IReadOnlyList<DesktopPreparedExportOperation> operations,
        DesktopProcessingPerformanceMode performanceMode,
        int threadLimit,
        int magickOperationLimit,
        DesktopMemoryPressureLevel memoryPressureLevel)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Count == 0)
        {
            return [];
        }

        var phases = new List<DesktopExportExecutionPhasePlan>(capacity: 4);
        foreach (var workloadKind in EnumerateExportExecutionPhaseOrder())
        {
            var stepOrdinals = new List<int>();
            for (var index = 0; index < operations.Count; index++)
            {
                if (ResolveExportWorkloadKind(operations[index]) == workloadKind)
                {
                    stepOrdinals.Add(index);
                }
            }

            if (stepOrdinals.Count == 0)
            {
                continue;
            }

            var executionPlan = DesktopBatchParallelismAdvisor.CreateExecutionPlan(
                workloadKind,
                stepOrdinals.Count,
                SumOperationBytes(stepOrdinals.Select(stepOrdinal => operations[stepOrdinal])),
                performanceMode,
                threadLimit,
                magickOperationLimit,
                memoryPressureLevel);
            phases.Add(new DesktopExportExecutionPhasePlan(
                workloadKind,
                executionPlan,
                [.. stepOrdinals]));
        }

        return phases;
    }

    internal static DesktopBatchWorkloadKind ResolveExportWorkloadKind(DesktopPreparedExportOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return operation.Mode switch
        {
            DesktopPreparedExportMode.CopySource
                or DesktopPreparedExportMode.StripMetadataWithoutReencode
                or DesktopPreparedExportMode.TargetSizeCopy => DesktopBatchWorkloadKind.ExportPassthrough,
            DesktopPreparedExportMode.TargetSizeReencode => DesktopBatchWorkloadKind.ExportCompression,
            _ when operation.ExecutionRequest.Watermark is not null => DesktopBatchWorkloadKind.ExportWatermark,
            _ when operation.ExecutionRequest.TargetFileSizeBytes is > 0 => DesktopBatchWorkloadKind.ExportCompression,
            _ => DesktopBatchWorkloadKind.ExportTranscode
        };
    }

    private static IEnumerable<DesktopBatchWorkloadKind> EnumerateExportExecutionPhaseOrder()
    {
        yield return DesktopBatchWorkloadKind.ExportPassthrough;
        yield return DesktopBatchWorkloadKind.ExportTranscode;
        yield return DesktopBatchWorkloadKind.ExportCompression;
        yield return DesktopBatchWorkloadKind.ExportWatermark;
    }

    private static long SumOperationBytes(IEnumerable<DesktopPreparedExportOperation> operations)
    {
        return SumOperationBytes(operations.Select(static operation => operation.SourceInfo.SizeBytes));
    }

    private static long SumOperationBytes(IEnumerable<ImageListItemViewModel> items)
    {
        return SumOperationBytes(items.Select(static item => item.SizeBytes));
    }

    private static long SumOperationBytes(IEnumerable<long> sizes)
    {
        var totalBytes = 0L;
        foreach (var size in sizes)
        {
            var normalizedSize = Math.Max(0L, size);
            if (long.MaxValue - totalBytes < normalizedSize)
            {
                return long.MaxValue;
            }

            totalBytes += normalizedSize;
        }

        return totalBytes;
    }

    private void ClearBatchFailures()
    {
        if (BatchFailures.Count == 0)
        {
            return;
        }

        BatchFailures.Clear();
        OnPropertyChanged(nameof(HasBatchFailures));
    }

    private void AddBatchFailure(int index, string displayName, string errorMessage)
    {
        BatchFailures.Add(new BatchFailureViewModel(index, displayName, errorMessage));
        OnPropertyChanged(nameof(HasBatchFailures));
    }

    private DesktopWatermarkRequest? BuildWatermarkRequest()
    {
        if (SelectedWatermarkPlacement is null)
        {
            OperationStatusText = "请先选择水印位置。";
            return null;
        }

        if (!TryParsePercent(WatermarkOpacityText, out var opacityPercent))
        {
            OperationStatusText = "水印透明度请填写 1 到 100 之间的整数。";
            return null;
        }

        if (!TryParseNonNegativeInt(WatermarkMarginText, out var marginPixels))
        {
            OperationStatusText = "水印边距请填写 0 或正整数。";
            return null;
        }

        if (UseImageWatermark)
        {
            if (string.IsNullOrWhiteSpace(WatermarkImagePath) || !File.Exists(WatermarkImagePath))
            {
                OperationStatusText = "请先选择一张可用的图片水印。";
                return null;
            }

            if (!TryParsePercent(WatermarkImageScaleText, out var imageScalePercent))
            {
                OperationStatusText = "图片水印大小请填写 1 到 100 之间的整数。";
                return null;
            }

            return new DesktopWatermarkRequest(
                DesktopWatermarkKind.Image,
                SelectedWatermarkPlacement.Value,
                string.Empty,
                WatermarkImagePath,
                opacityPercent,
                marginPixels,
                TextPointSize: 42,
                WatermarkTextColor,
                imageScalePercent);
        }

        if (string.IsNullOrWhiteSpace(WatermarkText))
        {
            OperationStatusText = "请先填写文字水印内容。";
            return null;
        }

        if (!TryParsePositiveInt(WatermarkTextSizeText, out var textPointSize))
        {
            OperationStatusText = "文字水印字号请填写正整数。";
            return null;
        }

        return new DesktopWatermarkRequest(
            DesktopWatermarkKind.Text,
            SelectedWatermarkPlacement.Value,
            WatermarkText,
            string.Empty,
            opacityPercent,
            marginPixels,
            Math.Clamp(textPointSize, 8, 256),
            WatermarkTextColor,
            ImageScalePercent: 18);
    }

    private bool IsWatermarkInputValid()
    {
        if (SelectedWatermarkPlacement is null
            || !TryParsePercent(WatermarkOpacityText, out _)
            || !TryParseNonNegativeInt(WatermarkMarginText, out _))
        {
            return false;
        }

        if (UseImageWatermark)
        {
            return !string.IsNullOrWhiteSpace(WatermarkImagePath)
                && File.Exists(WatermarkImagePath)
                && TryParsePercent(WatermarkImageScaleText, out _);
        }

        return !string.IsNullOrWhiteSpace(WatermarkText)
            && TryParsePositiveInt(WatermarkTextSizeText, out _);
    }

    private void NotifyWatermarkStateChanged()
    {
        OnPropertyChanged(nameof(CanRunWatermark));
        OnPropertyChanged(nameof(WatermarkActionText));
        OnPropertyChanged(nameof(WatermarkSummaryText));
    }

    private DesktopExportRequest? BuildExportRequest(
        ExportImageFormat format,
        bool stripMetadata,
        bool preserveEncodedData,
        long? targetFileSizeBytes = null,
        int? overrideLongEdge = null,
        int? overrideJpegQuality = null,
        DesktopWatermarkRequest? watermark = null)
    {
        int jpegQuality;
        if (overrideJpegQuality.HasValue)
        {
            jpegQuality = Math.Clamp(overrideJpegQuality.Value, 1, 100);
        }
        else if (!TryParseJpegQuality(ExportJpegQualityText, out jpegQuality))
        {
            OperationStatusText = "质量请填 1 到 100 之间的整数。";
            return null;
        }

        int? longEdgePixels;
        if (overrideLongEdge.HasValue)
        {
            longEdgePixels = overrideLongEdge.Value > 0 ? overrideLongEdge.Value : null;
        }
        else if (!LimitExportLongEdge)
        {
            longEdgePixels = null;
        }
        else if (!TryParsePositiveInt(ExportLongEdgeText, out var parsedLongEdge))
        {
            OperationStatusText = "长边像素请填一个正整数。";
            return null;
        }
        else
        {
            longEdgePixels = parsedLongEdge;
        }

        return new DesktopExportRequest(
            format,
            longEdgePixels,
            jpegQuality,
            stripMetadata,
            PreserveEncodedData: preserveEncodedData,
            FallbackFormat: ExportImageFormat.Jpeg,
            TargetFileSizeBytes: targetFileSizeBytes,
            Watermark: watermark);
    }

    private string BuildTargetPath(
        string sourcePath,
        string targetExtension,
        string collisionSuffixLabel,
        string destinationFolder,
        string? targetBaseName,
        ISet<string> reservedTargetPaths)
    {
        return ExportPathBuilder.BuildAvailableTargetPath(
            sourcePath,
            destinationFolder,
            targetExtension,
            collisionSuffixLabel,
            targetBaseName,
            reservedTargetPaths);
    }

    private void NotifyOperationStateChanged()
    {
        OnPropertyChanged(nameof(ResolvedExportDestinationFolder));
        OnPropertyChanged(nameof(CanRunExport));
        OnPropertyChanged(nameof(CanRunCompress));
        OnPropertyChanged(nameof(CanRunCleanMetadata));
        OnPropertyChanged(nameof(CanRunWatermark));
        OnPropertyChanged(nameof(CanRunExifEdit));
        OnPropertyChanged(nameof(CanCancelOperation));
        OnPropertyChanged(nameof(HasBatchFailures));
        OnPropertyChanged(nameof(OperationScopeSummaryText));
        OnPropertyChanged(nameof(ExportActionText));
        OnPropertyChanged(nameof(CompressActionText));
        OnPropertyChanged(nameof(CompressButtonText));
        OnPropertyChanged(nameof(CompressionModeSummaryText));
        OnPropertyChanged(nameof(ShowCompressionPresetSettings));
        OnPropertyChanged(nameof(ShowTargetSizeCompressionSettings));
        OnPropertyChanged(nameof(CleanMetadataActionText));
        OnPropertyChanged(nameof(WatermarkActionText));
        OnPropertyChanged(nameof(WatermarkSummaryText));
        OnPropertyChanged(nameof(ShowTextWatermarkSettings));
        OnPropertyChanged(nameof(ShowImageWatermarkSettings));
        OnPropertyChanged(nameof(ExifEditActionText));
        OnPropertyChanged(nameof(ExifEditSummaryText));
        OnPropertyChanged(nameof(CanRunRename));
        OnPropertyChanged(nameof(RenameActionText));
        OnPropertyChanged(nameof(RenamePreviewText));
        OnPropertyChanged(nameof(RenameExportOutputs));
        OnPropertyChanged(nameof(ExportRenamePreviewText));
        OnPropertyChanged(nameof(CopyToFolderActionText));
        OnPropertyChanged(nameof(MoveToFolderActionText));
        OnPropertyChanged(nameof(RecycleToTrashActionText));
        OnPropertyChanged(nameof(CanRecycleToTrash));
        NotifyImageMatchStateChanged();
        NotifyCompareStateChanged();
        NotifyNavigationStateChanged();
        NotifyReviewStateChanged();
        NotifySlideshowStateChanged();
    }

    private int GetOperationTargetCount()
    {
        return ResolveOperationTargets().Count;
    }

    private IReadOnlyList<ImageListItemViewModel> ResolveOperationTargets()
    {
        if (ProcessCurrentCollection)
        {
            return Images.ToList();
        }

        if (SelectedImages.Count > 0)
        {
            return SelectedImages.ToList();
        }

        return SelectedImage is null ? [] : [SelectedImage];
    }

    private string ResolveDestinationFolderForRun()
    {
        var resolvedPath = ResolvedExportDestinationFolder;
        if (!string.IsNullOrWhiteSpace(resolvedPath))
        {
            return resolvedPath;
        }

        var picturesFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return string.IsNullOrWhiteSpace(picturesFolder) ? Environment.CurrentDirectory : picturesFolder;
    }

    private void UpdateSuggestedExportDestinationFolder()
    {
        var suggestedPath = BuildSuggestedExportDestinationFolder();
        if (string.Equals(_suggestedExportDestinationFolder, suggestedPath, PathComparison.Comparison))
        {
            return;
        }

        _suggestedExportDestinationFolder = suggestedPath;
        OnPropertyChanged(nameof(ResolvedExportDestinationFolder));
    }

    private string BuildSuggestedExportDestinationFolder()
    {
        var sourcePath = SelectedImage?.FullPath
            ?? Images.FirstOrDefault()?.FullPath
            ?? _allImages.FirstOrDefault()?.FullPath;
        var baseDirectory = Path.GetDirectoryName(sourcePath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        }

        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            baseDirectory = Environment.CurrentDirectory;
        }

        return ProcessCurrentCollection && Images.Count > 1
            ? Path.Combine(baseDirectory, $"批量处理_{DateTime.Now:yyyyMMdd_HHmm}")
            : baseDirectory;
    }

    private static bool TryParsePositiveInt(string text, out int value)
    {
        return int.TryParse(text, out value) && value > 0;
    }

    private static bool TryParsePositiveLong(string text, out long value)
    {
        return long.TryParse(text, out value) && value > 0;
    }

    private static bool TryParseNonNegativeInt(string text, out int value)
    {
        return int.TryParse(text, out value) && value >= 0;
    }

    private static bool TryParsePercent(string text, out int value)
    {
        return int.TryParse(text, out value) && value is >= 1 and <= 100;
    }

    private static bool TryParseJpegQuality(string text, out int value)
    {
        return int.TryParse(text, out value) && value is >= 1 and <= 100;
    }

    private SequentialRenamePattern? GetExportRenamePatternOrNull()
    {
        if (!RenameExportOutputs)
        {
            return null;
        }

        return BuildRenamePatternOrNull(showValidationMessage: true);
    }

    private string GetPreviewExportExtension()
    {
        if (SelectedExportFormat is null || SelectedExportFormat.Value == ExportImageFormat.Original)
        {
            return "[原扩展名]";
        }

        return SelectedExportFormat.Value switch
        {
            ExportImageFormat.Jpeg => ".jpg",
            ExportImageFormat.Png => ".png",
            ExportImageFormat.WebP => ".webp",
            ExportImageFormat.Avif => ".avif",
            ExportImageFormat.Tiff => ".tif",
            ExportImageFormat.Bmp => ".bmp",
            ExportImageFormat.Jxl => ".jxl",
            ExportImageFormat.Heic => ".heic",
            _ => "[原扩展名]"
        };
    }

    private ExportImageFormat ResolveCompressionOutputFormat()
    {
        var selectedFormat = SelectedExportFormat?.Value ?? ExportImageFormat.Original;
        return DesktopImageExportService.SupportsTargetSizeCompression(selectedFormat)
            ? selectedFormat
            : ExportImageFormat.Jpeg;
    }

    private string BuildCompressionFormatSummary()
    {
        var selectedFormat = SelectedExportFormat?.Value ?? ExportImageFormat.Original;
        var outputFormat = ResolveCompressionOutputFormat();
        var outputLabel = GetExportFormatLabel(outputFormat);

        if (selectedFormat == outputFormat)
        {
            return $"输出 {outputLabel}";
        }

        return $"输出 {outputLabel}（当前输出格式不适合压缩，已自动使用 JPG）";
    }

    private static string GetExportFormatLabel(ExportImageFormat format)
    {
        return format switch
        {
            ExportImageFormat.Original => "原格式",
            ExportImageFormat.Jpeg => "JPG",
            ExportImageFormat.Png => "PNG",
            ExportImageFormat.WebP => "WEBP",
            ExportImageFormat.Avif => "AVIF",
            ExportImageFormat.Tiff => "TIFF",
            ExportImageFormat.Bmp => "BMP",
            ExportImageFormat.Jxl => "JXL",
            ExportImageFormat.Heic => "HEIC",
            _ => "JPG"
        };
    }

    private void NotifyExportSelectionStateChanged()
    {
        OnPropertyChanged(nameof(CanRunExport));
        OnPropertyChanged(nameof(CanRunCompress));
        OnPropertyChanged(nameof(CanRunCleanMetadata));
        OnPropertyChanged(nameof(CanRunWatermark));
        OnPropertyChanged(nameof(CanRunExifEdit));
        OnPropertyChanged(nameof(OperationScopeSummaryText));
        OnPropertyChanged(nameof(ExportActionText));
        OnPropertyChanged(nameof(CompressActionText));
        OnPropertyChanged(nameof(CompressButtonText));
        OnPropertyChanged(nameof(CleanMetadataActionText));
        OnPropertyChanged(nameof(WatermarkActionText));
        OnPropertyChanged(nameof(WatermarkSummaryText));
        OnPropertyChanged(nameof(ExifEditActionText));
        OnPropertyChanged(nameof(ExifEditSummaryText));
        OnPropertyChanged(nameof(CanRunRename));
        OnPropertyChanged(nameof(RenameActionText));
        OnPropertyChanged(nameof(RenamePreviewText));
        OnPropertyChanged(nameof(ExportRenamePreviewText));
    }
}




