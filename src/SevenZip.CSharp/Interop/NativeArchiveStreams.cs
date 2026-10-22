using System.Runtime.InteropServices;
using static SevenZip.CSharp.NativeArchiveInterop;

namespace SevenZip.CSharp;

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class NativeInputStream(Stream file, CancellationToken token, Action<long, bool>? read = null, ArchiveOperationControl? control = null, Action<int>? bytesRead = null) : SequentialInput, SeekableInput, IDisposable
{
    private readonly ArchiveOperationControl? executionControl = control;
    public unsafe int Read(nint data, uint size, nint processed)
    {
        try
        {
            if (processed != 0) Marshal.WriteInt32(processed, 0);
            if (!(executionControl?.TryCheckStop(token) ?? !token.IsCancellationRequested)) return unchecked((int)0x80004004);
            var count = file.Read(new Span<byte>((void*)data, checked((int)size)));
            bytesRead?.Invoke(count);
            read?.Invoke(file.Position, count == 0 && size != 0);
            // EOF is not stream release. ZIP can seek back to retry Store after
            // Deflate expands incompressible data.
            // Keep the same handle and source identity through that retry.
            if (processed != 0) Marshal.WriteInt32(processed, count);
            return 0;
        }
        catch (Exception exception) { return exception is OperationCanceledException ? unchecked((int)0x80004004) : Marshal.GetHRForException(exception); }
    }
    public int Seek(long offset, uint origin, nint position)
    {
        try
        {
            if (token.IsCancellationRequested) return unchecked((int)0x80004004);
            var value = file.Seek(offset, (SeekOrigin)origin); if (position != 0) Marshal.WriteInt64(position, value); return 0;
        }
        catch (Exception exception) { return Marshal.GetHRForException(exception); }
    }
    public void Dispose() => file.Dispose();
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class NativeOutputStream(Stream file, CancellationToken token, Action<long>? outputWritten = null, ArchiveOperationControl? control = null) : SequentialOutput, SeekableOutput
{
    public unsafe int Write(nint data, uint size, nint processed)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            control?.CheckStop(token);
            file.Write(new ReadOnlySpan<byte>((void*)data, checked((int)size)));
            outputWritten?.Invoke(file.Length);
            if (processed != 0) Marshal.WriteInt32(processed, checked((int)size));
            return 0;
        }
        catch (Exception exception) { return exception is OperationCanceledException ? unchecked((int)0x80004004) : Marshal.GetHRForException(exception); }
    }
    public int Seek(long offset, uint origin, nint position)
    {
        try { var value = file.Seek(offset, (SeekOrigin)origin); if (position != 0) Marshal.WriteInt64(position, value); return 0; }
        catch (Exception exception) { return Marshal.GetHRForException(exception); }
    }
    public int SetSize(ulong size)
    {
        try { file.SetLength(checked((long)size)); return 0; }
        catch (Exception exception) { return Marshal.GetHRForException(exception); }
    }
}
