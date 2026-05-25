using Avalonia;
using Avalonia.Controls;
using ModernImageViewer.Desktop.ViewModels;

namespace ModernImageViewer.Tests;

public sealed class LayoutBehaviorTests
{
    [Fact]
    public void SetLayoutViewportSize_prioritizes_preview_area_on_small_laptops()
    {
        using var viewModel = new MainWindowViewModel
        {
            IsSidebarVisible = true,
            IsInspectorVisible = true
        };

        viewModel.SetLayoutViewportSize(980, 720);

        Assert.True(viewModel.IsCompactLayout);
        Assert.True(viewModel.ShowSidebarInMain);
        Assert.True(viewModel.IsPreviewFocusedLayout);
        Assert.False(viewModel.ShowInspectorInMain);
        Assert.Equal(164, viewModel.SidebarColumnWidth.Value);
        Assert.Equal(0, viewModel.InspectorColumnWidth.Value);
    }

    [Fact]
    public void SetLayoutViewportSize_hides_sidebar_when_width_is_narrow()
    {
        using var viewModel = new MainWindowViewModel
        {
            IsSidebarVisible = true
        };

        viewModel.SetLayoutViewportSize(880, 720);

        Assert.True(viewModel.IsNarrowLayout);
        Assert.False(viewModel.ShowSidebarInMain);
        Assert.Equal(0, viewModel.SidebarColumnWidth.Value);
    }

    [Fact]
    public void SetToolboxViewportSize_prefers_wider_right_pane_on_desktop()
    {
        using var viewModel = new MainWindowViewModel();

        viewModel.SetToolboxViewportSize(1200, 760);

        Assert.False(viewModel.IsToolboxCompactLayout);
        Assert.Equal(GridUnitType.Star, viewModel.ToolboxRightColumnWidth.GridUnitType);
        Assert.Equal(2, viewModel.ToolboxRightColumnWidth.Value);
        Assert.Equal(GridUnitType.Star, viewModel.ToolboxBatchRightColumnWidth.GridUnitType);
        Assert.Equal(2, viewModel.ToolboxBatchRightColumnWidth.Value);
    }

    [Fact]
    public void SetLayoutViewportSize_keeps_inspector_visible_on_standard_window_width()
    {
        using var viewModel = new MainWindowViewModel
        {
            IsSidebarVisible = true,
            IsInspectorVisible = true
        };

        viewModel.SetLayoutViewportSize(1180, 720);

        Assert.True(viewModel.ShowSidebarInMain);
        Assert.False(viewModel.IsPreviewFocusedLayout);
        Assert.True(viewModel.ShowInspectorInMain);
        Assert.Equal(164, viewModel.SidebarColumnWidth.Value);
        Assert.Equal(164, viewModel.InspectorColumnWidth.Value);
    }

    [Fact]
    public void SetToolboxViewportSize_stacks_right_pane_under_left_pane_on_compact_width()
    {
        using var viewModel = new MainWindowViewModel();

        viewModel.SetToolboxViewportSize(900, 720);

        Assert.True(viewModel.IsToolboxCompactLayout);
        Assert.Equal(1, viewModel.ToolboxRightPaneRow);
        Assert.Equal(0, viewModel.ToolboxRightPaneColumn);
        Assert.Equal(GridUnitType.Auto, viewModel.ToolboxSecondRowHeight.GridUnitType);
    }
}
