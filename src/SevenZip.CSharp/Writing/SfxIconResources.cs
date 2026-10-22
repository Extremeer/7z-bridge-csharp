using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace SevenZip.CSharp;

/// <summary>Edits only an owned PE copy, loading resource tables as data, never as executable code.</summary>
public static class SfxIconResources
{
    private const int MaximumIconBytes = 8 * 1024 * 1024;
    private sealed record Resource(int Type, int Id, string? Name, ushort Language);

    public static (byte[] Group, byte[][] Images) Parse(byte[] bytes)
    {
        if (bytes.Length < 6 || bytes.Length > MaximumIconBytes || BinaryPrimitives.ReadUInt16LittleEndian(bytes) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2)) != 1) throw new ArgumentException("Invalid ICO header.");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));
        var tableEnd = 6 + count * 16;
        if (count is 0 or > 256 || tableEnd > bytes.Length) throw new ArgumentException("Invalid ICO directory.");
        var group = new byte[6 + count * 14]; bytes.AsSpan(0, 6).CopyTo(group);
        var images = new byte[count][];
        for (var index = 0; index < count; index++)
        {
            var entry = bytes.AsSpan(6 + index * 16, 16);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            if (entry[3] != 0 || size == 0 || offset < tableEnd || (ulong)offset + size > (ulong)bytes.Length)
                throw new ArgumentException("Invalid ICO image range.");
            entry[..12].CopyTo(group.AsSpan(6 + index * 14));
            BinaryPrimitives.WriteUInt16LittleEndian(group.AsSpan(6 + index * 14 + 12), (ushort)(index + 1));
            images[index] = bytes.AsSpan((int)offset, (int)size).ToArray();
        }
        return (group, images);
    }

    public static void Prepare(string module, string icon, string destination, Action markOwned, CancellationToken token)
        => PrepareResources(module, icon, null, destination, markOwned, token);

    public static void PrepareResources(string module, string? icon, string? version, string destination, Action markOwned, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(markOwned);
        token.ThrowIfCancellationRequested();
        (byte[] Group, byte[][] Images)? parsed = null;
        if (icon is not null)
        {
            using var source = ArchiveVolumeInput.Open(icon, token);
            if (source.Length > MaximumIconBytes) throw new ArgumentException("ICO input is too large.");
            var bytes = new byte[(int)source.Length]; source.ReadExactly(bytes);
            parsed = Parse(bytes);
        }
        var versions = version is null ? null : SfxVersionResources.Read(version, token);
        token.ThrowIfCancellationRequested();
        using (var source = ArchiveVolumeInput.Open(module, token))
        using (var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            markOwned();
            var buffer = new byte[64 * 1024]; int read;
            while ((read = source.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); target.Write(buffer, 0, read); }
        }
        var oldResources = ReadResources(destination, versions is not null);
        var update = BeginUpdateResource(destination, false);
        if (update == 0) throw Failure();
        var committed = false;
        try
        {
            foreach (var old in oldResources)
            {
                token.ThrowIfCancellationRequested();
                // Keep group names/IDs/languages used by the stub's window/dialog code.
                if (old.Type == 16 && versions is not null) Write(update, old, null);
                else if (old.Type != 16 && parsed is { } iconData) Write(update, old, old.Type == 14 ? iconData.Group : null);
            }
            if (parsed is { } icons)
            {
                for (var index = 0; index < icons.Images.Length; index++)
                { token.ThrowIfCancellationRequested(); Write(update, new(3, index + 1, null, 0), icons.Images[index]); }
                Write(update, new(14, 1, null, 0), icons.Group);
            }
            if (versions is not null)
                foreach (var entry in versions)
                { token.ThrowIfCancellationRequested(); Write(update, new(16, entry.Id, entry.Name, entry.Language), entry.Data); }
            token.ThrowIfCancellationRequested();
            var handle = update; update = 0;
            if (!EndUpdateResource(handle, false)) throw Failure();
            committed = true;
        }
        finally { if (update != 0) EndUpdateResource(update, !committed); }
        if (new FileInfo(destination).Length >= EmbeddedArchivePolicy.MaximumScan
            || !EmbeddedArchivePolicy.IsPortableExecutable(destination, token)) throw new ArgumentException("Updated SFX module is not supported.");
    }

    private static List<Resource> ReadResources(string path, bool includeVersion)
    {
        var module = LoadLibraryEx(path, 0, 0x2 | 0x20); // DATAFILE | IMAGE_RESOURCE: no imports or entry point executed.
        if (module == 0) throw Failure();
        try
        {
            var result = new List<Resource>();
            foreach (var type in includeVersion ? new[] { 3, 14, 16 } : new[] { 3, 14 })
            {
                Exception? callbackError = null;
                EnumNames names = (handle, resourceType, name, _) =>
                {
                    try
                    {
                        var numeric = ((nuint)name >> 16) == 0;
                        var text = numeric ? null : Marshal.PtrToStringUni(name);
                        EnumLanguages languages = (_, _, _, language, _) =>
                        {
                            try { result.Add(new(type, numeric ? (int)name : 0, text, language)); return true; }
                            catch (Exception error) { callbackError = error; return false; }
                        };
                        var ok = EnumResourceLanguages(handle, resourceType, name, languages, 0);
                        if (!ok && callbackError is null) callbackError = Failure();
                        GC.KeepAlive(languages); return ok;
                    }
                    catch (Exception error) { callbackError = error; return false; }
                };
                var success = EnumResourceNames(module, (nint)type, names, 0);
                var errorCode = Marshal.GetLastWin32Error(); GC.KeepAlive(names);
                if (callbackError is not null) ExceptionDispatchInfo.Capture(callbackError).Throw();
                if (!success && errorCode != 1813) throw new IOException("Unable to read SFX resources.", new Win32Exception(errorCode));
            }
            return result;
        }
        finally { FreeLibrary(module); }
    }

    private static void Write(nint update, Resource resource, byte[]? data)
    {
        var name = resource.Name is null ? (nint)resource.Id : Marshal.StringToHGlobalUni(resource.Name);
        try { if (!UpdateResource(update, (nint)resource.Type, name, resource.Language, data, (uint)(data?.Length ?? 0))) throw Failure(); }
        finally { if (resource.Name is not null) Marshal.FreeHGlobal(name); }
    }
    private static IOException Failure() => new("SFX resource operation failed.", new Win32Exception(Marshal.GetLastWin32Error()));
    private delegate bool EnumNames(nint module, nint type, nint name, nint parameter);
    private delegate bool EnumLanguages(nint module, nint type, nint name, ushort language, nint parameter);
    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryEx(string path, nint file, uint flags);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(nint module);
    [DllImport("kernel32.dll", EntryPoint = "EnumResourceNamesW", SetLastError = true)]
    private static extern bool EnumResourceNames(nint module, nint type, EnumNames callback, nint parameter);
    [DllImport("kernel32.dll", EntryPoint = "EnumResourceLanguagesW", SetLastError = true)]
    private static extern bool EnumResourceLanguages(nint module, nint type, nint name, EnumLanguages callback, nint parameter);
    [DllImport("kernel32.dll", EntryPoint = "BeginUpdateResourceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint BeginUpdateResource(string path, bool deleteExisting);
    [DllImport("kernel32.dll", EntryPoint = "UpdateResourceW", SetLastError = true)]
    private static extern bool UpdateResource(nint update, nint type, nint name, ushort language, byte[]? data, uint size);
    [DllImport("kernel32.dll", EntryPoint = "EndUpdateResourceW", SetLastError = true)]
    private static extern bool EndUpdateResource(nint update, bool discard);
}
