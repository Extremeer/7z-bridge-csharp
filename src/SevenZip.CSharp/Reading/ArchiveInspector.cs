using System.Runtime.InteropServices;

namespace SevenZip.CSharp;

/// <summary>
/// A per-operation read session. No RCW or native stream escapes its worker.
/// Null means an explicitly unsupported case, not a successful empty listing.
/// Fixed-byte ZIP/7Z volumes are joined; ZIP split disks and RAR volumes use SDK callbacks.
/// Unsupported metadata retains compatibility paths.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class ArchiveInspector
{
    public sealed record Snapshot(string Format, IReadOnlyList<ArchiveEntryInfo> Entries, IReadOnlyDictionary<uint, ArchiveEntryInfo> PhysicalEntries, bool IsMultiVolume = false, long ArchiveOffset = 0, long PrefixLength = 0, bool CanUpdate = false, bool RequiresHeaderPassword = false, IReadOnlyList<string>? SourceFiles = null)
    {
        public ArchiveRevision Revision { get; init; }
        public IReadOnlyDictionary<ArchiveEntryId, ArchiveEntryInfo> ById { get; init; } =
            new Dictionary<ArchiveEntryId, ArchiveEntryInfo>();

        public ArchiveEntrySelection SelectAll() => new(Revision, Entries.Select(entry => entry.Id), isAll: true);
        public ArchiveEntrySelection Select(IEnumerable<ArchiveEntryId> ids, bool includeDescendants = true)
        {
            ArgumentNullException.ThrowIfNull(ids);
            var selected = ids.Distinct().Select(id => ById.TryGetValue(id, out var entry) ? entry
                : throw new ArgumentException("条目选择已失效，请刷新归档", nameof(ids))).ToArray();
            var directories = includeDescendants ? selected.Where(entry => entry.IsDirectory).ToArray() : [];
            return new(Revision, selected.Select(entry => entry.Id).Concat(Entries.Where(entry =>
                directories.Any(directory => directory.Path.IsAncestorOf(entry.Path)
                    && (entry.IsDirectory || entry.Path.Segments.Count > directory.Path.Segments.Count))).Select(entry => entry.Id)));
        }
        public bool IsCurrent(ArchiveEntrySelection selection) => selection.Revision == Revision
            && selection.Ids.All(ById.ContainsKey) && (!selection.IsAll || selection.Count == ById.Count);
    }

    public static Task<Snapshot?> InspectAsync(string libraryPath, string path, ArchiveOperationContext? context = null)
    {
        context ??= new();
        return Task.Run(() => Read(libraryPath, path, context), context.CancellationToken);
    }

    private static Snapshot? Read(string libraryPath, string path, ArchiveOperationContext context)
    {
        var token = context.CancellationToken;
        var password = context.Password;
        if (!NativeReadSession.IsSupportedPlatform) throw new PlatformNotSupportedException("Native archive reads require Windows x86 or x64.");
        token.ThrowIfCancellationRequested();
        using var nativeControl = context.Control?.EnterNativeOperation();
        var library = SevenZipNativeLibrary.Load(libraryPath);
        foreach (var candidate in ArchiveFormatSelector.Candidates(path, library, context))
        {
            using var session = NativeReadSession.TryOpen(path, library, candidate, token, password, control: context.Control);
            if (session is null) continue;
            try { return Map(session, path, token); }
            catch (COMException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
            catch (InvalidDataException) { throw new ArchiveReadException(ArchiveReadError.UnsupportedOperation, "归档包含当前桥接尚不支持的元数据"); }
        }
        if (ArchiveVolumeInput.IsSplit(path))
            throw new ArchiveReadException(ArchiveReadError.ArchiveDamaged, "分卷压缩包不完整或已损坏，请检查最后一卷是否缺失");
        return null;
    }

    internal static Snapshot? Map(NativeReadSession session, string path, CancellationToken token)
    {
        var format = session.Format;
        // Do not accept SDK recovery/warnings as a clean native listing.
        if (ArchiveNumber(71) != 0 || ArchiveNumber(72) != 0)
        {
            if (session.IsMultiVolume) throw new ArchiveReadException(ArchiveReadError.ArchiveDamaged, "分卷压缩包不完整或已损坏，请检查最后一卷是否缺失");
            throw new ArchiveReadException(ArchiveReadError.ArchiveDamaged, "压缩包包含结构错误或校验警告，不能作为正常归档接受");
        }
        if ((ArchiveNumber(35) != 0 || ArchiveNumber(39) > 1) && !session.IsMultiVolume) return null;
        var revision = ArchiveRevision.Capture(session.SourceFiles, format);
        var count = session.ItemCount;
        var singleStream = format is "gzip" or "bzip2" or "xz" or "zstd";
        if (singleStream && count != 1) return null;
        var parents = new Dictionary<ArchivePath, ArchiveEntryInfo>();
        var physical = new Dictionary<uint, ArchiveEntryInfo>();
        for (uint index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            var anti = Property(index, 21) is true;
            var alternate = Property(index, 63) is true;
            var reparse = Property(index, 89) is { } special && special is not false;
            var attributes = Number(Property(index, 9));
            var posix = Number(Property(index, 53));
            var rawPath = Property(index, 3) as string;
            var synthetic = singleStream && string.IsNullOrWhiteSpace(rawPath);
            if (synthetic)
            {
                var name = Path.GetFileName(path);
                rawPath = Path.GetExtension(path).ToLowerInvariant() is ".tgz" or ".tbz2" or ".txz" or ".tzst"
                    ? Path.GetFileNameWithoutExtension(name) + ".tar" : Path.GetFileNameWithoutExtension(name);
            }
            if (string.IsNullOrWhiteSpace(rawPath)) return null;
            var entryPath = ArchivePath.From(rawPath.Replace('\\', '/'));
            if (entryPath.IsRoot) return null;
            var directory = Property(index, 6) is true;
            var flags = ArchiveItemAttributes.None;
            if (anti) flags |= ArchiveItemAttributes.AntiItem;
            if (alternate) flags |= ArchiveItemAttributes.AlternateDataStream;
            if (reparse || (attributes & 0x400) != 0) flags |= ArchiveItemAttributes.ReparsePoint;
            var encrypted = Property(index, 15) is true;
            if (encrypted) flags |= ArchiveItemAttributes.Encrypted;
            if (directory) flags |= ArchiveItemAttributes.Directory;
            if ((attributes & 1) != 0) flags |= ArchiveItemAttributes.ReadOnly;
            if ((attributes & 2) != 0) flags |= ArchiveItemAttributes.Hidden;
            if ((attributes & 4) != 0) flags |= ArchiveItemAttributes.System;
            if ((attributes & 32) != 0) flags |= ArchiveItemAttributes.Archive;
            if ((attributes & 0x800) != 0) flags |= ArchiveItemAttributes.Compressed;
            if ((posix & 0xf000) == 0xa000 || Property(index, 54) is string symbolic && !string.IsNullOrWhiteSpace(symbolic))
                flags |= ArchiveItemAttributes.SymbolicLink;
            if (Property(index, 90) is string hard && !string.IsNullOrWhiteSpace(hard)) flags |= ArchiveItemAttributes.HardLink;
            if (synthetic && format != "gzip") flags |= ArchiveItemAttributes.Archive;
            var packed = Number(Property(index, 8));
            if (synthetic && packed == 0) packed = ArchiveNumber(44);
            var method = Property(index, 22) as string;
            if (synthetic && string.IsNullOrWhiteSpace(method))
            {
                method = session.GetArchiveProperty(22) as string;
            }
            var rawSize = Property(index, 7);
            var entry = new ArchiveEntryInfo(entryPath, directory,
                checked((long)Number(rawSize)), checked((long)packed),
                synthetic && format != "gzip" ? null : Property(index, 12) as DateTimeOffset?, flags, encrypted, method,
                Property(index, 28) as string, Property(index, 19) as uint?, directory || rawSize is uint or ulong, new ArchiveEntryId(revision, index));
            physical.Add(index, entry);
            var parent = entryPath.Parent;
            while (!parent.IsRoot)
            {
                parents.TryAdd(parent, new ArchiveEntryInfo(parent, true, 0, 0, null,
                    ArchiveItemAttributes.Directory, false, null));
                parent = parent.Parent;
            }
        }
        // Files are archive items, not Windows destinations. Preserve every index.
        // Directories retain the merged navigation tree, as in the 7-Zip file manager.
        var nextVirtualId = count;
        var physicalDirectories = physical.Values.Where(entry => entry.IsDirectory).Select(entry => entry.Path).ToHashSet();
        var virtualDirectories = parents.Values.Where(entry => entry.IsDirectory && !physicalDirectories.Contains(entry.Path))
            .Select(entry => entry.WithId(new ArchiveEntryId(revision, nextVirtualId++))).ToArray();
        var listing = physical.Values.Concat(virtualDirectories).ToArray();
        var offset = checked((long)ArchiveNumber(36));
        var prefix = checked((long)(ArchiveNumber(36) + ArchiveNumber(88)));
        var canUpdate = session.CanUpdate && format is "zip" or "7z"
            && (format != "zip" || offset == prefix)
            && !physical.Values.Any(e => (e.Attributes & (ArchiveItemAttributes.SymbolicLink | ArchiveItemAttributes.HardLink
                | ArchiveItemAttributes.AntiItem | ArchiveItemAttributes.AlternateDataStream | ArchiveItemAttributes.ReparsePoint)) != 0);
        return new Snapshot(format, Array.AsReadOnly(listing.OrderBy(e => e.Path.Value,
            StringComparer.OrdinalIgnoreCase).ToArray()), new System.Collections.ObjectModel.ReadOnlyDictionary<uint, ArchiveEntryInfo>(physical), session.IsMultiVolume,
            offset, prefix, canUpdate, session.HeaderPasswordRequested, session.SourceFiles)
        {
            Revision = revision,
            ById = new System.Collections.ObjectModel.ReadOnlyDictionary<ArchiveEntryId, ArchiveEntryInfo>(listing.ToDictionary(entry => entry.Id))
        };

        ulong ArchiveNumber(uint id) => Number(session.GetArchiveProperty(id));
        object? Property(uint index, uint id) => session.GetProperty(index, id);
    }

    private static ulong Number(object? value) => value switch
    { ulong number => number, uint number => number, bool boolean => boolean ? 1UL : 0UL, _ => 0 };
}
