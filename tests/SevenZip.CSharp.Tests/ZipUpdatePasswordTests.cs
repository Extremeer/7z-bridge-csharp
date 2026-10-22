using System.Buffers.Binary;
using System.Text;
using SevenZip.CSharp;

namespace SevenZip.CSharp.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ZipUpdatePasswordTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SevenZip.CSharp.PasswordTests", Guid.NewGuid().ToString("N"));
    private static string Library => SevenZipNativeSettings.AutoDiscover().LibraryPath;
    private const string Secret = "correct-password";
    private static readonly string[] Operations = ["delete", "delete-all", "rename", "move", "add", "replace"];
    public static IEnumerable<object[]> Cases => new[] { ArchiveEncryptionAlgorithm.ZipCrypto, ArchiveEncryptionAlgorithm.Aes256 }
        .SelectMany(method => Operations.Select(operation => new object[] { method, operation }));
    public ZipUpdatePasswordTests() => Directory.CreateDirectory(root);

    private async Task<string> Create(ArchiveEncryptionAlgorithm method)
    {
        var input = Path.Combine(root, "input"); Directory.CreateDirectory(input);
        var data = new byte[128 * 1024]; new Random(42).NextBytes(data);
        await File.WriteAllBytesAsync(Path.Combine(input, "a.bin"), data);
        await File.WriteAllBytesAsync(Path.Combine(input, "z.bin"), data);
        using var password = new ArchivePassword(Secret);
        var path = Path.Combine(root, "encrypted.zip");
        var result = await ArchiveWriter.CreateAsync(Library, input, new()
        {
            ArchivePath = path, Format = ArchiveWriteFormat.Zip,
            Compression = new() { Method = "Copy" }, Encryption = new() { Method = method }
        }, new(password));
        Assert.True(result.IsSuccess, result.Message);
        return path;
    }

    private ArchiveUpdateRequest Request(string operation)
    {
        var added = Path.Combine(root, "added.txt"); File.WriteAllText(added, "new data");
        return operation switch
        {
            "delete" => new() { Deleted = [ArchivePath.From("a.bin")] },
            "delete-all" => new() { Deleted = [ArchivePath.From("a.bin"), ArchivePath.From("z.bin")] },
            "rename" => new() { RenameSource = ArchivePath.From("a.bin"), RenameDestination = ArchivePath.From("renamed.bin") },
            "move" => new() { Moves = [new(ArchivePath.From("a.bin"), ArchivePath.From("folder/a.bin"))] },
            "replace" => new() { Deleted = [ArchivePath.From("a.bin")], Sources = [ArchiveSourceItem.FromFileSystem(added, ArchivePath.From("a.bin"))] },
            _ => new() { Sources = [ArchiveSourceItem.FromFileSystem(added, ArchivePath.From("added.txt"))] }
        };
    }

    private void CleanAndUnlocked(string path)
    {
        Assert.DoesNotContain(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories), file => Path.GetFileName(file).Contains(".native-", StringComparison.Ordinal));
        using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task MissingZipPasswordAllowsEncodedUpdatesAndLeavesNewDataUnencrypted(ArchiveEncryptionAlgorithm method, string operation)
    {
        var path = await Create(method);
        var result = await ArchiveUpdater.UpdateAsync(Library, path, Request(operation));
        Assert.True(result.IsSuccess, result.Error?.Message);
        using var original = new ArchivePassword(Secret);
        var snapshot = await ArchiveInspector.InspectAsync(Library, path);
        if (operation != "delete-all")
        {
            Assert.True(snapshot!.Entries.Single(item => item.Path.Value == "z.bin").IsEncrypted);
            Assert.True((await ExtractSelected(path, "z.bin", original, "retained")).IsSuccess);
        }
        if (operation is "add" or "replace")
        {
            var entry = operation == "add" ? "added.txt" : "a.bin";
            Assert.False(snapshot!.Entries.Single(item => item.Path.Value == entry).IsEncrypted);
            Assert.True((await ExtractSelected(path, entry, null, "new-plain")).IsSuccess);
            Assert.Equal("new data", await File.ReadAllTextAsync(Path.Combine(root, "new-plain", entry)));
        }
        CleanAndUnlocked(path);
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task CorrectPasswordStillSupportsEveryUpdatePlan(ArchiveEncryptionAlgorithm method, string operation)
    {
        var path = await Create(method);
        using var password = new ArchivePassword(Secret); var context = new ArchiveOperationContext(password);
        var reports = new List<EngineProgress>();
        var result = await ArchiveUpdater.UpdateAsync(Library, path, Request(operation), context, new ActionProgress<EngineProgress>(reports.Add));
        Assert.True(result.IsSuccess, result.Error?.Message);
        string[] expected = operation switch
        {
            "delete" => ["z.bin"], "delete-all" => [], "rename" => ["renamed.bin", "z.bin"],
            "move" => ["folder/a.bin", "z.bin"], "add" => ["a.bin", "added.txt", "z.bin"],
            _ => ["a.bin", "z.bin"]
        };
        var snapshot = await ArchiveInspector.InspectAsync(Library, path, context);
        Assert.Equal(expected, snapshot!.Entries.Where(entry => !entry.IsDirectory).Select(entry => entry.Path.Value).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(reports, value => value.MetricsScope == "update-password-validation");
        Assert.True((await ArchiveExtractor.TestAsync(Library, path, context)).IsSuccess);
        Assert.False(password.IsDisposed);
        CleanAndUnlocked(path);
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task ZipUpdatesDoNotAuthenticateRetainedEncryptedPayload(ArchiveEncryptionAlgorithm method, string operation)
    {
        var path = await Create(method);
        using var wrong = new ArchivePassword("incorrect-password");
        var reports = new List<EngineProgress>();
        var result = await ArchiveUpdater.UpdateAsync(Library, path, Request(operation), new(wrong),
            new ActionProgress<EngineProgress>(reports.Add));
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.DoesNotContain(reports, value => value.MetricsScope == "update-password-validation");
        Assert.False(wrong.IsDisposed);

        // Retained bytes still decrypt with the ORIGINAL password. New data uses
        // the supplied password, so add/replace can intentionally produce mixed passwords.
        using var original = new ArchivePassword(Secret);
        if (operation != "delete-all")
        {
            var retained = await ExtractSelected(path, "z.bin", original, "retained");
            Assert.True(retained.IsSuccess, retained.Error?.Message);
        }
        if (operation is "add" or "replace")
        {
            var entry = ArchivePath.From(operation == "add" ? "added.txt" : "a.bin");
            var added = await ExtractSelected(path, entry.Value, wrong, "added-output");
            Assert.True(added.IsSuccess, added.Error?.Message);
        }
        CleanAndUnlocked(path);
    }

    private async Task<ArchiveEngineOutcome> ExtractSelected(string path, string entry, ArchivePassword? password, string directory)
    {
        var snapshot = await ArchiveInspector.InspectAsync(Library, path, new(password));
        var measured = new ExtractionMeasurement(snapshot!.Entries.Where(item => item.Path.Value == entry),
            new ActionProgress<EngineExtractionProgress>(_ => { }));
        return await ArchiveExtractor.RunWithPasswordsAsync(Library, path, Path.Combine(root, directory), measured,
            password, password, default);
    }

    public void Dispose() => Directory.Delete(root, true);
}
