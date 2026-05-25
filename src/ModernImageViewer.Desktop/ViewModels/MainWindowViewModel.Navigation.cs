namespace ModernImageViewer.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    public bool CanNavigateImages => Images.Count > 1
        && SelectedImage is not null
        && !IsExportProcessing
        && ShowSinglePreviewSurface;

    public string CurrentImagePositionText
    {
        get
        {
            if (Images.Count == 0 || SelectedImage is null)
            {
                return "当前列表 0 / 0";
            }

            var currentIndex = GetCurrentImageIndex();
            if (currentIndex < 0)
            {
                return $"当前列表 0 / {Images.Count}";
            }

            return $"当前列表 {currentIndex + 1} / {Images.Count}";
        }
    }

    public string CurrentImageNavigationHintText => Images.Count switch
    {
        <= 0 => "--",
        1 => "当前列表只有 1 张图片。",
        _ => "按当前筛选后的列表顺序切换。"
    };

    private void NotifyNavigationStateChanged()
    {
        OnPropertyChanged(nameof(CanNavigateImages));
        OnPropertyChanged(nameof(CurrentImagePositionText));
        OnPropertyChanged(nameof(CurrentImageNavigationHintText));
    }
}

