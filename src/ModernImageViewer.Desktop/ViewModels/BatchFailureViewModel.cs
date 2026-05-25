namespace ModernImageViewer.Desktop.ViewModels;

public sealed class BatchFailureViewModel
{
    public BatchFailureViewModel(int index, string displayName, string errorMessage)
    {
        Index = index;
        DisplayName = displayName;
        ErrorMessage = errorMessage;
    }

    public int Index { get; }

    public string DisplayName { get; }

    public string ErrorMessage { get; }

    public string SummaryText => $"{Index + 1}. {DisplayName}: {ErrorMessage}";
}
