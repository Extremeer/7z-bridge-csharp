using System.Buffers.Binary;
using System.Text;

namespace SevenZip.CSharp;

/// <summary>Bounded Windows compiled .res reader. Only RT_VERSION entries may reach the PE updater.</summary>
public static class SfxVersionResources
{
    public const int MaximumBytes = 1024 * 1024;
    public sealed record Entry(int Id, string? Name, ushort Language, byte[] Data);

    public static IReadOnlyList<Entry> Read(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var source = ArchiveVolumeInput.Open(path, token);
        if (source.Length > MaximumBytes) throw Invalid();
        var bytes = new byte[checked((int)source.Length)];
        source.ReadExactly(bytes);
        token.ThrowIfCancellationRequested();
        return Parse(bytes);
    }

    public static IReadOnlyList<Entry> Parse(byte[] bytes)
    {
        if (bytes.Length is < 32 or > MaximumBytes) throw Invalid();
        var result = new List<Entry>();
        var keys = new HashSet<(int, string?, ushort)>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 8 || result.Count >= 256) throw Invalid();
            var size = U32(bytes, offset);
            var header = U32(bytes, offset + 4);
            if (header < 24 || header > (uint)(bytes.Length - offset) || (header & 3) != 0
                || (ulong)offset + header + size > (ulong)bytes.Length) throw Invalid();
            var endHeader = offset + (int)header;
            var cursor = offset + 8;
            var type = ReadIdentifier(bytes, ref cursor, endHeader);
            var name = ReadIdentifier(bytes, ref cursor, endHeader);
            cursor = Align(cursor);
            if (endHeader - cursor != 16) throw Invalid();
            var language = U16(bytes, cursor + 6);
            var dataOffset = endHeader;
            var endData = checked(dataOffset + (int)size);
            if (offset == 0 && size == 0 && header == 32 && type == (0, null) && name == (0, null))
            {
                // RC's canonical empty initial entry is not a resource to install.
                if (bytes.AsSpan(16, 16).IndexOfAnyExcept((byte)0) >= 0) throw Invalid();
            }
            else
            {
                if (type != (16, null) || size == 0 || name == (0, null) || name == (0, "")
                    || !keys.Add((name.Id, name.Name, language))) throw Invalid();
                var data = bytes.AsSpan(dataOffset, (int)size).ToArray();
                ValidateVersion(data);
                result.Add(new(name.Id, name.Name, language, data));
            }
            var next = Align(endData);
            if (next > bytes.Length || bytes.AsSpan(endData, next - endData).IndexOfAnyExcept((byte)0) >= 0) throw Invalid();
            offset = next;
        }
        if (result.Count == 0) throw Invalid();
        return result;
    }

    private static (int Id, string? Name) ReadIdentifier(byte[] bytes, ref int cursor, int end)
    {
        if (end - cursor < 2) throw Invalid();
        if (U16(bytes, cursor) == ushort.MaxValue)
        {
            if (end - cursor < 4) throw Invalid();
            var id = U16(bytes, cursor + 2); cursor += 4;
            return (id, null);
        }
        return (0, ReadString(bytes, ref cursor, end));
    }

    private static string ReadString(byte[] bytes, ref int cursor, int end)
    {
        var start = cursor;
        while (end - cursor >= 2 && cursor - start <= 512)
        {
            var value = U16(bytes, cursor); cursor += 2;
            if (value == 0)
            {
                try { return new UnicodeEncoding(false, false, true).GetString(bytes, start, cursor - start - 2); }
                catch (DecoderFallbackException) { throw Invalid(); }
            }
        }
        throw Invalid();
    }

    private static void ValidateVersion(byte[] bytes)
    {
        if (bytes.Length < 6 || U16(bytes, 0) != bytes.Length || U16(bytes, 4) != 0) throw Invalid();
        var count = 0;
        ValidateBlock(bytes, 0, bytes.Length, 0, ref count);
    }

    private static void ValidateBlock(byte[] bytes, int start, int enclosingEnd, int depth, ref int count)
    {
        if (depth > 32 || ++count > 1024 || enclosingEnd - start < 6) throw Invalid();
        var length = U16(bytes, start);
        var valueLength = U16(bytes, start + 2);
        var type = U16(bytes, start + 4);
        if (length < 6 || length > enclosingEnd - start || type > 1) throw Invalid();
        var end = start + length;
        var cursor = start + 6;
        var key = ReadString(bytes, ref cursor, end);
        cursor = Align(cursor);
        var valueBytes = type == 1 ? valueLength * 2 : valueLength;
        if (cursor > end || valueBytes > end - cursor) throw Invalid();
        if (depth == 0 && (key != "VS_VERSION_INFO" || valueLength is not (0 or 52)
            || valueLength == 52 && (U32(bytes, cursor) != 0xFEEF04BD || U32(bytes, cursor + 4) != 0x00010000))) throw Invalid();
        cursor += valueBytes;
        while (cursor < end)
        {
            var aligned = Align(cursor);
            if (aligned >= end)
            {
                if (end - cursor > 3 || bytes.AsSpan(cursor, end - cursor).IndexOfAnyExcept((byte)0) >= 0) throw Invalid();
                break;
            }
            if (bytes.AsSpan(cursor, aligned - cursor).IndexOfAnyExcept((byte)0) >= 0) throw Invalid();
            cursor = aligned;
            ValidateBlock(bytes, cursor, end, depth + 1, ref count);
            cursor += U16(bytes, cursor);
        }
    }

    private static int Align(int value) => checked((value + 3) & ~3);
    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static ArgumentException Invalid() => new("Invalid compiled SFX version resource.");
}
