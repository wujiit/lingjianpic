namespace ModernImageViewer.Desktop.ViewModels;

public sealed class ImageMatchGroupViewModel
{
    public ImageMatchGroupViewModel(
        string title,
        string subtitle,
        string kindLabel,
        string folderLabel,
        string referenceLabel,
        IReadOnlyList<string> paths,
        IReadOnlyList<string> suggestedSelectionPaths,
        IReadOnlyList<ImageMatchItemViewModel> items)
    {
        Title = title;
        Subtitle = subtitle;
        KindLabel = kindLabel;
        FolderLabel = folderLabel;
        ReferenceLabel = referenceLabel;
        Paths = paths;
        SuggestedSelectionPaths = suggestedSelectionPaths;
        Items = items;
    }

    public string Title { get; }

    public string Subtitle { get; }

    public string Summary => Subtitle;

    public string KindLabel { get; }

    public string FolderLabel { get; }

    public string ReferenceLabel { get; }

    public IReadOnlyList<string> Paths { get; }

    public IReadOnlyList<string> SuggestedSelectionPaths { get; }

    public IReadOnlyList<ImageMatchItemViewModel> Items { get; }

    public string ReferencePath => Paths.FirstOrDefault() ?? string.Empty;

    public bool IsExactDuplicateGroup => string.Equals(KindLabel, "完全重复", StringComparison.Ordinal);

    public bool IsSimilarGroup => string.Equals(KindLabel, "相似图片", StringComparison.Ordinal);

    public bool HasSuggestedSelection => SuggestedSelectionPaths.Count > 0;

    public string SelectionActionText => IsExactDuplicateGroup ? "选中建议项" : "选中本组";
}

