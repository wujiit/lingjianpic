using System.Text;
using System.Text.Json;

namespace ModernImageViewer.Desktop.Services;

internal sealed record DesktopImageFingerprintTextEntry(
    DesktopImageFingerprintKind Kind,
    string Path,
    long SizeBytes,
    long SourceStampTicks,
    string Value);

internal sealed record DesktopImageFingerprintDifferenceHashEntry(
    string Path,
    long SizeBytes,
    long SourceStampTicks,
    ulong Value);

internal sealed class DesktopImageFingerprintStateStore
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions SerializerOptions = new();

    private readonly string _textStorePath;
    private readonly string _differenceHashStorePath;

    public DesktopImageFingerprintStateStore()
        : this(GetDefaultTextStorePath(), GetDefaultDifferenceHashStorePath())
    {
    }

    internal DesktopImageFingerprintStateStore(string textStorePath, string differenceHashStorePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(textStorePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(differenceHashStorePath);

        _textStorePath = textStorePath;
        _differenceHashStorePath = differenceHashStorePath;
    }

    public IReadOnlyList<DesktopImageFingerprintTextEntry> LoadTextEntries()
    {
        return LoadEntries<DesktopImageFingerprintTextEntry>(_textStorePath);
    }

    public IReadOnlyList<DesktopImageFingerprintDifferenceHashEntry> LoadDifferenceHashEntries()
    {
        return LoadEntries<DesktopImageFingerprintDifferenceHashEntry>(_differenceHashStorePath);
    }

    public bool SaveTextEntries(IReadOnlyList<DesktopImageFingerprintTextEntry> entries)
    {
        return SaveEntries(_textStorePath, entries);
    }

    public bool SaveDifferenceHashEntries(IReadOnlyList<DesktopImageFingerprintDifferenceHashEntry> entries)
    {
        return SaveEntries(_differenceHashStorePath, entries);
    }

    private static string GetDefaultTextStorePath()
    {
        return Path.Combine(GetDefaultRootDirectory(), "image-fingerprint-text-cache.json");
    }

    private static string GetDefaultDifferenceHashStorePath()
    {
        return Path.Combine(GetDefaultRootDirectory(), "image-fingerprint-dhash-cache.json");
    }

    private static string GetDefaultRootDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ModernImageViewer.Desktop");
    }

    private static IReadOnlyList<T> LoadEntries<T>(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            using var stream = File.OpenRead(path);
            using var reader = new StreamReader(stream, StrictUtf8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
            var json = reader.ReadToEnd();
            return JsonSerializer.Deserialize<List<T>>(json, SerializerOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static bool SaveEntries<T>(string path, IReadOnlyList<T> entries)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(entries, SerializerOptions);
            DesktopFileStreamFactory.WriteAtomically(path, stream =>
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
}
