using System.Collections.ObjectModel;
using ModernImageViewer.Core;
using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private enum ImageMatchResultMode
    {
        None,
        ExactDuplicates,
        SimilarImages
    }

    internal readonly record struct ImageMatchCollectionSnapshot(
        IReadOnlyList<ImageRecord> Records,
        string Signature);

    internal readonly record struct ImageMatchVisibleItemLookupEntry(
        ImageListItemViewModel Item,
        int Index);

    internal readonly record struct ImageMatchItemLookupSnapshot(
        IReadOnlyDictionary<string, ImageMatchVisibleItemLookupEntry> VisibleItemsByPath,
        IReadOnlyDictionary<string, ImageListItemViewModel> AllItemsByPath);

    private static readonly IReadOnlyDictionary<string, ImageMatchVisibleItemLookupEntry> EmptyVisibleImageMatchItemLookup =
        new Dictionary<string, ImageMatchVisibleItemLookupEntry>(PathComparison.Comparer);

    private static readonly IReadOnlyDictionary<string, ImageListItemViewModel> EmptyImageMatchItemLookup =
        new Dictionary<string, ImageListItemViewModel>(PathComparison.Comparer);

    private readonly DesktopExactDuplicateImageService _exactDuplicateImageService = new();
    private readonly DesktopSimilarImageService _similarImageService = new();
    private readonly DesktopImageMatchResultCacheStore _imageMatchResultCacheStore = new();
    private ReadOnlyCollection<ImageMatchThresholdOption> _imageMatchThresholdOptions = [];
    private ImageMatchThresholdOption? _selectedImageMatchThresholdOption;
    private string _imageMatchPanelTitle = "查重 / 相似图";
    private string _imageMatchPanelSummary = "选中图片后，可以在这里查看完全重复或相似的图片分组。";
    private string _imageMatchBaseSummary = "选中图片后，可以在这里查看完全重复或相似的图片分组。";
    private ImageMatchResultMode _imageMatchResultMode;
    private int _imageMatchInitialGroupCount;
    private int _imageMatchCollectionVersion;
    private int _cachedImageMatchCollectionVersion = -1;
    private IReadOnlyList<ImageRecord> _cachedImageMatchRecords = [];
    private string _cachedImageMatchSignature = string.Empty;
    private int _cachedImageMatchItemLookupVersion = -1;
    private IReadOnlyDictionary<string, ImageMatchVisibleItemLookupEntry> _cachedVisibleImageMatchItemsByPath = EmptyVisibleImageMatchItemLookup;
    private IReadOnlyDictionary<string, ImageListItemViewModel> _cachedAllImageMatchItemsByPath = EmptyImageMatchItemLookup;

    public ObservableCollection<ImageMatchGroupViewModel> ImageMatchGroups { get; } = [];

    public IReadOnlyList<ImageMatchThresholdOption> ImageMatchThresholdOptions => _imageMatchThresholdOptions;

    public ImageMatchThresholdOption? SelectedImageMatchThresholdOption
    {
        get => _selectedImageMatchThresholdOption;
        set
        {
            if (!SetProperty(ref _selectedImageMatchThresholdOption, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SimilarThresholdHintText));
            OnPropertyChanged(nameof(CanRunSimilarImageScan));
            ScheduleViewerSettingsSave();
        }
    }

    public string SimilarThresholdHintText => SelectedImageMatchThresholdOption is null
        ? "当前还没有可用的相似阈值。"
        : $"{SelectedImageMatchThresholdOption.Description}当前只会匹配 dHash 差异不超过 {SelectedImageMatchThresholdOption.DistanceThreshold} 的候选图片。";

    public string ImageMatchPanelTitle
    {
        get => _imageMatchPanelTitle;
        private set => SetProperty(ref _imageMatchPanelTitle, value);
    }

    public string ImageMatchPanelSummary
    {
        get => _imageMatchPanelSummary;
        private set => SetProperty(ref _imageMatchPanelSummary, value);
    }

    public bool HasImageMatchGroups => ImageMatchGroups.Count > 0;

    public bool ShowImageMatchEmptyState => !HasImageMatchGroups;

    public bool IsExactDuplicateResultActive => _imageMatchResultMode == ImageMatchResultMode.ExactDuplicates;

    public bool IsSimilarImageResultActive => _imageMatchResultMode == ImageMatchResultMode.SimilarImages;

    public bool CanRunExactDuplicateScan => Images.Count > 1 && !IsExportProcessing;

    public bool CanRunSimilarImageScan => Images.Count > 1 && SelectedImageMatchThresholdOption is not null && !IsExportProcessing;

    public bool CanClearImageMatchResults => HasImageMatchGroups || _imageMatchResultMode != ImageMatchResultMode.None;

    public string ImageMatchEmptyText => _imageMatchResultMode switch
    {
        ImageMatchResultMode.ExactDuplicates => "还没有发现完全重复的图片。",
        ImageMatchResultMode.SimilarImages => "还没有发现当前阈值下的相似图片。",
        _ => Images.Count switch
        {
            <= 0 => "载入图片后，就可以开始检查完全重复和相似图片。",
            1 => "当前只有 1 张图片，至少需要 2 张才能开始检查。",
            _ => "点击上面的按钮，开始分析当前列表。"
        }
    };

    private void InitializeImageMatchSettings()
    {
        _imageMatchThresholdOptions =
        [
            new ImageMatchThresholdOption(4, "较严", "只保留内容非常接近的图片，结果更干净。"),
            new ImageMatchThresholdOption(8, "平衡", "适合大多数相似图筛选场景，结果和范围更均衡。"),
            new ImageMatchThresholdOption(12, "宽松", "会找出更多接近图片，适合先做一轮粗筛。")
        ];
        SelectedImageMatchThresholdOption = _imageMatchThresholdOptions[1];
        ResetImageMatchResultsForCollectionLoad();
    }

    private void OnImageMatchTargetCollectionChanged()
    {
        InvalidateImageMatchCollectionSnapshot();
        ResetImageMatchResultsForCollectionLoad();
    }

    public void ClearImageMatchResults()
    {
        ReplaceImageMatchGroups(
            "查重 / 相似图",
            Images.Count switch
            {
                <= 0 => "选中图片后，可以在这里查看完全重复或相似的图片分组。",
                1 => "当前只有 1 张图片，至少需要 2 张才能开始检查。",
                _ => "结果已清空，可以重新分析当前列表里的完全重复或相似图片。"
            },
            [],
            ImageMatchResultMode.None);
        OperationStatusText = Images.Count > 1
            ? "已清空查重结果。"
            : OperationStatusText;
    }

    public bool FocusImageByPath(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return false;
        }

        var lookupSnapshot = GetOrCreateImageMatchItemLookupSnapshot();
        if (!lookupSnapshot.VisibleItemsByPath.TryGetValue(fullPath, out var visibleEntry))
        {
            OperationStatusText = "没有在当前列表里定位到这张图片。";
            return false;
        }

        var item = visibleEntry.Item;
        SelectedImage = item;
        OperationStatusText = $"已定位到：{item.FileName}";
        return true;
    }

    public IReadOnlyList<string> GetLeadingImageMatchSelectionPaths()
    {
        var firstGroup = ImageMatchGroups.FirstOrDefault();
        if (firstGroup is null)
        {
            return [];
        }

        return firstGroup.HasSuggestedSelection
            ? firstGroup.SuggestedSelectionPaths
            : firstGroup.Paths;
    }

    public bool SelectImageMatchGroup(ImageMatchGroupViewModel? group)
    {
        if (group is null)
        {
            return false;
        }

        var selection = group.HasSuggestedSelection ? group.SuggestedSelectionPaths : group.Paths;
        if (!SelectImagesByPath(selection))
        {
            OperationStatusText = "批量选中图片时，没有在当前列表里定位到目标。";
            return false;
        }

        OperationStatusText = group.IsExactDuplicateGroup
            ? group.SuggestedSelectionPaths.Count < group.Paths.Count
                ? $"已选中 {group.Title} 的重复项，保留 {group.ReferenceLabel}。"
                : $"已选中 {group.Title} 的 {group.Paths.Count} 张图片。"
            : $"已选中 {group.Title} 的 {selection.Count} 张候选图片。";
        return true;
    }

    public async Task RecycleImageMatchGroupSuggestionsAsync(ImageMatchGroupViewModel? group)
    {
        if (group is null)
        {
            OperationStatusText = "请先选中一组结果。";
            return;
        }

        var recyclePaths = group.HasSuggestedSelection
            ? group.SuggestedSelectionPaths
            : group.Paths.Skip(1).ToArray();
        if (recyclePaths.Count == 0)
        {
            OperationStatusText = "这一组里没有可移到回收站的图片。";
            return;
        }

        if (!SelectImagesByPath(recyclePaths))
        {
            OperationStatusText = "批量选择图片时，无法在当前列表中定位。";
            return;
        }

        await RecycleSelectedToTrashAsync();
    }

    public bool SelectImageMatchItemReference(ImageMatchItemViewModel? item)
    {
        if (item is null)
        {
            return false;
        }

        var selection = item.PathsToSelectWhenKeeping.Count > 0
            ? item.PathsToSelectWhenKeeping
            : [item.FullPath];
        if (!SelectImagesByPath(selection))
        {
            OperationStatusText = "批量选择图片时，无法在当前列表中定位。";
            return false;
        }

        OperationStatusText = string.Equals(item.MatchKindLabel, "完全重复", StringComparison.Ordinal)
            ? item.PathsToSelectWhenKeeping.Count == 0
                ? $"当前已保留 {item.FileName} 这 1 张图片。"
                : $"已改为保留 {item.FileName}，并选中其余 {item.PathsToSelectWhenKeeping.Count} 张重复图片。"
            : item.PathsToSelectWhenKeeping.Count == 0
                ? $"{item.FileName} 当前没有可联动选中的候选图。"
                : $"已将 {item.FileName} 设为参考图，并选中其余 {item.PathsToSelectWhenKeeping.Count} 张相似候选图。";
        return true;
    }

    public ImageMatchGroupViewModel? ConfirmImageMatchGroup(ImageMatchGroupViewModel group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var currentIndex = ImageMatchGroups.IndexOf(group);
        if (currentIndex < 0)
        {
            return ImageMatchGroups.FirstOrDefault();
        }

        ImageMatchGroups.RemoveAt(currentIndex);
        NotifyImageMatchStateChanged();
        RefreshImageMatchPanelSummary();
        OnPropertyChanged(nameof(ImageMatchEmptyText));

        if (ImageMatchGroups.Count == 0)
        {
            return null;
        }

        var nextIndex = Math.Min(currentIndex, ImageMatchGroups.Count - 1);
        return ImageMatchGroups[nextIndex];
    }

    public async Task FindExactDuplicatesAsync(CancellationToken cancellationToken = default)
    {
        using var trackedOperation = BeginTrackedOperation();
        if (!CanRunExactDuplicateScan)
        {
            if (Images.Count <= 1)
            {
                OperationStatusText = "至少需要 2 张图片才能开始检查完全重复。";
            }

            return;
        }

        var snapshot = GetOrCreateImageMatchCollectionSnapshot();
        var records = snapshot.Records;
        if (_imageMatchResultCacheStore.TryLoadExact(snapshot.Signature, out var cachedResult))
        {
            ApplyExactDuplicateMatchResults(cachedResult, autoSelectLeadingPaths: true, fromCache: true);
            OperationStatusText = cachedResult.DuplicateCount == 0
                ? "\u5df2\u76f4\u63a5\u6062\u590d\u4e0a\u6b21\u5b8c\u5168\u91cd\u590d\u68c0\u67e5\u7ed3\u679c\uff0c\u65e0\u9700\u91cd\u65b0\u626b\u63cf\u3002"
                : $"\u5df2\u4ece\u7f13\u5b58\u6062\u590d {cachedResult.GroupCount} \u7ec4\u5b8c\u5168\u91cd\u590d\u7ed3\u679c\uff0c\u65e0\u9700\u91cd\u65b0\u626b\u63cf\u3002";
            return;
        }

        var progress = CreateImageMatchProgressReporter();

        IsExportProcessing = true;
        BeginOperationProgress(records.Count, "正在检查完全重复图片...", $"准备扫描 {records.Count} 张图片。");

        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _currentOperationCts = operationCts;
        OnPropertyChanged(nameof(CanCancelOperation));

        try
        {
            var result = await Task.Run(
                () => _exactDuplicateImageService.FindDuplicates(records, operationCts.Token, progress),
                operationCts.Token);

            await _imageMatchResultCacheStore.SaveExactAsync(snapshot.Signature, result, CancellationToken.None);
            ApplyExactDuplicateMatchResults(result, autoSelectLeadingPaths: true, fromCache: false);
            OperationStatusText = result.DuplicateCount == 0
                ? result.FailedCount > 0
                    ? $"已扫描 {result.ScannedCount} 张图片，没有发现完全重复；另有 {result.FailedCount} 张未能完成读取。"
                    : $"已扫描 {result.ScannedCount} 张图片，没有发现完全重复。"
                : result.FailedCount > 0
                    ? $"已找到 {result.GroupCount} 组完全重复图片，共 {result.DuplicateCount} 张重复项；另有 {result.FailedCount} 张未能完成读取。"
                    : $"已找到 {result.GroupCount} 组完全重复图片，共 {result.DuplicateCount} 张重复项。";
        }
        catch (OperationCanceledException)
        {
            OperationStatusText = "已取消检查完全重复图片。";
        }
        catch (Exception ex)
        {
            OperationStatusText = $"检查完全重复图片失败：{ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_currentOperationCts, operationCts))
            {
                _currentOperationCts = null;
                OnPropertyChanged(nameof(CanCancelOperation));
            }

            DesktopImageProcessingPolicy.TrimMemory();
            IsExportProcessing = false;
        }
    }

    public async Task FindSimilarImagesAsync(CancellationToken cancellationToken = default)
    {
        using var trackedOperation = BeginTrackedOperation();
        if (!CanRunSimilarImageScan || SelectedImageMatchThresholdOption is null)
        {
            if (Images.Count <= 1)
            {
                OperationStatusText = "至少需要 2 张图片才能开始检查相似图。";
            }

            return;
        }

        var snapshot = GetOrCreateImageMatchCollectionSnapshot();
        var records = snapshot.Records;
        var progress = CreateImageMatchProgressReporter();
        var threshold = SelectedImageMatchThresholdOption;
        var totalWork = Math.Max(1, records.Count * 2);

        if (_imageMatchResultCacheStore.TryLoadSimilar(snapshot.Signature, threshold.DistanceThreshold, out var cachedResult))
        {
            ApplySimilarImageMatchResults(cachedResult, autoSelectLeadingPaths: true, fromCache: true);
            OperationStatusText = cachedResult.SimilarCount == 0
                ? "\u5df2\u76f4\u63a5\u6062\u590d\u4e0a\u6b21\u76f8\u4f3c\u56fe\u68c0\u67e5\u7ed3\u679c\uff0c\u65e0\u9700\u91cd\u65b0\u626b\u63cf\u3002"
                : $"\u5df2\u4ece\u7f13\u5b58\u6062\u590d {cachedResult.GroupCount} \u7ec4\u76f8\u4f3c\u56fe\u7ed3\u679c\uff0c\u65e0\u9700\u91cd\u65b0\u626b\u63cf\u3002";
            return;
        }

        IsExportProcessing = true;
        BeginOperationProgress(totalWork, "正在检查相似图片...", $"准备扫描 {records.Count} 张图片并计算相似特征。");

        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _currentOperationCts = operationCts;
        OnPropertyChanged(nameof(CanCancelOperation));

        try
        {
            var result = await Task.Run(
                () => _similarImageService.FindSimilarImages(records, threshold.DistanceThreshold, operationCts.Token, progress),
                operationCts.Token);

            await _imageMatchResultCacheStore.SaveSimilarAsync(snapshot.Signature, result, CancellationToken.None);
            ApplySimilarImageMatchResults(result, autoSelectLeadingPaths: true, fromCache: false);
            OperationStatusText = result.SimilarCount == 0
                ? result.FailedCount > 0
                    ? $"已扫描 {result.ScannedCount} 张图片，没有发现相似图；另有 {result.FailedCount} 张未能完成读取。"
                    : $"已扫描 {result.ScannedCount} 张图片，没有发现相似图。"
                : result.FailedCount > 0
                    ? $"已找到 {result.GroupCount} 组相似图片，共 {result.SimilarCount} 张候选项；另有 {result.FailedCount} 张未能完成读取。"
                    : $"已找到 {result.GroupCount} 组相似图片，共 {result.SimilarCount} 张候选项。";
        }
        catch (OperationCanceledException)
        {
            OperationStatusText = "已取消检查相似图片。";
        }
        catch (Exception ex)
        {
            OperationStatusText = $"检查相似图片失败：{ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_currentOperationCts, operationCts))
            {
                _currentOperationCts = null;
                OnPropertyChanged(nameof(CanCancelOperation));
            }

            DesktopImageProcessingPolicy.TrimMemory();
            IsExportProcessing = false;
        }
    }

    private void ResetImageMatchResultsForCollectionLoad()
    {
        var snapshot = GetOrCreateImageMatchCollectionSnapshot();
        if (snapshot.Records.Count > 1 &&
            _imageMatchResultCacheStore.TryLoadLatest(snapshot.Signature, out var cachedResult))
        {
            ApplyCachedImageMatchResults(cachedResult, autoSelectLeadingPaths: false);
            return;
        }

        ReplaceImageMatchGroups(
            "查重 / 相似图",
            Images.Count switch
            {
                <= 0 => "选中图片后，可以在这里查看完全重复或相似的图片分组。",
                1 => "当前只有 1 张图片，至少需要 2 张才能开始检查。",
                _ => "点击上面的按钮，开始分析当前列表里的完全重复或相似图片。"
            },
            [],
            ImageMatchResultMode.None);
    }

    private IProgress<DesktopOperationProgress> CreateImageMatchProgressReporter()
    {
        return new Progress<DesktopOperationProgress>(progress =>
        {
            UpdateOperationProgress(progress.CompletedCount, progress.TotalCount, progress.StatusText);
        });
    }

    internal ImageMatchCollectionSnapshot GetOrCreateImageMatchCollectionSnapshot()
    {
        if (_cachedImageMatchCollectionVersion == _imageMatchCollectionVersion)
        {
            return new ImageMatchCollectionSnapshot(_cachedImageMatchRecords, _cachedImageMatchSignature);
        }

        var records = Images
            .Select(static item => new ImageRecord(item.FullPath, item.FileName, item.SizeBytes, item.ModifiedAt))
            .ToArray();
        var signature = DesktopImageMatchResultCacheStore.CreateCollectionSignature(records);
        _cachedImageMatchRecords = records;
        _cachedImageMatchSignature = signature;
        _cachedImageMatchCollectionVersion = _imageMatchCollectionVersion;
        return new ImageMatchCollectionSnapshot(records, signature);
    }

    internal ImageMatchItemLookupSnapshot GetOrCreateImageMatchItemLookupSnapshot()
    {
        if (_cachedImageMatchItemLookupVersion == _imageMatchCollectionVersion)
        {
            return new ImageMatchItemLookupSnapshot(
                _cachedVisibleImageMatchItemsByPath,
                _cachedAllImageMatchItemsByPath);
        }

        var visibleItemsByPath = CreateVisibleImageMatchItemLookup(Images);
        var allItemsByPath = CreateImageMatchItemLookup(_allImages);
        _cachedVisibleImageMatchItemsByPath = visibleItemsByPath;
        _cachedAllImageMatchItemsByPath = allItemsByPath;
        _cachedImageMatchItemLookupVersion = _imageMatchCollectionVersion;
        return new ImageMatchItemLookupSnapshot(visibleItemsByPath, allItemsByPath);
    }

    internal ImageListItemViewModel? ResolveImageMatchPanelItem(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return null;
        }

        var lookupSnapshot = GetOrCreateImageMatchItemLookupSnapshot();
        if (lookupSnapshot.VisibleItemsByPath.TryGetValue(fullPath, out var visibleEntry))
        {
            return visibleEntry.Item;
        }

        return lookupSnapshot.AllItemsByPath.TryGetValue(fullPath, out var allItem)
            ? allItem
            : null;
    }

    internal void InvalidateImageMatchCollectionSnapshot()
    {
        unchecked
        {
            _imageMatchCollectionVersion++;
        }

        _cachedImageMatchCollectionVersion = -1;
        _cachedImageMatchRecords = [];
        _cachedImageMatchSignature = string.Empty;
        _cachedImageMatchItemLookupVersion = -1;
        _cachedVisibleImageMatchItemsByPath = EmptyVisibleImageMatchItemLookup;
        _cachedAllImageMatchItemsByPath = EmptyImageMatchItemLookup;
    }

    private void ApplyExactDuplicateMatchResults(
        ExactDuplicateScanResult result,
        bool autoSelectLeadingPaths,
        bool fromCache)
    {
        var groups = result.Groups
            .Select((group, index) => CreateImageMatchGroup("完全重复", index, group.Paths))
            .ToArray();
        var summary = AppendCachedMatchSummary(
            result.GroupCount == 0
                ? result.FailedCount > 0
                    ? $"已扫描 {result.ScannedCount} 张图片，没有发现完全重复；另有 {result.FailedCount} 张未能完成读取。"
                    : $"已扫描 {result.ScannedCount} 张图片，没有发现完全重复。"
                : result.FailedCount > 0
                    ? $"已找到 {result.GroupCount} 组完全重复图片，默认会帮每组保留 1 张作为参考；另有 {result.FailedCount} 张未能完成读取。"
                    : $"已找到 {result.GroupCount} 组完全重复图片，默认会帮每组保留 1 张作为参考。",
            fromCache);

        ReplaceImageMatchGroups("完全重复结果", summary, groups, ImageMatchResultMode.ExactDuplicates);
        if (autoSelectLeadingPaths)
        {
            SelectLeadingImageMatchPathsIfAvailable();
        }
    }

    private void ApplySimilarImageMatchResults(
        SimilarImageScanResult result,
        bool autoSelectLeadingPaths,
        bool fromCache)
    {
        var groups = result.Groups
            .Select((group, index) => CreateImageMatchGroup("相似图片", index, group.Paths))
            .ToArray();
        var thresholdLabel = ImageMatchThresholdOptions
            .FirstOrDefault(option => option.DistanceThreshold == result.DistanceThreshold)?.Label
            ?? $"阈值 <= {result.DistanceThreshold}";
        var summary = AppendCachedMatchSummary(
            result.GroupCount == 0
                ? result.FailedCount > 0
                    ? $"已扫描 {result.ScannedCount} 张图片，没有发现相似图；当前阈值 {thresholdLabel}；另有 {result.FailedCount} 张未能完成读取。"
                    : $"已扫描 {result.ScannedCount} 张图片，没有发现相似图；当前阈值 {thresholdLabel}。"
                : result.FailedCount > 0
                    ? $"已找到 {result.GroupCount} 组相似图片，当前阈值 {thresholdLabel}，dHash <= {result.DistanceThreshold}；结果仅供人工复核，另有 {result.FailedCount} 张未能完成读取。"
                    : $"已找到 {result.GroupCount} 组相似图片，当前阈值 {thresholdLabel}，dHash <= {result.DistanceThreshold}；结果仅供人工复核。",
            fromCache);

        ReplaceImageMatchGroups("相似图片结果", summary, groups, ImageMatchResultMode.SimilarImages);
        if (autoSelectLeadingPaths)
        {
            SelectLeadingImageMatchPathsIfAvailable();
        }
    }

    private void ApplyCachedImageMatchResults(
        DesktopImageMatchCachedResult cachedResult,
        bool autoSelectLeadingPaths)
    {
        if (cachedResult.Mode == DesktopImageMatchCacheMode.SimilarImages)
        {
            var threshold = ImageMatchThresholdOptions
                .FirstOrDefault(option => option.DistanceThreshold == cachedResult.DistanceThreshold);
            if (threshold is not null && !ReferenceEquals(SelectedImageMatchThresholdOption, threshold))
            {
                SelectedImageMatchThresholdOption = threshold;
            }

            if (cachedResult.SimilarImageResult is not null)
            {
                ApplySimilarImageMatchResults(cachedResult.SimilarImageResult, autoSelectLeadingPaths, fromCache: true);
            }

            return;
        }

        if (cachedResult.ExactDuplicateResult is not null)
        {
            ApplyExactDuplicateMatchResults(cachedResult.ExactDuplicateResult, autoSelectLeadingPaths, fromCache: true);
        }
    }

    private static string AppendCachedMatchSummary(string summary, bool fromCache)
    {
        return fromCache
            ? $"{summary} 已从缓存恢复。"
            : summary;
    }

    private ImageMatchGroupViewModel CreateImageMatchGroup(string kindLabel, int index, IReadOnlyList<string> paths)
    {
        var normalizedPaths = paths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(PathComparison.Comparer)
            .ToArray();
        var folders = normalizedPaths
            .Select(Path.GetDirectoryName)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(PathComparison.Comparer)
            .ToArray();
        var suggestedSelectionPaths = string.Equals(kindLabel, "完全重复", StringComparison.Ordinal)
            ? normalizedPaths.Skip(1).ToArray()
            : normalizedPaths;
        var items = normalizedPaths
            .Select((path, pathIndex) =>
            {
                var imageItem = ResolveImageMatchPanelItem(path);

                if (imageItem is not null && !imageItem.HasThumbnail && !imageItem.IsThumbnailLoading)
                {
                    ForgetBackgroundTask(EnsureThumbnailLoadedAsync(imageItem, CancellationToken.None));
                }

                return new ImageMatchItemViewModel(
                    imageItem,
                    path,
                    string.Equals(kindLabel, "完全重复", StringComparison.Ordinal)
                        ? pathIndex == 0 ? "保留参考" : "重复候选"
                        : pathIndex == 0 ? "参考图" : "相似图",
                    kindLabel,
                    pathIndex == 0 && string.Equals(kindLabel, "完全重复", StringComparison.Ordinal),
                    normalizedPaths
                        .Where(candidatePath => !string.Equals(candidatePath, path, PathComparison.Comparison))
                        .ToArray());
            })
            .ToArray();

        var fileNames = normalizedPaths
            .Select(Path.GetFileName)
            .Where(static fileName => !string.IsNullOrWhiteSpace(fileName))
            .Take(3)
            .ToList();
        var subtitle = fileNames.Count == 0
            ? $"{normalizedPaths.Length} 张图片"
            : $"{normalizedPaths.Length} 张图片 · {string.Join(" · ", fileNames)}";
        if (normalizedPaths.Length > fileNames.Count && fileNames.Count > 0)
        {
            subtitle += $" · 共 {normalizedPaths.Length} 张";
        }

        var folderLabel = folders.Length switch
        {
            0 => "位置未知",
            1 => folders[0]!,
            _ => $"分布在 {folders.Length} 个文件夹"
        };
        var referenceName = normalizedPaths.Length == 0
            ? "未知文件"
            : Path.GetFileName(normalizedPaths[0]);

        return new ImageMatchGroupViewModel(
            $"第 {index + 1} 组",
            subtitle,
            kindLabel,
            folderLabel,
            suggestedSelectionPaths.Length > 0
                ? $"建议保留：{referenceName}"
                : $"参考图片：{referenceName}",
            normalizedPaths,
            suggestedSelectionPaths.Length > 0 ? suggestedSelectionPaths : normalizedPaths,
            items);
    }

    private static Dictionary<string, ImageMatchVisibleItemLookupEntry> CreateVisibleImageMatchItemLookup(
        IReadOnlyList<ImageListItemViewModel> items)
    {
        var lookup = new Dictionary<string, ImageMatchVisibleItemLookupEntry>(
            Math.Max(0, items.Count),
            PathComparison.Comparer);

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (string.IsNullOrWhiteSpace(item.FullPath) || lookup.ContainsKey(item.FullPath))
            {
                continue;
            }

            lookup[item.FullPath] = new ImageMatchVisibleItemLookupEntry(item, index);
        }

        return lookup;
    }

    private static Dictionary<string, ImageListItemViewModel> CreateImageMatchItemLookup(
        IReadOnlyList<ImageListItemViewModel> items)
    {
        var lookup = new Dictionary<string, ImageListItemViewModel>(
            Math.Max(0, items.Count),
            PathComparison.Comparer);

        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.FullPath) || lookup.ContainsKey(item.FullPath))
            {
                continue;
            }

            lookup[item.FullPath] = item;
        }

        return lookup;
    }

    private void ReplaceImageMatchGroups(
        string title,
        string summary,
        IEnumerable<ImageMatchGroupViewModel> groups,
        ImageMatchResultMode mode)
    {
        var normalizedGroups = groups.ToArray();

        ReplaceObservableCollectionItemsIfChanged(ImageMatchGroups, normalizedGroups);

        _imageMatchResultMode = mode;
        _imageMatchBaseSummary = summary;
        _imageMatchInitialGroupCount = normalizedGroups.Length;
        ImageMatchPanelTitle = title;
        NotifyImageMatchStateChanged();
        RefreshImageMatchPanelSummary();
        OnPropertyChanged(nameof(ImageMatchEmptyText));
    }

    private void SelectLeadingImageMatchPathsIfAvailable()
    {
        var suggestedPaths = GetLeadingImageMatchSelectionPaths();
        if (suggestedPaths.Count == 0)
        {
            return;
        }

        _ = SelectImagesByPath(suggestedPaths);
    }

    private void NotifyImageMatchStateChanged()
    {
        OnPropertyChanged(nameof(HasImageMatchGroups));
        OnPropertyChanged(nameof(ShowImageMatchEmptyState));
        OnPropertyChanged(nameof(IsExactDuplicateResultActive));
        OnPropertyChanged(nameof(IsSimilarImageResultActive));
        OnPropertyChanged(nameof(CanRunExactDuplicateScan));
        OnPropertyChanged(nameof(CanRunSimilarImageScan));
        OnPropertyChanged(nameof(CanClearImageMatchResults));
        OnPropertyChanged(nameof(ImageMatchEmptyText));
    }

    private void RefreshImageMatchPanelSummary()
    {
        if (_imageMatchResultMode != ImageMatchResultMode.SimilarImages || _imageMatchInitialGroupCount <= 0)
        {
            ImageMatchPanelSummary = _imageMatchBaseSummary;
            return;
        }

        var reviewedCount = Math.Max(0, _imageMatchInitialGroupCount - ImageMatchGroups.Count);
        ImageMatchPanelSummary = ImageMatchGroups.Count == 0
            ? $"相似图片已全部确认，最初共 {_imageMatchInitialGroupCount} 组。"
            : $"{_imageMatchBaseSummary} 还剩 {ImageMatchGroups.Count} / {_imageMatchInitialGroupCount} 组，已确认 {reviewedCount} 组。";
    }
}
