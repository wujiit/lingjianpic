using ModernImageViewer.Core;
using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const string AllReviewFilterLabel = "全部标记";
    private const string ReviewedFilterLabel = "仅已标记";
    private const string UnreviewedFilterLabel = "仅未标记";
    private const string KeepReviewFilterLabel = "仅保留";
    private const string RejectReviewFilterLabel = "仅待删";
    private const string AllRatingsFilterLabel = "全部星级";
    private const string UnratedFilterLabel = "仅未打星";
    private const string OneStarFilterLabel = "1 星及以上";
    private const string TwoStarsFilterLabel = "2 星及以上";
    private const string ThreeStarsFilterLabel = "3 星及以上";
    private const string FourStarsFilterLabel = "4 星及以上";
    private const string FiveStarsFilterLabel = "5 星及以上";

    private readonly ImageReviewStateStore _reviewStateStore = new();
    private readonly Dictionary<string, ImageReviewStateSnapshot> _reviewStates = new(PathComparison.Comparer);
    private string _selectedReviewFilter = AllReviewFilterLabel;
    private string _selectedRatingFilter = AllRatingsFilterLabel;

    public IReadOnlyList<string> ReviewFilterOptions { get; } =
    [
        AllReviewFilterLabel,
        ReviewedFilterLabel,
        UnreviewedFilterLabel,
        KeepReviewFilterLabel,
        RejectReviewFilterLabel
    ];

    public IReadOnlyList<string> RatingFilterOptions { get; } =
    [
        AllRatingsFilterLabel,
        UnratedFilterLabel,
        OneStarFilterLabel,
        TwoStarsFilterLabel,
        ThreeStarsFilterLabel,
        FourStarsFilterLabel,
        FiveStarsFilterLabel
    ];

    public string SelectedReviewFilter
    {
        get => _selectedReviewFilter;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? AllReviewFilterLabel : value;
            if (!SetProperty(ref _selectedReviewFilter, normalized))
            {
                return;
            }

            ApplyFilters();
        }
    }

    public string SelectedRatingFilter
    {
        get => _selectedRatingFilter;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? AllRatingsFilterLabel : value;
            if (!SetProperty(ref _selectedRatingFilter, normalized))
            {
                return;
            }

            ApplyFilters();
        }
    }

    public bool CanMarkSelection => ResolveReviewTargets().Count > 0 && !IsExportProcessing;

    public bool CanClearReviewState => ResolveReviewTargets().Any(static item => item.HasReviewState) && !IsExportProcessing;

    public string ReviewOverviewText
    {
        get
        {
            if (_allImages.Count == 0)
            {
                return "载入图片后，可以在这里给图片做保留、待删和星级标记。";
            }

            var markedCount = _allImages.Count(static item => item.HasReviewState);
            var keepCount = _allImages.Count(static item => item.ReviewStatus == ImageReviewStatus.Keep);
            var rejectCount = _allImages.Count(static item => item.ReviewStatus == ImageReviewStatus.Reject);
            var ratedCount = _allImages.Count(static item => item.Rating > 0);
            return $"当前集合已标记 {markedCount} / {_allImages.Count} 张，保留 {keepCount}，待删 {rejectCount}，已打星 {ratedCount}。";
        }
    }

    public string ReviewActionScopeText => ProcessCurrentCollection
        ? Images.Count switch
        {
            <= 0 => "当前没有可批量标记的图片。",
            1 => "当前会标记当前列表中的 1 张图片。",
            _ => $"当前会批量标记当前列表中的 {Images.Count} 张图片。"
        }
        : SelectedImage is null
            ? "当前会标记右侧选中的那一张图片。"
            : $"当前只会标记：{SelectedImage.FileName}";

    public string ReviewSelectionSummaryText
    {
        get
        {
            var items = ResolveReviewTargets();
            if (items.Count == 0)
            {
                return "选中图片后，可以快速标记保留、待删，或者补上星级。";
            }

            if (items.Count == 1)
            {
                var item = items[0];
                return $"{item.FileName}：{item.ReviewSummaryText}";
            }

            var keepCount = items.Count(static item => item.ReviewStatus == ImageReviewStatus.Keep);
            var rejectCount = items.Count(static item => item.ReviewStatus == ImageReviewStatus.Reject);
            var ratedCount = items.Count(static item => item.Rating > 0);
            return $"当前作用范围 {items.Count} 张，其中保留 {keepCount}，待删 {rejectCount}，已打星 {ratedCount}。";
        }
    }

    public string CurrentImageReviewText => SelectedImage is null
        ? "还没有选中图片。"
        : $"当前图片：{SelectedImage.ReviewSummaryText}";

    private void InitializeReviewSettings()
    {
        _reviewStates.Clear();
        foreach (var state in _reviewStateStore.LoadStates())
        {
            var normalizedPath = NormalizeReviewPath(state.Path);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                continue;
            }

            var normalizedRating = Math.Clamp(state.Rating, 0, 5);
            if (normalizedRating == 0 && state.ReviewStatus == ImageReviewStatus.None)
            {
                continue;
            }

            _reviewStates[normalizedPath] = new ImageReviewStateSnapshot(
                normalizedPath,
                normalizedRating,
                state.ReviewStatus);
        }
    }

    private void OnReviewSelectionChanged(ImageListItemViewModel? _)
    {
        NotifyReviewStateChanged();
    }

    public void MarkSelectionAsKeep()
    {
        SetSelectionReviewStatus(ImageReviewStatus.Keep);
    }

    public void MarkSelectionAsReject()
    {
        SetSelectionReviewStatus(ImageReviewStatus.Reject);
    }

    public void ClearSelectionReviewState()
    {
        var items = ResolveReviewTargets();
        if (items.Count == 0)
        {
            return;
        }

        foreach (var item in items)
        {
            UpdateReviewState(item, 0, ImageReviewStatus.None);
        }

        PersistReviewStates();
        RefreshReviewUiAfterStateChange(items);
        OperationStatusText = items.Count == 1
            ? $"已清空 {items[0].FileName} 的筛图标记"
            : $"已清空 {items.Count} 张图片的筛图标记";
    }

    public void SetSelectionRating(int rating)
    {
        var normalizedRating = Math.Clamp(rating, 0, 5);
        var items = ResolveReviewTargets();
        if (items.Count == 0)
        {
            return;
        }

        foreach (var item in items)
        {
            UpdateReviewState(item, normalizedRating, item.ReviewStatus);
        }

        PersistReviewStates();
        RefreshReviewUiAfterStateChange(items);
        OperationStatusText = items.Count == 1
            ? $"已把 {items[0].FileName} 设为 {normalizedRating} 星"
            : $"已为 {items.Count} 张图片设置 {normalizedRating} 星";
    }

    private void SetSelectionReviewStatus(ImageReviewStatus reviewStatus)
    {
        var items = ResolveReviewTargets();
        if (items.Count == 0)
        {
            return;
        }

        foreach (var item in items)
        {
            UpdateReviewState(item, item.Rating, reviewStatus);
        }

        PersistReviewStates();
        RefreshReviewUiAfterStateChange(items);
        var actionText = reviewStatus switch
        {
            ImageReviewStatus.Keep => "保留",
            ImageReviewStatus.Reject => "待删",
            _ => "未标记"
        };
        OperationStatusText = items.Count == 1
            ? $"{items[0].FileName} 已标记为{actionText}"
            : $"已将 {items.Count} 张图片标记为{actionText}";
    }

    private void ApplyReviewState(ImageListItemViewModel item)
    {
        var normalizedPath = NormalizeReviewPath(item.FullPath);
        if (!string.IsNullOrWhiteSpace(normalizedPath)
            && _reviewStates.TryGetValue(normalizedPath, out var state))
        {
            item.ApplyReviewState(state.Rating, state.ReviewStatus);
            return;
        }

        item.ApplyReviewState(0, ImageReviewStatus.None);
    }

    private void ApplyReviewStates(IEnumerable<ImageListItemViewModel> items)
    {
        foreach (var item in items)
        {
            ApplyReviewState(item);
        }
    }

    private void UpdateReviewState(ImageListItemViewModel item, int rating, ImageReviewStatus reviewStatus)
    {
        var normalizedRating = Math.Clamp(rating, 0, 5);
        item.ApplyReviewState(normalizedRating, reviewStatus);

        var normalizedPath = NormalizeReviewPath(item.FullPath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return;
        }

        if (normalizedRating == 0 && reviewStatus == ImageReviewStatus.None)
        {
            _reviewStates.Remove(normalizedPath);
            return;
        }

        _reviewStates[normalizedPath] = new ImageReviewStateSnapshot(normalizedPath, normalizedRating, reviewStatus);
    }

    private void ReplacePathsInReviewStates(IReadOnlyDictionary<string, string> completedPathMap)
    {
        if (completedPathMap.Count == 0 || _reviewStates.Count == 0)
        {
            return;
        }

        foreach (var pair in completedPathMap)
        {
            var originalPath = NormalizeReviewPath(pair.Key);
            var updatedPath = NormalizeReviewPath(pair.Value);
            if (string.IsNullOrWhiteSpace(originalPath)
                || string.IsNullOrWhiteSpace(updatedPath)
                || !_reviewStates.Remove(originalPath, out var existingState))
            {
                continue;
            }

            _reviewStates[updatedPath] = existingState with { Path = updatedPath };
        }

        PersistReviewStates();
    }

    private void RefreshReviewUiAfterStateChange(IReadOnlyList<ImageListItemViewModel> items)
    {
        NotifyReviewStateChanged();

        if (IsAllReviewFilterSelected() && IsAllRatingFilterSelected())
        {
            return;
        }

        ApplyFilters(items.FirstOrDefault()?.FullPath ?? SelectedImage?.FullPath);
    }

    private IReadOnlyList<ImageListItemViewModel> ResolveReviewTargets()
    {
        return ResolveOperationTargets();
    }

    private bool MatchesReviewFilters(ImageListItemViewModel item)
    {
        return MatchesReviewFilter(item) && MatchesRatingFilter(item);
    }

    private bool MatchesReviewFilter(ImageListItemViewModel item)
    {
        return SelectedReviewFilter switch
        {
            ReviewedFilterLabel => item.HasReviewState,
            UnreviewedFilterLabel => !item.HasReviewState,
            KeepReviewFilterLabel => item.ReviewStatus == ImageReviewStatus.Keep,
            RejectReviewFilterLabel => item.ReviewStatus == ImageReviewStatus.Reject,
            _ => true
        };
    }

    private bool MatchesRatingFilter(ImageListItemViewModel item)
    {
        if (string.Equals(SelectedRatingFilter, AllRatingsFilterLabel, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(SelectedRatingFilter, UnratedFilterLabel, StringComparison.Ordinal))
        {
            return item.Rating == 0;
        }

        return TryGetMinimumRating(SelectedRatingFilter, out var minimumRating) && item.Rating >= minimumRating;
    }

    private static bool TryGetMinimumRating(string ratingFilter, out int minimumRating)
    {
        minimumRating = ratingFilter switch
        {
            OneStarFilterLabel => 1,
            TwoStarsFilterLabel => 2,
            ThreeStarsFilterLabel => 3,
            FourStarsFilterLabel => 4,
            FiveStarsFilterLabel => 5,
            _ => 0
        };

        return minimumRating > 0;
    }

    private void PersistReviewStates()
    {
        _reviewStateStore.SaveStates(
            _reviewStates.Values
                .OrderBy(static item => item.Path, PathComparison.Comparer)
                .ToList());
    }

    private void NotifyReviewStateChanged()
    {
        OnPropertyChanged(nameof(CanMarkSelection));
        OnPropertyChanged(nameof(CanClearReviewState));
        OnPropertyChanged(nameof(ReviewOverviewText));
        OnPropertyChanged(nameof(ReviewActionScopeText));
        OnPropertyChanged(nameof(ReviewSelectionSummaryText));
        OnPropertyChanged(nameof(CurrentImageReviewText));
    }

    private bool IsAllReviewFilterSelected()
    {
        return string.Equals(SelectedReviewFilter, AllReviewFilterLabel, StringComparison.Ordinal);
    }

    private bool IsAllRatingFilterSelected()
    {
        return string.Equals(SelectedRatingFilter, AllRatingsFilterLabel, StringComparison.Ordinal);
    }

    private static string? NormalizeReviewPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }
}
