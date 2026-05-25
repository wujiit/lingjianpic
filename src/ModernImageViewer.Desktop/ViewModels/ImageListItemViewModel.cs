using Avalonia.Media.Imaging;
using ModernImageViewer.Core;
using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Desktop.ViewModels;

public sealed class ImageListItemViewModel : ViewModelBase, IDisposable
{
    private Bitmap? _thumbnail;
    private bool _isThumbnailLoading;
    private int _rating;
    private ImageReviewStatus _reviewStatus;

    public ImageListItemViewModel(ImageRecord record)
    {
        FullPath = record.FullPath;
        FileName = record.FileName;
        SizeBytes = record.SizeBytes;
        ModifiedAt = record.ModifiedAt.ToLocalTime();
        Signature = new DesktopFileSignature(
            record.FullPath,
            record.SizeBytes,
            record.ModifiedAt.ToUniversalTime().Ticks);
    }

    public string FullPath { get; }

    public string FileName { get; }

    public long SizeBytes { get; }

    public DateTimeOffset ModifiedAt { get; }

    internal DesktopFileSignature Signature { get; }

    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            if (ReferenceEquals(_thumbnail, value))
            {
                return;
            }

            _thumbnail?.Dispose();
            _thumbnail = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasThumbnail));
            OnPropertyChanged(nameof(ShowThumbnailPlaceholder));
        }
    }

    public bool IsThumbnailLoading
    {
        get => _isThumbnailLoading;
        private set
        {
            if (!SetProperty(ref _isThumbnailLoading, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ThumbnailPlaceholderText));
            OnPropertyChanged(nameof(ThumbnailHintText));
        }
    }

    public string PathText => FullPath;

    public string SizeText => FormatFileSize(SizeBytes);

    public string ModifiedText => ModifiedAt.ToString("yyyy-MM-dd HH:mm");

    public bool HasThumbnail => Thumbnail is not null;

    public bool ShowThumbnailPlaceholder => Thumbnail is null;

    public int Rating => _rating;

    public ImageReviewStatus ReviewStatus => _reviewStatus;

    public bool HasRating => Rating > 0;

    public bool HasReviewBadge => ReviewStatus != ImageReviewStatus.None;

    public bool HasReviewState => HasReviewBadge || HasRating;

    public string ReviewBadgeText => ReviewStatus switch
    {
        ImageReviewStatus.Keep => "保留",
        ImageReviewStatus.Reject => "待删",
        _ => string.Empty
    };

    public string ReviewStatusText => ReviewStatus switch
    {
        ImageReviewStatus.Keep => "已标记保留",
        ImageReviewStatus.Reject => "已标记待删",
        _ => "未标记"
    };

    public string RatingText => Rating > 0 ? $"{Rating} 星" : "未打星";

    public string StarRatingText => Rating > 0 ? new string('★', Rating) : string.Empty;

    public string ReviewSummaryText => HasReviewState
        ? $"{ReviewStatusText} / {RatingText}"
        : "未标记 / 未打星";

    public string ThumbnailPlaceholderText => IsThumbnailLoading
        ? "加载中"
        : Path.GetExtension(FileName).TrimStart('.').ToUpperInvariant();

    public string ThumbnailHintText => IsThumbnailLoading ? "预览" : "图片";

    public void SetThumbnail(Bitmap? bitmap)
    {
        Thumbnail = bitmap;
    }

    public void SetThumbnailLoading(bool value)
    {
        IsThumbnailLoading = value;
    }

    public void ApplyReviewState(int rating, ImageReviewStatus reviewStatus)
    {
        var normalizedRating = Math.Clamp(rating, 0, 5);
        var changed = false;

        if (_rating != normalizedRating)
        {
            _rating = normalizedRating;
            OnPropertyChanged(nameof(Rating));
            OnPropertyChanged(nameof(HasRating));
            OnPropertyChanged(nameof(RatingText));
            OnPropertyChanged(nameof(StarRatingText));
            changed = true;
        }

        if (_reviewStatus != reviewStatus)
        {
            _reviewStatus = reviewStatus;
            OnPropertyChanged(nameof(ReviewStatus));
            OnPropertyChanged(nameof(HasReviewBadge));
            OnPropertyChanged(nameof(ReviewBadgeText));
            OnPropertyChanged(nameof(ReviewStatusText));
            changed = true;
        }

        if (changed)
        {
            OnPropertyChanged(nameof(HasReviewState));
            OnPropertyChanged(nameof(ReviewSummaryText));
        }
    }

    public void Dispose()
    {
        Thumbnail = null;
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


