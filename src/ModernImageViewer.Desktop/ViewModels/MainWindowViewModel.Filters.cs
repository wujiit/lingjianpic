using System.Collections.ObjectModel;
using ModernImageViewer.Core;

namespace ModernImageViewer.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const string AllFormatsFilterLabel = "全部格式";
    private const string AllFileSizesFilterLabel = "全部大小";
    private const string LightweightFileSizeFilterLabel = "轻量（小于 2 MB）";
    private const string StandardFileSizeFilterLabel = "标准（2 - 8 MB）";
    private const string LargeFileSizeFilterLabel = "较大（8 - 20 MB）";
    private const string HugeFileSizeFilterLabel = "超大（20 MB 以上）";

    private const int SearchFilterDebounceMilliseconds = 120;
    private string _searchText = string.Empty;
    private string _selectedFormatFilter = AllFormatsFilterLabel;
    private string _selectedSizeFilter = AllFileSizesFilterLabel;

    public ObservableCollection<string> FormatFilterOptions { get; } = [];

    public IReadOnlyList<string> SizeFilterOptions { get; } =
    [
        AllFileSizesFilterLabel,
        LightweightFileSizeFilterLabel,
        StandardFileSizeFilterLabel,
        LargeFileSizeFilterLabel,
        HugeFileSizeFilterLabel
    ];

    public string SearchText
    {
        get => _searchText;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (!SetProperty(ref _searchText, normalized))
            {
                return;
            }

            ScheduleSearchFilterApply();
        }
    }

    public string SelectedFormatFilter
    {
        get => _selectedFormatFilter;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? AllFormatsFilterLabel : value;
            if (!SetProperty(ref _selectedFormatFilter, normalized))
            {
                return;
            }

            ApplyFilters();
        }
    }

    public string SelectedSizeFilter
    {
        get => _selectedSizeFilter;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? AllFileSizesFilterLabel : value;
            if (!SetProperty(ref _selectedSizeFilter, normalized))
            {
                return;
            }

            ApplyFilters();
        }
    }

    public bool HasActiveFilters => HasActiveFilterCriteria(
        SearchText,
        SelectedFormatFilter,
        SelectedSizeFilter,
        SelectedReviewFilter,
        SelectedRatingFilter);

    public bool CanClearFilters => HasActiveFilters;

    public string FilteredImageCountText
    {
        get
        {
            if (_allImages.Count == 0)
            {
                return "0 张";
            }

            return Images.Count == _allImages.Count
                ? $"共 {Images.Count} 张"
                : $"显示 {Images.Count} / {_allImages.Count} 张";
        }
    }

    public string FilterSummaryText
    {
        get
        {
            if (_allImages.Count == 0)
            {
                return "可按名称、路径、格式、大小、标记和星级快速缩小当前列表。";
            }

            if (!HasActiveFilters)
            {
                return "当前显示全部已读取图片。";
            }

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                parts.Add($"关键词：{SearchText}");
            }

            if (!string.Equals(SelectedFormatFilter, AllFormatsFilterLabel, StringComparison.Ordinal))
            {
                parts.Add($"格式：{SelectedFormatFilter}");
            }

            if (!string.Equals(SelectedSizeFilter, AllFileSizesFilterLabel, StringComparison.Ordinal))
            {
                parts.Add($"大小：{SelectedSizeFilter}");
            }

            if (!string.Equals(SelectedReviewFilter, AllReviewFilterLabel, StringComparison.Ordinal))
            {
                parts.Add($"标记：{SelectedReviewFilter}");
            }

            if (!string.Equals(SelectedRatingFilter, AllRatingsFilterLabel, StringComparison.Ordinal))
            {
                parts.Add($"星级：{SelectedRatingFilter}");
            }

            return parts.Count == 0
                ? "当前显示全部已读取图片。"
                : $"当前筛选：{string.Join(" / ", parts)}";
        }
    }

    private void InitializeFilterSettings()
    {
        RefreshFormatFilterOptions();
    }

    public void ClearFilters()
    {
        var changed = false;

        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            _searchText = string.Empty;
            OnPropertyChanged(nameof(SearchText));
            changed = true;
        }

        if (!string.Equals(_selectedFormatFilter, AllFormatsFilterLabel, StringComparison.Ordinal))
        {
            _selectedFormatFilter = AllFormatsFilterLabel;
            OnPropertyChanged(nameof(SelectedFormatFilter));
            changed = true;
        }

        if (!string.Equals(_selectedSizeFilter, AllFileSizesFilterLabel, StringComparison.Ordinal))
        {
            _selectedSizeFilter = AllFileSizesFilterLabel;
            OnPropertyChanged(nameof(SelectedSizeFilter));
            changed = true;
        }

        if (!string.Equals(_selectedReviewFilter, AllReviewFilterLabel, StringComparison.Ordinal))
        {
            _selectedReviewFilter = AllReviewFilterLabel;
            OnPropertyChanged(nameof(SelectedReviewFilter));
            changed = true;
        }

        if (!string.Equals(_selectedRatingFilter, AllRatingsFilterLabel, StringComparison.Ordinal))
        {
            _selectedRatingFilter = AllRatingsFilterLabel;
            OnPropertyChanged(nameof(SelectedRatingFilter));
            changed = true;
        }

        if (changed)
        {
            ApplyFilters();
        }
    }

    private void RefreshFormatFilterOptions()
    {
        var formats = _allImages
            .Select(GetFormatFilterLabel)
            .Where(static label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static label => label, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var nextOptions = new string[formats.Length + 1];
        nextOptions[0] = AllFormatsFilterLabel;
        for (var index = 0; index < formats.Length; index++)
        {
            nextOptions[index + 1] = formats[index];
        }

        ReplaceObservableCollectionItemsIfChanged(
            FormatFilterOptions,
            nextOptions,
            StringComparer.OrdinalIgnoreCase);

        if (!nextOptions.Contains(_selectedFormatFilter, StringComparer.OrdinalIgnoreCase))
        {
            _selectedFormatFilter = AllFormatsFilterLabel;
            OnPropertyChanged(nameof(SelectedFormatFilter));
        }

        NotifyFilterStateChanged();
    }

    private void ScheduleSearchFilterApply(string? preferredFocusPath = null)
    {
        CancelScheduledSearchFilterApply();

        if (_allImages.Count == 0)
        {
            ApplyFiltersCore(preferredFocusPath);
            return;
        }

        _searchFilterApplyCts = new CancellationTokenSource();
        var scheduledCts = _searchFilterApplyCts;
        ForgetBackgroundTask(RunScheduledSearchFilterApplyAsync(preferredFocusPath, scheduledCts));
    }

    private async Task RunScheduledSearchFilterApplyAsync(string? preferredFocusPath, CancellationTokenSource scheduledCts)
    {
        try
        {
            await Task.Delay(SearchFilterDebounceMilliseconds, scheduledCts.Token);
            if (scheduledCts.IsCancellationRequested)
            {
                return;
            }

            await RunOnUiContextAsync(() => ApplyFiltersCore(preferredFocusPath), scheduledCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_searchFilterApplyCts, scheduledCts))
            {
                _searchFilterApplyCts = null;
            }

            DisposeQuietly(scheduledCts);
        }
    }

    private void CancelScheduledSearchFilterApply()
    {
        var scheduledCts = _searchFilterApplyCts;
        _searchFilterApplyCts = null;
        CancelQuietly(scheduledCts);
        DisposeQuietly(scheduledCts);
    }

    private void ApplyFilters(string? preferredFocusPath = null)
    {
        CancelScheduledSearchFilterApply();
        ApplyFiltersCore(preferredFocusPath);
    }

    private void ApplyFiltersCore(string? preferredFocusPath = null)
    {
        var focusPath = string.IsNullOrWhiteSpace(preferredFocusPath)
            ? SelectedImage?.FullPath
            : preferredFocusPath;
        var hasActiveFilters = HasActiveFilterCriteria(
            SearchText,
            SelectedFormatFilter,
            SelectedSizeFilter,
            SelectedReviewFilter,
            SelectedRatingFilter);

        IReadOnlyList<ImageListItemViewModel> filteredItems = hasActiveFilters
            ? _allImages.Where(MatchesActiveFilters).ToArray()
            : _allImages;
        var nextSelectedImage = filteredItems.FirstOrDefault(item =>
                string.Equals(item.FullPath, focusPath, PathComparison.Comparison))
            ?? filteredItems.FirstOrDefault();
        var currentSelectionPath = SelectedImage?.FullPath;
        var nextSelectionPath = nextSelectedImage?.FullPath;

        if (HasSameImageSequence(Images, filteredItems))
        {
            if (!string.Equals(currentSelectionPath, nextSelectionPath, PathComparison.Comparison))
            {
                SelectedImage = nextSelectedImage;
                QueueThumbnailWarmup();
            }

            NotifyFilterStateChanged();
            return;
        }

        _isRefreshingImageCollection = true;
        try
        {
            Images.Clear();
            foreach (var item in filteredItems)
            {
                Images.Add(item);
            }

            SelectedImage = nextSelectedImage;
        }
        finally
        {
            _isRefreshingImageCollection = false;
        }

        NotifyFilterStateChanged();
        OnImageCollectionChanged();
        QueueThumbnailWarmup();
    }

    internal static bool HasActiveFilterCriteria(
        string? searchText,
        string? selectedFormatFilter,
        string? selectedSizeFilter,
        string? selectedReviewFilter,
        string? selectedRatingFilter)
    {
        return !string.IsNullOrWhiteSpace(searchText)
            || !string.Equals(selectedFormatFilter, AllFormatsFilterLabel, StringComparison.Ordinal)
            || !string.Equals(selectedSizeFilter, AllFileSizesFilterLabel, StringComparison.Ordinal)
            || !string.Equals(selectedReviewFilter, AllReviewFilterLabel, StringComparison.Ordinal)
            || !string.Equals(selectedRatingFilter, AllRatingsFilterLabel, StringComparison.Ordinal);
    }

    internal static bool HasSameImageSequence(
        IReadOnlyList<ImageListItemViewModel> currentItems,
        IReadOnlyList<ImageListItemViewModel> nextItems)
    {
        ArgumentNullException.ThrowIfNull(currentItems);
        ArgumentNullException.ThrowIfNull(nextItems);

        if (currentItems.Count != nextItems.Count)
        {
            return false;
        }

        for (var index = 0; index < currentItems.Count; index++)
        {
            if (!string.Equals(currentItems[index].FullPath, nextItems[index].FullPath, PathComparison.Comparison))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool ReplaceObservableCollectionItemsIfChanged<T>(
        ObservableCollection<T> collection,
        IReadOnlyList<T> nextItems,
        IEqualityComparer<T>? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(nextItems);

        comparer ??= EqualityComparer<T>.Default;

        if (collection.Count == nextItems.Count)
        {
            var isSameSequence = true;
            for (var index = 0; index < collection.Count; index++)
            {
                if (!comparer.Equals(collection[index], nextItems[index]))
                {
                    isSameSequence = false;
                    break;
                }
            }

            if (isSameSequence)
            {
                return false;
            }
        }

        collection.Clear();
        foreach (var item in nextItems)
        {
            collection.Add(item);
        }

        return true;
    }

    private bool MatchesActiveFilters(ImageListItemViewModel item)
    {
        if (!string.IsNullOrWhiteSpace(SearchText)
            && item.FileName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) is false
            && item.FullPath.Contains(SearchText, StringComparison.OrdinalIgnoreCase) is false)
        {
            return false;
        }

        if (!string.Equals(SelectedFormatFilter, AllFormatsFilterLabel, StringComparison.Ordinal)
            && !string.Equals(GetFormatFilterLabel(item), SelectedFormatFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!MatchesSizeFilter(item.SizeBytes))
        {
            return false;
        }

        return MatchesReviewFilters(item);
    }

    private bool MatchesSizeFilter(long sizeBytes)
    {
        const long megabyte = 1024L * 1024L;

        return SelectedSizeFilter switch
        {
            LightweightFileSizeFilterLabel => sizeBytes < 2 * megabyte,
            StandardFileSizeFilterLabel => sizeBytes >= 2 * megabyte && sizeBytes < 8 * megabyte,
            LargeFileSizeFilterLabel => sizeBytes >= 8 * megabyte && sizeBytes < 20 * megabyte,
            HugeFileSizeFilterLabel => sizeBytes >= 20 * megabyte,
            _ => true
        };
    }

    private void NotifyFilterStateChanged()
    {
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(CanClearFilters));
        OnPropertyChanged(nameof(FilteredImageCountText));
        OnPropertyChanged(nameof(FilterSummaryText));
    }

    private static string GetFormatFilterLabel(ImageListItemViewModel item)
    {
        var extension = Path.GetExtension(item.FileName);
        return string.IsNullOrWhiteSpace(extension)
            ? "未知格式"
            : extension.TrimStart('.').ToUpperInvariant();
    }
}
