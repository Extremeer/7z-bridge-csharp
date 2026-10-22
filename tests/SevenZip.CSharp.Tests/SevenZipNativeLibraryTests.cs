using SevenZip.CSharp;

namespace SevenZip.CSharp.Tests;

public sealed class SevenZipNativeLibraryTests
{
    private static string LibraryPath => SevenZipNativeSettings.AutoDiscover().LibraryPath;

    [Fact]
    public void ReadsActualSdkFormatsAndWriteCapabilities()
    {
        var library = SevenZipNativeLibrary.Load(LibraryPath);
        Assert.True(library.Formats.Count > 20);
        var zip = Assert.IsType<SevenZipNativeFormat>(library.FindFormat("ZIP"));
        var sevenZip = Assert.IsType<SevenZipNativeFormat>(library.FindFormat("7z"));
        Assert.True(zip.CanUpdate); Assert.True(sevenZip.CanUpdate);
        foreach (var name in new[] { "zip", "7z", "rar", "rar5", "cab" })
            Assert.True(library.FindFormat(name)!.CanFindSignature, name);
        foreach (var name in new[] { "tar", "iso", "wim" })
            Assert.False(library.FindFormat(name)!.CanFindSignature, $"{name}: flags=0x{library.FindFormat(name)!.Flags:X8}");
        Assert.Equal(new Guid("23170F69-40C1-278A-1000-000110010000"), zip.ClassId);
        Assert.Equal(new Guid("23170F69-40C1-278A-1000-000110070000"), sevenZip.ClassId);
        Assert.False(Assert.IsType<SevenZipNativeFormat>(library.FindFormat("Rar")).CanUpdate);
        Assert.False(Assert.IsType<SevenZipNativeFormat>(library.FindFormat("Iso")).CanUpdate);
        Assert.Contains(zip, library.FindExtensionCandidates(".ZIP"));
        Assert.Null(library.FindFormat("not-a-format"));
        Assert.Empty(library.FindExtensionCandidates("not-a-format"));
        Assert.NotEqual(0, library.GetExport("CreateObject"));
    }

    [Fact]
    public async Task NormalizedPathsAndConcurrentLoadsShareOneImmutableCatalog()
    {
        var path = Path.GetFullPath(LibraryPath);
        var alias = Path.Combine(Path.GetDirectoryName(path)!, ".", Path.GetFileName(path));
        var loads = await Task.WhenAll(Enumerable.Range(0, 16).Select(index =>
            Task.Run(() => SevenZipNativeLibrary.Load(index % 2 == 0 ? path : alias))));
        Assert.All(loads, library => Assert.Same(loads[0], library));
        Assert.Equal(path, loads[0].LibraryPath);
        var formats = Assert.IsAssignableFrom<IList<SevenZipNativeFormat>>(loads[0].Formats);
        Assert.True(formats.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => formats.RemoveAt(0));
        var extensions = Assert.IsAssignableFrom<IList<string>>(loads[0].FindFormat("zip")!.Extensions);
        Assert.True(extensions.IsReadOnly);
    }

    [Fact]
    public void MissingLibraryFailsBeforeNativeLoading()
    {
        Assert.Equal(SevenZipNativeDependencyFailure.Missing, Assert.Throws<SevenZipNativeDependencyException>(() => SevenZipNativeLibrary.Load(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "7z.dll"))).Failure);
        Assert.Throws<ArgumentException>(() => SevenZipNativeLibrary.Load(" "));
    }

    [Theory]
    [InlineData("cab", 0U, false)] [InlineData("zip", 0U, false)]
    [InlineData("unknown", 8U, true)] [InlineData("iso", 24U, true)]
    [InlineData("7z", 16U, false)]
    public void SignatureSearchCapabilityComesFromFlagsNotAFormatName(string name, uint flags, bool expected)
    {
        var format = new SevenZipNativeFormat(0, name, Guid.Empty, [], false, []) { Flags = flags };
        Assert.Equal(expected, format.CanFindSignature);
        var (index, actualName, id, extensions, update, signatures) = format;
        Assert.Equal(0U, index); Assert.Equal(name, actualName); Assert.Equal(Guid.Empty, id);
        Assert.Empty(extensions); Assert.False(update); Assert.Empty(signatures);
    }

    [Fact]
    public void SignatureCatalogContainsImmutableBinaryBytesAndNonzeroOffsets()
    {
        var library = SevenZipNativeLibrary.Load(LibraryPath);
        var zip = library.FindFormat("zip")!;
        Assert.True(zip.Signatures.Count > 1);
        Assert.Contains(library.FindFormat("iso")!.Signatures, signature => signature.Offset > 0);
        var signature = Assert.Single(library.FindFormat("7z")!.Signatures);
        Assert.Equal(new byte[] { 0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c }, signature.Bytes);
        var bytes = Assert.IsAssignableFrom<IList<byte>>(signature.Bytes);
        Assert.Throws<NotSupportedException>(() => bytes[0] = 0);
    }
}
