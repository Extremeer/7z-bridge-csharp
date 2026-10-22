using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Variant = SevenZip.CSharp.SevenZipNativeVariant;

namespace SevenZip.CSharp;

/// <summary>
/// Process-lifetime SDK module and immutable handler catalog. Modules must not be
/// unloaded while archive RCWs or callbacks can retain pointers into their code.
/// </summary>
public sealed class SevenZipNativeLibrary
{
    private static readonly ConcurrentDictionary<string, Lazy<SevenZipNativeLibrary>> modules = new(StringComparer.OrdinalIgnoreCase);
    private readonly nint module;
    public string LibraryPath { get; }
    public IReadOnlyList<SevenZipNativeFormat> Formats { get; }
    private readonly Lazy<IReadOnlyList<SevenZipNativeCodec>> codecs;
    public IReadOnlyList<SevenZipNativeCodec> Codecs
    {
        get
        {
            try { return codecs.Value; }
            catch (Exception exception) when (exception is InvalidDataException or COMException)
            { throw SevenZipNativeDependencyException.FromDiscoveryFailure(exception); }
        }
    }

    private SevenZipNativeLibrary(string path)
    {
        LibraryPath = path;
        module = NativeLibrary.Load(path);
        try { Formats = ReadFormats(); }
        catch { NativeLibrary.Free(module); throw; } // No objects have escaped yet.
        codecs = new(ReadCodecs, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public static SevenZipNativeLibrary Load(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("7-Zip COM requires Windows.");
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        try { _ = new SevenZipNativeSettings(fullPath); }
        catch (Exception exception) when (exception is IOException or BadImageFormatException or UnauthorizedAccessException)
        { throw SevenZipNativeDependencyException.FromDiscoveryFailure(exception); }
        var lazy = modules.GetOrAdd(fullPath, key => new Lazy<SevenZipNativeLibrary>(() => new(key),
            LazyThreadSafetyMode.ExecutionAndPublication));
        try { return lazy.Value; }
        catch (Exception exception) when (exception is SevenZipNativeDependencyException or DllNotFoundException
            or BadImageFormatException or EntryPointNotFoundException or InvalidDataException or COMException or IOException or UnauthorizedAccessException)
        {
            // A failed initialization owns no escaped RCWs. Evict this exact Lazy
            // so a repaired deployment can retry without removing a concurrent replacement.
            ((ICollection<KeyValuePair<string, Lazy<SevenZipNativeLibrary>>>)modules).Remove(new(fullPath, lazy));
            throw SevenZipNativeDependencyException.FromDiscoveryFailure(exception);
        }
    }

    public SevenZipNativeFormat? FindFormat(string name) =>
        Formats.FirstOrDefault(format => format.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    // Extension candidates are hints, not signature-based detection or app capabilities.
    public IReadOnlyList<SevenZipNativeFormat> FindExtensionCandidates(string extension) =>
        Array.AsReadOnly(Formats.Where(format => format.Extensions.Contains(extension.TrimStart('.'), StringComparer.OrdinalIgnoreCase)).ToArray());

    internal nint GetExport(string name)
    {
        try { return NativeLibrary.GetExport(module, name); }
        catch (EntryPointNotFoundException exception) { throw SevenZipNativeDependencyException.FromDiscoveryFailure(exception); }
    }

    private IReadOnlyList<SevenZipNativeCodec> ReadCodecs()
    {
        var getCount = Marshal.GetDelegateForFunctionPointer<GetNumberOfFormats>(GetExport("GetNumberOfMethods"));
        var getProperty = Marshal.GetDelegateForFunctionPointer<GetHandlerProperty>(GetExport("GetMethodProperty"));
        Marshal.ThrowExceptionForHR(getCount(out var count));
        var result = new List<SevenZipNativeCodec>();
        for (uint index = 0; index < count; index++)
        {
            var id = Read(index, 0, value => value.Type == 21 ? value.Value
                : throw new InvalidDataException("Invalid SDK codec identifier."));
            var name = Read(index, 1, value => value.Type == 8 && value.Pointer != 0
                ? Marshal.PtrToStringBSTR(value.Pointer) : throw new InvalidDataException("Invalid SDK codec name."));
            var decoder = Read(index, 7, value => value.BooleanValue);
            var encoder = Read(index, 8, value => value.BooleanValue);
            var filter = Read(index, 10, value => value.BooleanValue);
            result.Add(new(id, name, encoder, decoder, filter));
        }
        return result.AsReadOnly();

        T Read<T>(uint index, uint property, Func<Variant, T> convert)
        {
            Marshal.ThrowExceptionForHR(getProperty(index, property, out var value));
            try { return convert(value); }
            finally { Clear(ref value); }
        }
    }

    private ReadOnlyCollection<SevenZipNativeFormat> ReadFormats()
    {
        var getCount = Marshal.GetDelegateForFunctionPointer<GetNumberOfFormats>(GetExport("GetNumberOfFormats"));
        var getProperty = Marshal.GetDelegateForFunctionPointer<GetHandlerProperty>(GetExport("GetHandlerProperty2"));
        Marshal.ThrowExceptionForHR(getCount(out var count));
        var formats = new List<SevenZipNativeFormat>();
        for (uint index = 0; index < count; index++)
        {
            var name = ReadString(index, 0);
            var extensions = ReadString(index, 2).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Marshal.ThrowExceptionForHR(getProperty(index, 1, out var classId));
            Guid id;
            try
            {
                // SDK kClassID is a binary, 16-byte VT_BSTR, not a textual GUID.
                if (classId.Type != 8 || classId.Pointer == 0 || SysStringByteLen(classId.Pointer) != 16)
                    throw new InvalidDataException("7-Zip returned an invalid handler ClassID.");
                var bytes = new byte[16]; Marshal.Copy(classId.Pointer, bytes, 0, bytes.Length); id = new Guid(bytes);
            }
            finally { Clear(ref classId); }
            Marshal.ThrowExceptionForHR(getProperty(index, 4, out var update));
            bool canUpdate;
            try { canUpdate = update.BooleanValue; }
            finally { Clear(ref update); }
            Marshal.ThrowExceptionForHR(getProperty(index, 8, out var signatureOffset));
            uint offset;
            try { offset = signatureOffset.Type == 19 ? (uint)signatureOffset.Value : 0; }
            finally { Clear(ref signatureOffset); }
            Marshal.ThrowExceptionForHR(getProperty(index, 11, out var handlerFlags));
            uint flags;
            try
            {
                if (handlerFlags.Type is not (0 or 19)) throw new InvalidDataException("Invalid SDK handler flags.");
                flags = handlerFlags.Type == 19 ? (uint)handlerFlags.Value : 0;
            }
            finally { Clear(ref handlerFlags); }
            var signatures = new List<SevenZipNativeSignature>();
            var single = ReadBinary(index, 6);
            if (single.Length > 0) signatures.Add(new(offset, Array.AsReadOnly(single)));
            var multiple = ReadBinary(index, 7);
            for (var position = 0; position < multiple.Length;)
            {
                var length = multiple[position++];
                if (length == 0 || position + length > multiple.Length) throw new InvalidDataException("Invalid SDK multi-signature.");
                signatures.Add(new(offset, Array.AsReadOnly(multiple.AsSpan(position, length).ToArray())));
                position += length;
            }
            formats.Add(new(index, name, id, Array.AsReadOnly(extensions), canUpdate, signatures.AsReadOnly()) { Flags = flags });
        }
        return formats.AsReadOnly();

        string ReadString(uint index, uint property)
        {
            Marshal.ThrowExceptionForHR(getProperty(index, property, out var value));
            try { return value.Type == 8 && value.Pointer != 0 ? Marshal.PtrToStringBSTR(value.Pointer) : string.Empty; }
            finally { Clear(ref value); }
        }
        byte[] ReadBinary(uint index, uint property)
        {
            Marshal.ThrowExceptionForHR(getProperty(index, property, out var value));
            try
            {
                if (value.Type != 8 || value.Pointer == 0) return [];
                var bytes = new byte[checked((int)SysStringByteLen(value.Pointer))];
                Marshal.Copy(value.Pointer, bytes, 0, bytes.Length);
                return bytes;
            }
            finally { Clear(ref value); }
        }
    }

    private static void Clear(ref Variant value) => Marshal.ThrowExceptionForHR(PropVariantClear(ref value));
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetNumberOfFormats(out uint count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetHandlerProperty(uint index, uint property, out Variant value);
    [DllImport("oleaut32.dll")] private static extern uint SysStringByteLen(nint value);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref Variant value);
}

public sealed record SevenZipNativeFormat(uint Index, string Name, Guid ClassId,
    IReadOnlyList<string> Extensions, bool CanUpdate, IReadOnlyList<SevenZipNativeSignature> Signatures)
{
    public uint Flags { get; init; }
    public bool CanFindSignature => (Flags & (1U << 3)) != 0;
}

public sealed record SevenZipNativeSignature(uint Offset, IReadOnlyList<byte> Bytes);

public sealed record SevenZipNativeCodec(ulong Id, string Name, bool CanEncode, bool CanDecode, bool IsFilter = false);
