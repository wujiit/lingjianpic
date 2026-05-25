using Avalonia.Threading;

namespace ModernImageViewer.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly DispatcherTimer _slideshowTimer = new();
    private readonly List<int> _slideshowShuffleQueue = [];
    private readonly Random _slideshowRandom = new();
    private bool _isSlideshowPlaying;
    private bool _isSlideshowShuffleEnabled;
    private bool _isSlideshowLoopEnabled = true;
    private double _slideshowIntervalSeconds = 4;

    public bool IsSlideshowPlaying
    {
        get => _isSlideshowPlaying;
        private set
        {
            if (!SetProperty(ref _isSlideshowPlaying, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SlideshowToggleText));
            OnPropertyChanged(nameof(SlideshowStatusText));
            OnPropertyChanged(nameof(PlaybackModeText));
        }
    }

    public bool IsSlideshowShuffleEnabled
    {
        get => _isSlideshowShuffleEnabled;
        set
        {
            if (!SetProperty(ref _isSlideshowShuffleEnabled, value))
            {
                return;
            }

            ResetSlideshowShuffleQueue();
            OnPropertyChanged(nameof(PlaybackModeText));
            OnPropertyChanged(nameof(SlideshowStatusText));
            ScheduleViewerSettingsSave();
        }
    }

    public bool IsSlideshowLoopEnabled
    {
        get => _isSlideshowLoopEnabled;
        set
        {
            if (!SetProperty(ref _isSlideshowLoopEnabled, value))
            {
                return;
            }

            OnPropertyChanged(nameof(PlaybackModeText));
            OnPropertyChanged(nameof(SlideshowStatusText));
            ScheduleViewerSettingsSave();
        }
    }

    public double SlideshowIntervalSeconds
    {
        get => _slideshowIntervalSeconds;
        set
        {
            var normalized = Math.Clamp(Math.Round(value), 2, 12);
            if (!SetProperty(ref _slideshowIntervalSeconds, normalized))
            {
                return;
            }

            _slideshowTimer.Interval = TimeSpan.FromSeconds(normalized);
            OnPropertyChanged(nameof(SlideshowIntervalText));
            OnPropertyChanged(nameof(SlideshowStatusText));
            OnPropertyChanged(nameof(PlaybackModeText));
            ScheduleViewerSettingsSave();
        }
    }

    public bool CanStartSlideshow => Images.Count > 1 && !IsExportProcessing;

    public bool CanStepSlideshow => Images.Count > 1 && !IsExportProcessing;

    public string SlideshowToggleText => IsSlideshowPlaying ? "暂停" : "播放";

    public string SlideshowIntervalText => $"{SlideshowIntervalSeconds:0} 秒";

    public string PlaybackModeText
    {
        get
        {
            var modes = new List<string>
            {
                IsSlideshowShuffleEnabled ? "随机" : "顺序",
                IsSlideshowLoopEnabled ? "循环" : "到末尾停止"
            };

            return string.Join(" / ", modes);
        }
    }

    public string SlideshowStatusText => Images.Count == 0
        ? "载入图片后即可使用幻灯片"
        : Images.Count == 1
            ? "至少需要 2 张图片才能自动播放"
            : IsSlideshowPlaying
                ? $"正在以每 {SlideshowIntervalSeconds:0} 秒播放，模式：{PlaybackModeText}"
                : $"已暂停，间隔 {SlideshowIntervalSeconds:0} 秒，模式：{PlaybackModeText}";

    public void ToggleSlideshow()
    {
        if (IsSlideshowPlaying)
        {
            StopSlideshow("幻灯片已暂停");
            return;
        }

        StartSlideshow();
    }

    public void ShowPreviousSlide()
    {
        if (!CanStepSlideshow)
        {
            OperationStatusText = Images.Count == 0
                ? "请先载入图片后再切换"
                : "当前列表至少需要 2 张图。";
            return;
        }

        MoveSelectionByDelta(-1);
        RestartSlideshowTimerIfPlaying();

    }

    public void ShowNextSlide()
    {
        if (!CanStepSlideshow)
        {
            OperationStatusText = Images.Count == 0
                ? "请先载入图片后再切换"
                : "当前列表至少需要 2 张图。";
            return;
        }

        MoveSelectionByDelta(1);
        RestartSlideshowTimerIfPlaying();

    }

    public void ShowFirstImage()
    {
        if (Images.Count == 0)
        {
            OperationStatusText = "请先载入图片后再切换";
            return;
        }

        SelectedImage = Images[0];
        RestartSlideshowTimerIfPlaying();
    }

    public void ShowLastImage()
    {
        if (Images.Count == 0)
        {
            OperationStatusText = "请先载入图片后再切换";
            return;
        }

        SelectedImage = Images[^1];
        RestartSlideshowTimerIfPlaying();
    }

    private void InitializeSlideshowSettings()
    {
        _slideshowTimer.Interval = TimeSpan.FromSeconds(_slideshowIntervalSeconds);
        _slideshowTimer.Tick += SlideshowTimer_OnTick;
        NotifySlideshowStateChanged();
    }

    private void DisposeSlideshowTimer()
    {
        _slideshowTimer.Stop();
        _slideshowTimer.Tick -= SlideshowTimer_OnTick;
        _slideshowShuffleQueue.Clear();
    }

    private void StartSlideshow()
    {
        if (!CanStartSlideshow)
        {
            OperationStatusText = Images.Count == 0
                ? "请先打开一组图片"
                : "至少载入两张图片后才能开始幻灯片";
            return;
        }

        if (SelectedImage is null)
        {
            SelectedImage = Images[0];
        }

        ResetSlideshowShuffleQueue();
        _slideshowTimer.Interval = TimeSpan.FromSeconds(SlideshowIntervalSeconds);
        _slideshowTimer.Start();
        IsSlideshowPlaying = true;
        OperationStatusText = $"幻灯片已开始，间隔 {SlideshowIntervalSeconds:0} 秒。";
    }

    private void StopSlideshow(string? statusMessage = null)
    {
        _slideshowTimer.Stop();
        _slideshowShuffleQueue.Clear();
        var wasPlaying = IsSlideshowPlaying;
        IsSlideshowPlaying = false;

        if (!string.IsNullOrWhiteSpace(statusMessage))
        {
            OperationStatusText = statusMessage;
        }
        else if (wasPlaying && SelectedImage is not null)
        {
            OperationStatusText = $"{SelectedImage.FileName} 已就绪。";
        }
    }

    private void SlideshowTimer_OnTick(object? sender, EventArgs e)
    {
        if (!CanStartSlideshow)
        {
            StopSlideshow();
            return;
        }

        if (!AdvanceSlideshow())
        {
            StopSlideshow("幻灯片已播放完成");
        }
    }

    private bool AdvanceSlideshow()
    {
        if (IsSlideshowShuffleEnabled)
        {
            return AdvanceShuffleSlideshow();
        }

        var currentIndex = GetCurrentImageIndex();
        if (currentIndex < 0)
        {
            SelectedImage = Images[0];
            return true;
        }

        if (currentIndex >= Images.Count - 1)
        {
            if (!IsSlideshowLoopEnabled)
            {
                return false;
            }

            SelectedImage = Images[0];
            return true;
        }

        SelectedImage = Images[currentIndex + 1];
        return true;
    }

    private bool AdvanceShuffleSlideshow()
    {
        if (_slideshowShuffleQueue.Count == 0)
        {
            if (!IsSlideshowLoopEnabled)
            {
                return false;
            }

            ResetSlideshowShuffleQueue();
            if (_slideshowShuffleQueue.Count == 0)
            {
                return false;
            }
        }

        var nextIndex = _slideshowShuffleQueue[0];
        _slideshowShuffleQueue.RemoveAt(0);
        SelectedImage = Images[nextIndex];
        return true;
    }

    private void MoveSelectionByDelta(int delta)
    {
        if (Images.Count == 0)
        {
            SelectedImage = null;
            return;
        }

        var currentIndex = GetCurrentImageIndex();
        if (currentIndex < 0)
        {
            SelectedImage = Images[0];
        }
        else
        {
            var nextIndex = (currentIndex + delta + Images.Count) % Images.Count;
            SelectedImage = Images[nextIndex];
        }

        if (IsSlideshowShuffleEnabled)
        {
            ResetSlideshowShuffleQueue();
        }
    }

    private void ResetSlideshowShuffleQueue()
    {
        _slideshowShuffleQueue.Clear();
        if (!IsSlideshowShuffleEnabled || Images.Count <= 1)
        {
            return;
        }

        var currentIndex = GetCurrentImageIndex();
        var available = Enumerable.Range(0, Images.Count)
            .Where(index => index != currentIndex)
            .OrderBy(_ => _slideshowRandom.Next())
            .ToList();

        _slideshowShuffleQueue.AddRange(available);
    }

    private int GetCurrentImageIndex()
    {
        if (SelectedImage is null)
        {
            return -1;
        }

        return Images.IndexOf(SelectedImage);
    }

    private void RestartSlideshowTimerIfPlaying()
    {
        if (!IsSlideshowPlaying)
        {
            return;
        }

        _slideshowTimer.Stop();
        _slideshowTimer.Start();
    }

    private void OnSlideshowCollectionChanged()
    {
        if (Images.Count <= 1)
        {
            if (IsSlideshowPlaying)
            {
                var statusMessage = Images.Count == 0
                    ? "当前列表已清空，幻灯片已停止"
                    : "当前列表不足 2 张，幻灯片已停止";
                StopSlideshow(statusMessage);
                return;
            }

            _slideshowShuffleQueue.Clear();
            NotifySlideshowStateChanged();
            return;
        }

        if (IsSlideshowShuffleEnabled)
        {
            ResetSlideshowShuffleQueue();
        }

        NotifySlideshowStateChanged();
    }

    private void ResetSlideshowForCollectionReload()
    {
        _slideshowTimer.Stop();
        _slideshowShuffleQueue.Clear();
        IsSlideshowPlaying = false;
        NotifySlideshowStateChanged();
    }

    private void NotifySlideshowStateChanged()
    {
        OnPropertyChanged(nameof(CanStartSlideshow));
        OnPropertyChanged(nameof(CanStepSlideshow));
        OnPropertyChanged(nameof(SlideshowToggleText));
        OnPropertyChanged(nameof(SlideshowIntervalText));
        OnPropertyChanged(nameof(PlaybackModeText));
        OnPropertyChanged(nameof(SlideshowStatusText));
    }
}

