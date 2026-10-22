using System.Runtime.InteropServices;

namespace SevenZip.CSharp;

public enum SevenZipNativeDependencyFailure { Missing, InvalidImage, ArchitectureMismatch, IncompatibleSdk, LoadFailed }

/// <summary>SDK deployment errors are distinct from invalid archives. Messages never copy OS exception text.</summary>
public sealed class SevenZipNativeDependencyException : Exception
{
    public SevenZipNativeDependencyFailure Failure { get; }
    public SevenZipNativeDependencyException(SevenZipNativeDependencyFailure failure, Exception? inner = null)
        : base(failure switch
        {
            SevenZipNativeDependencyFailure.Missing => "The native archive engine DLL is missing.",
            SevenZipNativeDependencyFailure.InvalidImage => "The native archive engine DLL is invalid or damaged.",
            SevenZipNativeDependencyFailure.ArchitectureMismatch => "The native archive engine DLL does not match the process architecture.",
            SevenZipNativeDependencyFailure.IncompatibleSdk => "The native archive engine DLL does not provide the required SDK interfaces.",
            _ => "The native archive engine DLL could not be loaded."
        }, inner) => Failure = failure;

    public static SevenZipNativeDependencyException FromDiscoveryFailure(Exception exception)
    {
        if (exception is SevenZipNativeDependencyException native) return native;
        var failure = exception switch
        {
            FileNotFoundException or DirectoryNotFoundException => SevenZipNativeDependencyFailure.Missing,
            EntryPointNotFoundException or InvalidDataException or COMException => SevenZipNativeDependencyFailure.IncompatibleSdk,
            BadImageFormatException => SevenZipNativeDependencyFailure.InvalidImage,
            _ => SevenZipNativeDependencyFailure.LoadFailed
        };
        if (exception is BadImageFormatException { FileName: { } path } && File.Exists(path))
        {
            try
            {
                if (SevenZipNativeSettings.ReadArchitecture(path) != RuntimeInformation.ProcessArchitecture)
                    failure = SevenZipNativeDependencyFailure.ArchitectureMismatch;
            }
            catch (BadImageFormatException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return new(failure, exception);
    }
}
