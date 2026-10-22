using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SevenZip.CSharp;

/// <summary>SDK ICoder.h hash ABI. Each instance owns its native hasher and never unloads the shared module.</summary>
[SupportedOSPlatform("windows")]
internal sealed class SevenZipNativeHasher : IDisposable
{
    private IHasher? hasher;
    private readonly string algorithm;
    private SevenZipNativeHasher(IHasher hasher, string algorithm)
    {
        this.hasher = hasher; this.algorithm = algorithm;
        try { hasher.Init(); } catch { Dispose(); throw; }
    }
    public static SevenZipNativeHasher? Create(SevenZipNativeLibrary library, string algorithm)
    {
        var get = Marshal.GetDelegateForFunctionPointer<GetHashers>(library.GetExport("GetHashers"));
        Marshal.ThrowExceptionForHR(get(out var pointer));
        object? catalog = null;
        try
        {
            catalog = Marshal.GetObjectForIUnknown(pointer); var hashers = (IHashers)catalog;
            for (uint index = 0; index < hashers.GetNumHashers(); index++)
            {
                Marshal.ThrowExceptionForHR(hashers.GetHasherProp(index, 1, out var property));
                string? name;
                try { name = property.Type == 8 ? Marshal.PtrToStringBSTR(property.Pointer) : null; }
                finally { Marshal.ThrowExceptionForHR(PropVariantClear(ref property)); }
                if (!string.Equals(name, algorithm, StringComparison.OrdinalIgnoreCase)) continue;
                Marshal.ThrowExceptionForHR(hashers.CreateHasher(index, out var instance));
                try { return new SevenZipNativeHasher((IHasher)Marshal.GetObjectForIUnknown(instance), algorithm); }
                finally { Marshal.Release(instance); }
            }
            return null;
        }
        finally { if (catalog is not null) Marshal.FinalReleaseComObject(catalog); Marshal.Release(pointer); }
    }
    public void Update(nint data, uint length) { ObjectDisposedException.ThrowIf(hasher is null, this); hasher.Update(data, length); }
    public string Finish()
    {
        ObjectDisposedException.ThrowIf(hasher is null, this);
        var size = hasher.GetDigestSize();
        if (size is 0 or > 1024) throw new InvalidDataException("Invalid SDK digest size.");
        var bytes = new byte[size]; var pointer = Marshal.AllocCoTaskMem((int)size);
        try { hasher.Final(pointer); Marshal.Copy(pointer, bytes, 0, (int)size); }
        finally { Marshal.FreeCoTaskMem(pointer); }
        // SDK CRC digests are little-endian integers; the displayed CRC is most-significant-byte first.
        if (algorithm.Equals("CRC32", StringComparison.OrdinalIgnoreCase) || algorithm.Equals("CRC64", StringComparison.OrdinalIgnoreCase)) Array.Reverse(bytes);
        return Convert.ToHexString(bytes);
    }
    public void Dispose() { if (hasher is not null) { Marshal.FinalReleaseComObject(hasher); hasher = null; } }
    [StructLayout(LayoutKind.Sequential)]
    public struct Property
    {
        private SevenZipNativeVariant native;
        public ushort Type { readonly get => native.Type; set => native.Type = value; }
        public nint Pointer { readonly get => native.Pointer; set => native.Pointer = value; }
    }
    [ComImport, Guid("23170F69-40C1-278A-0000-000400C00000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IHasher
    {
        [PreserveSig] void Init();
        [PreserveSig] void Update(nint data, uint size);
        [PreserveSig] void Final(nint digest);
        [PreserveSig] uint GetDigestSize();
    }
    [ComImport, Guid("23170F69-40C1-278A-0000-000400C10000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IHashers
    {
        [PreserveSig] uint GetNumHashers();
        [PreserveSig] int GetHasherProp(uint index, uint property, out Property value);
        [PreserveSig] int CreateHasher(uint index, out nint value);
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetHashers(out nint value);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref Property value);
}
