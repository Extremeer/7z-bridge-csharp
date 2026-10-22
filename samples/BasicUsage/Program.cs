using SevenZip.CSharp;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: BasicUsage <absolute path to 7z.dll> [archive path]");
    return 2;
}

var library = SevenZipNativeLibrary.Load(args[0]);
foreach (var format in library.Formats)
    Console.WriteLine($"{format.Name}: update={format.CanUpdate}");
if (args.Length == 2)
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Archive inspection requires Windows.");
    var snapshot = await ArchiveInspector.InspectAsync(args[0], args[1]);
    if (snapshot is null) return 1;
    foreach (var entry in snapshot.Entries)
        Console.WriteLine($"{entry.Path.Value}: {entry.UncompressedSize} bytes, directory={entry.IsDirectory}");
}
return 0;
