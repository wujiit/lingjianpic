using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ModernImageViewer.Desktop.ViewModels;

namespace ModernImageViewer.Desktop;

public partial class ToolboxWindow : Window
{
    private const double ViewerLayoutTolerance = 0.5;
    private WindowShortcutRouter? _shortcutRouter;
    private readonly HashSet<ScrollViewer> _comparePaneScrollViewers = [];
    private ScrollViewer? _activeComparePaneScrollViewer;
    private bool _isSynchronizingCompareScroll;

    public ToolboxWindow()
    {
        InitializeComponent();
        SizeChanged += ToolboxWindow_OnSizeChanged;
        AddHandler(KeyDownEvent, ToolboxWindow_OnKeyDown, RoutingStrategies.Tunnel);
    }

    public ToolboxWindow(MainWindowViewModel viewModel) : this()
    {
        DataContext = viewModel;
        _shortcutRouter = CreateShortcutRouter(viewModel);
        viewModel.SetToolboxViewportSize(Width, Height);
    }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    protected override void OnClosed(EventArgs e)
    {
        RemoveHandler(KeyDownEvent, ToolboxWindow_OnKeyDown);
        SizeChanged -= ToolboxWindow_OnSizeChanged;
        _comparePaneScrollViewers.Clear();
        _activeComparePaneScrollViewer = null;
        _shortcutRouter = null;
        DataContext = null;
        base.OnClosed(e);
    }

