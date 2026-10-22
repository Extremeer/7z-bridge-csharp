namespace SevenZip.CSharp;

/// <summary>
/// 领域错误枚举，覆盖压缩包操作的典型失败原因。
/// </summary>
public enum ArchiveFailureCode
{
    None = 0,
    InvalidPath,
    InvalidName,
    InvalidFormat,
    UnsupportedOperation,
    InvalidOperation,
    InvalidInput,
    ConflictDetected,
    RequiresPassword,
    IncorrectPassword,
    EntryNotFound,
    EntryAlreadyExists,
    ArchiveLocked,
    ArchiveDamaged,
    InsufficientDiskSpace,
    IoFailure,
    PermissionDenied,
    OperationCancelled,
    ArchiveWarning,
    EngineFailure,
    InsufficientMemory,
    Unknown,
    NativeEngineMissing,
    NativeEngineInvalid,
    NativeEngineArchitectureMismatch,
    NativeEngineIncompatible,
    NativeEngineLoadFailed,
    ArchiveBusy,
}
