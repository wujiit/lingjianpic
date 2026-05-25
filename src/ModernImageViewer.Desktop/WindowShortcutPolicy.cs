using Avalonia.Controls;
using Avalonia.Input;

namespace ModernImageViewer.Desktop;

public enum WindowShortcutCommand
{
    OpenFolder,
    OpenFiles,
    OpenToolbox,
    ToggleFilmstrip,
    PreviousImage,
    NextImage,
    FirstImage,
    LastImage,
    ToggleImmersive,
    ExitImmersive,
    TogglePreviewFit,
    ShowPreviewFitOrResetCompare,
    ShowPreviewActualSize,
    ToggleLongImageMode,
    ToggleSlideshow,
    RotatePreviewLeft,
    RotatePreviewRight,
    FlipPreviewHorizontal,
    ResetPreviewTools,
    ZoomPreviewIn,
    ZoomPreviewOut,
    RecycleToTrash,
    ToggleSidebar,
    ToggleInspector,
    ToggleContactSheet,
    ToggleCompareViewer,
    MarkKeep,
    MarkReject,
    ClearReviewState,
    ClearRating,
    SetRating1,
    SetRating2,
    SetRating3,
    SetRating4,
    SetRating5
}

public static class WindowShortcutPolicy
{
    public static bool ShouldIgnoreGlobalShortcut(object? source, Key key)
    {
        for (var control = source as Control; control is not null; control = control.Parent as Control)
        {
            if (control is TextBox or ComboBox or ComboBoxItem or Slider)
            {
                return true;
            }

            if (key == Key.Space && control is Button or CheckBox)
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsPrimaryShortcut(KeyModifiers modifiers, bool includeShift)
    {
        return includeShift
            ? modifiers is (KeyModifiers.Control | KeyModifiers.Shift) or (KeyModifiers.Meta | KeyModifiers.Shift)
            : modifiers is KeyModifiers.Control or KeyModifiers.Meta;
    }

    public static bool TryGetGlobalShortcutCommand(
        Key key,
        KeyModifiers modifiers,
        out WindowShortcutCommand command)
    {
        if (IsPrimaryShortcut(modifiers, includeShift: true) && key == Key.O)
        {
            command = WindowShortcutCommand.OpenFolder;
            return true;
        }

        if (IsPrimaryShortcut(modifiers, includeShift: false) && key == Key.O)
        {
            command = WindowShortcutCommand.OpenFiles;
            return true;
        }

        if (IsPrimaryShortcut(modifiers, includeShift: true) && key == Key.B)
        {
            command = WindowShortcutCommand.OpenToolbox;
            return true;
        }

        if (IsPrimaryShortcut(modifiers, includeShift: false) && key == Key.B)
        {
            command = WindowShortcutCommand.ToggleFilmstrip;
            return true;
        }

        if (modifiers == KeyModifiers.Shift && key == Key.OemPlus)
        {
            command = WindowShortcutCommand.ZoomPreviewIn;
            return true;
        }

        if (modifiers != KeyModifiers.None)
        {
            command = default;
            return false;
        }

        switch (key)
        {
            case Key.Left:
            case Key.Up:
            case Key.PageUp:
                command = WindowShortcutCommand.PreviousImage;
                return true;

            case Key.Right:
            case Key.Down:
            case Key.PageDown:
                command = WindowShortcutCommand.NextImage;
                return true;

            case Key.Home:
                command = WindowShortcutCommand.FirstImage;
                return true;

            case Key.End:
                command = WindowShortcutCommand.LastImage;
                return true;

            case Key.F11:
                command = WindowShortcutCommand.ToggleImmersive;
                return true;

            case Key.Escape:
                command = WindowShortcutCommand.ExitImmersive;
                return true;

            case Key.Space:
                command = WindowShortcutCommand.TogglePreviewFit;
                return true;

            case Key.F:
                command = WindowShortcutCommand.ShowPreviewFitOrResetCompare;
                return true;

            case Key.A:
                command = WindowShortcutCommand.ShowPreviewActualSize;
                return true;

            case Key.L:
                command = WindowShortcutCommand.ToggleLongImageMode;
                return true;

            case Key.P:
                command = WindowShortcutCommand.ToggleSlideshow;
                return true;

            case Key.Q:
                command = WindowShortcutCommand.RotatePreviewLeft;
                return true;

            case Key.E:
                command = WindowShortcutCommand.RotatePreviewRight;
                return true;

            case Key.H:
                command = WindowShortcutCommand.FlipPreviewHorizontal;
                return true;

            case Key.Z:
                command = WindowShortcutCommand.ResetPreviewTools;
                return true;

            case Key.OemPlus:
            case Key.Add:
                command = WindowShortcutCommand.ZoomPreviewIn;
                return true;

            case Key.OemMinus:
            case Key.Subtract:
                command = WindowShortcutCommand.ZoomPreviewOut;
                return true;

            case Key.Delete:
                command = WindowShortcutCommand.RecycleToTrash;
                return true;

            case Key.S:
                command = WindowShortcutCommand.ToggleSidebar;
                return true;

            case Key.I:
                command = WindowShortcutCommand.ToggleInspector;
                return true;

            case Key.G:
                command = WindowShortcutCommand.ToggleContactSheet;
                return true;

            case Key.D:
                command = WindowShortcutCommand.ToggleCompareViewer;
                return true;

            case Key.K:
                command = WindowShortcutCommand.MarkKeep;
                return true;

            case Key.X:
                command = WindowShortcutCommand.MarkReject;
                return true;

            case Key.C:
                command = WindowShortcutCommand.ClearReviewState;
                return true;

            case Key.D0:
            case Key.NumPad0:
                command = WindowShortcutCommand.ClearRating;
                return true;

            case Key.D1:
            case Key.NumPad1:
                command = WindowShortcutCommand.SetRating1;
                return true;

            case Key.D2:
            case Key.NumPad2:
                command = WindowShortcutCommand.SetRating2;
                return true;

            case Key.D3:
            case Key.NumPad3:
                command = WindowShortcutCommand.SetRating3;
                return true;

            case Key.D4:
            case Key.NumPad4:
                command = WindowShortcutCommand.SetRating4;
                return true;

            case Key.D5:
            case Key.NumPad5:
                command = WindowShortcutCommand.SetRating5;
                return true;

            default:
                command = default;
                return false;
        }
    }
}
