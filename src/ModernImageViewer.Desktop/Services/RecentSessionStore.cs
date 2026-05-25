using System.Text.Json;

namespace ModernImageViewer.Desktop.Services;

public sealed class RecentSessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _storePath;

    public RecentSessionStore()
    {
        var rootDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ModernImageViewer.Desktop");
        _storePath = Path.Combine(rootDirectory, "recent-sessions.json");
    }

    public IReadOnlyList<RecentSessionSnapshot> LoadSessions()
    {
        try
        {
            if (!File.Exists(_storePath))
            {
                return [];
            }

            using var stream = File.OpenRead(_storePath);
            var items = JsonSerializer.Deserialize<List<RecentSessionSnapshot>>(stream, JsonOptions);
            return items ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void SaveSessions(IReadOnlyList<RecentSessionSnapshot> sessions)
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
                stream => JsonSerializer.Serialize(stream, sessions, JsonOptions));
        }
        catch
        {
        }
    }
}

public sealed record RecentSessionSnapshot(
    string Label,
    string Subtitle,
    List<string> Inputs,
    DateTimeOffset OpenedAt,
    bool IsPinned = false);
