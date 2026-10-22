using System.Diagnostics;
using System.Runtime.InteropServices;
using static SevenZip.CSharp.NativeArchiveInterop;

namespace SevenZip.CSharp;

/// <summary>SDK work counters are not stream bytes. Hash bytes count only successful sink writes.</summary>
public sealed class ArchiveReadStatistics(IEnumerable<ArchiveEntryInfo> entries, bool hashing, IProgress<EngineProgress>? observer, ArchiveOperationControl? control = null)
{
    private readonly object gate = new();
    private readonly ArchiveProgressMetrics work = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly TimeSpan started = control?.ActiveElapsed ?? TimeSpan.Zero;
    private readonly long? totalBytes = entries.Where(e => !e.IsDirectory).All(e => e.HasKnownUncompressedSize)
        ? entries.Where(e => !e.IsDirectory).Sum(e => e.UncompressedSize) : null;
    private readonly int totalFiles = entries.Count(e => !e.IsDirectory);
    private long bytes;
    private int files;
    private string? currentItem;
    private TimeSpan last = TimeSpan.MinValue;

    public void SetTotal(long total) { lock (gate) { work.SetTotal(total); Report(true); } }
    public void SetCompleted(long? completed) { lock (gate) { work.SetCompleted(completed); Report(); } }
    public void SetRatio(long? input, long? output) { lock (gate) work.SetRatio(input, output); }
    public void BeginFile(ArchiveEntryInfo entry) { lock (gate) { currentItem = entry.Path.Value; Report(true); } }
    public void ValidatedFile() { lock (gate) { files++; Report(true); } }
    public void Finish() { lock (gate) Report(true); }
    private void Written(uint count) { lock (gate) { var first = bytes == 0; bytes += count; Report(first); } }
    private void Report(bool force = false)
    {
        var elapsed = control is null ? clock.Elapsed : control.ActiveElapsed - started;
        if (!force && last != TimeSpan.MinValue && (elapsed - last).TotalMilliseconds < 100) return;
        last = elapsed;
        var snapshot = work.Snapshot(elapsed);
        // HashGUI uses actual hashed bytes, not decoder work on unselected solid
        // entries. Integrity testing uses ExtractCallback's SDK work counters.
        var hashTotal = totalBytes ?? (files == totalFiles ? bytes : (long?)null);
        var displayed = hashing ? ArchiveProgressMetrics.Calculate(hashTotal, bytes, null, null, elapsed) : snapshot;
        observer?.Report(new EngineProgress(EngineStage.Reading, displayed.Percentage, currentItem,
            ProcessedBytes: hashing ? bytes : snapshot.Completed ?? 0,
            TotalBytes: hashing ? hashTotal : snapshot.Total ?? totalBytes,
            BytesPerSecond: displayed.AverageSpeed,
            EstimatedRemaining: displayed.Remaining, ProcessedItemCount: files, TotalItemCount: totalFiles,
            HasMeasuredProcessedBytes: hashing || snapshot.Completed.HasValue, MetricsScope: hashing ? "hash-decoded-input" : "integrity-native-work",
            IsRemainingEstimated: displayed.Remaining.HasValue, ActiveElapsed: elapsed, IsPaused: control?.IsPaused == true,
            NativeWorkCompleted: snapshot.Completed, NativeWorkTotal: snapshot.Total,
            RatioInputBytes: snapshot.RatioInput, RatioOutputBytes: snapshot.RatioOutput));
    }

    public SequentialOutput Wrap(SequentialOutput sink) => new CountingOutput(sink, this);
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class CountingOutput(SequentialOutput sink, ArchiveReadStatistics owner) : SequentialOutput
    {
        public unsafe int Write(nint data, uint size, nint processed)
        {
            uint accepted = 0;
            var result = sink.Write(data, size, (nint)(&accepted));
            if (processed != 0) Marshal.WriteInt32(processed, unchecked((int)accepted));
            if (result == 0 && accepted <= size) owner.Written(accepted);
            return result;
        }
    }
}
