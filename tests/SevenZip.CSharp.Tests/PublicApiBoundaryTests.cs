using System.Reflection;
using SevenZip.CSharp;

namespace SevenZip.CSharp.Tests;

public sealed class PublicApiBoundaryTests
{
    [Fact]
    public void FriendAccessIsLimitedToLibraryAndConsumerTestAssemblies()
    {
        var friends = typeof(ArchiveInspector).Assembly
            .GetCustomAttributes<System.Runtime.CompilerServices.InternalsVisibleToAttribute>()
            .Select(attribute => attribute.AssemblyName).OrderBy(name => name).ToArray();
        Assert.Equal(new[] { "ArchiveEngine.IntegrationTests", "SevenZip.CSharp.Tests" }, friends);
    }

    [Fact]
    public void NativeObjectsAndPointerOperationsDoNotEscapeTheConsumerApi()
    {
        var assembly = typeof(ArchiveInspector).Assembly;
        var exported = assembly.GetExportedTypes();
        Assert.DoesNotContain(typeof(NativeReadSession), exported);
        Assert.DoesNotContain(typeof(SevenZipNativeVariant), exported);
        Assert.DoesNotContain(typeof(NativeInputStream), exported);
        Assert.DoesNotContain(typeof(NativeOutputStream), exported);
        Assert.DoesNotContain(typeof(SevenZipNativeHasher), exported);
        Assert.DoesNotContain(typeof(ArchiveExtractor.Callback), exported);
        Assert.DoesNotContain(typeof(ArchiveWriter.UpdateCallback), exported);
        Assert.DoesNotContain(typeof(NativeArchiveInterop.NativeInArchive), exported);
        Assert.DoesNotContain(typeof(NativeArchiveInterop.NativeOutArchive), exported);
        Assert.DoesNotContain(typeof(NativeArchiveInterop.NativeSetProperties), exported);
        Assert.Null(typeof(SevenZipNativeLibrary).GetMethod("GetExport"));
        Assert.Null(typeof(ArchiveWriter).GetMethod("SetProperties"));
        Assert.Null(typeof(ArchiveExtractor).GetMethod("RunToStreamsWithPasswordsAsync"));

        foreach (var type in new[] { typeof(ArchiveInspector), typeof(ArchiveExtractor), typeof(ArchiveWriter),
                     typeof(ArchiveUpdater), typeof(ArchiveHasher) })
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            Assert.NotEqual(typeof(nint), method.ReturnType);
            Assert.NotEqual(typeof(object), method.ReturnType);
            Assert.DoesNotContain(method.GetParameters(), parameter => parameter.ParameterType == typeof(nint)
                || parameter.ParameterType.DeclaringType == typeof(NativeArchiveInterop));
        }
    }
}
