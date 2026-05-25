using Avalonia.Input;
using ModernImageViewer.Desktop.ViewModels;

namespace ModernImageViewer.Desktop;

public sealed class WindowShortcutRouter(
    MainWindowViewModel viewModel,
    Action openFolder,
    Action openFiles,
    Action openToolbox,
    Action recycleToTrash,
    Action toggleImmersive,
    Action exitImmersive,
    Action? synchronizeCompareScroll = null,
    Action? restoreViewerFocus = null)
{
    private readonly MainWindowViewModel _viewModel = viewModel;
    private readonly Action _openFolder = openFolder;
    private readonly Action _openFiles = openFiles;
    private readonly Action _openToolbox = openToolbox;
    private readonly Action _recycleToTrash = recycleToTrash;
    private readonly Action _toggleImmersive = toggleImmersive;
    private readonly Action _exitImmersive = exitImmersive;
    private readonly Action? _synchronizeCompareScroll = synchronizeCompareScroll;
    private readonly Action? _restoreViewerFocus = restoreViewerFocus;

    public bool TryHandle(object? source, Key key, KeyModifiers modifiers)
    {
        if (WindowShortcutPolicy.ShouldIgnoreGlobalShortcut(source, key)
            || !WindowShortcutPolicy.TryGetGlobalShortcutCommand(key, modifiers, out var command))
        {
            return false;
        }

        Execute(command);
        if (ShouldRestoreViewerFocus(command))
        {
            _restoreViewerFocus?.Invoke();
        }

        return true;
    }

    private void Execute(WindowShortcutCommand command)
    {
        switch (command)
        {
            case WindowShortcutCommand.OpenFolder:
                _openFolder();
                break;

            case WindowShortcutCommand.OpenFiles:
                _openFiles();
                break;

            case WindowShortcutCommand.OpenToolbox:
                _openToolbox();
                break;

            case WindowShortcutCommand.ToggleFilmstrip:
                _viewModel.ToggleFilmstrip();
                break;

            case WindowShortcutCommand.PreviousImage:
                _viewModel.ShowPreviousSlide();
                break;

            case WindowShortcutCommand.NextImage:
                _viewModel.ShowNextSlide();
                break;

            case WindowShortcutCommand.FirstImage:
                _viewModel.ShowFirstImage();
                break;

            case WindowShortcutCommand.LastImage:
                _viewModel.ShowLastImage();
                break;

            case WindowShortcutCommand.ToggleImmersive:
                _toggleImmersive();
                break;

            case WindowShortcutCommand.ExitImmersive:
                _exitImmersive();
                break;

            case WindowShortcutCommand.TogglePreviewFit:
                _viewModel.TogglePreviewFitMode();
                break;

            case WindowShortcutCommand.ShowPreviewFitOrResetCompare:
                if (_viewModel.ShowCompareViewerSurface)
                {
                    _viewModel.ResetCompareZoom();
                    _synchronizeCompareScroll?.Invoke();
                }
                else
                {
                    _viewModel.ShowPreviewFitMode();
                }

                break;

            case WindowShortcutCommand.ShowPreviewActualSize:
                _viewModel.ShowPreviewActualSize();
                break;

            case WindowShortcutCommand.ToggleLongImageMode:
                _viewModel.ToggleLongImageMode();
                break;

            case WindowShortcutCommand.ToggleSlideshow:
                _viewModel.ToggleSlideshow();
                break;

            case WindowShortcutCommand.RotatePreviewLeft:
                _viewModel.RotatePreviewCounterClockwise();
                break;

            case WindowShortcutCommand.RotatePreviewRight:
                _viewModel.RotatePreviewClockwise();
                break;

            case WindowShortcutCommand.FlipPreviewHorizontal:
                _viewModel.FlipPreviewHorizontal();
                break;

            case WindowShortcutCommand.ResetPreviewTools:
                _viewModel.ResetPreviewTools();
                break;

            case WindowShortcutCommand.ZoomPreviewIn:
                _viewModel.ZoomPreviewIn();
                break;

            case WindowShortcutCommand.ZoomPreviewOut:
                _viewModel.ZoomPreviewOut();
                break;

            case WindowShortcutCommand.RecycleToTrash:
                _recycleToTrash();
                break;

            case WindowShortcutCommand.ToggleSidebar:
                _viewModel.ToggleSidebar();
                break;

            case WindowShortcutCommand.ToggleInspector:
                _viewModel.ToggleInspector();
                break;

            case WindowShortcutCommand.ToggleContactSheet:
                _viewModel.ToggleContactSheet();
                break;

            case WindowShortcutCommand.ToggleCompareViewer:
                _viewModel.ToggleCompareViewer();
                _synchronizeCompareScroll?.Invoke();
                break;

            case WindowShortcutCommand.MarkKeep:
                _viewModel.MarkSelectionAsKeep();
                break;

            case WindowShortcutCommand.MarkReject:
                _viewModel.MarkSelectionAsReject();
                break;

            case WindowShortcutCommand.ClearReviewState:
                _viewModel.ClearSelectionReviewState();
                break;

            case WindowShortcutCommand.ClearRating:
                _viewModel.SetSelectionRating(0);
                break;

            case WindowShortcutCommand.SetRating1:
                _viewModel.SetSelectionRating(1);
                break;

            case WindowShortcutCommand.SetRating2:
                _viewModel.SetSelectionRating(2);
                break;

            case WindowShortcutCommand.SetRating3:
                _viewModel.SetSelectionRating(3);
                break;

            case WindowShortcutCommand.SetRating4:
                _viewModel.SetSelectionRating(4);
                break;

            case WindowShortcutCommand.SetRating5:
                _viewModel.SetSelectionRating(5);
                break;
        }
    }

    private static bool ShouldRestoreViewerFocus(WindowShortcutCommand command)
    {
        return command is not (
            WindowShortcutCommand.OpenFolder
            or WindowShortcutCommand.OpenFiles
            or WindowShortcutCommand.OpenToolbox);
    }
}
