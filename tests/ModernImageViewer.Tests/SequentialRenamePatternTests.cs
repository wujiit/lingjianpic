using ModernImageViewer.Desktop.ViewModels;

namespace ModernImageViewer.Tests;

public sealed class SequentialRenamePatternTests
{
    [Fact]
    public void BuildFileBaseName_returns_trimmed_name_for_single_item()
    {
        var pattern = new SequentialRenamePattern("  trip  ", "_", 1, 3);

        var name = pattern.BuildFileBaseName(sequenceIndex: 0, totalCount: 1);

        Assert.Equal("trip", name);
    }

    [Fact]
    public void BuildFileBaseName_appends_padded_sequence_for_multi_select()
    {
        var pattern = new SequentialRenamePattern("trip", "_", 7, 4);

        var name = pattern.BuildFileBaseName(sequenceIndex: 2, totalCount: 5);

        Assert.Equal("trip_0009", name);
    }

    [Fact]
    public void BuildFileBaseName_omits_separator_when_not_provided()
    {
        var pattern = new SequentialRenamePattern("trip", string.Empty, 12, 2);

        var name = pattern.BuildFileBaseName(sequenceIndex: 0, totalCount: 3);

        Assert.Equal("trip12", name);
    }

    [Fact]
    public void BuildFileBaseName_throws_when_sequence_overflows()
    {
        var pattern = new SequentialRenamePattern("trip", "_", int.MaxValue, 2);

        Assert.Throws<OverflowException>(() => pattern.BuildFileBaseName(sequenceIndex: 1, totalCount: 2));
    }
}
