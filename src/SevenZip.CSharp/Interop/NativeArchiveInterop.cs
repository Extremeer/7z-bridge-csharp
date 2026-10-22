using System.Runtime.InteropServices;

namespace SevenZip.CSharp;

/// <summary>Shared SDK ABI. Low-level compatibility surface, not the final high-level API.</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public static class NativeArchiveInterop
{
    [ComVisible(true), Guid("23170F69-40C1-278A-0000-000600100000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface NativeOpenCallback
    {
        [PreserveSig] int SetTotal(nint files, nint bytes);
        [PreserveSig] int SetCompleted(nint files, nint bytes);
    }
    [ComVisible(true), Guid("23170F69-40C1-278A-0000-000500100000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface NativeReadPassword
    { [PreserveSig] int CryptoGetTextPassword(out nint value); }

    [ComVisible(true), Guid("23170F69-40C1-278A-0000-000600300000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface NativeOpenVolume
    {
        [PreserveSig] int GetProperty(uint property, out Variant value);
        [PreserveSig] int GetStream([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Interface)] out SeekableInput? stream);
    }


    [ComVisible(true), Guid("23170F69-40C1-278A-0000-000600200000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface NativeExtractCallback
    {
        [PreserveSig] int SetTotal(ulong total);
        [PreserveSig] int SetCompleted(nint completed);
        [PreserveSig] int GetStream(uint index, [MarshalAs(UnmanagedType.Interface)] out SequentialOutput? stream, int askMode);
        [PreserveSig] int PrepareOperation(int askMode);
        [PreserveSig] int SetOperationResult(int result);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Variant
    {
        private SevenZipNativeVariant native;
        public ushort Type { readonly get => native.Type; set => native.Type = value; }
        public ulong Value { readonly get => native.Value; set => native.Value = value; }
        public nint Pointer { readonly get => native.Pointer; set => native.Pointer = value; }
        public static Variant Text(string value) => new() { Type = 8, Pointer = Marshal.StringToBSTR(value) };
        public static Variant Number(uint value) => new() { Type = 19, Value = value };
        public static Variant Size(ulong value) => new() { Type = 21, Value = value };
        public static Variant Boolean(bool value) => new() { Type = 11, Value = value ? 0xffffUL : 0 };
    }

    [ComImport, Guid("23170F69-40C1-278A-0000-000600A00000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface NativeOutArchive
    {
        [PreserveSig] int UpdateItems([MarshalAs(UnmanagedType.Interface)] SequentialOutput stream, uint count,
            [MarshalAs(UnmanagedType.Interface)] NativeUpdateCallback callback);
        [PreserveSig] int GetFileTimeType(out uint type);
    }
    [ComImport, Guid("23170F69-40C1-278A-0000-000600030000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface NativeSetProperties
    { [PreserveSig] int SetProperties([In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] nint[] names, nint values, uint count); }
    [ComImport, Guid("23170F69-40C1-278A-0000-000600600000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface NativeInArchive
    {
        [PreserveSig] int Open([MarshalAs(UnmanagedType.Interface)] SeekableInput stream, nint maxStart, nint callback);
        [PreserveSig] int Close();
        [PreserveSig] int GetNumberOfItems(out uint count);
        [PreserveSig] int GetProperty(uint index, uint property, out Variant value);
        [PreserveSig] int Extract(nint indices, uint count, int test, nint callback);
        [PreserveSig] int GetArchiveProperty(uint property, out Variant value);
        [PreserveSig] int GetNumberOfProperties(out uint count);
        [PreserveSig] int GetPropertyInfo(uint index, out nint name, out uint property, out ushort type);
        [PreserveSig] int GetNumberOfArchiveProperties(out uint count);
        [PreserveSig] int GetArchivePropertyInfo(uint index, out nint name, out uint property, out ushort type);
    }
    [ComVisible(true), Guid("23170F69-40C1-278A-0000-000600800000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface NativeUpdateCallback
    {
        [PreserveSig] int SetTotal(ulong total);
        [PreserveSig] int SetCompleted(nint completed);
        [PreserveSig] int GetUpdateItemInfo(uint index, out int newData, out int newProperties, out uint archiveIndex);
        [PreserveSig] int GetProperty(uint index, uint property, out Variant value);
        [PreserveSig] int GetStream(uint index, [MarshalAs(UnmanagedType.Interface)] out SequentialInput? stream);
        [PreserveSig] int SetOperationResult(int result);
    }
    [ComVisible(true), Guid("23170F69-40C1-278A-0000-000400040000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface RatioCallback
    { [PreserveSig] int SetRatioInfo(nint input, nint output); }
    // IArchiveUpdateCallbackFile, IArchive.h: GetStream2 then ReportOperation.
    [ComVisible(true), Guid("23170F69-40C1-278A-0000-000600830000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface NativeUpdateFileCallback
    {
        [PreserveSig] int GetStream2(uint index, [MarshalAs(UnmanagedType.Interface)] out SequentialInput? stream, uint operation);
        [PreserveSig] int ReportOperation(uint indexType, uint index, uint operation);
    }
    [ComVisible(true), Guid("23170F69-40C1-278A-0000-000500110000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface PasswordCallback
    { [PreserveSig] int CryptoGetTextPassword2(out int defined, out nint password); }
    [ComVisible(true), Guid("23170F69-40C1-278A-0000-000300010000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface SequentialInput
    { [PreserveSig] int Read(nint data, uint size, nint processed); }
    [ComVisible(true), Guid("23170F69-40C1-278A-0000-000300030000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface SeekableInput
    {
        [PreserveSig] int Read(nint data, uint size, nint processed);
        [PreserveSig] int Seek(long offset, uint origin, nint position);
    }
    [ComVisible(true), Guid("23170F69-40C1-278A-0000-000300020000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface SequentialOutput
    { [PreserveSig] int Write(nint data, uint size, nint processed); }
    [ComVisible(true), Guid("23170F69-40C1-278A-0000-000300040000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface SeekableOutput
    {
        [PreserveSig] int Write(nint data, uint size, nint processed);
        [PreserveSig] int Seek(long offset, uint origin, nint position);
        [PreserveSig] int SetSize(ulong size);
    }

}
