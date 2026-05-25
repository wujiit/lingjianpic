using System.Text;
using ModernImageViewer.Desktop.Services;

namespace ModernImageViewer.Tests;

public sealed class FileStreamFactoryTests
{
    [Fact]
    public void WriteAtomically_replaces_existing_file_and_cleans_up_temp_file()
    {
        using var paths = TestPaths.Create();
        var targetPath = paths.Combine("state.json");
        File.WriteAllText(targetPath, "old");

        DesktopFileStreamFactory.WriteAtomically(targetPath, stream =>
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true);
            writer.Write("new");
            writer.Flush();
        });

        Assert.Equal("new", File.ReadAllText(targetPath));
        Assert.Equal(new[] { targetPath }, Directory.GetFiles(paths.RootPath));
    }

    [Fact]
    public void TryWriteAtomically_keeps_existing_file_when_commit_is_rejected()
    {
        using var paths = TestPaths.Create();
        var targetPath = paths.Combine("state.json");
        File.WriteAllText(targetPath, "stable");

        var committed = DesktopFileStreamFactory.TryWriteAtomically(targetPath, stream =>
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true);
            writer.Write("candidate");
            writer.Flush();
            return false;
        });

        Assert.False(committed);
        Assert.Equal("stable", File.ReadAllText(targetPath));
        Assert.Equal(new[] { targetPath }, Directory.GetFiles(paths.RootPath));
    }
}
