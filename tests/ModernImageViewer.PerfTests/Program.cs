using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ModernImageViewer.Core;
using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.PerfTests;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };
    private static readonly Assembly DesktopServicesAssembly = typeof(PreviewImageService).Assembly;
    private static readonly MethodInfo DesktopImageSourceInfoReadMethod =
        (DesktopServicesAssembly.GetType("ModernImageViewer.Desktop.Services.DesktopImageSourceInfoReader", throwOnError: true)
         ?? throw new TypeLoadException("Unable to load DesktopImageSourceInfoReader type."))
        .GetMethod("Read", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException("DesktopImageSourceInfoReader", "Read");
    private static readonly object DesktopDimensionCacheStoreShared =
        ((DesktopServicesAssembly.GetType("ModernImageViewer.Desktop.Services.DesktopImageDimensionCacheStore", throwOnError: true)
          ?? throw new TypeLoadException("Unable to load DesktopImageDimensionCacheStore type."))
         .GetProperty("Shared", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
         ?? throw new MissingMemberException("DesktopImageDimensionCacheStore", "Shared"))
        .GetValue(null)
        ?? throw new InvalidOperationException("DesktopImageDimensionCacheStore.Shared returned null.");
    private static readonly MethodInfo PreviewLoadCacheEntryMethod =
        typeof(PreviewImageService).GetMethod("LoadCacheEntry", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(typeof(PreviewImageService).FullName, "LoadCacheEntry");
    private static readonly object PreviewLoadModeValue =
        Enum.Parse(
            typeof(PreviewImageService).GetNestedType("PreviewImageLoadMode", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(typeof(PreviewImageService).FullName, "PreviewImageLoadMode"),
            "Preview");
    private static readonly PropertyInfo PreviewCacheHasBitmapProperty =
        PreviewLoadCacheEntryMethod.ReturnType.GetProperty("HasBitmap")
        ?? throw new MissingMemberException(PreviewLoadCacheEntryMethod.ReturnType.FullName, "HasBitmap");
    private static readonly PropertyInfo PreviewCacheSizeBytesProperty =
        PreviewLoadCacheEntryMethod.ReturnType.GetProperty("SizeBytes")
        ?? throw new MissingMemberException(PreviewLoadCacheEntryMethod.ReturnType.FullName, "SizeBytes");

    private static readonly IReadOnlyList<PerfScenarioDefinition> ScenarioDefinitions =
    [
        new(
            "scan-100",
            "扫描 100 张常规图片目录",
            static (dataset, cancellationToken) => RunCollectionScanScenario(
                dataset.Scan100Path,
                expectedCount: 100,
                includeSubfolders: false,
                cancellationToken)),
        new(
            "scan-1000",
            "扫描 1000 张常规图片目录",
            static (dataset, cancellationToken) => RunCollectionScanScenario(
                dataset.Scan1000Path,
                expectedCount: 1000,
                includeSubfolders: false,
                cancellationToken)),
        new(
            "scan-5000-subfolders",
            "扫描 5000 张分层目录图片集合",
            static (dataset, cancellationToken) => RunCollectionScanScenario(
                dataset.Scan5000Path,
                expectedCount: 5000,
                includeSubfolders: true,
                cancellationToken)),
        new(
            "preview-large-sequence",
            "切换大图预览序列",
            static (dataset, cancellationToken) => RunPreviewScenario(
                dataset.PreviewLargePath,
                previewLongEdge: 2560,
                replayBackward: true,
                cancellationToken)),
        new(
            "preview-long-image",
            "打开超长图预览",
            static (dataset, cancellationToken) => RunPreviewScenario(
                dataset.PreviewLongPath,
                previewLongEdge: 2200,
                replayBackward: false,
                cancellationToken)),
        new(
            "batch-export-100",
            "导出 100 张图片为 JPEG",
            static (dataset, cancellationToken) => RunBatchExportScenario(
                dataset.ExportSourcePath,
                "batch-export-100",
                new DesktopExportRequest(
                    ExportImageFormat.Jpeg,
                    LongEdgePixels: 2048,
                    JpegQuality: 90,
                    StripMetadata: true),
                cancellationToken)),
        new(
            "batch-compress-target-size-100",
            "压缩 100 张图片到目标体积",
            static (dataset, cancellationToken) => RunBatchExportScenario(
                dataset.ExportSourcePath,
                "batch-compress-target-size-100",
                new DesktopExportRequest(
                    ExportImageFormat.Jpeg,
                    LongEdgePixels: null,
                    JpegQuality: 88,
                    StripMetadata: true,
                    TargetFileSizeBytes: 240L * 1024L),
                cancellationToken)),
        new(
            "exact-duplicate-scan",
            "扫描完全重复图片",
            static (dataset, cancellationToken) => RunExactDuplicateScenario(
                dataset.ExactDuplicatePath,
                cancellationToken)),
        new(
            "similar-image-scan",
            "扫描相似图片",
            static (dataset, cancellationToken) => RunSimilarScenario(
                dataset.SimilarPath,
                distanceThreshold: 7,
                cancellationToken))
    ];

    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var arguments = PerfArguments.Parse(args);
            if (arguments.ListOnly)
            {
                PrintScenarioList();
                return 0;
            }

            return arguments.ScenarioName is null
                ? await RunCoordinatorAsync(arguments)
                : await RunScenarioWorkerAsync(arguments);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void PrintScenarioList()
    {
        foreach (var definition in ScenarioDefinitions)
        {
            Console.WriteLine($"{definition.Name} - {definition.Description}");
        }
    }

    private static async Task<int> RunCoordinatorAsync(PerfArguments arguments)
    {
        var repositoryRoot = RepositoryPaths.FindRoot();
        var artifactsRoot = Path.Combine(repositoryRoot, "artifacts", "perf");
        var runStamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var runRoot = Path.Combine(artifactsRoot, runStamp);
        var datasetRoot = Path.Combine(runRoot, "dataset");
        Directory.CreateDirectory(runRoot);

        Console.WriteLine($"[perf] 输出目录: {runRoot}");
        Console.WriteLine("[perf] 生成基准数据集...");
        var dataset = PerfDatasetFactory.Create(datasetRoot);

        var selectedScenarios = SelectScenarios(arguments.FilterNames);
        var results = new List<PerfScenarioResult>(selectedScenarios.Count);

        foreach (var definition in selectedScenarios)
        {
            Console.WriteLine($"[perf] 运行场景: {definition.Name}");
            var scenarioResult = await RunChildScenarioAsync(
                repositoryRoot,
                dataset.RootPath,
                definition,
                arguments.PerformanceMode,
                runRoot);
            results.Add(scenarioResult);
            Console.WriteLine(
                $"[perf] 完成 {definition.Name}: {scenarioResult.ElapsedMilliseconds} ms, 峰值内存 {FormatBytes(scenarioResult.PeakWorkingSetBytes)}");
        }

        var summary = new PerfRunSummary(
            new PerfEnvironmentInfo(
                DateTimeOffset.UtcNow,
                RuntimeInformation.OSDescription,
                RuntimeInformation.OSArchitecture.ToString(),
                RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.ProcessorCount,
                RuntimeInformation.FrameworkDescription,
                arguments.PerformanceMode.ToString()),
            dataset.Manifest,
            results);

        WriteRunReports(artifactsRoot, runRoot, summary);
        Console.WriteLine($"[perf] 基准报告已写入: {runRoot}");
        return 0;
    }

    private static async Task<int> RunScenarioWorkerAsync(PerfArguments arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments.ScenarioName)
            || string.IsNullOrWhiteSpace(arguments.DatasetRootPath)
            || string.IsNullOrWhiteSpace(arguments.OutputPath))
        {
            throw new InvalidOperationException("Scenario worker requires --scenario, --dataset-root and --output.");
        }

        var definition = FindScenario(arguments.ScenarioName);
        var dataset = PerfDatasetBundle.Load(arguments.DatasetRootPath);

        DesktopImageProcessingPolicy.Configure();
        DesktopImageProcessingPolicy.ApplyPerformanceMode(arguments.PerformanceMode);

        var process = Process.GetCurrentProcess();
        process.Refresh();

        var cpuStart = process.TotalProcessorTime;
        var allocatedBytesStart = GC.GetTotalAllocatedBytes(precise: true);
        var gen0Start = GC.CollectionCount(0);
        var gen1Start = GC.CollectionCount(1);
        var gen2Start = GC.CollectionCount(2);

        var stopwatch = Stopwatch.StartNew();
        var outcome = definition.Execute(dataset, CancellationToken.None);
        stopwatch.Stop();

        process.Refresh();
        var cpuTime = process.TotalProcessorTime - cpuStart;
        var averageCpuPercent = stopwatch.Elapsed.TotalMilliseconds <= 0
            ? 0
            : Math.Clamp(
                cpuTime.TotalMilliseconds / (stopwatch.Elapsed.TotalMilliseconds * Math.Max(1, Environment.ProcessorCount)) * 100.0,
                0,
                100);
        var throughput = stopwatch.Elapsed.TotalSeconds <= 0
            ? 0
            : outcome.OperationCount / stopwatch.Elapsed.TotalSeconds;

        var result = new PerfScenarioResult(
            definition.Name,
            definition.Description,
            outcome.OperationCount,
            outcome.OperationLabel,
            (long)Math.Ceiling(stopwatch.Elapsed.TotalMilliseconds),
            Math.Round(cpuTime.TotalMilliseconds, 2),
            Math.Round(averageCpuPercent, 2),
            Math.Round(throughput, 2),
            process.PeakWorkingSet64,
            process.WorkingSet64,
            GC.GetGCMemoryInfo().HeapSizeBytes,
            GC.GetTotalAllocatedBytes(precise: true) - allocatedBytesStart,
            GC.CollectionCount(0) - gen0Start,
            GC.CollectionCount(1) - gen1Start,
            GC.CollectionCount(2) - gen2Start,
            outcome.Metrics);

        Directory.CreateDirectory(Path.GetDirectoryName(arguments.OutputPath)!);
        await File.WriteAllTextAsync(arguments.OutputPath, JsonSerializer.Serialize(result, JsonOptions), Encoding.UTF8);
        Console.WriteLine($"{result.Name}: {result.ElapsedMilliseconds} ms");
        return 0;
    }

    private static IReadOnlyList<PerfScenarioDefinition> SelectScenarios(IReadOnlyCollection<string> filters)
    {
        if (filters.Count == 0)
        {
            return ScenarioDefinitions;
        }

        var selected = ScenarioDefinitions
            .Where(definition => filters.Contains(definition.Name, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        if (selected.Length == 0)
        {
            throw new InvalidOperationException($"No perf scenario matched filter: {string.Join(", ", filters)}");
        }

        return selected;
    }

    private static PerfScenarioDefinition FindScenario(string scenarioName)
    {
        return ScenarioDefinitions.FirstOrDefault(
                   definition => string.Equals(definition.Name, scenarioName, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException($"Unknown perf scenario: {scenarioName}");
    }

    private static async Task<PerfScenarioResult> RunChildScenarioAsync(
        string repositoryRoot,
        string datasetRoot,
        PerfScenarioDefinition definition,
        DesktopProcessingPerformanceMode performanceMode,
        string runRoot)
    {
        var scenarioOutputPath = Path.Combine(runRoot, $"{definition.Name}.json");
        var startInfo = CreateChildStartInfo(
            repositoryRoot,
            [
                "--scenario", definition.Name,
                "--dataset-root", datasetRoot,
                "--output", scenarioOutputPath,
                "--mode", performanceMode.ToString()
            ]);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Perf scenario '{definition.Name}' failed with exit code {process.ExitCode}.{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        }

        if (!File.Exists(scenarioOutputPath))
        {
            throw new FileNotFoundException($"Perf scenario '{definition.Name}' did not write its result file.", scenarioOutputPath);
        }

        var content = await File.ReadAllTextAsync(scenarioOutputPath, Encoding.UTF8);
        return JsonSerializer.Deserialize<PerfScenarioResult>(content, JsonOptions)
               ?? throw new InvalidOperationException($"Unable to deserialize perf result for scenario '{definition.Name}'.");
    }

    private static ProcessStartInfo CreateChildStartInfo(string workingDirectory, IReadOnlyList<string> args)
    {
        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        ProcessStartInfo startInfo;

        if (string.Equals(Path.GetExtension(assemblyPath), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(assemblyPath);
        }
        else
        {
            startInfo = new ProcessStartInfo(assemblyPath)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
        }

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        return startInfo;
    }

    private static PerfScenarioOutcome RunCollectionScanScenario(
        string folderPath,
        int expectedCount,
        bool includeSubfolders,
        CancellationToken cancellationToken)
    {
        var builder = new ImageCollectionBuilder();
        var result = builder.Build(new[] { folderPath }, SortMode.Name, includeSubfolders, cancellationToken);
        if (result.Images.Count != expectedCount)
        {
            throw new InvalidOperationException($"Expected {expectedCount} records but collected {result.Images.Count} from {folderPath}.");
        }

        return new PerfScenarioOutcome(
            result.Images.Count,
            "images",
            new Dictionary<string, string>
            {
                ["focusPath"] = result.FocusPath ?? string.Empty,
                ["sourceLabel"] = result.SourceLabel,
                ["includeSubfolders"] = includeSubfolders.ToString()
            });
    }

    private static PerfScenarioOutcome RunPreviewScenario(
        string folderPath,
        int previewLongEdge,
        bool replayBackward,
        CancellationToken cancellationToken)
    {
        var files = Directory.EnumerateFiles(folderPath)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0)
        {
            throw new InvalidOperationException($"Preview scenario folder is empty: {folderPath}");
        }

        var operations = 0;
        long totalEncodedPreviewBytes = 0;
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalEncodedPreviewBytes += LoadPreviewCacheEntrySize(path, previewLongEdge);
            operations++;
        }

        if (replayBackward)
        {
            foreach (var path in files.Reverse())
            {
                cancellationToken.ThrowIfCancellationRequested();
                totalEncodedPreviewBytes += LoadPreviewCacheEntrySize(path, previewLongEdge);
                operations++;
            }
        }

        return new PerfScenarioOutcome(
            operations,
            "preview-loads",
            new Dictionary<string, string>
            {
                ["fileCount"] = files.Length.ToString(),
                ["previewLongEdge"] = previewLongEdge.ToString(),
                ["replayBackward"] = replayBackward.ToString(),
                ["totalEncodedPreviewBytes"] = totalEncodedPreviewBytes.ToString()
            });
    }

    private static PerfScenarioOutcome RunBatchExportScenario(
        string sourceFolderPath,
        string exportFolderName,
        DesktopExportRequest request,
        CancellationToken cancellationToken)
    {
        var service = new DesktopImageExportService();
        var files = Directory.EnumerateFiles(sourceFolderPath)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0)
        {
            throw new InvalidOperationException($"Export scenario folder is empty: {sourceFolderPath}");
        }

        var targetFolderPath = Path.Combine(sourceFolderPath, "..", "outputs", exportFolderName);
        if (Directory.Exists(targetFolderPath))
        {
            Directory.Delete(targetFolderPath, recursive: true);
        }

        Directory.CreateDirectory(targetFolderPath);
        var operations = 0;
        long totalOutputBytes = 0;

        foreach (var sourcePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extension = service.GetFileExtension(sourcePath, request);
            var targetPath = Path.Combine(
                targetFolderPath,
                Path.GetFileNameWithoutExtension(sourcePath) + extension);
            service.Export(sourcePath, targetPath, request, cancellationToken);
            totalOutputBytes += new FileInfo(targetPath).Length;
            operations++;
        }

        return new PerfScenarioOutcome(
            operations,
            "exports",
            new Dictionary<string, string>
            {
                ["sourceCount"] = files.Length.ToString(),
                ["outputDirectory"] = Path.GetFullPath(targetFolderPath),
                ["totalOutputBytes"] = totalOutputBytes.ToString()
            });
    }

    private static PerfScenarioOutcome RunExactDuplicateScenario(string folderPath, CancellationToken cancellationToken)
    {
        var builder = new ImageCollectionBuilder();
        var records = builder.Build(new[] { folderPath }, SortMode.Name, includeSubfolders: false, cancellationToken).Images;
        var service = new DesktopExactDuplicateImageService();
        var result = service.FindDuplicates(records, cancellationToken);

        return new PerfScenarioOutcome(
            result.ScannedCount,
            "images",
            new Dictionary<string, string>
            {
                ["groupCount"] = result.GroupCount.ToString(),
                ["duplicateCount"] = result.DuplicateCount.ToString(),
                ["failedCount"] = result.FailedCount.ToString()
            });
    }

    private static PerfScenarioOutcome RunSimilarScenario(
        string folderPath,
        int distanceThreshold,
        CancellationToken cancellationToken)
    {
        var builder = new ImageCollectionBuilder();
        var records = builder.Build(new[] { folderPath }, SortMode.Name, includeSubfolders: false, cancellationToken).Images;
        var service = new DesktopSimilarImageService();
        var result = service.FindSimilarImages(records, distanceThreshold, cancellationToken);

        return new PerfScenarioOutcome(
            result.ScannedCount,
            "images",
            new Dictionary<string, string>
            {
                ["groupCount"] = result.GroupCount.ToString(),
                ["similarCount"] = result.SimilarCount.ToString(),
                ["distanceThreshold"] = result.DistanceThreshold.ToString(),
                ["failedCount"] = result.FailedCount.ToString()
            });
    }

    private static int LoadPreviewCacheEntrySize(string path, int previewLongEdge)
    {
        var sourceInfo = DesktopImageSourceInfoReadMethod.Invoke(null, [path, DesktopDimensionCacheStoreShared])
                         ?? throw new InvalidOperationException($"Unable to read source info for '{path}'.");
        var cacheEntry = PreviewLoadCacheEntryMethod.Invoke(null, [sourceInfo, previewLongEdge, PreviewLoadModeValue])
                         ?? throw new InvalidOperationException($"Preview cache entry was null for '{path}'.");
        var hasBitmap = (bool)(PreviewCacheHasBitmapProperty.GetValue(cacheEntry) ?? false);
        if (!hasBitmap)
        {
            throw new InvalidOperationException($"Preview cache entry did not contain encoded preview bytes for '{path}'.");
        }

        return (int)(PreviewCacheSizeBytesProperty.GetValue(cacheEntry) ?? 0);
    }

    private static void WriteRunReports(string artifactsRoot, string runRoot, PerfRunSummary summary)
    {
        Directory.CreateDirectory(artifactsRoot);
        var json = JsonSerializer.Serialize(summary, JsonOptions);
        File.WriteAllText(Path.Combine(runRoot, "summary.json"), json, Encoding.UTF8);
        File.WriteAllText(Path.Combine(artifactsRoot, "latest.json"), json, Encoding.UTF8);

        var csv = BuildCsv(summary);
        File.WriteAllText(Path.Combine(runRoot, "summary.csv"), csv, Encoding.UTF8);
        File.WriteAllText(Path.Combine(artifactsRoot, "latest.csv"), csv, Encoding.UTF8);

        var markdown = BuildMarkdown(summary, runRoot);
        File.WriteAllText(Path.Combine(runRoot, "summary.md"), markdown, Encoding.UTF8);
        File.WriteAllText(Path.Combine(artifactsRoot, "latest.md"), markdown, Encoding.UTF8);
    }

    private static string BuildCsv(PerfRunSummary summary)
    {
        var builder = new StringBuilder();
        builder.AppendLine("scenario,description,operation_count,operation_label,elapsed_ms,throughput_per_second,average_cpu_percent,peak_working_set_bytes,working_set_bytes,managed_heap_bytes,allocated_bytes,gen0,gen1,gen2,metrics");

        foreach (var result in summary.Results)
        {
            builder
                .Append(Csv(result.Name)).Append(',')
                .Append(Csv(result.Description)).Append(',')
                .Append(result.OperationCount).Append(',')
                .Append(Csv(result.OperationLabel)).Append(',')
                .Append(result.ElapsedMilliseconds).Append(',')
                .Append(result.ThroughputPerSecond).Append(',')
                .Append(result.AverageCpuPercent).Append(',')
                .Append(result.PeakWorkingSetBytes).Append(',')
                .Append(result.WorkingSetBytes).Append(',')
                .Append(result.ManagedHeapBytes).Append(',')
                .Append(result.AllocatedBytes).Append(',')
                .Append(result.Gen0Collections).Append(',')
                .Append(result.Gen1Collections).Append(',')
                .Append(result.Gen2Collections).Append(',')
                .Append(Csv(string.Join("; ", result.Metrics.Select(static pair => $"{pair.Key}={pair.Value}"))))
                .AppendLine();
        }

        return builder.ToString();
    }

    private static string BuildMarkdown(PerfRunSummary summary, string runRoot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# ModernImageViewer Perf Baseline");
        builder.AppendLine();
        builder.AppendLine($"- 生成时间: {summary.Environment.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        builder.AppendLine($"- 平台: {summary.Environment.OsDescription}");
        builder.AppendLine($"- 架构: {summary.Environment.OsArchitecture} / {summary.Environment.ProcessArchitecture}");
        builder.AppendLine($"- .NET: {summary.Environment.FrameworkDescription}");
        builder.AppendLine($"- CPU 核心: {summary.Environment.ProcessorCount}");
        builder.AppendLine($"- 性能模式: {summary.Environment.PerformanceMode}");
        builder.AppendLine($"- 数据集目录: `{summary.Dataset.RootPath}`");
        builder.AppendLine($"- 报告目录: `{runRoot}`");
        builder.AppendLine();
        builder.AppendLine("## 数据集");
        builder.AppendLine();
        builder.AppendLine("| 名称 | 图片数 | 说明 |");
        builder.AppendLine("| --- | ---: | --- |");
        foreach (var dataset in summary.Dataset.Datasets)
        {
            builder.AppendLine($"| {dataset.Name} | {dataset.FileCount} | {dataset.Description} |");
        }

        builder.AppendLine();
        builder.AppendLine("## 场景结果");
        builder.AppendLine();
        builder.AppendLine("| 场景 | 耗时(ms) | 吞吐 | 平均 CPU | 峰值内存 |");
        builder.AppendLine("| --- | ---: | ---: | ---: | ---: |");
        foreach (var result in summary.Results)
        {
            builder.AppendLine(
                $"| {result.Name} | {result.ElapsedMilliseconds} | {result.ThroughputPerSecond}/{result.OperationLabel}/s | {result.AverageCpuPercent}% | {FormatBytes(result.PeakWorkingSetBytes)} |");
        }

        builder.AppendLine();
        builder.AppendLine("## 细项");
        builder.AppendLine();
        foreach (var result in summary.Results)
        {
            builder.AppendLine($"### {result.Name}");
            builder.AppendLine();
            builder.AppendLine($"- 描述: {result.Description}");
            builder.AppendLine($"- 操作数: {result.OperationCount} {result.OperationLabel}");
            builder.AppendLine($"- 耗时: {result.ElapsedMilliseconds} ms");
            builder.AppendLine($"- 吞吐: {result.ThroughputPerSecond} {result.OperationLabel}/s");
            builder.AppendLine($"- 平均 CPU: {result.AverageCpuPercent}%");
            builder.AppendLine($"- 峰值工作集: {FormatBytes(result.PeakWorkingSetBytes)}");
            builder.AppendLine($"- 当前工作集: {FormatBytes(result.WorkingSetBytes)}");
            builder.AppendLine($"- 托管堆: {FormatBytes(result.ManagedHeapBytes)}");
            builder.AppendLine($"- 分配字节: {FormatBytes(result.AllocatedBytes)}");
            builder.AppendLine($"- GC: gen0={result.Gen0Collections}, gen1={result.Gen1Collections}, gen2={result.Gen2Collections}");

            foreach (var metric in result.Metrics)
            {
                builder.AppendLine($"- {metric.Key}: {metric.Value}");
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string Csv(string value)
    {
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }

    private sealed record PerfScenarioDefinition(
        string Name,
        string Description,
        Func<PerfDatasetBundle, CancellationToken, PerfScenarioOutcome> Execute);

    private sealed record PerfScenarioOutcome(
        int OperationCount,
        string OperationLabel,
        IReadOnlyDictionary<string, string> Metrics);

    private sealed record PerfScenarioResult(
        string Name,
        string Description,
        int OperationCount,
        string OperationLabel,
        long ElapsedMilliseconds,
        double CpuMilliseconds,
        double AverageCpuPercent,
        double ThroughputPerSecond,
        long PeakWorkingSetBytes,
        long WorkingSetBytes,
        long ManagedHeapBytes,
        long AllocatedBytes,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections,
        IReadOnlyDictionary<string, string> Metrics);

    private sealed record PerfRunSummary(
        PerfEnvironmentInfo Environment,
        PerfDatasetManifest Dataset,
        IReadOnlyList<PerfScenarioResult> Results);

    private sealed record PerfEnvironmentInfo(
        DateTimeOffset GeneratedAtUtc,
        string OsDescription,
        string OsArchitecture,
        string ProcessArchitecture,
        int ProcessorCount,
        string FrameworkDescription,
        string PerformanceMode);

    private sealed class PerfArguments
    {
        public string? ScenarioName { get; private set; }

        public string? DatasetRootPath { get; private set; }

        public string? OutputPath { get; private set; }

        public DesktopProcessingPerformanceMode PerformanceMode { get; private set; } = DesktopProcessingPerformanceMode.Balanced;

        public bool ListOnly { get; private set; }

        public IReadOnlyCollection<string> FilterNames { get; private set; } = Array.Empty<string>();

        public static PerfArguments Parse(string[] args)
        {
            var result = new PerfArguments();

            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--scenario":
                        result.ScenarioName = ReadValue(args, ref index);
                        break;
                    case "--dataset-root":
                        result.DatasetRootPath = ReadValue(args, ref index);
                        break;
                    case "--output":
                        result.OutputPath = ReadValue(args, ref index);
                        break;
                    case "--mode":
                        result.PerformanceMode = ParsePerformanceMode(ReadValue(args, ref index));
                        break;
                    case "--filter":
                        result.FilterNames = ReadValue(args, ref index)
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        break;
                    case "--list":
                        result.ListOnly = true;
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown argument: {args[index]}");
                }
            }

            return result;
        }

        private static string ReadValue(string[] args, ref int index)
        {
            if (index + 1 >= args.Length)
            {
                throw new InvalidOperationException($"Missing value for argument: {args[index]}");
            }

            index++;
            return args[index];
        }

        private static DesktopProcessingPerformanceMode ParsePerformanceMode(string raw)
        {
            if (Enum.TryParse<DesktopProcessingPerformanceMode>(raw, ignoreCase: true, out var mode))
            {
                return mode;
            }

            throw new InvalidOperationException($"Unknown performance mode: {raw}");
        }
    }

    private static class RepositoryPaths
    {
        public static string FindRoot()
        {
            var current = new DirectoryInfo(Environment.CurrentDirectory);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "ModernImageViewer.slnx")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException("Unable to locate ModernImageViewer repository root.");
        }
    }
}
