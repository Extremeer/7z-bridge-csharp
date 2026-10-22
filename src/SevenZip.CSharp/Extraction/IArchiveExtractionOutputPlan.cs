namespace SevenZip.CSharp;

/// <summary>
/// Host-resolved outputs. The host owns conflict decisions, rollback and final transaction commit.
/// Paths remain constrained to the supplied destination; the engine never owns unrelated files.
/// All callbacks execute synchronously on the native worker and must not access UI controls directly.
/// </summary>
public interface IArchiveExtractionOutputPlan
{
    ArchiveEntryInfo? OutputEntry(ArchiveEntryInfo entry);
    string OutputPath(ArchiveEntryInfo entry);
    string BeginFile(ArchiveEntryInfo entry);
    void ValidateOutput(ArchiveEntryInfo output);
    bool OwnsDirectory(string path);
    void PrepareDirectories(CancellationToken token);
    void Publish(ArchiveEntryInfo entry, string temporaryPath, string outputPath, CancellationToken token);
}
