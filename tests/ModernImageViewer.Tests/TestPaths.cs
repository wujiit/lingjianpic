namespace ModernImageViewer.Tests;

internal sealed class TestPaths : IDisposable
{
    public string RootPath { get; }

    private TestPaths(string rootPath)
    {
        RootPath = rootPath;
    }

    public static TestPaths Create()
    {
        var path = Path.Combine(Path.GetTempPath(), "ModernImageViewer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new TestPaths(path);
    }

    public string Combine(params string[] segments)
    {
        return segments.Aggregate(RootPath, Path.Combine);
    }

    public void Dispose()
    {
        if (!Directory.Exists(RootPath))
        {
            return;
        }

        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                Directory.Delete(RootPath, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 11)
            {
                System.Threading.Thread.Sleep((attempt + 1) * 40);
            }
            catch (UnauthorizedAccessException) when (attempt < 11)
            {
                System.Threading.Thread.Sleep((attempt + 1) * 40);
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }
}
