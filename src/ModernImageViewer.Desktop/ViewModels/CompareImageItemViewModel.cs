using Avalonia.Media.Imaging;
using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Desktop.ViewModels;

public sealed class CompareImageItemViewModel : ViewModelBase, IDisposable
{
    private Bitmap? _previewBitmap;
    private string _roleLabel = "对比";
    private string _statusText = "等待载入";
    private string _detailsText;
    private bool _isPrimary;
    private bool _isPreviewLoading;
    private double _zoomFactor = 1;
    private int _previewPixelWidth;
    private int _previewPixelHeight;
    private int _previewDecodeLongEdge;

    public CompareImageItemViewModel(ImageListItemViewModel source)
    {
        FullPath = source.FullPath;
        FileName = source.FileName;
        FolderPath = Path.GetDirectoryName(source.FullPath) ?? "位置未知";
        SizeBytes = source.SizeBytes;
        ModifiedAt = source.ModifiedAt;
        Signature = source.Signature;
        _detailsText = BuildFallbackDetailsText();
    }

    public string FullPath { get; }

    public string FileName { get; }

    public string FolderPath { get; }

    public long SizeBytes { get; }

    public DateTimeOffset ModifiedAt { get; }

    internal DesktopFileSignature Signature { get; }

    public Bitmap? PreviewBitmap
    {
        get => _previewBitmap;
        private set
        {
            if (ReferenceEquals(_previewBitmap, value))
            {
                return;
            }

            _previewBitmap?.Dispose();
            _previewBitmap = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPreview));
            OnPropertyChanged(nameof(ShowPreviewPlaceholder));
        }
    }

    public bool HasPreview => PreviewBitmap is not null;

    public bool IsPreviewLoading => _isPreviewLoading;

    public int PreviewDecodeLongEdge => _previewDecodeLongEdge;

    public bool ShowPreviewPlaceholder => PreviewBitmap is null;

    public string RoleLabel
    {
        get => _roleLabel;
        private set => SetProperty(ref _roleLabel, value);
    }

    public bool IsPrimary
    {
        get => _isPrimary;
        private set
        {
            if (!SetProperty(ref _isPrimary, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanPromote));
        }
    }

    public bool CanPromote => !IsPrimary;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string DetailsText
    {
        get => _detailsText;
        private set => SetProperty(ref _detailsText, value);
    }

    public double DisplayWidth => _previewPixelWidth <= 0
        ? 1
        : Math.Max(1, _previewPixelWidth * _zoomFactor);

    public double DisplayHeight => _previewPixelHeight <= 0
        ? 1
        : Math.Max(1, _previewPixelHeight * _zoomFactor);

    public void SetRole(bool isPrimary, int compareIndex)
    {
        IsPrimary = isPrimary;
        RoleLabel = isPrimary ? "主图" : $"对比 {compareIndex}";
    }

    public void SetZoomFactor(double zoomFactor)
    {
        var normalized = Math.Clamp(zoomFactor, 0.25, 3);
        if (Math.Abs(_zoomFactor - normalized) < 0.0001)
        {
            return;
        }

        _zoomFactor = normalized;
        OnPropertyChanged(nameof(DisplayWidth));
        OnPropertyChanged(nameof(DisplayHeight));
    }

    public void MarkLoading()
    {
        _isPreviewLoading = true;
        StatusText = "加载中...";
        DetailsText = BuildFallbackDetailsText();
    }

    public void MarkRefreshing()
    {
        _isPreviewLoading = true;
    }

    public void ResetLoadingState()
    {
        _isPreviewLoading = false;
    }

    public void UpdatePreview(Bitmap bitmap, int decodeLongEdge)
    {
        var pixelSize = bitmap.PixelSize;
        _previewPixelWidth = pixelSize.Width;
        _previewPixelHeight = pixelSize.Height;
        _previewDecodeLongEdge = Math.Max(0, decodeLongEdge);
        _isPreviewLoading = false;
        PreviewBitmap = bitmap;
        DetailsText = $"{_previewPixelWidth} x {_previewPixelHeight} / {FormatFileSize(SizeBytes)} / {ModifiedAt:yyyy-MM-dd HH:mm}";
        StatusText = "预览已就绪";
        OnPropertyChanged(nameof(DisplayWidth));
        OnPropertyChanged(nameof(DisplayHeight));
    }

    public void MarkUnavailable(string statusText, string detailsText)
    {
        _previewPixelWidth = 0;
        _previewPixelHeight = 0;
        _previewDecodeLongEdge = 0;
        _isPreviewLoading = false;
        PreviewBitmap = null;
        StatusText = string.IsNullOrWhiteSpace(statusText) ? "无法预览" : statusText;
        DetailsText = string.IsNullOrWhiteSpace(detailsText) ? BuildFallbackDetailsText() : detailsText;
        OnPropertyChanged(nameof(DisplayWidth));
        OnPropertyChanged(nameof(DisplayHeight));
    }

    public void Dispose()
    {
        _previewDecodeLongEdge = 0;
        _isPreviewLoading = false;
        PreviewBitmap = null;
    }

    private string BuildFallbackDetailsText()
    {
        return $"{FormatFileSize(SizeBytes)} / {ModifiedAt:yyyy-MM-dd HH:mm}";
    }

    private static string FormatFileSize(long sizeBytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = sizeBytes;
        var unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{size:0} {units[unitIndex]}"
            : $"{size:0.##} {units[unitIndex]}";
    }
}
