namespace SevenZip.CSharp;

public sealed record EngineExtractionProgress(ArchivePath Entry, long ProcessedBytes, long TotalBytes, int? Percentage = null,
    EngineProgress? OperationProgress = null);

/// <summary>Expected entries are supplied by a validated snapshot, not inferred from destination contents.</summary>
public sealed class ExtractionMeasurement(IEnumerable<ArchiveEntryInfo> entries, IProgress<EngineExtractionProgress> observer)
    : IProgress<EngineExtractionProgress>
{
    public IReadOnlyList<ArchiveEntryInfo> Entries { get; } = entries.ToArray();
    public IReadOnlyList<ArchiveEntryInfo> Files => Entries.Where(entry => !entry.IsDirectory).ToArray();
    public void Report(EngineExtractionProgress progress) => observer.Report(progress);
}

public sealed class ActionProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}
