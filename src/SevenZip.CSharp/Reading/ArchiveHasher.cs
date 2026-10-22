using System.Runtime.InteropServices;
using static SevenZip.CSharp.NativeArchiveInterop;

namespace SevenZip.CSharp;

public sealed record ArchiveHashEntry(ArchivePath Entry, string Algorithm, string Hash);
public sealed record ArchiveHashOutcome(ArchiveEngineOutcome Outcome, IReadOnlyList<ArchiveHashEntry>? Entries = null,
    bool DecodingAttempted = false)
{
    public bool IsSuccess => Outcome.IsSuccess;
}

/// <summary>Hashes decoded entry streams with the SDK; never writes temporary extracted files.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class ArchiveHasher
{
    public static Task<ArchiveHashOutcome> ComputeAsync(string libraryPath, string path, IReadOnlyCollection<ArchivePath> entries,
        string algorithm, ArchiveOperationContext? context = null, IProgress<EngineProgress>? progress = null)
    {
        context ??= new();
        return ComputeWithPasswordsAsync(libraryPath, path, entries, algorithm, context.Password, context.Password,
            context.CancellationToken, progress, context);
    }

    public static async Task<ArchiveHashOutcome> ComputeWithPasswordsAsync(string libraryPath, string path,
        IReadOnlyCollection<ArchivePath> entries, string algorithm, ArchivePassword? headerPassword, ArchivePassword? dataPassword,
        CancellationToken token, IProgress<EngineProgress>? progress = null, ArchiveOperationContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (token.IsCancellationRequested) return new(new(true, new(ArchiveFailureCode.OperationCancelled, "操作已取消")));
        var library = SevenZipNativeLibrary.Load(libraryPath);
        using (var probe = SevenZipNativeHasher.Create(library, algorithm))
            if (probe is null) return new(new(true, new(ArchiveFailureCode.UnsupportedOperation, "DLL 不支持此哈希算法：" + algorithm)));
        var results = new List<ArchiveHashEntry>();
        var streams = new List<HashOutput>();
        var gate = new object();
        try
        {
            HashOutput? current = null;
            var outcome = await ArchiveExtractor.RunToStreamsWithPasswordsAsync(libraryPath, path, entries,
                entry =>
                {
                    lock (gate)
                    {
                        current = new HashOutput(SevenZipNativeHasher.Create(library, algorithm)!, token, context?.Control);
                        streams.Add(current); return current;
                    }
                }, entry =>
                {
                    lock (gate)
                    {
                        results.Add(new(entry.Path, algorithm, current!.Finish())); current.Dispose(); current = null;
                    }
                }, headerPassword, dataPassword, token, progress, context).ConfigureAwait(false);
            return new(outcome, outcome.IsSuccess
                ? Array.AsReadOnly(results.OrderBy(entry => entry.Entry.Value, StringComparer.OrdinalIgnoreCase).ToArray()) : null, true);
        }
        finally { foreach (var stream in streams) stream.Dispose(); }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class HashOutput(SevenZipNativeHasher hasher, CancellationToken token, ArchiveOperationControl? control)
        : SequentialOutput, IDisposable
    {
        private readonly object gate = new();
        public int Write(nint data, uint size, nint processed)
        {
            try
            {
                control?.CheckStop(token); token.ThrowIfCancellationRequested();
                lock (gate) hasher.Update(data, size);
                if (processed != 0) Marshal.WriteInt32(processed, checked((int)size)); return 0;
            }
            catch (Exception exception) { return Marshal.GetHRForException(exception); }
        }
        public string Finish() { lock (gate) return hasher.Finish(); }
        public void Dispose() { lock (gate) hasher.Dispose(); }
    }
}
