using ModernImageViewer.Core;
using ModernImageViewer.Desktop.ViewModels;

namespace ModernImageViewer.Desktop.Services;

internal enum DesktopRenameStage
{
    Temporary,
    Final
}

internal sealed record DesktopRenameItem(
    string SourcePath,
    string TargetPath,
    string TemporaryPath)
{
    public bool RequiresRename => !string.Equals(SourcePath, TargetPath, PathComparison.Comparison);
}

internal sealed record DesktopRenamePlan(
    string PreviewBaseName,
    IReadOnlyList<DesktopRenameItem> Items)
{
    public int ChangedItemCount => Items.Count(static item => item.RequiresRename);

    public int TotalMoveCount => ChangedItemCount * 2;
}

internal sealed record DesktopRenameProgress(
    DesktopRenameStage Stage,
    DesktopRenameItem Item,
    int CompletedMoveCount,
    int TotalMoveCount);

internal sealed record DesktopRenameFailure(
    DesktopRenameStage Stage,
    DesktopRenameItem Item,
    string Message);

internal sealed record DesktopRenameExecutionResult(
    IReadOnlyDictionary<string, string> CompletedRenameMap,
    bool WasCanceled,
    DesktopRenameFailure? Failure,
    string? RollbackMessage,
    int CompletedMoveCount,
    int TotalMoveCount);

internal static class DesktopRenameService
{
    private const int MaximumTemporaryPathAttempts = 64;
    private static readonly DesktopBatchExecutionPlan RenameExecutionPlan = new(
        MaxDegreeOfParallelism: 1,
        ProgressInterval: TimeSpan.Zero,
        ProgressStride: 1,
        YieldInterval: 1,
        MemoryTrimInterval: 0,
        StopOnFailure: true);

    public static DesktopRenamePlan? TryBuildPlan(
        IReadOnlyList<string> sourcePaths,
        SequentialRenamePattern renamePattern,
        out string previewBaseName,
        out string? validationMessage)
    {
        previewBaseName = NormalizeRenameBaseName(renamePattern.BaseName);
        validationMessage = null;

        if (sourcePaths.Count == 0)
        {
            validationMessage = "当前没有可重命名的图片。";
            return null;
        }

        var baseName = NormalizeRenameBaseName(renamePattern.BaseName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            validationMessage = "重命名基础名称不能为空。";
            return null;
        }

        if (baseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            validationMessage = "重命名基础名称包含无效字符。";
            return null;
        }

        var separator = renamePattern.SequenceSeparator ?? string.Empty;
        if (separator.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            validationMessage = "连接字符包含无效字符。";
            return null;
        }

        var normalizedPattern = renamePattern with { BaseName = baseName, SequenceSeparator = separator };
        try
        {
            previewBaseName = normalizedPattern.BuildFileBaseName(0, sourcePaths.Count);
        }
        catch (OverflowException)
        {
            validationMessage = "起始编号过大，无法生成连续编号。";
            return null;
        }

        var originalPaths = sourcePaths
            .Distinct(PathComparison.Comparer)
            .ToHashSet(PathComparison.Comparer);
        var targetPaths = new HashSet<string>(PathComparison.Comparer);
        var pendingItems = new List<(string SourcePath, string TargetPath)>(sourcePaths.Count);

        for (var index = 0; index < sourcePaths.Count; index++)
        {
            var sourcePath = sourcePaths[index];
            var directoryPath = Path.GetDirectoryName(sourcePath);
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                validationMessage = $"无法确定 {Path.GetFileName(sourcePath)} 的所在文件夹。";
                return null;
            }

            string targetBaseName;
            try
            {
                targetBaseName = normalizedPattern.BuildFileBaseName(index, sourcePaths.Count);
            }
            catch (OverflowException)
            {
                validationMessage = "起始编号过大，无法生成连续编号。";
                return null;
            }

            var targetPath = Path.Combine(directoryPath, $"{targetBaseName}{Path.GetExtension(sourcePath)}");
            if (!targetPaths.Add(targetPath))
            {
                validationMessage = $"目标文件名重复：{Path.GetFileName(targetPath)}";
                return null;
            }

            if (File.Exists(targetPath)
                && !string.Equals(targetPath, sourcePath, PathComparison.Comparison)
                && !originalPaths.Contains(targetPath))
            {
                validationMessage = $"目标文件已存在：{Path.GetFileName(targetPath)}";
                return null;
            }

            pendingItems.Add((sourcePath, targetPath));
        }

