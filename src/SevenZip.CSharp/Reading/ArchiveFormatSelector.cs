
namespace SevenZip.CSharp;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class ArchiveFormatSelector
{
    public static IReadOnlyList<SevenZipNativeFormat> Candidates(string path, SevenZipNativeLibrary library, ArchiveOperationContext? context = null)
    {
        context ??= new();
        var token = context.CancellationToken;
        path = ZipVolumePolicy.DirectoryPath(RarVolumePolicy.FirstPath(path,
            NativeReadSession.CreateRar5VolumeProbe(library, context.Password, token, context.Control)));
        using var file = ArchiveVolumeInput.Open(path, token);
        var available = library.Formats.Where(f => context.AllowedFormats is null || context.AllowedFormats.Contains(f.Name)).ToArray();
        if (EmbeddedArchivePolicy.IsPortableExecutable(path, token))
            return available.Where(f => f.CanFindSignature).ToArray();
        var matching = new List<SevenZipNativeFormat>();
        foreach (var format in available)
        {
            token.ThrowIfCancellationRequested();
            foreach (var signature in format.Signatures)
            {
                if ((long)signature.Offset + signature.Bytes.Count > file.Length) continue;
                file.Position = signature.Offset;
                var bytes = new byte[signature.Bytes.Count]; file.ReadExactly(bytes);
                if (bytes.SequenceEqual(signature.Bytes)) { matching.Add(format); break; }
            }
        }
        // A matching magic is only a candidate: invalid prefix bytes may imitate
        // a different handler. Keep head matches first, but let SDK Open try the
        // registered prefix-search handlers if those matches reject the input.
        if (matching.Count > 0)
            return matching.Concat(available.Where(f => f.CanFindSignature)).Distinct().ToArray();
        var extension = Path.GetExtension(path).TrimStart('.');
        return available.Where(f => f.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            // A prefix or misleading extension must not prevent the SDK from identifying the payload.
            .Concat(available.Where(f => f.CanFindSignature))
            .Concat(available.Where(f => f.Name.Equals("tar", StringComparison.OrdinalIgnoreCase)))
            .Distinct().ToArray();
    }
}
