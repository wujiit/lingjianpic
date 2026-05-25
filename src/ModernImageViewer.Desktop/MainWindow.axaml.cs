using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ModernImageViewer.Core;
using ModernImageViewer.Desktop.ViewModels;

namespace ModernImageViewer.Desktop;

public partial class MainWindow : Window
{
    private const double ViewerLayoutTolerance = 0.5;
    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(4);
    private readonly WindowShortcutRouter _shortcutRouter;
    private ToolboxWindow? _toolboxWindow;
    private bool _isPreviewPanning;
    private Point _previewPanStart;
    private Vector _previewScrollStart;
    private ScrollViewer? _activeComparePaneScrollViewer;
    private readonly HashSet<ScrollViewer> _comparePaneScrollViewers = [];
    private bool _isSynchronizingCompareScroll;
    private bool _isSyncingThumbnailSelection;
    private bool _isImmersiveMode;
    private bool _isClosing;
    private WindowState _windowStateBeforeImmersive = WindowState.Normal;
    private SystemDecorations _systemDecorationsBeforeImmersive = SystemDecorations.Full;

    private static readonly string[] SupportedPatterns =
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
    ];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
        _shortcutRouter = CreateShortcutRouter();
        ViewModel.PropertyChanged += ViewModel_OnPropertyChanged;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, Window_OnDragOver);
        AddHandler(DragDrop.DropEvent, Window_OnDrop);
        SizeChanged += MainWindow_OnSizeChanged;
        AddHandler(KeyDownEvent, Window_OnKeyDown, RoutingStrategies.Tunnel);
        ViewModel.SetLayoutViewportSize(Width, Height);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Dispatcher.UIThread.Post(FocusPrimaryViewerSurface, DispatcherPriority.Background);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_isClosing)
        {
            e.Cancel = true;
            _isClosing = true;
            IsEnabled = false;
            _ = CompleteShutdownAndCloseAsync();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        var viewModel = ViewModel;
        RemoveHandler(KeyDownEvent, Window_OnKeyDown);
        SizeChanged -= MainWindow_OnSizeChanged;
        viewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
        _comparePaneScrollViewers.Clear();
        _activeComparePaneScrollViewer = null;
        _isPreviewPanning = false;

        CloseToolboxWindow();

        if (viewModel is IDisposable disposable)
        {
            disposable.Dispose();
        }

        DataContext = null;
        base.OnClosed(e);

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.TryShutdown();
        }
    }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    private void MainWindow_OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ViewModel.SetLayoutViewportSize(e.NewSize.Width, e.NewSize.Height);
    }

    private void ViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.SelectedImage)
            or nameof(MainWindowViewModel.SelectedImages)
            or nameof(MainWindowViewModel.ShowFilmstripInMain))
        {
            Dispatcher.UIThread.Post(SyncSelectionInLists, DispatcherPriority.Background);
        }

        if (e.PropertyName is nameof(MainWindowViewModel.CompareZoomPercent)
            or nameof(MainWindowViewModel.HasCompareItems)
            or nameof(MainWindowViewModel.ShowCompareViewerSurface))
        {
            Dispatcher.UIThread.Post(
                () => QueueCompareScrollSync(_activeComparePaneScrollViewer ?? _comparePaneScrollViewers.FirstOrDefault()),
                DispatcherPriority.Background);
        }

        if (e.PropertyName is nameof(MainWindowViewModel.ShowContactSheetViewer)
            or nameof(MainWindowViewModel.ContactSheetItems))
        {
            Dispatcher.UIThread.Post(UpdateContactSheetViewportMetrics, DispatcherPriority.Background);
        }
    }

    private void SyncSelectionInLists()
    {
        SyncThumbnailSelectionFromViewModel();

        var selected = ViewModel.SelectedImage;
        if (selected is null)
        {
            return;
        }

        ThumbnailListBox?.ScrollIntoView(selected);

        if (ViewModel.ShowFilmstripInMain)
        {
            FilmstripListBox?.ScrollIntoView(selected);
        }
    }

    private void SyncThumbnailSelectionFromViewModel()
    {
        if (ThumbnailListBox?.SelectedItems is not { } selectedItems)
        {
            return;
        }

        var targetSelection = ViewModel.SelectedImages.Count > 0
            ? ViewModel.SelectedImages.Cast<object>().ToArray()
            : ViewModel.SelectedImage is null
                ? []
                : [ViewModel.SelectedImage];

        var needsSync = selectedItems.Count != targetSelection.Length;
        if (!needsSync)
        {
            for (var index = 0; index < targetSelection.Length; index++)
            {
                if (!ReferenceEquals(selectedItems[index], targetSelection[index]))
                {
                    needsSync = true;
                    break;
                }
            }
        }

        if (!needsSync && ReferenceEquals(ThumbnailListBox.SelectedItem, ViewModel.SelectedImage))
        {
            return;
        }

        try
        {
            _isSyncingThumbnailSelection = true;
            selectedItems.Clear();
            foreach (var item in targetSelection)
            {
                selectedItems.Add(item);
            }

            ThumbnailListBox.SelectedItem = ViewModel.SelectedImage;
        }
        finally
        {
            _isSyncingThumbnailSelection = false;
        }
    }

    private void ThumbnailListBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingThumbnailSelection || sender is not ListBox listBox)
        {
            return;
        }

        ViewModel.SyncSelectedImages(listBox.SelectedItems?.OfType<ImageListItemViewModel>() ?? []);

        if (listBox.SelectedItem is ImageListItemViewModel selected
            && !ReferenceEquals(ViewModel.SelectedImage, selected))
        {
            ViewModel.SelectedImage = selected;
        }
    }

    private void Window_OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = HasDroppableItems(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_OnDrop(object? sender, DragEventArgs e)
    {
        var items = e.DataTransfer.TryGetFiles()?.ToArray();
        if (items is null || items.Length == 0)
        {
            return;
        }

        var localPaths = FilterLocalPaths(items)
            .Distinct(PathComparison.Comparer)
            .ToArray();
        if (localPaths.Length == 0)
        {
            ViewModel.SetStatusMessage("拖入的内容里没有可用的本地图片或文件夹。");
            return;
        }

        await ViewModel.LoadInputsAsync(localPaths);
        FocusPrimaryViewerSurface();
        e.Handled = true;
    }

    private void Window_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        if (_shortcutRouter.TryHandle(e.Source, e.Key, e.KeyModifiers))
        {
            e.Handled = true;
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
                    Patterns = SupportedPatterns
                }
            ]
        });

        await ViewModel.LoadInputsAsync(FilterLocalPaths(files));
        FocusPrimaryViewerSurface();
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
        FocusPrimaryViewerSurface();
    }

    private async void ReloadButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await ViewModel.ReloadAsync();
        FocusPrimaryViewerSurface();
    }

    private void ClearFiltersButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.ClearFilters();
    }

    private void OpenToolboxButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_toolboxWindow is { IsVisible: true })
        {
            _toolboxWindow.Activate();
            return;
        }

        _toolboxWindow = new ToolboxWindow(ViewModel);
        ApplyToolboxWindowLayout(_toolboxWindow);
        _toolboxWindow.Closed += ToolboxWindow_OnClosed;
        _toolboxWindow.Show(this);
    }

    private void ToggleImmersiveModeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ToggleImmersiveMode();
    }

    private void ToggleImmersiveMode()
    {
        if (_isImmersiveMode)
        {
            ExitImmersiveMode();
            return;
        }

        _windowStateBeforeImmersive = WindowState;
        _systemDecorationsBeforeImmersive = SystemDecorations;
        SystemDecorations = SystemDecorations.None;
        WindowState = WindowState.FullScreen;
        _isImmersiveMode = true;
        ViewModel.SetStatusMessage("已进入沉浸模式，按 Esc 或 F11 退出。");
    }

    private void ExitImmersiveMode()
    {
        if (!_isImmersiveMode)
        {
            return;
        }

        WindowState = _windowStateBeforeImmersive;
        SystemDecorations = _systemDecorationsBeforeImmersive;
        _isImmersiveMode = false;
        ViewModel.SetStatusMessage("已退出沉浸模式。");
    }

    internal void ToggleImmersiveModeFromShortcut()
    {
        ToggleImmersiveMode();
    }

    internal void ExitImmersiveModeFromShortcut()
    {
        ExitImmersiveMode();
    }

    private void ToggleContactSheetButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.ToggleContactSheet();
    }

    private void ToggleCompareViewerButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.ToggleCompareViewer();
    }

    private void ToggleFilmstripButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.ToggleFilmstrip();
    }

    private void ToggleInspectorButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.ToggleInspector();
    }

    private void ToggleSidebarButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.ToggleSidebar();
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

    private void OpenContainingFolderButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.OpenContainingFolder();
    }

    private async void RecycleToTrashButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await ViewModel.RecycleSelectedToTrashAsync();
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

    private void RotatePreviewLeftButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.RotatePreviewCounterClockwise();
    }

    private void PreviewFitModeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.ShowPreviewFitMode();
    }

    private void PreviewActualSizeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.ShowPreviewActualSize();
    }

    private void ToggleLongImageModeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.ToggleLongImageMode();
    }

    private void RotatePreviewRightButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.RotatePreviewClockwise();
    }

    private void FlipPreviewButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.FlipPreviewHorizontal();
    }

    private void ResetPreviewToolsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.ResetPreviewTools();
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

    private void ClearRecentSessionsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.ClearRecentSessions();
    }

    private void ToggleRecentSessionPinnedButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: RecentSessionItemViewModel item })
        {
            ViewModel.ToggleRecentSessionPinned(item);
        }
    }

    private void PreviousImageButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.ShowPreviousSlide();
    }

    private void NextImageButton_OnClick(object? sender, RoutedEventArgs e)
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

    private void SelectedPreviewHost_OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ViewModel.UpdatePreviewViewportSize(
            Math.Max(1, e.NewSize.Width - 24),
            Math.Max(1, e.NewSize.Height - 24));
    }

    private void SelectedPreviewScrollViewer_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        FocusPrimaryViewerSurface();

        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0)
        {
            return;
        }

        if (e.Delta.Y > 0)
        {
            ViewModel.ZoomPreviewIn();
        }
        else
        {
            ViewModel.ZoomPreviewOut();
        }

        e.Handled = true;
    }

    private void SelectedPreviewImage_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        FocusPrimaryViewerSurface();
        ViewModel.TogglePreviewFitMode();
        e.Handled = true;
    }

    private void SelectedPreviewScrollViewer_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        scrollViewer.Focus();

        if (!ViewModel.CanPanPreview || e.ClickCount > 1)
        {
            return;
        }

        var point = e.GetCurrentPoint(scrollViewer);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isPreviewPanning = true;
        _previewPanStart = e.GetPosition(scrollViewer);
        _previewScrollStart = scrollViewer.Offset;
        e.Pointer.Capture(scrollViewer);
        e.Handled = true;
    }

    private void SelectedPreviewScrollViewer_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPreviewPanning || sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        var currentPoint = e.GetPosition(scrollViewer);
        var delta = currentPoint - _previewPanStart;
        scrollViewer.Offset = new Vector(
            Math.Max(0, _previewScrollStart.X - delta.X),
            Math.Max(0, _previewScrollStart.Y - delta.Y));
        e.Handled = true;
    }

    private void SelectedPreviewScrollViewer_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPreviewPanning)
        {
            return;
        }

        _isPreviewPanning = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void FocusContactSheetImageButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ImageListItemViewModel item })
        {
            ViewModel.FocusContactSheetImage(item.FullPath);
        }
    }

    private void ApplyToolboxWindowLayout(Window toolboxWindow)
    {
        var targetWidth = Math.Clamp(Bounds.Width * 0.92, 640, 1180);
        var targetHeight = Math.Clamp(Bounds.Height * 0.9, 480, 820);

        toolboxWindow.Width = targetWidth;
        toolboxWindow.Height = targetHeight;
        toolboxWindow.MinWidth = 620;
        toolboxWindow.MinHeight = 480;
        ViewModel.SetToolboxViewportSize(targetWidth, targetHeight);

        var offsetX = Math.Max(12d, (Bounds.Width - targetWidth) / 2d);
        var offsetY = Math.Max(16d, (Bounds.Height - targetHeight) / 2d);
        toolboxWindow.Position = new PixelPoint(
            Position.X + (int)Math.Round(offsetX),
            Position.Y + (int)Math.Round(offsetY));
    }

    private void ContactSheetViewport_OnLoaded(object? sender, RoutedEventArgs e)
    {
        UpdateContactSheetViewportMetrics();
    }

    private void ContactSheetViewport_OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateContactSheetViewportMetrics();
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

        if (e.ClickCount == 2)
        {
            ViewModel.ResetCompareZoom();
            QueueCompareScrollSync(scrollViewer);
            e.Handled = true;
        }
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

    private static bool HasDroppableItems(DragEventArgs e)
    {
        return e.DataTransfer.TryGetFiles()?.Any() == true;
    }

    private void FocusPrimaryViewerSurface()
    {
        if (SelectedPreviewScrollViewer?.IsVisible == true
            && SelectedPreviewScrollViewer.IsEnabled
            && SelectedPreviewScrollViewer.Focusable)
        {
            SelectedPreviewScrollViewer.Focus();
            return;
        }

        if (ThumbnailListBox?.IsVisible == true
            && ThumbnailListBox.IsEnabled
            && ThumbnailListBox.Focusable)
        {
            ThumbnailListBox.Focus();
            return;
        }

        Focus();
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

    private void UpdateContactSheetViewportMetrics()
    {
        if (ContactSheetViewport is null)
        {
            return;
        }

        var width = ContactSheetViewport.Bounds.Width;
        var height = ContactSheetViewport.Bounds.Height;
        if (width <= 1 || height <= 1)
        {
            return;
        }

        ViewModel.UpdateContactSheetViewport(width, height);
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

    private void ToolboxWindow_OnClosed(object? sender, EventArgs e)
    {
        if (_toolboxWindow is not null)
        {
            _toolboxWindow.Closed -= ToolboxWindow_OnClosed;
            _toolboxWindow = null;
        }
    }

    private async Task CompleteShutdownAndCloseAsync()
    {
        try
        {
            CloseToolboxWindow();

            if (DataContext is MainWindowViewModel viewModel)
            {
                using var shutdownCts = new CancellationTokenSource(ShutdownDrainTimeout);
                try
                {
                    await viewModel.PrepareForShutdownAsync(shutdownCts.Token);
                }
                catch (OperationCanceledException)
                {
                    viewModel.SetStatusMessage("正在关闭，后台任务仍在收尾，已继续退出。");
                }
            }
        }
        catch (Exception ex)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.SetStatusMessage($"关闭前清理失败：{ex.Message}");
            }
        }
        finally
        {
            Dispatcher.UIThread.Post(() => Close(), DispatcherPriority.Send);
        }
    }

    private void CloseToolboxWindow()
    {
        if (_toolboxWindow is null)
        {
            return;
        }

        _toolboxWindow.Closed -= ToolboxWindow_OnClosed;
        var toolboxWindow = _toolboxWindow;
        _toolboxWindow = null;
        toolboxWindow.Close();
    }

    private WindowShortcutRouter CreateShortcutRouter()
    {
        return new WindowShortcutRouter(
            ViewModel,
            openFolder: () => OpenFolderButton_OnClick(this, new RoutedEventArgs()),
            openFiles: () => OpenFilesButton_OnClick(this, new RoutedEventArgs()),
            openToolbox: () => OpenToolboxButton_OnClick(this, new RoutedEventArgs()),
            recycleToTrash: () => RecycleToTrashButton_OnClick(this, new RoutedEventArgs()),
            toggleImmersive: ToggleImmersiveMode,
            exitImmersive: ExitImmersiveMode,
            synchronizeCompareScroll: () => QueueCompareScrollSync(_activeComparePaneScrollViewer ?? _comparePaneScrollViewers.FirstOrDefault()),
            restoreViewerFocus: FocusPrimaryViewerSurface);
    }
}


