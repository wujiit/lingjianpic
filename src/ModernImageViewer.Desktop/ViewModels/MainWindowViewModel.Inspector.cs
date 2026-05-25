using System.Collections.ObjectModel;
using ModernImageViewer.Core;
using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const string DefaultInspectorMetadataHint = "\u8f7d\u5165\u56fe\u7247\u540e\u53ef\u67e5\u770b\u62cd\u6444\u4fe1\u606f\u3002";
    private const string LoadingInspectorMetadataHint = "\u6b63\u5728\u8bfb\u53d6\u62cd\u6444\u4fe1\u606f...";
    private const string EmptyInspectorMetadataHint = "\u8fd9\u5f20\u56fe\u7247\u6ca1\u6709\u62cd\u6444\u4fe1\u606f\u3002";
    private const string FailedInspectorMetadataHint = "\u62cd\u6444\u4fe1\u606f\u8bfb\u53d6\u5931\u8d25\u3002";

    private readonly DesktopImageMetadataService _imageMetadataService = new();
    private readonly ObservableCollection<InspectorMetadataItemViewModel> _selectedExifItems = [];
    private CancellationTokenSource? _inspectorMetadataLoadCts;
    private DesktopImageDimensions? _selectedOriginalDimensions;
    private string _selectedExifStatusText = DefaultInspectorMetadataHint;

    public string SelectedFolderText => SelectedImage is null
        ? "--"
        : Path.GetDirectoryName(SelectedImage.FullPath) ?? "--";

    public string SelectedFormatText => SelectedImage is null
        ? "--"
        : Path.GetExtension(SelectedImage.FileName).TrimStart('.').ToUpperInvariant();

    public string SelectedDimensionsText => _selectedOriginalDimensions is DesktopImageDimensions dimensions
        ? $"{dimensions.Width} x {dimensions.Height}"
        : SelectedPreviewBitmap is null
            ? "--"
            : $"{SelectedPreviewBitmap.PixelSize.Width} x {SelectedPreviewBitmap.PixelSize.Height}";

    public string InspectorModeText => ShowCompareViewerSurface
        ? "\u5bf9\u6bd4\u6a21\u5f0f"
        : ShowContactSheetViewer
            ? "\u8054\u7cfb\u8868\u603b\u89c8"
            : "\u5355\u56fe\u9884\u89c8";

    public ObservableCollection<InspectorMetadataItemViewModel> SelectedExifItems => _selectedExifItems;

    public bool HasSelectedExifItems => _selectedExifItems.Count > 0;

    public bool ShowSelectedExifHint => !HasSelectedExifItems && !string.IsNullOrWhiteSpace(SelectedExifStatusText);

    public string SelectedExifStatusText
    {
        get => _selectedExifStatusText;
        private set
        {
            if (!SetProperty(ref _selectedExifStatusText, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ShowSelectedExifHint));
        }
    }

    private void OnInspectorSelectionChanged(ImageListItemViewModel? selectedImage)
    {
        CancelInspectorMetadataLoad();
        SetSelectedOriginalDimensions(null);
        ReplaceSelectedExifItems([]);

        if (selectedImage is null)
        {
            SelectedExifStatusText = DefaultInspectorMetadataHint;
            return;
        }

        SelectedExifStatusText = LoadingInspectorMetadataHint;
        var cancellationTokenSource = new CancellationTokenSource();
        _inspectorMetadataLoadCts = cancellationTokenSource;
        ForgetBackgroundTask(LoadSelectedInspectorAsync(selectedImage.Signature, cancellationTokenSource.Token));
    }

    private async Task LoadSelectedInspectorAsync(
        DesktopFileSignature signature,
        CancellationToken cancellationToken)
    {
        using var trace = BeginDiagnosticsOperation(
            "inspector",
            "load-selected-image-metadata",
            ("path", signature.Path),
            ("sizeBytes", signature.SizeBytes.ToString()));

        try
        {
            var snapshot = await Task.Run(
                () => ReadSelectedInspectorSnapshot(signature, cancellationToken),
                cancellationToken);

            await RunOnUiContextAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested
                    || SelectedImage is null
                    || !IsSameInspectorTarget(SelectedImage.Signature, signature))
                {
                    return;
                }

                SetSelectedOriginalDimensions(snapshot.Dimensions);
                ReplaceSelectedExifItems(
                    snapshot.Metadata.Items
                        .Select(static item => new InspectorMetadataItemViewModel(item.Label, item.Value))
                        .ToArray());
                SelectedExifStatusText = snapshot.Metadata.Items.Count == 0
                    ? EmptyInspectorMetadataHint
                    : string.Empty;
            }, cancellationToken);

            trace.Success(CreateDiagnosticsProperties(
                ("hasDimensions", (snapshot.Dimensions is not null).ToString()),
                ("metadataCount", snapshot.Metadata.Items.Count.ToString())));
        }
        catch (OperationCanceledException)
        {
            trace.Canceled();
        }
        catch (Exception ex)
        {
            WriteDiagnosticsWarning(
                "inspector",
                "load-selected-image-metadata-failed",
                ("path", signature.Path),
                ("message", ex.Message));

            try
            {
                await RunOnUiContextAsync(() =>
                {
                    if (SelectedImage is null || !IsSameInspectorTarget(SelectedImage.Signature, signature))
                    {
                        return;
                    }

                    SetSelectedOriginalDimensions(null);
                    ReplaceSelectedExifItems([]);
                    SelectedExifStatusText = FailedInspectorMetadataHint;
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }

            trace.Fail(ex);
        }
    }

    private (DesktopImageDimensions? Dimensions, DesktopImageMetadataSummary Metadata) ReadSelectedInspectorSnapshot(
        DesktopFileSignature signature,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var dimensions = DesktopImageDimensionCacheStore.Shared.GetOrLoad(
            signature,
            DesktopImageDimensionReader.TryRead);

        cancellationToken.ThrowIfCancellationRequested();

        var metadata = _imageMetadataService.GetOrLoad(signature, cancellationToken);
        return (dimensions, metadata);
    }

    private void CancelInspectorMetadataLoad()
    {
        CancelQuietly(_inspectorMetadataLoadCts);
        DisposeQuietly(_inspectorMetadataLoadCts);
        _inspectorMetadataLoadCts = null;
    }

    private void SetSelectedOriginalDimensions(DesktopImageDimensions? dimensions)
    {
        if (_selectedOriginalDimensions == dimensions)
        {
            return;
        }

        _selectedOriginalDimensions = dimensions;
        OnPropertyChanged(nameof(SelectedDimensionsText));
    }

    private void ReplaceSelectedExifItems(IReadOnlyList<InspectorMetadataItemViewModel> items)
    {
        if (!ReplaceObservableCollectionItemsIfChanged(_selectedExifItems, items))
        {
            return;
        }

        OnPropertyChanged(nameof(HasSelectedExifItems));
        OnPropertyChanged(nameof(ShowSelectedExifHint));
    }

    private static bool IsSameInspectorTarget(DesktopFileSignature left, DesktopFileSignature right)
    {
        return left.SizeBytes == right.SizeBytes
            && left.SourceStampTicks == right.SourceStampTicks
            && string.Equals(left.Path, right.Path, PathComparison.Comparison);
    }
}
