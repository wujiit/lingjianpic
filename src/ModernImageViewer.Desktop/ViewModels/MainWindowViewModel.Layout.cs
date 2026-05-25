using Avalonia.Controls;

using Avalonia;

namespace ModernImageViewer.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const double CompactLayoutWidth = 1440;
    private const double HeaderCompactWidth = 1260;
    private const double SidebarCollapseWidth = 980;
    private const double InspectorCollapseWidth = 1040;
    private const double CompactLayoutHeight = 800;
    private const double ToolboxCompactWidth = 1120;
    private const double SidebarWidthValue = 176;
    private const double CompactSidebarWidthValue = 164;
    private const double SidebarGapWidthValue = 6;
    private const double InspectorWidthValue = 192;
    private const double CompactInspectorWidthValue = 164;
    private const double InspectorGapWidthValue = 8;

    private bool _isSidebarVisible = true;
    private bool _isCompactLayout;
    private bool _isContactSheetVisible;
    private bool _isCompareViewerVisible;
    private bool _isFilmstripVisible;
    private bool _isInspectorVisible;
    private bool _isSelectedPreviewLoading;
    private double _layoutViewportWidth = 1080;
    private double _layoutViewportHeight = 680;
    private double _toolboxViewportWidth = 1080;

    public bool HasImages => Images.Count > 0;

    public bool IsSidebarVisible
    {
        get => _isSidebarVisible;
        set
        {
            if (!SetProperty(ref _isSidebarVisible, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SidebarColumnWidth));
            OnPropertyChanged(nameof(SidebarGapColumnWidth));
            OnPropertyChanged(nameof(ShowSidebarInMain));
            OnPropertyChanged(nameof(SidebarToggleText));
            ScheduleViewerSettingsSave();
        }
    }

    public bool IsContactSheetVisible
    {
        get => _isContactSheetVisible;
        set
        {
            if (!SetProperty(ref _isContactSheetVisible, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ShowContactSheetViewer));
            OnPropertyChanged(nameof(ShowSinglePreviewSurface));
            OnPropertyChanged(nameof(ShowContactSheetInMain));
            OnPropertyChanged(nameof(ShowWorkspaceTabsInMain));
            OnPropertyChanged(nameof(ShowFilmstripInMain));
            OnPropertyChanged(nameof(ContactSheetToggleText));
            NotifyNavigationStateChanged();
            NotifyPreviewToolStateChanged();
            QueueThumbnailWarmup();
            ScheduleViewerSettingsSave();
        }
    }

    public bool IsCompareViewerVisible
    {
        get => _isCompareViewerVisible;
        set
        {
            if (!SetProperty(ref _isCompareViewerVisible, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ShowCompareViewerSurface));
            OnPropertyChanged(nameof(ShowSinglePreviewSurface));
            OnPropertyChanged(nameof(ShowWorkspaceTabsInMain));
            OnPropertyChanged(nameof(CompareViewerToggleText));
            NotifyNavigationStateChanged();
            NotifyPreviewToolStateChanged();
            MaybeReloadComparePreviewsForDecodeSize();
        }
    }

    public bool IsFilmstripVisible
    {
        get => _isFilmstripVisible;
        set
        {
            if (!SetProperty(ref _isFilmstripVisible, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ShowFilmstripInMain));
            OnPropertyChanged(nameof(FilmstripToggleText));
            ScheduleViewerSettingsSave();
        }
    }

    public bool IsInspectorVisible
    {
        get => _isInspectorVisible;
        set
        {
            if (!SetProperty(ref _isInspectorVisible, value))
            {
                return;
            }

            OnPropertyChanged(nameof(InspectorSpacerColumnWidth));
            OnPropertyChanged(nameof(InspectorColumnWidth));
            OnPropertyChanged(nameof(ShowInspectorInMain));
            OnPropertyChanged(nameof(InspectorToggleText));
            ScheduleViewerSettingsSave();
        }
    }

    public bool IsSelectedPreviewLoading
    {
        get => _isSelectedPreviewLoading;
        private set
        {
            if (!SetProperty(ref _isSelectedPreviewLoading, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ShowSelectedPreviewPlaceholder));
            OnPropertyChanged(nameof(ShowSelectedPreviewLoadingOverlay));
        }
    }

    public bool ShowContactSheetViewer => IsContactSheetVisible && HasImages;

    public bool ShowCompareViewerSurface => IsCompareViewerVisible && HasImages;

    public bool ShowSinglePreviewSurface => !ShowContactSheetViewer && !ShowCompareViewerSurface;

    public bool ShowContactSheetInMain => HasImages;

    public bool IsCompactLayout => _isCompactLayout;

    public bool IsHeaderCompactLayout => _layoutViewportWidth < HeaderCompactWidth;

    public bool IsNarrowLayout => _layoutViewportWidth < SidebarCollapseWidth;

    public bool IsPreviewFocusedLayout => _layoutViewportWidth < InspectorCollapseWidth;

    public bool IsShortLayout => _layoutViewportHeight < CompactLayoutHeight;

    public bool IsToolboxCompactLayout => _toolboxViewportWidth < ToolboxCompactWidth;

    public bool ShowFullHeaderActions => !IsHeaderCompactLayout;

    public bool ShowCompactHeaderActions => IsHeaderCompactLayout;

    public bool ShowHeaderSubfolderToggle => !IsHeaderCompactLayout;

    public bool ShowFooterActions => !IsHeaderCompactLayout;

    public bool ShowSidebarInMain => IsSidebarVisible && !IsNarrowLayout;

    public bool ShowInspectorInMain => IsInspectorVisible && !IsPreviewFocusedLayout;

    public bool ShowFilmstripInMain => IsFilmstripVisible && HasImages && !ShowContactSheetViewer;

    public bool ShowWorkspaceTabsInMain => (HasImages || HasRecentSessions) && !ShowContactSheetViewer && !ShowCompareViewerSurface;

    public bool ShowSelectedPreviewLoadingOverlay => IsSelectedPreviewLoading && ShowSinglePreviewSurface;

    public GridLength SidebarColumnWidth => new(ShowSidebarInMain ? CurrentSidebarWidth : 0);

    public GridLength SidebarGapColumnWidth => new(ShowSidebarInMain ? SidebarGapWidthValue : 0);

    public GridLength InspectorSpacerColumnWidth => new(ShowInspectorInMain ? InspectorGapWidthValue : 0);

    public GridLength InspectorColumnWidth => new(ShowInspectorInMain ? CurrentInspectorWidth : 0);

    private double CurrentSidebarWidth => IsCompactLayout ? CompactSidebarWidthValue : SidebarWidthValue;

    private double CurrentInspectorWidth => IsCompactLayout ? CompactInspectorWidthValue : InspectorWidthValue;

    public Thickness MainShellMargin => IsCompactLayout ? new Thickness(2) : new Thickness(4);

    public double PreviewPanelMinHeight => IsShortLayout ? 180 : 228;

    public double FilmstripHeight => IsShortLayout ? 74 : 88;

    public double FilmstripItemWidth => IsShortLayout ? 94 : 110;

    public double FilmstripThumbnailHeight => IsShortLayout ? 44 : 56;

    public GridLength ToolboxGapColumnWidth => new(IsToolboxCompactLayout ? 0 : 8);

    public GridLength ToolboxBatchLeftColumnWidth => new(1, GridUnitType.Star);

    public GridLength ToolboxBatchRightColumnWidth => IsToolboxCompactLayout
        ? new GridLength(0)
        : new GridLength(2, GridUnitType.Star);

    public GridLength ToolboxRightColumnWidth => IsToolboxCompactLayout
        ? new GridLength(0)
        : new GridLength(2, GridUnitType.Star);

    public GridLength ToolboxSecondRowHeight => IsToolboxCompactLayout
        ? GridLength.Auto
        : new GridLength(0);

    public int ToolboxRightPaneRow => IsToolboxCompactLayout ? 1 : 0;

    public int ToolboxRightPaneColumn => IsToolboxCompactLayout ? 0 : 2;

    public Thickness ToolboxRightPaneMargin => IsToolboxCompactLayout
        ? new Thickness(0, 12, 0, 0)
        : new Thickness(0, 0, 0, 0);

    public Thickness ToolboxWindowMargin => IsToolboxCompactLayout
        ? new Thickness(2)
        : new Thickness(6);

    public Thickness ToolboxScrollMargin => IsToolboxCompactLayout
        ? new Thickness(2)
        : new Thickness(8);

    public double ToolboxCompareItemWidth => IsToolboxCompactLayout ? 260 : 312;

    public double ToolboxComparePreviewHeight => IsToolboxCompactLayout ? 180 : 236;

    public bool CanToggleContactSheet => HasImages;

    public bool CanToggleCompareViewer => HasImages || HasCompareItems;

    public bool CanToggleFilmstrip => HasImages;

    public string SidebarToggleText => IsSidebarVisible ? "隐藏侧栏" : "显示侧栏";

    public string ContactSheetToggleText => IsContactSheetVisible ? "退出总览" : "联系表";

    public string CompareViewerToggleText => IsCompareViewerVisible ? "退出对比" : "对比模式";

    public string FilmstripToggleText => IsFilmstripVisible ? "隐藏胶片栏" : "胶片栏";

    public string InspectorToggleText => IsInspectorVisible ? "隐藏信息面板" : "显示信息面板";

    public string FilmstripSummaryText => Images.Count switch
    {
        <= 0 => string.Empty,
        1 => "当前只有 1 张图片。",
        _ => "底部胶片栏会跟着当前筛选后的列表同步变化。"
    };

    public void ToggleSidebar()
    {
        IsSidebarVisible = !IsSidebarVisible;
        OperationStatusText = IsSidebarVisible ? "已显示左侧缩略图栏。" : "已隐藏左侧缩略图栏。";
    }

    public void ToggleContactSheet()
    {
        if (!HasImages)
        {
            OperationStatusText = "请先载入图片后再打开联系表。";
            return;
        }

        if (!IsContactSheetVisible && IsCompareViewerVisible)
        {
            IsCompareViewerVisible = false;
        }

        IsContactSheetVisible = !IsContactSheetVisible;
        OperationStatusText = IsContactSheetVisible ? "已切换到联系表模式。" : "已返回单图预览。";
    }

    public void ToggleCompareViewer()
    {
        if (!HasImages && !HasCompareItems)
        {
            OperationStatusText = "请先载入图片，再打开对比模式。";
            return;
        }

        if (IsCompareViewerVisible)
        {
            IsCompareViewerVisible = false;
            OperationStatusText = "已返回单图预览。";
            return;
        }

        if (IsContactSheetVisible)
        {
            IsContactSheetVisible = false;
        }

        if (SelectedImage is null && Images.Count > 0)
        {
            SelectedImage = Images[0];
        }

        if (!HasCompareItems && SelectedImage is not null)
        {
            AddCurrentToCompare();
        }

        IsCompareViewerVisible = true;
        OperationStatusText = HasEnoughCompareItems
            ? "已切换到对比模式。"
            : "已打开对比模式，再加 1 张图就可以并排对比。";
    }

    public void ToggleFilmstrip()
    {
        if (!HasImages)
        {
            OperationStatusText = "请先载入图片后再打开胶片栏。";
            return;
        }

        IsFilmstripVisible = !IsFilmstripVisible;
        OperationStatusText = IsFilmstripVisible ? "已显示底部胶片栏。" : "已隐藏底部胶片栏。";
    }

    public void ToggleInspector()
    {
        IsInspectorVisible = !IsInspectorVisible;
        OperationStatusText = IsInspectorVisible ? "已显示信息面板。" : "已隐藏信息面板。";
    }

    private void NotifyLayoutStateChanged()
    {
        OnPropertyChanged(nameof(HasImages));
        OnPropertyChanged(nameof(CanToggleContactSheet));
        OnPropertyChanged(nameof(CanToggleCompareViewer));
        OnPropertyChanged(nameof(CanToggleFilmstrip));
        OnPropertyChanged(nameof(IsSidebarVisible));
        OnPropertyChanged(nameof(IsCompactLayout));
        OnPropertyChanged(nameof(IsHeaderCompactLayout));
        OnPropertyChanged(nameof(IsNarrowLayout));
        OnPropertyChanged(nameof(IsPreviewFocusedLayout));
        OnPropertyChanged(nameof(IsShortLayout));
        OnPropertyChanged(nameof(ShowFullHeaderActions));
        OnPropertyChanged(nameof(ShowCompactHeaderActions));
        OnPropertyChanged(nameof(ShowHeaderSubfolderToggle));
        OnPropertyChanged(nameof(ShowFooterActions));
        OnPropertyChanged(nameof(ShowSidebarInMain));
        OnPropertyChanged(nameof(ShowInspectorInMain));
        OnPropertyChanged(nameof(SidebarColumnWidth));
        OnPropertyChanged(nameof(SidebarGapColumnWidth));
        OnPropertyChanged(nameof(InspectorSpacerColumnWidth));
        OnPropertyChanged(nameof(InspectorColumnWidth));
        OnPropertyChanged(nameof(SidebarToggleText));
        OnPropertyChanged(nameof(ShowContactSheetViewer));
        OnPropertyChanged(nameof(ShowCompareViewerSurface));
        OnPropertyChanged(nameof(ShowSinglePreviewSurface));
        OnPropertyChanged(nameof(ShowContactSheetInMain));
        OnPropertyChanged(nameof(ShowFilmstripInMain));
        OnPropertyChanged(nameof(ShowWorkspaceTabsInMain));
        OnPropertyChanged(nameof(ContactSheetToggleText));
        OnPropertyChanged(nameof(CompareViewerToggleText));
        OnPropertyChanged(nameof(FilmstripToggleText));
        OnPropertyChanged(nameof(IsInspectorVisible));
        OnPropertyChanged(nameof(ShowInspectorInMain));
        OnPropertyChanged(nameof(InspectorSpacerColumnWidth));
        OnPropertyChanged(nameof(InspectorColumnWidth));
        OnPropertyChanged(nameof(InspectorToggleText));
        OnPropertyChanged(nameof(FilmstripSummaryText));
        OnPropertyChanged(nameof(MainShellMargin));
        OnPropertyChanged(nameof(PreviewPanelMinHeight));
        OnPropertyChanged(nameof(FilmstripHeight));
        OnPropertyChanged(nameof(FilmstripItemWidth));
        OnPropertyChanged(nameof(FilmstripThumbnailHeight));
        OnPropertyChanged(nameof(ViewerHeadlineText));
        OnPropertyChanged(nameof(ViewerSubtitleText));
        OnPropertyChanged(nameof(ViewerPathText));
        OnPropertyChanged(nameof(InspectorModeText));
        OnPropertyChanged(nameof(ShowSelectedPreviewLoadingOverlay));
    }

    public void SetLayoutViewportWidth(double viewportWidth)
    {
        SetLayoutViewportSize(viewportWidth, _layoutViewportHeight);
    }

    public void SetLayoutViewportSize(double viewportWidth, double viewportHeight)
    {
        var isCompactLayout = viewportWidth < CompactLayoutWidth;
        var normalizedWidth = Math.Max(1, viewportWidth);
        var normalizedHeight = Math.Max(1, viewportHeight);
        var widthChanged = Math.Abs(_layoutViewportWidth - normalizedWidth) >= 0.5;
        var heightChanged = Math.Abs(_layoutViewportHeight - normalizedHeight) >= 0.5;
        var compactChanged = _isCompactLayout != isCompactLayout;

        if (!widthChanged && !heightChanged && !compactChanged)
        {
            return;
        }

        _layoutViewportWidth = normalizedWidth;
        _layoutViewportHeight = normalizedHeight;
        SetProperty(ref _isCompactLayout, isCompactLayout, nameof(IsCompactLayout));

        OnPropertyChanged(nameof(IsHeaderCompactLayout));
        OnPropertyChanged(nameof(IsNarrowLayout));
        OnPropertyChanged(nameof(IsPreviewFocusedLayout));
        OnPropertyChanged(nameof(IsShortLayout));
        OnPropertyChanged(nameof(ShowFullHeaderActions));
        OnPropertyChanged(nameof(ShowCompactHeaderActions));
        OnPropertyChanged(nameof(ShowHeaderSubfolderToggle));
        OnPropertyChanged(nameof(ShowFooterActions));
        OnPropertyChanged(nameof(ShowSidebarInMain));
        OnPropertyChanged(nameof(ShowInspectorInMain));
        OnPropertyChanged(nameof(SidebarColumnWidth));
        OnPropertyChanged(nameof(SidebarGapColumnWidth));
        OnPropertyChanged(nameof(InspectorSpacerColumnWidth));
        OnPropertyChanged(nameof(InspectorColumnWidth));
        OnPropertyChanged(nameof(ShowFilmstripInMain));
        OnPropertyChanged(nameof(MainShellMargin));
        OnPropertyChanged(nameof(PreviewPanelMinHeight));
        OnPropertyChanged(nameof(FilmstripHeight));
        OnPropertyChanged(nameof(FilmstripItemWidth));
        OnPropertyChanged(nameof(FilmstripThumbnailHeight));
        QueueThumbnailWarmup();
    }

    public void SetToolboxViewportSize(double viewportWidth, double viewportHeight)
    {
        var normalizedWidth = Math.Max(1, viewportWidth);
        if (Math.Abs(_toolboxViewportWidth - normalizedWidth) < 0.5)
        {
            return;
        }

        _toolboxViewportWidth = normalizedWidth;
        OnPropertyChanged(nameof(IsToolboxCompactLayout));
        OnPropertyChanged(nameof(ToolboxGapColumnWidth));
        OnPropertyChanged(nameof(ToolboxBatchLeftColumnWidth));
        OnPropertyChanged(nameof(ToolboxBatchRightColumnWidth));
        OnPropertyChanged(nameof(ToolboxRightColumnWidth));
        OnPropertyChanged(nameof(ToolboxSecondRowHeight));
        OnPropertyChanged(nameof(ToolboxRightPaneRow));
        OnPropertyChanged(nameof(ToolboxRightPaneColumn));
        OnPropertyChanged(nameof(ToolboxRightPaneMargin));
        OnPropertyChanged(nameof(ToolboxWindowMargin));
        OnPropertyChanged(nameof(ToolboxScrollMargin));
        OnPropertyChanged(nameof(ToolboxCompareItemWidth));
        OnPropertyChanged(nameof(ToolboxComparePreviewHeight));
        MaybeReloadComparePreviewsForDecodeSize();
    }
}

