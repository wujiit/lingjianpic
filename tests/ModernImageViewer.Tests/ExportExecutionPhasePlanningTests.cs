using ModernImageViewer.Desktop.Services;
using ModernImageViewer.Desktop.ViewModels;

namespace ModernImageViewer.Tests;

public sealed class ExportExecutionPhasePlanningTests
{
    private const long Megabyte = 1024L * 1024L;

    [Fact]
    public void BuildExportExecutionPhases_all_copy_like_operations_share_single_passthrough_phase()
    {
        var operations = Enumerable.Range(0, 6)
            .Select(index => CreateOperation(
                index,
                sizeBytes: 4 * Megabyte,
                (index % 3) switch
                {
                    0 => DesktopPreparedExportMode.CopySource,
                    1 => DesktopPreparedExportMode.TargetSizeCopy,
                    _ => DesktopPreparedExportMode.StripMetadataWithoutReencode
                }))
            .ToArray();

        var phases = MainWindowViewModel.BuildExportExecutionPhases(
            operations,
            DesktopProcessingPerformanceMode.HighPerformance,
            threadLimit: 6,
            magickOperationLimit: 2);

        var phase = Assert.Single(phases);
        Assert.Equal(DesktopBatchWorkloadKind.ExportPassthrough, phase.WorkloadKind);
        Assert.Equal([0, 1, 2, 3, 4, 5], phase.StepOrdinals);
        Assert.Equal(6, phase.ExecutionPlan.MaxDegreeOfParallelism);
    }

    [Fact]
    public void BuildExportExecutionPhases_mixed_copy_and_target_size_reencode_split_into_fast_and_compression_phases()
    {
        var operations = new[]
        {
            CreateOperation(0, 3 * Megabyte, DesktopPreparedExportMode.CopySource),
            CreateOperation(1, 18 * Megabyte, DesktopPreparedExportMode.TargetSizeReencode, targetFileSizeBytes: 700 * 1024L),
            CreateOperation(2, 4 * Megabyte, DesktopPreparedExportMode.TargetSizeCopy, targetFileSizeBytes: 700 * 1024L),
            CreateOperation(3, 20 * Megabyte, DesktopPreparedExportMode.TargetSizeReencode, targetFileSizeBytes: 700 * 1024L),
            CreateOperation(4, 3 * Megabyte, DesktopPreparedExportMode.CopySource),
            CreateOperation(5, 22 * Megabyte, DesktopPreparedExportMode.TargetSizeReencode, targetFileSizeBytes: 700 * 1024L)
        };

        var phases = MainWindowViewModel.BuildExportExecutionPhases(
            operations,
            DesktopProcessingPerformanceMode.HighPerformance,
            threadLimit: 6,
            magickOperationLimit: 2);

        Assert.Equal(2, phases.Count);
        Assert.Equal(DesktopBatchWorkloadKind.ExportPassthrough, phases[0].WorkloadKind);
        Assert.Equal([0, 2, 4], phases[0].StepOrdinals);
        Assert.Equal(3, phases[0].ExecutionPlan.MaxDegreeOfParallelism);

        Assert.Equal(DesktopBatchWorkloadKind.ExportCompression, phases[1].WorkloadKind);
        Assert.Equal([1, 3, 5], phases[1].StepOrdinals);
        Assert.True(phases[1].ExecutionPlan.MaxDegreeOfParallelism < phases[0].ExecutionPlan.MaxDegreeOfParallelism);
    }

    [Fact]
    public void BuildExportExecutionPhases_orders_transcode_before_watermark_after_passthrough()
    {
        var operations = new[]
        {
            CreateOperation(0, 2 * Megabyte, DesktopPreparedExportMode.StripMetadataWithoutReencode),
            CreateOperation(1, 10 * Megabyte, DesktopPreparedExportMode.Reencode),
            CreateOperation(2, 12 * Megabyte, DesktopPreparedExportMode.Reencode, hasWatermark: true),
            CreateOperation(3, 8 * Megabyte, DesktopPreparedExportMode.Reencode)
        };

        var phases = MainWindowViewModel.BuildExportExecutionPhases(
            operations,
            DesktopProcessingPerformanceMode.Balanced,
            threadLimit: 6,
            magickOperationLimit: 2);

        Assert.Equal(3, phases.Count);
        Assert.Equal(DesktopBatchWorkloadKind.ExportPassthrough, phases[0].WorkloadKind);
        Assert.Equal([0], phases[0].StepOrdinals);
        Assert.Equal(DesktopBatchWorkloadKind.ExportTranscode, phases[1].WorkloadKind);
        Assert.Equal([1, 3], phases[1].StepOrdinals);
        Assert.Equal(DesktopBatchWorkloadKind.ExportWatermark, phases[2].WorkloadKind);
        Assert.Equal([2], phases[2].StepOrdinals);
    }

    [Fact]
    public void ResolveExportWorkloadKind_target_size_request_without_binary_search_mode_stays_compression()
    {
        var operation = CreateOperation(
            index: 0,
            sizeBytes: 9 * Megabyte,
            mode: DesktopPreparedExportMode.Reencode,
            targetFileSizeBytes: 512 * 1024L);

        var actual = MainWindowViewModel.ResolveExportWorkloadKind(operation);

        Assert.Equal(DesktopBatchWorkloadKind.ExportCompression, actual);
    }

    private static DesktopPreparedExportOperation CreateOperation(
        int index,
        long sizeBytes,
        DesktopPreparedExportMode mode,
        bool hasWatermark = false,
        long? targetFileSizeBytes = null)
    {
        var path = $@"E:\temp\phase-{index}.jpg";
        var request = new DesktopExportRequest(
            mode is DesktopPreparedExportMode.CopySource
                or DesktopPreparedExportMode.TargetSizeCopy
                or DesktopPreparedExportMode.StripMetadataWithoutReencode
                    ? ExportImageFormat.Original
                    : ExportImageFormat.Jpeg,
            LongEdgePixels: null,
            JpegQuality: 92,
            StripMetadata: mode == DesktopPreparedExportMode.StripMetadataWithoutReencode,
            PreserveEncodedData: mode == DesktopPreparedExportMode.StripMetadataWithoutReencode,
            TargetFileSizeBytes: targetFileSizeBytes,
            Watermark: hasWatermark ? CreateWatermarkRequest() : null);

        return new DesktopPreparedExportOperation(
            path,
            new DesktopImageSourceInfo(
                new DesktopFileSignature(path, sizeBytes, SourceStampTicks: 1),
                Dimensions: null,
                Exists: true),
            request,
            request.Format == ExportImageFormat.Original ? ExportImageFormat.Original : ExportImageFormat.Jpeg,
            ".jpg",
            mode);
    }

    private static DesktopWatermarkRequest CreateWatermarkRequest()
    {
        return new DesktopWatermarkRequest(
            DesktopWatermarkKind.Text,
            DesktopWatermarkPlacement.BottomRight,
            "LingJian",
            string.Empty,
            OpacityPercent: 45,
            MarginPixels: 32,
            TextPointSize: 42,
            TextColor: "#FFFFFF",
            ImageScalePercent: 18);
    }
}
