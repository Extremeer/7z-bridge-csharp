using System.Diagnostics;

namespace SevenZip.CSharp;

/// <summary>Job-owned cooperative control; hiding a dialog does not resume or cancel it.</summary>
public sealed class ArchiveOperationControl
{
    private readonly object gate = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private TimeSpan pauseStarted, pausedTime;
    private int nativeDepth;
    private bool paused;
    public event Action? Changed;
    public bool CanPause { get { lock (gate) return nativeDepth > 0; } }
    public bool IsPaused { get { lock (gate) return paused; } }
    public TimeSpan ActiveElapsed { get { lock (gate) return (paused ? pauseStarted : clock.Elapsed) - pausedTime; } }
    public void StartTiming()
    {
        lock (gate) { clock.Restart(); pausedTime = TimeSpan.Zero; pauseStarted = TimeSpan.Zero; }
    }
    public void StopTiming()
    {
        lock (gate) { ResumeLocked(); clock.Stop(); }
    }
    public void HoldTiming() { lock (gate) clock.Stop(); }
    public void ResumeTiming() { lock (gate) clock.Start(); }

    public IDisposable EnterNativeOperation()
    {
        lock (gate) nativeDepth++;
        Changed?.Invoke();
        return new Scope(() =>
        {
            lock (gate)
            {
                nativeDepth--;
                if (nativeDepth == 0) ResumeLocked();
            }
            Changed?.Invoke();
        });
    }
    public bool SetPaused(bool value)
    {
        lock (gate)
        {
            if (nativeDepth == 0) return false;
            if (paused == value) return true;
            if (value) { paused = true; pauseStarted = clock.Elapsed; }
            else ResumeLocked();
        }
        Changed?.Invoke(); return true;
    }
    private void ResumeLocked()
    {
        if (paused) { pausedTime += clock.Elapsed - pauseStarted; paused = false; }
        Monitor.PulseAll(gate);
    }
    public void CheckStop(CancellationToken token)
    {
        if (!TryCheckStop(token)) token.ThrowIfCancellationRequested();
    }
    /// <summary>Waits while paused; native callbacks use false to return E_ABORT without throwing.</summary>
    public bool TryCheckStop(CancellationToken token)
    {
        lock (gate)
        {
            while (paused && !token.IsCancellationRequested) Monitor.Wait(gate, 100);
            return !token.IsCancellationRequested;
        }
    }
    private sealed class Scope(Action exit) : IDisposable
    {
        private Action? release = exit;
        public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
    }
}
