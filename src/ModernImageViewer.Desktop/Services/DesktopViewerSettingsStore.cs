using System.Text;
using System.Text.Json;

namespace ModernImageViewer.Desktop.Services;

public sealed class DesktopViewerSettingsStore
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _syncRoot = new();
    private readonly string _settingsPath;
    private string? _lastSavedJson;

    public DesktopViewerSettingsStore()
        : this(GetDefaultSettingsPath())
    {
    }

    internal DesktopViewerSettingsStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsPath = settingsPath;
    }

    private static string GetDefaultSettingsPath()
    {
        var rootDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ModernImageViewer.Desktop");
        return Path.Combine(rootDirectory, "settings.json");
    }

    public DesktopViewerSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new DesktopViewerSettings();
            }

            var json = ReadSettingsText(_settingsPath);
            lock (_syncRoot)
            {
                _lastSavedJson = json;
            }

            return JsonSerializer.Deserialize<DesktopViewerSettings>(json, SerializerOptions)
                ?? new DesktopViewerSettings();
        }
        catch
        {
            return new DesktopViewerSettings();
        }
    }

    public void Save(DesktopViewerSettings settings)
    {
        try
        {
            var directoryPath = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            var json = JsonSerializer.Serialize(settings, SerializerOptions);
            lock (_syncRoot)
            {
                if (string.Equals(_lastSavedJson, json, StringComparison.Ordinal))
                {
                    return;
                }

                DesktopFileStreamFactory.WriteAtomically(_settingsPath, stream =>
                {
                    using var writer = new StreamWriter(stream, StrictUtf8, bufferSize: 1024, leaveOpen: true);
                    writer.Write(json);
                });
                _lastSavedJson = json;
            }
        }
        catch
        {
        }
    }

    private static string ReadSettingsText(string path)
    {
        try
        {
            return File.ReadAllText(path, StrictUtf8);
        }
        catch (DecoderFallbackException)
        {
            return File.ReadAllText(path, Encoding.Default);
        }
    }
}
