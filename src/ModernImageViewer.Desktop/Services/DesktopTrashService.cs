using System.Diagnostics;
using Microsoft.VisualBasic.FileIO;

namespace ModernImageViewer.Desktop.Services;

public sealed class DesktopTrashService
{
    public void MoveFileToTrash(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path cannot be empty.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("File does not exist.", fullPath);
        }

        if (OperatingSystem.IsWindows())
        {
            FileSystem.DeleteFile(fullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            MoveMacFileToTrash(fullPath);
            return;
        }

        throw new PlatformNotSupportedException("Moving files to trash is only supported on Windows and macOS.");
    }

    private static void MoveMacFileToTrash(string fullPath)
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "osascript",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        }.WithArguments("-e", $"tell application \"Finder\" to delete POSIX file \"{EscapeAppleScript(fullPath)}\""));

        if (process is null)
        {
            throw new InvalidOperationException("Unable to start macOS trash operation.");
        }

        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            var errorText = process.StandardError.ReadToEnd();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorText)
                ? "macOS trash operation failed."
                : errorText.Trim());
        }
    }

    private static string EscapeAppleScript(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}

internal static class ProcessStartInfoExtensions
{
    public static ProcessStartInfo WithArguments(this ProcessStartInfo startInfo, params string[] arguments)
    {
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}
