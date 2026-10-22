namespace SevenZip.CSharp;

/// <summary>One atomic snapshot of native work progress and codec ratio counters.</summary>
public sealed class ArchiveProgressMetrics
{
    private readonly object gate = new();
    private long? total, completed, ratioInput, ratioOutput;

    public void SetTotal(long value) { lock (gate) total = Math.Max(0, value); }
    public void SetCompleted(long? value) { lock (gate) { if (value.HasValue) completed = Math.Max(0, value.Value); } }
    public void SetRatio(long? input, long? output)
    {
        lock (gate)
        {
            if (input.HasValue) ratioInput = Math.Max(0, input.Value);
            if (output.HasValue) ratioOutput = Math.Max(0, output.Value);
        }
    }

    public ArchiveProgressSnapshot Snapshot(TimeSpan activeElapsed)
    {
        lock (gate)
        {
            return Calculate(total, completed, ratioInput, ratioOutput, activeElapsed);
        }
    }

    // Both native snapshots and timer-driven UI rendering use the same upstream
    // formula. Re-rendering must not keep a speed/ETA from an old callback time.
    public static ArchiveProgressSnapshot Calculate(long? total, long? completed,
        long? ratioInput, long? ratioOutput, TimeSpan activeElapsed)
    {
        // Average speed = completed * 1000 / elapsedMilliseconds;
        // remaining = elapsed * (total - completed) / completed.
        // Use floating-point intermediates to avoid UInt64 multiplication overflow.
        var seconds = Math.Max(.001, activeElapsed.TotalSeconds);
        var speed = completed is >= 0 ? completed.Value / seconds : (double?)null;
        var remainingSeconds = total.HasValue && completed is > 0
            ? seconds * Math.Max(0d, (double)total.Value - completed.Value) / completed.Value : (double?)null;
        var remaining = remainingSeconds is >= 0 && remainingSeconds < TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromSeconds(remainingSeconds.Value) : (TimeSpan?)null;
        return new(total, completed, total is > 0 && completed.HasValue
            ? Math.Clamp(completed.Value * 100d / total.Value, 0, 99) : null,
            speed, remaining, ratioInput, ratioOutput, activeElapsed);
    }
}

public sealed record ArchiveProgressSnapshot(long? Total, long? Completed, double? Percentage,
    double? AverageSpeed, TimeSpan? Remaining, long? RatioInput, long? RatioOutput, TimeSpan ActiveElapsed);
