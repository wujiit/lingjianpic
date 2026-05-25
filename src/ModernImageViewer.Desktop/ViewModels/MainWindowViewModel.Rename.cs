using ModernImageViewer.Core;
using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const int DefaultRenameStartNumber = 1;
    private const int DefaultRenameNumberDigits = 3;
    private const int MaximumRenameStartNumber = 99_999_999;
    private const int MinimumRenameNumberDigits = 1;
    private const int MaximumRenameNumberDigits = 8;

    private string _renameBaseName = string.Empty;
    private string _renameSequenceSeparator = "_";
    private string _renameStartNumberText = DefaultRenameStartNumber.ToString();
    private string _renameNumberDigitsText = DefaultRenameNumberDigits.ToString();
    private string _lastSuggestedRenameBaseName = string.Empty;

    public string RenameBaseName
    {
        get => _renameBaseName;
        set
        {
            if (!SetProperty(ref _renameBaseName, value))
            {
                return;
            }

            NotifyRenameStateChanged();
        }
    }

    public string RenameSequenceSeparator
    {
        get => _renameSequenceSeparator;
        set
        {
            if (!SetProperty(ref _renameSequenceSeparator, value))
            {
                return;
            }

            NotifyRenameStateChanged();
            ScheduleViewerSettingsSave();
        }
    }

    public string RenameStartNumberText
    {
        get => _renameStartNumberText;
        set
        {
            if (!SetProperty(ref _renameStartNumberText, value))
            {
                return;
            }

            NotifyRenameStateChanged();
            ScheduleViewerSettingsSave();
        }
    }

    public string RenameNumberDigitsText
    {
        get => _renameNumberDigitsText;
        set
        {
            if (!SetProperty(ref _renameNumberDigitsText, value))
            {
                return;
            }

            NotifyRenameStateChanged();
            ScheduleViewerSettingsSave();
        }
    }

    public bool CanRunRename => GetOperationTargetCount() > 0
        && !IsExportProcessing
        && !string.IsNullOrWhiteSpace(NormalizeRenameBaseName(RenameBaseName));

    public string RenameActionText => ProcessCurrentCollection
        ? "批量重命名"
        : HasBatchSelection ? "重命名所选" : "重命名当前图";

    public string RenamePreviewText
    {
        get
        {
            var pattern = BuildRenamePatternOrNull(showValidationMessage: false);
            if (pattern is null)
            {
                return "请先填写有效的基础名称、起始编号和编号位数。";
            }

            var previewTarget = ResolveOperationTargets().FirstOrDefault();
            var extension = Path.GetExtension(previewTarget?.FileName ?? SelectedImage?.FileName ?? Images.FirstOrDefault()?.FileName ?? ".jpg");
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".jpg";
            }

            return BuildRenamePreviewText(pattern, extension, GetOperationTargetCount());
        }
    }

    private void InitializeRenameSettings()
    {
        _lastSuggestedRenameBaseName = BuildSuggestedRenameBaseName();
        _renameBaseName = _lastSuggestedRenameBaseName;
    }

    private void OnRenameSelectionChanged(ImageListItemViewModel? _)
    {
        UpdateSuggestedRenameBaseName();
        NotifyRenameStateChanged();
    }

    private void OnRenameTargetCollectionChanged()
    {
        UpdateSuggestedRenameBaseName();
        NotifyRenameStateChanged();
    }

    public async Task RenameSelectedAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRunRename)
        {
            return;
        }

        using var trackedOperation = BeginTrackedOperation();
        var renamePattern = BuildRenamePatternOrNull(showValidationMessage: true);
        if (renamePattern is null)
        {
            return;
        }

        var renameTargets = ResolveOperationTargets()
            .DistinctBy(static item => item.FullPath, PathComparison.Comparer)
            .ToArray();
        if (renameTargets.Length == 0)
        {
            return;
        }

        var renamePlan = BuildRenamePlan(renameTargets, renamePattern, out var previewBaseName, out var validationMessage);
        if (renamePlan is null)
        {
            OperationStatusText = validationMessage ?? "重命名计划无效。";
            return;
        }

        var focusSourcePath = SelectedImage?.FullPath ?? renameTargets[0].FullPath;
        var operationWorkCount = Math.Max(1, renamePlan.TotalMoveCount);

        IsExportProcessing = true;
        BeginOperationProgress(
            operationWorkCount,
            renameTargets.Length == 1
                ? $"正在重命名 {Path.GetFileName(renameTargets[0].FullPath)}..."
                : $"正在批量重命名 {renameTargets.Length} 张图片...",
            $"准备重命名 {renameTargets.Length} 张图片。");

        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _currentOperationCts = operationCts;
        OnPropertyChanged(nameof(CanCancelOperation));
        using var trace = BeginDiagnosticsOperation(
            "batch",
            "rename",
            ("targetCount", renameTargets.Length.ToString()),
            ("changedItemCount", renamePlan.ChangedItemCount.ToString()),
            ("totalMoveCount", renamePlan.TotalMoveCount.ToString()),
            ("previewBaseName", previewBaseName));

        try
        {
            var result = await DesktopRenameService.ExecuteAsync(
                renamePlan,
                operationCts.Token,
                progress =>
                {
                    var statusText = progress.Stage == DesktopRenameStage.Temporary
                        ? $"正在准备新文件名 {Path.GetFileName(progress.Item.SourcePath)}..."
                        : $"正在应用新文件名 {Path.GetFileName(progress.Item.TargetPath)}...";
                    UpdateOperationProgress(
                        progress.CompletedMoveCount,
                        progress.TotalMoveCount,
                        statusText,
                        force: progress.CompletedMoveCount == progress.TotalMoveCount || progress.CompletedMoveCount == 1);
                });

            if (renamePlan.TotalMoveCount == 0)
            {
                UpdateOperationProgress(1, 1, force: true);
            }

            var completedRenameMap = result.CompletedRenameMap;
            if (completedRenameMap.Count > 0)
            {
                UpdateLastInputsAfterRename(completedRenameMap);
                ReplacePathsInReviewStates(completedRenameMap);
                var focusPath = completedRenameMap.TryGetValue(focusSourcePath, out var renamedFocusPath)
                    ? renamedFocusPath
                    : completedRenameMap.Values.FirstOrDefault() ?? focusSourcePath;
                if (!ReplacePathsInCurrentCollection(completedRenameMap, focusPath))
                {
                    await LoadInputsAsync(_lastInputs, operationCts.Token, focusPath);
                }
            }

            if (result.WasCanceled)
            {
                OperationStatusText = string.IsNullOrWhiteSpace(result.RollbackMessage)
                    ? "本次重命名已取消。"
                    : $"本次重命名已取消，已尝试回滚：{result.RollbackMessage}";
                trace.Canceled(CreateDiagnosticsProperties(("rollbackMessage", result.RollbackMessage)));
                WriteBatchDiagnosticsReport(
                    "rename",
                    renamePlan.TotalMoveCount,
                    result.CompletedMoveCount,
                    0,
                    wasCanceled: true,
                    result.Failure is null
                        ? []
                        : [new DesktopBatchItemFailure(result.CompletedMoveCount, Path.GetFileName(result.Failure.Item.SourcePath), result.Failure.Message)],
                    ("targetCount", renameTargets.Length.ToString()),
                    ("previewBaseName", previewBaseName));
                return;
            }

            if (result.Failure is not null)
            {
                var failureMessage = $"{Path.GetFileName(result.Failure.Item.SourcePath)}：{result.Failure.Message}";
                OperationStatusText = string.IsNullOrWhiteSpace(result.RollbackMessage)
                    ? $"重命名失败：{failureMessage}"
                    : $"重命名失败，已尝试回滚：{failureMessage}；回滚结果：{result.RollbackMessage}";
                trace.Fail(
                    new InvalidOperationException(result.Failure.Message),
                    CreateDiagnosticsProperties(
                        ("failedStage", result.Failure.Stage.ToString()),
                        ("failedPath", result.Failure.Item.SourcePath),
                        ("rollbackMessage", result.RollbackMessage)));
                WriteBatchDiagnosticsReport(
                    "rename",
                    renamePlan.TotalMoveCount,
                    result.CompletedMoveCount,
                    0,
                    wasCanceled: false,
                    [new DesktopBatchItemFailure(result.CompletedMoveCount, Path.GetFileName(result.Failure.Item.SourcePath), result.Failure.Message)],
                    ("targetCount", renameTargets.Length.ToString()),
                    ("previewBaseName", previewBaseName),
                    ("rollbackMessage", result.RollbackMessage));
                return;
            }

            OperationStatusText = renameTargets.Length == 1
                ? $"已重命名为 {previewBaseName}"
                : $"已将 {renameTargets.Length} 张图片重命名为 {previewBaseName} 起。";
            trace.Success(CreateDiagnosticsProperties(("renamedCount", completedRenameMap.Count.ToString())));
            WriteBatchDiagnosticsReport(
                "rename",
                renamePlan.TotalMoveCount,
                result.CompletedMoveCount,
                completedRenameMap.Count,
                wasCanceled: false,
                [],
                ("targetCount", renameTargets.Length.ToString()),
                ("previewBaseName", previewBaseName));
        }
        finally
        {
            if (ReferenceEquals(_currentOperationCts, operationCts))
            {
                _currentOperationCts = null;
                OnPropertyChanged(nameof(CanCancelOperation));
            }

            IsExportProcessing = false;
        }
    }

    private DesktopRenamePlan? BuildRenamePlan(
        IReadOnlyList<ImageListItemViewModel> targets,
        SequentialRenamePattern renamePattern,
        out string previewBaseName,
        out string? validationMessage)
    {
        return DesktopRenameService.TryBuildPlan(
            targets.Select(static item => item.FullPath).ToArray(),
            renamePattern,
            out previewBaseName,
            out validationMessage);
    }

    private SequentialRenamePattern? BuildRenamePatternOrNull(bool showValidationMessage)
    {
        var normalizedBaseName = NormalizeRenameBaseName(RenameBaseName);
        if (string.IsNullOrWhiteSpace(normalizedBaseName))
        {
            if (showValidationMessage)
            {
                OperationStatusText = "请先填写基础名称。";
            }

            return null;
        }

        if (normalizedBaseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            if (showValidationMessage)
            {
                OperationStatusText = "基础名称包含无效字符，请换一个名称。";
            }

            return null;
        }

        var separator = RenameSequenceSeparator ?? string.Empty;
        if (separator.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            if (showValidationMessage)
            {
                OperationStatusText = "连接符包含无效字符，请换一个符号。";
            }

            return null;
        }

        if (!int.TryParse(RenameStartNumberText.Trim(), out var startNumber)
            || startNumber < 0
            || startNumber > MaximumRenameStartNumber)
        {
            if (showValidationMessage)
            {
                OperationStatusText = $"起始编号请填 0 到 {MaximumRenameStartNumber} 之间的整数。";
            }

            return null;
        }

        if (!int.TryParse(RenameNumberDigitsText.Trim(), out var numberDigits)
            || numberDigits < MinimumRenameNumberDigits
            || numberDigits > MaximumRenameNumberDigits)
        {
            if (showValidationMessage)
            {
                OperationStatusText = $"编号位数请填 {MinimumRenameNumberDigits} 到 {MaximumRenameNumberDigits} 之间的整数。";
            }

            return null;
        }

        return new SequentialRenamePattern(normalizedBaseName, separator, startNumber, numberDigits);
    }

    private void NotifyRenameStateChanged()
    {
        OnPropertyChanged(nameof(CanRunRename));
        OnPropertyChanged(nameof(RenameActionText));
        OnPropertyChanged(nameof(RenamePreviewText));
    }

    private void UpdateLastInputsAfterRename(IReadOnlyDictionary<string, string> completedRenameMap)
    {
        if (completedRenameMap.Count == 0 || _lastInputs.Count == 0)
        {
            return;
        }

        var updatedInputs = new List<string>(_lastInputs.Count);
        var hasChanges = false;

        foreach (var inputPath in _lastInputs)
        {
            if (completedRenameMap.TryGetValue(inputPath, out var renamedPath))
            {
                updatedInputs.Add(renamedPath);
                hasChanges = true;
            }
            else
            {
                updatedInputs.Add(inputPath);
            }
        }

        if (hasChanges)
        {
            _lastInputs = updatedInputs
                .Distinct(PathComparison.Comparer)
                .ToArray();
        }
    }

    private void UpdateSuggestedRenameBaseName()
    {
        var suggestedBaseName = BuildSuggestedRenameBaseName();
        if (string.IsNullOrWhiteSpace(suggestedBaseName))
        {
            suggestedBaseName = "图片";
        }

        if (string.IsNullOrWhiteSpace(_renameBaseName)
            || string.Equals(_renameBaseName, _lastSuggestedRenameBaseName, StringComparison.Ordinal))
        {
            _renameBaseName = suggestedBaseName;
            OnPropertyChanged(nameof(RenameBaseName));
        }

        _lastSuggestedRenameBaseName = suggestedBaseName;
    }

    private string BuildSuggestedRenameBaseName()
    {
        if (ProcessCurrentCollection && Images.Count > 1)
        {
            return "图片";
        }

        var fileName = SelectedImage?.FileName ?? Images.FirstOrDefault()?.FileName ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        return string.IsNullOrWhiteSpace(baseName) ? "图片" : baseName;
    }

    private static string NormalizeRenameBaseName(string renameBaseName)
    {
        var trimmedBaseName = renameBaseName.Trim();
        if (string.IsNullOrWhiteSpace(trimmedBaseName))
        {
            return string.Empty;
        }

        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(trimmedBaseName);
        return string.IsNullOrWhiteSpace(fileNameWithoutExtension)
            ? trimmedBaseName
            : fileNameWithoutExtension;
    }

    private static string BuildRenamePreviewText(SequentialRenamePattern renamePattern, string extension, int targetCount)
    {
        if (targetCount <= 0)
        {
            return "请先选择要处理的图片。";
        }

        var previewCount = Math.Min(targetCount, 3);
        var previewItems = Enumerable
            .Range(0, previewCount)
            .Select(index => $"{renamePattern.BuildFileBaseName(index, targetCount)}{extension}");
        var previewText = string.Join(Environment.NewLine, previewItems);
        return targetCount > previewCount
            ? $"{previewText}{Environment.NewLine}……共 {targetCount} 项"
            : previewText;
    }
}




