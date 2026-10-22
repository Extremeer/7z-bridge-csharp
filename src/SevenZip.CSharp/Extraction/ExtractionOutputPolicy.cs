
namespace SevenZip.CSharp;

public static class ExtractionOutputPolicy
{
    public static string PathFor(string root, ArchiveEntryInfo entry) => Path.GetFullPath(Path.Combine(root, entry.Path.Value.Replace('/', Path.DirectorySeparatorChar)));

    public static ArchiveFailure? Validate(string root, IEnumerable<ArchiveEntryInfo> entries, string? source = null)
    {
        var items = entries.ToArray();
        var safety = OutputSafetyValidator.Validate(root, items.Select(e => e.Path));
        if (safety is not null) return safety;
        foreach (var entry in items)
        {
            var path = PathFor(root, entry);
            if (source is not null && path.Equals(Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
                return new(ArchiveFailureCode.InvalidOperation, "解压输出不能覆盖正在读取的归档", entry.Path.Value);
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return new(ArchiveFailureCode.InvalidPath, "解压目标条目包含重解析点", entry.Path.Value);
            if (entry.IsDirectory && File.Exists(path) || !entry.IsDirectory && Directory.Exists(path))
                return new(ArchiveFailureCode.InvalidOperation, "解压目标存在文件与文件夹类型冲突", entry.Path.Value);
            for (var parent = Path.GetDirectoryName(path); parent is not null && DiskPathPolicy.IsSameOrDescendant(parent, root); parent = Path.GetDirectoryName(parent))
                if (File.Exists(parent)) return new(ArchiveFailureCode.InvalidOperation, "解压目标的父路径是文件", entry.Path.Value);
        }
        return null;
    }
}
