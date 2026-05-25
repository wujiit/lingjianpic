using System.Text.Json;
using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Tests;

public sealed class DesktopDiagnosticsLogServiceTests
{
    [Fact]
    public void WriteInfo_and_WriteBatchReport_create_structured_files()
    {
        using var paths = TestPaths.Create();
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 4, 22, 9, 30, 0, TimeSpan.Zero));
        var service = new DesktopDiagnosticsLogService(paths.RootPath, timeProvider);

        service.WriteInfo("app", "startup.ready", new Dictionary<string, string>
        {
            ["threadLimit"] = "4"
        });
        service.WriteBatchReport(
            "export",
            totalCount: 10,
            processedCount: 9,
            successCount: 8,
            wasCanceled: false,
            [new DesktopBatchItemFailure(2, "photo_003.jpg", "broken")],
            new Dictionary<string, string>
            {
                ["outputFormat"] = "Jpeg"
            });

        var logPath = Path.Combine(service.LogDirectory, "20260422.diagnostics.jsonl");
        Assert.True(File.Exists(logPath));

        var logLine = File.ReadLines(logPath).Single();
        using var logJson = JsonDocument.Parse(logLine);
        Assert.Equal("app", logJson.RootElement.GetProperty("Category").GetString());
        Assert.Equal("startup.ready", logJson.RootElement.GetProperty("EventName").GetString());
        Assert.Equal("4", logJson.RootElement.GetProperty("Properties").GetProperty("threadLimit").GetString());

        var reportPath = Directory.GetFiles(service.BatchReportDirectory, "*.json").Single();
        using var reportJson = JsonDocument.Parse(File.ReadAllText(reportPath));
        Assert.Equal("export", reportJson.RootElement.GetProperty("OperationName").GetString());
        Assert.Equal(10, reportJson.RootElement.GetProperty("TotalCount").GetInt32());
        Assert.Equal("photo_003.jpg", reportJson.RootElement.GetProperty("Failures")[0].GetProperty("DisplayName").GetString());
    }

    [Fact]
    public void BeginOperation_can_record_failure_with_duration()
    {
        using var paths = TestPaths.Create();
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 4, 22, 10, 0, 0, TimeSpan.Zero));
        var service = new DesktopDiagnosticsLogService(paths.RootPath, timeProvider);
        var trace = service.BeginOperation("preview", "load-selected-preview", new Dictionary<string, string>
        {
            ["path"] = "demo.jpg"
        });

        trace.Fail(new InvalidOperationException("decode failed"));
        trace.Dispose();

        var logPath = Path.Combine(service.LogDirectory, "20260422.diagnostics.jsonl");
        var logLine = File.ReadLines(logPath).Single();
        using var logJson = JsonDocument.Parse(logLine);
        Assert.Equal("error", logJson.RootElement.GetProperty("Result").GetString());
        Assert.Equal("System.InvalidOperationException", logJson.RootElement.GetProperty("ExceptionType").GetString());
        Assert.Equal("decode failed", logJson.RootElement.GetProperty("ExceptionMessage").GetString());
        Assert.True(logJson.RootElement.GetProperty("DurationMilliseconds").GetDouble() >= 0);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }
}
