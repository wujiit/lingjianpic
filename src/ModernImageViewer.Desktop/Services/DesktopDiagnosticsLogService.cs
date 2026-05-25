using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ModernImageViewer.Desktop.Services;

internal enum DesktopDiagnosticsSeverity
{
    Information,
    Warning,
    Error
}

internal sealed record DesktopDiagnosticsLogEntry(
    DateTimeOffset TimestampUtc,
    string SessionId,
    string Severity,
    string Category,
    string EventName,
    string? Result,
    double? DurationMilliseconds,
    IReadOnlyDictionary<string, string>? Properties,
    string? ExceptionType,
    string? ExceptionMessage);

internal sealed record DesktopBatchDiagnosticsReport(
    DateTimeOffset TimestampUtc,
    string SessionId,
    string OperationName,
    int TotalCount,
    int ProcessedCount,
    int SuccessCount,
    bool WasCanceled,
    IReadOnlyList<DesktopBatchItemFailure> Failures,
    IReadOnlyDictionary<string, string>? Properties);

internal sealed class DesktopDiagnosticsOperationTrace : IDisposable
{
    private readonly DesktopDiagnosticsLogService _owner;
    private readonly string _category;
    private readonly string _eventName;
    private readonly IReadOnlyDictionary<string, string>? _properties;
    private readonly long _startedAt;
    private int _completed;

    internal DesktopDiagnosticsOperationTrace(
        DesktopDiagnosticsLogService owner,
        string category,
        string eventName,
        IReadOnlyDictionary<string, string>? properties)
    {
        _owner = owner;
        _category = category;
        _eventName = eventName;
        _properties = properties;
        _startedAt = Stopwatch.GetTimestamp();
    }

    public void Success(IReadOnlyDictionary<string, string>? properties = null)
    {
        Complete("success", null, properties);
    }

    public void Canceled(IReadOnlyDictionary<string, string>? properties = null)
    {
        Complete("canceled", null, properties);
    }

    public void Fail(Exception exception, IReadOnlyDictionary<string, string>? properties = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Complete("error", exception, properties);
    }

    public void Dispose()
    {
    }

    private void Complete(string result, Exception? exception, IReadOnlyDictionary<string, string>? properties)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }

        var duration = Stopwatch.GetElapsedTime(_startedAt);
        var mergedProperties = DesktopDiagnosticsLogService.MergeProperties(_properties, properties);
        _owner.Write(
            exception is null ? DesktopDiagnosticsSeverity.Information : DesktopDiagnosticsSeverity.Error,
            _category,
            _eventName,
            result,
            duration,
            mergedProperties,
            exception);
    }
}

internal sealed class DesktopDiagnosticsLogService
{
    private const int MaximumDiagnosticLogFiles = 14;
    private const int MaximumBatchReportFiles = 120;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions CompactJsonOptions = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _syncRoot = new();
    private readonly string _logDirectory;
    private readonly string _batchReportDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly string _sessionId;

    public static DesktopDiagnosticsLogService Shared { get; } = new();

    public DesktopDiagnosticsLogService()
        : this(GetDefaultRootDirectory(), TimeProvider.System)
    {
    }

    internal DesktopDiagnosticsLogService(string rootDirectory, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logDirectory = Path.Combine(rootDirectory, "logs");
        _batchReportDirectory = Path.Combine(_logDirectory, "batch-reports");
        _sessionId = Guid.NewGuid().ToString("N");
    }

    internal string LogDirectory => _logDirectory;

    internal string BatchReportDirectory => _batchReportDirectory;

    internal string SessionId => _sessionId;

    public DesktopDiagnosticsOperationTrace BeginOperation(
        string category,
        string eventName,
        IReadOnlyDictionary<string, string>? properties = null)
    {
        return new DesktopDiagnosticsOperationTrace(this, category, eventName, properties);
    }

    public void WriteInfo(
        string category,
        string eventName,
        IReadOnlyDictionary<string, string>? properties = null)
    {
        Write(DesktopDiagnosticsSeverity.Information, category, eventName, null, null, properties, null);
    }

