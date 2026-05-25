using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly DesktopDiagnosticsLogService _diagnosticsLog = DesktopDiagnosticsLogService.Shared;

    private DesktopDiagnosticsOperationTrace BeginDiagnosticsOperation(
        string category,
        string eventName,
        params (string Key, string? Value)[] properties)
    {
        return _diagnosticsLog.BeginOperation(category, eventName, CreateDiagnosticsProperties(properties));
    }

    private void WriteDiagnosticsInfo(
        string category,
        string eventName,
        params (string Key, string? Value)[] properties)
    {
        _diagnosticsLog.WriteInfo(category, eventName, CreateDiagnosticsProperties(properties));
    }

    private void WriteDiagnosticsWarning(
        string category,
        string eventName,
        params (string Key, string? Value)[] properties)
    {
        _diagnosticsLog.WriteWarning(category, eventName, CreateDiagnosticsProperties(properties));
    }

    private void WriteDiagnosticsError(
        string category,
        string eventName,
        Exception exception,
        params (string Key, string? Value)[] properties)
    {
        _diagnosticsLog.WriteError(category, eventName, exception, CreateDiagnosticsProperties(properties));
    }

    private void WriteBatchDiagnosticsReport(
        string operationName,
        int totalCount,
        int processedCount,
        int successCount,
        bool wasCanceled,
        IReadOnlyList<DesktopBatchItemFailure> failures,
        params (string Key, string? Value)[] properties)
    {
        _diagnosticsLog.WriteBatchReport(
            operationName,
            totalCount,
            processedCount,
            successCount,
            wasCanceled,
            failures,
            CreateDiagnosticsProperties(properties));
    }

    private static IReadOnlyDictionary<string, string>? CreateDiagnosticsProperties(params (string Key, string? Value)[] properties)
    {
        if (properties.Length == 0)
        {
            return null;
        }

        Dictionary<string, string>? bag = null;
        foreach (var (key, value) in properties)
        {
            if (string.IsNullOrWhiteSpace(key) || value is null)
            {
                continue;
            }

            bag ??= new Dictionary<string, string>(StringComparer.Ordinal);
            bag[key] = value;
        }

        return bag;
    }
}
