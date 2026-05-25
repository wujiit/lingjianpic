using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using ModernImageViewer.Core;
using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private const int ThumbnailLoadGateCount = 3;
    private const int PreviewBitmapCacheCapacity = 6;
    private const int ThumbnailWarmupTrimInterval = 12;
    private const int PreviewWarmupTrimInterval = 2;
    private const int PreviewWarmupNeighborCount = 4;
    private const int PreviewWarmupBitmapNeighborCount = 2;
    private const int PreviewWarmupMinimumLongEdge = 1400;
    private const int PreviewWarmupLargeSourceMaximumLongEdge = 1800;
    private const int PreviewWarmupMaximumLongEdge = 2200;
    private const long PreviewWarmupLargeSourceSizeBytes = 24L * 1024L * 1024L;
    private const long PreviewWarmupHugeSourceSizeBytes = 48L * 1024L * 1024L;
    private const int PreviewReloadDebounceMilliseconds = 90;
    private const int ProgressiveCollectionBatchSize = 160;
    private const int ProgressiveCollectionMinimumInputCount = 180;
    private const int ProgressiveCollectionThumbnailRefreshInterval = 3;
    private const int ProgressiveCollectionThumbnailRefreshLargeInputCount = 960;
    private const int ProgressiveCollectionThumbnailRefreshHugeInputCount = 2400;

    private readonly ImageCollectionBuilder _collectionBuilder = new();
    private readonly List<ImageListItemViewModel> _allImages = [];
    private readonly PreviewImageService _previewImageService = new();
    private readonly DesktopPreviewBitmapCache _previewBitmapCache = new(PreviewBitmapCacheCapacity);
    private readonly DesktopShellService _desktopShellService = new();
    private readonly DesktopTrashService _desktopTrashService = new();
    private readonly ReadOnlyCollection<SortModeOption> _sortModes;
    private readonly SemaphoreSlim _thumbnailLoadGate = new(ThumbnailLoadGateCount, ThumbnailLoadGateCount);
    private readonly SynchronizationContext? _uiSynchronizationContext = SynchronizationContext.Current;
    private IReadOnlyList<string> _lastInputs = Array.Empty<string>();
    private SortModeOption _selectedSortMode;
    private ImageListItemViewModel? _selectedImage;
    private Bitmap? _selectedPreviewBitmap;
    private bool _isRefreshingImageCollection;
    private CancellationTokenSource? _thumbnailLoadCts;
    private CancellationTokenSource? _previewLoadCts;
    private CancellationTokenSource? _previewReloadCts;
    private CancellationTokenSource? _previewWarmupCts;
    private CancellationTokenSource? _searchFilterApplyCts;
    private CancellationTokenSource? _collectionLoadCts;
    private bool _includeSubfolders;
    private bool _isDisposed;
    private string _statusText = string.Empty;
    private string _sourceText = string.Empty;
    private string _selectedPreviewStatusText = string.Empty;
    private string _selectedPreviewDetailsText = string.Empty;
    private int _selectedPreviewDecodeLongEdge = 1600;
    private string? _selectedPreviewPath;
    private string? _pendingPreviewPath;
    private int _pendingPreviewDecodeLongEdge;
    private readonly object _backgroundTaskSync = new();
    private readonly HashSet<Task> _backgroundTasks = [];
    private readonly object _trackedOperationSync = new();
    private TaskCompletionSource<object?> _trackedOperationsIdleTcs;
    private int _trackedOperationCount;
    private int _shutdownPreparationStarted;

    private readonly record struct CollectionLoadChannelMessage(
        ImageCollectionBuildBatch? Batch,
        ImageCollectionResult? Result,
        Exception? Error)
    {
        public static CollectionLoadChannelMessage FromBatch(ImageCollectionBuildBatch batch) => new(batch, null, null);

        public static CollectionLoadChannelMessage FromResult(ImageCollectionResult result) => new(null, result, null);

        public static CollectionLoadChannelMessage FromError(Exception error) => new(null, null, error);
    }

    public MainWindowViewModel()
    {
        _trackedOperationsIdleTcs = CreateSignalTaskCompletionSource(completed: true);
        _sortModes =
        [
            new SortModeOption(SortMode.Name, "按名称"),
            new SortModeOption(SortMode.Modified, "按修改时间"),
            new SortModeOption(SortMode.Size, "按体积")
        ];
        _selectedSortMode = _sortModes[0];
        InitializePerformanceSettings();
        SupportedFormatsText = "支持 JPG、PNG、WEBP、HEIC、AVIF、RAW 等常见格式。";
        InitializeFilterSettings();
        InitializeReviewSettings();
        InitializeExportSettings();
        InitializeRenameSettings();
        InitializeImageMatchSettings();
        InitializeSlideshowSettings();
        InitializeRecentSessions();
        InitializeViewerSettings();
    }

    public ObservableCollection<ImageListItemViewModel> Images { get; } = [];

    public IReadOnlyList<SortModeOption> SortModes => _sortModes;

    public SortModeOption SelectedSortMode
    {
        get => _selectedSortMode;
        set
        {
            if (!SetProperty(ref _selectedSortMode, value))
            {
                return;
            }

            ApplySortModeInCurrentCollection();
            ScheduleViewerSettingsSave();
        }
    }

    public bool IncludeSubfolders
    {
        get => _includeSubfolders;
        set
        {
            if (!SetProperty(ref _includeSubfolders, value))
            {
                return;
            }

            _ = ReloadAsync();
            ScheduleViewerSettingsSave();
        }
    }

    public ImageListItemViewModel? SelectedImage
    {
        get => _selectedImage;
        set
        {
            if (!SetProperty(ref _selectedImage, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedFileNameText));
            OnPropertyChanged(nameof(SelectedPathText));
            OnPropertyChanged(nameof(SelectedMetaText));
            OnPropertyChanged(nameof(SelectedFolderText));
            OnPropertyChanged(nameof(SelectedFormatText));
            OnPropertyChanged(nameof(SelectedExifStatusText));
            OnPropertyChanged(nameof(ViewerHeadlineText));
            OnPropertyChanged(nameof(ViewerSubtitleText));
            OnPropertyChanged(nameof(ViewerPathText));
            OnPropertyChanged(nameof(CanOpenContainingFolder));
            OnPropertyChanged(nameof(CanRecycleToTrash));
            SyncSelectedImagesFromPrimary();
            OnInspectorSelectionChanged(value);

            if (value is not null)
            {
                _ = EnsureThumbnailLoadedAsync(value, CancellationToken.None);
            }

            LoadPreviewToolState(value);
            QueueSelectedPreviewLoad();
            OnRenameSelectionChanged(value);
            OnExportSelectionChanged(value);
            OnReviewSelectionChanged(value);
            NotifyPreviewToolStateChanged();
            NotifyCompareStateChanged();
            NotifyNavigationStateChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string SourceText
    {
        get => _sourceText;
        private set => SetProperty(ref _sourceText, value);
    }

    public string SupportedFormatsText { get; }

    public Bitmap? SelectedPreviewBitmap
    {
        get => _selectedPreviewBitmap;
        private set
        {
            if (ReferenceEquals(_selectedPreviewBitmap, value))
            {
                return;
            }

            var previousBitmap = _selectedPreviewBitmap;
            _selectedPreviewBitmap = value;
            if (previousBitmap is not null
                && !ReferenceEquals(previousBitmap, value)
                && !_previewBitmapCache.ContainsBitmap(previousBitmap))
            {
                previousBitmap.Dispose();
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedPreview));
            OnPropertyChanged(nameof(ShowSelectedPreviewPlaceholder));
            OnPropertyChanged(nameof(HasLongImageCandidate));
            OnPropertyChanged(nameof(ShowLongImageModeButton));
            OnPropertyChanged(nameof(CanToggleLongImageMode));
            OnPropertyChanged(nameof(LongImageModeButtonText));
            OnPropertyChanged(nameof(SelectedPreviewDisplayWidth));
            OnPropertyChanged(nameof(SelectedPreviewDisplayHeight));
            OnPropertyChanged(nameof(SelectedPreviewImageDisplayWidth));
            OnPropertyChanged(nameof(SelectedPreviewImageDisplayHeight));
            OnPropertyChanged(nameof(SelectedDimensionsText));
            OnPropertyChanged(nameof(PreviewZoomText));
            OnPropertyChanged(nameof(PreviewTransformSummaryText));
        }
    }

    public bool HasSelectedPreview => SelectedPreviewBitmap is not null;

    public bool ShowSelectedPreviewPlaceholder => SelectedPreviewBitmap is null && !IsSelectedPreviewLoading;

    public string SelectedPreviewStatusText
    {
        get => _selectedPreviewStatusText;
        private set => SetProperty(ref _selectedPreviewStatusText, value);
    }

    public string SelectedPreviewDetailsText
    {
        get => _selectedPreviewDetailsText;
        private set => SetProperty(ref _selectedPreviewDetailsText, value);
    }

    public string SelectedFileNameText => SelectedImage?.FileName ?? string.Empty;

    public string SelectedPathText => SelectedImage?.FullPath ?? string.Empty;

    public string SelectedMetaText => SelectedImage is null
        ? string.Empty
        : $"{SelectedImage.SizeText} / {SelectedImage.ModifiedText}";

    public string ViewerHeadlineText => ShowCompareViewerSurface
        ? "对比模式"
        : ShowContactSheetViewer
            ? "联系表"
            : SelectedFileNameText;

    public string ViewerSubtitleText => ShowCompareViewerSurface
        ? CompareSummaryText
        : ShowContactSheetViewer
            ? ContactSheetSummaryText
            : SelectedMetaText;

    public string ViewerPathText => ShowCompareViewerSurface
        ? "当前对比区会保留你加入的图片，并跟随统一缩放。"
        : ShowContactSheetViewer
            ? "点任意缩略图会直接回到单图预览。"
            : SelectedPathText;

    public async Task LoadInputsAsync(
        IEnumerable<string> inputPaths,
        CancellationToken cancellationToken = default,
        string? preferredFocusPath = null)
    {
        _lastInputs = inputPaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(PathComparison.Comparer)
            .ToArray();

        if (_lastInputs.Count == 0)
        {
            ClearImages();
            SelectedImage = null;
            StatusText = "没有拿到可读取的本地路径。";
            SourceText = "选择图片或文件夹开始处理。";
            SetSelectedPreview(null, string.Empty, string.Empty);
            OnImageCollectionChanged();
            return;
        }

        using var trackedOperation = BeginTrackedOperation();
        using var collectionLoadCts = BeginCollectionLoadScope(cancellationToken);
        var operationToken = collectionLoadCts.Token;
        var shouldResumeBackgroundWarmup = false;
        StatusText = "正在读取图片...";
        SourceText = "正在整理文件并生成列表。";
        using var trace = BeginDiagnosticsOperation(
            "collection",
            "load-inputs",
            ("inputCount", _lastInputs.Count.ToString()),
            ("includeSubfolders", IncludeSubfolders.ToString()),
            ("sortMode", SelectedSortMode.Value.ToString()),
            ("preferredFocusPath", preferredFocusPath));

        try
        {
            ClearImages();
            var useProgressiveLoad = ShouldUseProgressiveCollectionLoad(_lastInputs);
            var progressiveBatchCount = 0;
            ImageCollectionResult result;

            if (useProgressiveLoad)
            {
                (result, progressiveBatchCount) = await LoadInputsProgressivelyAsync(
                    _lastInputs,
                    preferredFocusPath,
                    operationToken);
            }
            else
            {
                result = await Task.Run(
                    () => _collectionBuilder.Build(_lastInputs, SelectedSortMode.Value, IncludeSubfolders, operationToken),
                    operationToken);

                _allImages.AddRange(result.Images.Select(static image => new ImageListItemViewModel(image)));
                ApplyReviewStates(_allImages);
                RefreshFormatFilterOptions();
            }

            var focusPath = string.IsNullOrWhiteSpace(preferredFocusPath)
                ? result.FocusPath
                : preferredFocusPath;
            FinalizeLoadedCollection(result, focusPath);
            if (result.Images.Count > 0)
            {
                RegisterRecentSession(_lastInputs, result.SourceLabel);
            }

            shouldResumeBackgroundWarmup = true;

            SourceText = $"来源：{result.SourceLabel}";
            StatusText = result.Images.Count == 0
                ? "没有找到受支持的图片。"
                : $"已读取 {result.Images.Count} 张图片。";
            trace.Success(CreateDiagnosticsProperties(
                ("imageCount", result.Images.Count.ToString()),
                ("sourceLabel", result.SourceLabel),
                ("focusPath", result.FocusPath),
                ("progressiveLoad", useProgressiveLoad.ToString()),
                ("progressiveBatchCount", progressiveBatchCount.ToString())));

        }
        catch (OperationCanceledException)
        {
            StatusText = "读取已取消。";
            trace.Canceled();
        }
        catch (Exception ex)
        {
            ClearImages();
            SelectedImage = null;
            StatusText = $"读取失败：{ex.Message}";
            SourceText = "请检查路径、权限或图片格式后重试。";
            SetSelectedPreview(null, "预览失败", "请重新载入后再试。");
            OnImageCollectionChanged();
            trace.Fail(ex);
        }
        finally
        {
            CompleteCollectionLoadScope(collectionLoadCts);
            if (shouldResumeBackgroundWarmup)
            {
                ResumeDeferredImageWarmup();
            }
        }
    }

    private async Task<(ImageCollectionResult Result, int BatchCount)> LoadInputsProgressivelyAsync(
        IReadOnlyList<string> inputPaths,
        string? preferredFocusPath,
        CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<CollectionLoadChannelMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        var worker = Task.Run(() =>
        {
            try
            {
                var result = _collectionBuilder.BuildProgressive(
                    inputPaths,
                    SelectedSortMode.Value,
                    IncludeSubfolders,
                    batch => channel.Writer.TryWrite(CollectionLoadChannelMessage.FromBatch(batch)),
                    ProgressiveCollectionBatchSize,
                    cancellationToken);
                channel.Writer.TryWrite(CollectionLoadChannelMessage.FromResult(result));
            }
            catch (Exception ex)
            {
                channel.Writer.TryWrite(CollectionLoadChannelMessage.FromError(ex));
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, cancellationToken);

        ImageCollectionResult? finalResult = null;
        var batchCount = 0;

        await foreach (var message in channel.Reader.ReadAllAsync(cancellationToken))
        {
            if (message.Error is not null)
            {
                ExceptionDispatchInfo.Capture(message.Error).Throw();
            }

            if (message.Batch is not null)
            {
                batchCount++;
                var shouldRefreshThumbnails = ShouldRefreshProgressiveThumbnails(
                    batchCount,
                    message.Batch.TotalImageCount,
                    IsSelectedPreviewLoading || _previewLoadCts is not null);
                AppendProgressiveCollectionBatch(message.Batch, preferredFocusPath, shouldRefreshThumbnails);
                await Task.Yield();
            }

            if (message.Result is not null)
            {
                finalResult = message.Result;
            }
        }

        await worker;

        return (finalResult ?? throw new InvalidOperationException("Incremental collection load completed without a result."), batchCount);
    }

    private void AppendProgressiveCollectionBatch(
        ImageCollectionBuildBatch batch,
        string? preferredFocusPath,
        bool shouldRefreshThumbnails)
    {
        if (batch.Images.Count == 0)
        {
            return;
        }

        var createdItems = batch.Images
            .Select(static image => new ImageListItemViewModel(image))
            .ToArray();
        ApplyReviewStates(createdItems);
        _allImages.AddRange(createdItems);
        RefreshFormatFilterOptions();

        var focusPath = string.IsNullOrWhiteSpace(preferredFocusPath)
            ? SelectedImage?.FullPath ?? createdItems[0].FullPath
            : preferredFocusPath;
        var hasActiveFilters = HasActiveFilterCriteria(
            SearchText,
            SelectedFormatFilter,
            SelectedSizeFilter,
            SelectedReviewFilter,
            SelectedRatingFilter);

        if (hasActiveFilters)
        {
            ApplyFiltersCore(focusPath);
        }
        else
        {
            var nextSelectedImage = SelectedImage
                ?? createdItems.FirstOrDefault(item => string.Equals(item.FullPath, focusPath, PathComparison.Comparison))
                ?? createdItems[0];

            _isRefreshingImageCollection = true;
            try
            {
                foreach (var item in createdItems)
                {
                    Images.Add(item);
                }

                if (!string.Equals(SelectedImage?.FullPath, nextSelectedImage.FullPath, PathComparison.Comparison))
                {
                    SelectedImage = nextSelectedImage;
                }
            }
            finally
            {
                _isRefreshingImageCollection = false;
            }

            NotifyFilterStateChanged();
            OnImageCollectionChanged();
            if (shouldRefreshThumbnails)
            {
                QueueThumbnailWarmup();
            }
        }

        StatusText = $"正在读取图片... 已发现 {batch.TotalImageCount} 张，继续补全中。";
    }

    private void FinalizeLoadedCollection(ImageCollectionResult result, string? focusPath)
    {
        if (_allImages.Count > 1)
        {
            var sortedItems = SortImageItemsForMode(_allImages, SelectedSortMode.Value);
            var isSameOrder = _allImages.Count == sortedItems.Length
                && _allImages.Zip(sortedItems, static (left, right) => string.Equals(left.FullPath, right.FullPath, PathComparison.Comparison))
                    .All(static isSame => isSame);

            if (!isSameOrder)
            {
                _allImages.Clear();
                _allImages.AddRange(sortedItems);
            }
        }

        RefreshFormatFilterOptions();
        ApplyFilters(focusPath);
    }

    private bool ShouldUseProgressiveCollectionLoad(IReadOnlyList<string> inputPaths)
    {
        return IncludeSubfolders
            || inputPaths.Count >= ProgressiveCollectionMinimumInputCount
            || inputPaths.Any(Directory.Exists);
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        if (_lastInputs.Count == 0)
        {
            StatusText = "还没有可重读的输入。";
            return;
        }

        await LoadInputsAsync(_lastInputs, cancellationToken);
    }

    private void ApplySortModeInCurrentCollection()
    {
        if (_allImages.Count == 0)
        {
            return;
        }

        var focusPath = SelectedImage?.FullPath;
        var sortedItems = SortImageItemsForMode(_allImages, SelectedSortMode.Value);
        var isSameOrder = _allImages.Count == sortedItems.Length
            && _allImages.Zip(sortedItems, static (left, right) => string.Equals(left.FullPath, right.FullPath, PathComparison.Comparison))
                .All(static isSame => isSame);
        if (isSameOrder)
        {
            return;
        }

        _allImages.Clear();
        _allImages.AddRange(sortedItems);
        ApplyFilters(focusPath);
    }

    public void SetStatusMessage(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            StatusText = message;
        }
    }

    public void PrepareForShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownPreparationStarted, 1) == 1)
        {
            return;
        }

        WriteDiagnosticsInfo(
            "app",
            "shutdown.prepare",
            ("hasCollectionLoad", (_collectionLoadCts is not null).ToString()),
            ("hasThumbnailLoad", (_thumbnailLoadCts is not null).ToString()),
            ("hasPreviewLoad", (_previewLoadCts is not null).ToString()),
            ("hasPreviewReload", (_previewReloadCts is not null).ToString()),
            ("hasCompareLoad", (_compareLoadCts is not null).ToString()),
            ("hasOperation", (_currentOperationCts is not null).ToString()),
            ("backgroundTaskCount", GetPendingBackgroundTaskCount().ToString()),
            ("trackedOperationCount", GetTrackedOperationCount().ToString()));
        _settingsPersistTimer.Stop();
        StopSlideshow();
        CancelQuietly(_collectionLoadCts);
        CancelQuietly(_thumbnailLoadCts);
        CancelQuietly(_previewLoadCts);
        var currentOperationCts = _currentOperationCts;
        _currentOperationCts = null;
        CancelQuietly(currentOperationCts);
        OnPropertyChanged(nameof(CanCancelOperation));
        CancelQuietly(_searchFilterApplyCts);
        CancelPreviewReload();
        CancelPreviewWarmup();
        CancelPreviewLoad();
        CancelInspectorMetadataLoad();
        CancelComparePreviewLoading();
    }

    public async Task PrepareForShutdownAsync(CancellationToken cancellationToken = default)
    {
        PrepareForShutdown();

        try
        {
            await Task.WhenAll(
                WaitForTrackedOperationsAsync(cancellationToken),
                WaitForBackgroundTasksAsync(cancellationToken));
            await DesktopImageDimensionCacheStore.Shared.WaitForPendingPersistenceAsync().WaitAsync(cancellationToken);
            DesktopImageDimensionCacheStore.Shared.Persist();

            WriteDiagnosticsInfo(
                "app",
                "shutdown.drained",
                ("backgroundTaskCount", GetPendingBackgroundTaskCount().ToString()),
                ("trackedOperationCount", GetTrackedOperationCount().ToString()));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            WriteDiagnosticsWarning(
                "app",
                "shutdown.drain-timeout",
                ("backgroundTaskCount", GetPendingBackgroundTaskCount().ToString()),
                ("trackedOperationCount", GetTrackedOperationCount().ToString()));
            throw;
        }
        catch (Exception ex)
        {
                WriteDiagnosticsError(
                    "app",
                    "shutdown.drain-failed",
                    ex,
                    ("backgroundTaskCount", GetPendingBackgroundTaskCount().ToString()),
                    ("trackedOperationCount", GetTrackedOperationCount().ToString()));
                throw;
        }
        finally
        {
            DisposeShutdownCancellationScopes();
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        if (_uiSynchronizationContext is null)
        {
            try
            {
                using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                PrepareForShutdownAsync(shutdownCts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                WriteDiagnosticsWarning("app", "dispose.drain-failed", ("message", ex.Message));
                PrepareForShutdown();
            }
        }
        else
        {
            PrepareForShutdown();
        }

        DisposeShutdownCancellationScopes();
        DisposeViewerSettings();
        CancelPreviewReload();
        CancelPreviewWarmup();
        DisposeSlideshowTimer();
        DesktopImageDimensionCacheStore.Shared.Persist();
        _thumbnailLoadGate.Dispose();
        _compareLoadGate.Dispose();
        ClearImages();
        SelectedPreviewBitmap = null;
        _previewBitmapCache.Dispose();
    }

    private void ClearImages()
    {
        ResetSlideshowForCollectionReload();
        ResetCompareItemsForCollectionReload();
        SetSelectedPreview(null, string.Empty, string.Empty);
        _selectedPreviewDecodeLongEdge = 0;
        _pendingPreviewPath = null;
        _pendingPreviewDecodeLongEdge = 0;
        _previewBitmapCache.Clear();

        foreach (var image in _allImages)
        {
            image.Dispose();
        }

        _allImages.Clear();
        Images.Clear();
        ReplaceSelectedImages([]);
        RefreshFormatFilterOptions();
        CancelThumbnailWarmup();
    }

    private void QueueThumbnailWarmup()
    {
        CancelThumbnailWarmup();

        if (Images.Count == 0 || IsExportProcessing)
        {
            return;
        }

        _thumbnailLoadCts = new CancellationTokenSource();
        var token = _thumbnailLoadCts.Token;

        var plan = DesktopThumbnailWarmupAdvisor.CreatePlan(
            Images.Count,
            ShowContactSheetViewer,
            _layoutViewportHeight,
            ContactSheetColumns,
            _contactSheetViewportHeight,
            ContactSheetTileHeight);
        var isCollectionLoading = _collectionLoadCts is not null;
        var isForegroundPreviewLoading = IsSelectedPreviewLoading || _previewLoadCts is not null || _previewReloadCts is not null;
        var maxWarmupItemCount = CalculateDeferredThumbnailWarmupItemCount(
            plan.MaxItemCount,
            isForegroundPreviewLoading,
            isCollectionLoading);
        var prioritizedSelection = plan.PrioritizeSelection ? SelectedImage : null;
        var items = BuildThumbnailWarmupItems(Images, prioritizedSelection, maxWarmupItemCount);
        if (items.Length == 0)
        {
            DisposeQuietly(_thumbnailLoadCts);
            _thumbnailLoadCts = null;
            return;
        }

        var workerCount = CalculateThumbnailWarmupWorkerCount(
            items.Length,
            SumThumbnailWarmupBytes(items),
            DesktopImageProcessingPolicy.CurrentPerformanceMode,
            DesktopImageProcessingPolicy.ThreadLimit,
            ThumbnailLoadGateCount,
            isForegroundPreviewLoading,
            isCollectionLoading);
        for (var workerIndex = 0; workerIndex < workerCount; workerIndex++)
        {
            ForgetBackgroundTask(WarmThumbnailBatchAsync(items, workerIndex, workerCount, token));
        }
    }

    private void CancelThumbnailWarmup()
    {
        CancelQuietly(_thumbnailLoadCts);
        DisposeQuietly(_thumbnailLoadCts);
        _thumbnailLoadCts = null;
    }

    private static void CancelQuietly(CancellationTokenSource? cancellationTokenSource)
    {
        try
        {
            cancellationTokenSource?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void DisposeQuietly(CancellationTokenSource? cancellationTokenSource)
    {
        try
        {
            cancellationTokenSource?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void DisposeShutdownCancellationScopes()
    {
        DisposeAndClearCancellationSource(ref _collectionLoadCts);
        DisposeAndClearCancellationSource(ref _thumbnailLoadCts);
        DisposeAndClearCancellationSource(ref _previewLoadCts);
        DisposeAndClearCancellationSource(ref _previewReloadCts);
        DisposeAndClearCancellationSource(ref _previewWarmupCts);
        DisposeAndClearCancellationSource(ref _searchFilterApplyCts);
        DisposeAndClearCancellationSource(ref _inspectorMetadataLoadCts);
        DisposeAndClearCancellationSource(ref _compareLoadCts);
        DisposeAndClearCancellationSource(ref _currentOperationCts);
        OnPropertyChanged(nameof(CanCancelOperation));
    }

    private static void DisposeAndClearCancellationSource(ref CancellationTokenSource? cancellationTokenSource)
    {
        var source = cancellationTokenSource;
        cancellationTokenSource = null;
        DisposeQuietly(source);
    }

    private async Task WarmThumbnailBatchAsync(
        IReadOnlyList<ImageListItemViewModel> items,
        int startIndex,
        int step,
        CancellationToken cancellationToken)
    {
        var processedCount = 0;

        for (var index = startIndex; index < items.Count; index += step)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await EnsureThumbnailLoadedAsync(items[index], cancellationToken);
            processedCount++;

            if (processedCount % ThumbnailWarmupTrimInterval == 0)
            {
                DesktopImageProcessingPolicy.TrimMemory();
            }
        }
    }

    private async Task EnsureThumbnailLoadedAsync(ImageListItemViewModel item, CancellationToken cancellationToken)
    {
        if (IsExportProcessing)
        {
            return;
        }

        if (item.HasThumbnail || item.IsThumbnailLoading)
        {
            return;
        }

        item.SetThumbnailLoading(true);

        try
        {
            await _thumbnailLoadGate.WaitAsync(cancellationToken);
            try
            {
                var result = await Task.Run(
                    () => _previewImageService.LoadThumbnail(item.Signature, 160),
                    cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                {
                    result.Bitmap?.Dispose();
                    return;
                }

                item.SetThumbnail(result.Bitmap);
            }
            finally
            {
                _thumbnailLoadGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            item.SetThumbnail(null);
        }
        finally
        {
            item.SetThumbnailLoading(false);
        }
    }

    internal static ImageListItemViewModel[] BuildThumbnailWarmupItems(
        IReadOnlyList<ImageListItemViewModel> items,
        ImageListItemViewModel? selectedItem,
        int maxCount)
    {
        if (items.Count == 0 || maxCount <= 0)
        {
            return [];
        }

        var selectedIndex = -1;
        if (selectedItem is not null)
        {
            for (var index = 0; index < items.Count; index++)
            {
                if (ReferenceEquals(items[index], selectedItem))
                {
                    selectedIndex = index;
                    break;
                }
            }
        }

        var results = new List<ImageListItemViewModel>(Math.Min(items.Count, maxCount));
        if (selectedIndex < 0 || selectedIndex >= items.Count)
        {
            for (var index = 0; index < items.Count && results.Count < maxCount; index++)
            {
                var item = items[index];
                if (NeedsThumbnailWarmup(item))
                {
                    results.Add(item);
                }
            }

            return results.ToArray();
        }

        for (var distance = 0; results.Count < maxCount && results.Count < items.Count; distance++)
        {
            if (distance == 0)
            {
                var current = items[selectedIndex];
                if (NeedsThumbnailWarmup(current))
                {
                    results.Add(current);
                }

                continue;
            }

            var nextIndex = selectedIndex + distance;
            if (nextIndex < items.Count)
            {
                var nextItem = items[nextIndex];
                if (NeedsThumbnailWarmup(nextItem))
                {
                    results.Add(nextItem);
                    if (results.Count >= maxCount)
                    {
                        break;
                    }
                }
            }

            var previousIndex = selectedIndex - distance;
            if (previousIndex >= 0)
            {
                var previousItem = items[previousIndex];
                if (NeedsThumbnailWarmup(previousItem))
                {
                    results.Add(previousItem);
                }
            }

            if (nextIndex >= items.Count && previousIndex < 0)
            {
                break;
            }
        }

        return results.ToArray();
    }

    internal static bool NeedsThumbnailWarmup(ImageListItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return NeedsThumbnailWarmup(item.HasThumbnail, item.IsThumbnailLoading);
    }

    internal static bool NeedsThumbnailWarmup(bool hasThumbnail, bool isThumbnailLoading)
    {
        return !hasThumbnail && !isThumbnailLoading;
    }

    internal static int[] BuildThumbnailWarmupIndices(int totalCount, int selectedIndex, int maxCount)
    {
        if (totalCount <= 0 || maxCount <= 0)
        {
            return [];
        }

        var targetCount = Math.Min(totalCount, maxCount);
        if (selectedIndex < 0 || selectedIndex >= totalCount)
        {
            return Enumerable.Range(0, targetCount).ToArray();
        }

        var results = new List<int>(targetCount) { selectedIndex };
        for (var distance = 1; results.Count < targetCount; distance++)
        {
            var nextIndex = selectedIndex + distance;
            if (nextIndex < totalCount)
            {
                results.Add(nextIndex);
                if (results.Count == targetCount)
                {
                    break;
                }
            }

            var previousIndex = selectedIndex - distance;
            if (previousIndex >= 0)
            {
                results.Add(previousIndex);
            }
        }

        return results.ToArray();
    }

    internal static int CalculateThumbnailWarmupWorkerCount(int itemCount)
    {
        return CalculateThumbnailWarmupWorkerCount(
            itemCount,
            totalInputBytes: 0,
            DesktopImageProcessingPolicy.CurrentPerformanceMode,
            DesktopImageProcessingPolicy.ThreadLimit,
            ThumbnailLoadGateCount);
    }

    internal static int CalculateThumbnailWarmupWorkerCount(
        int itemCount,
        long totalInputBytes,
        DesktopProcessingPerformanceMode performanceMode,
        int threadLimit,
        int thumbnailLoadGateCount,
        bool isForegroundPreviewLoading = false,
        bool isCollectionLoading = false)
    {
        return DesktopThumbnailWarmupAdvisor.CalculateWorkerCount(
            itemCount,
            totalInputBytes,
            performanceMode,
            threadLimit,
            thumbnailLoadGateCount,
            isForegroundPreviewLoading,
            isCollectionLoading);
    }

    internal static int CalculateDeferredThumbnailWarmupItemCount(
        int plannedItemCount,
        bool isForegroundPreviewLoading,
        bool isCollectionLoading,
        DesktopMemoryPressureLevel? memoryPressureLevel = null)
    {
        return DesktopThumbnailWarmupAdvisor.CalculateDeferredItemLimit(
            plannedItemCount,
            isForegroundPreviewLoading,
            isCollectionLoading,
            memoryPressureLevel ?? DesktopImageProcessingPolicy.GetMemoryPressureLevel());
    }

    private static long SumThumbnailWarmupBytes(IEnumerable<ImageListItemViewModel> items)
    {
        var totalBytes = 0L;
        foreach (var item in items)
        {
            var normalizedSize = Math.Max(0L, item.SizeBytes);
            if (long.MaxValue - totalBytes < normalizedSize)
            {
                return long.MaxValue;
            }

            totalBytes += normalizedSize;
        }

        return totalBytes;
    }

    private void QueueSelectedPreviewLoad()
    {
        CancelPreviewReload();

        if (SelectedImage is null)
        {
            CancelPreviewLoad();
            CancelPreviewWarmup();
            IsSelectedPreviewLoading = false;
            _pendingPreviewPath = null;
            _pendingPreviewDecodeLongEdge = 0;

            if (_isRefreshingImageCollection)
            {
                return;
            }

            SetSelectedPreview(null, string.Empty, string.Empty);
            return;
        }

        if (IsExportProcessing)
        {
            CancelPreviewLoad();
            CancelPreviewWarmup();
            IsSelectedPreviewLoading = false;
            _pendingPreviewPath = null;
            _pendingPreviewDecodeLongEdge = 0;
            return;
        }

        var target = SelectedImage;
        var decodeLongEdge = CalculatePreviewDecodeLongEdge(
            _previewViewportWidth,
            _previewViewportHeight,
            IsPreviewFitMode,
            PreviewZoomPercent,
            GetCurrentPreviewContentWidth(),
            GetCurrentPreviewContentHeight(),
            ShouldUseLongImageWidthFit());

        if (CanReuseSelectedPreview(target.FullPath, _selectedPreviewPath, decodeLongEdge, _selectedPreviewDecodeLongEdge))
        {
            return;
        }

        if (CanReusePendingPreviewRequest(target.FullPath, _pendingPreviewPath, decodeLongEdge, _pendingPreviewDecodeLongEdge))
        {
            return;
        }

        if (TryUseCachedSelectedPreview(target, decodeLongEdge))
        {
            return;
        }

        var usedLowerResolutionCachedPreview = TryUseLowerResolutionCachedSelectedPreview(target, decodeLongEdge);

        CancelPreviewLoad();
        _previewLoadCts = new CancellationTokenSource();
        _pendingPreviewPath = target.FullPath;
        _pendingPreviewDecodeLongEdge = decodeLongEdge;
        IsSelectedPreviewLoading = true;

        if (SelectedPreviewBitmap is null || !string.Equals(_selectedPreviewPath, target.FullPath, PathComparison.Comparison))
        {
            SetSelectedPreview(null, "正在加载", string.Empty);
        }
        else if (usedLowerResolutionCachedPreview)
        {
            SelectedPreviewStatusText = "正在加载";
            SelectedPreviewDetailsText = AppendPreviewHint(SelectedPreviewDetailsText, "已先显示快速预览，稍后补清晰版本。");
        }

        ForgetBackgroundTask(LoadSelectedPreviewAsync(target, decodeLongEdge, _previewLoadCts.Token));
    }

    private void CancelPreviewLoad()
    {
        CancelQuietly(_previewLoadCts);
        DisposeQuietly(_previewLoadCts);
        _previewLoadCts = null;
        _pendingPreviewPath = null;
        _pendingPreviewDecodeLongEdge = 0;
    }

    private void CancelPreviewReload()
    {
        CancelQuietly(_previewReloadCts);
        DisposeQuietly(_previewReloadCts);
        _previewReloadCts = null;
    }

    private void PauseBackgroundImageLoading()
    {
        CancelThumbnailWarmup();
        CancelPreviewReload();
        CancelPreviewWarmup();
        CancelPreviewLoad();
        CancelComparePreviewLoading();
        IsSelectedPreviewLoading = false;
    }

    private void ResumeBackgroundImageLoading()
    {
        if (_isDisposed)
        {
            return;
        }

        if (SelectedImage is not null && SelectedPreviewBitmap is null)
        {
            QueueSelectedPreviewLoad();
        }

        QueueThumbnailWarmup();
    }

    private void QueuePreviewWarmup(int selectedPreviewLongEdge)
    {
        CancelPreviewWarmup();

        if (SelectedImage is null || Images.Count <= 1 || IsExportProcessing)
        {
            return;
        }

        if (ShouldDeferPreviewWarmup(_collectionLoadCts is not null, Images.Count))
        {
            return;
        }

        var warmupItems = BuildPreviewWarmupItems(Images, SelectedImage, PreviewWarmupNeighborCount);
        if (warmupItems.Length == 0)
        {
            return;
        }

        var warmupLongEdge = CalculatePreviewWarmupLongEdge(selectedPreviewLongEdge);
        if (warmupLongEdge <= 0)
        {
            return;
        }

        _previewWarmupCts = new CancellationTokenSource();
        ForgetBackgroundTask(WarmPreviewBatchAsync(warmupItems, warmupLongEdge, _previewWarmupCts.Token));
    }

    private void CancelPreviewWarmup()
    {
        CancelQuietly(_previewWarmupCts);
        DisposeQuietly(_previewWarmupCts);
        _previewWarmupCts = null;
    }

    private async Task WarmPreviewBatchAsync(
        IReadOnlyList<ImageListItemViewModel> items,
        int maxLongEdgePixels,
        CancellationToken cancellationToken)
    {
        var processedCount = 0;

        for (var warmupIndex = 0; warmupIndex < items.Count; warmupIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[warmupIndex];
            var warmupRequestedLongEdge = CalculatePreviewWarmupLongEdge(maxLongEdgePixels, item.SizeBytes);
            if (warmupRequestedLongEdge <= 0)
            {
                continue;
            }

            var warmupLoadPlan = _previewImageService.CreatePreviewLoadPlan(item.Signature, warmupRequestedLongEdge);
            var warmupDecodeLongEdge = warmupLoadPlan.InitialLongEdgePixels;
            if (warmupDecodeLongEdge <= 0)
            {
                continue;
            }

            if (_previewBitmapCache.TryGet(item.FullPath, warmupDecodeLongEdge, out _))
            {
                continue;
            }

            var shouldWarmBitmap = ShouldWarmPreviewBitmapCache(warmupIndex, item.SizeBytes);
            if (!shouldWarmBitmap)
            {
                await Task.Run(
                    () => _previewImageService.WarmPreview(item.Signature, warmupDecodeLongEdge),
                    cancellationToken);
            }
            else
            {
                var result = await Task.Run(
                    () => _previewImageService.LoadPreview(item.Signature, warmupDecodeLongEdge),
                    cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    result.Bitmap?.Dispose();
                    return;
                }

                if (result.Bitmap is not null)
                {
                    _previewBitmapCache.StoreOrUpdate(
                        item.FullPath,
                        warmupDecodeLongEdge,
                        result.Bitmap,
                        result.StatusText,
                        result.DetailsText);
                }
            }

            processedCount++;

            if (processedCount % PreviewWarmupTrimInterval == 0)
            {
                DesktopImageProcessingPolicy.TrimMemory();
            }
        }
    }

    private async Task LoadSelectedPreviewAsync(ImageListItemViewModel item, int maxLongEdgePixels, CancellationToken cancellationToken)
    {
        var hasExistingPreviewForPath = !string.IsNullOrWhiteSpace(_selectedPreviewPath)
            && string.Equals(_selectedPreviewPath, item.FullPath, PathComparison.Comparison)
            && _selectedPreviewDecodeLongEdge > 0;
        var loadPlan = hasExistingPreviewForPath
            ? new PreviewImageLoadPlan(maxLongEdgePixels, maxLongEdgePixels)
            : _previewImageService.CreatePreviewLoadPlan(item.Signature, maxLongEdgePixels);
        using var trace = BeginDiagnosticsOperation(
            "preview",
            "load-selected-preview",
            ("path", item.FullPath),
            ("extension", Path.GetExtension(item.FullPath)),
            ("maxLongEdgePixels", maxLongEdgePixels.ToString()),
            ("initialLongEdgePixels", loadPlan.InitialLongEdgePixels.ToString()),
            ("fileSizeBytes", item.SizeBytes.ToString()));
        try
        {
            var result = await Task.Run(
                () => _previewImageService.LoadPreview(item.Signature, loadPlan.InitialLongEdgePixels),
                cancellationToken);

            if (cancellationToken.IsCancellationRequested || !ReferenceEquals(item, SelectedImage))
            {
                result.Bitmap?.Dispose();
                return;
            }

            IsSelectedPreviewLoading = false;
            _pendingPreviewPath = null;
            _pendingPreviewDecodeLongEdge = 0;
            var detailsText = loadPlan.RequiresFollowUpReload
                ? AppendPreviewHint(result.DetailsText, "已先加载快速预览，稍后补清晰版本。")
                : result.DetailsText;
            if (result.Bitmap is not null)
            {
                _previewBitmapCache.SetPinnedPath(item.FullPath);
                var cachedPreview = _previewBitmapCache.StoreOrUpdate(
                    item.FullPath,
                    loadPlan.InitialLongEdgePixels,
                    result.Bitmap,
                    result.StatusText,
                    detailsText);
                _selectedPreviewDecodeLongEdge = cachedPreview.DecodeLongEdge;
                SetSelectedPreview(cachedPreview.Bitmap, cachedPreview.StatusText, cachedPreview.DetailsText);
            }
            else
            {
                _selectedPreviewDecodeLongEdge = 0;
                SetSelectedPreview(null, result.StatusText, detailsText);
            }

            QueuePreviewWarmup(maxLongEdgePixels);
            if (loadPlan.RequiresFollowUpReload
                && ShouldReloadSelectedPreviewForGrowth(loadPlan.FinalLongEdgePixels, loadPlan.InitialLongEdgePixels))
            {
                ScheduleSelectedPreviewReload();
            }
            trace.Success(CreateDiagnosticsProperties(
                ("statusText", result.StatusText),
                ("detailsText", detailsText),
                ("followUpReload", loadPlan.RequiresFollowUpReload.ToString())));
        }
        catch (OperationCanceledException)
        {
            trace.Canceled();
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(item, SelectedImage))
            {
                IsSelectedPreviewLoading = false;
                _pendingPreviewPath = null;
                _pendingPreviewDecodeLongEdge = 0;
                _selectedPreviewDecodeLongEdge = 0;
                SetSelectedPreview(null, "预览加载失败", ex.Message);
            }

            trace.Fail(ex);
        }
    }

    private void SetSelectedPreview(Bitmap? bitmap, string statusText, string detailsText)
    {
        SelectedPreviewBitmap = bitmap;
        _selectedPreviewPath = bitmap is null ? null : SelectedImage?.FullPath;
        _previewBitmapCache.SetPinnedPath(_selectedPreviewPath);
        SelectedPreviewStatusText = statusText;
        SelectedPreviewDetailsText = detailsText;
        ApplyAutoLongImageModeForCurrentPreview();
    }

    private bool TryUseCachedSelectedPreview(ImageListItemViewModel item, int decodeLongEdge)
    {
        if (!_previewBitmapCache.TryGet(item.FullPath, decodeLongEdge, out var cachedPreview))
        {
            return false;
        }

        CancelPreviewLoad();
        IsSelectedPreviewLoading = false;
        _pendingPreviewPath = null;
        _pendingPreviewDecodeLongEdge = 0;
        _selectedPreviewDecodeLongEdge = cachedPreview.DecodeLongEdge;
        SetSelectedPreview(cachedPreview.Bitmap, cachedPreview.StatusText, cachedPreview.DetailsText);
        QueuePreviewWarmup(decodeLongEdge);
        return true;
    }

    private bool TryUseLowerResolutionCachedSelectedPreview(ImageListItemViewModel item, int decodeLongEdge)
    {
        if (!_previewBitmapCache.TryGetBestAvailable(item.FullPath, out var cachedPreview)
            || cachedPreview.DecodeLongEdge >= decodeLongEdge)
        {
            return false;
        }

        _selectedPreviewDecodeLongEdge = cachedPreview.DecodeLongEdge;
        SetSelectedPreview(cachedPreview.Bitmap, cachedPreview.StatusText, cachedPreview.DetailsText);
        return true;
    }

    internal static bool CanReuseSelectedPreview(string requestedPath, string? loadedPath, int requestedLongEdge, int loadedLongEdge)
    {
        return !string.IsNullOrWhiteSpace(loadedPath)
            && string.Equals(requestedPath, loadedPath, PathComparison.Comparison)
            && loadedLongEdge >= requestedLongEdge;
    }

    internal static bool CanReusePendingPreviewRequest(string requestedPath, string? pendingPath, int requestedLongEdge, int pendingLongEdge)
    {
        return !string.IsNullOrWhiteSpace(pendingPath)
            && string.Equals(requestedPath, pendingPath, PathComparison.Comparison)
            && pendingLongEdge >= requestedLongEdge;
    }

    internal static ImageListItemViewModel[] BuildPreviewWarmupItems(
        IReadOnlyList<ImageListItemViewModel> items,
        ImageListItemViewModel? selectedItem,
        int maxCount)
    {
        if (items.Count == 0 || maxCount <= 0 || selectedItem is null)
        {
            return [];
        }

        var selectedIndex = -1;
        for (var index = 0; index < items.Count; index++)
        {
            if (ReferenceEquals(items[index], selectedItem))
            {
                selectedIndex = index;
                break;
            }
        }

        if (selectedIndex < 0)
        {
            return [];
        }

        return BuildPreviewWarmupIndices(items.Count, selectedIndex, maxCount)
            .Select(index => items[index])
            .ToArray();
    }

    internal static int[] BuildPreviewWarmupIndices(int totalCount, int selectedIndex, int maxCount)
    {
        if (totalCount <= 1 || maxCount <= 0 || selectedIndex < 0 || selectedIndex >= totalCount)
        {
            return [];
        }

        var results = new List<int>(Math.Min(maxCount, totalCount - 1));
        for (var distance = 1; results.Count < maxCount; distance++)
        {
            var nextIndex = selectedIndex + distance;
            if (nextIndex < totalCount)
            {
                results.Add(nextIndex);
                if (results.Count == maxCount)
                {
                    break;
                }
            }

            var previousIndex = selectedIndex - distance;
            if (previousIndex >= 0)
            {
                results.Add(previousIndex);
            }

            if (nextIndex >= totalCount && previousIndex < 0)
            {
                break;
            }
        }

        return results.ToArray();
    }

    internal static int CalculatePreviewWarmupLongEdge(int selectedPreviewLongEdge)
    {
        if (selectedPreviewLongEdge <= 0)
        {
            return 0;
        }

        return Math.Clamp(selectedPreviewLongEdge, PreviewWarmupMinimumLongEdge, PreviewWarmupMaximumLongEdge);
    }

    internal static int CalculatePreviewWarmupLongEdge(int selectedPreviewLongEdge, long sourceSizeBytes)
    {
        var warmupLongEdge = CalculatePreviewWarmupLongEdge(selectedPreviewLongEdge);
        if (warmupLongEdge <= 0)
        {
            return 0;
        }

        if (sourceSizeBytes >= PreviewWarmupHugeSourceSizeBytes)
        {
            return Math.Min(warmupLongEdge, PreviewWarmupMinimumLongEdge);
        }

        if (sourceSizeBytes >= PreviewWarmupLargeSourceSizeBytes)
        {
            return Math.Min(warmupLongEdge, PreviewWarmupLargeSourceMaximumLongEdge);
        }

        return warmupLongEdge;
    }

    internal static bool ShouldRefreshProgressiveThumbnails(
        int batchCount,
        int totalImageCount,
        bool isForegroundPreviewLoading)
    {
        if (batchCount <= 1)
        {
            return true;
        }

        var interval = ProgressiveCollectionThumbnailRefreshInterval;
        if (totalImageCount >= ProgressiveCollectionThumbnailRefreshHugeInputCount)
        {
            interval += 5;
        }
        else if (totalImageCount >= ProgressiveCollectionThumbnailRefreshLargeInputCount)
        {
            interval += 2;
        }

        if (isForegroundPreviewLoading)
        {
            interval += 1;
        }

        return batchCount % Math.Max(1, interval) == 0;
    }

    internal static bool ShouldDeferPreviewWarmup(bool isCollectionLoading, int totalImageCount)
    {
        return isCollectionLoading && totalImageCount >= ProgressiveCollectionMinimumInputCount;
    }

    internal static bool ShouldWarmPreviewBitmapCache(int warmupIndex, long sourceSizeBytes)
    {
        if (warmupIndex < 0)
        {
            return false;
        }

        var bitmapNeighborCount = sourceSizeBytes >= PreviewWarmupHugeSourceSizeBytes
            ? 0
            : sourceSizeBytes >= PreviewWarmupLargeSourceSizeBytes
                ? 1
                : PreviewWarmupBitmapNeighborCount;
        return warmupIndex < bitmapNeighborCount;
    }

    private void ResumeDeferredImageWarmup()
    {
        if (_isDisposed || IsExportProcessing)
        {
            return;
        }

        QueueThumbnailWarmup();
        if (SelectedImage is not null && _selectedPreviewDecodeLongEdge > 0)
        {
            QueuePreviewWarmup(_selectedPreviewDecodeLongEdge);
        }
    }

    private static string AppendPreviewHint(string detailsText, string hint)
    {
        if (string.IsNullOrWhiteSpace(hint))
        {
            return detailsText;
        }

        if (string.IsNullOrWhiteSpace(detailsText))
        {
            return hint;
        }

        return detailsText.Contains(hint, StringComparison.Ordinal)
            ? detailsText
            : $"{detailsText} {hint}";
    }

    private void ForgetBackgroundTask(Task task)
    {
        RegisterBackgroundTask(task);
    }

    internal int GetPendingBackgroundTaskCount()
    {
        lock (_backgroundTaskSync)
        {
            return _backgroundTasks.Count(static task => !task.IsCompleted);
        }
    }

    internal int GetTrackedOperationCount()
    {
        lock (_trackedOperationSync)
        {
            return _trackedOperationCount;
        }
    }

    internal IDisposable BeginTrackedOperationForTesting()
    {
        return BeginTrackedOperation();
    }

    internal void RegisterBackgroundTaskForTesting(Task task)
    {
        RegisterBackgroundTask(task);
    }

    private CancellationTokenSource BeginCollectionLoadScope(CancellationToken cancellationToken)
    {
        var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var previousTokenSource = _collectionLoadCts;
        _collectionLoadCts = linkedTokenSource;
        CancelQuietly(previousTokenSource);
        DisposeQuietly(previousTokenSource);
        return linkedTokenSource;
    }

    private void CompleteCollectionLoadScope(CancellationTokenSource cancellationTokenSource)
    {
        if (ReferenceEquals(_collectionLoadCts, cancellationTokenSource))
        {
            _collectionLoadCts = null;
        }
    }

    private IDisposable BeginTrackedOperation()
    {
        lock (_trackedOperationSync)
        {
            if (_trackedOperationCount == 0)
            {
                _trackedOperationsIdleTcs = CreateSignalTaskCompletionSource(completed: false);
            }

            _trackedOperationCount++;
        }

        return new TrackedOperationScope(this);
    }

    private void EndTrackedOperation()
    {
        TaskCompletionSource<object?>? idleSignal = null;
        lock (_trackedOperationSync)
        {
            if (_trackedOperationCount == 0)
            {
                return;
            }

            _trackedOperationCount--;
            if (_trackedOperationCount == 0)
            {
                idleSignal = _trackedOperationsIdleTcs;
            }
        }

        idleSignal?.TrySetResult(null);
    }

    private void RegisterBackgroundTask(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (task.IsCompleted)
        {
            ObserveBackgroundTaskCompletion(task);
            return;
        }

        lock (_backgroundTaskSync)
        {
            _backgroundTasks.Add(task);
        }

        task.ContinueWith(
            static (completedTask, state) =>
            {
                var owner = (MainWindowViewModel)state!;
                lock (owner._backgroundTaskSync)
                {
                    owner._backgroundTasks.Remove(completedTask);
                }

                owner.ObserveBackgroundTaskCompletion(completedTask);
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ObserveBackgroundTaskCompletion(Task task)
    {
        if (task.IsFaulted)
        {
            _ = task.Exception;
        }
    }

    private async Task WaitForBackgroundTasksAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task[] snapshot;
            lock (_backgroundTaskSync)
            {
                snapshot = _backgroundTasks
                    .Where(static task => !task.IsCompleted)
                    .ToArray();
            }

            if (snapshot.Length == 0)
            {
                return;
            }

            await Task.WhenAll(snapshot.Select(IgnoreTaskOutcome)).WaitAsync(cancellationToken);
        }
    }

    private async Task WaitForTrackedOperationsAsync(CancellationToken cancellationToken)
    {
        Task idleTask;
        lock (_trackedOperationSync)
        {
            idleTask = _trackedOperationsIdleTcs.Task;
        }

        await IgnoreTaskOutcome(idleTask).WaitAsync(cancellationToken);
    }

    private static Task IgnoreTaskOutcome(Task task)
    {
        return task.ContinueWith(
            static _ => { },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static TaskCompletionSource<object?> CreateSignalTaskCompletionSource(bool completed)
    {
        var signal = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completed)
        {
            signal.TrySetResult(null);
        }

        return signal;
    }

    private sealed class TrackedOperationScope(MainWindowViewModel owner) : IDisposable
    {
        private MainWindowViewModel? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.EndTrackedOperation();
        }
    }

    private Task RunOnUiContextAsync(Action action, CancellationToken cancellationToken = default)
    {
        return RunOnSynchronizationContextAsync(
            _uiSynchronizationContext,
            SynchronizationContext.Current,
            action,
            cancellationToken);
    }

    internal static Task RunOnSynchronizationContextAsync(
        SynchronizationContext? uiSynchronizationContext,
        SynchronizationContext? currentSynchronizationContext,
        Action action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        if (uiSynchronizationContext is null || ReferenceEquals(currentSynchronizationContext, uiSynchronizationContext))
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        uiSynchronizationContext.Post(static state =>
        {
            var (callback, taskCompletionSource, token) = ((Action Callback, TaskCompletionSource Completion, CancellationToken Token))state!;
            if (token.IsCancellationRequested)
            {
                taskCompletionSource.TrySetCanceled(token);
                return;
            }

            try
            {
                callback();
                taskCompletionSource.TrySetResult();
            }
            catch (Exception ex)
            {
                taskCompletionSource.TrySetException(ex);
            }
        }, (action, completion, cancellationToken));

        return completion.Task;
    }
}

