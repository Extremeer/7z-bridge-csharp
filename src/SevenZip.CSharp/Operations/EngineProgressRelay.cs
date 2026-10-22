namespace SevenZip.CSharp;

/// <summary>Explicit operation-local relay. Advisory events cannot veto cleanup or committed output.</summary>
public sealed class EngineProgressRelay(IProgress<EngineProgress>? target) : IProgress<EngineProgress>
{
    private EngineProgress? last;
    private int reporting;
    private string? diagnostic;
    public string? ObserverDiagnostic => Volatile.Read(ref diagnostic);
    public void Report(EngineProgress value) { Volatile.Write(ref last, value); target?.Report(value); }
    public void RecordObserverFailure(Exception exception) => Interlocked.CompareExchange(ref diagnostic,
        "Phase progress reporting failed: " + EngineFailureClassifier.Diagnostic(exception), null);
    public void ReportCompletion(EngineProgress value)
    {
        if (Interlocked.CompareExchange(ref reporting, 1, 0) != 0) return;
        try { Report(value); }
        catch (Exception exception) { RecordObserverFailure(exception); }
        finally { Volatile.Write(ref reporting, 0); }
    }
    public void ReportStage(EngineStage stage, string? item = null)
    {
        if (Interlocked.CompareExchange(ref reporting, 1, 0) != 0) return;
        try
        {
            var previous = Volatile.Read(ref last) ?? new EngineProgress(stage);
            Report(previous with { Stage = stage, CurrentItem = item ?? previous.CurrentItem, Percentage = null,
                BytesPerSecond = null, OutputBytesPerSecond = null, EstimatedRemaining = null, IsPaused = false });
        }
        catch (Exception exception) { RecordObserverFailure(exception); }
        finally { Volatile.Write(ref reporting, 0); }
    }
    public static string? CombineDiagnostics(string? primary, string? auxiliary)
    {
        if (auxiliary is null) return primary;
        if (primary is null) return auxiliary;
        if (string.IsNullOrWhiteSpace(auxiliary)) return primary;
        if (string.IsNullOrWhiteSpace(primary)) return auxiliary;
        return primary.Contains(auxiliary, StringComparison.Ordinal) ? primary : primary + "; " + auxiliary;
    }
}
