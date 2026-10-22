using System.Diagnostics;
using System.Runtime.InteropServices;
using static SevenZip.CSharp.NativeArchiveInterop;

namespace SevenZip.CSharp;

/// <summary>
/// Uses the bundled 7-Zip SDK's IOutArchive callbacks and actual input/output
/// streams. Byte counters never originate in an integer console percentage.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class ArchiveWriter
{
    private const int Abort = unchecked((int)0x80004004);

    public static bool IsSupportedPlatform => OperatingSystem.IsWindows()
        && RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.X86;

    public static bool CanCreate(ArchiveWriteOptions options) => IsSupportedPlatform
        && options.Format is ArchiveWriteFormat.Zip or ArchiveWriteFormat.SevenZip
            or ArchiveWriteFormat.Tar or ArchiveWriteFormat.Wim or ArchiveWriteFormat.GZip or ArchiveWriteFormat.BZip2 or ArchiveWriteFormat.Xz
        && (options.Format is ArchiveWriteFormat.Zip or ArchiveWriteFormat.SevenZip || options.Encryption is null)
        && (options.Encryption is null || (options.Format == ArchiveWriteFormat.Zip
            ? !options.Encryption.EncryptFileNames && options.Encryption.Method is null or ArchiveEncryptionAlgorithm.Default
                or ArchiveEncryptionAlgorithm.ZipCrypto or ArchiveEncryptionAlgorithm.Aes128 or ArchiveEncryptionAlgorithm.Aes192 or ArchiveEncryptionAlgorithm.Aes256
            : options.Encryption.Method is null or ArchiveEncryptionAlgorithm.Default or ArchiveEncryptionAlgorithm.Aes256))
        && (options.Format == ArchiveWriteFormat.SevenZip || options.Compression?.SolidMode is not ArchiveSolidMode.On
            && options.Compression?.SolidBlockSize is null)
        && (options.Compression?.SolidBlockSize is null || options.Compression.SolidMode != ArchiveSolidMode.Off)
        && (options.Format != ArchiveWriteFormat.Tar || options.Compression is null)
        && (options.Split is null || options.Format is ArchiveWriteFormat.Zip or ArchiveWriteFormat.SevenZip)
        && (options.Split is null || options.Split.VolumeSize > 0)
        && (options.Sfx is not { Enable: true } || options.Format == ArchiveWriteFormat.SevenZip && options.Split is null)
        && (options.Compression?.MethodChain.Count is not > 0 || options.Format == ArchiveWriteFormat.SevenZip);

    public static Task<ArchiveWriteResult> CreateAsync(string libraryPath, string root, ArchiveWriteOptions options,
        ArchiveOperationContext? context = null, IProgress<EngineProgress>? progress = null)
    {
        context ??= new();
        return WriteAsync(libraryPath, root, options.Snapshot(), progress, context.CancellationToken, context.Password, control: context.Control);
    }

    public static Task<ArchiveWriteResult> CreateAsync(string libraryPath, IReadOnlyList<ArchiveSourceItem> sources, ArchiveWriteOptions options,
        ArchiveOperationContext? context = null, IProgress<EngineProgress>? progress = null)
    {
        context ??= new();
        return WriteAsync(libraryPath, string.Empty, options.Snapshot(), progress, context.CancellationToken,
            context.Password, sources.ToArray(), context.Control);
    }

    private static Task<ArchiveWriteResult> WriteAsync(string libraryPath, string root, ArchiveWriteOptions options,
        IProgress<EngineProgress>? progress, CancellationToken token, ArchivePassword? borrowedPassword,
        IReadOnlyList<ArchiveSourceItem>? sources = null, ArchiveOperationControl? control = null) => Task.Run(() =>
    {
        var elapsed = Stopwatch.StartNew();
        var password = borrowedPassword;
        using var nativeControlScope = control?.EnterNativeOperation();
        var relay = new EngineProgressRelay(progress);
        progress = relay;
        object? archive = null;
        nint unknown = 0;
        var outputPath = options.ArchivePath + ".native-" + Guid.NewGuid().ToString("N");
        // Keep canonical suffixes for split-volume names even when the caller
        // chose an arbitrary final filename such as .archive.
        if (options.Split is not null) outputPath += options.Format == ArchiveWriteFormat.Zip ? ".zip" : ".7z";
        var ownsOutput = false;
        string? ownedSfxModule = null;
        SplitVolumeOutputStream? splitOutput = null;
        ArchiveWriteResult CreateCore()
        {
        try
        {
            using var writer = NativeArchiveWriteLock.AcquireAsync(options.ArchivePath, token).GetAwaiter().GetResult();
            token.ThrowIfCancellationRequested();
            if (options.Split is not null) EnsureSplitDestinationAvailable(options.ArchivePath);
            if (!CanCreate(options))
                return new ArchiveWriteResult(ArchiveWriteStatus.Unsupported, "Native writer does not support these creation settings", elapsed.Elapsed);
            var sfxModule = options.Sfx is { Enable: true } ? options.Sfx.ModulePath ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(libraryPath))!, "7z.sfx") : null;
            if (sfxModule is not null && (!File.Exists(sfxModule) || (File.GetAttributes(sfxModule) & FileAttributes.ReparsePoint) != 0
                || new FileInfo(sfxModule).Length >= EmbeddedArchivePolicy.MaximumScan || !EmbeddedArchivePolicy.IsPortableExecutable(sfxModule, token)))
                return new ArchiveWriteResult(ArchiveWriteStatus.Unsupported, "SFX module is missing or is not a supported PE module", elapsed.Elapsed);
            if (sfxModule is not null && options.Sfx is { } sfxResources && (sfxResources.IconPath is not null || sfxResources.VersionResourcePath is not null))
            {
                var preparedModule = outputPath + ".sfx-module";
                SfxIconResources.PrepareResources(sfxModule, sfxResources.IconPath, sfxResources.VersionResourcePath,
                    preparedModule, () => ownedSfxModule = preparedModule, token);
                sfxModule = preparedModule;
            }
            // This SDK writer does not support non-ASCII ZIP creation passwords.
            // Reject before output creation instead of depending on callback/COM
            // error propagation to classify the same unsupported parameter.
            if (options.Encryption is not null && password is null)
                return new ArchiveWriteResult(ArchiveWriteStatus.Unsupported, "An encryption password is required", elapsed.Elapsed);
            if (password is not null) password.Use(chars => chars.Length); // Reject a disposed borrowed handle before output creation.
            if (options.Format == ArchiveWriteFormat.Zip && password is not null && password.Use(chars =>
                { foreach (var character in chars) if (character > 0x7f) return true; return false; }))
                return new ArchiveWriteResult(ArchiveWriteStatus.Unsupported, "Native ZIP creation passwords must be ASCII", elapsed.Elapsed);
            var library = SevenZipNativeLibrary.Load(libraryPath);
            if (options.Format == ArchiveWriteFormat.SevenZip && options.Compression is { } codecSettings)
            {
                IEnumerable<string> methods = codecSettings.MethodChain.Count > 0 ? codecSettings.MethodChain
                    : string.IsNullOrWhiteSpace(codecSettings.Method) ? [] : [codecSettings.Method];
                foreach (var method in methods)
                {
                    var name = method.Split(':', 2)[0];
                    var numeric = ulong.TryParse(name, System.Globalization.NumberStyles.AllowHexSpecifier,
                        System.Globalization.CultureInfo.InvariantCulture, out var id);
                    if (!library.Codecs.Any(codec => codec.CanEncode &&
                        (codec.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || numeric && codec.Id == id)))
                        return new ArchiveWriteResult(ArchiveWriteStatus.Unsupported, "Native compression method is unavailable", elapsed.Elapsed);
                }
            }
            var create = Marshal.GetDelegateForFunctionPointer<CreateObject>(library.GetExport("CreateObject"));
            var format = library.FindFormat(HandlerName(options.Format));
            if (format is not { CanUpdate: true }) return new ArchiveWriteResult(ArchiveWriteStatus.HandlerUnavailable, "Native write handler is unavailable", elapsed.Elapsed);
            var classId = format.ClassId;
            var iid = typeof(NativeOutArchive).GUID;
            Marshal.ThrowExceptionForHR(create(ref classId, ref iid, out unknown));
            archive = Marshal.GetObjectForIUnknown(unknown);
            SetProperties((NativeSetProperties)archive, options, library);
            using var callback = new UpdateCallback(root, password, progress, token, sources, control, relay);
            if (options.Format is ArchiveWriteFormat.GZip or ArchiveWriteFormat.BZip2 or ArchiveWriteFormat.Xz
                && (callback.Items.Length != 1 || !File.Exists(callback.Items[0])))
                return new ArchiveWriteResult(ArchiveWriteStatus.Unsupported, "Single-stream formats require exactly one file and no directory entries", elapsed.Elapsed);
            using Stream file = options.Split?.VolumeSize is { } size
                ? splitOutput = new SplitVolumeOutputStream(outputPath, size)
                // ZIP can truncate/overwrite this private output on a Store retry.
                // Readers must not map unfinished bytes and prevent SetLength (0x800704C8).
                : new FileStream(outputPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            ownsOutput = options.Split is null;
            long prefix = 0;
            if (sfxModule is not null)
            {
                using var stub = File.OpenRead(sfxModule); var buffer = new byte[64 * 1024]; int count;
                while ((count = stub.Read(buffer)) > 0) { control?.CheckStop(token); token.ThrowIfCancellationRequested(); file.Write(buffer, 0, count); callback.OutputWritten(file.Length); }
                prefix = file.Length;
            }
            callback.OutputPrefixBytes = prefix;
            var stream = new NativeOutputStream(prefix == 0 ? file : new PayloadOutputStream(file, prefix), token,
                length => callback.OutputWritten(checked(prefix + length)), control);
            var hr = ((NativeOutArchive)archive).UpdateItems(stream, (uint)callback.Items.Length, callback);
            token.ThrowIfCancellationRequested();
            if (callback.OperationFailed) return new ArchiveWriteResult(ArchiveWriteStatus.Failed, "Native creation reported a failed item", elapsed.Elapsed);
            Marshal.ThrowExceptionForHR(hr);
            var finalLength = file.Length;
            file.Dispose();
            // Validate publication identity/version, not the archive payload. A
            // phase observer or another process must not replace/truncate a volume.
            var volumeVersions = splitOutput?.CreatedPaths.ToDictionary(path => path, ArchiveFileIdentity.Capture,
                StringComparer.OrdinalIgnoreCase);
            callback.Finalizing(finalLength);
            token.ThrowIfCancellationRequested();
            if (options.Split is null)
            {
                // Successful UpdateItems + successful output close
                // proceeds to publication, without an implicit full-archive test.
                if (sfxModule is not null && !EmbeddedArchivePolicy.PrefixEquals(sfxModule, outputPath, prefix, token, control))
                    return new ArchiveWriteResult(ArchiveWriteStatus.Failed, "Native SFX prefix validation failed", elapsed.Elapsed);
                callback.SetStage(EngineStage.Committing);
                control?.CheckStop(token);
                token.ThrowIfCancellationRequested(); File.Move(outputPath, options.ArchivePath, false); ownsOutput = false;
            }
            else
            {
                var volumes = splitOutput!.CreatedPaths;
                if (volumes.Count == 0) return new ArchiveWriteResult(ArchiveWriteStatus.Failed, "Native output contains no volumes", elapsed.Elapsed);
                callback.SetStage(EngineStage.Committing);
                control?.CheckStop(token);
                foreach (var volume in volumes)
                    if (volumeVersions is null || !volumeVersions.TryGetValue(volume, out var expected)
                        || !File.Exists(volume) || (File.GetAttributes(volume) & FileAttributes.ReparsePoint) != 0
                        || expected != ArchiveFileIdentity.Capture(volume))
                        throw new IOException("Native output volume changed before publication.");
                token.ThrowIfCancellationRequested(); PublishSplitVolumes(volumes, options.ArchivePath, token);
            }
            callback.Complete(finalLength);
            GC.KeepAlive(stream);
            return new ArchiveWriteResult(ArchiveWriteStatus.Succeeded, "", elapsed.Elapsed);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return new ArchiveWriteResult(ArchiveWriteStatus.Cancelled, "", elapsed.Elapsed); }
        catch (ArgumentException)
        { return new ArchiveWriteResult(ArchiveWriteStatus.Unsupported, "Native archive parameters or password are not supported", elapsed.Elapsed); }
        catch (ObjectDisposedException)
        { return new ArchiveWriteResult(ArchiveWriteStatus.Unsupported, "The archive password is no longer available", elapsed.Elapsed); }
        catch (NotSupportedException exception)
        { return new ArchiveWriteResult(ArchiveWriteStatus.Unsupported, exception.Message, elapsed.Elapsed); }
        catch (Exception exception) when (exception is COMException or IOException or UnauthorizedAccessException)
        { return new ArchiveWriteResult(ArchiveWriteStatus.Failed, $"Native 7-Zip: {exception.Message}", elapsed.Elapsed)
            { NativeFailureHResult = exception.HResult, Failure = EngineFailureClassifier.TryMap(exception) }; }
        finally
        {
            if (archive is not null || unknown != 0 || ownsOutput || splitOutput is not null)
                relay.ReportStage(EngineStage.CleaningUp, options.ArchivePath);
            if (archive is not null && Marshal.IsComObject(archive)) Marshal.FinalReleaseComObject(archive);
            if (unknown != 0) Marshal.Release(unknown);
        }
        }

        string? CleanupOwnedOutputs()
        {
            var failures = new List<string>();
            var paths = ownsOutput ? new[] { outputPath } : Array.Empty<string>();
            foreach (var path in paths.Concat(splitOutput?.CreatedPaths ?? Array.Empty<string>())
                .Concat(ownedSfxModule is null ? Array.Empty<string>() : new[] { ownedSfxModule }))
            {
                try { File.Delete(path); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Never parse or forward arbitrary exception text. Keep trying later volumes.
                    if (failures.Count < 8) failures.Add($"Retained={path}; {EngineFailureClassifier.Diagnostic(exception)}");
                }
            }
            return failures.Count == 0 ? null : "Native creation cleanup failed; " + string.Join("; ", failures);
        }
        ArchiveWriteResult result;
        try { result = CreateCore(); }
        catch { CleanupOwnedOutputs(); throw; }
        var cleanup = CleanupOwnedOutputs();
        var diagnostic = relay.ObserverDiagnostic;
        if (cleanup is not null) diagnostic = EngineProgressRelay.CombineDiagnostics(diagnostic, "Native creation cleanup failed; RetainedTemporaryFile=True");
        return cleanup is null && diagnostic is null ? result : result with
            { CleanupDiagnostic = cleanup, AuxiliaryDiagnostic = diagnostic };

    }, token);

    // Multi-file publication cannot be filesystem-atomic. Roll back only paths
    // successfully created by this attempt; never remove the conflicting target.
    public static void PublishSplitVolumes(IReadOnlyList<string> volumes, string archivePath, CancellationToken token,
        Action<string, string>? move = null)
    {
        EnsureSplitDestinationAvailable(archivePath);
        var published = new List<string>();
        try
        {
            for (var index = 0; index < volumes.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var target = archivePath + "." + (index + 1).ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
                if (move is null) File.Move(volumes[index], target, false); else move(volumes[index], target);
                published.Add(target);
            }
        }
        catch
        {
            foreach (var target in published) if (File.Exists(target)) File.Delete(target);
            throw;
        }
    }

    private static void EnsureSplitDestinationAvailable(string archivePath)
    {
        var fullPath = Path.GetFullPath(archivePath);
        if (File.Exists(fullPath) || Directory.Exists(fullPath)) throw new IOException("Archive output already exists.");
        var parent = Path.GetDirectoryName(fullPath)!;
        if (!Directory.Exists(parent)) return;
        var prefix = Path.GetFileName(fullPath) + ".";
        foreach (var entry in Directory.EnumerateFileSystemEntries(parent))
        {
            var name = Path.GetFileName(entry);
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var suffix = name.AsSpan(prefix.Length);
            if (suffix.Length >= 3 && suffix.IndexOfAnyExceptInRange('0', '9') < 0)
                throw new IOException("Archive volume output already exists.");
        }
    }

    public static string HandlerName(ArchiveWriteFormat? format) => format switch
    {
        ArchiveWriteFormat.Zip => "zip", ArchiveWriteFormat.SevenZip => "7z", ArchiveWriteFormat.Tar => "tar",
        ArchiveWriteFormat.Wim => "wim", ArchiveWriteFormat.GZip => "gzip", ArchiveWriteFormat.BZip2 => "bzip2",
        ArchiveWriteFormat.Xz => "xz", _ => throw new ArgumentException("Unsupported native write format.")
    };

    internal static void SetProperties(NativeSetProperties archive, ArchiveWriteOptions options, SevenZipNativeLibrary? library = null)
    {
        if (options.Compression is { } settings && (settings.MethodChain.Any(string.IsNullOrWhiteSpace)
            || settings.Level is < 0 or > 9 || settings.ThreadCount is <= 0 || settings.FastBytes is <= 0))
            throw new ArgumentException("Invalid compression settings.");
        var properties = new List<(string Name, Variant Value)>();
        var sevenZip = options.Format == ArchiveWriteFormat.SevenZip;
        var codecPrefix = sevenZip ? "0" : "";
        var compression = options.Compression;
        if (sevenZip && compression?.MethodChain.Count > 0 &&
            (compression.DictionarySize is not null || compression.FastBytes is not null || compression.MatchFinder is not null))
        {
            if (library is null) throw new ArgumentException("Method-chain parameters require the native codec catalog.");
            var primary = -1;
            for (var index = 0; index < compression.MethodChain.Count; index++)
            {
                var name = compression.MethodChain[index].Split(':', 2)[0];
                var numeric = ulong.TryParse(name, System.Globalization.NumberStyles.AllowHexSpecifier,
                    System.Globalization.CultureInfo.InvariantCulture, out var id);
                if (library.Codecs.Any(codec => codec.CanEncode && !codec.IsFilter &&
                    (codec.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || numeric && codec.Id == id)))
                { primary = index; break; }
            }
            if (primary < 0) throw new ArgumentException("Compression parameters cannot be applied to a filter-only chain.");
            codecPrefix = primary.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        if (compression?.Level is { } level) properties.Add(("x", Variant.Number((uint)level)));
        if (compression?.MethodChain.Count > 0)
        {
            for (var index = 0; index < compression.MethodChain.Count; index++)
            {
                properties.Add((index.ToString(System.Globalization.CultureInfo.InvariantCulture), Variant.Text(compression.MethodChain[index])));
            }
        }
        else if (!string.IsNullOrWhiteSpace(compression?.Method)) properties.Add((sevenZip ? "0" : "m", Variant.Text(compression.Method)));
        if (compression?.DictionarySize is { } dictionary) properties.Add((codecPrefix + "d", Variant.Text(dictionary)));
        if (compression?.FastBytes is { } fastBytes) properties.Add((codecPrefix + "fb", Variant.Number((uint)fastBytes)));
        if (compression?.MatchFinder is { } finder) properties.Add((codecPrefix + "mf", Variant.Text(finder)));
        if (compression?.ThreadCount is { } threads) properties.Add(("mt", Variant.Number((uint)threads)));
        if (sevenZip && compression?.SolidBlockSize is { } block) properties.Add(("s", Variant.Text(block)));
        else if (sevenZip && compression?.SolidMode is ArchiveSolidMode.On or ArchiveSolidMode.Off)
            properties.Add(("s", Variant.Boolean(compression.SolidMode == ArchiveSolidMode.On)));
        if (options.Encryption is { } encryption)
        {
            if (sevenZip) properties.Add(("he", Variant.Boolean(encryption.EncryptFileNames)));
            else if (options.Format == ArchiveWriteFormat.Zip) properties.Add(("em", Variant.Text(encryption.Method switch
            {
                ArchiveEncryptionAlgorithm.ZipCrypto => "ZipCrypto",
                ArchiveEncryptionAlgorithm.Aes128 => "AES128",
                ArchiveEncryptionAlgorithm.Aes192 => "AES192",
                _ => "AES256"
            })));
        }
        var names = new nint[properties.Count];
        var values = Marshal.AllocCoTaskMem(properties.Count * Marshal.SizeOf<Variant>());
        try
        {
            for (var i = 0; i < properties.Count; i++)
            {
                names[i] = Marshal.StringToCoTaskMemUni(properties[i].Name);
                Marshal.StructureToPtr(properties[i].Value, values + i * Marshal.SizeOf<Variant>(), false);
            }
            Marshal.ThrowExceptionForHR(archive.SetProperties(names, values, (uint)properties.Count));
        }
        finally
        {
            foreach (var name in names) if (name != 0) Marshal.FreeCoTaskMem(name);
            foreach (var property in properties) if (property.Value.Type == 8) Marshal.FreeBSTR(property.Value.Pointer);
            Marshal.FreeCoTaskMem(values);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateObject(ref Guid classId, ref Guid interfaceId, out nint value);

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class UpdateCallback : NativeUpdateCallback, NativeUpdateFileCallback, RatioCallback, PasswordCallback, IDisposable
    {
        private readonly string root;
        private readonly IReadOnlyList<ArchiveSourceItem>? sources;
        private readonly ArchivePassword? password;
        private readonly IProgress<EngineProgress>? progress;
        private readonly CancellationToken token;
        private readonly List<NativeInputStream> streams = [];
        private readonly object gate = new();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly ArchiveOperationControl? control;
        private readonly EngineProgressRelay? reporter;
        private readonly TimeSpan activeStarted;
        private readonly ArchiveProgressMetrics metrics = new();
        private long total, output;
        private TimeSpan previousSample;
        private EngineStage stage = EngineStage.Compressing;
        private int files;
        private int totalFiles;
        private bool finished;
        private string? currentItem;
        public string[] Items { get; }
        public bool OperationFailed { get; private set; }
        public long OutputPrefixBytes { get; set; }

        public UpdateCallback(string root, ArchivePassword? password, IProgress<EngineProgress>? progress, CancellationToken token,
            IReadOnlyList<ArchiveSourceItem>? sources = null, ArchiveOperationControl? control = null, EngineProgressRelay? relay = null)
        {
            this.control = control; activeStarted = control?.ActiveElapsed ?? TimeSpan.Zero; reporter = relay ?? progress as EngineProgressRelay;
            this.root = root; this.password = password; this.progress = progress; this.token = token;
            this.sources = sources ?? ArchiveSourceEnumerator.EnumerateDirectory(root, token, progress);
            var names = new HashSet<ArchivePath>();
            foreach (var item in this.sources)
            {
                if (item.Path.IsRoot || !names.Add(item.Path)) throw new ArgumentException("Duplicate or root archive source path.");
                var failure = WindowsOutputNamePolicy.ValidateEntry(item.Path, item.SourcePath ?? item.Path.Value);
                if (failure is not null) throw new ArgumentException(failure.Message);
            }
            Items = this.sources.Select(s => s.SourcePath ?? string.Empty).ToArray();
            total = this.sources.Sum(s => s.Size);
            totalFiles = this.sources.Count(s => !s.IsDirectory);
        }
        // ZIP includes header-writing work in SetTotal/SetCompleted. Those are
        // progress units, not an exact input-byte counter. Keep them separate.
        private int CheckStop() { try { control?.CheckStop(token); return token.IsCancellationRequested ? Abort : 0; } catch (OperationCanceledException) { return Abort; } }
        public int SetTotal(ulong value) { lock (gate) { metrics.SetTotal(checked((long)value)); Report(true); } return CheckStop(); }
        public int SetCompleted(nint value)
        {
            var stop = CheckStop(); if (stop != 0) return stop;
            lock (gate) { if (value != 0) metrics.SetCompleted(Marshal.ReadInt64(value)); Report(); }
            return 0;
        }
        public int SetRatioInfo(nint input, nint packed)
        {
            var stop = CheckStop(); if (stop != 0) return stop;
            // CLocalProgress reports the ratio before SetCompleted. Store it,
            // but let the completed-work callback publish the matching sample.
            lock (gate) { metrics.SetRatio(input == 0 ? null : Marshal.ReadInt64(input), packed == 0 ? null : Marshal.ReadInt64(packed)); }
            return 0;
        }
        public void OutputWritten(long size)
        { lock (gate) { output = size; Report(); } }
        public int GetUpdateItemInfo(uint index, out int newData, out int newProperties, out uint archiveIndex)
        {
            newData = 1; newProperties = 1; archiveIndex = uint.MaxValue;
            return token.IsCancellationRequested ? Abort : 0;
        }
        public int GetProperty(uint index, uint property, out Variant value)
        {
            value = default;
            try
            {
                var path = Items[index]; var source = sources?[(int)index]; var directory = source?.IsDirectory ?? Directory.Exists(path);
                value = property switch
                {
                    3 => Variant.Text(source?.Path.Value ?? Path.GetRelativePath(root, path)),
                    6 => Variant.Boolean(directory),
                    7 => Variant.Size(directory ? 0 : (ulong)(source?.Size ?? new FileInfo(path).Length)),
                    9 => Variant.Number((uint)(source?.Attributes ?? File.GetAttributes(path))),
                    12 => new Variant { Type = 64, Value = (ulong)(source?.Modified.UtcDateTime ?? File.GetLastWriteTimeUtc(path)).ToFileTimeUtc() },
                    _ => default
                };
                return token.IsCancellationRequested ? Abort : 0;
            }
            catch (Exception exception) { return Marshal.GetHRForException(exception); }
        }
        public int GetStream(uint index, out SequentialInput? stream)
        {
            stream = null;
            try
            {
                token.ThrowIfCancellationRequested();
                var path = Items[index];
                lock (gate) { currentItem = sources?[(int)index].Path.Value ?? Path.GetRelativePath(root, path); Report(true); }
                if (sources?[(int)index] is { } source)
                { source.ValidateSource(); if (source.IsDirectory) return 0; }
                else if (!File.Exists(path)) return 0;
                // Callback disposal releases inputs after UpdateItems, including any Store retry.
                var input = new NativeInputStream(File.OpenRead(path), token, control: control);
                lock (gate) { streams.Add(input); stream = input; }
                return 0;
            }
            catch (Exception exception) { return exception is OperationCanceledException ? Abort : Marshal.GetHRForException(exception); }
        }
        public int GetStream2(uint index, out SequentialInput? stream, uint operation) => GetStream(index, out stream);
        public int ReportOperation(uint indexType, uint index, uint operation)
        {
            try
            {
                var stop = CheckStop(); if (stop != 0) return stop;
                lock (gate)
                {
                    currentItem = indexType == 3 && index < Items.Length
                        ? sources?[(int)index].Path.Value ?? Path.GetRelativePath(root, Items[index]) : null;
                    Report(true);
                }
                return CheckStop();
            }
            catch (Exception exception) { return exception is OperationCanceledException ? Abort : Marshal.GetHRForException(exception); }
        }
        public int SetOperationResult(int result)
        {
            var stop = CheckStop(); if (stop != 0) return stop;
            if (result == 0)
            {
                // OperationResult reports engine processing,
                // not the later output close/publication result.
                lock (gate) { files = Math.Min(files + 1, totalFiles); Report(true); }
                return 0;
            }
            lock (gate) OperationFailed = true;
            return unchecked((int)0x80004005);
        }
        public unsafe int CryptoGetTextPassword2(out int defined, out nint value)
        {
            defined = 0; value = 0;
            var stop = CheckStop(); if (stop != 0) return stop;
            if (password is null) return 0;
            try
            {
                value = password.Use(chars =>
                {
                    fixed (char* pointer = chars) return SysAllocStringLen((nint)pointer, checked((uint)chars.Length));
                });
                if (value == 0) return unchecked((int)0x8007000e);
                defined = 1;
                return 0; // SDK owns and frees the returned BSTR.
            }
            catch (Exception exception) { return Marshal.GetHRForException(exception); }
        }
        [DllImport("oleaut32.dll")] private static extern nint SysAllocStringLen(nint value, uint length);
        public void Complete(long outputBytes)
        {
            lock (gate)
            {
                output = outputBytes; files = totalFiles; finished = true;
                try { Report(true); }
                catch (Exception exception) { reporter?.RecordObserverFailure(exception); }
            }
        }
        public void Finalizing(long outputBytes)
        {
            lock (gate)
            {
                stage = EngineStage.Finalizing; output = outputBytes;
                try { Report(true); } catch (Exception exception) { reporter?.RecordObserverFailure(exception); }
            }
        }
        public void SetStage(EngineStage value)
        {
            lock (gate)
            {
                stage = value;
                try { Report(true); } catch (Exception exception) { reporter?.RecordObserverFailure(exception); }
            }
        }
        private void Report(bool force = false)
        {
            var now = control is null ? clock.Elapsed : control.ActiveElapsed - activeStarted;
            var seconds = (now - previousSample).TotalSeconds;
            if (!force && seconds < .1) return;
            var snapshot = metrics.Snapshot(now);
            // Pre-read source buffers and physical output length can stall while
            // encoding continues. Use the SDK work/ratio counters.
            // ZIP's total includes header work: use that matching total too.
            var processed = finished ? total : snapshot.Completed ?? 0;
            var displayedTotal = finished ? total : snapshot.Total ?? total;
            var encoding = stage == EngineStage.Compressing && !finished;
            previousSample = now;
            progress?.Report(new EngineProgress(stage,
                snapshot.Percentage, currentItem,
                ProcessedBytes: processed, TotalBytes: displayedTotal,
                BytesPerSecond: encoding ? snapshot.AverageSpeed : null,
                EstimatedRemaining: encoding ? snapshot.Remaining : null,
                ProcessedItemCount: Math.Min(files, totalFiles), TotalItemCount: totalFiles,
                OutputBytes: encoding ? Math.Max(checked(OutputPrefixBytes + (snapshot.RatioOutput ?? 0)), output) : output,
                HasMeasuredProcessedBytes: finished || snapshot.Completed.HasValue,
                // Publication switches from SDK work units (ZIP headers included)
                // to the actual source/archive summary. The job must not merge
                // the larger work counter into this final byte total.
                MetricsScope: finished ? "creation-final" : "compression-native", IsRemainingEstimated: encoding && snapshot.Remaining.HasValue,
                OutputIncludesExistingEntries: false, RatioInputBytes: finished ? total : snapshot.RatioInput,
                ActiveElapsed: now, IsPaused: control?.IsPaused == true,
                NativeWorkCompleted: finished ? null : snapshot.Completed, NativeWorkTotal: finished ? null : snapshot.Total,
                RatioOutputBytes: finished ? output : snapshot.RatioOutput));
        }
        public void Dispose() { foreach (var stream in streams) stream.Dispose(); }
    }
}
