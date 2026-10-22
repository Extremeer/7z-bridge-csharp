using SevenZip.CSharp;

namespace SevenZip.CSharp.Tests;

public sealed class ArchiveVolumeSetTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "SevenZip.CSharp.Tests",
        Guid.NewGuid().ToString("N"));

    public ArchiveVolumeSetTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void TryAnalyze_SelectingAnyVolume_ResolvesTheFirstVolume()
    {
        var basePath = Path.Combine(directory, "sample.7z");
        File.WriteAllText(basePath + ".001", "one");
        File.WriteAllText(basePath + ".002", "two");
        File.WriteAllText(basePath + ".003", "three");

        Assert.True(ArchiveVolumeSet.TryAnalyze(basePath + ".002", out var set));
        Assert.True(set.IsComplete);
        Assert.Equal(basePath, set.BaseArchivePath);
        Assert.Equal(basePath + ".001", set.FirstVolumePath);
        Assert.Equal(3, set.VolumePaths.Count);
    }

    [Fact]
    public void TryAnalyze_ReportsEveryDetectableGap()
    {
        var basePath = Path.Combine(directory, "sample.zip");
        File.WriteAllText(basePath + ".002", "two");
        File.WriteAllText(basePath + ".004", "four");

        Assert.True(ArchiveVolumeSet.TryAnalyze(basePath + ".004", out var set));
        Assert.False(set.IsComplete);
        Assert.Equal([basePath + ".001", basePath + ".003"], set.MissingVolumePaths);
    }

    [Theory]
    [InlineData("sample.zip")]
    [InlineData("sample.zip.part1")]
    [InlineData("sample.zip.01")]
    public void TryAnalyze_RejectsFilesThatAreNotNumberedVolumes(string name)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "data");

        Assert.False(ArchiveVolumeSet.TryAnalyze(path, out _));
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
