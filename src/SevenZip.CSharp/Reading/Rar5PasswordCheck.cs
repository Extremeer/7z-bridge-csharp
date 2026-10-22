using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SevenZip.CSharp;

/// <summary>Failure-only RAR5 password-check verification; never decrypts or opens an archive instead of the SDK.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class Rar5PasswordCheck
{
    // The SDK Open API does not expose whether its password check passed before an encrypted-header CRC failed.
    // Only a bounded, CRC-protected archive-encryption record with a valid check checksum can disambiguate it.
    public static unsafe bool? Verify(SevenZipNativeLibrary library, string path, ArchivePassword password, CancellationToken token, ArchiveOperationControl? control = null)
    {
        control?.CheckStop(token); token.ThrowIfCancellationRequested();
        using var source = ArchiveVolumeInput.Open(path, token);
        var buffer = new byte[128]; var count = source.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        if (count < 16 || !buffer.AsSpan(0, 8).SequenceEqual("Rar!\x1a\x07\x01\0"u8)) return null;
        var cursor = 12;
        var size = ReadVint(buffer, ref cursor, count);
        if (size is null || size > 96 || size > (ulong)(count - cursor)) return null;
        var end = cursor + (int)size.Value;
        using var crc = SevenZipNativeHasher.Create(library, "CRC32");
        if (crc is null) return null;
        fixed (byte* pointer = buffer) crc.Update((nint)(pointer + 12), (uint)(end - 12));
        if (uint.Parse(crc.Finish(), NumberStyles.HexNumber, CultureInfo.InvariantCulture) != BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(8))) return null;
        if (ReadVint(buffer, ref cursor, end) != 4 || ReadVint(buffer, ref cursor, end) != 0
            || ReadVint(buffer, ref cursor, end) != 0 || ReadVint(buffer, ref cursor, end) != 1 || cursor >= end) return null;
        var exponent = buffer[cursor++];
        if (exponent > 24 || end - cursor != 28) return null;
        var salt = buffer.AsSpan(cursor, 16).ToArray(); cursor += 16;
        var check = buffer.AsSpan(cursor, 8).ToArray(); cursor += 8;
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(check).AsSpan(0, 4), buffer.AsSpan(cursor, 4))) return null;
        var verified = password.Use(chars =>
        {
            var encoded = new byte[Encoding.UTF8.GetByteCount(chars)];
            var derived = new byte[32]; var actual = new byte[8];
            try
            {
                Encoding.UTF8.GetBytes(chars, encoded);
                Rfc2898DeriveBytes.Pbkdf2(encoded, salt, derived, checked((1 << exponent) + 32), HashAlgorithmName.SHA256);
                for (var i = 0; i < 8; i++) actual[i] = (byte)(derived[i] ^ derived[i + 8] ^ derived[i + 16] ^ derived[i + 24]);
                return CryptographicOperations.FixedTimeEquals(check, actual);
            }
            finally { CryptographicOperations.ZeroMemory(encoded); CryptographicOperations.ZeroMemory(derived); CryptographicOperations.ZeroMemory(actual); }
        });
        control?.CheckStop(token); token.ThrowIfCancellationRequested();
        return verified;
    }
    private static ulong? ReadVint(byte[] bytes, ref int cursor, int end)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64 && cursor < end; shift += 7)
        {
            var next = bytes[cursor++];
            if (shift == 63 && (next & 0xfe) != 0) return null;
            value |= (ulong)(next & 127) << shift;
            if ((next & 128) == 0) return value;
        }
        return null;
    }
}
