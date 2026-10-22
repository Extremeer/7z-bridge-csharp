
namespace SevenZip.CSharp;

public static class OutputSafetyValidator
{
    public static ArchiveFailure? Validate(string destinationPath, IEnumerable<ArchivePath> entries) =>
        Validate(destinationPath, entries, GetExistingAttributes);

    public static ArchiveFailure? Validate(
        string destinationPath,
        IEnumerable<ArchivePath> entries,
        Func<string, FileAttributes?> getExistingAttributes)
    {
        ArgumentNullException.ThrowIfNull(getExistingAttributes);
        if (string.IsNullOrWhiteSpace(destinationPath) || !Path.IsPathFullyQualified(destinationPath))
        {
            return Failure("解压目标必须是完整的本地路径", destinationPath);
        }

        string root;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationPath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Failure("解压目标路径无效", destinationPath, EngineFailureClassifier.Diagnostic(exception));
        }

        if (File.Exists(root))
        {
            return Failure("解压目标是文件而不是文件夹", root);
        }

        var reparseResult = ValidateExistingAncestors(root, getExistingAttributes);
        if (reparseResult is not null)
        {
            return reparseResult;
        }

        var rootPrefix = root + Path.DirectorySeparatorChar;
        foreach (var entry in entries ?? Array.Empty<ArchivePath>())
        {
            if (entry is null || entry.IsRoot)
            {
                continue;
            }

            string outputPath;
            try
            {
                outputPath = Path.GetFullPath(Path.Combine(
                    root,
                    entry.Value.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Failure("压缩包条目无法映射到安全的输出路径", entry.Value, EngineFailureClassifier.Diagnostic(exception));
            }

            if (!outputPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return Failure("压缩包条目试图写出目标文件夹", entry.Value);
            }

            var nameResult = WindowsOutputNamePolicy.ValidateEntry(entry, outputPath);
            if (nameResult is not null)
            {
                return nameResult;
            }

            var outputAncestorResult = ValidateExistingAncestors(
                Path.GetDirectoryName(outputPath) ?? root,
                getExistingAttributes);
            if (outputAncestorResult is not null)
            {
                return outputAncestorResult;
            }
        }

        return null;
    }

    private static ArchiveFailure? ValidateExistingAncestors(
        string destinationPath,
        Func<string, FileAttributes?> getExistingAttributes)
    {
        for (var current = new DirectoryInfo(destinationPath); current is not null; current = current.Parent)
        {
            try
            {
                if (getExistingAttributes(current.FullName) is { } attributes &&
                    (attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return Failure("解压目标路径包含重解析点，已阻止可能的目录逃逸", current.FullName);
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                return Failure("无法验证解压目标路径", current.FullName, EngineFailureClassifier.Diagnostic(exception));
            }
        }

        return null;
    }

    private static FileAttributes? GetExistingAttributes(string path) =>
        Directory.Exists(path) || File.Exists(path) ? File.GetAttributes(path) : null;

    private static ArchiveFailure? Failure(string message, string? path, string? diagnostic = null) =>
        new(ArchiveFailureCode.InvalidPath, message, path, diagnostic);
}