    private void ToolboxWindow_OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SetToolboxViewportSize(e.NewSize.Width, e.NewSize.Height);
        }
    }

    private void CloseToolboxButton_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ToolboxWindow_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        if (_shortcutRouter?.TryHandle(e.Source, e.Key, e.KeyModifiers) == true)
        {
            e.Handled = true;
        }
    }

    private async void ChooseExportFolderButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null)
        {
            ViewModel.SetStatusMessage("当前窗口还没有准备好文件夹选择器。");
            return;
        }

        var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择导出文件夹",
            AllowMultiple = false
        });

        var folderPath = FilterLocalPaths(folders).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(folderPath))
        {
            ViewModel.SetExportDestinationFolder(folderPath);
        }
    }

    private async void OpenFilesButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null)
        {
            ViewModel.SetStatusMessage("当前窗口还没有准备好文件选择器。");
            return;
        }

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择图片",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("图片文件")
                {
                    Patterns =
                    [
                        "*.avif",
                        "*.arw",
                        "*.bmp",
                        "*.cr3",
                        "*.dng",
                        "*.gif",
                        "*.heic",
                        "*.heif",
                        "*.ico",
                        "*.jpeg",
                        "*.jpg",
                        "*.jxl",
                        "*.nef",
                        "*.png",
                        "*.psd",
                        "*.svg",
                        "*.tif",
                        "*.tiff",
                        "*.webp"
                    ]
                }
            ]
        });

        await ViewModel.LoadInputsAsync(FilterLocalPaths(files));
    }

    private async void OpenFolderButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null)
        {
            ViewModel.SetStatusMessage("当前窗口还没有准备好文件夹选择器。");
            return;
        }

        var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择图片文件夹",
            AllowMultiple = false
        });

        await ViewModel.LoadInputsAsync(FilterLocalPaths(folders));
    }

    private async void RenameSelectedButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await ViewModel.RenameSelectedAsync();
    }

    private async void ExportSelectedButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await ViewModel.ExportSelectedAsync();
    }

    private async void CompressSelectedButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await ViewModel.CompressSelectedAsync();
    }

    private async void CleanMetadataButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await ViewModel.CleanMetadataSelectedAsync();
    }

    private async void EditExifButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await ViewModel.EditExifSelectedAsync();
    }

    private async void ChooseWatermarkImageButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null)
        {
            ViewModel.SetStatusMessage("当前窗口还没有准备好图片选择器。");
            return;
        }

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择图片水印",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("图片")
                {
                    Patterns =
                    [
                        "*.png",
                        "*.jpg",
                        "*.jpeg",
                        "*.webp",
                        "*.bmp",
                        "*.tif",
                        "*.tiff",
                        "*.svg"
                    ]
                }
            ]
        });

        var imagePath = FilterLocalPaths(files).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            ViewModel.SetWatermarkImagePath(imagePath);
        }
    }

    private async void AddWatermarkButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await ViewModel.AddWatermarkSelectedAsync();
    }

    private void CancelOperationButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.CancelCurrentOperation();
    }

    private void OpenContainingFolderButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.OpenContainingFolder();
    }

    private void PreviousSlideshowButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.ShowPreviousSlide();
    }

    private void ToggleSlideshowButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.ToggleSlideshow();
    }

    private void NextSlideshowButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.ShowNextSlide();
    }

    private void MarkKeepButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.MarkSelectionAsKeep();
    }

    private void MarkRejectButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.MarkSelectionAsReject();
    }

    private void ClearReviewStateButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.ClearSelectionReviewState();
    }

    private void SelectAllImagesButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.SelectAllImages();
    }

    private void InvertSelectionButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.InvertSelection();
    }

    private void KeepCurrentOnlyButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.KeepCurrentOnly();
    }

    private void SetRating1Button_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.SetSelectionRating(1);
    }

    private void SetRating2Button_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.SetSelectionRating(2);
    }

    private void SetRating3Button_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.SetSelectionRating(3);
    }

    private void SetRating4Button_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.SetSelectionRating(4);
    }

    private void SetRating5Button_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.SetSelectionRating(5);
    }

    private void ClearRatingButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.SetSelectionRating(0);
    }

    private void FocusContactSheetImageButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ImageListItemViewModel item })
        {
            ViewModel.FocusContactSheetImage(item.FullPath);
        }
    }

    private void AddCurrentToCompareButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.AddCurrentToCompare();
    }

    private void RemoveCurrentFromCompareButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.RemoveCurrentFromCompare();
    }

    private void ClearCompareButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.ClearCompareItems();
    }

    private void PromoteCompareItemButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: CompareImageItemViewModel item })
        {
            ViewModel.PromoteCompareItem(item.FullPath);
        }
    }

    private void RemoveCompareItemButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: CompareImageItemViewModel item })
        {
            ViewModel.RemoveCompareItem(item.FullPath);
        }
    }

    private void ComparePaneScrollViewer_OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        _comparePaneScrollViewers.Add(scrollViewer);
        QueueCompareScrollSync(_activeComparePaneScrollViewer ?? _comparePaneScrollViewers.FirstOrDefault());
    }

    private void ComparePaneScrollViewer_OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        _comparePaneScrollViewers.Remove(scrollViewer);
        if (ReferenceEquals(_activeComparePaneScrollViewer, scrollViewer))
        {
            _activeComparePaneScrollViewer = _comparePaneScrollViewers.FirstOrDefault();
        }
    }

    private void ComparePaneScrollViewer_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        _activeComparePaneScrollViewer = scrollViewer;

        if (scrollViewer.DataContext is CompareImageItemViewModel item)
        {
            ViewModel.PromoteCompareItem(item.FullPath);
        }

        if (e.ClickCount != 2)
        {
            return;
        }

        ViewModel.ResetCompareZoom();
        QueueCompareScrollSync(scrollViewer);
        e.Handled = true;
    }

    private void ComparePaneScrollViewer_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        _activeComparePaneScrollViewer = scrollViewer;

        if ((e.KeyModifiers & KeyModifiers.Control) == 0)
        {
            return;
        }

        var horizontalRatio = GetScrollRatio(scrollViewer.Offset.X, scrollViewer.Extent.Width, scrollViewer.Viewport.Width);
        var verticalRatio = GetScrollRatio(scrollViewer.Offset.Y, scrollViewer.Extent.Height, scrollViewer.Viewport.Height);

        ViewModel.AdjustCompareZoom(e.Delta.Y > 0 ? 1.12 : 1 / 1.12);
        QueueCompareScrollSync(scrollViewer, horizontalRatio, verticalRatio);
        e.Handled = true;
    }

    private void ComparePaneScrollViewer_OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_isSynchronizingCompareScroll || sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        if (Math.Abs(e.OffsetDelta.X) < ViewerLayoutTolerance
            && Math.Abs(e.OffsetDelta.Y) < ViewerLayoutTolerance
            && Math.Abs(e.ViewportDelta.X) < ViewerLayoutTolerance
            && Math.Abs(e.ViewportDelta.Y) < ViewerLayoutTolerance
            && Math.Abs(e.ExtentDelta.X) < ViewerLayoutTolerance
            && Math.Abs(e.ExtentDelta.Y) < ViewerLayoutTolerance)
        {
            return;
        }

        _activeComparePaneScrollViewer = scrollViewer;
        SynchronizeCompareScrollOffsets(scrollViewer);
    }

    private async void CopyPathButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!ViewModel.TryBuildOperationPathClipboardText(out var text, out var statusMessage))
        {
            ViewModel.SetStatusMessage(statusMessage);
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            ViewModel.SetStatusMessage("当前窗口还没有准备好剪贴板。");
            return;
        }

        await clipboard.SetTextAsync(text);
        ViewModel.SetStatusMessage(statusMessage);
    }

    private async void CopyToFolderButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var folderPath = await PickSingleFolderAsync("选择复制目标文件夹");
        if (!string.IsNullOrWhiteSpace(folderPath))
        {
            await ViewModel.CopyToFolderAsync(folderPath);
        }
    }

    private async void MoveToFolderButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var folderPath = await PickSingleFolderAsync("选择移动目标文件夹");
        if (!string.IsNullOrWhiteSpace(folderPath))
        {
            await ViewModel.MoveToFolderAsync(folderPath);
        }
    }

    private async void RecycleToTrashButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await ViewModel.RecycleSelectedToTrashAsync();
    }

    private async void FindExactDuplicatesButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await ViewModel.FindExactDuplicatesAsync();
    }

    private async void FindSimilarImagesButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await ViewModel.FindSimilarImagesAsync();
    }

    private void ClearImageMatchesButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.ClearImageMatchResults();
    }

    private void FocusMatchedImageButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ImageMatchItemViewModel item })
        {
            ViewModel.FocusImageByPath(item.FullPath);
        }
    }

    private void SelectImageMatchGroupButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ImageMatchGroupViewModel group })
        {
            ViewModel.SelectImageMatchGroup(group);
        }
    }

    private void SelectImageMatchItemReferenceButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ImageMatchItemViewModel item })
        {
            ViewModel.SelectImageMatchItemReference(item);
        }
    }

    private void ConfirmImageMatchGroupButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ImageMatchGroupViewModel group })
        {
            return;
        }

        var nextGroup = ViewModel.ConfirmImageMatchGroup(group);
        if (nextGroup is not null)
        {
            ViewModel.SelectImageMatchGroup(nextGroup);
            ViewModel.SetStatusMessage($"已确认 {group.Title}，已切换到 {nextGroup.Title}。");
            return;
        }

        ViewModel.SetStatusMessage("相似图片候选已全部确认。");
    }

    private async void RecycleImageMatchGroupButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ImageMatchGroupViewModel group })
        {
            await ViewModel.RecycleImageMatchGroupSuggestionsAsync(group);
        }
    }

    private async void OpenRecentSessionButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: RecentSessionItemViewModel item })
        {
            await ViewModel.OpenRecentSessionAsync(item);
        }
    }

    private void RemoveRecentSessionButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: RecentSessionItemViewModel item })
        {
            ViewModel.RemoveRecentSession(item);
        }
    }

    private void ToggleRecentSessionPinnedButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: RecentSessionItemViewModel item })
        {
            ViewModel.ToggleRecentSessionPinned(item);
        }
    }

    private void ClearRecentSessionsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.ClearRecentSessions();
    }

    private static IEnumerable<string> FilterLocalPaths(IEnumerable<IStorageItem> items)
    {
        foreach (var item in items)
        {
            var localPath = TryGetLocalPath(item);
            if (!string.IsNullOrWhiteSpace(localPath))
            {
                yield return localPath;
            }
        }
    }

    private static string? TryGetLocalPath(IStorageItem item)
    {
        var uri = item.Path;
        return uri is { IsAbsoluteUri: true } && uri.IsFile
            ? uri.LocalPath
            : null;
    }

    private async Task<string?> PickSingleFolderAsync(string title)
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null)
        {
            ViewModel.SetStatusMessage("当前窗口还没有准备好文件夹选择器。");
            return null;
        }

        var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        return FilterLocalPaths(folders).FirstOrDefault();
    }

    private void QueueCompareScrollSync(ScrollViewer? source, double? horizontalRatio = null, double? verticalRatio = null)
    {
        if (source is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () => SynchronizeCompareScrollOffsets(source, horizontalRatio, verticalRatio),
            DispatcherPriority.Background);
    }

    private void SynchronizeCompareScrollOffsets(ScrollViewer source, double? horizontalRatio = null, double? verticalRatio = null)
    {
        if (_comparePaneScrollViewers.Count <= 1)
        {
            return;
        }

        var effectiveHorizontalRatio = horizontalRatio ?? GetScrollRatio(source.Offset.X, source.Extent.Width, source.Viewport.Width);
        var effectiveVerticalRatio = verticalRatio ?? GetScrollRatio(source.Offset.Y, source.Extent.Height, source.Viewport.Height);

        _isSynchronizingCompareScroll = true;
        try
        {
            foreach (var viewer in _comparePaneScrollViewers)
            {
                if (ReferenceEquals(viewer, source)
                    || viewer.Bounds.Width <= 1
                    || viewer.Bounds.Height <= 1)
                {
                    continue;
                }

                viewer.Offset = new Vector(
                    GetScrollOffsetFromRatio(effectiveHorizontalRatio, viewer.Extent.Width, viewer.Viewport.Width),
                    GetScrollOffsetFromRatio(effectiveVerticalRatio, viewer.Extent.Height, viewer.Viewport.Height));
            }
        }
        finally
        {
            _isSynchronizingCompareScroll = false;
        }
    }

    private static double GetScrollRatio(double offset, double extent, double viewport)
    {
        var scrollable = extent - viewport;
        if (scrollable <= ViewerLayoutTolerance)
        {
            return 0;
        }

        return Math.Clamp(offset / scrollable, 0, 1);
    }

    private static double GetScrollOffsetFromRatio(double ratio, double extent, double viewport)
    {
        var scrollable = extent - viewport;
        if (scrollable <= ViewerLayoutTolerance)
        {
            return 0;
        }

        return Math.Clamp(ratio, 0, 1) * scrollable;
    }

    private WindowShortcutRouter CreateShortcutRouter(MainWindowViewModel viewModel)
    {
        return new WindowShortcutRouter(
            viewModel,
            openFolder: () => OpenFolderButton_OnClick(this, new RoutedEventArgs()),
            openFiles: () => OpenFilesButton_OnClick(this, new RoutedEventArgs()),
            openToolbox: Activate,
            recycleToTrash: () => RecycleToTrashButton_OnClick(this, new RoutedEventArgs()),
            toggleImmersive: () => (Owner as MainWindow)?.ToggleImmersiveModeFromShortcut(),
            exitImmersive: () => (Owner as MainWindow)?.ExitImmersiveModeFromShortcut(),
            synchronizeCompareScroll: () => QueueCompareScrollSync(_activeComparePaneScrollViewer ?? _comparePaneScrollViewers.FirstOrDefault()));
    }
}
