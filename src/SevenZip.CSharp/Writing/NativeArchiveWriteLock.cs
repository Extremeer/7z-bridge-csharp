using System.Security.Cryptography;
using System.Text;

namespace SevenZip.CSharp;

/// <summary>One Windows-session writer per archive, including separate shell processes.</summary>
public static class NativeArchiveWriteLock
{
    public static string ResourcePath(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var suffix = Path.GetExtension(full);
        if (suffix.Length >= 4 && int.TryParse(suffix.AsSpan(1), out var number) && number > 0)
        {
            var stem = full[..^suffix.Length];
            if (Path.GetExtension(stem).ToLowerInvariant() is ".zip" or ".7z") full = stem;
        }
        return full;
    }
    public static string ResourceName(string path) => "Local\\SevenZip.CSharp.ArchiveWrite." + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(ResourcePath(path).ToUpperInvariant())));

    public static Task<IDisposable> AcquireAsync(string path, CancellationToken token = default)
        => AcquireCoreAsync(path, wait: false, token);

    // Navigation/refresh is not a second modification; preserve its short read synchronization.
    internal static Task<IDisposable> AcquireReadAsync(string path, CancellationToken token = default)
        => AcquireCoreAsync(path, wait: true, token);

    private static Task<IDisposable> AcquireCoreAsync(string path, bool wait, CancellationToken token)
    {
        var name = ResourceName(path);
        var acquired = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Mutex ownership is thread-affine. This thread owns both WaitOne and
        // ReleaseMutex; callers can await/dispose their lease on any thread.
        var thread = new Thread(() =>
        {
            Mutex? mutex = null;
            var held = false;
            try
            {
                mutex = new Mutex(false, name);
                do
                {
                    token.ThrowIfCancellationRequested();
                    try { held = mutex.WaitOne(wait ? 100 : 0); }
                    catch (AbandonedMutexException) { held = true; }
                } while (wait && !held);
                token.ThrowIfCancellationRequested();
                if (!held) throw new ArchiveWriterBusyException(path);
                using var lease = new Lease();
                acquired.SetResult(lease);
                lease.WaitForRelease();
            }
            catch (OperationCanceledException) { acquired.TrySetCanceled(token); }
            catch (Exception exception) { acquired.TrySetException(exception); }
            finally { if (held) mutex!.ReleaseMutex(); mutex?.Dispose(); }
        }) { IsBackground = true, Name = "7z-bridge-csharp archive writer lock" };
        thread.Start();
        return acquired.Task;
    }

    private sealed class Lease : IDisposable
    {
        private readonly ManualResetEventSlim released = new();
        private readonly Thread owner = Thread.CurrentThread;
        private int signalled;
        public void WaitForRelease() { try { released.Wait(); } finally { released.Dispose(); } }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref signalled, 1) == 0) released.Set();
            // Release is complete on return, so an immediate retry cannot see our stale owner.
            if (Thread.CurrentThread != owner) owner.Join();
        }
    }
}

/// <summary>A competing library writer owns this archive; never queue a second mutation.</summary>
public sealed class ArchiveWriterBusyException(string path) : IOException("The archive is being modified by another task. Please try again later.")
{
    public string ArchivePath { get; } = path;
}
