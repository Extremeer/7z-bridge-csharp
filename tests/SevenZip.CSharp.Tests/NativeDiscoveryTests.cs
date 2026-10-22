using SevenZip.CSharp;

namespace SevenZip.CSharp.Tests;

public sealed class NativeDiscoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SevenZip.CSharp.Tests", Guid.NewGuid().ToString("N"));

    public NativeDiscoveryTests() => Directory.CreateDirectory(root);

    [Fact]
    public void DefaultDiscoveryDoesNotConsumeParentRepositoryPayload()
    {
        var child = Path.Combine(root, "child");
        Directory.CreateDirectory(child);
        File.WriteAllBytes(Path.Combine(root, "7z.dll"), [1, 2, 3]);
        Assert.Throws<FileNotFoundException>(() => SevenZipNativeSettings.Discover(child));
        // Explicit legacy opt-in reaches that parent but correctly rejects its invalid image.
        Assert.Throws<BadImageFormatException>(() => SevenZipNativeSettings.Discover(child, searchParents: true));
    }

    [Fact]
    public void ExplicitMissingPathDoesNotFallBackToAnotherPayload()
    {
        File.WriteAllBytes(Path.Combine(root, "7z.dll"), [1, 2, 3]);
        Assert.Throws<FileNotFoundException>(() => SevenZipNativeSettings.Discover(root,
            Path.Combine(root, "missing.dll")));
    }

    [Fact]
    public void ExplicitPathIsNormalizedAndArchitectureChecked()
    {
        var actual = SevenZipNativeSettings.AutoDiscover();
        var other = actual.Architecture == System.Runtime.InteropServices.Architecture.X64
            ? System.Runtime.InteropServices.Architecture.X86 : System.Runtime.InteropServices.Architecture.X64;
        Assert.Throws<BadImageFormatException>(() => new SevenZipNativeSettings(actual.LibraryPath, other));
        Assert.Equal(Path.GetFullPath(actual.LibraryPath), actual.LibraryPath);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
