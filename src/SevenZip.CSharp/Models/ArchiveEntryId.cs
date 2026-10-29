using System.Collections.Immutable;
using System.Security.Cryptography;

namespace SevenZip.CSharp;

/// <summary>Version of the opened source files. A file replacement always creates a new version.</summary>
public readonly record struct ArchiveRevision(Guid Value)
{
    public bool IsEmpty => Value == Guid.Empty;
    public static ArchiveRevision New() => new(Guid.NewGuid());

    public static ArchiveRevision Capture(IEnumerable<string> sourceFiles, string format)
    {
        ArgumentNullException.ThrowIfNull(sourceFiles);
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(format);
            foreach (var path in sourceFiles.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase))
            {
                var file = ArchiveFileIdentity.Capture(path);
                writer.Write(OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path);
                writer.Write(file.Volume); writer.Write(file.FileId); writer.Write(file.Length);
                writer.Write(file.Modified); writer.Write(file.Created);
            }
        }
        return new(new Guid(SHA256.HashData(buffer.ToArray()).AsSpan(0, 16)));
    }
}

/// <summary>Identity of an item in one archive version. It is never an archive or disk path.</summary>
public readonly record struct ArchiveEntryId(ArchiveRevision Revision, uint Value)
{
    private static int detachedSequence;
    public bool IsBound => !Revision.IsEmpty;
    internal static ArchiveEntryId Detached() => new(default, unchecked((uint)Interlocked.Increment(ref detachedSequence)));
}

/// <summary>An immutable, explicitly versioned set of items; directories are expanded before execution.</summary>
public sealed class ArchiveEntrySelection
{
    public ArchiveRevision Revision { get; }
    public ImmutableArray<ArchiveEntryId> Ids { get; }
    public bool IsAll { get; }
    public int Count => Ids.Length;

    public ArchiveEntrySelection(ArchiveRevision revision, IEnumerable<ArchiveEntryId> ids, bool isAll = false)
    {
        if (revision.IsEmpty) throw new ArgumentException("归档条目选择必须绑定归档版本", nameof(revision));
        ArgumentNullException.ThrowIfNull(ids);
        Revision = revision;
        Ids = ids.Distinct().ToImmutableArray();
        if (Ids.Any(id => id.Revision != revision)) throw new ArgumentException("条目不属于同一归档版本", nameof(ids));
        IsAll = isAll;
    }
}
