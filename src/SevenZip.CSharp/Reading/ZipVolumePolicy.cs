using System.Text.RegularExpressions;

namespace SevenZip.CSharp;

/// <summary>ZIP split disks use the final .zip directory, not byte concatenation.</summary>
public static class ZipVolumePolicy
{
    public static bool IsNumbered(string path) => Regex.IsMatch(Path.GetExtension(path), @"^\.z[0-9]{2,}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static string DirectoryPath(string path)
    {
        if (!IsNumbered(path)) return path;
        var final = Path.ChangeExtension(path, ".zip");
        if (!File.Exists(final)) throw new ArchiveReadException(ArchiveReadError.ArchiveDamaged, "缺少末卷：" + Path.GetFileName(final), isVolumeFailure: true);
        return final;
    }
    public static string? Resolve(string source, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['/', '\\', ':', '\0']) >= 0) return null;
        var allowed = Regex.IsMatch(name, "^" + Regex.Escape(Path.GetFileNameWithoutExtension(source)) + @"\.(zip|z[0-9]{2,})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return allowed ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!, name) : null;
    }
}
