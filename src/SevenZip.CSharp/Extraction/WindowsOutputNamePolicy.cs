
namespace SevenZip.CSharp;

/// <summary>Deterministic Windows extraction-name policy, independent of host process settings.</summary>
public static class WindowsOutputNamePolicy
{
    public const int MaximumSegmentLength = 255;
    public const int MaximumExtendedPathLength = 32_767;

    private static readonly char[] InvalidNameCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static ArchiveFailure? ValidateEntry(ArchivePath entry, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(entry);

        foreach (var segment in entry.Segments)
        {
            var segmentResult = ValidateSegment(segment, entry.Value);
            if (segmentResult is not null)
            {
                return segmentResult;
            }
        }

        if (outputPath.Length > MaximumExtendedPathLength)
        {
            return Failure("解压输出路径超过 Windows 扩展路径上限", entry.Value);
        }

        return null;
    }

    private static ArchiveFailure? ValidateSegment(string segment, string entryPath)
    {
        if (segment.Length == 0)
        {
            return Failure("压缩包条目包含空路径片段", entryPath);
        }

        if (segment.Length > MaximumSegmentLength)
        {
            return Failure("压缩包条目的名称超过 Windows 单个路径片段上限", entryPath);
        }

        if (segment.EndsWith(' ') || segment.EndsWith('.'))
        {
            return Failure("压缩包条目的名称不能以空格或句点结尾", entryPath);
        }

        if (segment.Any(character => character < 32 || InvalidNameCharacters.Contains(character)))
        {
            return Failure("压缩包条目包含 Windows 文件名非法字符或 NTFS 数据流分隔符", entryPath);
        }

        var deviceName = segment.Split('.', 2)[0];
        if (ReservedDeviceNames.Contains(deviceName))
        {
            return Failure("压缩包条目使用了 Windows 保留设备名称", entryPath);
        }

        return null;
    }

    private static ArchiveFailure? Failure(string message, string path) =>
        new(ArchiveFailureCode.InvalidPath, message, path);
}
