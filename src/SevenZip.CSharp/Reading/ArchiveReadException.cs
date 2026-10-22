namespace SevenZip.CSharp;

public enum ArchiveReadError { ArchiveDamaged, UnsupportedOperation, RequiresPassword, IncorrectPassword }

/// <summary>Typed SDK-open failure, independent of host error models and password UI.</summary>
public sealed class ArchiveReadException : Exception
{
    public ArchiveReadError Error { get; }
    public bool IsVolumeFailure { get; }

    public ArchiveReadException(ArchiveReadError error, string message, bool isVolumeFailure = false) : base(message)
    {
        if (!Enum.IsDefined(error)) throw new ArgumentOutOfRangeException(nameof(error));
        Error = error;
        IsVolumeFailure = isVolumeFailure;
    }
}
