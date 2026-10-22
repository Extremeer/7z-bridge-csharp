namespace SevenZip.CSharp;

/// <summary>SDK positions are relative to the archive payload after a preserved SFX stub.</summary>
public class PayloadOutputStream(Stream inner, long prefix) : Stream
{
    public override bool CanRead => false;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanSeek => inner.CanSeek;
    public override long Length => inner.Length - prefix;
    public override long Position { get => inner.Position - prefix; set => Seek(value, SeekOrigin.Begin); }
    public override void Flush() => inner.Flush();
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
    public override long Seek(long offset, SeekOrigin origin)
    {
        var value = checked((origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => Position, SeekOrigin.End => Length,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)) }) + offset);
        if (value < 0) throw new IOException("Cannot seek into the SFX prefix.");
        inner.Position = checked(prefix + value); return value;
    }
    public override void SetLength(long value) { ArgumentOutOfRangeException.ThrowIfNegative(value); inner.SetLength(checked(prefix + value)); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
