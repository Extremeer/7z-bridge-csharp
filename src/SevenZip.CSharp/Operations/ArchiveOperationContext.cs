using System.Collections.Frozen;

namespace SevenZip.CSharp;

/// <summary>
/// Explicit per-operation inputs. Password and control are borrowed, never disposed by an operation.
/// No ambient state is read; callers may use independent contexts concurrently.
/// </summary>
public sealed class ArchiveOperationContext
{
    public ArchivePassword? Password { get; }
    public ArchiveOperationControl? Control { get; }
    public CancellationToken CancellationToken { get; }
    /// <summary>Optional host format policy. Null allows all handlers registered by the DLL.</summary>
    public IReadOnlySet<string>? AllowedFormats { get; }

    public ArchiveOperationContext(ArchivePassword? password = null, ArchiveOperationControl? control = null,
        CancellationToken cancellationToken = default, IEnumerable<string>? allowedFormats = null)
    {
        Password = password;
        Control = control;
        CancellationToken = cancellationToken;
        AllowedFormats = allowedFormats?.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    public void CheckStop()
    {
        CancellationToken.ThrowIfCancellationRequested();
        Control?.CheckStop(CancellationToken);
    }
}
