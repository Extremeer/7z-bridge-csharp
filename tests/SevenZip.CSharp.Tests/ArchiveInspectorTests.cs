using System.IO.Compression;
using System.Formats.Tar;
using SevenZip.CSharp;

namespace SevenZip.CSharp.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ArchiveInspectorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SevenZip.CSharp.Tests", Guid.NewGuid().ToString("N"));
    private static string Library => SevenZipNativeSettings.AutoDiscover().LibraryPath;
    public ArchiveInspectorTests() => Directory.CreateDirectory(root);

    private string CreateZip(params string[] names)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var name in names)
        {
            var entry = zip.CreateEntry(name);
            if (!name.EndsWith('/'))
            {
                using var output = entry.Open();
                output.Write("native-inspection-content"u8);
            }
        }
        return path;
    }

    [Fact]
    public async Task InspectsRealZipWithImplicitParentsEmptyDirectoryAndReleasedSources()
    {
        var path = CreateZip("nested/data.txt", "empty/");
        var snapshot = await ArchiveInspector.InspectAsync(Library, path);
        Assert.NotNull(snapshot);
        Assert.Equal("zip", snapshot.Format);
        Assert.True(snapshot.CanUpdate);
        Assert.False(snapshot.IsMultiVolume);
        var file = Assert.Single(snapshot.Entries, entry => !entry.IsDirectory);
        Assert.Equal("nested/data.txt", file.Path.Value);
        Assert.Equal(25L, file.UncompressedSize);
        Assert.True(file.HasKnownUncompressedSize);
        Assert.NotNull(file.Crc32);
        Assert.Contains(snapshot.Entries, entry => entry.Path.Value == "nested" && entry.IsDirectory);
        Assert.Contains(snapshot.Entries, entry => entry.Path.Value == "empty" && entry.IsDirectory);
        Assert.Equal(2, snapshot.PhysicalEntries.Count);
        Assert.Contains(Path.GetFullPath(path), snapshot.SourceFiles!);
        using var unlocked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task RejectsCaseInsensitiveDuplicatePhysicalPaths()
    {
        var path = CreateZip("data.txt", "DATA.txt");
        var failure = await Assert.ThrowsAsync<ArchiveReadException>(() => ArchiveInspector.InspectAsync(Library, path));
        Assert.Equal(ArchiveReadError.UnsupportedOperation, failure.Error);
        using var unlocked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task RejectsTraversalWithoutLeakingTheInputHandle()
    {
        var path = CreateZip("../escape.txt");
        await Assert.ThrowsAsync<ArgumentException>(() => ArchiveInspector.InspectAsync(Library, path));
        using var unlocked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task TarRemainsReadableButNotWritableUnderApplicationEnginePolicy()
    {
        var path = Path.Combine(root, "read-only.tar");
        using (var file = File.Create(path))
        using (var writer = new TarWriter(file))
        using (var data = new MemoryStream("tar-native-content"u8.ToArray()))
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "data.txt") { DataStream = data });
        var snapshot = await ArchiveInspector.InspectAsync(Library, path);
        Assert.NotNull(snapshot);
        Assert.Equal("tar", snapshot.Format);
        Assert.False(snapshot.CanUpdate);
        Assert.Equal("data.txt", Assert.Single(snapshot.Entries).Path.Value);
    }

    [Fact]
    public async Task PreCancelledContextDoesNotBorrowOrDisposeThePassword()
    {
        var path = CreateZip("data.txt");
        using var password = new ArchivePassword("borrowed");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ArchiveInspector.InspectAsync(Library, path,
            new(password, cancellationToken: cancellation.Token)));
        Assert.False(password.IsDisposed);
    }

    [Fact]
    public async Task HostFormatPolicyIsExplicitAndCopiedRatherThanShared()
    {
        var path = CreateZip("data.txt");
        var formats = new List<string> { "ZIP" };
        var context = new ArchiveOperationContext(allowedFormats: formats);
        formats.Clear();
        Assert.Equal("zip", (await ArchiveInspector.InspectAsync(Library, path, context))!.Format);
        Assert.Null(await ArchiveInspector.InspectAsync(Library, path,
            new(allowedFormats: Array.Empty<string>())));
    }

    [Fact]
    public async Task ConcurrentInspectionsHaveIndependentThreadAffineSessions()
    {
        var path = CreateZip("data.txt");
        var snapshots = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ArchiveInspector.InspectAsync(Library, path, new())));
        Assert.All(snapshots, snapshot => Assert.Equal("data.txt", Assert.Single(snapshot!.Entries).Path.Value));
        using var unlocked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData("/absolute.txt")]
    [InlineData("../escape.txt")]
    [InlineData("a/../escape.txt")]
    public void ArchivePathRejectsAbsoluteAndParentTraversal(string path)
        => Assert.Throws<ArgumentException>(() => ArchivePath.From(path));

    public void Dispose() => Directory.Delete(root, recursive: true);
}
