namespace ModernImageViewer.Desktop.ViewModels;

public sealed class ImageMatchItemViewModel
{
    public ImageMatchItemViewModel(
        ImageListItemViewModel? thumbnailItem,
        string fullPath,
        string roleLabel,
        string matchKindLabel,
        bool isSuggestedKeep,
        IReadOnlyList<string> pathsToSelectWhenKeeping)
    {
        ThumbnailItem = thumbnailItem;
        FullPath = fullPath;
        RoleLabel = roleLabel;
        MatchKindLabel = matchKindLabel;
        IsSuggestedKeep = isSuggestedKeep;
        PathsToSelectWhenKeeping = pathsToSelectWhenKeeping;
    }

    public ImageListItemViewModel? ThumbnailItem { get; }

    public string FullPath { get; }

    public string FileName => Path.GetFileName(FullPath);

    public string FolderPath => Path.GetDirectoryName(FullPath) ?? "位置未知";

    public string RoleLabel { get; }

    public string MatchKindLabel { get; }

    public bool IsSuggestedKeep { get; }

    public IReadOnlyList<string> PathsToSelectWhenKeeping { get; }

    public string KeepActionText => string.Equals(MatchKindLabel, "完全重复", StringComparison.Ordinal)
        ? "保留这张"
        : "设为参考";

    public string SelectionSummaryText => PathsToSelectWhenKeeping.Count switch
    {
        <= 0 => "当前组里没有其他候选图片。",
        1 => "会选中其余 1 张候选图片。",
        _ => $"会选中其余 {PathsToSelectWhenKeeping.Count} 张候选图片。"
    };
}

