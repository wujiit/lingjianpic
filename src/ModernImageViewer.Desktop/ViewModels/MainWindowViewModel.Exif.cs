using System.Collections.Concurrent;
using ModernImageViewer.Core;
using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly DesktopBatchProcessor _desktopBatchProcessor = new();
    private readonly DesktopExifEditService _desktopExifEditService = new();
    private bool _exifStripAllMetadata;
    private bool _exifRemoveGps = true;
    private bool _exifShiftDateTime;
    private string _exifAuthorText = string.Empty;
    private string _exifCopyrightText = string.Empty;
    private string _exifCommentText = string.Empty;
    private string _exifDateTimeOffsetMinutesText = "0";

    public bool ExifStripAllMetadata
    {
        get => _exifStripAllMetadata;
        set
        {
            if (!SetProperty(ref _exifStripAllMetadata, value))
            {
                return;
            }

            NotifyExifEditStateChanged();
            ScheduleViewerSettingsSave();
        }
    }

    public bool ExifRemoveGps
    {
        get => _exifRemoveGps;
        set
        {
            if (!SetProperty(ref _exifRemoveGps, value))
            {
                return;
            }

            NotifyExifEditStateChanged();
            ScheduleViewerSettingsSave();
        }
    }

    public bool ExifShiftDateTime
    {
        get => _exifShiftDateTime;
        set
        {
            if (!SetProperty(ref _exifShiftDateTime, value))
            {
                return;
            }

            NotifyExifEditStateChanged();
            ScheduleViewerSettingsSave();
        }
    }

    public string ExifAuthorText
    {
        get => _exifAuthorText;
        set
        {
            if (!SetProperty(ref _exifAuthorText, value))
            {
                return;
            }

            NotifyExifEditStateChanged();
            ScheduleViewerSettingsSave();
        }
    }

    public string ExifCopyrightText
    {
        get => _exifCopyrightText;
        set
        {
            if (!SetProperty(ref _exifCopyrightText, value))
            {
                return;
            }

            NotifyExifEditStateChanged();
            ScheduleViewerSettingsSave();
        }
    }

    public string ExifCommentText
    {
        get => _exifCommentText;
        set
        {
            if (!SetProperty(ref _exifCommentText, value))
            {
                return;
            }

            NotifyExifEditStateChanged();
            ScheduleViewerSettingsSave();
        }
    }

    public string ExifDateTimeOffsetMinutesText
    {
        get => _exifDateTimeOffsetMinutesText;
        set
        {
            if (!SetProperty(ref _exifDateTimeOffsetMinutesText, value))
            {
                return;
            }

            NotifyExifEditStateChanged();
            ScheduleViewerSettingsSave();
        }
    }

    public bool CanRunExifEdit => SelectedExportFormat is not null
        && GetOperationTargetCount() > 0
        && !IsExportProcessing
        && IsExifEditInputValid()
        && (!RenameExportOutputs || !string.IsNullOrWhiteSpace(NormalizeRenameBaseName(RenameBaseName)));

    public string ExifEditActionText => ProcessCurrentCollection
        ? "批量写入 EXIF"
        : HasBatchSelection ? "写入所选 EXIF" : "写入当前 EXIF";

    public string ExifEditSummaryText
    {
        get
        {
            var actions = new List<string>();
            if (ExifStripAllMetadata)
            {
                actions.Add("清理全部元数据");
            }

            if (ExifRemoveGps)
            {
                actions.Add("移除 GPS");
            }

            if (!string.IsNullOrWhiteSpace(ExifAuthorText))
            {
                actions.Add("写入作者");
            }

            if (!string.IsNullOrWhiteSpace(ExifCopyrightText))
            {
                actions.Add("写入版权");
            }

            if (!string.IsNullOrWhiteSpace(ExifCommentText))
            {
                actions.Add("写入备注");
            }

            if (ExifShiftDateTime)
            {
                actions.Add($"时间偏移 {ExifDateTimeOffsetMinutesText} 分钟");
            }

            return actions.Count == 0
                ? "选择要写入或清理的 EXIF 操作。"
                : string.Join("；", actions);
        }
    }

    public async Task EditExifSelectedAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRunExifEdit || SelectedExportFormat is null)
        {
            return;
        }

        var editRequest = BuildExifEditRequest();
        if (editRequest is null)
        {
            return;
        }

        if (!TryParseJpegQuality(ExportJpegQualityText, out var jpegQuality))
        {
            OperationStatusText = "质量请填 1 到 100 之间的整数。";
            return;
        }

        await RunExifEditAsync(editRequest, SelectedExportFormat.Value, jpegQuality, cancellationToken);
    }

    private DesktopExifEditRequest? BuildExifEditRequest()
    {
        var offsetMinutes = 0;
        if (ExifShiftDateTime && !int.TryParse(ExifDateTimeOffsetMinutesText, out offsetMinutes))
        {
            OperationStatusText = "EXIF 时间偏移请填写整数分钟，可以为负数。";
            return null;
        }

        return new DesktopExifEditRequest(
            ExifStripAllMetadata,
            ExifRemoveGps,
            ExifAuthorText,
            ExifCopyrightText,
            ExifCommentText,
            ExifShiftDateTime,
            offsetMinutes);
    }

    private bool IsExifEditInputValid()
    {
        if (ExifShiftDateTime && !int.TryParse(ExifDateTimeOffsetMinutesText, out _))
        {
            return false;
        }

        return ExifStripAllMetadata
            || ExifRemoveGps
            || ExifShiftDateTime
            || !string.IsNullOrWhiteSpace(ExifAuthorText)
            || !string.IsNullOrWhiteSpace(ExifCopyrightText)
            || !string.IsNullOrWhiteSpace(ExifCommentText);
    }

    private async Task RunExifEditAsync(
        DesktopExifEditRequest request,
        ExportImageFormat outputFormat,
        int jpegQuality,
        CancellationToken cancellationToken)
    {
        using var trackedOperation = BeginTrackedOperation();
        var targets = ResolveOperationTargets();
        if (targets.Count == 0)
        {
            return;
        }

        var destinationFolder = ResolveDestinationFolderForRun();
        Directory.CreateDirectory(destinationFolder);
        ClearBatchFailures();

        var reservedTargetPaths = new HashSet<string>(PathComparison.Comparer);
        var exportRenamePattern = GetExportRenamePatternOrNull();
        if (RenameExportOutputs && exportRenamePattern is null)
        {
            return;
        }

        var hasMultipleTargets = targets.Count > 1;
        var failureMessages = new List<string>();
        string? lastTargetPath = null;

        IsExportProcessing = true;
        BeginOperationProgress(
            targets.Count,
            hasMultipleTargets ? "正在批量写入 EXIF..." : "正在写入当前图片 EXIF...",
            hasMultipleTargets ? $"准备处理 {targets.Count} 张图片。" : "准备处理当前图片。");

        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _currentOperationCts = operationCts;
        OnPropertyChanged(nameof(CanCancelOperation));
        using var trace = BeginDiagnosticsOperation(
            "batch",
            "exif-edit",
            ("targetCount", targets.Count.ToString()),
            ("outputFormat", outputFormat.ToString()),
            ("destinationFolder", destinationFolder));

        try
        {
            var plan = new List<DesktopBatchItem<(int Index, string SourcePath, string TargetPath)>>(targets.Count);
            for (var index = 0; index < targets.Count; index++)
            {
                operationCts.Token.ThrowIfCancellationRequested();

                var sourcePath = targets[index].FullPath;
                string? targetBaseName = null;
                if (exportRenamePattern is not null)
                {
                    try
                    {
                        targetBaseName = exportRenamePattern.BuildFileBaseName(index, targets.Count);
                    }
                    catch (OverflowException)
                    {
                        OperationStatusText = "起始编号过大，无法继续 EXIF 重命名。";
                        return;
                    }
                }

                var targetPath = BuildExifTargetPath(
                    sourcePath,
                    outputFormat,
                    destinationFolder,
                    targetBaseName,
                    reservedTargetPaths);

                plan.Add(new DesktopBatchItem<(int Index, string SourcePath, string TargetPath)>(
                    (index, sourcePath, targetPath),
                    Path.GetFileName(sourcePath)));
            }

            var successfulTargets = new ConcurrentDictionary<int, string>();
            IProgress<DesktopBatchProgress> progress = new Progress<DesktopBatchProgress>(update =>
            {
                UpdateOperationProgress(
                    update.ProcessedCount,
                    targets.Count,
                    hasMultipleTargets
                        ? $"正在写入 EXIF {update.ProcessedCount}/{targets.Count}：{update.DisplayName}"
                        : null,
                    force: update.ProcessedCount == targets.Count);

            });

            var executionPlan = CreateExifExecutionPlan(targets);

            var result = await _desktopBatchProcessor.RunAsync(
                plan,
                async (item, _, token) =>
                {
                    var step = item.Value;
                    await RunBatchSynchronousWorkAsync(
                        () => _desktopExifEditService.Apply(
                            step.SourcePath,
                            step.TargetPath,
                            outputFormat,
                            jpegQuality,
                            request,
                            token),
                        executionPlan,
                        token);
                    successfulTargets[step.Index] = step.TargetPath;
                },
                progress,
                executionPlan: executionPlan,
                cancellationToken: operationCts.Token);

            foreach (var failure in result.Failures)
            {
                AddBatchFailure(failure.Index, failure.DisplayName, failure.ErrorMessage);
                failureMessages.Add($"{failure.DisplayName}：{failure.ErrorMessage}");
            }
            WriteBatchDiagnosticsReport(
                "exif-edit",
                result.TotalCount,
                result.ProcessedCount,
                result.SuccessCount,
                result.WasCanceled,
                result.Failures,
                ("targetCount", targets.Count.ToString()),
                ("outputFormat", outputFormat.ToString()),
                ("destinationFolder", destinationFolder));

            lastTargetPath = successfulTargets
                .OrderBy(static item => item.Key)
                .Select(static item => item.Value)
                .LastOrDefault();

            UpdateOperationProgress(result.ProcessedCount, targets.Count, force: true);

            if (result.WasCanceled)
            {
                OperationStatusText = hasMultipleTargets
                    ? $"本次 EXIF 批处理已取消：已处理 {result.ProcessedCount}/{targets.Count} 张。"
                    : "本次 EXIF 批处理已取消。";
                trace.Canceled(CreateDiagnosticsProperties(("processedCount", result.ProcessedCount.ToString())));
                return;
            }

            var successCount = result.SuccessCount;
            if (!hasMultipleTargets)
            {
                OperationStatusText = failureMessages.Count == 0 && !string.IsNullOrWhiteSpace(lastTargetPath)
                    ? $"已生成 EXIF 副本：{Path.GetFileName(lastTargetPath)}"
                    : $"EXIF 写入失败：{failureMessages.FirstOrDefault() ?? "未能生成输出文件"}";
                if (failureMessages.Count == 0)
                {
                    trace.Success(CreateDiagnosticsProperties(("successCount", successCount.ToString())));
                }
                else
                {
                    trace.Fail(new InvalidOperationException(failureMessages.FirstOrDefault() ?? "EXIF 写入失败"));
                }
                return;
            }

            OperationStatusText = failureMessages.Count == 0
                ? $"批量 EXIF 写入完成：共 {successCount} 张，输出：{destinationFolder}"
                : $"批量 EXIF 写入完成：成功 {successCount}，失败 {failureMessages.Count}。首个失败：{failureMessages[0]}";
            if (failureMessages.Count == 0)
            {
                trace.Success(CreateDiagnosticsProperties(("successCount", successCount.ToString())));
            }
            else
            {
                trace.Fail(
                    new InvalidOperationException(failureMessages[0]),
                    CreateDiagnosticsProperties(
                        ("successCount", successCount.ToString()),
                        ("failureCount", failureMessages.Count.ToString())));
            }
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(OperationStatusText))
            {
                BatchResultSummaryText = OperationStatusText;
            }

            if (ReferenceEquals(_currentOperationCts, operationCts))
            {
                _currentOperationCts = null;
                OnPropertyChanged(nameof(CanCancelOperation));
            }

            DesktopImageProcessingPolicy.TrimMemory();
            IsExportProcessing = false;
        }
    }

    private static DesktopBatchExecutionPlan CreateExifExecutionPlan(IReadOnlyList<ImageListItemViewModel> targets)
    {
        return DesktopBatchParallelismAdvisor.CreateAdaptiveExecutionPlan(
            DesktopBatchWorkloadKind.ExifEdit,
            targets.Count,
            SumOperationBytes(targets));
    }

    private string BuildExifTargetPath(
        string sourcePath,
        ExportImageFormat outputFormat,
        string destinationFolder,
        string? targetBaseName,
        ISet<string> reservedTargetPaths)
    {
        var targetExtension = _desktopExifEditService.GetFileExtension(sourcePath, outputFormat);
        return ExportPathBuilder.BuildAvailableTargetPath(
            sourcePath,
            destinationFolder,
            targetExtension,
            "EXIF",
            targetBaseName,
            reservedTargetPaths);
    }

    private void NotifyExifEditStateChanged()
    {
        OnPropertyChanged(nameof(CanRunExifEdit));
        OnPropertyChanged(nameof(ExifEditActionText));
        OnPropertyChanged(nameof(ExifEditSummaryText));
    }
}


