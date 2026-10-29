using SevenZip.CSharp;
namespace SevenZip.CSharp.Tests;
// Test fixture names are queries; the library receives only IDs from a fresh snapshot.
internal sealed record NamedMove(ArchivePath Source, ArchivePath Destination);
internal sealed record NamedUpdatePlan
{
    public IReadOnlyList<ArchivePath> Deleted { get; init; } = [];
    public IReadOnlyList<NamedMove> Moves { get; init; } = [];
    public ArchivePath? RenameSource { get; init; }
    public ArchivePath? RenameDestination { get; init; }
    public IReadOnlyList<ArchiveSourceItem>? Sources { get; init; }
}
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class NamedEntryFixture
{
    public static async Task<ArchiveHashOutcome> HashAsync(string library, string path, IReadOnlyCollection<ArchivePath> names,
        string algorithm, ArchiveOperationContext? context = null)
    {
        var snapshot = (await ArchiveInspector.InspectAsync(library, path, context))!;
        var selection = names.Any(name => name.IsRoot) ? snapshot.SelectAll() : snapshot.Select(snapshot.Entries
            .Where(item => names.Any(name => name.Equals(item.Path))).Select(item => item.Id));
        return await ArchiveHasher.ComputeItemsAsync(library, path, selection, algorithm, context);
    }
    public static async Task<ArchiveEngineOutcome> ApplyAsync(string library, string path, NamedUpdatePlan plan,
        ArchiveOperationContext? context = null, IProgress<EngineProgress>? progress = null)
    {
        ArchiveInspector.Snapshot? snapshot;
        try { snapshot = await ArchiveInspector.InspectAsync(library, path, context); }
        catch (ArchiveReadException error)
        { return new(true, new(Enum.Parse<ArchiveFailureCode>(error.Error.ToString()), error.Message)); }
        if (snapshot is null) return await ArchiveUpdater.UpdateItemsAsync(library, path, new()
            { Deleted = new(ArchiveRevision.New(), []), Sources = plan.Sources }, context, progress);
        var deleted = snapshot.Select(snapshot.Entries.Where(item => plan.Deleted.Any(name => name.Equals(item.Path))).Select(item => item.Id));
        var moves = plan.Moves.Concat(plan.RenameSource is { } source ? [new NamedMove(source, plan.RenameDestination!)] : []).Select(move =>
            new ArchiveItemMove(snapshot.Entries.Single(item => item.Path.Equals(move.Source)).Id, move.Destination)).ToArray();
        return await ArchiveUpdater.UpdateItemsAsync(library, path, new()
            { Deleted = deleted, Moves = moves, Sources = plan.Sources }, context, progress);
    }
}
