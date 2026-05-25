using System.Collections.ObjectModel;
using ModernImageViewer.Core;

namespace ModernImageViewer.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const int MaxCompareItemCount = 4;
    private const double MainCompareItemWidth = 360;
    private const double MainComparePreviewHeight = 260;
    private const double ComparePreviewSurfacePadding = 24;
    private const double ComparePreviewDecodeOverscanFactor = 3.1;
    private const int ComparePreviewMinLongEdge = 960;
    private const int ComparePreviewMaxLongEdge = 3200;
    private readonly SemaphoreSlim _compareLoadGate = new(2, 2);
    private readonly Dictionary<string, int> _pendingComparePreviewRequests = new(PathComparison.Comparer);
    private CancellationTokenSource? _compareLoadCts;
    private double _compareZoomPercent = 100;

    public ObservableCollection<CompareImageItemViewModel> CompareItems { get; } = [];

    public bool HasCompareItems => CompareItems.Count > 0;

    public bool ShowCompareEmptyState => !HasCompareItems;

    public bool HasEnoughCompareItems => CompareItems.Count > 1;

    public bool CanAddCurrentToCompare => SelectedImage is not null
        && !IsExportProcessing
        && CompareItems.Count < MaxCompareItemCount
        && !ContainsCompareItem(SelectedImage.FullPath);

    public bool CanRemoveCurrentFromCompare => SelectedImage is not null
        && ContainsCompareItem(SelectedImage.FullPath)
        && !IsExportProcessing;

    public bool CanClearCompare => CompareItems.Count > 0 && !IsExportProcessing;

    public double CompareZoomPercent
    {
        get => _compareZoomPercent;
        set
        {
            var normalized = Math.Clamp(value, 25, 300);
            if (!SetProperty(ref _compareZoomPercent, normalized))
            {
                return;
            }

            ApplyCompareZoom();
            MaybeReloadComparePreviewsForDecodeSize();
            OnPropertyChanged(nameof(CompareZoomText));
            OnPropertyChanged(nameof(CompareSummaryText));
        }
    }

    public string CompareZoomText => $"{CompareZoomPercent:0}%";

    public string AddCurrentToCompareActionText
    {
        get
        {
            if (SelectedImage is null)
            {
                return "加入当前图";
            }

            if (ContainsCompareItem(SelectedImage.FullPath))
            {
                return "当前图已在对比区";
            }

            return CompareItems.Count >= MaxCompareItemCount ? "对比区已满" : "加入当前图";
        }
    }

    public string CompareSummaryText => CompareItems.Count switch
    {
        <= 0 => "选择图片后可加入对比区。",
        1 => "再加入一张图片即可开始对比。",
        _ => $"当前正在对比 {CompareItems.Count} 张图片，统一缩放 {CompareZoomText}。"
    };

    public string CompareEmptyText => CompareItems.Count switch
    {
        <= 0 => "当前还没有对比图片。",
        1 => "当前只有 1 张图片，再加入 1 张即可并排查看。",
        _ => "当前对比区没有可显示的画面。"
    };

    public void AddCurrentToCompare()
    {
        var selected = SelectedImage;
        if (selected is null)
        {
            OperationStatusText = "请先选中一张图片，再加入对比区。";
            return;
        }

        if (ContainsCompareItem(selected.FullPath))
        {
            OperationStatusText = $"{selected.FileName} 已经在对比区里了。";
            return;
        }

        if (CompareItems.Count >= MaxCompareItemCount)
        {
            OperationStatusText = $"对比区最多同时保留 {MaxCompareItemCount} 张图。";
            return;
        }

        var item = new CompareImageItemViewModel(selected);
        item.SetZoomFactor(CompareZoomPercent / 100d);
        CompareItems.Add(item);
        RefreshCompareRoles();
        NotifyCompareStateChanged();
        EnsureComparePreviewLoaded(item);
        OperationStatusText = CompareItems.Count == 1
            ? $"已把 {selected.FileName} 放进对比区，再加 1 张就能开始对比。"
            : $"已把 {selected.FileName} 加入对比区。";
    }

    public void RemoveCurrentFromCompare()
    {
        var selectedPath = SelectedImage?.FullPath;
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            OperationStatusText = "请先选中一张图片，再从对比区移除。";
            return;
        }

        if (!RemoveCompareItemCore(selectedPath, out var removedFileName))
        {
            OperationStatusText = "当前选中的图片不在对比区里。";
            return;
        }

        OperationStatusText = $"已把 {removedFileName} 从对比区移除。";
    }

    public void ClearCompareItems()
    {
        if (CompareItems.Count == 0)
        {
            OperationStatusText = "对比区已经是空的。";
            return;
        }

        ClearCompareItemsCore(resetZoom: true);
        OperationStatusText = "已清空对比区。";
    }

    public void PromoteCompareItem(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return;
        }

        var index = FindCompareItemIndex(fullPath);
        if (index <= 0)
        {
            return;
        }

        var item = CompareItems[index];
        CompareItems.RemoveAt(index);
        CompareItems.Insert(0, item);
        RefreshCompareRoles();
        NotifyCompareStateChanged();
        OperationStatusText = $"已把 {item.FileName} 设为主图。";
    }

    public void RemoveCompareItem(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return;
        }

        if (RemoveCompareItemCore(fullPath, out var removedFileName))
        {
            OperationStatusText = $"已把 {removedFileName} 从对比区移除。";
        }
    }

    public void AdjustCompareZoom(double multiplier)
    {
        if (!HasCompareItems)
        {
            return;
        }

        CompareZoomPercent = Math.Clamp(CompareZoomPercent * multiplier, 25, 300);
    }

    public void ResetCompareZoom()
    {
        if (!HasCompareItems || Math.Abs(CompareZoomPercent - 100) < 0.0001)
        {
            return;
        }

        CompareZoomPercent = 100;
        OperationStatusText = "已把对比区缩放恢复到 100%。";
    }

    private void EnsureComparePreviewLoaded(CompareImageItemViewModel item)
    {
        if (!CanQueueComparePreviewLoad(item.HasPreview, item.IsPreviewLoading, _pendingComparePreviewRequests.ContainsKey(item.FullPath)))
        {
            return;
        }

        EnsureComparePreviewLoaded(item, GetDesiredComparePreviewDecodeLongEdge());
    }

    private void EnsureComparePreviewLoaded(CompareImageItemViewModel item, int requestedLongEdge)
    {
        if (item.IsPreviewLoading
            || CanReuseComparePreview(item.PreviewDecodeLongEdge, requestedLongEdge)
            || CanReusePendingComparePreviewRequest(item.FullPath, requestedLongEdge))
        {
            return;
        }

        if (item.HasPreview)
        {
            item.MarkRefreshing();
        }
        else
        {
            item.MarkLoading();
        }

        _pendingComparePreviewRequests[item.FullPath] = requestedLongEdge;
        var token = (_compareLoadCts ??= new CancellationTokenSource()).Token;
        ForgetBackgroundTask(LoadComparePreviewAsync(item, requestedLongEdge, token));
    }

    private async Task LoadComparePreviewAsync(CompareImageItemViewModel item, int decodeLongEdge, CancellationToken cancellationToken)
    {
        var hadPreviewBeforeLoad = item.HasPreview;
        try
        {
            await _compareLoadGate.WaitAsync(cancellationToken);
            try
            {
                var result = await Task.Run(
                    () => _previewImageService.LoadPreview(item.Signature, decodeLongEdge),
                    cancellationToken);

                if (cancellationToken.IsCancellationRequested || !ContainsCompareItem(item.FullPath))
                {
                    result.Bitmap?.Dispose();
                    return;
                }

                if (result.Bitmap is not null)
                {
                    item.UpdatePreview(result.Bitmap, decodeLongEdge);
                    item.SetZoomFactor(CompareZoomPercent / 100d);
                    MaybeReloadComparePreviewForGrowth(item);
                    return;
                }

                if (hadPreviewBeforeLoad)
                {
                    item.ResetLoadingState();
                    return;
                }

                item.MarkUnavailable(result.StatusText, result.DetailsText);
            }
            finally
            {
                _compareLoadGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            if (ContainsCompareItem(item.FullPath))
            {
                item.ResetLoadingState();
            }
        }
        catch (Exception ex)
        {
            if (ContainsCompareItem(item.FullPath))
            {
                if (hadPreviewBeforeLoad)
                {
                    item.ResetLoadingState();
                }
                else
                {
                    item.MarkUnavailable("预览加载失败", ex.Message);
                }
            }
        }
        finally
        {
            if (_pendingComparePreviewRequests.TryGetValue(item.FullPath, out var pendingLongEdge)
                && pendingLongEdge == decodeLongEdge)
            {
                _pendingComparePreviewRequests.Remove(item.FullPath);
            }
        }
    }

    private void RefreshCompareRoles()
    {
        for (var index = 0; index < CompareItems.Count; index++)
        {
            CompareItems[index].SetRole(index == 0, index + 1);
        }
    }

    private void ApplyCompareZoom()
    {
        var zoomFactor = CompareZoomPercent / 100d;
        foreach (var item in CompareItems)
        {
            item.SetZoomFactor(zoomFactor);
        }
    }

    private void MaybeReloadComparePreviewsForDecodeSize()
    {
        if (CompareItems.Count == 0 || IsExportProcessing)
        {
            return;
        }

        var desiredLongEdge = GetDesiredComparePreviewDecodeLongEdge();
        foreach (var item in CompareItems)
        {
            if (!item.HasPreview
                || item.IsPreviewLoading
                || !ShouldReloadComparePreviewForGrowth(desiredLongEdge, item.PreviewDecodeLongEdge))
            {
                continue;
            }

            EnsureComparePreviewLoaded(item, desiredLongEdge);
        }
    }

    private void MaybeReloadComparePreviewForGrowth(CompareImageItemViewModel item)
    {
        if (!item.HasPreview || item.IsPreviewLoading || IsExportProcessing)
        {
            return;
        }

        var desiredLongEdge = GetDesiredComparePreviewDecodeLongEdge();
        if (!ShouldReloadComparePreviewForGrowth(desiredLongEdge, item.PreviewDecodeLongEdge))
        {
            return;
        }

        EnsureComparePreviewLoaded(item, desiredLongEdge);
    }

    private int GetDesiredComparePreviewDecodeLongEdge()
    {
        return CalculateComparePreviewDecodeLongEdge(
            ShowCompareViewerSurface ? MainCompareItemWidth : 0,
            ShowCompareViewerSurface ? MainComparePreviewHeight : 0,
            ToolboxCompareItemWidth,
            ToolboxComparePreviewHeight,
            CompareZoomPercent,
            CompareItems.Count);
    }

    private void NotifyCompareStateChanged()
    {
        OnPropertyChanged(nameof(HasCompareItems));
        OnPropertyChanged(nameof(ShowCompareEmptyState));
        OnPropertyChanged(nameof(HasEnoughCompareItems));
        OnPropertyChanged(nameof(CanAddCurrentToCompare));
        OnPropertyChanged(nameof(CanRemoveCurrentFromCompare));
        OnPropertyChanged(nameof(CanClearCompare));
        OnPropertyChanged(nameof(AddCurrentToCompareActionText));
        OnPropertyChanged(nameof(CompareSummaryText));
        OnPropertyChanged(nameof(CompareEmptyText));
        OnPropertyChanged(nameof(CompareZoomText));
        OnPropertyChanged(nameof(ViewerHeadlineText));
        OnPropertyChanged(nameof(ViewerSubtitleText));
        OnPropertyChanged(nameof(ViewerPathText));
        NotifyLayoutStateChanged();
        MaybeReloadComparePreviewsForDecodeSize();
    }

    private bool RemoveCompareItemCore(string fullPath, out string removedFileName)
    {
        var index = FindCompareItemIndex(fullPath);
        if (index < 0)
        {
            removedFileName = string.Empty;
            return false;
        }

        var item = CompareItems[index];
        removedFileName = item.FileName;
        CompareItems.RemoveAt(index);
        item.Dispose();
        RefreshCompareRoles();
        NotifyCompareStateChanged();
        return true;
    }

    private int FindCompareItemIndex(string fullPath)
    {
        for (var index = 0; index < CompareItems.Count; index++)
        {
            if (string.Equals(CompareItems[index].FullPath, fullPath, PathComparison.Comparison))
            {
                return index;
            }
        }

        return -1;
    }

    private bool ContainsCompareItem(string fullPath)
    {
        return FindCompareItemIndex(fullPath) >= 0;
    }

    private void ClearCompareItemsCore(bool resetZoom)
    {
        CancelComparePreviewLoading();

        foreach (var item in CompareItems)
        {
            item.Dispose();
        }

        CompareItems.Clear();

        if (resetZoom && Math.Abs(CompareZoomPercent - 100) > 0.0001)
        {
            _compareZoomPercent = 100;
            OnPropertyChanged(nameof(CompareZoomPercent));
        }

        NotifyCompareStateChanged();
    }

    private void CancelComparePreviewLoading()
    {
        CancelQuietly(_compareLoadCts);
        DisposeQuietly(_compareLoadCts);
        _compareLoadCts = null;
        _pendingComparePreviewRequests.Clear();
    }

    internal static bool CanQueueComparePreviewLoad(bool hasPreview, bool isPreviewLoading, bool hasPendingRequest)
    {
        return !hasPreview && !isPreviewLoading && !hasPendingRequest;
    }

    internal static bool CanReuseComparePreview(int loadedLongEdge, int requestedLongEdge)
    {
        return loadedLongEdge >= requestedLongEdge;
    }

    internal static bool ShouldReloadComparePreviewForGrowth(int desiredLongEdge, int loadedLongEdge)
    {
        return desiredLongEdge > loadedLongEdge * 1.2;
    }

    internal static int CalculateComparePreviewDecodeLongEdge(
        double mainItemWidth,
        double mainPreviewHeight,
        double toolboxItemWidth,
        double toolboxPreviewHeight,
        double zoomPercent,
        int compareItemCount)
    {
        var mainViewportWidth = Math.Max(0, mainItemWidth - ComparePreviewSurfacePadding);
        var mainViewportHeight = Math.Max(0, mainPreviewHeight - ComparePreviewSurfacePadding);
        var toolboxViewportWidth = Math.Max(0, toolboxItemWidth - ComparePreviewSurfacePadding);
        var toolboxViewportHeight = Math.Max(0, toolboxPreviewHeight - ComparePreviewSurfacePadding);
        var viewportLongEdge = Math.Max(
            1,
            Math.Max(
                Math.Max(mainViewportWidth, mainViewportHeight),
                Math.Max(toolboxViewportWidth, toolboxViewportHeight)));
        var zoomScale = Math.Clamp(zoomPercent, 25, 300) / 100d;
        var itemDensityFactor = compareItemCount switch
        {
            <= 1 => 1.15,
            2 => 1.08,
            3 => 1.0,
            _ => 0.92
        };
        var rawLongEdge = viewportLongEdge * zoomScale * ComparePreviewDecodeOverscanFactor * itemDensityFactor;
        var roundedLongEdge = (int)(Math.Ceiling(rawLongEdge / 20d) * 20d);
        return (int)Math.Clamp(roundedLongEdge, ComparePreviewMinLongEdge, ComparePreviewMaxLongEdge);
    }

    private bool CanReusePendingComparePreviewRequest(string fullPath, int requestedLongEdge)
    {
        return _pendingComparePreviewRequests.TryGetValue(fullPath, out var pendingLongEdge)
            && pendingLongEdge >= requestedLongEdge;
    }

    private void ResetCompareItemsForCollectionReload()
    {
        ClearCompareItemsCore(resetZoom: true);
    }
}


