namespace SevenZip.CSharp;

public enum ArchiveUpdateMode { Add, Update }
public sealed record ArchiveUpdateOptions(bool Overwrite = false, ArchiveUpdateMode WriteMode = ArchiveUpdateMode.Add);
public sealed record ArchiveMove(ArchivePath Source, ArchivePath? DestinationPath);

/// <summary>One resolved update. The engine reuses retained encoded data in a single UpdateItems call.</summary>
public sealed record ArchiveUpdateRequest
{
    public IReadOnlyCollection<ArchivePath> Deleted { get; init; } = Array.Empty<ArchivePath>();
    public ArchivePath? RenameSource { get; init; }
    public ArchivePath? RenameDestination { get; init; }
    public IReadOnlyList<ArchiveMove>? Moves { get; init; }
    public IReadOnlyList<ArchiveSourceItem>? Sources { get; init; }
    public string? StagingRoot { get; init; }
    public ArchiveUpdateOptions? AddOptions { get; init; }
    /// <summary>Optional caller-owned transaction output. Null atomically replaces the protected source on success.</summary>
    public string? TransactionOutputPath { get; init; }
    internal ArchiveUpdateRequest Snapshot() => this with
    {
        Deleted = Array.AsReadOnly(Deleted.ToArray()), Moves = Moves is null ? null : Array.AsReadOnly(Moves.ToArray()),
        Sources = Sources is null ? null : Array.AsReadOnly(Sources.ToArray()),
        AddOptions = AddOptions ?? (Sources is not null || StagingRoot is not null ? new ArchiveUpdateOptions() : null)
    };
}
