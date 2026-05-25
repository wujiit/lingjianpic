using System.Collections.Concurrent;
using System.Buffers;
using System.Security.Cryptography;
using ModernImageViewer.Core;

namespace ModernImageViewer.Desktop.Services;

public sealed record ExactDuplicateGroup(
    long SizeBytes,
    string Hash,
    IReadOnlyList<string> Paths);

public sealed record ExactDuplicateScanResult(
    int ScannedCount,
    int FailedCount,
    IReadOnlyList<ExactDuplicateGroup> Groups)
{
    public int GroupCount => Groups.Count;

    public int DuplicateCount => Groups.Sum(static group => Math.Max(0, group.Paths.Count - 1));
}

public sealed class DesktopExactDuplicateImageService
{
    private const int SampleBlockSize = 128 * 1024;
    private const int HashReadBufferSize = 128 * 1024;
    private readonly DesktopImageFingerprintCacheStore _cache;
    private readonly DesktopBatchProcessor _batchProcessor;

    public DesktopExactDuplicateImageService()
        : this(new DesktopImageFingerprintCacheStore(), new DesktopBatchProcessor())
    {
    }

    internal DesktopExactDuplicateImageService(DesktopImageFingerprintCacheStore cache)
        : this(cache, new DesktopBatchProcessor())
    {
    }

