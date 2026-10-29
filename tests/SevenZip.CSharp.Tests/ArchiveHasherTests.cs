using System.IO.Compression;
using SevenZip.CSharp;

namespace SevenZip.CSharp.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ArchiveHasherTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SevenZip.CSharp.Tests", Guid.NewGuid().ToString("N"));
    private static string Library => SevenZipNativeSettings.AutoDiscover().LibraryPath;
    public ArchiveHasherTests() => Directory.CreateDirectory(root);
    private string CreateZip()
    {
        var path = Path.Combine(root, "hash.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var output = zip.CreateEntry("data.txt").Open();
        output.Write("123456789"u8);
        return path;
    }

    [Theory]
    [InlineData("CRC32", "CBF43926")]
    [InlineData("SHA256", "15E2B0D3C33891EBB0F1EF609EC419420C20E320CE94C65FBC8C3312448EB225")]
    public async Task ComputesKnownDecodedVectorsWithoutTemporaryExtraction(string algorithm, string expected)
    {
        var path = CreateZip();
        var outcome = await NamedEntryFixture.HashAsync(Library, path, [ArchivePath.Root], algorithm);
        Assert.True(outcome.IsSuccess, outcome.Outcome.Error?.Message);
        var entry = Assert.Single(outcome.Entries!);
        Assert.Equal("data.txt", entry.Entry.Value);
        Assert.Equal(expected, entry.Hash);
        Assert.Empty(Directory.GetDirectories(root));
        Assert.Single(Directory.GetFiles(root));
        using var unlocked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task UnsupportedHasherIsDistinguishedFromDecodeFailure()
    {
        var outcome = await NamedEntryFixture.HashAsync(Library, CreateZip(), [ArchivePath.Root], "not-a-hasher");
        Assert.Equal(ArchiveFailureCode.UnsupportedOperation, outcome.Outcome.Error!.Code);
        Assert.False(outcome.DecodingAttempted);
    }

    [Fact]
    public async Task StaleRequestedIdIsNotASuccessfulEmptyHashResult()
    {
        var path = CreateZip();
        var snapshot = (await ArchiveInspector.InspectAsync(Library, path))!;
        var outcome = await ArchiveHasher.ComputeItemsAsync(Library, path, new(snapshot.Revision, [new(snapshot.Revision, uint.MaxValue)]), "SHA256");
        Assert.Equal(ArchiveFailureCode.ConflictDetected, outcome.Outcome.Error!.Code);
        Assert.Null(outcome.Entries);
        Assert.True(outcome.DecodingAttempted);
        using var unlocked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
