using System.Text;
using System.Text.Json;

namespace ModernImageViewer.Desktop.Services;

internal sealed record DesktopImageDimensionStateEntry(
    string Path,
    long SizeBytes,
    long SourceStampTicks,
    uint Width,
    uint Height);

internal sealed class DesktopImageDimensionStateStore
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions SerializerOptions = new();

    private readonly string _storePath;

    public DesktopImageDimensionStateStore()
        : this(GetDefaultStorePath())
    {
    }

    internal DesktopImageDimensionStateStore(string storePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);
        _storePath = storePath;
    }

    public IReadOnlyList<DesktopImageDimensionStateEntry> LoadEntries()
    {
        try
        {
            if (!File.Exists(_storePath))
            {
                return [];
            }

            using var stream = File.OpenRead(_storePath);
            using var reader = new StreamReader(stream, StrictUtf8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
            var json = reader.ReadToEnd();
            return JsonSerializer.Deserialize<List<DesktopImageDimensionStateEntry>>(json, SerializerOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public bool SaveEntries(IReadOnlyList<DesktopImageDimensionStateEntry> entries)
    {
        try
        {
            var directory = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(entries, SerializerOptions);
            DesktopFileStreamFactory.WriteAtomically(_storePath, stream =>
            {
                using var writer = new StreamWriter(stream, StrictUtf8, bufferSize: 1024, leaveOpen: true);
                writer.Write(json);
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetDefaultStorePath()
    {
        return Path.Combine(GetDefaultRootDirectory(), "image-dimension-cache.json");
    }

    private static string GetDefaultRootDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ModernImageViewer.Desktop");
    }
}
