using System.Collections.ObjectModel;
using ModernImageViewer.Core;
using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const int MaxRecentSessionCount = 8;
    private readonly RecentSessionStore _recentSessionStore = new();

    public ObservableCollection<RecentSessionItemViewModel> RecentSessions { get; } = [];

    public bool HasRecentSessions => RecentSessions.Count > 0;

    public bool ShowRecentSessionsEmptyState => !HasRecentSessions;

    public bool CanClearRecentSessions => RecentSessions.Count > 0;

    public string RecentSessionsSummaryText => RecentSessions.Count switch
    {
        <= 0 => "最近打开会记住你刚刚处理过的单图或文件夹，方便下次直接回来。",
        1 => RecentSessions.Any(static item => item.IsPinned)
            ? "当前保留 1 条最近打开记录，并已置顶。"
            : "当前保留 1 条最近打开记录。",
        _ => RecentSessions.Count(static item => item.IsPinned) switch
        {
            <= 0 => $"当前保留 {RecentSessions.Count} 条最近打开记录。",
            1 => $"当前保留 {RecentSessions.Count} 条最近打开记录，其中 1 条已置顶。",
            var pinnedCount => $"当前保留 {RecentSessions.Count} 条最近打开记录，其中 {pinnedCount} 条已置顶。"
        }
    };

    private void InitializeRecentSessions()
    {
        var loadedSnapshots = _recentSessionStore.LoadSessions()
            .OrderByDescending(static item => item.IsPinned)
            .ThenByDescending(static item => item.OpenedAt)
            .ToList();
        var cleanedSnapshots = loadedSnapshots
            .Where(HasAvailableRecentInputs)
            .Take(MaxRecentSessionCount)
            .ToList();

        ReplaceRecentSessions(cleanedSnapshots.Select(static item => new RecentSessionItemViewModel(
            item.Label,
            item.Subtitle,
            item.Inputs,
            item.OpenedAt,
            item.IsPinned)));

        if (cleanedSnapshots.Count != loadedSnapshots.Count)
        {
            _recentSessionStore.SaveSessions(cleanedSnapshots);
        }
    }

    public async Task OpenRecentSessionAsync(RecentSessionItemViewModel? item, CancellationToken cancellationToken = default)
    {
        if (item is null || item.Inputs.Count == 0)
        {
            return;
        }

        var availableInputs = item.Inputs
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Where(static path => File.Exists(path) || Directory.Exists(path))
            .Distinct(PathComparison.Comparer)
            .ToArray();

        if (availableInputs.Length == 0)
        {
            RemoveRecentSession(item);
            StatusText = $"这条记录对应的路径已经不存在，已从最近打开移除：{item.Label}";
            return;
        }

        await LoadInputsAsync(availableInputs, cancellationToken);
        StatusText = availableInputs.Length == item.Inputs.Count
            ? $"已重新打开：{item.Label}"
            : $"已重新打开仍然可用的路径：{item.Label}";
    }

    public void RemoveRecentSession(RecentSessionItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (RecentSessions.Remove(item))
        {
            PersistRecentSessions();
            NotifyRecentSessionsStateChanged();
        }
    }

    public void ClearRecentSessions()
    {
        if (RecentSessions.Count == 0)
        {
            return;
        }

        RecentSessions.Clear();
        PersistRecentSessions();
        NotifyRecentSessionsStateChanged();
        StatusText = "已清空最近打开记录。";
    }

    public void ToggleRecentSessionPinned(RecentSessionItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        item.SetPinned(!item.IsPinned);
        SortRecentSessions();
        PersistRecentSessions();
        NotifyRecentSessionsStateChanged();
        StatusText = item.IsPinned
            ? $"已置顶最近记录：{item.Label}"
            : $"已取消置顶：{item.Label}";
    }

    private void RegisterRecentSession(IReadOnlyList<string> inputs, string sourceLabel)
    {
        if (inputs.Count == 0)
        {
            return;
        }

        var normalizedInputs = inputs
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(PathComparison.Comparer)
            .ToArray();
        if (normalizedInputs.Length == 0)
        {
            return;
        }

        var duplicate = RecentSessions.FirstOrDefault(item => HaveSameInputs(item.Inputs, normalizedInputs));
        var wasPinned = duplicate?.IsPinned ?? false;
        var nextSessions = RecentSessions
            .Where(item => !ReferenceEquals(item, duplicate))
            .Append(new RecentSessionItemViewModel(
                BuildRecentLabel(normalizedInputs, sourceLabel),
                BuildRecentSubtitle(normalizedInputs),
                normalizedInputs,
                DateTimeOffset.Now,
                wasPinned))
            .OrderByDescending(static item => item.IsPinned)
            .ThenByDescending(static item => item.OpenedAt)
            .Take(MaxRecentSessionCount)
            .ToArray();

        ReplaceObservableCollectionItemsIfChanged(RecentSessions, nextSessions);
        PersistRecentSessions();
        NotifyRecentSessionsStateChanged();
    }

    private void SortRecentSessions()
    {
        var orderedSessions = RecentSessions
            .OrderByDescending(static item => item.IsPinned)
            .ThenByDescending(static item => item.OpenedAt)
            .Take(MaxRecentSessionCount)
            .ToArray();

        ReplaceObservableCollectionItemsIfChanged(RecentSessions, orderedSessions);
    }

    private void TrimRecentSessions()
    {
        while (RecentSessions.Count > MaxRecentSessionCount)
        {
            RecentSessions.RemoveAt(RecentSessions.Count - 1);
        }
    }

    private void ReplaceRecentSessions(IEnumerable<RecentSessionItemViewModel> items)
    {
        ReplaceObservableCollectionItemsIfChanged(RecentSessions, items.ToArray());
        NotifyRecentSessionsStateChanged();
    }

    private void PersistRecentSessions()
    {
        _recentSessionStore.SaveSessions(
            RecentSessions
                .Select(static item => new RecentSessionSnapshot(
                    item.Label,
                    item.Subtitle,
                    item.Inputs.ToList(),
                    item.OpenedAt,
                    item.IsPinned))
                .ToList());
    }

    private void NotifyRecentSessionsStateChanged()
    {
        OnPropertyChanged(nameof(HasRecentSessions));
        OnPropertyChanged(nameof(ShowRecentSessionsEmptyState));
        OnPropertyChanged(nameof(CanClearRecentSessions));
        OnPropertyChanged(nameof(RecentSessionsSummaryText));
        NotifyLayoutStateChanged();
    }

    private static bool HaveSameInputs(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(
                    Path.GetFullPath(left[index]),
                    Path.GetFullPath(right[index]),
                    PathComparison.Comparison))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasAvailableRecentInputs(RecentSessionSnapshot item)
    {
        return item.Inputs.Count > 0
            && item.Inputs.Any(static path =>
                !string.IsNullOrWhiteSpace(path)
                && (File.Exists(path) || Directory.Exists(path)));
    }

    private static string BuildRecentLabel(IReadOnlyList<string> inputs, string sourceLabel)
    {
        if (inputs.Count == 1)
        {
            var singlePath = inputs[0];
            if (Directory.Exists(singlePath))
            {
                return Path.GetFileName(singlePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }

            if (File.Exists(singlePath))
            {
                return Path.GetFileName(singlePath);
            }
        }

        return sourceLabel;
    }

    private static string BuildRecentSubtitle(IReadOnlyList<string> inputs)
    {
        if (inputs.Count == 1)
        {
            return inputs[0];
        }

        return $"已选 {inputs.Count} 项";
    }
}
