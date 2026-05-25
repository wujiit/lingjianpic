using System.IO;
using System.Threading;

namespace ModernImageViewer.Desktop.Services;

internal static class DesktopFileStreamFactory
{
    private const int BufferSize = 131072;
    private const int MaxAttempts = 6;
    private const int BaseRetryDelayMilliseconds = 40;

    public static FileStream OpenReadShared(string path)
    {
        return OpenWithRetry(() => new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            BufferSize,
            FileOptions.SequentialScan));
    }

    public static void CopyFile(string sourcePath, string targetPath, bool overwrite = false)
    {
        RunWithRetry(() => File.Copy(sourcePath, targetPath, overwrite));
    }

    public static void MoveFile(string sourcePath, string targetPath)
    {
        RunWithRetry(() => File.Move(sourcePath, targetPath));
    }

    public static void WriteAtomically(string targetPath, Action<Stream> writeAction)
    {
        ArgumentNullException.ThrowIfNull(writeAction);
        TryWriteAtomically(targetPath, stream =>
        {
            writeAction(stream);
            return true;
        });
    }

    public static bool TryWriteAtomically(string targetPath, Func<Stream, bool> writeAction)
    {
        ArgumentNullException.ThrowIfNull(writeAction);

        var directoryPath = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new InvalidOperationException("Target path must include a parent directory.");
        }

        Directory.CreateDirectory(directoryPath);
        var tempPath = Path.Combine(directoryPath, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var shouldCommit = false;
            using (var output = CreateWriteExclusive(tempPath))
            {
                shouldCommit = writeAction(output);
                if (!shouldCommit)
                {
                    return false;
                }

                output.Flush(flushToDisk: true);
            }

            ReplaceFile(tempPath, targetPath);
            return true;
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static void RunWithRetry(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        OpenWithRetry(() =>
        {
            action();
            return Stream.Null;
        }).Dispose();
    }

    private static FileStream CreateWriteExclusive(string path)
    {
        return OpenWithRetry(() => new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.SequentialScan));
    }

    private static T OpenWithRetry<T>(Func<T> operation)
    {
        Exception? lastException = null;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                return operation();
            }
            catch (IOException exception) when (attempt < MaxAttempts - 1)
            {
                lastException = exception;
            }
            catch (UnauthorizedAccessException exception) when (attempt < MaxAttempts - 1)
            {
                lastException = exception;
            }

            Thread.Sleep(GetRetryDelayMilliseconds(attempt));
        }

        throw lastException ?? new IOException("Unable to complete file operation.");
    }

    private static void ReplaceFile(string sourcePath, string targetPath)
    {
        Exception? lastException = null;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                File.Move(sourcePath, targetPath, overwrite: true);
                return;
            }
            catch (IOException exception) when (attempt < MaxAttempts - 1)
            {
                lastException = exception;
            }
            catch (UnauthorizedAccessException exception) when (attempt < MaxAttempts - 1)
            {
                lastException = exception;
            }

            Thread.Sleep(GetRetryDelayMilliseconds(attempt));
        }

        throw lastException ?? new IOException("Unable to replace target file.");
    }

    private static int GetRetryDelayMilliseconds(int attempt)
    {
        return BaseRetryDelayMilliseconds * (attempt + 1);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
