namespace SevenZip.CSharp;

public sealed record ArchiveItemMove(ArchiveEntryId Source, ArchivePath Destination);

/// <summary>All existing items are addressed by versioned ID; paths only name new output destinations.</summary>
public sealed record ArchiveItemUpdateRequest
{
    public ArchiveEntrySelection? Deleted { get; init; }
    public IReadOnlyList<ArchiveItemMove>? Moves { get; init; }
    public IReadOnlyList<ArchiveSourceItem>? Sources { get; init; }
    public string? StagingRoot { get; init; }
    public ArchiveUpdateOptions? AddOptions { get; init; }
    public string? TransactionOutputPath { get; init; }
    internal ArchiveItemUpdateRequest Snapshot() => this with
    {
        Moves = Moves is null ? null : Array.AsReadOnly(Moves.ToArray()),
        Sources = Sources is null ? null : Array.AsReadOnly(Sources.ToArray()),
        AddOptions = AddOptions ?? (Sources is not null || StagingRoot is not null ? new ArchiveUpdateOptions() : null)
    };
}
