using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var diagnostics = DesktopDiagnosticsLogService.Shared;
        diagnostics.WriteInfo(
            "app",
            "startup.begin",
            new Dictionary<string, string>
            {
                ["os"] = RuntimeInformation.OSDescription,
                ["framework"] = RuntimeInformation.FrameworkDescription,
                ["processArch"] = RuntimeInformation.ProcessArchitecture.ToString(),
                ["argumentCount"] = args.Length.ToString()
            });

        try
        {
            PrepareProcessWorkingDirectory();
            DesktopImageProcessingPolicy.Configure();
            diagnostics.WriteInfo(
                "app",
                "startup.ready",
                new Dictionary<string, string>
                {
                    ["workingDirectory"] = Environment.CurrentDirectory,
                    ["threadLimit"] = DesktopImageProcessingPolicy.ThreadLimit.ToString(),
                    ["magickLimit"] = DesktopImageProcessingPolicy.MagickOperationLimit.ToString()
                });
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            diagnostics.WriteInfo("app", "shutdown.completed");
        }
        catch (Exception ex)
        {
            diagnostics.WriteError("app", "startup.failed", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }

    private static void PrepareProcessWorkingDirectory()
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                return;
            }

            var workingDirectory = Path.Combine(localAppData, "LingJianImageAssistant");
            Directory.CreateDirectory(workingDirectory);
            Directory.SetCurrentDirectory(workingDirectory);
        }
        catch
        {
        }
    }
}
