using System.Runtime.InteropServices;
using static SevenZip.CSharp.NativeArchiveInterop;

namespace SevenZip.CSharp;

/// <summary>Thread-affine SDK archive, source stream and property ownership.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class NativeReadSession : IDisposable
{
    public static bool IsSupportedPlatform => OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.X86;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private readonly CancellationToken token;
    private readonly long sourceLength;
    private NativeInputStream? stream;
    private NativeInArchive? archive;
    private object? rcw;
    private nint unknown;
    private bool disposed;
    private OpenCallback? callback;
    private readonly IReadOnlyList<string> primaryPaths;
    public IReadOnlyList<string> SourceFiles => primaryPaths.Concat(callback?.OpenedPaths ?? [])
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public string Format { get; }
    public bool IsMultiVolume { get; private set; }
    public string? MissingVolumePath => callback?.MissingVolumePath;
    public bool HeaderPasswordRequested => callback?.PasswordRequested == true;
    private readonly bool handlerCanUpdate;
    public bool CanUpdate
    {
        get { CheckAccess(); return handlerCanUpdate && !IsMultiVolume && rcw is NativeOutArchive && GetArchiveProperty(93) is not true; }
    }

    private NativeReadSession(string path, SevenZipNativeLibrary library, SevenZipNativeFormat format, CancellationToken token, Action<int>? bytesRead, ArchiveOperationControl? control)
    {
        this.token = token; Format = format.Name.ToLowerInvariant(); handlerCanUpdate = format.CanUpdate;
        IsMultiVolume = ArchiveVolumeInput.IsSplit(path);
        primaryPaths = IsMultiVolume && ArchiveVolumeSet.TryAnalyze(path, out var volumes)
            ? volumes.VolumePaths : [Path.GetFullPath(path)];
        var input = ArchiveVolumeInput.Open(path, token);
        sourceLength = input.Length;
        stream = new NativeInputStream(input, token, control: control, bytesRead: bytesRead);
        try
        {
            var create = Marshal.GetDelegateForFunctionPointer<CreateObject>(library.GetExport("CreateObject"));
            var classId = format.ClassId; var iid = typeof(NativeInArchive).GUID;
            Marshal.ThrowExceptionForHR(create(ref classId, ref iid, out unknown));
            rcw = Marshal.GetObjectForIUnknown(unknown); archive = (NativeInArchive)rcw;
        }
        catch { Dispose(); throw; }
    }

    public static NativeReadSession? TryOpen(string path, SevenZipNativeLibrary library,
        SevenZipNativeFormat format, CancellationToken token = default, ArchivePassword? password = null, Action<int>? bytesRead = null, ArchiveOperationControl? control = null)
    {
        if (!IsSupportedPlatform) throw new PlatformNotSupportedException("Native read sessions require Windows x86 or x64.");
        token.ThrowIfCancellationRequested();
        var rarProbe = CreateRar5VolumeProbe(library, password, token, control);
        if (format.Name.Equals("rar", StringComparison.OrdinalIgnoreCase) || format.Name.Equals("rar5", StringComparison.OrdinalIgnoreCase)) path = RarVolumePolicy.FirstPath(path, rarProbe);
        if (format.Name.Equals("zip", StringComparison.OrdinalIgnoreCase)) path = ZipVolumePolicy.DirectoryPath(path);
        var session = new NativeReadSession(path, library, format, token, bytesRead, control);
        nint maxStart = 0, callbackPointer = 0;
        try
        {
            maxStart = Marshal.AllocCoTaskMem(sizeof(long));
            var zipVolumes = format.Name.Equals("zip", StringComparison.OrdinalIgnoreCase) && Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase);
            session.callback = new OpenCallback(password, token, format.Name.StartsWith("rar", StringComparison.OrdinalIgnoreCase) || zipVolumes ? path : null, bytesRead, zipVolumes, rarProbe, control);
            callbackPointer = Marshal.GetComInterfaceForObject(session.callback, typeof(NativeOpenCallback));
            var prefixSearch = format.CanFindSignature;
            // Finite EOF bound, not an arbitrary prefix cutoff or an unbounded SDK search.
            // NativeInputStream.Read checks cancellation/pause on every native read.
            Marshal.WriteInt64(maxStart, prefixSearch ? session.sourceLength : 0);
            var result = session.archive!.Open(session.stream!, maxStart, callbackPointer);
            token.ThrowIfCancellationRequested();
            session.IsMultiVolume |= RarVolumePolicy.IsNumbered(path) || session.callback.OpenedVolumeCount > 0;
            if (result == 0)
                session.IsMultiVolume |= session.GetArchiveProperty(35) is true || Convert.ToUInt64(session.GetArchiveProperty(39) ?? 0) > 1;
            if (session.callback.RejectedVolume)
                throw new ArchiveReadException(ArchiveReadError.UnsupportedOperation, "分卷路径超出所选归档组或包含重解析点，已拒绝读取");
            // Old RAR probes beyond the final disk. Only the handler knows whether
            // that disk was required; do not treat a successful optional probe as damage.
            if (session.callback.MissingVolumePath is { } missing &&
                (result != 0 || HasUnfinishedSplitItem(session) || session.GetArchiveProperty(55) is string error && !string.IsNullOrWhiteSpace(error)
                    || Convert.ToUInt64(session.GetArchiveProperty(71) ?? 0) != 0 || Convert.ToUInt64(session.GetArchiveProperty(72) ?? 0) != 0))
                throw new ArchiveReadException(ArchiveReadError.ArchiveDamaged, "缺少分卷：" + Path.GetFileName(missing), isVolumeFailure: true);
            if (result == 0)
            {
                // Validate SDK-reported offsets against the owned source handle, including buffered scans.
                if (prefixSearch)
                {
                    var offset = Convert.ToUInt64(session.GetArchiveProperty(36) ?? 0);
                    var prefix = Convert.ToUInt64(session.GetArchiveProperty(88) ?? 0);
                    if (offset > (ulong)session.sourceLength || prefix > (ulong)session.sourceLength - offset)
                    { session.Dispose(); return null; }
                }
                return session;
            }
            if (session.callback.PasswordRequested)
            {
                if (password is not null && session.Format == "rar5"
                    && Rar5PasswordCheck.Verify(library, path, password, token, control) == true)
                    throw new ArchiveReadException(ArchiveReadError.ArchiveDamaged, "密码检查通过，但加密归档头已损坏");
                throw new ArchiveReadException(password is null ? ArchiveReadError.RequiresPassword : ArchiveReadError.IncorrectPassword,
                    password is null ? "压缩包需要密码" : "密码不正确或加密归档头已损坏");
            }
            session.Dispose(); return null;
        }
        catch { session.Dispose(); throw; }
        finally { if (callbackPointer != 0) Marshal.Release(callbackPointer); if (maxStart != 0) Marshal.FreeCoTaskMem(maxStart); }
    }

    public static Func<string, ulong?> CreateRar5VolumeProbe(SevenZipNativeLibrary library, ArchivePassword? password, CancellationToken token, ArchiveOperationControl? control = null)
    {
        var cache = new Dictionary<string, ulong?>(StringComparer.OrdinalIgnoreCase);
        return path =>
        {
            control?.CheckStop(token); token.ThrowIfCancellationRequested();
            if (!File.Exists(path)) return null;
            RarVolumePolicy.RejectReparseVolume(path);
            if (!cache.TryGetValue(path, out var index)) cache[path] = index = ProbeRar5VolumeIndex(library, path, password, token, control);
            return index;
        };
    }

    // Raw, single-volume SDK Open: no normalization and no volume source in the
    // callback, hence no neighboring file can be opened and no recursion occurs.
    private static ulong? ProbeRar5VolumeIndex(SevenZipNativeLibrary library, string path, ArchivePassword? password, CancellationToken token, ArchiveOperationControl? control)
    {
        if (!IsSupportedPlatform) throw new PlatformNotSupportedException("Native volume probes require Windows x86 or x64.");
        var format = library.Formats.FirstOrDefault(item => item.Name.Equals("rar5", StringComparison.OrdinalIgnoreCase));
        if (format is null) return null;
        using var session = new NativeReadSession(path, library, format, token, null, control);
        session.callback = new OpenCallback(password, token, control: control);
        nint pointer = 0, maximum = 0;
        try
        {
            pointer = Marshal.GetComInterfaceForObject(session.callback, typeof(NativeOpenCallback));
            maximum = Marshal.AllocCoTaskMem(sizeof(long));
            Marshal.WriteInt64(maximum, 0);
            var result = session.archive!.Open(session.stream!, maximum, pointer);
            token.ThrowIfCancellationRequested();
            if (result != 0)
            {
                if (session.callback.PasswordRequested)
                {
                    if (password is not null && Rar5PasswordCheck.Verify(library, path, password, token, control) == true)
                        throw new ArchiveReadException(ArchiveReadError.ArchiveDamaged, "密码检查通过，但加密归档头已损坏");
                    throw new ArchiveReadException(password is null ? ArchiveReadError.RequiresPassword : ArchiveReadError.IncorrectPassword,
                        password is null ? "压缩包需要密码" : "密码不正确或加密归档头已损坏");
                }
                return null;
            }
            if (session.GetArchiveProperty(35) is not true) return null;
            // A deliberately isolated middle disk has incomplete split-file
            // continuity, which RAR5 reports as HeadersError. Only naming metadata
            // is used here; the full multi-volume Open/Map still rejects all flags.
            if ((Convert.ToUInt64(session.GetArchiveProperty(71) ?? 0) & ~2UL) != 0 || Convert.ToUInt64(session.GetArchiveProperty(72) ?? 0) != 0)
                throw new ArchiveReadException(ArchiveReadError.ArchiveDamaged, "分卷头包含结构错误或校验警告");
            return session.GetArchiveProperty(80) is { } value ? Convert.ToUInt64(value) : null;
        }
        finally { if (maximum != 0) Marshal.FreeCoTaskMem(maximum); if (pointer != 0) Marshal.Release(pointer); }
    }

    private static bool HasUnfinishedSplitItem(NativeReadSession session)
    {
        if (session.Format is not ("rar" or "rar5")) return false;
        for (uint index = 0; index < session.ItemCount; index++)
            if (session.GetProperty(index, 17) is true) return true;
        return false;
    }

    public uint ItemCount
    {
        get { CheckAccess(); Marshal.ThrowExceptionForHR(archive!.GetNumberOfItems(out var count)); return count; }
    }

    public object? GetProperty(uint index, uint id)
    {
        CheckAccess(); Marshal.ThrowExceptionForHR(archive!.GetProperty(index, id, out var value));
        return ConvertAndClear(ref value);
    }

    public object? GetArchiveProperty(uint id)
    {
        CheckAccess(); Marshal.ThrowExceptionForHR(archive!.GetArchiveProperty(id, out var value));
        return ConvertAndClear(ref value);
    }

    public int Extract(nint indices, uint count, bool testMode, nint callbackPointer)
    {
        CheckAccess();
        return archive!.Extract(indices, count, testMode ? 1 : 0, callbackPointer);
    }

    private void CheckAccess()
    {
        if (Environment.CurrentManagedThreadId != ownerThread) throw new InvalidOperationException("Native session cannot cross threads.");
        ObjectDisposedException.ThrowIf(disposed, this);
        token.ThrowIfCancellationRequested();
    }

    public int UpdateItems(SequentialOutput output, uint count, NativeUpdateCallback update)
    {
        CheckAccess(); return ((NativeOutArchive)rcw!).UpdateItems(output, count, update);
    }

    public void ConfigureUpdate(ArchiveWriteOptions options)
    { CheckAccess(); ArchiveWriter.SetProperties((NativeSetProperties)rcw!, options); }

    public void Dispose()
    {
        if (Environment.CurrentManagedThreadId != ownerThread) throw new InvalidOperationException("Native session must be disposed by its owner thread.");
        if (disposed) return;
        disposed = true;
        try { archive?.Close(); }
        finally
        {
            try { if (rcw is not null && Marshal.IsComObject(rcw)) Marshal.FinalReleaseComObject(rcw); }
            finally
            {
                if (unknown != 0) Marshal.Release(unknown);
                stream?.Dispose(); stream = null; archive = null; rcw = null; unknown = 0;
                callback?.Dispose(); callback = null;
            }
        }
    }

    private static object? ConvertAndClear(ref Variant value)
    {
        try
        {
            return value.Type switch
            {
                0 => null, 8 => Marshal.PtrToStringBSTR(value.Pointer),
                11 => (value.Value & 0xffff) != 0, 19 => (uint)value.Value, 21 => value.Value,
                64 => new DateTimeOffset(DateTime.FromFileTimeUtc(checked((long)value.Value))),
                _ => throw new InvalidDataException($"Unsupported native property type {value.Type}.")
            };
        }
        finally { Marshal.ThrowExceptionForHR(PropVariantClear(ref value)); }
    }

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref Variant value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateObject(ref Guid classId, ref Guid interfaceId, out nint value);

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class OpenCallback(ArchivePassword? password, CancellationToken token, string? volumeSource = null, Action<int>? bytesRead = null, bool zipVolumes = false, Func<string, ulong?>? rarVolumeProbe = null, ArchiveOperationControl? control = null) : NativeOpenCallback, NativeReadPassword, NativeOpenVolume, IDisposable
    {
        private const int Abort = unchecked((int)0x80004004);
        public bool PasswordRequested { get; private set; }
        public string? MissingVolumePath { get; private set; }
        public int OpenedVolumeCount { get; private set; }
        public bool RejectedVolume { get; private set; }
        private readonly List<NativeInputStream> volumes = [];
        public List<string> OpenedPaths { get; } = [];
        private int CheckStop()
        {
            try { control?.CheckStop(token); token.ThrowIfCancellationRequested(); return 0; }
            catch (OperationCanceledException) { return Abort; }
        }
        public int GetProperty(uint property, out Variant value)
        {
            value = default;
            var stop = CheckStop(); if (stop != 0) return stop;
            try
            {
                token.ThrowIfCancellationRequested();
                if (volumeSource is null) return 0;
                value = property switch { 3 or 4 => Variant.Text(Path.GetFileName(volumeSource)), 6 => Variant.Boolean(false),
                    7 => Variant.Size((ulong)new FileInfo(volumeSource).Length), 9 => Variant.Number((uint)File.GetAttributes(volumeSource)), _ => default };
                return 0;
            }
            catch (Exception exception) { return Marshal.GetHRForException(exception); }
        }
        public int GetStream(string name, out SeekableInput? stream)
        {
            stream = null;
            var stop = CheckStop(); if (stop != 0) return stop;
            try
            {
                token.ThrowIfCancellationRequested();
                if (volumeSource is null) return 1;
                // ZIP asks for a same-name SFX when z01 is absent. Decline this
                // optional fallback without opening it or obscuring the missing z01.
                if (zipVolumes && name.Equals(Path.GetFileNameWithoutExtension(volumeSource) + ".exe", StringComparison.OrdinalIgnoreCase)) return 1;
                var path = zipVolumes ? ZipVolumePolicy.Resolve(volumeSource, name) : RarVolumePolicy.Resolve(volumeSource, name, rarVolumeProbe);
                if (path is null) { RejectedVolume = true; return unchecked((int)0x80070005); }
                if (!File.Exists(path)) { MissingVolumePath = path; return 1; }
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { RejectedVolume = true; return unchecked((int)0x80070005); }
                var input = new NativeInputStream(File.OpenRead(path), token, control: control, bytesRead: bytesRead); volumes.Add(input); OpenedPaths.Add(path); stream = input; OpenedVolumeCount++; return 0;
            }
            catch (Exception exception) { return Marshal.GetHRForException(exception); }
        }
        public void Dispose() { foreach (var volume in volumes) volume.Dispose(); volumes.Clear(); }
        public int SetTotal(nint files, nint bytes) => CheckStop();
        public int SetCompleted(nint files, nint bytes) => CheckStop();
        public unsafe int CryptoGetTextPassword(out nint value)
        {
            value = 0;
            var stop = CheckStop(); if (stop != 0) return stop;
            PasswordRequested = true;
            if (password is null) return Abort;
            try
            {
                value = password.Use(chars =>
                {
                    fixed (char* pointer = chars) return SysAllocStringLen((nint)pointer, checked((uint)chars.Length));
                });
                return value == 0 ? unchecked((int)0x8007000e) : 0;
            }
            catch (Exception exception) { return Marshal.GetHRForException(exception); }
        }
        [DllImport("oleaut32.dll")] private static extern nint SysAllocStringLen(nint value, uint length);
    }
}
