using System.Collections.Concurrent;
using ModernImageViewer.Core;
using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    public bool CanOpenContainingFolder => SelectedImage is not null && !IsExportProcessing;

    public bool CanCopyOperationPaths => GetOperationTargetCount() > 0 && !IsExportProcessing;

    public bool CanCopyToFolder => GetOperationTargetCount() > 0 && !IsExportProcessing;

    public bool CanMoveToFolder => GetOperationTargetCount() > 0 && !IsExportProcessing;

    public bool CanRecycleToTrash => GetOperationTargetCount() > 0 && !IsExportProcessing;

    public string CopyToFolderActionText => ProcessCurrentCollection
        ? "复制当前列表到..."
        : HasBatchSelection ? "复制所选到..." : "复制当前图到...";

    public string MoveToFolderActionText => ProcessCurrentCollection
        ? "移动当前列表到..."
        : HasBatchSelection ? "移动所选到..." : "移动当前图到...";

    public string RecycleToTrashActionText => ProcessCurrentCollection
        ? "移到回收站"
        : HasBatchSelection ? "移所选到回收站" : "移当前图到回收站";

    public bool TryBuildOperationPathClipboardText(out string text, out string statusMessage)
    {
        var paths = ResolveOperationTargets()
            .Select(static item => item.FullPath)
            .Distinct(PathComparison.Comparer)
            .ToArray();
        if (paths.Length == 0)
        {
            text = string.Empty;
            statusMessage = "当前没有可复制的图片路径。";
            return false;
        }

        text = string.Join(Environment.NewLine, paths);
        statusMessage = paths.Length == 1
            ? $"已复制 {Path.GetFileName(paths[0])} 的路径。"
            : $"已复制 {paths.Length} 条图片路径。";
        return true;
    }

    public async Task CopyToFolderAsync(string destinationFolder, CancellationToken cancellationToken = default)
    {
        await TransferToFolderAsync(destinationFolder, moveFiles: false, cancellationToken);
    }

    public async Task MoveToFolderAsync(string destinationFolder, CancellationToken cancellationToken = default)
    {
        await TransferToFolderAsync(destinationFolder, moveFiles: true, cancellationToken);
    }

    public async Task RecycleSelectedToTrashAsync(CancellationToken cancellationToken = default)
    {
        using var trackedOperation = BeginTrackedOperation();
        var targets = ResolveOperationTargets()
            .DistinctBy(static item => item.FullPath, PathComparison.Comparer)
            .ToArray();
        if (targets.Length == 0)
        {
            OperationStatusText = "当前没有可移到回收站的图片。";
            return;
        }

        var targetPaths = targets.Select(static item => item.FullPath).ToArray();
        var inputSnapshot = _lastInputs.ToArray();
        var completedPaths = new ConcurrentDictionary<string, byte>(PathComparison.Comparer);
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var trace = BeginDiagnosticsOperation(
            "batch",
            "recycle-to-trash",
            ("targetCount", targetPaths.Length.ToString()));

        try
        {
            IsExportProcessing = true;
            ClearBatchFailures();
            BeginOperationProgress(
                targetPaths.Length,
                targetPaths.Length == 1
                    ? $"正在移到回收站：{Path.GetFileName(targetPaths[0])}"
                    : $"正在移到回收站：{targetPaths.Length} 张图片",
                $"准备移到回收站 {targetPaths.Length} 张图片。");

            _currentOperationCts = operationCts;
            OnPropertyChanged(nameof(CanCancelOperation));

            var batchItems = targetPaths
                .Select(static path => new DesktopBatchItem<string>(path, Path.GetFileName(path)))
                .ToArray();
            var executionPlan = CreateRecycleExecutionPlan(targets);

            IProgress<DesktopBatchProgress> progress = new Progress<DesktopBatchProgress>(update =>
            {
                UpdateOperationProgress(
                    update.ProcessedCount,
                    targetPaths.Length,
                    targetPaths.Length == 1
                        ? $"正在移到回收站：{update.DisplayName}"
                        : $"正在移到回收站 {update.ProcessedCount}/{targetPaths.Length}：{update.DisplayName}",
                    force: update.ProcessedCount == targetPaths.Length);
            });

            var result = await _desktopBatchProcessor.RunAsync(
                batchItems,
                async (item, _, token) =>
                {
                    await RunBatchSynchronousWorkAsync(
                        () => _desktopTrashService.MoveFileToTrash(item.Value),
                        executionPlan,
                        token);
                    completedPaths[item.Value] = 0;
                },
                progress,
                executionPlan: executionPlan,
                cancellationToken: operationCts.Token);

            foreach (var failure in result.Failures)
            {
                AddBatchFailure(failure.Index, failure.DisplayName, failure.ErrorMessage);
            }
            WriteBatchDiagnosticsReport(
                "recycle-to-trash",
                result.TotalCount,
                result.ProcessedCount,
                result.SuccessCount,
                result.WasCanceled,
                result.Failures,
                ("targetCount", targetPaths.Length.ToString()));

            if (completedPaths.Count > 0)
            {
                var removedPathSet = completedPaths.Keys.ToHashSet(PathComparison.Comparer);
                var focusPath = FindNextAvailableFocusPath(removedPathSet);
                RemoveRecycledFileInputs(removedPathSet);

                if (CanRefreshCollectionInPlaceFromExplicitFileInputs(inputSnapshot))
                {
                    RemovePathsFromCurrentCollection(removedPathSet, focusPath);
                }
                else
                {
                    var refreshToken = result.WasCanceled ? CancellationToken.None : operationCts.Token;
                    await LoadInputsAsync(_lastInputs, refreshToken, focusPath);
                }
            }

            if (result.WasCanceled)
            {
                OperationStatusText = completedPaths.Count == 0
                    ? "移到回收站已取消。"
                    : $"移到回收站已取消：已完成 {completedPaths.Count}/{targetPaths.Length}。";
                trace.Canceled(CreateDiagnosticsProperties(("completedCount", completedPaths.Count.ToString())));
                return;
            }

            if (result.Failures.Count == 0)
            {
                var completedPath = completedPaths.Keys.FirstOrDefault();
                OperationStatusText = completedPaths.Count == 1
                    ? $"已移到回收站：{Path.GetFileName(completedPath ?? targetPaths[0])}"
                    : $"已移到回收站：{completedPaths.Count} 张图片";
                trace.Success(CreateDiagnosticsProperties(("completedCount", completedPaths.Count.ToString())));
                return;
            }

            var firstFailure = result.Failures[0];
            OperationStatusText = completedPaths.Count == 0
                ? $"移到回收站失败：{firstFailure.DisplayName}：{firstFailure.ErrorMessage}"
                : $"移到回收站完成：成功 {completedPaths.Count}，失败 {result.Failures.Count}。首个失败：{firstFailure.DisplayName}：{firstFailure.ErrorMessage}";
            trace.Fail(
                new InvalidOperationException(firstFailure.ErrorMessage),
                CreateDiagnosticsProperties(
                    ("completedCount", completedPaths.Count.ToString()),
                    ("failureCount", result.Failures.Count.ToString())));
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

    public void OpenContainingFolder()
    {
        var path = SelectedImage?.FullPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            OperationStatusText = "请先选中一张图片，再打开所在文件夹。";
            return;
        }

        try
        {
            _desktopShellService.RevealInFileManager(path);
            OperationStatusText = $"已打开 {Path.GetFileName(path)} 所在文件夹。";
        }
        catch (Exception ex)
        {
            OperationStatusText = $"打开所在文件夹失败：{ex.Message}";
        }
    }

    private async Task TransferToFolderAsync(string destinationFolder, bool moveFiles, CancellationToken cancellationToken)
    {
        using var trackedOperation = BeginTrackedOperation();
        var targets = ResolveOperationTargets()
            .DistinctBy(static item => item.FullPath, PathComparison.Comparer)
            .ToArray();
        if (targets.Length == 0)
        {
            OperationStatusText = moveFiles ? "当前没有可移动的图片。" : "当前没有可复制的图片。";
            return;
        }

        if (string.IsNullOrWhiteSpace(destinationFolder))
        {
            OperationStatusText = moveFiles ? "已取消移动，因为没有选择目标文件夹。" : "已取消复制，因为没有选择目标文件夹。";
            return;
        }

        string normalizedDestinationFolder;
        try
        {
            normalizedDestinationFolder = Path.GetFullPath(destinationFolder);
            Directory.CreateDirectory(normalizedDestinationFolder);
        }
        catch (Exception ex)
        {
            OperationStatusText = $"无法准备目标文件夹：{ex.Message}";
            return;
        }

        ClearBatchFailures();

        var transferPlan = new List<(int Index, string SourcePath, string TargetPath)>(targets.Length);
        var reservedTargetPaths = new HashSet<string>(PathComparison.Comparer);

        for (var index = 0; index < targets.Length; index++)
        {
            var sourcePath = targets[index].FullPath;
            if (moveFiles)
            {
                var targetPath = Path.Combine(normalizedDestinationFolder, Path.GetFileName(sourcePath));
                if (string.Equals(sourcePath, targetPath, PathComparison.Comparison))
                {
                    continue;
                }

                if (!reservedTargetPaths.Add(targetPath))
                {
                    OperationStatusText = $"已取消移动，因为 {Path.GetFileName(targetPath)} 会出现重复。";
                    return;
                }

                if (File.Exists(targetPath))
                {
                    OperationStatusText = $"已取消移动，因为 {Path.GetFileName(targetPath)} 已存在。";
                    return;
                }

                transferPlan.Add((index, sourcePath, targetPath));
                continue;
            }

            var copyTargetPath = ExportPathBuilder.BuildAvailableTargetPath(
                sourcePath,
                normalizedDestinationFolder,
                Path.GetExtension(sourcePath),
                "复制",
                null,
                reservedTargetPaths);
            transferPlan.Add((index, sourcePath, copyTargetPath));
        }

        if (transferPlan.Count == 0)
        {
            OperationStatusText = moveFiles
                ? "已取消移动，所选图片已经在该文件夹中。"
                : "没有可复制的图片。";
            return;
        }

        var actionLabel = moveFiles ? "移动" : "复制";
        var completedTransferMap = new ConcurrentDictionary<string, string>(PathComparison.Comparer);
        var failureMessages = new List<string>();
        var focusSourcePath = SelectedImage?.FullPath ?? transferPlan[0].SourcePath;

        IsExportProcessing = true;
        BeginOperationProgress(
            transferPlan.Count,
            transferPlan.Count == 1
                ? $"正在{actionLabel} {Path.GetFileName(transferPlan[0].SourcePath)}..."
                : $"正在批量{actionLabel} {transferPlan.Count} 张图片...",
            $"准备{actionLabel} {transferPlan.Count} 张图片。");

        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _currentOperationCts = operationCts;
        OnPropertyChanged(nameof(CanCancelOperation));
        using var trace = BeginDiagnosticsOperation(
            "batch",
            moveFiles ? "move-to-folder" : "copy-to-folder",
            ("targetCount", transferPlan.Count.ToString()),
            ("destinationFolder", normalizedDestinationFolder));

        try
        {
            var batchItems = transferPlan
                .Select(static step => new DesktopBatchItem<(int Index, string SourcePath, string TargetPath)>(step, Path.GetFileName(step.SourcePath)))
                .ToArray();
            var executionPlan = CreateTransferExecutionPlan(moveFiles, targets, transferPlan);

            IProgress<DesktopBatchProgress> progress = new Progress<DesktopBatchProgress>(update =>
            {
                UpdateOperationProgress(
                    update.ProcessedCount,
                    transferPlan.Count,
                    transferPlan.Count == 1
                        ? $"正在{actionLabel} {update.DisplayName}..."
                        : $"正在{actionLabel} {update.ProcessedCount}/{transferPlan.Count}：{update.DisplayName}",
                    force: update.ProcessedCount == transferPlan.Count);
            });

            var result = await _desktopBatchProcessor.RunAsync(
                batchItems,
                async (item, _, token) =>
                {
                    var step = item.Value;
                    await RunBatchSynchronousWorkAsync(
                        () =>
                        {
                            if (moveFiles)
                            {
                                DesktopFileStreamFactory.MoveFile(step.SourcePath, step.TargetPath);
                            }
                            else
                            {
                                DesktopFileStreamFactory.CopyFile(step.SourcePath, step.TargetPath);
                            }
                        },
                        executionPlan,
                        token);

                    completedTransferMap[step.SourcePath] = step.TargetPath;
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
                moveFiles ? "move-to-folder" : "copy-to-folder",
                result.TotalCount,
                result.ProcessedCount,
                result.SuccessCount,
                result.WasCanceled,
                result.Failures,
                ("destinationFolder", normalizedDestinationFolder),
                ("targetCount", transferPlan.Count.ToString()));

            if (moveFiles && completedTransferMap.Count > 0)
            {
                UpdateLastInputsAfterTransfer(completedTransferMap);
                ReplacePathsInReviewStates(completedTransferMap);
                var focusPath = completedTransferMap.TryGetValue(focusSourcePath, out var movedFocusPath)
                    ? movedFocusPath
                    : FindNextAvailableFocusPath(completedTransferMap.Keys) ?? Images.FirstOrDefault()?.FullPath;
                if (!ReplacePathsInCurrentCollection(completedTransferMap, focusPath))
                {
                    var refreshToken = result.WasCanceled ? CancellationToken.None : operationCts.Token;
                    await LoadInputsAsync(_lastInputs, refreshToken, focusPath);
                }
            }
            else if (!moveFiles
                && completedTransferMap.Count > 0
                && !result.WasCanceled
                && ShouldReloadAfterCopyTransfer(_lastInputs, completedTransferMap.Values, IncludeSubfolders))
            {
                await LoadInputsAsync(_lastInputs, operationCts.Token, focusSourcePath);
            }

            if (result.WasCanceled)
            {
                OperationStatusText = completedTransferMap.Count == 0
                    ? $"本次{actionLabel}已取消。"
                    : $"本次{actionLabel}已取消：已完成 {completedTransferMap.Count}/{transferPlan.Count}。";
                trace.Canceled(CreateDiagnosticsProperties(("completedCount", completedTransferMap.Count.ToString())));
                return;
            }

            if (failureMessages.Count == 0)
            {
                var completedSourceName = completedTransferMap.Keys
                    .Select(Path.GetFileName)
                    .FirstOrDefault();
                OperationStatusText = completedTransferMap.Count == 1
                    ? $"已将 {completedSourceName ?? Path.GetFileName(transferPlan[0].SourcePath)} {(moveFiles ? "移动到" : "复制到")} {normalizedDestinationFolder}"
                    : $"已将 {completedTransferMap.Count} 张图片{(moveFiles ? "移动到" : "复制到")} {normalizedDestinationFolder}";
                trace.Success(CreateDiagnosticsProperties(("completedCount", completedTransferMap.Count.ToString())));
                return;
            }

            if (completedTransferMap.Count == 0)
            {
                OperationStatusText = $"{actionLabel}失败：{failureMessages[0]}";
                trace.Fail(
                    new InvalidOperationException(failureMessages[0]),
                    CreateDiagnosticsProperties(("failureCount", failureMessages.Count.ToString())));
                return;
            }

            OperationStatusText = $"{actionLabel}完成：成功 {completedTransferMap.Count}，失败 {failureMessages.Count}。首个失败：{failureMessages[0]}";
            trace.Fail(
                new InvalidOperationException(failureMessages[0]),
                CreateDiagnosticsProperties(
                    ("completedCount", completedTransferMap.Count.ToString()),
                    ("failureCount", failureMessages.Count.ToString())));
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

    private static DesktopBatchExecutionPlan CreateTransferExecutionPlan(
        bool moveFiles,
        IReadOnlyList<ImageListItemViewModel> targets,
        IReadOnlyList<(int Index, string SourcePath, string TargetPath)> transferPlan)
    {
        var sizeByPath = targets.ToDictionary(
            static item => item.FullPath,
            static item => item.SizeBytes,
            PathComparison.Comparer);
        var totalBytes = SumOperationBytes(transferPlan.Select(step =>
            sizeByPath.TryGetValue(step.SourcePath, out var sizeBytes)
                ? sizeBytes
                : 0L));

        return DesktopBatchParallelismAdvisor.CreateAdaptiveExecutionPlan(
            moveFiles ? DesktopBatchWorkloadKind.FileMove : DesktopBatchWorkloadKind.FileCopy,
            transferPlan.Count,
            totalBytes);
    }

    private static DesktopBatchExecutionPlan CreateRecycleExecutionPlan(
        IReadOnlyList<ImageListItemViewModel> targets)
    {
        return DesktopBatchParallelismAdvisor.CreateAdaptiveExecutionPlan(
            DesktopBatchWorkloadKind.RecycleToTrash,
            targets.Count,
            SumOperationBytes(targets));
    }

    internal static bool ShouldReloadAfterCopyTransfer(
        IReadOnlyList<string> inputPaths,
        IEnumerable<string> copiedTargetPaths,
        bool includeSubfolders)
    {
        ArgumentNullException.ThrowIfNull(inputPaths);
        ArgumentNullException.ThrowIfNull(copiedTargetPaths);

        var directoryInputs = inputPaths
            .Where(static path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Select(Path.GetFullPath)
            .Distinct(PathComparison.Comparer)
            .ToArray();
        if (directoryInputs.Length == 0)
        {
            return false;
        }

        foreach (var copiedTargetPath in copiedTargetPaths)
        {
            if (string.IsNullOrWhiteSpace(copiedTargetPath))
            {
                continue;
            }

            string targetDirectory;
            try
            {
                targetDirectory = Path.GetDirectoryName(Path.GetFullPath(copiedTargetPath)) ?? string.Empty;
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                continue;
            }

            foreach (var inputDirectory in directoryInputs)
            {
                if (string.Equals(targetDirectory, inputDirectory, PathComparison.Comparison))
                {
                    return true;
                }

                if (includeSubfolders && IsPathNestedUnderRoot(targetDirectory, inputDirectory))
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static bool IsPathNestedUnderRoot(string path, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(rootPath))
        {
            return false;
        }

        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        if (normalizedPath.Length <= normalizedRoot.Length)
        {
            return false;
        }

        if (!normalizedPath.StartsWith(normalizedRoot, PathComparison.Comparison))
        {
            return false;
        }

        return normalizedPath[normalizedRoot.Length] == Path.DirectorySeparatorChar
            || normalizedPath[normalizedRoot.Length] == Path.AltDirectorySeparatorChar;
    }

    internal static bool CanRefreshCollectionInPlaceAfterRemovingPaths(IReadOnlyList<string> inputPaths)
        => CanRefreshCollectionInPlaceFromExplicitFileInputs(inputPaths);

    internal static bool CanRefreshCollectionInPlaceFromExplicitFileInputs(IReadOnlyList<string> inputPaths)
    {
        ArgumentNullException.ThrowIfNull(inputPaths);

        foreach (var inputPath in inputPaths)
        {
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                continue;
            }

            try
            {
                if (Directory.Exists(Path.GetFullPath(inputPath)))
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsPathVisibleForCurrentInputs(
        IReadOnlyList<string> inputPaths,
        string path,
        bool includeSubfolders)
    {
        ArgumentNullException.ThrowIfNull(inputPaths);

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(path);
        }
        catch
        {
            return false;
        }

        foreach (var inputPath in inputPaths)
        {
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                continue;
            }

            string normalizedInputPath;
            try
            {
                normalizedInputPath = Path.GetFullPath(inputPath);
            }
            catch
            {
                continue;
            }

            if (Directory.Exists(normalizedInputPath))
            {
                var targetDirectory = Path.GetDirectoryName(normalizedPath);
                if (string.IsNullOrWhiteSpace(targetDirectory))
                {
                    continue;
                }

                if (string.Equals(targetDirectory, normalizedInputPath, PathComparison.Comparison))
                {
                    return true;
                }

                if (includeSubfolders && IsPathNestedUnderRoot(targetDirectory, normalizedInputPath))
                {
                    return true;
                }

                continue;
            }

            if (string.Equals(normalizedPath, normalizedInputPath, PathComparison.Comparison))
            {
                return true;
            }
        }

        return false;
    }

    internal static ImageListItemViewModel[] SortImageItemsForMode(IEnumerable<ImageListItemViewModel> items, SortMode sortMode)
    {
        return SortImageItemsForMode(items, sortMode, PathComparison.NameComparer);
    }

    internal static ImageListItemViewModel[] SortImageItemsForMode(
        IEnumerable<ImageListItemViewModel> items,
        SortMode sortMode,
        StringComparer fileNameComparer)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(fileNameComparer);

        return sortMode switch
        {
            SortMode.Modified => items
                .OrderByDescending(static item => item.ModifiedAt)
                .ThenBy(static item => item.FileName, fileNameComparer)
                .ThenBy(static item => item.FileName, StringComparer.Ordinal)
                .ToArray(),
            SortMode.Size => items
                .OrderByDescending(static item => item.SizeBytes)
                .ThenBy(static item => item.FileName, fileNameComparer)
                .ThenBy(static item => item.FileName, StringComparer.Ordinal)
                .ToArray(),
            _ => items
                .OrderBy(static item => item.FileName, fileNameComparer)
                .ThenBy(static item => item.FileName, StringComparer.Ordinal)
                .ToArray()
        };
    }

    private void UpdateLastInputsAfterTransfer(IReadOnlyDictionary<string, string> completedTransferMap)
    {
        if (completedTransferMap.Count == 0 || _lastInputs.Count == 0)
        {
            return;
        }

        var updatedInputs = new List<string>(_lastInputs.Count);
        var hasChanges = false;

        foreach (var inputPath in _lastInputs)
        {
            if (completedTransferMap.TryGetValue(inputPath, out var movedPath))
            {
                updatedInputs.Add(movedPath);
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

    private void RemoveRecycledFileInputs(IReadOnlySet<string> removedPaths)
    {
        if (removedPaths.Count == 0 || _lastInputs.Count == 0)
        {
            return;
        }

        _lastInputs = _lastInputs
            .Where(path => !removedPaths.Contains(Path.GetFullPath(path)))
            .Distinct(PathComparison.Comparer)
            .ToArray();
    }

    private void RemovePathsFromCurrentCollection(IReadOnlySet<string> removedPaths, string? preferredFocusPath)
    {
        if (removedPaths.Count == 0 || _allImages.Count == 0)
        {
            return;
        }

        var retainedItems = _allImages
            .Where(item => !removedPaths.Contains(item.FullPath))
            .ToArray();
        if (retainedItems.Length == _allImages.Count)
        {
            return;
        }

        foreach (var removedItem in _allImages.Where(item => removedPaths.Contains(item.FullPath)).ToArray())
        {
            removedItem.Dispose();
        }

        _allImages.Clear();
        _allImages.AddRange(retainedItems);
        RefreshFormatFilterOptions();
        ApplyFilters(preferredFocusPath);
    }

    private bool ReplacePathsInCurrentCollection(
        IReadOnlyDictionary<string, string> replacedPathMap,
        string? preferredFocusPath)
    {
        if (replacedPathMap.Count == 0 || _allImages.Count == 0)
        {
            return false;
        }

        var updatedSelectionPaths = SelectedImages
            .Select(item => replacedPathMap.TryGetValue(item.FullPath, out var updatedPath) ? updatedPath : item.FullPath)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(PathComparison.Comparer)
            .ToArray();
        var updatedItems = new List<ImageListItemViewModel>(_allImages.Count);
        var replacedItems = new List<ImageListItemViewModel>();
        var createdItems = new List<ImageListItemViewModel>();
        var hasChanges = false;

        try
        {
            foreach (var item in _allImages)
            {
                if (replacedPathMap.TryGetValue(item.FullPath, out var updatedPath))
                {
                    if (IsPathVisibleForCurrentInputs(_lastInputs, updatedPath, IncludeSubfolders))
                    {
                        var updatedItem = CreateUpdatedImageListItem(item, updatedPath);
                        updatedItems.Add(updatedItem);
                        createdItems.Add(updatedItem);
                    }

                    replacedItems.Add(item);
                    hasChanges = true;
                }
                else
                {
                    updatedItems.Add(item);
                }
            }
        }
        catch
        {
            foreach (var createdItem in createdItems)
            {
                createdItem.Dispose();
            }

            return false;
        }

        if (!hasChanges)
        {
            foreach (var createdItem in createdItems)
            {
                createdItem.Dispose();
            }

            return false;
        }

        ApplyReviewStates(createdItems);

        _allImages.Clear();
        _allImages.AddRange(SortImageItemsForMode(updatedItems, SelectedSortMode.Value));
        RefreshFormatFilterOptions();
        ApplyFilters(preferredFocusPath);

        if (updatedSelectionPaths.Length > 0)
        {
            SelectImagesByPath(updatedSelectionPaths, preferredFocusPath);
        }

        foreach (var replacedItem in replacedItems)
        {
            replacedItem.Dispose();
        }

        return true;
    }

    private static ImageListItemViewModel CreateUpdatedImageListItem(ImageListItemViewModel item, string updatedPath)
    {
        var normalizedPath = Path.GetFullPath(updatedPath);
        var fileInfo = new FileInfo(normalizedPath);
        var sizeBytes = item.SizeBytes;
        var modifiedAt = item.ModifiedAt;

        if (fileInfo.Exists)
        {
            sizeBytes = fileInfo.Length;
            modifiedAt = fileInfo.LastWriteTime;
        }

        return new ImageListItemViewModel(new ImageRecord(
            normalizedPath,
            Path.GetFileName(normalizedPath),
            sizeBytes,
            modifiedAt));
    }

    private string? FindNextAvailableFocusPath(IEnumerable<string> removedPaths)
    {
        var removedPathSet = removedPaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(PathComparison.Comparer);

        return Images
            .Select(static item => item.FullPath)
            .FirstOrDefault(path => !removedPathSet.Contains(path));
    }

    private void NotifyFileOperationStateChanged()
    {
        OnPropertyChanged(nameof(CanOpenContainingFolder));
        OnPropertyChanged(nameof(CanCopyOperationPaths));
        OnPropertyChanged(nameof(CanCopyToFolder));
        OnPropertyChanged(nameof(CanMoveToFolder));
        OnPropertyChanged(nameof(CanRecycleToTrash));
        OnPropertyChanged(nameof(CopyToFolderActionText));
        OnPropertyChanged(nameof(MoveToFolderActionText));
        OnPropertyChanged(nameof(RecycleToTrashActionText));
    }
}