        var reservedPaths = new HashSet<string>(originalPaths, PathComparison.Comparer);
        reservedPaths.UnionWith(targetPaths);

        var items = new List<DesktopRenameItem>(pendingItems.Count);
        foreach (var (sourcePath, targetPath) in pendingItems)
        {
            if (string.Equals(sourcePath, targetPath, PathComparison.Comparison))
            {
                items.Add(new DesktopRenameItem(sourcePath, targetPath, sourcePath));
                continue;
            }

            var temporaryPath = CreateTemporaryPath(sourcePath, reservedPaths);
            items.Add(new DesktopRenameItem(sourcePath, targetPath, temporaryPath));
        }

        return new DesktopRenamePlan(previewBaseName, items);
    }

    public static async Task<DesktopRenameExecutionResult> ExecuteAsync(
        DesktopRenamePlan plan,
        CancellationToken cancellationToken,
        Action<DesktopRenameProgress>? progress = null)
    {
        return await ExecuteAsync(plan, new DesktopBatchProcessor(), cancellationToken, progress);
    }

    internal static async Task<DesktopRenameExecutionResult> ExecuteAsync(
        DesktopRenamePlan plan,
        DesktopBatchProcessor batchProcessor,
        CancellationToken cancellationToken,
        Action<DesktopRenameProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(batchProcessor);

        var changedItems = plan.Items
            .Where(static item => item.RequiresRename)
            .ToArray();
        var completedRenameMap = new Dictionary<string, string>(PathComparison.Comparer);

        if (changedItems.Length == 0)
        {
            return new DesktopRenameExecutionResult(
                completedRenameMap,
                WasCanceled: false,
                Failure: null,
                RollbackMessage: null,
                CompletedMoveCount: 0,
                TotalMoveCount: 0);
        }

        var tempCompleted = new List<DesktopRenameItem>(changedItems.Length);
        var finalCompleted = new List<DesktopRenameItem>(changedItems.Length);
        var totalMoveCount = changedItems.Length * 2;
        var completedMoveCount = 0;

        try
        {
            var temporaryPhaseItems = changedItems
                .Select(static item => new DesktopBatchItem<DesktopRenameItem>(item, Path.GetFileName(item.SourcePath)))
                .ToArray();
            var temporaryResult = await batchProcessor.RunAsync(
                temporaryPhaseItems,
                async (batchItem, _, token) =>
                {
                    var item = batchItem.Value;
                    await Task.Run(() => DesktopFileStreamFactory.MoveFile(item.SourcePath, item.TemporaryPath), token);
                    tempCompleted.Add(item);
                    completedMoveCount++;
                    progress?.Invoke(new DesktopRenameProgress(DesktopRenameStage.Temporary, item, completedMoveCount, totalMoveCount));
                },
                executionPlan: RenameExecutionPlan,
                cancellationToken: cancellationToken);

            if (temporaryResult.WasCanceled)
            {
                var rollbackMessage = TryRollback(finalCompleted, tempCompleted);
                return new DesktopRenameExecutionResult(
                    new Dictionary<string, string>(PathComparison.Comparer),
                    WasCanceled: true,
                    Failure: null,
                    RollbackMessage: rollbackMessage,
                    CompletedMoveCount: completedMoveCount,
                    TotalMoveCount: totalMoveCount);
            }

            if (temporaryResult.Failures.Count > 0)
            {
                var failedIndex = temporaryResult.Failures[0].Index;
                var failedItem = changedItems[Math.Clamp(failedIndex, 0, changedItems.Length - 1)];
                var rollbackMessage = TryRollback(finalCompleted, tempCompleted);

                return new DesktopRenameExecutionResult(
                    new Dictionary<string, string>(PathComparison.Comparer),
                    WasCanceled: false,
                    Failure: new DesktopRenameFailure(DesktopRenameStage.Temporary, failedItem, temporaryResult.Failures[0].ErrorMessage),
                    RollbackMessage: rollbackMessage,
                    CompletedMoveCount: completedMoveCount,
                    TotalMoveCount: totalMoveCount);
            }

            var finalPhaseItems = changedItems
                .Select(static item => new DesktopBatchItem<DesktopRenameItem>(item, Path.GetFileName(item.TargetPath)))
                .ToArray();
            var finalResult = await batchProcessor.RunAsync(
                finalPhaseItems,
                async (batchItem, _, token) =>
                {
                    var item = batchItem.Value;
                    await Task.Run(() => DesktopFileStreamFactory.MoveFile(item.TemporaryPath, item.TargetPath), token);
                    finalCompleted.Add(item);
                    completedRenameMap[item.SourcePath] = item.TargetPath;
                    completedMoveCount++;
                    progress?.Invoke(new DesktopRenameProgress(DesktopRenameStage.Final, item, completedMoveCount, totalMoveCount));
                },
                executionPlan: RenameExecutionPlan,
                cancellationToken: cancellationToken);

            if (finalResult.WasCanceled)
            {
                var rollbackMessage = TryRollback(finalCompleted, tempCompleted);
                return new DesktopRenameExecutionResult(
                    new Dictionary<string, string>(PathComparison.Comparer),
                    WasCanceled: true,
                    Failure: null,
                    RollbackMessage: rollbackMessage,
                    CompletedMoveCount: completedMoveCount,
                    TotalMoveCount: totalMoveCount);
            }

            if (finalResult.Failures.Count > 0)
            {
                var failedIndex = finalResult.Failures[0].Index;
                var failedItem = changedItems[Math.Clamp(failedIndex, 0, changedItems.Length - 1)];
                var rollbackMessage = TryRollback(finalCompleted, tempCompleted);

                return new DesktopRenameExecutionResult(
                    new Dictionary<string, string>(PathComparison.Comparer),
                    WasCanceled: false,
                    Failure: new DesktopRenameFailure(DesktopRenameStage.Final, failedItem, finalResult.Failures[0].ErrorMessage),
                    RollbackMessage: rollbackMessage,
                    CompletedMoveCount: completedMoveCount,
                    TotalMoveCount: totalMoveCount);
            }

            return new DesktopRenameExecutionResult(
                completedRenameMap,
                WasCanceled: false,
                Failure: null,
                RollbackMessage: null,
                CompletedMoveCount: completedMoveCount,
                TotalMoveCount: totalMoveCount);
        }
        catch (OperationCanceledException)
        {
            var rollbackMessage = TryRollback(finalCompleted, tempCompleted);
            return new DesktopRenameExecutionResult(
                new Dictionary<string, string>(PathComparison.Comparer),
                WasCanceled: true,
                Failure: null,
                RollbackMessage: rollbackMessage,
                CompletedMoveCount: completedMoveCount,
                TotalMoveCount: totalMoveCount);
        }
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

    private static string CreateTemporaryPath(string sourcePath, ISet<string> reservedPaths)
    {
        var directoryPath = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new InvalidOperationException($"无法确定 {sourcePath} 的父级文件夹。");
        }

        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        var extension = Path.GetExtension(sourcePath);

        for (var attempt = 0; attempt < MaximumTemporaryPathAttempts; attempt++)
        {
            var candidate = Path.Combine(
                directoryPath,
                $".{baseName}.{Guid.NewGuid():N}.renaming{extension}");

            if (reservedPaths.Contains(candidate) || File.Exists(candidate))
            {
                continue;
            }

            reservedPaths.Add(candidate);
            return candidate;
        }

        throw new IOException($"无法为 {Path.GetFileName(sourcePath)} 创建临时重命名路径。");
    }

    private static string? TryRollback(
        IReadOnlyList<DesktopRenameItem> finalCompleted,
        IReadOnlyList<DesktopRenameItem> tempCompleted)
    {
        try
        {
            for (var index = finalCompleted.Count - 1; index >= 0; index--)
            {
                var item = finalCompleted[index];
                if (File.Exists(item.TargetPath))
                {
                    DesktopFileStreamFactory.MoveFile(item.TargetPath, item.SourcePath);
                }
            }

            for (var index = tempCompleted.Count - 1; index >= 0; index--)
            {
                var item = tempCompleted[index];
                if (File.Exists(item.TemporaryPath))
                {
                    DesktopFileStreamFactory.MoveFile(item.TemporaryPath, item.SourcePath);
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
