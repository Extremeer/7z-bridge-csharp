using System.Runtime.InteropServices;

namespace SevenZip.CSharp;

/// <summary>DLL-only discovery and PE architecture validation; never probes an EXE or PATH.</summary>
public sealed class SevenZipNativeSettings
{
    public string LibraryPath { get; }
    public Architecture Architecture { get; }

    public SevenZipNativeSettings(string libraryPath, Architecture? architecture = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);
        LibraryPath = Path.GetFullPath(libraryPath);
        Architecture = architecture ?? RuntimeInformation.ProcessArchitecture;
        if (!File.Exists(LibraryPath)) throw new FileNotFoundException("7-Zip SDK DLL was not found.", LibraryPath);
        if (ReadArchitecture(LibraryPath) != Architecture)
            throw new BadImageFormatException($"7-Zip SDK DLL does not match process architecture {Architecture}.", LibraryPath);
    }

    public static SevenZipNativeSettings AutoDiscover() => Discover(AppContext.BaseDirectory,
        Environment.GetEnvironmentVariable("SEVENZIP_CSHARP_DLL_PATH"));

    public static SevenZipNativeSettings Discover(string baseDirectory, string? explicitPath = null,
        Architecture? architecture = null, bool searchParents = false)
    {
        // An explicit override is authoritative. Do not silently load a different DLL.
        if (!string.IsNullOrWhiteSpace(explicitPath)) return new(explicitPath, architecture);
        var processArchitecture = architecture ?? RuntimeInformation.ProcessArchitecture;
        BadImageFormatException? incompatible = null;
        var probe = new DirectoryInfo(Path.GetFullPath(baseDirectory));
        while (probe is not null)
        {
            foreach (var path in new[]
            {
                Path.Combine(probe.FullName, "7zip", processArchitecture.ToString().ToLowerInvariant(), "7z.dll"),
                Path.Combine(probe.FullName, "7zip", "7z.dll"),
                Path.Combine(probe.FullName, "third_party", "7zip", processArchitecture.ToString().ToLowerInvariant(), "7z.dll"),
                Path.Combine(probe.FullName, "third_party", "7zip", "7z.dll"),
                Path.Combine(probe.FullName, "7z.dll"),
            })
            {
                if (!File.Exists(path)) continue;
                try { return new(path, processArchitecture); }
                catch (BadImageFormatException exception) { incompatible = exception; }
            }
            probe = searchParents ? probe.Parent : null;
        }
        if (incompatible is not null) throw incompatible;
        throw new FileNotFoundException("Cannot locate 7z.dll. Provide an explicit path or SEVENZIP_CSHARP_DLL_PATH.");
    }

    internal static Architecture ReadArchitecture(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (reader.BaseStream.Length < 64 || reader.ReadUInt16() != 0x5a4d) throw Invalid();
        reader.BaseStream.Position = 0x3c;
        var offset = reader.ReadInt32();
        if (offset < 64 || offset > reader.BaseStream.Length - 6) throw Invalid();
        reader.BaseStream.Position = offset;
        if (reader.ReadUInt32() != 0x00004550) throw Invalid();
        return reader.ReadUInt16() switch
        {
            0x014c => Architecture.X86,
            0x8664 => Architecture.X64,
            0xaa64 => Architecture.Arm64,
            _ => throw Invalid(),
        };
        BadImageFormatException Invalid() => new("Invalid or unsupported 7-Zip SDK PE image.", path);
    }
}
