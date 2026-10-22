namespace SevenZip.CSharp;

public sealed record ArchiveSourceItem(string? SourcePath, ArchivePath Path, bool IsDirectory, long Size,
    DateTimeOffset Modified, FileAttributes Attributes)
{
    public static ArchiveSourceItem Directory(ArchivePath path) => new(null, path, true, 0, DateTimeOffset.UtcNow, FileAttributes.Directory);
    public static ArchiveSourceItem FromFileSystem(string source, ArchivePath path)
    {
        var attributes = File.GetAttributes(source);
        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new NotSupportedException("压缩来源不支持重解析点");
        var directory = (attributes & FileAttributes.Directory) != 0;
        return new(System.IO.Path.GetFullPath(source), path, directory, directory ? 0 : new FileInfo(source).Length,
            new DateTimeOffset(File.GetLastWriteTimeUtc(source)), attributes);
    }
    public void ValidateSource()
    {
        if (SourcePath is null && IsDirectory) return;
        var current = FromFileSystem(SourcePath!, Path);
        if (current.IsDirectory != IsDirectory || current.Size != Size || current.Modified != Modified)
            throw new IOException("压缩来源在扫描后发生变化，请重试");
    }
}
