using ModernImageViewer.Core;
using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Tests;

public sealed class DesktopExactDuplicateImageServiceTests
{
    [Fact]
    public void FindDuplicates_confirms_full_hash_after_matching_samples()
    {
        using var paths = TestPaths.Create();
        var firstPath = paths.Combine("first.bin");
        var secondPath = paths.Combine("second.bin");
        var differentPath = paths.Combine("different.bin");

        var bytes = CreatePatternBytes(512 * 1024);
        File.WriteAllBytes(firstPath, bytes);
        File.WriteAllBytes(secondPath, bytes);

        var differentBytes = bytes.ToArray();
        differentBytes[150_000] ^= 0x7F;
        File.WriteAllBytes(differentPath, differentBytes);

        var records = new[]
        {
            CreateRecord(firstPath),
            CreateRecord(secondPath),
            CreateRecord(differentPath)
        };

        var result = new DesktopExactDuplicateImageService().FindDuplicates(records, CancellationToken.None);

        var group = Assert.Single(result.Groups);
        Assert.Equal(2, group.Paths.Count);
        Assert.Contains(firstPath, group.Paths);
        Assert.Contains(secondPath, group.Paths);
        Assert.DoesNotContain(differentPath, group.Paths);
    }

    [Fact]
    public void FindDuplicates_returns_empty_when_all_file_sizes_are_unique()
    {
        using var paths = TestPaths.Create();
        var firstPath = paths.Combine("first.bin");
        var secondPath = paths.Combine("second.bin");
        var thirdPath = paths.Combine("third.bin");

        File.WriteAllBytes(firstPath, CreatePatternBytes(64 * 1024));
        File.WriteAllBytes(secondPath, CreatePatternBytes(96 * 1024));
        File.WriteAllBytes(thirdPath, CreatePatternBytes(128 * 1024));

        var records = new[]
        {
            CreateRecord(firstPath),
            CreateRecord(secondPath),
            CreateRecord(thirdPath)
        };

        var result = new DesktopExactDuplicateImageService().FindDuplicates(records, CancellationToken.None);

        Assert.Equal(3, result.ScannedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Empty(result.Groups);
    }

    [Fact]
    public void FindDuplicates_skips_redundant_full_hash_entries_for_small_files()
    {
        using var paths = TestPaths.Create();
        var firstPath = paths.Combine("first-small.bin");
        var secondPath = paths.Combine("second-small.bin");
        var differentPath = paths.Combine("different-small.bin");

        var duplicateBytes = CreatePatternBytes(192 * 1024);
        File.WriteAllBytes(firstPath, duplicateBytes);
        File.WriteAllBytes(secondPath, duplicateBytes);

        var differentBytes = duplicateBytes.ToArray();
        differentBytes[42_000] ^= 0x5A;
        File.WriteAllBytes(differentPath, differentBytes);

        var records = new[]
        {
            CreateRecord(firstPath),
            CreateRecord(secondPath),
            CreateRecord(differentPath)
        };

        var cache = new DesktopImageFingerprintCacheStore(stateStore: null, maxTextEntries: 16, maxDifferenceHashEntries: 16);
        var service = new DesktopExactDuplicateImageService(cache);

        var result = service.FindDuplicates(records, CancellationToken.None);
        var counts = cache.GetEntryCounts();

        var group = Assert.Single(result.Groups);
        Assert.Equal(2, group.Paths.Count);
        Assert.Equal(3, counts.TextEntryCount);
    }

    [Theory]
    [InlineData(128 * 1024L, true)]
    [InlineData(384 * 1024L, true)]
    [InlineData(384 * 1024L + 1, false)]
    public void UsesSampleHashAsFullHash_matches_small_file_threshold(long sizeBytes, bool expected)
    {
        Assert.Equal(expected, DesktopExactDuplicateImageService.UsesSampleHashAsFullHash(sizeBytes));
    }

    [Fact]
    public void CalculateSampleHashParallelism_keeps_more_workers_for_sampling_than_full_hash()
    {
        var sampleParallelism = DesktopExactDuplicateImageService.CalculateSampleHashParallelism(
            totalCount: 12,
            totalInputBytes: 12L * 180L * 1024L * 1024L);
        var fullParallelism = DesktopExactDuplicateImageService.CalculateFullHashParallelism(
            totalCount: 12,
            totalInputBytes: 12L * 180L * 1024L * 1024L);

        Assert.True(sampleParallelism > fullParallelism);
        Assert.Equal(1, fullParallelism);
    }

    [Fact]
    public void CreateExecutionPlans_keep_sample_hash_looser_than_full_hash()
    {
        var samplePlan = DesktopExactDuplicateImageService.CreateSampleHashExecutionPlan(
            totalCount: 12,
            totalInputBytes: 12L * 180L * 1024L * 1024L);
        var fullPlan = DesktopExactDuplicateImageService.CreateFullHashExecutionPlan(
            totalCount: 12,
            totalInputBytes: 12L * 180L * 1024L * 1024L);

        Assert.True(samplePlan.MaxDegreeOfParallelism > fullPlan.MaxDegreeOfParallelism);
        Assert.True(samplePlan.ProgressInterval > fullPlan.ProgressInterval);
        Assert.True(samplePlan.MemoryTrimInterval > fullPlan.MemoryTrimInterval);
    }

    [Fact]
    public void ComputeFileHash_throws_when_cancellation_is_already_requested()
    {
        using var paths = TestPaths.Create();
        var path = paths.Combine("hash.bin");
        File.WriteAllBytes(path, CreatePatternBytes(256 * 1024));
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            DesktopExactDuplicateImageService.ComputeFileHash(path, cancellationSource.Token));
    }

    [Fact]
    public void ComputeSampleHash_throws_when_cancellation_is_already_requested()
    {
        using var paths = TestPaths.Create();
        var path = paths.Combine("sample.bin");
        File.WriteAllBytes(path, CreatePatternBytes(512 * 1024));
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            DesktopExactDuplicateImageService.ComputeSampleHash(path, 512 * 1024L, cancellationSource.Token));
    }

    [Fact]
    public void FindDuplicates_internal_scan_can_skip_cache_persist()
    {
        using var paths = TestPaths.Create();
        var firstPath = paths.Combine("first.bin");
        var secondPath = paths.Combine("second.bin");
        var textCachePath = paths.Combine("fingerprint-text-cache.json");
        var differenceCachePath = paths.Combine("fingerprint-dhash-cache.json");

        var bytes = CreatePatternBytes(512 * 1024);
        File.WriteAllBytes(firstPath, bytes);
        File.WriteAllBytes(secondPath, bytes);

        var records = new[]
        {
            CreateRecord(firstPath),
            CreateRecord(secondPath)
        };

        var cache = new DesktopImageFingerprintCacheStore(
            new DesktopImageFingerprintStateStore(textCachePath, differenceCachePath),
            maxTextEntries: 16,
            maxDifferenceHashEntries: 16);
        var service = new DesktopExactDuplicateImageService(cache);

        var result = service.FindDuplicates(records, CancellationToken.None, progress: null, persistCache: false);

        Assert.Single(result.Groups);
        Assert.False(File.Exists(textCachePath));
        Assert.False(File.Exists(differenceCachePath));
    }

    private static ImageRecord CreateRecord(string path)
    {
        var info = new FileInfo(path);
        return new ImageRecord(path, Path.GetFileName(path), info.Length, info.LastWriteTimeUtc);
    }

    private static byte[] CreatePatternBytes(int length)
    {
        var bytes = new byte[length];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)((index * 31 + 17) % 251);
        }

        return bytes;
    }
}
