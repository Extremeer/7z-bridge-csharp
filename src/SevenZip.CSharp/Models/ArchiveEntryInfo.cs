using System;

namespace SevenZip.CSharp;

/// <summary>
/// 表示压缩包中的单个条目。
/// </summary>
public sealed class ArchiveEntryInfo : IEquatable<ArchiveEntryInfo>
{
    public ArchiveEntryInfo(
        ArchivePath path,
        bool isDirectory,
        long uncompressedSize,
        long compressedSize,
        DateTimeOffset? modified,
        ArchiveItemAttributes attributes,
        bool isEncrypted,
        string? compressionMethod,
        string? comment = null,
        uint? crc32 = null,
        bool hasKnownUncompressedSize = true,
        ArchiveEntryId? id = null)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Id = id ?? ArchiveEntryId.Detached();
        IsDirectory = isDirectory;

        if (uncompressedSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(uncompressedSize), "大小不能为负数");
        }

        if (compressedSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(compressedSize), "压缩大小不能为负数");
        }

        if (!isDirectory && path.IsRoot)
        {
            throw new ArgumentException("根目录只能表示目录条目", nameof(path));
        }

        UncompressedSize = uncompressedSize;
        CompressedSize = compressedSize;
        Modified = modified;
        Attributes = attributes;
        IsEncrypted = isEncrypted;
        CompressionMethod = compressionMethod;
        Comment = comment;
        Crc32 = crc32;
        HasKnownUncompressedSize = hasKnownUncompressedSize;
    }

    public ArchiveEntryId Id { get; }
    public ArchiveEntryInfo WithId(ArchiveEntryId id) => new(Path, IsDirectory, UncompressedSize, CompressedSize, Modified,
        Attributes, IsEncrypted, CompressionMethod, Comment, Crc32, HasKnownUncompressedSize, id);

    public ArchivePath Path { get; }

    public bool IsDirectory { get; }

    public long UncompressedSize { get; }
    public bool HasKnownUncompressedSize { get; }

    public long CompressedSize { get; }

    public DateTimeOffset? Modified { get; }

    public ArchiveItemAttributes Attributes { get; }

    public bool IsEncrypted { get; }

    public string? CompressionMethod { get; }

    public string? Comment { get; }

    /// <summary>Stored archive CRC32 metadata; null means unavailable, not zero or verified.</summary>
    public uint? Crc32 { get; }

    public string Name => Path.Name;

    public ArchiveEntryInfo WithPath(ArchivePath newPath) => new(newPath, IsDirectory, UncompressedSize, CompressedSize, Modified, Attributes, IsEncrypted, CompressionMethod, Comment, Crc32, HasKnownUncompressedSize, Id);

    public ArchiveEntryInfo WithSizes(long uncompressedSize, long compressedSize) => new(Path, IsDirectory, uncompressedSize, compressedSize, Modified, Attributes, IsEncrypted, CompressionMethod, Comment, Crc32, true, Id);

    public ArchiveEntryInfo WithAttributes(ArchiveItemAttributes attributes) => new(Path, IsDirectory, UncompressedSize, CompressedSize, Modified, attributes, IsEncrypted, CompressionMethod, Comment, Crc32, HasKnownUncompressedSize, Id);

    public bool Equals(ArchiveEntryInfo? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null)
        {
            return false;
        }

        return Path.Equals(other.Path)
            && IsDirectory == other.IsDirectory
            && UncompressedSize == other.UncompressedSize
            && HasKnownUncompressedSize == other.HasKnownUncompressedSize
            && CompressedSize == other.CompressedSize
            && Nullable.Equals(Modified, other.Modified)
            && Attributes == other.Attributes
            && IsEncrypted == other.IsEncrypted
            && Crc32 == other.Crc32
            && string.Equals(CompressionMethod, other.CompressionMethod, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Comment, other.Comment, StringComparison.Ordinal);
    }

    public override bool Equals(object? obj) => obj is ArchiveEntryInfo other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Path);
        hash.Add(IsDirectory);
        hash.Add(UncompressedSize);
        hash.Add(HasKnownUncompressedSize);
        hash.Add(CompressedSize);
        hash.Add(Modified);
        hash.Add(Attributes);
        hash.Add(IsEncrypted);
        hash.Add(CompressionMethod, StringComparer.OrdinalIgnoreCase);
        hash.Add(Comment, StringComparer.Ordinal);
        hash.Add(Crc32);
        return hash.ToHashCode();
    }
}
