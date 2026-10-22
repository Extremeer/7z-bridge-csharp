namespace SevenZip.CSharp;

public enum ArchiveWriteFormat { SevenZip, Zip, Tar, Wim, Xz, GZip, BZip2, Zstd, ZstdTar, Unknown }
public enum ArchiveSolidMode { Default, On, Off }
public enum ArchiveEncryptionAlgorithm { Default, Aes256, Aes192, Aes128, ZipCrypto }

public sealed record ArchiveCompressionSettings
{
    public int? Level { get; init; }
    public string? Method { get; init; }
    public IReadOnlyList<string> MethodChain { get; init; } = Array.Empty<string>();
    public string? DictionarySize { get; init; }
    public int? FastBytes { get; init; }
    public string? MatchFinder { get; init; }
    public ArchiveSolidMode SolidMode { get; init; }
    public string? SolidBlockSize { get; init; }
    public int? ThreadCount { get; init; }
}

/// <summary>Password ownership belongs to the explicit operation context, not to serializable options.</summary>
public sealed record ArchiveEncryptionSettings
{
    public ArchiveEncryptionAlgorithm? Method { get; init; }
    public bool EncryptFileNames { get; init; }
}

public sealed record ArchiveSplitSettings(long VolumeSize);
public sealed record ArchiveSfxSettings
{
    public bool Enable { get; init; }
    public string? ModulePath { get; init; }
    public string? IconPath { get; init; }
    public string? VersionResourcePath { get; init; }
}

public sealed record ArchiveWriteOptions
{
    public required string ArchivePath { get; init; }
    public ArchiveWriteFormat? Format { get; init; }
    public ArchiveCompressionSettings? Compression { get; init; }
    public ArchiveEncryptionSettings? Encryption { get; init; }
    public ArchiveSplitSettings? Split { get; init; }
    public ArchiveSfxSettings? Sfx { get; init; }

    internal ArchiveWriteOptions Snapshot() => this with
    {
        Compression = Compression is { } value ? value with { MethodChain = Array.AsReadOnly(value.MethodChain.ToArray()) } : null
    };
}

public enum ArchiveWriteStatus { Succeeded = 0, Failed = 2, Unsupported = 7, Cancelled = 255, HandlerUnavailable = -1 }
public sealed record ArchiveWriteResult(ArchiveWriteStatus Status, string Message, TimeSpan Duration)
{
    public bool IsSuccess => Status == ArchiveWriteStatus.Succeeded;
    public bool WasCancelled => Status == ArchiveWriteStatus.Cancelled;
    public int? NativeFailureHResult { get; init; }
    public string? CleanupDiagnostic { get; init; }
    public string? AuxiliaryDiagnostic { get; init; }
    public ArchiveFailure? Failure { get; init; }
    public ArchiveFailure? Error => Failure ?? (Status switch
    {
        ArchiveWriteStatus.Succeeded => null,
        ArchiveWriteStatus.Cancelled => new(ArchiveFailureCode.OperationCancelled, Message),
        ArchiveWriteStatus.Unsupported or ArchiveWriteStatus.HandlerUnavailable => new(ArchiveFailureCode.UnsupportedOperation, Message),
        _ => new(ArchiveFailureCode.EngineFailure, Message)
    });
    public override string ToString() => $"ArchiveWriteResult {{ Status = {Status}, Duration = {Duration} }}";
}
