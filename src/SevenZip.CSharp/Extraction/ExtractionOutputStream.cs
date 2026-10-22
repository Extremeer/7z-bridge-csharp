using System.Runtime.InteropServices;
using static SevenZip.CSharp.NativeArchiveInterop;
namespace SevenZip.CSharp;

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class ExtractionOutputStream(Stream file, ExtractionStatistics statistics, CancellationToken token,
    ArchiveOperationControl? control = null, Func<Exception, int>? failure = null) : SequentialOutput
{
    public unsafe int Write(nint data, uint size, nint processed)
    {
        try
        {
            if (processed != 0) Marshal.WriteInt32(processed, 0);
            if (!(control?.TryCheckStop(token) ?? !token.IsCancellationRequested)) return unchecked((int)0x80004004);
            file.Write(new ReadOnlySpan<byte>((void*)data, checked((int)size)));
            statistics.RecordWrite(size);
            if (processed != 0) Marshal.WriteInt32(processed, checked((int)size));
            return 0;
        }
        catch (Exception exception)
        {
            return failure is not null ? failure(exception) : exception is OperationCanceledException
                ? unchecked((int)0x80004004) : Marshal.GetHRForException(exception);
        }
    }
}
