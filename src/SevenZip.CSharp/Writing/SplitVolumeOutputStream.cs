namespace SevenZip.CSharp;

/// <summary>Seekable 7-Zip output split into fixed-size volumes, including header rewrites.</summary>
public class SplitVolumeOutputStream(string path, long volumeSize) : Stream
{
    private readonly HashSet<long> createdVolumes = [];
    // Earlier volumes can still be rewritten/truncated. Protect all of them,
    // not just the volume currently receiving writes, until publication.
    private readonly Dictionary<long, FileStream> openVolumes = [];
    private FileStream? current;
    private long currentIndex = -1;
    private long position, length;
    public IReadOnlyList<string> CreatedPaths => createdVolumes.Order().Select(index =>
        path + "." + (index + 1).ToString("D3", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    public override bool CanRead => false;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => length;
    public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }
    public override void Flush() { foreach (var file in openVolumes.Values) file.Flush(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            var index = position / volumeSize;
            var offset = position % volumeSize;
            if (index != currentIndex)
            {
                var volume = path + "." + (index + 1).ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
                if (!openVolumes.TryGetValue(index, out current))
                {
                    current = new FileStream(volume, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                    openVolumes.Add(index, current);
                }
                createdVolumes.Add(index); currentIndex = index;
            }
            current!.Position = offset;
            var count = (int)Math.Min(buffer.Length, volumeSize - offset);
            current.Write(buffer[..count]); position += count;
            length = Math.Max(length, position); buffer = buffer[count..];
        }
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => checked(position + offset),
            SeekOrigin.End => checked(length + offset), _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
        if (target < 0) throw new IOException("Cannot seek before the archive output.");
        return position = target;
    }
    public override void SetLength(long value)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        if (value > length)
        {
            var saved = position; position = length;
            var zeros = new byte[64 * 1024];
            while (length < value) Write(zeros.AsSpan(0, (int)Math.Min(zeros.Length, value - length)));
            position = saved;
        }
        else if (value < length)
        {
            foreach (var index in createdVolumes.ToArray())
            {
                var volume = path + "." + (index + 1).ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
                if (index * volumeSize >= value)
                {
                    openVolumes.Remove(index, out var removed); removed?.Dispose();
                    if (currentIndex == index) { current = null; currentIndex = -1; }
                    File.Delete(volume); createdVolumes.Remove(index);
                }
                else if ((index + 1) * volumeSize > value)
                    openVolumes[index].SetLength(value - index * volumeSize);
            }
            length = value;
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Exception? failure = null;
            foreach (var file in openVolumes.Values)
            {
                try { file.Dispose(); }
                catch (Exception exception) { failure ??= exception; }
            }
            openVolumes.Clear(); current = null; currentIndex = -1;
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        base.Dispose(disposing);
    }
}
