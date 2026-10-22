namespace SevenZip.CSharp;

public enum EngineFailureStage { Validation, Preparing, Engine, Committing, Cleanup }
public sealed record ArchiveFailure(ArchiveFailureCode Code, string Message, string? Path = null, string? Diagnostic = null,
    EngineFailureStage? Stage = null);

public sealed record ArchiveEngineOutcome(bool Applicable, ArchiveFailure? Error = null, string? Diagnostic = null)
{
    public bool IsSuccess => Applicable && Error is null;
}

/// <summary>Safe classification without logging source paths, passwords or arbitrary exception messages.</summary>
public static class EngineFailureClassifier
{
    public static string Diagnostic(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return $"{exception.GetType().Name}; HResult=0x{exception.HResult:X8}";
    }

    public static ArchiveFailureCode FromIOException(IOException exception) => (exception.HResult & 0xFFFF) switch
    {
        0x27 or 0x70 => ArchiveFailureCode.InsufficientDiskSpace,
        0x20 or 0x21 or 0x4C8 => ArchiveFailureCode.ArchiveLocked,
        _ => ArchiveFailureCode.IoFailure
    };

    public static ArchiveFailure? TryMap(Exception exception)
    {
        ArchiveFailureCode? code = exception switch
        {
            ArchiveWriterBusyException => ArchiveFailureCode.ArchiveBusy,
            ArchiveReadException read => Enum.Parse<ArchiveFailureCode>(read.Error.ToString()),
            SevenZipNativeDependencyException dependency => dependency.Failure switch
            {
                SevenZipNativeDependencyFailure.Missing => ArchiveFailureCode.NativeEngineMissing,
                SevenZipNativeDependencyFailure.InvalidImage => ArchiveFailureCode.NativeEngineInvalid,
                SevenZipNativeDependencyFailure.ArchitectureMismatch => ArchiveFailureCode.NativeEngineArchitectureMismatch,
                SevenZipNativeDependencyFailure.IncompatibleSdk => ArchiveFailureCode.NativeEngineIncompatible,
                _ => ArchiveFailureCode.NativeEngineLoadFailed
            },
            UnauthorizedAccessException => ArchiveFailureCode.PermissionDenied,
            FileNotFoundException or DirectoryNotFoundException or PathTooLongException => ArchiveFailureCode.InvalidPath,
            InvalidDataException => ArchiveFailureCode.ArchiveDamaged,
            IOException io => FromIOException(io),
            OutOfMemoryException => ArchiveFailureCode.InsufficientMemory,
            NotSupportedException => ArchiveFailureCode.UnsupportedOperation,
            _ => null
        };
        if (code is null) return null;
        var message = exception is ArchiveReadException or SevenZipNativeDependencyException ? exception.Message : code switch
        {
            ArchiveFailureCode.PermissionDenied => "Access to the archive or destination was denied.",
            ArchiveFailureCode.InvalidPath => "A required source or destination path is unavailable.",
            ArchiveFailureCode.ArchiveDamaged => "The archive data is damaged or invalid.",
            ArchiveFailureCode.ArchiveLocked => "The archive or destination is in use by another operation.",
            ArchiveFailureCode.InsufficientDiskSpace => "There is not enough free disk space.",
            ArchiveFailureCode.InsufficientMemory => "There is not enough memory to complete the operation.",
            ArchiveFailureCode.UnsupportedOperation => "This operation is not supported.",
            _ => "An archive input/output operation failed."
        };
        return new(code.Value, message, Diagnostic: Diagnostic(exception));
    }
}
