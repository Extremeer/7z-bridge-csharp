using System.Text.RegularExpressions;
using System.Buffers.Binary;

namespace SevenZip.CSharp;

/// <summary>Only the selected RAR family in the same directory may be supplied to the SDK.</summary>
public static class RarVolumePolicy
{
    /// <summary>Returns a password-aware volume-index probe without exposing a native session.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static Func<string, ulong?> CreateVolumeProbe(SevenZipNativeLibrary library, ArchiveOperationContext? context = null)
    {
        context ??= new();
        return NativeReadSession.CreateRar5VolumeProbe(library, context.Password, context.CancellationToken, context.Control);
    }

    private static readonly Regex parts = new(@"^(.*\.part)([0-9]+)(\.rar)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex numeric = new(@"^(.+\.)([0-9]{3,})(\.rar)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static bool RequiresSdkVolumeProbe(string path)
    {
        if (!numeric.IsMatch(Path.GetFileName(path)) || LegacyVolumeFlags(path) is not null || Rar5VolumeIndex(path) is not null) return false;
        RejectReparseVolume(path);
        using var stream = File.OpenRead(path); Span<byte> signature = stackalloc byte[8];
        return stream.ReadAtLeast(signature, signature.Length, false) == signature.Length && signature.SequenceEqual("Rar!\x1a\x07\x01\0"u8);
    }
    public static bool IsNumbered(string path) => parts.IsMatch(Path.GetFileName(path))
        || Regex.IsMatch(Path.GetExtension(path), @"^\.[r-z][0-9]{2}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        || numeric.IsMatch(Path.GetFileName(path)) && HasLegacyVolumeHeader(path);
    public static string FirstPath(string path, Func<string, ulong?>? sdkProbe = null)
    {
        var match = parts.Match(Path.GetFileName(path));
        if (match.Success || Regex.IsMatch(Path.GetExtension(path), @"^\.[r-z][0-9]{2}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            RejectReparseVolume(path);
        if (!match.Success)
        {
            var numbered = numeric.Match(Path.GetFileName(path));
            if (numbered.Success && HasLegacyVolumeHeader(path, sdkProbe))
            {
                return NumericFirstPath(path, numbered, sdkProbe);
            }
            if (!IsNumbered(path)) return path;
            // Old RAR advances r99 -> s00 ... zNN, which overlaps ZIP .zNN.
            // Only a selected RAR-signed zNN may normalize to .rar; do not
            // misroute a ZIP disk or open a neighboring unrelated .rar.
            if (Path.GetExtension(path).StartsWith(".z", StringComparison.OrdinalIgnoreCase) && !HasRarSignature(path)) return path;
            var legacyFirst = Path.ChangeExtension(path, ".rar");
            if (!File.Exists(legacyFirst)) throw new ArchiveReadException(ArchiveReadError.ArchiveDamaged, "缺少首卷：" + Path.GetFileName(legacyFirst), isVolumeFailure: true);
            RejectReparseVolume(legacyFirst);
            return legacyFirst;
        }
        return PartFirstPath(path, match);
    }
    public static string? Resolve(string source, string name, Func<string, ulong?>? sdkProbe = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['/', '\\', ':', '\0']) >= 0 || name is "." or "..") return null;
        var original = Path.GetFileName(source); var match = parts.Match(original);
        var numbered = numeric.Match(original);
        var allowed = match.Success
            ? ResolvePartName(source, match, name)
            : numbered.Success && HasLegacyVolumeHeader(source, sdkProbe)
                ? ResolveNumericName(source, numbered, name, sdkProbe)
            : Regex.IsMatch(name, "^" + Regex.Escape(Path.GetFileNameWithoutExtension(original)) + @"\.(rar|[r-z][0-9]{2})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return allowed ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!, name) : null;
    }

    private static string PartFirstPath(string path, Match selected)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var digits = selected.Groups[2].Value;
        var minimum = digits[0] == '0' ? digits.Length : 1;
        string? first = null;
        // The SDK grows the number, not the original padding, at 999 -> 1000.
        // Alternate-width candidates must actually be RAR files. The SDK remains
        // responsible for validating their main/encrypted headers; do not decrypt
        // or infer RAR5 first-volume flags without its password callback.
        for (var width = minimum; width <= digits.Length; width++)
        {
            var candidate = Path.Combine(directory, selected.Groups[1].Value + "1".PadLeft(width, '0') + selected.Groups[3].Value);
            if (!File.Exists(candidate)) continue;
            RejectReparseVolume(candidate);
            if (width != digits.Length && !HasRarSignature(candidate)) continue;
            if (first is not null) throw new ArchiveReadException(ArchiveReadError.ArchiveDamaged, "part 分卷首卷存在歧义，已拒绝混合归档组");
            first = candidate;
        }
        return first ?? throw new ArchiveReadException(ArchiveReadError.ArchiveDamaged,
            "缺少首卷：" + selected.Groups[1].Value + "1".PadLeft(digits.Length, '0') + selected.Groups[3].Value, isVolumeFailure: true);
    }

    private static bool ResolvePartName(string source, Match selected, string name)
    {
        var candidate = parts.Match(name);
        if (!candidate.Success || !candidate.Groups[1].Value.Equals(selected.Groups[1].Value, StringComparison.OrdinalIgnoreCase)) return false;
        var first = parts.Match(Path.GetFileName(PartFirstPath(source, selected)));
        var width = first.Groups[2].Length; var digits = candidate.Groups[2].Value;
        return digits.Any(value => value != '0') && (digits.Length == width || digits.Length > width && digits[0] != '0');
    }

    private static string NumericFirstPath(string path, Match numbered, Func<string, ulong?>? sdkProbe = null)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var digits = numbered.Groups[2].Value;
        // SDK numbering expands 999 -> 1000. A leading zero cannot be an expanded number.
        // Search only possible initial widths, and require an actual first-volume main header.
        var minimum = digits[0] == '0' ? digits.Length : 3;
        string? first = null;
        for (var width = minimum; width <= digits.Length; width++)
        {
            foreach (var start in new[] { '0', '1' })
            {
                var candidate = Path.Combine(directory, numbered.Groups[1].Value + start.ToString().PadLeft(width, '0') + numbered.Groups[3].Value);
                if (!HasFirstLegacyVolumeHeader(candidate, sdkProbe)) continue;
                if (first is not null) throw new ArchiveReadException(ArchiveReadError.ArchiveDamaged, "数字分卷首卷存在歧义，已拒绝混合归档组");
                first = candidate;
            }
        }
        return first ?? throw new ArchiveReadException(ArchiveReadError.ArchiveDamaged, "缺少首卷：" + numbered.Groups[1].Value + new string('0', digits.Length) + numbered.Groups[3].Value, isVolumeFailure: true);
    }

    private static bool ResolveNumericName(string source, Match numbered, string name, Func<string, ulong?>? sdkProbe)
    {
        var candidate = numeric.Match(name);
        if (!candidate.Success || !candidate.Groups[1].Value.Equals(numbered.Groups[1].Value, StringComparison.OrdinalIgnoreCase)) return false;
        var first = numeric.Match(Path.GetFileName(NumericFirstPath(source, numbered, sdkProbe)));
        var width = first.Groups[2].Length;
        var digits = candidate.Groups[2].Value;
        // Original padding is retained until overflow; additional leading zeros are aliases, not next volumes.
        return digits.Length == width || digits.Length > width && digits[0] != '0';
    }

    // A numeric basename alone may be an ordinary archive (e.g. backup.2026.rar).
    // Only an actual RAR3 or CRC-protected plaintext RAR5 volume main header
    // enables this naming scheme, never a neighboring file alone.
    private static bool HasLegacyVolumeHeader(string path, Func<string, ulong?>? sdkProbe = null)
        => LegacyVolumeFlags(path) is { } flags && (flags & 1) != 0 || (Rar5VolumeIndex(path) ?? sdkProbe?.Invoke(path)) is not null;
    private static bool HasFirstLegacyVolumeHeader(string path, Func<string, ulong?>? sdkProbe = null)
        => LegacyVolumeFlags(path) is { } flags && (flags & 0x101) == 0x101 || (Rar5VolumeIndex(path) ?? sdkProbe?.Invoke(path)) == 0;

    // Naming only: bounded, CRC-protected plaintext main header. Never decrypt
    // headers here, guess from neighbors, or substitute this for SDK validation.
    private static ulong? Rar5VolumeIndex(string path)
    {
        if (!File.Exists(path)) return null;
        RejectReparseVolume(path);
        using var stream = File.OpenRead(path);
        Span<byte> buffer = stackalloc byte[256];
        var count = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        if (count < 16 || !buffer[..8].SequenceEqual("Rar!\x1a\x07\x01\0"u8)) return null;
        var cursor = 12;
        var size = ReadVint(buffer[..count], ref cursor);
        if (size is null || size > (ulong)(count - cursor)) return null;
        var end = cursor + (int)size.Value;
        uint crc = uint.MaxValue;
        foreach (var value in buffer[12..end])
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u);
        }
        if (~crc != BinaryPrimitives.ReadUInt32LittleEndian(buffer[8..12])) return null;
        var header = buffer[..end];
        if (ReadVint(header, ref cursor) != 1) return null; // main, not encryption
        var flags = ReadVint(header, ref cursor);
        if (flags is null || (flags.Value & ~5UL) != 0) return null; // main: extra, skip-if-unknown
        if ((flags.Value & 1) != 0)
        {
            var extraSize = ReadVint(header, ref cursor);
            if (extraSize is null || extraSize > (ulong)(end - cursor)) return null;
            header = header[..(end - (int)extraSize.Value)];
        }
        var archiveFlags = ReadVint(header, ref cursor);
        if (archiveFlags is null || (archiveFlags.Value & 1) == 0) return null;
        return (archiveFlags.Value & 2) != 0 ? ReadVint(header, ref cursor) : 0;
    }

    private static ulong? ReadVint(ReadOnlySpan<byte> bytes, ref int cursor)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64 && cursor < bytes.Length; shift += 7)
        {
            var next = bytes[cursor++];
            if (shift == 63 && (next & 0xfe) != 0) return null;
            value |= (ulong)(next & 127) << shift;
            if ((next & 128) == 0) return value;
        }
        return null;
    }
    private static ushort? LegacyVolumeFlags(string path)
    {
        if (!File.Exists(path)) return null;
        RejectReparseVolume(path);
        using var stream = File.OpenRead(path); Span<byte> header = stackalloc byte[12];
        return stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length
            && header[..7].SequenceEqual("Rar!\x1a\x07\0"u8) && header[9] == 0x73
            ? BinaryPrimitives.ReadUInt16LittleEndian(header[10..]) : null;
    }

    private static bool HasRarSignature(string path)
    {
        if (!File.Exists(path)) return false;
        RejectReparseVolume(path);
        using var stream = File.OpenRead(path);
        Span<byte> signature = stackalloc byte[8];
        var read = stream.ReadAtLeast(signature, signature.Length, throwOnEndOfStream: false);
        return read >= 7 && signature[..6].SequenceEqual("Rar!\x1a\x07"u8)
            && (signature[6] == 0 || read == 8 && signature[6] == 1 && signature[7] == 0);
    }

    public static void RejectReparseVolume(string path)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ArchiveReadException(ArchiveReadError.UnsupportedOperation, "不支持通过重解析点探测或读取 RAR 分卷");
    }
}
