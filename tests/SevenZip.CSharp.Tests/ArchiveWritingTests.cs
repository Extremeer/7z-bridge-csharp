using System.Formats.Tar;
using SevenZip.CSharp;

namespace SevenZip.CSharp.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ArchiveWritingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SevenZip.CSharp.Tests", Guid.NewGuid().ToString("N"));
    private static string Library => SevenZipNativeSettings.AutoDiscover().LibraryPath;
    public ArchiveWritingTests() => Directory.CreateDirectory(root);
    private string Input()
    {
        var path = Path.Combine(root, "input"); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "data.txt"), "original data");
        File.WriteAllText(Path.Combine(path, "keep.txt"), "retained data");
        Directory.CreateDirectory(Path.Combine(path, "empty"));
        return path;
    }
    private string Archive(ArchiveWriteFormat format) => Path.Combine(root, format == ArchiveWriteFormat.Zip ? "sample.zip" : "sample.7z");
    private void NoOwnedTemporaryOutput() => Assert.DoesNotContain(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories),
        path => Path.GetFileName(path).Contains(".native-", StringComparison.Ordinal));

    [Theory]
    [InlineData(ArchiveWriteFormat.Zip)] [InlineData(ArchiveWriteFormat.SevenZip)]
    public async Task StandaloneCreateBulkUpdateRenameDeleteExtractAndHashRoundTrip(ArchiveWriteFormat format)
    {
        var archive = Archive(format);
        var created = await ArchiveWriter.CreateAsync(Library, Input(), new() { ArchivePath = archive, Format = format });
        Assert.True(created.IsSuccess, created.Message);
        var added = Path.Combine(root, "added.txt"); File.WriteAllText(added, "new data");
        var updated = await NamedEntryFixture.ApplyAsync(Library, archive, new()
        {
            Deleted = [ArchivePath.From("keep.txt")],
            Moves = [new(ArchivePath.From("data.txt"), ArchivePath.From("renamed.txt"))],
            Sources = [ArchiveSourceItem.FromFileSystem(added, ArchivePath.From("new.txt"))]
        });
        Assert.True(updated.IsSuccess, updated.Error?.Message);
        var snapshot = await ArchiveInspector.InspectAsync(Library, archive);
        Assert.NotNull(snapshot);
        Assert.Equal(new[] { "new.txt", "renamed.txt" }, snapshot.Entries.Where(entry => !entry.IsDirectory).Select(entry => entry.Path.Value));
        Assert.Contains(snapshot.Entries, entry => entry.Path.Value == "empty" && entry.IsDirectory);
        var output = Path.Combine(root, "out");
        Assert.True((await ArchiveExtractor.ExtractAsync(Library, archive, output)).IsSuccess);
        Assert.Equal("original data", File.ReadAllText(Path.Combine(output, "renamed.txt")));
        Assert.Equal("new data", File.ReadAllText(Path.Combine(output, "new.txt")));
        Assert.True((await NamedEntryFixture.HashAsync(Library, archive, [ArchivePath.Root], "SHA256")).IsSuccess);
        Assert.True((await NamedEntryFixture.ApplyAsync(Library, archive, new()
        { RenameSource = ArchivePath.From("renamed.txt"), RenameDestination = ArchivePath.From("renamed-again.txt") })).IsSuccess);
        Assert.True((await NamedEntryFixture.ApplyAsync(Library, archive, new()
        { Deleted = [ArchivePath.From("renamed-again.txt")] })).IsSuccess);
        Assert.True((await ArchiveExtractor.TestAsync(Library, archive)).IsSuccess);
        Assert.Equal("new.txt", Assert.Single((await ArchiveInspector.InspectAsync(Library, archive))!.Entries, entry => !entry.IsDirectory).Path.Value);
        NoOwnedTemporaryOutput();
        using var unlocked = File.Open(archive, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData(ArchiveWriteFormat.Zip)] [InlineData(ArchiveWriteFormat.SevenZip)]
    public async Task PasswordIsBorrowedAcrossCreationAndEncryptedUpdate(ArchiveWriteFormat format)
    {
        var archive = Archive(format);
        using var password = new ArchivePassword("native-password");
        var context = new ArchiveOperationContext(password);
        var created = await ArchiveWriter.CreateAsync(Library, Input(), new()
        {
            ArchivePath = archive, Format = format,
            Encryption = new() { Method = ArchiveEncryptionAlgorithm.Aes256, EncryptFileNames = format == ArchiveWriteFormat.SevenZip }
        }, context);
        Assert.True(created.IsSuccess, created.Message);
        var updated = await NamedEntryFixture.ApplyAsync(Library, archive, new()
        { RenameSource = ArchivePath.From("data.txt"), RenameDestination = ArchivePath.From("renamed.txt") }, context);
        Assert.True(updated.IsSuccess, updated.Error?.Message);
        Assert.True((await NamedEntryFixture.HashAsync(Library, archive, [ArchivePath.Root], "SHA256", context)).IsSuccess);
        Assert.False(password.IsDisposed);
        NoOwnedTemporaryOutput();
    }

    [Theory]
    [InlineData(ArchiveWriteFormat.Zip)] [InlineData(ArchiveWriteFormat.SevenZip)]
    public async Task SplitCreationPublishesAllVolumesAndExtractsFromTheLastVolume(ArchiveWriteFormat format)
    {
        var input = Path.Combine(root, "split-input"); Directory.CreateDirectory(input);
        var bytes = new byte[40_000]; new Random(719).NextBytes(bytes);
        File.WriteAllBytes(Path.Combine(input, "payload.bin"), bytes);
        var archive = Archive(format);
        var result = await ArchiveWriter.CreateAsync(Library, input, new()
        { ArchivePath = archive, Format = format, Compression = new() { Level = 0 }, Split = new(4096) });
        Assert.True(result.IsSuccess, result.Message);
        var volumes = Directory.GetFiles(root, Path.GetFileName(archive) + ".*").Order(StringComparer.Ordinal).ToArray();
        Assert.True(volumes.Length > 1);
        var output = Path.Combine(root, "out");
        Assert.True((await ArchiveExtractor.ExtractAsync(Library, volumes[^1], output)).IsSuccess);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(output, "payload.bin")));
        NoOwnedTemporaryOutput();
    }

    [Theory]
    [InlineData(ArchiveWriteFormat.Zip)] [InlineData(ArchiveWriteFormat.SevenZip)]
    public async Task CancelledCreationDoesNotPublishItsPrivateOutput(ArchiveWriteFormat format)
    {
        var archive = Archive(format);
        using var stop = new CancellationTokenSource();
        var result = await ArchiveWriter.CreateAsync(Library, Input(), new() { ArchivePath = archive, Format = format },
            new(cancellationToken: stop.Token), new ActionProgress<EngineProgress>(value => { if (value.OutputBytes > 0) stop.Cancel(); }));
        Assert.True(result.WasCancelled);
        Assert.False(File.Exists(archive));
        NoOwnedTemporaryOutput();
    }

    [Theory]
    [InlineData(ArchiveWriteFormat.Zip)] [InlineData(ArchiveWriteFormat.SevenZip)]
    public async Task CancelledUpdateKeepsTheOriginalArchiveUnchanged(ArchiveWriteFormat format)
    {
        var archive = Archive(format);
        Assert.True((await ArchiveWriter.CreateAsync(Library, Input(), new() { ArchivePath = archive, Format = format })).IsSuccess);
        var before = File.ReadAllBytes(archive);
        using var stop = new CancellationTokenSource();
        var result = await NamedEntryFixture.ApplyAsync(Library, archive,
            new() { Deleted = [ArchivePath.From("data.txt")] }, new(cancellationToken: stop.Token),
            new ActionProgress<EngineProgress>(value => { if (value.OutputBytes > 0) stop.Cancel(); }));
        Assert.Equal(ArchiveFailureCode.OperationCancelled, result.Error!.Code);
        Assert.Equal(before, File.ReadAllBytes(archive));
        NoOwnedTemporaryOutput();
    }

    [Fact]
    public async Task IndependentWriterLockRejectsASecondWriterWithATypedFailure()
    {
        var archive = Archive(ArchiveWriteFormat.Zip);
        using var held = await NativeArchiveWriteLock.AcquireAsync(archive);
        var result = await ArchiveWriter.CreateAsync(Library, Input(), new() { ArchivePath = archive, Format = ArchiveWriteFormat.Zip });
        Assert.Equal(ArchiveFailureCode.ArchiveBusy, result.Error!.Code);
        Assert.False(File.Exists(archive));
        NoOwnedTemporaryOutput();
    }

    [Fact]
    public async Task ReadOnlyTarUpdateIsRefusedWithoutChangingAnyBytes()
    {
        var path = Path.Combine(root, "read-only.tar");
        using (var file = File.Create(path))
        using (var writer = new TarWriter(file))
        using (var data = new MemoryStream("original"u8.ToArray()))
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "data.txt") { DataStream = data });
        var before = File.ReadAllBytes(path);
        var result = await NamedEntryFixture.ApplyAsync(Library, path, new() { Deleted = [ArchivePath.From("data.txt")] });
        Assert.Equal(ArchiveFailureCode.UnsupportedOperation, result.Error!.Code);
        Assert.Equal(before, File.ReadAllBytes(path));
        NoOwnedTemporaryOutput();
    }

    [Theory]
    [InlineData("delete")] [InlineData("rename")] [InlineData("add")]
    public async Task DamagedZipUpdateIsNotMisreportedAsReadOnly(string operation)
    {
        var path = Path.Combine(root, "damaged.zip");
        byte[] before = [0x50, 0x4b, 3, 4, 1, 2];
        await File.WriteAllBytesAsync(path, before);
        var request = operation switch
        {
            "delete" => new NamedUpdatePlan { Deleted = [ArchivePath.From("data.txt")] },
            "rename" => new NamedUpdatePlan
                { RenameSource = ArchivePath.From("data.txt"), RenameDestination = ArchivePath.From("renamed.txt") },
            _ => new NamedUpdatePlan { Sources = ArchiveSourceEnumerator.EnumerateDirectory(Input()) }
        };
        var result = await NamedEntryFixture.ApplyAsync(Library, path, request);
        Assert.False(result.IsSuccess);
        Assert.Equal(ArchiveFailureCode.ArchiveDamaged, result.Error!.Code);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        NoOwnedTemporaryOutput();
        using var unlocked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
