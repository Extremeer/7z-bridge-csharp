using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SevenZip.CSharp;

/// <summary>A cheap identity/version check; never decodes or hashes archive payload.</summary>
public sealed record ArchiveFileIdentity(uint Volume, ulong FileId, long Length, long Modified, long Created)
{
    public static ArchiveFileIdentity? CaptureIfExists(string path) => File.Exists(path) ? Capture(path) : null;

    public static ArchiveFileIdentity Capture(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandle(file.SafeFileHandle, out var info))
                throw new IOException("无法读取归档文件标识", Marshal.GetHRForLastWin32Error());
            return new(info.Volume, Join(info.IdHigh, info.IdLow), (long)Join(info.SizeHigh, info.SizeLow),
                (long)Join(info.ModifiedHigh, info.ModifiedLow), (long)Join(info.CreatedHigh, info.CreatedLow));
        }
        var metadata = new FileInfo(path);
        return new(0, 0, file.Length, metadata.LastWriteTimeUtc.Ticks, metadata.CreationTimeUtc.Ticks);
    }

    private static ulong Join(uint high, uint low) => ((ulong)high << 32) | low;
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreatedLow, CreatedHigh, AccessedLow, AccessedHigh, ModifiedLow, ModifiedHigh,
            Volume, SizeHigh, SizeLow, Links, IdHigh, IdLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
}
