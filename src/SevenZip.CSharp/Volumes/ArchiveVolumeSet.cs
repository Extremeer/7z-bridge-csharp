namespace SevenZip.CSharp;

public sealed record ArchiveVolumeSet(
    string BaseArchivePath,
    string FirstVolumePath,
    IReadOnlyList<string> VolumePaths,
    IReadOnlyList<string> MissingVolumePaths,
    int MissingVolumeCount)
{
    private const int MaximumReportedMissingPaths = 100;

    public bool IsComplete => MissingVolumeCount == 0;

    public static bool TryAnalyze(string selectedPath, out ArchiveVolumeSet volumeSet)
    {
        volumeSet = null!;
        if (!TryGetVolumeIndex(selectedPath, out var basePath, out _))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(basePath);
        var fileName = Path.GetFileName(basePath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName) ||
            !Directory.Exists(directory))
        {
            return false;
        }

        var indexedVolumes = Directory.EnumerateFiles(directory, fileName + ".*")
            .Select(path => TryGetVolumeIndex(path, out var candidateBase, out var index) &&
                            string.Equals(candidateBase, basePath, StringComparison.OrdinalIgnoreCase)
                ? (Path: path, Index: (int?)index)
                : (Path: path, Index: (int?)null))
            .Where(item => item.Index.HasValue)
            .OrderBy(item => item.Index)
            .ToArray();
        if (indexedVolumes.Length == 0)
        {
            return false;
        }

        var missing = new List<string>();
        var missingCount = 0;
        long expected = 1;
        foreach (var item in indexedVolumes)
        {
            var actual = item.Index!.Value;
            var gap = actual - expected;
            if (gap > 0)
            {
                missingCount = checked(missingCount + (int)gap);
                for (var index = expected;
                     index < actual && missing.Count < MaximumReportedMissingPaths;
                     index++)
                {
                    missing.Add(FormatVolumePath(basePath, (int)index));
                }
            }
            expected = (long)actual + 1;
        }

        volumeSet = new ArchiveVolumeSet(
            basePath,
            FormatVolumePath(basePath, 1),
            indexedVolumes.Select(item => item.Path).ToArray(),
            missing,
            missingCount);
        return true;
    }

    private static bool TryGetVolumeIndex(string path, out string basePath, out int index)
    {
        basePath = string.Empty;
        index = 0;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        if (extension.Length < 4 ||
            !extension.Skip(1).All(char.IsAsciiDigit) ||
            !int.TryParse(extension.AsSpan(1), out index) ||
            index <= 0)
        {
            return false;
        }

        basePath = path[..^extension.Length];
        return true;
    }

    private static string FormatVolumePath(string basePath, int index) =>
        $"{basePath}.{index:000}";
}
