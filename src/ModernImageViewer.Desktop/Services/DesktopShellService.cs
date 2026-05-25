using System.Diagnostics;

namespace ModernImageViewer.Desktop.Services;

public sealed class DesktopShellService
{
    public void RevealInFileManager(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("路径不能为空。", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
        {
            RevealFile(fullPath);
            return;
        }

        if (Directory.Exists(fullPath))
        {
            OpenDirectory(fullPath);
            return;
        }

        throw new FileNotFoundException("要打开的路径不存在。", fullPath);
    }

    private static void RevealFile(string fullPath)
    {
        if (OperatingSystem.IsWindows())
        {
            StartProcess("explorer.exe", $"/select,\"{fullPath}\"");
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            StartProcess("open", $"-R \"{fullPath}\"");
            return;
        }

        OpenDirectory(Path.GetDirectoryName(fullPath) ?? fullPath);
    }

    private static void OpenDirectory(string directoryPath)
    {
        if (OperatingSystem.IsWindows())
        {
            StartProcess("explorer.exe", $"\"{directoryPath}\"");
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            StartProcess("open", $"\"{directoryPath}\"");
            return;
        }

        StartProcess("xdg-open", $"\"{directoryPath}\"");
    }

    private static void StartProcess(string fileName, string arguments)
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = true
        });

        if (process is null)
        {
            throw new InvalidOperationException("系统没有成功打开文件管理器。");
        }
    }
}
