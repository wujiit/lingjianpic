using System.Collections.Concurrent;
using System.Numerics;
using ImageMagick;
using ModernImageViewer.Core;

namespace ModernImageViewer.Desktop.Services;

public sealed record SimilarImageGroup(
    ulong Hash,
    IReadOnlyList<string> Paths);

public sealed record SimilarImageScanResult(
    int ScannedCount,
    int FailedCount,
    int DistanceThreshold,
    IReadOnlyList<SimilarImageGroup> Groups)
{
    public int GroupCount => Groups.Count;

    public int SimilarCount => Groups.Sum(static group => Math.Max(0, group.Paths.Count - 1));
}

public sealed class DesktopSimilarImageService
{
    private const long Megabyte = 1024L * 1024L;
    private const int HashWidth = 9;
    private const int HashHeight = 8;
    // dHash only needs a tiny low-frequency sample, so keep the working decode size
    // close to fingerprint scale instead of preview scale.
    private const int HashReadLongEdge = 160;
    private const long LargeHashSourceSizeBytes = 24L * Megabyte;
    private const long HugeHashSourceSizeBytes = 96L * Megabyte;
    private const long HashDecodePixelBudget = 20_480L;
    private const long LargeSourceHashDecodePixelBudget = 16_384L;
    private const long HugeSourceHashDecodePixelBudget = 12_288L;
    private const int UnknownDimensionHashReadLongEdge = 128;
    private const int LargeUnknownDimensionHashReadLongEdge = 96;
    private const int HugeUnknownDimensionHashReadLongEdge = 80;
    private readonly DesktopImageFingerprintCacheStore _cache;
    private readonly DesktopImageDimensionCacheStore _dimensionCache;
    private readonly DesktopExactDuplicateImageService _exactDuplicateService;
    private readonly DesktopBatchProcessor _batchProcessor;

    private readonly record struct SimilarHashCluster(
        (int InputOrder, ImageRecord Record) Representative,
        IReadOnlyList<(int InputOrder, ImageRecord Record)> Members);

    public DesktopSimilarImageService()
        : this(new DesktopImageFingerprintCacheStore(), DesktopImageDimensionCacheStore.Shared, new DesktopBatchProcessor())
    {
    }

    internal DesktopSimilarImageService(DesktopImageFingerprintCacheStore cache)
        : this(cache, DesktopImageDimensionCacheStore.Shared, new DesktopBatchProcessor())
    {
    }

    internal DesktopSimilarImageService(
        DesktopImageFingerprintCacheStore cache,
        DesktopImageDimensionCacheStore dimensionCache)
        : this(cache, dimensionCache, new DesktopBatchProcessor())
    {
    }

