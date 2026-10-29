using System.IO.Compression;
using System.Formats.Tar;
using SevenZip.CSharp;

namespace SevenZip.CSharp.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ArchiveExtractorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SevenZip.CSharp.Tests", Guid.NewGuid().ToString("N"));
    private static string Library => SevenZipNativeSettings.AutoDiscover().LibraryPath;
    private static readonly byte[] Data = "real native extraction bytes"u8.ToArray();
    public ArchiveExtractorTests() => Directory.CreateDirectory(root);
    private string CreateZip(string name = "nested/data.txt")
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        using (var output = zip.CreateEntry(name).Open()) output.Write(Data);
        zip.CreateEntry("empty/");
        return path;
    }

    [Fact]
    public async Task StandaloneZipExtractAndTestUseRealBytesAndReleaseSources()
    {
        var path = CreateZip();
        var output = Path.Combine(root, "out");
        var reports = new List<EngineProgress>();
        var result = await ArchiveExtractor.ExtractAsync(Library, path, output,
            progress: new ActionProgress<EngineProgress>(reports.Add));
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(Data, await File.ReadAllBytesAsync(Path.Combine(output, "nested", "data.txt")));
        Assert.True(Directory.Exists(Path.Combine(output, "empty")));
        Assert.Contains(reports, report => report.ProcessedBytes > 0 && report.HasMeasuredProcessedBytes);
        Assert.Empty(Directory.GetFiles(output, "*.tmp", SearchOption.AllDirectories));
        Assert.True((await ArchiveExtractor.TestAsync(Library, path)).IsSuccess);
        using var unlocked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task ExistingTargetIsSkippedUnlessOverwriteWasExplicitlyRequested()
    {
        var path = CreateZip("data.txt");
        var output = Path.Combine(root, "out"); Directory.CreateDirectory(output);
        var target = Path.Combine(output, "data.txt");
        await File.WriteAllTextAsync(target, "original");
        Assert.True((await ArchiveExtractor.ExtractAsync(Library, path, output)).IsSuccess);
        Assert.Equal("original", await File.ReadAllTextAsync(target));
        Assert.True((await ArchiveExtractor.ExtractAsync(Library, path, output, overwrite: true)).IsSuccess);
        Assert.Equal(Data, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task CancellationAtFirstRealWritePreservesExistingTargetAndCleansOwnedTemporaryFile()
    {
        var path = CreateZip("data.txt");
        var output = Path.Combine(root, "out"); Directory.CreateDirectory(output);
        var target = Path.Combine(output, "data.txt");
        await File.WriteAllTextAsync(target, "original");
        using var stop = new CancellationTokenSource();
        var result = await ArchiveExtractor.ExtractAsync(Library, path, output, new(cancellationToken: stop.Token), overwrite: true,
            extractionProgress: new ActionProgress<EngineExtractionProgress>(value => { if (value.ProcessedBytes > 0) stop.Cancel(); }));
        Assert.Equal(ArchiveFailureCode.OperationCancelled, result.Error!.Code);
        Assert.Equal("original", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(output, ".archive-extract-*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("NUL.txt")]
    [InlineData("data.txt:stream")]
    public async Task DefaultDiskSafetyRejectsWindowsDeviceNamesAndDataStreams(string name)
    {
        var path = CreateZip(name);
        var output = Path.Combine(root, "out");
        var result = await ArchiveExtractor.ExtractAsync(Library, path, output);
        Assert.False(result.IsSuccess);
        Assert.Equal(ArchiveFailureCode.InvalidPath, result.Error!.Code);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task ReadOnlyTarCanBeExtractedWithoutAnApplicationHost()
    {
        var path = Path.Combine(root, "source.tar");
        using (var file = File.Create(path))
        using (var writer = new TarWriter(file))
        using (var data = new MemoryStream(Data))
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "data.txt") { DataStream = data });
        var output = Path.Combine(root, "out");
        Assert.True((await ArchiveExtractor.ExtractAsync(Library, path, output)).IsSuccess);
        Assert.Equal(Data, await File.ReadAllBytesAsync(Path.Combine(output, "data.txt")));
    }

    [Fact]
    public async Task ManagedSinkFailureKeepsItsCategoryAndDoesNotLeakSecrets()
    {
        var path = CreateZip();
        var outcome = await ArchiveExtractor.RunToStreamsWithPasswordsAsync(Library, path, (await ArchiveInspector.InspectAsync(Library, path))!.SelectAll(),
            _ => throw new IOException("secret", unchecked((int)0x80070070)), _ => { }, null, null, default);
        Assert.Equal(ArchiveFailureCode.InsufficientDiskSpace, outcome.Error!.Code);
        Assert.DoesNotContain("secret", outcome.Error.Message);
        Assert.DoesNotContain("secret", outcome.Error.Diagnostic);
        using var unlocked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
