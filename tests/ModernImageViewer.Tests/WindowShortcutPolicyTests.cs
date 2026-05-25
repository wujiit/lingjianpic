using Avalonia.Controls;
using Avalonia.Input;
using ModernImageViewer.Desktop;

namespace ModernImageViewer.Tests;

public sealed class WindowShortcutPolicyTests
{
    [Fact]
    public void ShouldIgnoreGlobalShortcut_returns_true_for_text_input_controls()
    {
        Assert.True(WindowShortcutPolicy.ShouldIgnoreGlobalShortcut(new TextBox(), Key.Right));
        Assert.True(WindowShortcutPolicy.ShouldIgnoreGlobalShortcut(new ComboBox(), Key.Left));
        Assert.True(WindowShortcutPolicy.ShouldIgnoreGlobalShortcut(new ComboBoxItem(), Key.Space));
        Assert.True(WindowShortcutPolicy.ShouldIgnoreGlobalShortcut(new Slider(), Key.Right));
    }

    [Fact]
    public void ShouldIgnoreGlobalShortcut_keeps_arrow_shortcuts_for_buttons_and_checkboxes()
    {
        Assert.False(WindowShortcutPolicy.ShouldIgnoreGlobalShortcut(new Button(), Key.Right));
        Assert.False(WindowShortcutPolicy.ShouldIgnoreGlobalShortcut(new CheckBox(), Key.Left));
    }

    [Fact]
    public void ShouldIgnoreGlobalShortcut_keeps_space_for_buttons_and_checkboxes_local()
    {
        Assert.True(WindowShortcutPolicy.ShouldIgnoreGlobalShortcut(new Button(), Key.Space));
        Assert.True(WindowShortcutPolicy.ShouldIgnoreGlobalShortcut(new CheckBox(), Key.Space));
    }

    [Theory]
    [InlineData(KeyModifiers.Control, false, true)]
    [InlineData(KeyModifiers.Meta, false, true)]
    [InlineData(KeyModifiers.Control | KeyModifiers.Shift, true, true)]
    [InlineData(KeyModifiers.Meta | KeyModifiers.Shift, true, true)]
    [InlineData(KeyModifiers.Control, true, false)]
    [InlineData(KeyModifiers.None, false, false)]
    public void IsPrimaryShortcut_matches_expected_modifier_pairs(
        KeyModifiers modifiers,
        bool includeShift,
        bool expected)
    {
        Assert.Equal(expected, WindowShortcutPolicy.IsPrimaryShortcut(modifiers, includeShift));
    }

    [Theory]
    [InlineData(Key.Right, KeyModifiers.None, WindowShortcutCommand.NextImage)]
    [InlineData(Key.Home, KeyModifiers.None, WindowShortcutCommand.FirstImage)]
    [InlineData(Key.End, KeyModifiers.None, WindowShortcutCommand.LastImage)]
    [InlineData(Key.S, KeyModifiers.None, WindowShortcutCommand.ToggleSidebar)]
    [InlineData(Key.D, KeyModifiers.None, WindowShortcutCommand.ToggleCompareViewer)]
    [InlineData(Key.K, KeyModifiers.None, WindowShortcutCommand.MarkKeep)]
    [InlineData(Key.X, KeyModifiers.None, WindowShortcutCommand.MarkReject)]
    [InlineData(Key.C, KeyModifiers.None, WindowShortcutCommand.ClearReviewState)]
    [InlineData(Key.D0, KeyModifiers.None, WindowShortcutCommand.ClearRating)]
    [InlineData(Key.D5, KeyModifiers.None, WindowShortcutCommand.SetRating5)]
    [InlineData(Key.O, KeyModifiers.Control, WindowShortcutCommand.OpenFiles)]
    [InlineData(Key.O, KeyModifiers.Control | KeyModifiers.Shift, WindowShortcutCommand.OpenFolder)]
    [InlineData(Key.B, KeyModifiers.Control | KeyModifiers.Shift, WindowShortcutCommand.OpenToolbox)]
    public void TryGetGlobalShortcutCommand_maps_expected_commands(
        Key key,
        KeyModifiers modifiers,
        WindowShortcutCommand expected)
    {
        Assert.True(WindowShortcutPolicy.TryGetGlobalShortcutCommand(key, modifiers, out var command));
        Assert.Equal(expected, command);
    }

    [Fact]
    public void TryGetGlobalShortcutCommand_returns_false_for_unmapped_modifier_combinations()
    {
        Assert.False(WindowShortcutPolicy.TryGetGlobalShortcutCommand(Key.Right, KeyModifiers.Shift, out _));
    }
}
