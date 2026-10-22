
namespace SevenZip.CSharp;

/// <summary>Seekable concatenation for fixed-byte ZIP and 7Z volumes, not RAR multi-volume archives.</summary>
public sealed class ArchiveVolumeInput : Stream
{
    private readonly List<FileStream> files = [];
    private readonly List<long> starts = [];
    private readonly CancellationToken token;
    private long position;
    private long length;
    public static bool IsSplit(string path) => ArchiveVolumeSet.TryAnalyze(path, out var set)
        && Path.GetExtension(set.BaseArchivePath).ToLowerInvariant() is ".zip" or ".7z";

    public static Stream Open(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ArchiveVolumeSet.TryAnalyze(path, out var set)
            || Path.GetExtension(set.BaseArchivePath).ToLowerInvariant() is not (".zip" or ".7z")) return File.OpenRead(path);
        if (!set.IsComplete)
            throw new ArchiveReadException(ArchiveReadError.ArchiveDamaged, "缺少分卷：" + string.Join("、", set.MissingVolumePaths.Select(Path.GetFileName)), isVolumeFailure: true);
        // Ambiguous aliases (.001 and .0001) must not concatenate the same logical volume twice.
        if (set.VolumePaths.Select(p => int.Parse(Path.GetExtension(p).AsSpan(1))).Distinct().Count() != set.VolumePaths.Count)
            throw new ArchiveReadException(ArchiveReadError.ArchiveDamaged, "分卷编号重复，无法安全读取");
        return new ArchiveVolumeInput(set.VolumePaths, token);
    }

    private ArchiveVolumeInput(IReadOnlyList<string> paths, CancellationToken token)
    {
        this.token = token;
        try
        {
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new ArchiveReadException(ArchiveReadError.UnsupportedOperation, "不支持通过重解析点读取分卷");
                var file = File.OpenRead(path); files.Add(file); starts.Add(length);
                if (file.Length == 0) throw new ArchiveReadException(ArchiveReadError.ArchiveDamaged, "分卷为空：" + Path.GetFileName(path));
                length = checked(length + file.Length);
            }
        }
        catch { Dispose(); throw; }
    }
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(files.Count == 0, this);
        var total = 0;
        while (buffer.Length > 0 && position < length)
        {
            token.ThrowIfCancellationRequested();
            var index = starts.BinarySearch(position);
            if (index < 0) index = ~index - 1;
            var file = files[index]; file.Position = position - starts[index];
            var count = file.Read(buffer[..(int)Math.Min(buffer.Length, file.Length - file.Position)]);
            if (count == 0) throw new IOException("分卷在读取过程中被截断");
            total += count; position += count; buffer = buffer[count..];
        }
        return total;
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(files.Count == 0, this);
        var next = checked((origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => position, SeekOrigin.End => length,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)) }) + offset);
        if (next < 0) throw new IOException("Cannot seek before start.");
        return position = next;
    }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) { foreach (var file in files) file.Dispose(); files.Clear(); }
        base.Dispose(disposing);
    }
}
