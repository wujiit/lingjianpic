using System.Collections.ObjectModel;
using ModernImageViewer.Core;

namespace ModernImageViewer.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool _isUpdatingSelectedImages;

    public ObservableCollection<ImageListItemViewModel> SelectedImages { get; } = [];

    public int SelectedImageCount => SelectedImages.Count;

    public bool HasBatchSelection => SelectedImageCount > 1;

    public bool SelectImagesByPath(
        IEnumerable<string> fullPaths,
        string? preferredPrimaryPath = null,
        string? statusMessage = null)
    {
        var pathSet = fullPaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(PathComparison.Comparer)
            .ToHashSet(PathComparison.Comparer);
        if (pathSet.Count == 0)
        {
            return false;
        }

        ImageListItemViewModel[] orderedSelection;
        if (pathSet.Count >= Math.Max(1, Images.Count / 2))
        {
            orderedSelection = Images
                .Where(item => pathSet.Contains(item.FullPath))
                .ToArray();
        }
        else
        {
            var lookupSnapshot = GetOrCreateImageMatchItemLookupSnapshot();
            var orderedEntries = new List<ImageMatchVisibleItemLookupEntry>(pathSet.Count);
            foreach (var path in pathSet)
            {
                if (lookupSnapshot.VisibleItemsByPath.TryGetValue(path, out var entry))
                {
                    orderedEntries.Add(entry);
                }
            }

            orderedSelection = orderedEntries
                .OrderBy(static entry => entry.Index)
                .Select(static entry => entry.Item)
                .ToArray();
        }

        if (orderedSelection.Length == 0)
        {
            return false;
        }

        var preferredPrimary = !string.IsNullOrWhiteSpace(preferredPrimaryPath)
            ? orderedSelection.FirstOrDefault(item =>
                string.Equals(item.FullPath, Path.GetFullPath(preferredPrimaryPath), PathComparison.Comparison))
            : null;
        var retainedPrimary = SelectedImage is null
            ? null
            : orderedSelection.FirstOrDefault(item =>
                string.Equals(item.FullPath, SelectedImage.FullPath, PathComparison.Comparison));
        var primary = preferredPrimary ?? retainedPrimary ?? orderedSelection[0];

        ReplaceSelectedImages(orderedSelection);
        SelectedImage = primary;

        if (!string.IsNullOrWhiteSpace(statusMessage))
        {
            OperationStatusText = statusMessage;
        }

        return true;
    }

    public void SelectAllImages()
    {
        if (Images.Count == 0)
        {
            OperationStatusText = "当前列表里还没有可选图片。";
            return;
        }

        SelectImagesByPath(
            Images.Select(static item => item.FullPath),
            SelectedImage?.FullPath,
            Images.Count == 1
                ? "已选中当前这 1 张图片。"
                : $"已选中当前列表的 {Images.Count} 张图片。");
    }

    public void InvertSelection()
    {
        if (Images.Count == 0)
        {
            OperationStatusText = "当前列表里还没有可选图片。";
            return;
        }

        var selectedPathSet = SelectedImages
            .Select(static item => item.FullPath)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(PathComparison.Comparer);

        if (selectedPathSet.Count == 0)
        {
            SelectAllImages();
            return;
        }

        var invertedSelection = Images
            .Where(item => !selectedPathSet.Contains(item.FullPath))
            .ToArray();
        if (invertedSelection.Length == 0)
        {
            KeepCurrentOnly();
            return;
        }

        SelectImagesByPath(
            invertedSelection.Select(static item => item.FullPath),
            invertedSelection[0].FullPath,
            invertedSelection.Length == 1
                ? "反选后当前只剩 1 张图片。"
                : $"反选完成，当前选中 {invertedSelection.Length} 张图片。");
    }

    public void KeepCurrentOnly()
    {
        if (SelectedImage is null)
        {
            OperationStatusText = "还没有当前图片可保留。";
            return;
        }

        ReplaceSelectedImages([SelectedImage]);
        OperationStatusText = $"已只保留当前图片：{SelectedImage.FileName}";
    }

    public void SyncSelectedImages(IEnumerable<ImageListItemViewModel> selectedItems)
    {
        var selectedPaths = selectedItems
            .Where(static item => item is not null)
            .Select(static item => item.FullPath)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(PathComparison.Comparer)
            .ToHashSet(PathComparison.Comparer);

        var orderedSelection = Images
            .Where(item => selectedPaths.Contains(item.FullPath))
            .ToArray();

        ReplaceSelectedImages(orderedSelection);
    }

    private void SyncSelectedImagesFromPrimary()
    {
        if (_isUpdatingSelectedImages || _isRefreshingImageCollection)
        {
            return;
        }

        if (SelectedImage is null)
        {
            ReplaceSelectedImages([]);
            return;
        }

        if (HasBatchSelection
            && SelectedImages.Any(item => string.Equals(item.FullPath, SelectedImage.FullPath, PathComparison.Comparison)))
        {
            return;
        }

        ReplaceSelectedImages([SelectedImage]);
    }

    private void RefreshSelectedImagesAfterCollectionChange()
    {
        if (_isUpdatingSelectedImages)
        {
            return;
        }

        if (Images.Count == 0)
        {
            ReplaceSelectedImages([]);
            return;
        }

        if (SelectedImages.Count == 0)
        {
            SyncSelectedImagesFromPrimary();
            return;
        }

        var selectedPaths = SelectedImages
            .Select(static item => item.FullPath)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(PathComparison.Comparer);

        var retainedSelection = Images
            .Where(item => selectedPaths.Contains(item.FullPath))
            .ToArray();

        if (retainedSelection.Length == 0 && SelectedImage is not null)
        {
            retainedSelection = Images
                .Where(item => string.Equals(item.FullPath, SelectedImage.FullPath, PathComparison.Comparison))
                .Take(1)
                .ToArray();
        }

        ReplaceSelectedImages(retainedSelection);
    }

    private void ReplaceSelectedImages(IEnumerable<ImageListItemViewModel> items)
    {
        _isUpdatingSelectedImages = true;
        try
        {
            ReplaceObservableCollectionItemsIfChanged(SelectedImages, items.ToArray());
        }
        finally
        {
            _isUpdatingSelectedImages = false;
        }

        OnPropertyChanged(nameof(SelectedImageCount));
        OnPropertyChanged(nameof(HasBatchSelection));
        NotifyReviewStateChanged();
        NotifyExportSelectionStateChanged();
        NotifyRenameStateChanged();
        NotifyFileOperationStateChanged();
    }
}
