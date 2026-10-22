using System;

namespace SevenZip.CSharp;

[Flags]
public enum ArchiveItemAttributes
{
    None = 0,
    ReadOnly = 1 << 0,
    Hidden = 1 << 1,
    System = 1 << 2,
    Directory = 1 << 3,
    Archive = 1 << 4,
    Encrypted = 1 << 5,
    Compressed = 1 << 6,
    SymbolicLink = 1 << 7,
    HardLink = 1 << 8,
    Sparse = 1 << 9,
    AntiItem = 1 << 10,
    AlternateDataStream = 1 << 11,
    ReparsePoint = 1 << 12,
}