    public void WriteWarning(
        string category,
        string eventName,
        IReadOnlyDictionary<string, string>? properties = null)
    {
        Write(DesktopDiagnosticsSeverity.Warning, category, eventName, null, null, properties, null);
    }

    public void WriteError(
        string category,
        string eventName,
        Exception exception,
        IReadOnlyDictionary<string, string>? properties = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Write(DesktopDiagnosticsSeverity.Error, category, eventName, "error", null, properties, exception);
    }

    public void WriteBatchReport(
        string operationName,
        int totalCount,
        int processedCount,
        int successCount,
        bool wasCanceled,
        IReadOnlyList<DesktopBatchItemFailure> failures,
        IReadOnlyDictionary<string, string>? properties = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentNullException.ThrowIfNull(failures);

        try
        {
            EnsureDirectories();

            var now = _timeProvider.GetUtcNow();
            var report = new DesktopBatchDiagnosticsReport(
                now,
                _sessionId,
                operationName,
                totalCount,
                processedCount,
                successCount,
                wasCanceled,
                failures,
                properties);
            var filePath = Path.Combine(
                _batchReportDirectory,
                $"{now:yyyyMMdd-HHmmssfff}-{SanitizeFileName(operationName)}.json");
            var json = JsonSerializer.Serialize(report, JsonOptions);

            lock (_syncRoot)
            {
                DesktopFileStreamFactory.WriteAtomically(filePath, stream =>
                {
                    using var writer = new StreamWriter(stream, StrictUtf8, bufferSize: 1024, leaveOpen: true);
                    writer.Write(json);
                });

                TrimOldFiles(_batchReportDirectory, "*.json", MaximumBatchReportFiles);
            }
        }
        catch
        {
        }
    }

    internal void Write(
        DesktopDiagnosticsSeverity severity,
        string category,
        string eventName,
        string? result,
        TimeSpan? duration,
        IReadOnlyDictionary<string, string>? properties,
        Exception? exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);

        try
        {
            EnsureDirectories();

            var now = _timeProvider.GetUtcNow();
            var entry = new DesktopDiagnosticsLogEntry(
                now,
                _sessionId,
                severity.ToString(),
                category,
                eventName,
                result,
                duration?.TotalMilliseconds,
                properties,
                exception?.GetType().FullName,
                exception?.Message);
            var filePath = Path.Combine(_logDirectory, $"{now:yyyyMMdd}.diagnostics.jsonl");
            var line = JsonSerializer.Serialize(entry, CompactJsonOptions);

            lock (_syncRoot)
            {
                using var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, StrictUtf8, bufferSize: 1024, leaveOpen: false);
                writer.WriteLine(line);

                TrimOldFiles(_logDirectory, "*.diagnostics.jsonl", MaximumDiagnosticLogFiles);
            }
        }
        catch
        {
        }
    }

    internal static IReadOnlyDictionary<string, string>? MergeProperties(
        IReadOnlyDictionary<string, string>? first,
        IReadOnlyDictionary<string, string>? second)
    {
        if (first is null || first.Count == 0)
        {
            return second;
        }

        if (second is null || second.Count == 0)
        {
            return first;
        }

        var merged = new Dictionary<string, string>(first.Count + second.Count, StringComparer.Ordinal);
        foreach (var pair in first)
        {
            merged[pair.Key] = pair.Value;
        }

        foreach (var pair in second)
        {
            merged[pair.Key] = pair.Value;
        }

        return merged;
    }

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(_logDirectory);
        Directory.CreateDirectory(_batchReportDirectory);
    }

    private static void TrimOldFiles(string directoryPath, string searchPattern, int maximumFileCount)
    {
        var files = new DirectoryInfo(directoryPath)
            .GetFiles(searchPattern)
            .OrderByDescending(static file => file.LastWriteTimeUtc)
            .ToArray();
        for (var index = maximumFileCount; index < files.Length; index++)
        {
            try
            {
                files[index].Delete();
            }
            catch
            {
            }
        }
    }

    private static string GetDefaultRootDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ModernImageViewer.Desktop");
    }

    private static string SanitizeFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(Array.IndexOf(invalidChars, character) >= 0 ? '_' : character);
        }

        var sanitized = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "report" : sanitized;
    }
}