    internal DesktopSimilarImageService(
        DesktopImageFingerprintCacheStore cache,
        DesktopImageDimensionCacheStore dimensionCache,
        DesktopBatchProcessor batchProcessor)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _dimensionCache = dimensionCache ?? throw new ArgumentNullException(nameof(dimensionCache));
        _batchProcessor = batchProcessor ?? throw new ArgumentNullException(nameof(batchProcessor));
        _exactDuplicateService = new DesktopExactDuplicateImageService(_cache, _batchProcessor);
    }

    public void RefreshProcessingPolicy()
    {
        _cache.UpdateEntryLimits(
            DesktopImageProcessingPolicy.FingerprintTextCacheEntryLimit,
            DesktopImageProcessingPolicy.FingerprintDifferenceHashCacheEntryLimit);
        _dimensionCache.UpdateEntryLimit(DesktopImageProcessingPolicy.ImageDimensionCacheEntryLimit);
        _exactDuplicateService.RefreshProcessingPolicy();
    }

    public SimilarImageScanResult FindSimilarImages(
        IReadOnlyList<ImageRecord> images,
        int distanceThreshold,
        CancellationToken cancellationToken,
        IProgress<DesktopOperationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(images);

        try
        {
            if (images.Count == 0)
            {
                return new SimilarImageScanResult(0, 0, distanceThreshold, []);
            }

            var totalWork = Math.Max(1, images.Count * 2);
            var hashClusters = CreateHashClusters(images, cancellationToken);
            var descriptorsBag = new ConcurrentBag<(int InputOrder, ImageRecord Record, ulong Hash)>();
            var failedCount = 0;
            var totalInputBytes = SumInputBytes(images);
            var hashExecutionPlan = CreateAdaptiveHashExecutionPlan(images.Count, totalInputBytes);
            var completedHashes = 0;
            var hashBatchItems = hashClusters
                .Select(static cluster => new DesktopBatchItem<SimilarHashCluster>(cluster, cluster.Representative.Record.FileName))
                .ToArray();
            var hashProgress = progress is null
                ? null
                : new Progress<DesktopBatchProgress>(update =>
                {
                    var completed = Math.Clamp(Volatile.Read(ref completedHashes), 0, images.Count);
                    progress.Report(new DesktopOperationProgress(
                        completed,
                        totalWork,
                        $"正在计算相似特征 {completed} / {images.Count} - {update.DisplayName}"));
                });
            var hashResult = _batchProcessor.RunAsync(
                    hashBatchItems,
                    (item, _, token) =>
                    {
                        var cluster = item.Value;
                        try
                        {
                            PopulateDescriptorCluster(
                                cluster,
                                descriptorsBag,
                                token,
                                () => Interlocked.Increment(ref failedCount));
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch
                        {
                            foreach (var member in cluster.Members)
                            {
                                if (!TryComputeDifferenceHash(member.Record, token, out var hash))
                                {
                                    Interlocked.Increment(ref failedCount);
                                    continue;
                                }

                                descriptorsBag.Add((member.InputOrder, member.Record, hash));
                            }
                        }
                        finally
                        {
                            Interlocked.Add(ref completedHashes, cluster.Members.Count);
                        }

                        return Task.CompletedTask;
                    },
                    hashProgress,
                    executionPlan: hashExecutionPlan.ToBatchExecutionPlan(),
                    cancellationToken: cancellationToken)
                .GetAwaiter()
                .GetResult();
            failedCount += hashResult.FailureCount;
            if (hashResult.WasCanceled && cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            DesktopImageProcessingPolicy.TrimMemory();

            var descriptors = descriptorsBag
                .OrderBy(static item => item.InputOrder)
                .ToList();

            if (descriptors.Count < 2)
            {
                progress?.Report(new DesktopOperationProgress(totalWork, totalWork, "没有足够可比较的图片。"));
                return new SimilarImageScanResult(images.Count, failedCount, distanceThreshold, []);
            }

            var tree = new HammingBkTree();
            for (var index = 0; index < descriptors.Count; index++)
            {
                tree.Add(descriptors[index].Hash, index);
            }

            var disjointSet = new DisjointSet(descriptors.Count);
            var matchExecutionCoordinator = new DesktopAnalysisExecutionCoordinator(progress, hashExecutionPlan);
            for (var index = 0; index < descriptors.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var image = descriptors[index].Record;
                foreach (var candidateIndex in tree.Search(descriptors[index].Hash, distanceThreshold))
                {
                    if (candidateIndex <= index)
                    {
                        continue;
                    }

                    if (GetHammingDistance(descriptors[index].Hash, descriptors[candidateIndex].Hash) <= distanceThreshold)
                    {
                        disjointSet.Union(index, candidateIndex);
                    }
                }

                var completed = index + 1;
                matchExecutionCoordinator.ReportIfNeeded(
                    images.Count + completed,
                    totalWork,
                    $"正在匹配相似图片 {completed} / {descriptors.Count} - {image.FileName}");
                matchExecutionCoordinator.OnItemCompleted(completed);
            }

            DesktopImageProcessingPolicy.TrimMemory();

            var groups = descriptors
                .Select((descriptor, index) => new { Descriptor = descriptor, Index = index })
                .GroupBy(item => disjointSet.Find(item.Index))
                .Where(static group => group.Count() > 1)
                .Select(group =>
                {
                    var orderedItems = group
                        .Select(static item => item.Descriptor)
                        .OrderBy(static item => item.Record.ModifiedAt)
                        .ThenBy(static item => item.Record.FileName, PathComparison.NameComparer)
                        .ThenBy(static item => item.Record.FileName, StringComparer.Ordinal)
                        .ThenBy(static item => item.InputOrder)
                        .ToList();

                    return new
                    {
                        FirstInputOrder = group.Min(static item => item.Descriptor.InputOrder),
                        Hash = orderedItems[0].Hash,
                        Paths = orderedItems.Select(static item => item.Record.FullPath).ToArray()
                    };
                })
                .OrderBy(static group => group.FirstInputOrder)
                .Select(static group => new SimilarImageGroup(group.Hash, group.Paths))
                .ToArray();

            progress?.Report(new DesktopOperationProgress(totalWork, totalWork, $"相似图片分组完成，共 {groups.Length} 组。"));
            return new SimilarImageScanResult(images.Count, failedCount, distanceThreshold, groups);
        }
        finally
        {
            _cache.Persist();
        }
    }

    private bool TryComputeDifferenceHash(ImageRecord image, CancellationToken cancellationToken, out ulong hash)
    {
        hash = 0;

        try
        {
            hash = _cache.GetOrAddDifferenceHash(
                image.FullPath,
                image.SizeBytes,
                image.ModifiedAt.ToUniversalTime().Ticks,
                () => ComputeDifferenceHash(image, _dimensionCache, cancellationToken));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void PopulateDescriptorCluster(
        SimilarHashCluster cluster,
        ConcurrentBag<(int InputOrder, ImageRecord Record, ulong Hash)> descriptorsBag,
        CancellationToken cancellationToken,
        Action onFailure)
    {
        ArgumentNullException.ThrowIfNull(descriptorsBag);
        ArgumentNullException.ThrowIfNull(onFailure);

        if (!TryComputeDifferenceHash(cluster.Representative.Record, cancellationToken, out var representativeHash))
        {
            foreach (var member in cluster.Members)
            {
                if (!TryComputeDifferenceHash(member.Record, cancellationToken, out var memberHash))
                {
                    onFailure();
                    continue;
                }

                descriptorsBag.Add((member.InputOrder, member.Record, memberHash));
            }

            return;
        }

        descriptorsBag.Add((cluster.Representative.InputOrder, cluster.Representative.Record, representativeHash));

        foreach (var member in cluster.Members)
        {
            if (member.InputOrder == cluster.Representative.InputOrder
                && PathComparison.Comparer.Equals(member.Record.FullPath, cluster.Representative.Record.FullPath))
            {
                continue;
            }

            try
            {
                var memberHash = _cache.GetOrAddDifferenceHash(
                    member.Record.FullPath,
                    member.Record.SizeBytes,
                    member.Record.ModifiedAt.ToUniversalTime().Ticks,
                    () => representativeHash);
                descriptorsBag.Add((member.InputOrder, member.Record, memberHash));
            }
            catch
            {
                if (!TryComputeDifferenceHash(member.Record, cancellationToken, out var fallbackHash))
                {
                    onFailure();
                    continue;
                }

                descriptorsBag.Add((member.InputOrder, member.Record, fallbackHash));
            }
        }
    }

    private IReadOnlyList<SimilarHashCluster> CreateHashClusters(
        IReadOnlyList<ImageRecord> images,
        CancellationToken cancellationToken)
    {
        var indexedImages = images
            .Select(static (record, index) => (InputOrder: index, Record: record))
            .ToArray();

        if (!ShouldUseExactContentPrefilter(images))
        {
            return indexedImages
                .Select(static item => new SimilarHashCluster(item, [item]))
                .ToArray();
        }

        var duplicateScan = _exactDuplicateService.FindDuplicates(
            images,
            cancellationToken,
            progress: null,
            persistCache: false);
        if (duplicateScan.Groups.Count == 0)
        {
            return indexedImages
                .Select(static item => new SimilarHashCluster(item, [item]))
                .ToArray();
        }

        var indexedImagesByPath = indexedImages.ToDictionary(static item => item.Record.FullPath, PathComparison.Comparer);
        var groupedPaths = new HashSet<string>(PathComparison.Comparer);
        var clusters = new List<SimilarHashCluster>(indexedImages.Length);

        foreach (var group in duplicateScan.Groups)
        {
            var members = group.Paths
                .Where(indexedImagesByPath.ContainsKey)
                .Select(path => indexedImagesByPath[path])
                .OrderBy(static item => item.InputOrder)
                .ToArray();
            if (members.Length <= 1)
            {
                continue;
            }

            foreach (var member in members)
            {
                groupedPaths.Add(member.Record.FullPath);
            }

            clusters.Add(new SimilarHashCluster(members[0], members));
        }

        foreach (var item in indexedImages)
        {
            if (groupedPaths.Contains(item.Record.FullPath))
            {
                continue;
            }

            clusters.Add(new SimilarHashCluster(item, [item]));
        }

        clusters.Sort(static (left, right) => left.Representative.InputOrder.CompareTo(right.Representative.InputOrder));
        return clusters;
    }

    internal static bool ShouldUseExactContentPrefilter(IReadOnlyList<ImageRecord> images)
    {
        ArgumentNullException.ThrowIfNull(images);

        if (images.Count < 3)
        {
            return false;
        }

        var sizeCounts = new Dictionary<long, int>();
        foreach (var image in images)
        {
            sizeCounts.TryGetValue(image.SizeBytes, out var count);
            sizeCounts[image.SizeBytes] = count + 1;
        }

        var duplicateSizeCandidates = 0;
        var duplicateInputBytes = 0L;
        foreach (var image in images)
        {
            if (!sizeCounts.TryGetValue(image.SizeBytes, out var count) || count <= 1)
            {
                continue;
            }

            duplicateSizeCandidates++;
            var normalizedSize = Math.Max(0L, image.SizeBytes);
            if (long.MaxValue - duplicateInputBytes < normalizedSize)
            {
                duplicateInputBytes = long.MaxValue;
            }
            else
            {
                duplicateInputBytes += normalizedSize;
            }
        }

        return duplicateSizeCandidates >= 2
            && (duplicateSizeCandidates >= Math.Max(2, images.Count / 6)
                || duplicateInputBytes >= 16L * Megabyte);
    }

    private static ulong ComputeDifferenceHash(
        ImageRecord image,
        DesktopImageDimensionCacheStore dimensionCache,
        CancellationToken cancellationToken)
    {
        return DesktopMagickOperationGate.Shared.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceInfo = DesktopImageSourceInfoReader.Read(
                new DesktopFileSignature(
                    image.FullPath,
                    image.SizeBytes,
                    image.ModifiedAt.ToUniversalTime().Ticks),
                dimensionCache);
            var sourceSizeBytes = sourceInfo.SizeBytes;
            var dimensions = sourceInfo.Dimensions;
            var readSettings = CreateHashReadSettings(dimensions, sourceSizeBytes);
            using var magickImage = new MagickImage(image.FullPath, readSettings);
            cancellationToken.ThrowIfCancellationRequested();
            magickImage.AutoOrient();
            ResizeMagickImageToLongEdge(magickImage, CalculateHashDecodeLongEdge(dimensions, sourceSizeBytes));
            magickImage.Resize(new MagickGeometry(HashWidth, HashHeight) { IgnoreAspectRatio = true });
            magickImage.ColorSpace = ColorSpace.Gray;
            magickImage.Depth = 8;
            magickImage.Format = MagickFormat.Gray;

            var pixels = magickImage.GetPixels();

            ulong currentHash = 0;
            var bitIndex = 0;
            for (var row = 0; row < HashHeight; row++)
            {
                for (var column = 0; column < HashWidth - 1; column++)
                {
                    if (GetPixelGrayValue(pixels, column, row) > GetPixelGrayValue(pixels, column + 1, row))
                    {
                        currentHash |= 1UL << bitIndex;
                    }

                    bitIndex++;
                }
            }

            return currentHash;
        });
    }

    private static byte GetPixelGrayValue(IPixelCollection<byte> pixels, int x, int y)
    {
        var color = pixels.GetPixel(x, y)?.ToColor();
        if (color is null)
        {
            throw new InvalidOperationException("Unable to read grayscale pixel from image.");
        }

        return color.R;
    }

    internal static int CalculateHashParallelism(int totalCount, long totalInputBytes)
    {
        return DesktopAnalysisParallelismAdvisor.Calculate(
            DesktopAnalysisWorkloadKind.SimilarHash,
            totalCount,
            totalInputBytes);
    }

    internal static DesktopAnalysisExecutionPlan CreateHashExecutionPlan(int totalCount, long totalInputBytes)
    {
        return DesktopAnalysisParallelismAdvisor.CreateExecutionPlan(
            DesktopAnalysisWorkloadKind.SimilarHash,
            totalCount,
            totalInputBytes);
    }

    private static DesktopAnalysisExecutionPlan CreateAdaptiveHashExecutionPlan(int totalCount, long totalInputBytes)
    {
        return DesktopAnalysisParallelismAdvisor.CreateAdaptiveExecutionPlan(
            DesktopAnalysisWorkloadKind.SimilarHash,
            totalCount,
            totalInputBytes);
    }

    internal static MagickReadSettings CreateHashReadSettings(
        DesktopImageDimensions? dimensions,
        long sourceSizeBytes)
    {
        var settings = new MagickReadSettings();
        var effectiveLongEdge = CalculateHashDecodeLongEdge(dimensions, sourceSizeBytes);
        if (effectiveLongEdge <= 0)
        {
            return settings;
        }

        if (dimensions is { } imageSize)
        {
            if (imageSize.LongEdge <= effectiveLongEdge)
            {
                return settings;
            }

            var scale = effectiveLongEdge / (double)imageSize.LongEdge;
            settings.Width = (uint)Math.Max(1, (int)Math.Round(imageSize.Width * scale));
            settings.Height = (uint)Math.Max(1, (int)Math.Round(imageSize.Height * scale));
            return settings;
        }

        var fallbackReadLongEdge = CalculateUnknownDimensionHashReadLongEdge(sourceSizeBytes);
        if (fallbackReadLongEdge > 0)
        {
            settings.Width = (uint)fallbackReadLongEdge;
            settings.Height = (uint)fallbackReadLongEdge;
        }

        return settings;
    }

    internal static int CalculateHashDecodeLongEdge(
        DesktopImageDimensions? dimensions,
        long sourceSizeBytes)
    {
        if (dimensions is not { } imageSize || imageSize.LongEdge == 0)
        {
            return HashReadLongEdge;
        }

        var effectiveLongEdge = Math.Min(HashReadLongEdge, (int)Math.Min(int.MaxValue, imageSize.LongEdge));
        var pixelBudget = ResolveHashDecodePixelBudget(sourceSizeBytes);
        if (pixelBudget <= 0 || CalculateScaledPixelCount(imageSize, effectiveLongEdge) <= pixelBudget)
        {
            return effectiveLongEdge;
        }

        var sourcePixelCount = (double)imageSize.Width * imageSize.Height;
        if (sourcePixelCount <= 0)
        {
            return effectiveLongEdge;
        }

        var scale = Math.Sqrt(pixelBudget / sourcePixelCount);
        var budgetedLongEdge = (int)Math.Floor(imageSize.LongEdge * scale);
        return Math.Clamp(budgetedLongEdge, 1, effectiveLongEdge);
    }

    internal static int CalculateUnknownDimensionHashReadLongEdge(long sourceSizeBytes)
    {
        if (sourceSizeBytes >= HugeHashSourceSizeBytes)
        {
            return HugeUnknownDimensionHashReadLongEdge;
        }

        if (sourceSizeBytes >= LargeHashSourceSizeBytes)
        {
            return LargeUnknownDimensionHashReadLongEdge;
        }

        return UnknownDimensionHashReadLongEdge;
    }

    private static long ResolveHashDecodePixelBudget(long sourceSizeBytes)
    {
        if (sourceSizeBytes >= HugeHashSourceSizeBytes)
        {
            return HugeSourceHashDecodePixelBudget;
        }

        if (sourceSizeBytes >= LargeHashSourceSizeBytes)
        {
            return LargeSourceHashDecodePixelBudget;
        }

        return HashDecodePixelBudget;
    }

    private static long CalculateScaledPixelCount(DesktopImageDimensions dimensions, int longEdgePixels)
    {
        if (longEdgePixels <= 0 || dimensions.LongEdge == 0)
        {
            return 0;
        }

        var scale = longEdgePixels / (double)dimensions.LongEdge;
        return (long)Math.Ceiling(dimensions.Width * scale * dimensions.Height * scale);
    }

    private static void ResizeMagickImageToLongEdge(MagickImage image, int maxLongEdgePixels)
    {
        var longEdge = Math.Max(image.Width, image.Height);
        if (maxLongEdgePixels <= 0 || longEdge <= maxLongEdgePixels)
        {
            return;
        }

        var scale = maxLongEdgePixels / (double)longEdge;
        var targetWidth = (uint)Math.Max(1, (int)Math.Round(image.Width * scale));
        var targetHeight = (uint)Math.Max(1, (int)Math.Round(image.Height * scale));
        image.Resize(targetWidth, targetHeight);
    }

    private static int GetHammingDistance(ulong left, ulong right)
    {
        return BitOperations.PopCount(left ^ right);
    }

    private static bool ShouldReportProgress(int completedCount, int totalCount)
    {
        return completedCount <= 0
            || completedCount >= totalCount
            || completedCount % 8 == 0;
    }

    private static long SumInputBytes(IReadOnlyList<ImageRecord> images)
    {
        var totalBytes = 0L;
        foreach (var image in images)
        {
            var normalizedSize = Math.Max(0L, image.SizeBytes);
            if (long.MaxValue - totalBytes < normalizedSize)
            {
                return long.MaxValue;
            }

            totalBytes += normalizedSize;
        }

        return totalBytes;
    }

    private sealed class HammingBkTree
    {
        private Node? _root;

        public void Add(ulong hash, int index)
        {
            if (_root is null)
            {
                _root = new Node(hash, index);
                return;
            }

            var current = _root;
            while (true)
            {
                var distance = GetHammingDistance(hash, current.Hash);
                if (!current.Children.TryGetValue(distance, out var child))
                {
                    current.Children[distance] = new Node(hash, index);
                    return;
                }

                current = child;
            }
        }

        public IReadOnlyList<int> Search(ulong hash, int distanceThreshold)
        {
            if (_root is null)
            {
                return [];
            }

            var matches = new List<int>();
            var stack = new Stack<Node>();
            stack.Push(_root);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                var distance = GetHammingDistance(hash, current.Hash);
                if (distance <= distanceThreshold)
                {
                    matches.Add(current.Index);
                }

                var minDistance = distance - distanceThreshold;
                var maxDistance = distance + distanceThreshold;
                foreach (var child in current.Children)
                {
                    if (child.Key >= minDistance && child.Key <= maxDistance)
                    {
                        stack.Push(child.Value);
                    }
                }
            }

            return matches;
        }

        private sealed class Node(ulong hash, int index)
        {
            public ulong Hash { get; } = hash;

            public int Index { get; } = index;

            public Dictionary<int, Node> Children { get; } = [];
        }
    }

    private sealed class DisjointSet
    {
        private readonly int[] _parents;
        private readonly byte[] _ranks;

        public DisjointSet(int count)
        {
            _parents = new int[count];
            _ranks = new byte[count];
            for (var index = 0; index < count; index++)
            {
                _parents[index] = index;
            }
        }

        public int Find(int index)
        {
            if (_parents[index] == index)
            {
                return index;
            }

            _parents[index] = Find(_parents[index]);
            return _parents[index];
        }

        public void Union(int left, int right)
        {
            var leftRoot = Find(left);
            var rightRoot = Find(right);
            if (leftRoot == rightRoot)
            {
                return;
            }

            if (_ranks[leftRoot] < _ranks[rightRoot])
            {
                _parents[leftRoot] = rightRoot;
                return;
            }

            if (_ranks[leftRoot] > _ranks[rightRoot])
            {
                _parents[rightRoot] = leftRoot;
                return;
            }

            _parents[rightRoot] = leftRoot;
            _ranks[leftRoot]++;
        }
    }
}
