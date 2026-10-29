namespace SevenZip.CSharp;

public enum ArchiveUpdateMode { Add, Update }
public sealed record ArchiveUpdateOptions(bool Overwrite = false, ArchiveUpdateMode WriteMode = ArchiveUpdateMode.Add);
