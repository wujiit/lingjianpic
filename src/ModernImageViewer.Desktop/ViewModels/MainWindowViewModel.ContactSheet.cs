namespace ModernImageViewer.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const int MaxContactSheetItemCount = 180;
    private const double ContactSheetGap = 12;
    private double _contactSheetViewportWidth = 960;
    private double _contactSheetViewportHeight = 620;
    private int _contactSheetColumns = 4;
    private double _contactSheetTileWidth = 188;
    private double _contactSheetTileHeight = 132;

    public IEnumerable<ImageListItemViewModel> ContactSheetItems => Images.Take(MaxContactSheetItemCount);

    public bool HasContactSheetItems => Images.Count > 0;

    public bool ShowContactSheetEmptyState => !HasContactSheetItems;

    public int ContactSheetColumns => _contactSheetColumns;

    public double ContactSheetTileWidth => _contactSheetTileWidth;

    public double ContactSheetTileHeight => _contactSheetTileHeight;

    public string ContactSheetSummaryText => Images.Count switch
    {
        <= 0 => "当前还没有可生成总览的图片。",
        1 => "当前只有 1 张图片。",
        <= MaxContactSheetItemCount => $"当前总览展示 {Images.Count} 张图片，约 {ContactSheetColumns} 列。",
        _ => $"当前总览先展示前 {MaxContactSheetItemCount} / {Images.Count} 张图片，约 {ContactSheetColumns} 列。"
    };

    public string ContactSheetEmptyText => "当前还没有可生成总览的图片。先载入一张图片，或者一个文件夹。";

    public void FocusContactSheetImage(string? fullPath)
    {
        FocusImageByPath(fullPath);
        if (IsContactSheetVisible)
        {
            IsContactSheetVisible = false;
            OperationStatusText = "已从联系表回到单图预览。";
        }
    }

    public void UpdateContactSheetViewport(double width, double height)
    {
        var normalizedWidth = Math.Max(0, width);
        var normalizedHeight = Math.Max(0, height);

        if (Math.Abs(normalizedWidth - _contactSheetViewportWidth) < 0.5
            && Math.Abs(normalizedHeight - _contactSheetViewportHeight) < 0.5)
        {
            return;
        }

        _contactSheetViewportWidth = normalizedWidth;
        _contactSheetViewportHeight = normalizedHeight;
        UpdateContactSheetLayout();
        QueueThumbnailWarmup();
    }

    private void OnContactSheetCollectionChanged()
    {
        UpdateContactSheetLayout();
        NotifyContactSheetStateChanged();
    }

    private void NotifyContactSheetStateChanged()
    {
        OnPropertyChanged(nameof(ContactSheetItems));
        OnPropertyChanged(nameof(HasContactSheetItems));
        OnPropertyChanged(nameof(ShowContactSheetEmptyState));
        OnPropertyChanged(nameof(ContactSheetColumns));
        OnPropertyChanged(nameof(ContactSheetTileWidth));
        OnPropertyChanged(nameof(ContactSheetTileHeight));
        OnPropertyChanged(nameof(ContactSheetSummaryText));
        OnPropertyChanged(nameof(ContactSheetEmptyText));
        OnPropertyChanged(nameof(ViewerHeadlineText));
        OnPropertyChanged(nameof(ViewerSubtitleText));
        OnPropertyChanged(nameof(ViewerPathText));
    }

    private void UpdateContactSheetLayout()
    {
        var itemCount = Math.Min(Images.Count, MaxContactSheetItemCount);
        if (itemCount <= 0)
        {
            UpdateContactSheetLayoutState(columns: 4, tileWidth: 188, tileHeight: 132);
            return;
        }

        var availableWidth = Math.Max(320, _contactSheetViewportWidth - 24);
        var maxColumns = Math.Min(itemCount, 12);
        var columns = Math.Clamp(
            (int)Math.Floor((availableWidth + ContactSheetGap) / (188d + ContactSheetGap)),
            1,
            Math.Max(1, maxColumns));

        while (columns > 1)
        {
            var candidateWidth = (availableWidth - (Math.Max(0, columns - 1) * ContactSheetGap)) / columns;
            if (candidateWidth >= 156)
            {
                break;
            }

            columns--;
        }

        var tileWidth = Math.Max(156, (availableWidth - (Math.Max(0, columns - 1) * ContactSheetGap)) / Math.Max(1, columns));
        var tileHeight = Math.Clamp(tileWidth * 0.74, 132, 208);
        UpdateContactSheetLayoutState(columns, tileWidth, tileHeight);
    }

    private void UpdateContactSheetLayoutState(int columns, double tileWidth, double tileHeight)
    {
        var changed = false;

        if (_contactSheetColumns != columns)
        {
            _contactSheetColumns = columns;
            OnPropertyChanged(nameof(ContactSheetColumns));
            changed = true;
        }

        if (Math.Abs(_contactSheetTileWidth - tileWidth) >= 0.5)
        {
            _contactSheetTileWidth = tileWidth;
            OnPropertyChanged(nameof(ContactSheetTileWidth));
            changed = true;
        }

        if (Math.Abs(_contactSheetTileHeight - tileHeight) >= 0.5)
        {
            _contactSheetTileHeight = tileHeight;
            OnPropertyChanged(nameof(ContactSheetTileHeight));
            changed = true;
        }

        if (changed)
        {
            OnPropertyChanged(nameof(ContactSheetSummaryText));
            OnPropertyChanged(nameof(ViewerSubtitleText));
        }
    }
}

