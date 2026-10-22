using System.Buffers.Binary;

namespace SevenZip.CSharp;

/// <summary>Inspect executable containers as data; never execute their stub.</summary>
public static class EmbeddedArchivePolicy
{
    // Creation/resource editing bound only. Reading searches within the actual source file.
    public const long MaximumScan = 16 * 1024 * 1024;
    public static bool IsPortableExecutable(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var file = ArchiveVolumeInput.Open(path, token);
        if (file.Length < 64) return false;
        Span<byte> header = stackalloc byte[64]; file.ReadExactly(header);
        if (header[0] != 'M' || header[1] != 'Z') return false;
        var offset = BinaryPrimitives.ReadInt32LittleEndian(header[60..]);
        if (offset < 64 || offset > file.Length - 6) return false;
        file.Position = offset; Span<byte> signature = stackalloc byte[4]; file.ReadExactly(signature);
        return BinaryPrimitives.ReadUInt32LittleEndian(signature) == 0x00004550;
    }

    public static bool PrefixEquals(string source, string output, long length, CancellationToken token, ArchiveOperationControl? control = null)
    {
        if (length < 0) return false;
        using var input = ArchiveVolumeInput.Open(source, token); using var updated = ArchiveVolumeInput.Open(output, token);
        if (length > input.Length || length > updated.Length) return false;
        var before = new byte[64 * 1024]; var after = new byte[before.Length];
        while (length > 0)
        {
            control?.CheckStop(token); token.ThrowIfCancellationRequested();
            var count = (int)Math.Min(length, before.Length);
            input.ReadExactly(before.AsSpan(0, count)); updated.ReadExactly(after.AsSpan(0, count));
            if (!before.AsSpan(0, count).SequenceEqual(after.AsSpan(0, count))) return false;
            length -= count;
        }
        return true;
    }
}
