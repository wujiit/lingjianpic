namespace ModernImageViewer.Desktop.ViewModels;

public sealed class RecentSessionItemViewModel : ViewModelBase
{
    public RecentSessionItemViewModel(
        string label,
        string subtitle,
        IReadOnlyList<string> inputs,
        DateTimeOffset openedAt,
        bool isPinned)
    {
        Label = label;
        Subtitle = subtitle;
        Inputs = inputs;
        OpenedAt = openedAt.ToLocalTime();
        IsPinned = isPinned;
    }

    public string Label { get; }

    public string Subtitle { get; }

    public IReadOnlyList<string> Inputs { get; }

    public DateTimeOffset OpenedAt { get; }

    public bool IsPinned { get; private set; }

    public string OpenedAtText => OpenedAt.ToString("yyyy-MM-dd HH:mm");

    public string PinActionText => IsPinned ? "取消置顶" : "置顶";

    public void SetPinned(bool value)
    {
        if (IsPinned == value)
        {
            return;
        }

        IsPinned = value;
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(PinActionText));
    }
}

