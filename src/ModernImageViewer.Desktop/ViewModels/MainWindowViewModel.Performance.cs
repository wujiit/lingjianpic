using System.Collections.ObjectModel;
using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly ReadOnlyCollection<ProcessingPerformanceModeOption> _processingPerformanceModeOptions =
        new([
            new ProcessingPerformanceModeOption(
                DesktopProcessingPerformanceMode.Quiet,
                "\u5B89\u9759",
                "\u66F4\u7701 CPU \u548C\u5185\u5B58\uFF0C\u9002\u5408\u8F7B\u8584\u672C\u6216\u540E\u53F0\u6279\u91CF\u5904\u7406\u3002"),
            new ProcessingPerformanceModeOption(
                DesktopProcessingPerformanceMode.Balanced,
                "\u5E73\u8861",
                "\u9ED8\u8BA4\u63A8\u8350\uFF0C\u9884\u89C8\u3001\u6279\u91CF\u5904\u7406\u548C\u8D44\u6E90\u5360\u7528\u66F4\u5747\u8861\u3002"),
            new ProcessingPerformanceModeOption(
                DesktopProcessingPerformanceMode.HighPerformance,
                "\u9AD8\u6027\u80FD",
                "\u66F4\u79EF\u6781\u4F7F\u7528\u591A\u6838\uFF0C\u9002\u5408\u6279\u91CF\u538B\u7F29\u3001\u67E5\u91CD\u548C\u5927\u56FE\u6574\u7406\u3002")
        ]);

    private ProcessingPerformanceModeOption? _selectedProcessingPerformanceModeOption;

    public IReadOnlyList<ProcessingPerformanceModeOption> ProcessingPerformanceModeOptions => _processingPerformanceModeOptions;

    public ProcessingPerformanceModeOption? SelectedProcessingPerformanceModeOption
    {
        get => _selectedProcessingPerformanceModeOption;
        set
        {
            if (!SetProperty(ref _selectedProcessingPerformanceModeOption, value))
            {
                return;
            }

            ApplySelectedProcessingPerformanceMode(scheduleSave: true);
        }
    }

    public string ProcessingPerformanceModeSummaryText
    {
        get
        {
            var option = _selectedProcessingPerformanceModeOption ?? _processingPerformanceModeOptions[1];
            var previewCacheMegabytes = Math.Max(1, DesktopImageProcessingPolicy.PreviewCacheLimitBytes / (1024L * 1024L));
            var previewDiskCacheMegabytes = Math.Max(1, DesktopImageProcessingPolicy.PreviewDiskCacheLimitBytes / (1024L * 1024L));
            var thumbnailDiskCacheMegabytes = Math.Max(1, DesktopImageProcessingPolicy.ThumbnailDiskCacheLimitBytes / (1024L * 1024L));
            return string.Format(
                "{0} \u5F53\u524D\u56FE\u50CF\u7EBF\u7A0B\u4E0A\u9650 {1}\uFF0C\u9884\u89C8\u5185\u5B58\u7F13\u5B58\u7EA6 {2} MB\uFF0C\u672C\u5730\u9884\u89C8\u7F13\u5B58\u7EA6 {3} MB\uFF0C\u7F29\u7565\u56FE\u78C1\u76D8\u7F13\u5B58\u7EA6 {4} MB\u3002",
                option.Description,
                DesktopImageProcessingPolicy.ThreadLimit,
                previewCacheMegabytes,
                previewDiskCacheMegabytes,
                thumbnailDiskCacheMegabytes);
        }
    }

    private void InitializePerformanceSettings()
    {
        _selectedProcessingPerformanceModeOption = _processingPerformanceModeOptions
            .FirstOrDefault(option => option.Value == DesktopImageProcessingPolicy.CurrentPerformanceMode)
            ?? _processingPerformanceModeOptions[1];
        ApplySelectedProcessingPerformanceMode(
            scheduleSave: false,
            previewCacheMaintenanceMode: PreviewCacheMaintenanceMode.None);
    }

    private void ApplySelectedProcessingPerformanceMode(
        bool scheduleSave,
        PreviewCacheMaintenanceMode previewCacheMaintenanceMode = PreviewCacheMaintenanceMode.Background)
    {
        var mode = _selectedProcessingPerformanceModeOption?.Value ?? DesktopProcessingPerformanceMode.Balanced;
        DesktopImageProcessingPolicy.ApplyPerformanceMode(mode);
        _previewImageService.RefreshProcessingPolicy(previewCacheMaintenanceMode);
        _similarImageService.RefreshProcessingPolicy();
        _exactDuplicateImageService.RefreshProcessingPolicy();

        OnPropertyChanged(nameof(ProcessingPerformanceModeSummaryText));
        OnPropertyChanged(nameof(BatchPerformanceGuardText));

        if (scheduleSave)
        {
            ScheduleViewerSettingsSave();
        }
    }
}
