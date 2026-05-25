using System.Collections.ObjectModel;

namespace ModernImageViewer.Core;

public sealed class ImageCollectionBuilder
{
    private const int DefaultProgressBatchSize = 160;
    private const int EarlyFirstProgressBatchSize = 24;
    private const int EarlyProgressBatchSize = 48;
    private const int EarlyProgressDirectoryBoundaryBatchSize = 12;
    private const int EarlyProgressImageTargetCount = 200;
    private static readonly EnumerationOptions TopDirectoryEnumerationOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.System
    };

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avif",
        ".arw",
        ".bmp",
        ".cr3",
        ".dng",
        ".gif",
        ".heic",
        ".heif",
        ".ico",
        ".jpeg",
        ".jpg",
        ".jxl",
        ".nef",
        ".png",
        ".psd",
        ".svg",
        ".tif",
        ".tiff",
        ".webp"
    };

    public ReadOnlyCollection<string> Extensions { get; } = SupportedExtensions
        .OrderBy(static extension => extension)
        .ToList()
        .AsReadOnly();

    public ImageCollectionResult Build(
        IEnumerable<string> inputPaths,
        SortMode sortMode,
        bool includeSubfolders = false,
        CancellationToken cancellationToken = default)
    {
        return BuildCore(
            inputPaths,
            sortMode,
            includeSubfolders,
            onBatchDiscovered: null,
            progressBatchSize: DefaultProgressBatchSize,
            cancellationToken);
    }

    public ImageCollectionResult BuildProgressive(
        IEnumerable<string> inputPaths,
        SortMode sortMode,
        bool includeSubfolders = false,
        Action<ImageCollectionBuildBatch>? onBatchDiscovered = null,
        int progressBatchSize = DefaultProgressBatchSize,
        CancellationToken cancellationToken = default)
    {
        return BuildCore(
            inputPaths,
            sortMode,
            includeSubfolders,
            onBatchDiscovered,
            progressBatchSize,
            cancellationToken);
    }

    private static ImageCollectionResult BuildCore(
        IEnumerable<string> inputPaths,
        SortMode sortMode,
        bool includeSubfolders,
        Action<ImageCollectionBuildBatch>? onBatchDiscovered,
        int progressBatchSize,
        CancellationToken cancellationToken)
    {
        if (progressBatchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(progressBatchSize), progressBatchSize, "Batch size must be positive.");
        }

        var normalizedPaths = inputPaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(PathComparison.Comparer)
            .ToArray();

        if (normalizedPaths.Length == 0)
        {
            return new ImageCollectionResult(Array.Empty<ImageRecord>(), null, "拖入图片或文件夹");
        }

        var collectedFiles = new HashSet<string>(PathComparison.Comparer);
        var records = new List<ImageRecord>();
        List<ImageRecord>? batchBuffer = onBatchDiscovered is null ? null : new(progressBatchSize);
        string? focusPath = null;
        var earlyProgressBatchSize = Math.Min(progressBatchSize, EarlyProgressBatchSize);
        var earlyFirstProgressBatchSize = Math.Min(progressBatchSize, EarlyFirstProgressBatchSize);
        var earlyDirectoryBoundaryBatchSize = Math.Min(earlyProgressBatchSize, EarlyProgressDirectoryBoundaryBatchSize);
        var sourceLabel = "已选位置";

        void FlushBatch()
        {
            if (onBatchDiscovered is null || batchBuffer is null || batchBuffer.Count == 0)
            {
                return;
            }

            onBatchDiscovered(new ImageCollectionBuildBatch(batchBuffer.ToArray(), records.Count));
            batchBuffer.Clear();
        }

        void TryFlushEarlyBatch(bool isDirectoryBoundary = false, bool isRootDirectory = false)
        {
            if (onBatchDiscovered is null || batchBuffer is null || batchBuffer.Count == 0)
            {
                return;
            }

            if (records.Count > EarlyProgressImageTargetCount)
            {
                return;
            }

            var targetBatchSize = records.Count <= earlyFirstProgressBatchSize
                ? earlyFirstProgressBatchSize
                : earlyProgressBatchSize;

            if (batchBuffer.Count >= targetBatchSize
                || (isRootDirectory && batchBuffer.Count > 0)
                || (isDirectoryBoundary && batchBuffer.Count >= earlyDirectoryBoundaryBatchSize))
            {
                FlushBatch();
            }
        }

        void AddRecordFile(FileInfo fileInfo)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!collectedFiles.Add(fileInfo.FullName))
            {
                return;
            }

            var record = TryCreateRecord(fileInfo);
            if (record is null)
            {
                return;
            }

            records.Add(record);

            if (batchBuffer is null)
            {
                return;
            }

            batchBuffer.Add(record);
            if (batchBuffer.Count >= progressBatchSize)
            {
                FlushBatch();
                return;
            }

            TryFlushEarlyBatch();
        }

        if (normalizedPaths.Length == 1)
        {
            var singlePath = normalizedPaths[0];
            cancellationToken.ThrowIfCancellationRequested();

            if (Directory.Exists(singlePath))
            {
                sourceLabel = singlePath;
                ScanDirectoryFiles(
                    singlePath,
                    includeSubfolders,
                    cancellationToken,
                    AddRecordFile,
                    isRootDirectory => TryFlushEarlyBatch(isDirectoryBoundary: true, isRootDirectory));
            }
            else if (File.Exists(singlePath) && IsSupportedImage(singlePath))
            {
                focusPath = singlePath;
                sourceLabel = singlePath;
                AddRecordFile(new FileInfo(singlePath));
            }
        }
        else
        {
            sourceLabel = "已选项目";

            foreach (var path in normalizedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(path))
                {
                    ScanDirectoryFiles(
                        path,
                        includeSubfolders,
                        cancellationToken,
                        AddRecordFile,
                        isRootDirectory => TryFlushEarlyBatch(isDirectoryBoundary: true, isRootDirectory));

                    continue;
                }

                if (File.Exists(path) && IsSupportedImage(path))
                {
                    AddRecordFile(new FileInfo(path));
                }
            }

            var firstFile = normalizedPaths.FirstOrDefault(File.Exists);
            if (!string.IsNullOrWhiteSpace(firstFile))
            {
                focusPath = firstFile;
            }
        }

        FlushBatch();

        records = Sort(records, sortMode);
        focusPath ??= records.FirstOrDefault()?.FullPath;

        return new ImageCollectionResult(records, focusPath, sourceLabel);
    }

    private static void ScanDirectoryFiles(
        string folderPath,
        bool includeSubfolders,
        CancellationToken cancellationToken,
        Action<FileInfo> onFileDiscovered,
        Action<bool>? onDirectoryCompleted)
    {
        var pendingFolders = new Queue<string>();
        var visitedFolders = new HashSet<string>(PathComparison.Comparer);
        pendingFolders.Enqueue(folderPath);
        var isRootDirectory = true;

        while (pendingFolders.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentFolder = pendingFolders.Dequeue();
            string normalizedFolder;
            try
            {
                normalizedFolder = Path.GetFullPath(currentFolder);
            }
            catch
            {
                continue;
            }

            if (!visitedFolders.Add(normalizedFolder))
            {
                continue;
            }

            foreach (var file in SafeEnumerateEntries(
                         () =>
                         {
                             var directoryInfo = new DirectoryInfo(currentFolder);
                             return directoryInfo.EnumerateFiles("*", TopDirectoryEnumerationOptions);
                         },
                         cancellationToken))
            {
                if (IsSupportedImage(file.Name))
                {
                    onFileDiscovered(file);
                }
            }

            onDirectoryCompleted?.Invoke(isRootDirectory);
            isRootDirectory = false;

            if (!includeSubfolders)
            {
                continue;
            }

            foreach (var childFolder in SafeEnumerateEntries(
                         () =>
                         {
                             var directoryInfo = new DirectoryInfo(currentFolder);
                             return directoryInfo.EnumerateDirectories("*", TopDirectoryEnumerationOptions);
                         },
                         cancellationToken))
            {
                if (ShouldSkipFolder(childFolder))
                {
                    continue;
                }

                pendingFolders.Enqueue(childFolder.FullName);
            }
        }
    }

    private static IEnumerable<TEntry> SafeEnumerateEntries<TEntry>(
        Func<IEnumerable<TEntry>> enumerationFactory,
        CancellationToken cancellationToken)
    {
        IEnumerator<TEntry>? enumerator = null;

        try
        {
            enumerator = enumerationFactory().GetEnumerator();
        }
        catch
        {
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool hasNext;
                try
                {
                    hasNext = enumerator.MoveNext();
                }
                catch
                {
                    yield break;
                }

                if (!hasNext)
                {
                    yield break;
                }

                yield return enumerator.Current;
            }
        }
    }

    private static bool ShouldSkipFolder(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return true;
        }
    }

    private static bool ShouldSkipFolder(DirectoryInfo directoryInfo)
    {
        try
        {
            return directoryInfo.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return true;
        }
    }

    private static List<ImageRecord> Sort(List<ImageRecord> records, SortMode sortMode)
    {
        return Sort(records, sortMode, PathComparison.NameComparer);
    }

    internal static List<ImageRecord> Sort(
        List<ImageRecord> records,
        SortMode sortMode,
        StringComparer fileNameComparer)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(fileNameComparer);

        Comparison<ImageRecord> comparison = sortMode switch
        {
            SortMode.Modified => (left, right) =>
            {
                var modifiedComparison = right.ModifiedAt.CompareTo(left.ModifiedAt);
                return modifiedComparison != 0
                    ? modifiedComparison
                    : CompareByName(left, right, fileNameComparer);
            },
            SortMode.Size => (left, right) =>
            {
                var sizeComparison = right.SizeBytes.CompareTo(left.SizeBytes);
                return sizeComparison != 0
                    ? sizeComparison
                    : CompareByName(left, right, fileNameComparer);
            },
            _ => (left, right) => CompareByName(left, right, fileNameComparer)
        };

        records.Sort(comparison);
        return records;
    }

    private static int CompareByName(ImageRecord left, ImageRecord right, StringComparer fileNameComparer)
    {
        var fileNameComparison = fileNameComparer.Compare(left.FileName, right.FileName);
        return fileNameComparison != 0
            ? fileNameComparison
            : StringComparer.Ordinal.Compare(left.FileName, right.FileName);
    }

    private static ImageRecord? TryCreateRecord(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            return TryCreateRecord(fileInfo);
        }
        catch
        {
            return null;
        }
    }

    private static ImageRecord? TryCreateRecord(FileInfo fileInfo)
    {
        try
        {
            return fileInfo.Exists
                ? new ImageRecord(fileInfo.FullName, fileInfo.Name, fileInfo.Length, fileInfo.LastWriteTime)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSupportedImage(string path)
    {
        var extension = Path.GetExtension(path);
        return !string.IsNullOrWhiteSpace(extension) && SupportedExtensions.Contains(extension);
    }
}
