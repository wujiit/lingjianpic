namespace ModernImageViewer.Desktop.ViewModels;

public sealed record SequentialRenamePattern(
    string BaseName,
    string SequenceSeparator,
    int StartNumber,
    int NumberDigits)
{
    public string BuildFileBaseName(int sequenceIndex, int totalCount)
    {
        var trimmedBaseName = BaseName.Trim();
        if (totalCount <= 1)
        {
            return trimmedBaseName;
        }

        var sequence = checked(StartNumber + sequenceIndex).ToString($"D{NumberDigits}");
        return string.IsNullOrEmpty(SequenceSeparator)
            ? $"{trimmedBaseName}{sequence}"
            : $"{trimmedBaseName}{SequenceSeparator}{sequence}";
    }
}
