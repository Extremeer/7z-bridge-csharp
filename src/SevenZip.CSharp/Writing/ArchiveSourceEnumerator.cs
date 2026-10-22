namespace SevenZip.CSharp;

public static class ArchiveSourceEnumerator
{
    /// <summary>Enumerates descendants once without following reparse points; the selected root is not an entry.</summary>
    public static IReadOnlyList<ArchiveSourceItem> EnumerateDirectory(string root, CancellationToken token = default,
        IProgress<EngineProgress>? progress = null)
    {
        var items = new List<ArchiveSourceItem>();
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new NotSupportedException("Native creation does not follow reparse points.");
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                progress?.Report(new EngineProgress(EngineStage.Scanning, CurrentItem: path));
                var item = ArchiveSourceItem.FromFileSystem(path, ArchivePath.From(Path.GetRelativePath(root, path)));
                items.Add(item);
                if (item.IsDirectory) pending.Push(path);
            }
        }
        return Array.AsReadOnly(items.OrderBy(item => item.SourcePath, StringComparer.Ordinal).ToArray());
    }
}