    internal DesktopExactDuplicateImageService(
        DesktopImageFingerprintCacheStore cache,
        DesktopBatchProcessor batchProcessor)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _batchProcessor = batchProcessor ?? throw new ArgumentNullException(nameof(batchProcessor));
    }

    public void RefreshProcessingPolicy()
    {
        _cache.UpdateEntryLimits(
            DesktopImageProcessingPolicy.FingerprintTextCacheEntryLimit,
            DesktopImageProcessingPolicy.FingerprintDifferenceHashCacheEntryLimit);
    }

    public ExactDuplicateScanResult FindDuplicates(
        IReadOnlyList<ImageRecord> images,
        CancellationToken cancellationToken,
        IProgress<DesktopOperationProgress>? progress = null)
    {
        return FindDuplicates(images, cancellationToken, progress, persistCache: true);
    }

    internal ExactDuplicateScanResult FindDuplicates(
        IReadOnlyList<ImageRecord> images,
        CancellationToken cancellationToken,
        IProgress<DesktopOperationProgress>? progress,
        bool persistCache)
    {
        ArgumentNullException.ThrowIfNull(images);

        try
        {
            if (images.Count == 0)
            {
                return new ExactDuplicateScanResult(0, 0, []);
            }

            var sizeCounts = images
                .GroupBy(static image => image.SizeBytes)
                .ToDictionary(static group => group.Key, static group => group.Count());
            var duplicateSizeCandidates = images
                .Select(static (image, index) => (InputOrder: index, Record: image))
                .Where(item => sizeCounts[item.Record.SizeBytes] > 1)
                .ToArray();
            var totalWork = duplicateSizeCandidates.Length <= 0
                ? 1
                : Math.Max(1, duplicateSizeCandidates.Length * 2);
            var sampleBuckets = new ConcurrentDictionary<(long SizeBytes, string SampleHash), ConcurrentBag<(int InputOrder, ImageRecord Record)>>();
            var hashBuckets = new ConcurrentDictionary<(long SizeBytes, string Hash), ConcurrentBag<(int InputOrder, ImageRecord Record)>>();
            var failedCount = 0;

            if (duplicateSizeCandidates.Length == 0)
            {
                progress?.Report(new DesktopOperationProgress(totalWork, totalWork, "没有发现需要继续确认的重复候选。"));
                return new ExactDuplicateScanResult(images.Count, 0, []);
            }

            var completedSampleHashes = 0;
            var sampleExecutionPlan = CreateAdaptiveSampleHashExecutionPlan(
                duplicateSizeCandidates.Length,
                SumInputBytes(duplicateSizeCandidates.Select(static item => item.Record.SizeBytes)));
            var sampleBatchItems = duplicateSizeCandidates
                .Select(static item => new DesktopBatchItem<(int InputOrder, ImageRecord Record)>(item, item.Record.FileName))
                .ToArray();
            var sampleProgress = progress is null
                ? null
                : new Progress<DesktopBatchProgress>(update =>
                {
                    var completed = Math.Clamp(update.ProcessedCount, 0, duplicateSizeCandidates.Length);
                    progress.Report(new DesktopOperationProgress(
                        completed,
                        totalWork,
                        $"计算抽样指纹 {completed} / {duplicateSizeCandidates.Length} - {update.DisplayName}"));
                });
            var sampleResult = _batchProcessor.RunAsync(
                    sampleBatchItems,
                    (item, _, token) =>
                    {
                        var record = item.Value.Record;
                        var sampleHash = GetSampleHash(record, token);
                        var key = (record.SizeBytes, sampleHash);
                        var bucket = sampleBuckets.GetOrAdd(key, static _ => []);
                        bucket.Add(item.Value);
                        Interlocked.Increment(ref completedSampleHashes);
                        return Task.CompletedTask;
                    },
                    sampleProgress,
                    executionPlan: sampleExecutionPlan.ToBatchExecutionPlan(),
                    cancellationToken: cancellationToken)
                .GetAwaiter()
                .GetResult();
            failedCount += sampleResult.FailureCount;
            if (sampleResult.WasCanceled && cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            DesktopImageProcessingPolicy.TrimMemory();

            var fullHashCandidates = sampleBuckets
                .Where(static entry => entry.Value.Count > 1)
                .ToArray();

            foreach (var confirmedSampleBucket in fullHashCandidates.Where(static entry => UsesSampleHashAsFullHash(entry.Key.SizeBytes)))
            {
                var key = (confirmedSampleBucket.Key.SizeBytes, confirmedSampleBucket.Key.SampleHash);
                var bucket = hashBuckets.GetOrAdd(key, static _ => []);
                foreach (var item in confirmedSampleBucket.Value)
                {
                    bucket.Add(item);
                }
            }

            var deferredFullHashCandidates = fullHashCandidates
                .Where(static entry => !UsesSampleHashAsFullHash(entry.Key.SizeBytes))
                .SelectMany(static entry => entry.Value)
                .OrderBy(static item => item.InputOrder)
                .ToArray();

            if (deferredFullHashCandidates.Length > 0)
            {
                var completedFullHashes = 0;
                var fullExecutionPlan = CreateAdaptiveFullHashExecutionPlan(
                    deferredFullHashCandidates.Length,
                    SumInputBytes(deferredFullHashCandidates.Select(static item => item.Record.SizeBytes)));
                var fullBatchItems = deferredFullHashCandidates
                    .Select(static item => new DesktopBatchItem<(int InputOrder, ImageRecord Record)>(item, item.Record.FileName))
                    .ToArray();
                var fullProgress = progress is null
                    ? null
                    : new Progress<DesktopBatchProgress>(update =>
                    {
                        var completed = Math.Clamp(update.ProcessedCount, 0, deferredFullHashCandidates.Length);
                        progress.Report(new DesktopOperationProgress(
                            duplicateSizeCandidates.Length + completed,
                            totalWork,
                            $"确认重复内容 {completed} / {deferredFullHashCandidates.Length} - {update.DisplayName}"));
                    });
                var fullResult = _batchProcessor.RunAsync(
                        fullBatchItems,
                        (item, _, token) =>
                        {
                            var record = item.Value.Record;
                            var hash = GetFullHash(record, token);
                            var key = (record.SizeBytes, hash);
                            var bucket = hashBuckets.GetOrAdd(key, static _ => []);
                            bucket.Add(item.Value);
                            Interlocked.Increment(ref completedFullHashes);
                            return Task.CompletedTask;
                        },
                        fullProgress,
                        executionPlan: fullExecutionPlan.ToBatchExecutionPlan(),
                        cancellationToken: cancellationToken)
                    .GetAwaiter()
                    .GetResult();
                failedCount += fullResult.FailureCount;
                if (fullResult.WasCanceled && cancellationToken.IsCancellationRequested)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                DesktopImageProcessingPolicy.TrimMemory();
            }

            progress?.Report(new DesktopOperationProgress(totalWork, totalWork, "完全重复检查完成。"));

            var groups = hashBuckets
                .Where(static entry => entry.Value.Count > 1)
                .Select(entry =>
                {
                    var orderedItems = entry.Value
                        .OrderBy(static item => item.Record.ModifiedAt)
                        .ThenBy(static item => item.Record.FileName, PathComparison.NameComparer)
                        .ThenBy(static item => item.Record.FileName, StringComparer.Ordinal)
                        .ThenBy(static item => item.InputOrder)
                        .ToList();

                    return new
                    {
                        entry.Key.SizeBytes,
                        entry.Key.Hash,
                        FirstInputOrder = entry.Value.Min(static item => item.InputOrder),
                        Paths = orderedItems.Select(static item => item.Record.FullPath).ToArray()
                    };
                })
                .OrderBy(static group => group.FirstInputOrder)
                .Select(static group => new ExactDuplicateGroup(group.SizeBytes, group.Hash, group.Paths))
                .ToArray();

            return new ExactDuplicateScanResult(images.Count, failedCount, groups);
        }
        finally
        {
            if (persistCache)
            {
                _cache.Persist();
            }
        }
    }

    internal static int CalculateSampleHashParallelism(int totalCount, long totalInputBytes)
    {
        return DesktopAnalysisParallelismAdvisor.Calculate(
            DesktopAnalysisWorkloadKind.ExactDuplicateSampleHash,
            totalCount,
            totalInputBytes);
    }

    internal static DesktopAnalysisExecutionPlan CreateSampleHashExecutionPlan(int totalCount, long totalInputBytes)
    {
        return DesktopAnalysisParallelismAdvisor.CreateExecutionPlan(
            DesktopAnalysisWorkloadKind.ExactDuplicateSampleHash,
            totalCount,
            totalInputBytes);
    }

    internal static bool UsesSampleHashAsFullHash(long sizeBytes)
    {
        return sizeBytes <= SampleBlockSize * 3L;
    }

    internal static int CalculateFullHashParallelism(int totalCount, long totalInputBytes)
    {
        return DesktopAnalysisParallelismAdvisor.Calculate(
            DesktopAnalysisWorkloadKind.ExactDuplicateFullHash,
            totalCount,
            totalInputBytes);
    }

    internal static DesktopAnalysisExecutionPlan CreateFullHashExecutionPlan(int totalCount, long totalInputBytes)
    {
        return DesktopAnalysisParallelismAdvisor.CreateExecutionPlan(
            DesktopAnalysisWorkloadKind.ExactDuplicateFullHash,
            totalCount,
            totalInputBytes);
    }

    private static DesktopAnalysisExecutionPlan CreateAdaptiveSampleHashExecutionPlan(int totalCount, long totalInputBytes)
    {
        return DesktopAnalysisParallelismAdvisor.CreateAdaptiveExecutionPlan(
            DesktopAnalysisWorkloadKind.ExactDuplicateSampleHash,
            totalCount,
            totalInputBytes);
    }

    private static DesktopAnalysisExecutionPlan CreateAdaptiveFullHashExecutionPlan(int totalCount, long totalInputBytes)
    {
        return DesktopAnalysisParallelismAdvisor.CreateAdaptiveExecutionPlan(
            DesktopAnalysisWorkloadKind.ExactDuplicateFullHash,
            totalCount,
            totalInputBytes);
    }

    private string GetFullHash(ImageRecord image, CancellationToken cancellationToken)
    {
        return _cache.GetOrAddText(
            DesktopImageFingerprintKind.FullHash,
            image.FullPath,
            image.SizeBytes,
            image.ModifiedAt.ToUniversalTime().Ticks,
            () => ComputeFileHash(image.FullPath, cancellationToken));
    }

    private string GetSampleHash(ImageRecord image, CancellationToken cancellationToken)
    {
        return _cache.GetOrAddText(
            DesktopImageFingerprintKind.SampleHash,
            image.FullPath,
            image.SizeBytes,
            image.ModifiedAt.ToUniversalTime().Ticks,
            () => ComputeSampleHash(image.FullPath, image.SizeBytes, cancellationToken));
    }

    internal static string ComputeFileHash(string path, CancellationToken cancellationToken)
    {
        using var stream = DesktopFileStreamFactory.OpenReadShared(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(HashReadBufferSize);

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead <= 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, bytesRead);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static string ComputeSampleHash(string path, long sizeBytes, CancellationToken cancellationToken)
    {
        if (UsesSampleHashAsFullHash(sizeBytes))
        {
            return ComputeFileHash(path, cancellationToken);
        }

        using var stream = DesktopFileStreamFactory.OpenReadShared(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var sizeBytesBuffer = BitConverter.GetBytes(sizeBytes);
        hash.AppendData(sizeBytesBuffer);

        var buffer = ArrayPool<byte>.Shared.Rent(SampleBlockSize);
        try
        {
            AppendSample(hash, stream, buffer, 0, cancellationToken);
            AppendSample(hash, stream, buffer, Math.Max(0, (stream.Length - SampleBlockSize) / 2), cancellationToken);
            AppendSample(hash, stream, buffer, Math.Max(0, stream.Length - SampleBlockSize), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void AppendSample(IncrementalHash hash, Stream stream, byte[] buffer, long offset, CancellationToken cancellationToken)
    {
        stream.Position = Math.Clamp(offset, 0, Math.Max(0, stream.Length - 1));
        var remaining = Math.Min(buffer.Length, stream.Length - stream.Position);
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
    }

    private static long SumInputBytes(IEnumerable<long> sizes)
    {
        var totalBytes = 0L;
        foreach (var size in sizes)
        {
            var normalizedSize = Math.Max(0L, size);
            if (long.MaxValue - totalBytes < normalizedSize)
            {
                return long.MaxValue;
            }

            totalBytes += normalizedSize;
        }

        return totalBytes;
    }
}



