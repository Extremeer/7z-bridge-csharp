using System.Runtime.InteropServices;

namespace SevenZip.CSharp;

/// <summary>
/// Windows PROPVARIANT ABI: 8-byte header followed by the native union.
/// The vector arm supplies pointer-dependent union size/alignment (16 bytes
/// overall on x86, 24 on x64/ARM64). A fixed 24-byte callback corrupts x86
/// callers, and fixed-stride property arrays read the wrong subsequent value.
/// Only SDK scalar/BSTR values are interpreted by this bridge.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SevenZipNativeVariant
{
    public ushort Type;
    private ushort reserved1, reserved2, reserved3;
    private Payload data;
    public ulong Value { readonly get => data.Number; set => data.Number = value; }
    public nint Pointer { readonly get => data.Pointer; set => data.Pointer = value; }
    // VARIANT_BOOL occupies only the low 16 bits; other union bytes are unspecified.
    public readonly bool BooleanValue => Type == 11 && (data.Number & 0xffff) != 0;

    [StructLayout(LayoutKind.Explicit)]
    private struct Payload
    {
        [FieldOffset(0)] public ulong Number;
        [FieldOffset(0)] public nint Pointer;
        [FieldOffset(0)] private Vector vector;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Vector
    {
        private uint count;
        private nint pointer;
    }
}
