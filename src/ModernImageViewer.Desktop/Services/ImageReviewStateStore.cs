using System.Text.Json;

namespace ModernImageViewer.Desktop.Services;

public enum ImageReviewStatus
{
    None = 0,
    Keep = 1,
    Reject = 2
}

public sealed record ImageReviewStateSnapshot(
    string Path,
    int Rating,
    ImageReviewStatus ReviewStatus);

public sealed class ImageReviewStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _storePath;

    public ImageReviewStateStore()
    {
        var rootDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ModernImageViewer.Desktop");
        _storePath = Path.Combine(rootDirectory, "review-states.json");
    }

    public IReadOnlyList<ImageReviewStateSnapshot> LoadStates()
    {
        try
        {
            if (!File.Exists(_storePath))
            {
                return [];
            }

            using var stream = File.OpenRead(_storePath);
            var items = JsonSerializer.Deserialize<List<ImageReviewStateSnapshot>>(stream, JsonOptions);
            return items ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void SaveStates(IReadOnlyList<ImageReviewStateSnapshot> states)
    {
        try
        {
            var directory = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            DesktopFileStreamFactory.WriteAtomically(
                _storePath,
                stream => JsonSerializer.Serialize(stream, states, JsonOptions));
        }
        catch
        {
        }
    }
}
