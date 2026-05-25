using ModernImageViewer.Core;

namespace ModernImageViewer.Desktop.Services;

internal static class ExportPathBuilder
{
    public static string BuildAvailableTargetPath(
        string sourcePath,
        string destinationFolder,
        string targetExtension,
        string collisionSuffixLabel,
        string? baseNameOverride = null,
        ISet<string>? reservedTargetPaths = null)
    {
        var baseName = string.IsNullOrWhiteSpace(baseNameOverride)
            ? Path.GetFileNameWithoutExtension(sourcePath)
            : baseNameOverride.Trim();
        var candidatePath = Path.Combine(destinationFolder, $"{baseName}{targetExtension}");
        if (!string.Equals(candidatePath, sourcePath, PathComparison.Comparison)
            && !File.Exists(candidatePath)
            && (reservedTargetPaths is null || !reservedTargetPaths.Contains(candidatePath)))
        {
            reservedTargetPaths?.Add(candidatePath);
            return candidatePath;
        }

        var exportIndex = 1;
        while (true)
        {
            var indexedCandidatePath = Path.Combine(
                destinationFolder,
                $"{baseName}_{collisionSuffixLabel}{exportIndex:00}{targetExtension}");
            if (!string.Equals(indexedCandidatePath, sourcePath, PathComparison.Comparison)
                && !File.Exists(indexedCandidatePath)
                && (reservedTargetPaths is null || !reservedTargetPaths.Contains(indexedCandidatePath)))
            {
                reservedTargetPaths?.Add(indexedCandidatePath);
                return indexedCandidatePath;
            }

            exportIndex++;
        }
    }
}
