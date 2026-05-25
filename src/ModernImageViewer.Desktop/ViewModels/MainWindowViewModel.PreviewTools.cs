using ModernImageViewer.Core;

namespace ModernImageViewer.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const double LongImageAspectRatioThreshold = 2.6;
    private const int LongImageFitDecodeMinLongEdge = 1600;
    private const int LongImageFitDecodeMaxLongEdge = 6400;

    private readonly Dictionary<string, PreviewToolState> _previewToolStates = new(PathComparison.Comparer);
    private bool _isPreviewFitMode = true;
    private bool _isPreviewLongImageMode;
    private bool _isPreviewMirrored;
    private bool _isRestoringPreviewToolState;
    private int _previewRotationDegrees;
    private double _previewZoomPercent = 100;
    private double _previewViewportWidth = 480;
    private double _previewViewportHeight = 316;

    public int PreviewRotationDegrees => _previewRotationDegrees;

    public bool IsPreviewMirrored => _isPreviewMirrored;

    public bool IsPreviewFitMode => _isPreviewFitMode;

    public bool IsPreviewLongImageMode => _isPreviewLongImageMode;

    public double PreviewZoomPercent
    {
        get => _previewZoomPercent;
        set => SetPreviewToolState(
            _previewRotationDegrees,
            _isPreviewMirrored,
            value,
            isFitMode: false,
            isLongImageMode: _isPreviewLongImageMode,
            persistForSelection: true);
    }

    public double PreviewScaleX => IsPreviewMirrored ? -1d : 1d;

    public double PreviewScaleY => 1d;

    public bool CanAdjustPreview => SelectedImage is not null && !IsExportProcessing;

    public bool CanPanPreview => CanAdjustPreview && ShowSinglePreviewSurface && !IsPreviewFitMode;

    public bool HasLongImageCandidate => SelectedPreviewBitmap is not null
        && IsLongImageCandidate(SelectedPreviewBitmap.PixelSize.Width, SelectedPreviewBitmap.PixelSize.Height);

    public bool ShowLongImageModeButton => HasLongImageCandidate || _isPreviewLongImageMode;

    public bool CanToggleLongImageMode => CanAdjustPreview
        && ShowSinglePreviewSurface
        && ShowLongImageModeButton;

    public bool CanUsePreviewFitMode => CanAdjustPreview && !IsPreviewFitMode;

    public bool CanUsePreviewActualSize => CanAdjustPreview && (IsPreviewFitMode || Math.Abs(_previewZoomPercent - 100) > 0.01);

    public bool CanResetPreviewTools => CanAdjustPreview
        && (!_isPreviewFitMode
            || _isPreviewLongImageMode
            || _previewRotationDegrees != 0
            || _isPreviewMirrored
            || Math.Abs(_previewZoomPercent - 100) > 0.01);

    public string LongImageModeButtonText => _isPreviewLongImageMode ? "长图适宽" : "长图";

    public string PreviewZoomText => IsPreviewFitMode
        ? GetPreviewFitLabel()
        : $"{PreviewZoomPercent:0}%";

    public string PreviewTransformSummaryText => ShowCompareViewerSurface
        ? "对比模式下会保持统一缩放和滚动位置。"
        : ShowContactSheetViewer
            ? "联系表模式下点击缩略图可直接回到单图预览。"
            : SelectedImage is null
                ? "选中图片后，可以旋转、镜像、缩放；长图会自动切换到适宽查看。"
                : $"当前预览：{PreviewZoomText} / {PreviewRotationDegrees:0}°{(IsPreviewMirrored ? " / 已镜像" : string.Empty)}";

    public void RotatePreviewClockwise()
    {
        if (!CanAdjustPreview)
        {
            return;
        }

        SetPreviewToolState(
            _previewRotationDegrees + 90,
            _isPreviewMirrored,
            _previewZoomPercent,
            _isPreviewFitMode,
            _isPreviewLongImageMode,
            persistForSelection: true);
        OperationStatusText = "已顺时针旋转 90°。";
    }

    public void RotatePreviewCounterClockwise()
    {
        if (!CanAdjustPreview)
        {
            return;
        }

        SetPreviewToolState(
            _previewRotationDegrees - 90,
            _isPreviewMirrored,
            _previewZoomPercent,
            _isPreviewFitMode,
            _isPreviewLongImageMode,
            persistForSelection: true);
        OperationStatusText = "已逆时针旋转 90°。";
    }

    public void FlipPreviewHorizontal()
    {
        if (!CanAdjustPreview)
        {
            return;
        }

        var nextValue = !_isPreviewMirrored;
        SetPreviewToolState(
            _previewRotationDegrees,
            nextValue,
            _previewZoomPercent,
            _isPreviewFitMode,
            _isPreviewLongImageMode,
            persistForSelection: true);
        OperationStatusText = nextValue ? "已开启水平镜像。" : "已关闭水平镜像。";
    }

    public void ShowPreviewFitMode()
    {
        if (!CanAdjustPreview)
        {
            return;
        }

        SetPreviewToolState(
            _previewRotationDegrees,
            _isPreviewMirrored,
            _previewZoomPercent,
            isFitMode: true,
            isLongImageMode: _isPreviewLongImageMode,
            persistForSelection: true);
        OperationStatusText = _isPreviewLongImageMode ? "已切换为长图适宽。" : "已切换为适应窗口。";
    }

    public void ShowPreviewActualSize()
    {
        if (!CanAdjustPreview)
        {
            return;
        }

        SetPreviewToolState(
            _previewRotationDegrees,
            _isPreviewMirrored,
            100,
            isFitMode: false,
            isLongImageMode: _isPreviewLongImageMode,
            persistForSelection: true);
        OperationStatusText = "已切换为 100% 实际大小。";
    }

    public void TogglePreviewFitMode()
    {
        if (!CanAdjustPreview)
        {
            return;
        }

        if (IsPreviewFitMode)
        {
            ShowPreviewActualSize();
            return;
        }

        ShowPreviewFitMode();
    }

    public void ToggleLongImageMode()
    {
        if (!CanToggleLongImageMode)
        {
            return;
        }

        var nextValue = !_isPreviewLongImageMode;
        SetPreviewToolState(
            _previewRotationDegrees,
            _isPreviewMirrored,
            _previewZoomPercent,
            isFitMode: nextValue ? true : _isPreviewFitMode,
            isLongImageMode: nextValue,
            persistForSelection: true);
        OperationStatusText = nextValue ? "已开启长图适宽。" : "已关闭长图适宽。";
    }

    public void ZoomPreviewIn()
    {
        if (!CanAdjustPreview)
        {
            return;
        }

        var nextZoom = IsPreviewFitMode
            ? 112
            : Math.Clamp(Math.Round(_previewZoomPercent * 1.12), 25, 300);

        SetPreviewToolState(
            _previewRotationDegrees,
            _isPreviewMirrored,
            nextZoom,
            isFitMode: false,
            isLongImageMode: _isPreviewLongImageMode,
            persistForSelection: true);
        OperationStatusText = $"预览已缩放到 {PreviewZoomText}。";
    }

    public void ZoomPreviewOut()
    {
        if (!CanAdjustPreview)
        {
            return;
        }

        if (IsPreviewFitMode)
        {
            return;
        }

        var nextZoom = Math.Clamp(Math.Round(_previewZoomPercent / 1.12), 25, 300);
        if (nextZoom <= 100)
        {
            ShowPreviewActualSize();
            if (nextZoom < 100)
            {
                SetPreviewToolState(
                    _previewRotationDegrees,
                    _isPreviewMirrored,
                    nextZoom,
                    isFitMode: false,
                    isLongImageMode: _isPreviewLongImageMode,
                    persistForSelection: true);
            }
        }
        else
        {
            SetPreviewToolState(
                _previewRotationDegrees,
                _isPreviewMirrored,
                nextZoom,
                isFitMode: false,
                isLongImageMode: _isPreviewLongImageMode,
                persistForSelection: true);
        }

        OperationStatusText = $"预览已缩放到 {PreviewZoomText}。";
    }

    public void ResetPreviewTools()
    {
        if (!CanAdjustPreview)
        {
            return;
        }

        if (SelectedImage is not null)
        {
            _previewToolStates.Remove(SelectedImage.FullPath);
        }

        SetPreviewToolState(0, false, 100, isFitMode: true, isLongImageMode: false, persistForSelection: false);
        ApplyAutoLongImageModeForCurrentPreview();
        OperationStatusText = "已恢复默认预览状态。";
    }

    public void UpdatePreviewViewportSize(double width, double height)
    {
        var normalizedWidth = Math.Max(1, width);
        var normalizedHeight = Math.Max(1, height);
        if (Math.Abs(_previewViewportWidth - normalizedWidth) < 0.5
            && Math.Abs(_previewViewportHeight - normalizedHeight) < 0.5)
        {
            return;
        }

        _previewViewportWidth = normalizedWidth;
        _previewViewportHeight = normalizedHeight;

        if (IsPreviewFitMode)
        {
            OnPropertyChanged(nameof(SelectedPreviewDisplayWidth));
            OnPropertyChanged(nameof(SelectedPreviewDisplayHeight));
        }

        MaybeReloadSelectedPreviewForDecodeSize();
    }

    private void LoadPreviewToolState(ImageListItemViewModel? item)
    {
        _isRestoringPreviewToolState = true;
        try
        {
            if (item is null)
            {
                SetPreviewToolState(0, false, 100, isFitMode: true, isLongImageMode: false, persistForSelection: false);
                return;
            }

            if (_previewToolStates.TryGetValue(item.FullPath, out var state))
            {
                SetPreviewToolState(
                    state.RotationDegrees,
                    state.IsMirrored,
                    state.ZoomPercent,
                    state.IsFitMode,
                    state.IsLongImageMode,
                    persistForSelection: false);
                return;
            }

            SetPreviewToolState(0, false, 100, isFitMode: true, isLongImageMode: false, persistForSelection: false);
        }
        finally
        {
            _isRestoringPreviewToolState = false;
        }
    }

    private void SetPreviewToolState(
        int rotationDegrees,
        bool isMirrored,
        double zoomPercent,
        bool isFitMode,
        bool isLongImageMode,
        bool persistForSelection)
    {
        var normalizedRotation = NormalizePreviewRotation(rotationDegrees);
        var normalizedZoom = Math.Clamp(Math.Round(zoomPercent), 25, 300);
        var changed = false;

        if (_previewRotationDegrees != normalizedRotation)
        {
            _previewRotationDegrees = normalizedRotation;
            OnPropertyChanged(nameof(PreviewRotationDegrees));
            changed = true;
        }

        if (_isPreviewMirrored != isMirrored)
        {
            _isPreviewMirrored = isMirrored;
            OnPropertyChanged(nameof(IsPreviewMirrored));
            changed = true;
        }

        if (_isPreviewFitMode != isFitMode)
        {
            _isPreviewFitMode = isFitMode;
            OnPropertyChanged(nameof(IsPreviewFitMode));
            changed = true;
        }

        if (_isPreviewLongImageMode != isLongImageMode)
        {
            _isPreviewLongImageMode = isLongImageMode;
            OnPropertyChanged(nameof(IsPreviewLongImageMode));
            changed = true;
        }

        if (Math.Abs(_previewZoomPercent - normalizedZoom) > 0.01)
        {
            _previewZoomPercent = normalizedZoom;
            OnPropertyChanged(nameof(PreviewZoomPercent));
            changed = true;
        }

        if (!changed)
        {
            if (persistForSelection)
            {
                PersistPreviewToolState();
            }

            MaybeReloadSelectedPreviewForDecodeSize();
            return;
        }

        NotifyPreviewToolStateChanged();

        if (persistForSelection)
        {
            PersistPreviewToolState();
        }

        MaybeReloadSelectedPreviewForDecodeSize();
    }

    internal static int CalculatePreviewDecodeLongEdge(
        double viewportWidth,
        double viewportHeight,
        bool isFitMode,
        double zoomPercent)
    {
        return CalculatePreviewDecodeLongEdge(
            viewportWidth,
            viewportHeight,
            isFitMode,
            zoomPercent,
            contentWidth: 0,
            contentHeight: 0,
            preferWidthFit: false);
    }

    internal static int CalculatePreviewDecodeLongEdge(
        double viewportWidth,
        double viewportHeight,
        bool isFitMode,
        double zoomPercent,
        double contentWidth,
        double contentHeight,
        bool preferWidthFit)
    {
        var viewportLongEdge = Math.Max(1, Math.Max(viewportWidth, viewportHeight));
        if (isFitMode)
        {
            if (preferWidthFit && IsLongImageCandidate(contentWidth, contentHeight))
            {
                var safeViewportWidth = Math.Max(1, viewportWidth);
                var targetWidth = safeViewportWidth * 1.18;
                var scale = targetWidth / Math.Max(1, contentWidth);
                var desiredLongEdge = Math.Ceiling(Math.Max(contentWidth, contentHeight) * scale);
                return (int)Math.Clamp(desiredLongEdge, LongImageFitDecodeMinLongEdge, LongImageFitDecodeMaxLongEdge);
            }

            return (int)Math.Clamp(Math.Ceiling(viewportLongEdge * 1.35), 900, 2200);
        }

        var zoomScale = Math.Clamp(zoomPercent, 25, 300) / 100d;
        return (int)Math.Clamp(Math.Ceiling(viewportLongEdge * zoomScale * 1.35), 1600, 3200);
    }

    private void MaybeReloadSelectedPreviewForDecodeSize()
    {
        if (_isRestoringPreviewToolState || SelectedImage is null || SelectedPreviewBitmap is null || IsSelectedPreviewLoading)
        {
            return;
        }

        var desiredLongEdge = CalculatePreviewDecodeLongEdge(
            _previewViewportWidth,
            _previewViewportHeight,
            IsPreviewFitMode,
            PreviewZoomPercent,
            GetCurrentPreviewContentWidth(),
            GetCurrentPreviewContentHeight(),
            ShouldUseLongImageWidthFit());
        if (!ShouldReloadSelectedPreviewForGrowth(desiredLongEdge, _selectedPreviewDecodeLongEdge))
        {
            return;
        }

        ScheduleSelectedPreviewReload();
    }

    internal static bool ShouldReloadSelectedPreviewForGrowth(int desiredLongEdge, int loadedLongEdge)
    {
        return desiredLongEdge > loadedLongEdge * 1.2;
    }

    private void ScheduleSelectedPreviewReload()
    {
        var selectedPath = SelectedImage?.FullPath;
        if (string.IsNullOrWhiteSpace(selectedPath) || IsExportProcessing)
        {
            return;
        }

        CancelPreviewReload();
        var reloadCts = new CancellationTokenSource();
        _previewReloadCts = reloadCts;
        ForgetBackgroundTask(RunScheduledPreviewReloadAsync(selectedPath, reloadCts));
    }

    private async Task RunScheduledPreviewReloadAsync(string selectedPath, CancellationTokenSource reloadCts)
    {
        try
        {
            await Task.Delay(PreviewReloadDebounceMilliseconds, reloadCts.Token);

            if (reloadCts.Token.IsCancellationRequested
                || SelectedImage is null
                || !string.Equals(SelectedImage.FullPath, selectedPath, PathComparison.Comparison))
            {
                return;
            }

            QueueSelectedPreviewLoad();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_previewReloadCts, reloadCts))
            {
                _previewReloadCts = null;
            }

            DisposeQuietly(reloadCts);
        }
    }

    private void PersistPreviewToolState()
    {
        var selectedPath = SelectedImage?.FullPath;
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return;
        }

        if (_previewRotationDegrees == 0
            && !_isPreviewMirrored
            && Math.Abs(_previewZoomPercent - 100) <= 0.01
            && _isPreviewFitMode
            && !_isPreviewLongImageMode)
        {
            _previewToolStates.Remove(selectedPath);
            return;
        }

        _previewToolStates[selectedPath] = new PreviewToolState(
            _previewRotationDegrees,
            _isPreviewMirrored,
            _previewZoomPercent,
            _isPreviewFitMode,
            _isPreviewLongImageMode);
    }

    private void NotifyPreviewToolStateChanged()
    {
        OnPropertyChanged(nameof(PreviewScaleX));
        OnPropertyChanged(nameof(PreviewScaleY));
        OnPropertyChanged(nameof(IsPreviewFitMode));
        OnPropertyChanged(nameof(IsPreviewLongImageMode));
        OnPropertyChanged(nameof(CanAdjustPreview));
        OnPropertyChanged(nameof(CanPanPreview));
        OnPropertyChanged(nameof(HasLongImageCandidate));
        OnPropertyChanged(nameof(ShowLongImageModeButton));
        OnPropertyChanged(nameof(CanToggleLongImageMode));
        OnPropertyChanged(nameof(CanUsePreviewFitMode));
        OnPropertyChanged(nameof(CanUsePreviewActualSize));
        OnPropertyChanged(nameof(CanResetPreviewTools));
        OnPropertyChanged(nameof(LongImageModeButtonText));
        OnPropertyChanged(nameof(PreviewZoomText));
        OnPropertyChanged(nameof(PreviewTransformSummaryText));
        OnPropertyChanged(nameof(SelectedPreviewDisplayWidth));
        OnPropertyChanged(nameof(SelectedPreviewDisplayHeight));
        OnPropertyChanged(nameof(SelectedPreviewImageDisplayWidth));
        OnPropertyChanged(nameof(SelectedPreviewImageDisplayHeight));
    }

    private static int NormalizePreviewRotation(int rotationDegrees)
    {
        var normalized = rotationDegrees % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }

    private (double Width, double Height) GetRotatedPreviewPixelSize(Avalonia.Media.Imaging.Bitmap bitmap)
    {
        var width = bitmap.PixelSize.Width;
        var height = bitmap.PixelSize.Height;
        return _previewRotationDegrees is 90 or 270
            ? (height, width)
            : (width, height);
    }

    private double GetPreviewScaleFactor(Avalonia.Media.Imaging.Bitmap bitmap)
    {
        if (!IsPreviewFitMode)
        {
            return PreviewZoomPercent / 100d;
        }

        var (rotatedWidth, rotatedHeight) = GetRotatedPreviewPixelSize(bitmap);
        if (rotatedWidth <= 0 || rotatedHeight <= 0)
        {
            return 1d;
        }

        if (ShouldUseLongImageWidthFit(rotatedWidth, rotatedHeight))
        {
            return Math.Max(0.01, _previewViewportWidth / rotatedWidth);
        }

        var fitWidth = _previewViewportWidth / rotatedWidth;
        var fitHeight = _previewViewportHeight / rotatedHeight;
        var fitScale = Math.Min(fitWidth, fitHeight);
        return Math.Max(0.01, fitScale);
    }

    public double SelectedPreviewDisplayWidth
    {
        get
        {
            var bitmap = SelectedPreviewBitmap;
            if (bitmap is null)
            {
                return 0;
            }

            var (width, _) = GetRotatedPreviewPixelSize(bitmap);
            return Math.Max(1, width * GetPreviewScaleFactor(bitmap));
        }
    }

    public double SelectedPreviewDisplayHeight
    {
        get
        {
            var bitmap = SelectedPreviewBitmap;
            if (bitmap is null)
            {
                return 0;
            }

            var (_, height) = GetRotatedPreviewPixelSize(bitmap);
            return Math.Max(1, height * GetPreviewScaleFactor(bitmap));
        }
    }

    public double SelectedPreviewImageDisplayWidth
    {
        get
        {
            var bitmap = SelectedPreviewBitmap;
            if (bitmap is null)
            {
                return 0;
            }

            return Math.Max(1, bitmap.PixelSize.Width * GetPreviewScaleFactor(bitmap));
        }
    }

    public double SelectedPreviewImageDisplayHeight
    {
        get
        {
            var bitmap = SelectedPreviewBitmap;
            if (bitmap is null)
            {
                return 0;
            }

            return Math.Max(1, bitmap.PixelSize.Height * GetPreviewScaleFactor(bitmap));
        }
    }

    private void ApplyAutoLongImageModeForCurrentPreview()
    {
        if (_isRestoringPreviewToolState || SelectedPreviewBitmap is not { } bitmap)
        {
            return;
        }

        var selectedPath = SelectedImage?.FullPath;
        if (string.IsNullOrWhiteSpace(selectedPath) || _previewToolStates.ContainsKey(selectedPath))
        {
            return;
        }

        if (!IsLongImageCandidate(bitmap.PixelSize.Width, bitmap.PixelSize.Height) || _isPreviewLongImageMode)
        {
            return;
        }

        SetPreviewToolState(
            _previewRotationDegrees,
            _isPreviewMirrored,
            _previewZoomPercent,
            isFitMode: true,
            isLongImageMode: true,
            persistForSelection: true);
    }

    internal static bool IsLongImageCandidate(double width, double height)
    {
        return width > 0
            && height > 0
            && height >= width * LongImageAspectRatioThreshold;
    }

    private string GetPreviewFitLabel()
    {
        return ShouldUseLongImageWidthFit() ? "适宽" : "适应";
    }

    private bool ShouldUseLongImageWidthFit()
    {
        return SelectedPreviewBitmap is not null
            && ShouldUseLongImageWidthFit(GetCurrentPreviewContentWidth(), GetCurrentPreviewContentHeight());
    }

    private bool ShouldUseLongImageWidthFit(double contentWidth, double contentHeight)
    {
        return _isPreviewLongImageMode
            && IsPreviewFitMode
            && IsLongImageCandidate(contentWidth, contentHeight);
    }

    private double GetCurrentPreviewContentWidth()
    {
        return SelectedPreviewBitmap is { } bitmap
            ? GetRotatedPreviewPixelSize(bitmap).Width
            : 0;
    }

    private double GetCurrentPreviewContentHeight()
    {
        return SelectedPreviewBitmap is { } bitmap
            ? GetRotatedPreviewPixelSize(bitmap).Height
            : 0;
    }

    private readonly record struct PreviewToolState(
        int RotationDegrees,
        bool IsMirrored,
        double ZoomPercent,
        bool IsFitMode,
        bool IsLongImageMode);
}
