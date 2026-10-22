using System.Diagnostics;

namespace SevenZip.CSharp;

/// <summary>
/// Counts only successful output-stream writes, never preallocated file lengths.
/// File completion is reported by the native extraction callback after CRC checks.
/// </summary>
public sealed class ExtractionStatistics(ExtractionMeasurement progress, ArchiveOperationControl? control = null)
{
    private readonly object gate = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly TimeSpan activeStarted = control?.ActiveElapsed ?? TimeSpan.Zero;
    private readonly ArchiveProgressMetrics metrics = new();
    private bool hasNativeWorkCounters;
    private readonly long? total = progress.Files.All(entry => entry.HasKnownUncompressedSize)
        ? progress.Files.Sum(entry => entry.UncompressedSize) : null;
    private int percent;
    private EngineStage currentStage = EngineStage.Extracting;
    private ArchivePath currentItem = ArchivePath.Root;

    public void BeginFile(ArchivePath path)
    {
        lock (gate)
        {
            currentItem = path;
            currentStage = EngineStage.Extracting;
            Sample();
        }
    }

    public void SetPercentage(int value) { lock (gate) percent = Math.Max(percent, value); }
    public void SetNativeTotal(long value) { lock (gate) { hasNativeWorkCounters = true; metrics.SetTotal(value); } }
    public void SetNativeCompleted(long? value) { lock (gate) metrics.SetCompleted(value); }
    public void SetNativeRatio(long? input, long? output) { lock (gate) metrics.SetRatio(input, output); }

    public void RecordWrite(long count)
    {
        lock (gate)
        {
            currentStage = EngineStage.Extracting;
            var firstWrite = writtenBytes == 0 && count > 0;
            writtenBytes += count;
            // Short stored ZIP entries can finish before the periodic sample.
            // Publish the first real write so cancellation and observers remain live.
            if (firstWrite) Sample();
        }
    }
    public void RecordCompletedFile() { lock (gate) { completedFiles++; currentStage = EngineStage.Extracting; } }
    private long writtenBytes;
    private int completedFiles;
    private string? observerDiagnostic;
    public string? ObserverDiagnostic => Volatile.Read(ref observerDiagnostic);
    private void CaptureObserverFailure(Exception exception) => Interlocked.CompareExchange(ref observerDiagnostic,
        "Progress sampling failed: " + EngineFailureClassifier.Diagnostic(exception), null);
    private bool TrySample()
    {
        try { Sample(); return true; }
        catch (Exception exception) { CaptureObserverFailure(exception); return false; }
    }

    private int phaseReporting;
    public void ReportCleanup() => ReportStage(EngineStage.CleaningUp);
    public void ReportStage(EngineStage stage)
    {
        if (Interlocked.CompareExchange(ref phaseReporting, 1, 0) != 0) return;
        try { Sample(stage); }
        catch (Exception exception) { CaptureObserverFailure(exception); } // Advisory notification must not replace the operation result.
        finally { Volatile.Write(ref phaseReporting, 0); }
    }
    public void Sample(EngineStage? stage = null)
    {
        lock (gate)
        {
            if (stage.HasValue) currentStage = stage.Value;
            var bytes = writtenBytes;
            var now = control is null ? clock.Elapsed : control.ActiveElapsed - activeStarted;
            if (!hasNativeWorkCounters) { if (total.HasValue) metrics.SetTotal(total.Value); metrics.SetCompleted(bytes); }
            var snapshot = metrics.Snapshot(now);
            var percentage = hasNativeWorkCounters ? snapshot.Percentage : Math.Min(99, percent);
            // Per-file validation/publication are still part of the same measured
            // extraction. Do not reset the overall bar and metrics at file boundaries.
            var active = currentStage is EngineStage.Extracting
                or EngineStage.Validating or EngineStage.Committing;
            var operation = new EngineProgress(currentStage, active ? percentage : null,
                CurrentItem: currentItem.IsRoot ? null : currentItem.Value,
                ProcessedBytes: snapshot.Completed ?? bytes,
                TotalBytes: snapshot.Total ?? total ?? (completedFiles == progress.Files.Count ? bytes : null),
                BytesPerSecond: active ? snapshot.AverageSpeed : null,
                EstimatedRemaining: active ? snapshot.Remaining : null,
                ProcessedItemCount: completedFiles, TotalItemCount: progress.Files.Count,
                HasMeasuredProcessedBytes: true, MetricsScope: "extraction-engine-output", IsRemainingEstimated: true,
                ActiveElapsed: now, IsPaused: control?.IsPaused == true,
                NativeWorkCompleted: hasNativeWorkCounters ? snapshot.Completed : null,
                NativeWorkTotal: hasNativeWorkCounters ? snapshot.Total : null,
                RatioInputBytes: snapshot.RatioInput, RatioOutputBytes: snapshot.RatioOutput);
            // Preserve exact destination I/O independently of the SDK's work
            // statistics, for cancellation, validation and committed summaries.
            progress.Report(new EngineExtractionProgress(ArchivePath.Root, bytes,
                total ?? (completedFiles == progress.Files.Count ? bytes : 0), (int?)operation.Percentage, operation));
        }
    }

    public async Task<T> RunAsync<T>(Func<Task<T>> execute)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        Sample();
        var sampling = SampleAsync(timer);
        try { return await execute().ConfigureAwait(false); }
        finally { timer.Dispose(); await sampling.ConfigureAwait(false); TrySample(); }
    }
    private async Task SampleAsync(PeriodicTimer timer)
    {
        while (await timer.WaitForNextTickAsync().ConfigureAwait(false)) { if (!TrySample()) break; }
    }
}
