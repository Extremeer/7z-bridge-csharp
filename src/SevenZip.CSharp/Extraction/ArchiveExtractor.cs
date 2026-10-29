using System.Runtime.InteropServices;
using static SevenZip.CSharp.NativeArchiveInterop;

namespace SevenZip.CSharp;

/// <summary>One worker owns Open, properties, Extract/testMode and Close.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class ArchiveExtractor
{
    public static Task<ArchiveEngineOutcome> ExtractItemsAsync(string libraryPath, string path,
        ArchiveEntrySelection selection, string destination, ArchiveOperationContext? context = null,
        bool overwrite = false, IProgress<EngineExtractionProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        context ??= new();
        return RunItemsWithPasswordsAsync(libraryPath, path, selection, destination, null,
            context.Password, context.Password, context.CancellationToken, overwrite, context: context,
            extractionProgress: progress);
    }

    public static Task<ArchiveEngineOutcome> RunItemsWithPasswordsAsync(string libraryPath, string path,
        ArchiveEntrySelection selection, string? destination, ExtractionMeasurement? measured,
        ArchivePassword? headerPassword, ArchivePassword? dataPassword, CancellationToken token,
        bool overwrite = false, IProgress<EngineProgress>? operationProgress = null,
        IArchiveExtractionOutputPlan? outputTransaction = null, ArchiveOperationContext? context = null,
        string temporaryFilePrefix = ".archive-extract-", IProgress<EngineExtractionProgress>? extractionProgress = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var operation = new ArchiveOperationContext(headerPassword, context?.Control, token, context?.AllowedFormats);
        return Task.Run(() => Run(libraryPath, path, destination, measured, headerPassword, dataPassword, token,
            overwrite: overwrite, operationProgress: operationProgress, outputTransaction: outputTransaction,
            context: operation, temporaryFilePrefix: temporaryFilePrefix, extractionProgress: extractionProgress,
            itemSelection: selection));
    }

    /// <summary>Extract all entries to a disk destination using safe per-file publication.</summary>
    public static Task<ArchiveEngineOutcome> ExtractAsync(string libraryPath, string path, string destination,
        ArchiveOperationContext? context = null, bool overwrite = false, IProgress<EngineProgress>? progress = null,
        IProgress<EngineExtractionProgress>? extractionProgress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        context ??= new();
        return RunAllWithPasswordsAsync(libraryPath, path, destination, null, context.Password, context.Password,
            context.CancellationToken, overwrite, progress, context: context, extractionProgress: extractionProgress);
    }

    public static Task<ArchiveEngineOutcome> TestWithPasswordsAsync(string libraryPath, string path,
        ArchivePassword? headerPassword, ArchivePassword? dataPassword, CancellationToken token,
        IProgress<EngineProgress>? progress = null, ArchiveOperationContext? context = null)
        => RunAllWithPasswordsAsync(libraryPath, path, null, null, headerPassword, dataPassword, token,
            operationProgress: progress, context: context);

    public static Task<ArchiveEngineOutcome> TestAsync(string libraryPath, string path,
        ArchiveOperationContext? context = null, IProgress<EngineProgress>? progress = null)
    {
        context ??= new();
        return RunAllWithPasswordsAsync(libraryPath, path, null, null, context.Password, context.Password,
            context.CancellationToken, operationProgress: progress, context: context);
    }

    /// <summary>A null data password deliberately does not reuse the header password. Borrow both handles.</summary>
    internal static Task<ArchiveEngineOutcome> RunAllWithPasswordsAsync(string libraryPath, string path, string? destination,
        ExtractionMeasurement? measured, ArchivePassword? headerPassword, ArchivePassword? dataPassword,
        CancellationToken token, bool overwrite = false, IProgress<EngineProgress>? operationProgress = null,
        IArchiveExtractionOutputPlan? outputTransaction = null, ArchiveOperationContext? context = null,
        string temporaryFilePrefix = ".archive-extract-", IProgress<EngineExtractionProgress>? extractionProgress = null)
    {
        var operation = new ArchiveOperationContext(headerPassword, context?.Control, token, context?.AllowedFormats);
        return Task.Run(() => Run(libraryPath, path, destination, measured, headerPassword, dataPassword, token,
            overwrite: overwrite, operationProgress: operationProgress, outputTransaction: outputTransaction,
            context: operation, temporaryFilePrefix: temporaryFilePrefix, extractionProgress: extractionProgress));
    }

    internal static Task<ArchiveEngineOutcome> RunToStreamsWithPasswordsAsync(string libraryPath, string path,
        ArchiveEntrySelection requested, Func<ArchiveEntryInfo, SequentialOutput> sink,
        Action<ArchiveEntryInfo> completed, ArchivePassword? headerPassword, ArchivePassword? dataPassword, CancellationToken token,
        IProgress<EngineProgress>? operationProgress = null, ArchiveOperationContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(completed);
        var operation = new ArchiveOperationContext(headerPassword, context?.Control, token, context?.AllowedFormats);
        return Task.Run(() => Run(libraryPath, path, null, null, headerPassword, dataPassword, token, sink: sink, completed: completed,
            operationProgress: operationProgress, context: operation, itemSelection: requested));
    }

    private static ArchiveEngineOutcome Run(string libraryPath, string path, string? destination,
        ExtractionMeasurement? measured, ArchivePassword? password, ArchivePassword? dataPassword, CancellationToken token,
        Func<ArchiveEntryInfo, SequentialOutput>? sink = null, Action<ArchiveEntryInfo>? completed = null, bool overwrite = false,
        IProgress<EngineProgress>? operationProgress = null, IArchiveExtractionOutputPlan? outputTransaction = null,
        ArchiveOperationContext? context = null, string temporaryFilePrefix = ".archive-extract-",
        IProgress<EngineExtractionProgress>? extractionProgress = null, ArchiveEntrySelection? itemSelection = null)
    {
        context ??= new(password, cancellationToken: token);
        using var nativeControl = context.Control?.EnterNativeOperation();
        try
        {
            token.ThrowIfCancellationRequested();
            var library = SevenZipNativeLibrary.Load(libraryPath);
            foreach (var candidate in ArchiveFormatSelector.Candidates(path, library, context))
            {
                using var session = NativeReadSession.TryOpen(path, library, candidate, token, password, control: context.Control);
                if (session is null) continue;
                ArchiveInspector.Snapshot? snapshot;
                try { snapshot = ArchiveInspector.Map(session, path, token); }
                catch (InvalidDataException) { return new(true, new(ArchiveFailureCode.UnsupportedOperation, "归档包含当前桥接尚不支持的元数据")); } // No output has been created.
                if (snapshot is null) return new(false);
                itemSelection ??= snapshot.SelectAll();
                if (!snapshot.IsCurrent(itemSelection))
                    return new(true, new(ArchiveFailureCode.ConflictDetected, "条目选择已失效，请刷新归档后重试"));
                var requestedItems = itemSelection.Ids.Select(id => snapshot.ById[id]).ToArray();
                var requestedIds = itemSelection.Ids.ToHashSet();
                if (itemSelection is not null && measured is not null
                    && (!measured.Entries.Where(entry => !entry.IsDirectory).Select(entry => entry.Id).ToHashSet()
                        .SetEquals(requestedItems.Where(entry => !entry.IsDirectory).Select(entry => entry.Id))
                        || measured.Entries.Any(entry => !snapshot.ById.TryGetValue(entry.Id, out var actual)
                            || entry.UncompressedSize != actual.UncompressedSize || entry.Crc32 != actual.Crc32)))
                    return new(true, new(ArchiveFailureCode.ConflictDetected, "解压计划不属于当前归档条目版本"));
                if (destination is not null && measured is null)
                    measured = new ExtractionMeasurement(requestedItems, new ActionProgress<EngineExtractionProgress>(value =>
                    {
                        extractionProgress?.Report(value);
                        if (value.OperationProgress is not null) operationProgress?.Report(value.OperationProgress);
                    }));
                var testing = destination is null && sink is null;
                var expected = requestedItems;
                var selected = snapshot.PhysicalEntries.Where(pair => requestedIds.Contains(pair.Value.Id))
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
                if (destination is not null || sink is not null)
                {
                    if (selected.Values.Any(e => (e.Attributes & (ArchiveItemAttributes.SymbolicLink | ArchiveItemAttributes.HardLink
                        | ArchiveItemAttributes.AntiItem | ArchiveItemAttributes.AlternateDataStream | ArchiveItemAttributes.ReparsePoint)) != 0))
                        return new(true, new(ArchiveFailureCode.UnsupportedOperation, "默认安全策略不输出链接、重解析点、反向条目或备用数据流"));
                    if (destination is not null && outputTransaction is null)
                    {
                        var unsafeOutput = ExtractionOutputPolicy.Validate(destination, expected, path);
                        if (unsafeOutput is not null) return new(true, unsafeOutput);
                    }
                    if (!expected.Where(entry => !entry.IsDirectory).Select(entry => entry.Id).ToHashSet()
                        .SetEquals(selected.Values.Where(entry => !entry.IsDirectory).Select(entry => entry.Id)))
                        return new(true, new(ArchiveFailureCode.ConflictDetected, "当前归档与已验证的解压快照不一致"));
                    if (outputTransaction is not null)
                    {
                        foreach (var index in selected.Where(p => outputTransaction.OutputEntry(p.Value) is null).Select(p => p.Key).ToArray()) selected.Remove(index);
                        outputTransaction.PrepareDirectories(token);
                    }
                    if (destination is not null && !overwrite && outputTransaction is null)
                    {
                        // Reserve output names in physical order. Later archive items can collide
                        // even when the destination was initially empty. Exclude skipped bytes.
                        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var skipped = selected.Where(pair => !pair.Value.IsDirectory
                            && (File.Exists(ExtractionOutputPolicy.PathFor(destination, pair.Value))
                                || !reserved.Add(pair.Value.Path.Value))).Select(pair => pair.Key).ToArray();
                        foreach (var index in skipped) selected.Remove(index);
                    }
                    if (destination is not null && outputTransaction is null)
                    {
                        // Only create output after opening and validating the archive/selection.
                        Directory.CreateDirectory(destination);
                        foreach (var directory in expected.Where(e => e.IsDirectory))
                            Directory.CreateDirectory(Path.Combine(destination, directory.Path.Value.Replace('/', Path.DirectorySeparatorChar)));
                    }
                }
                var stats = measured is null ? null : new ExtractionStatistics(new ExtractionMeasurement(selected.Values, measured), context.Control);
                var readProgress = operationProgress is null ? null : new ArchiveReadStatistics(selected.Values, sink is not null, operationProgress, context.Control);
                using var callback = new Callback(destination, selected, stats, dataPassword, token, sink, completed, overwrite, outputTransaction, context.Control, temporaryFilePrefix)
                    { ReadProgress = readProgress };
                ArchiveEngineOutcome Complete(ArchiveFailure? error = null)
                {
                    callback.Dispose();
                    var diagnostic = string.Join(Environment.NewLine, new[] { stats?.ObserverDiagnostic, callback.CleanupDiagnostic }.Where(d => !string.IsNullOrEmpty(d)));
                    if (diagnostic.Length == 0) return new(true, error);
                    return new(true, error is null ? null : error with
                    { Diagnostic = string.Join(Environment.NewLine, new[] { error.Diagnostic, diagnostic }.Where(d => !string.IsNullOrEmpty(d))) }, diagnostic);
                }
                var indices = Marshal.AllocCoTaskMem(selected.Count * sizeof(uint)); nint pointer = 0;
                try
                {
                    var offset = 0;
                    foreach (var index in selected.Keys) Marshal.WriteInt32(indices, offset++ * sizeof(uint), unchecked((int)index));
                    pointer = Marshal.GetComInterfaceForObject(callback, typeof(NativeExtractCallback));
                    int Execute() => selected.Count == 0 ? 0 : session.Extract(indices, (uint)selected.Count, testing, pointer);
                    // Sampling is asynchronous, but SDK calls and disposal stay on this worker.
                    var hr = stats is null ? Execute() : stats.RunAsync(() => Task.FromResult(Execute())).GetAwaiter().GetResult();
                    if (token.IsCancellationRequested) return Complete(new(ArchiveFailureCode.OperationCancelled, "操作已取消"));
                    if (callback.Error is not null) return Complete(callback.Error);
                    Marshal.ThrowExceptionForHR(hr);
                    callback.FinishProgress();
                    if (token.IsCancellationRequested) return Complete(new(ArchiveFailureCode.OperationCancelled, "操作已取消"));
                    if (callback.Error is not null) return Complete(callback.Error);
                    return Complete();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                { return Complete(new(ArchiveFailureCode.OperationCancelled, "操作已取消")); }
                catch (Exception exception)
                { return Complete(callback.Error ?? EngineFailureClassifier.TryMap(exception) ?? new(
                    exception is COMException ? ArchiveFailureCode.IoFailure : exception is OperationCanceledException ? ArchiveFailureCode.OperationCancelled : ArchiveFailureCode.EngineFailure,
                    "原生解压执行失败", Diagnostic: EngineFailureClassifier.Diagnostic(exception))); }
                finally { if (pointer != 0) Marshal.Release(pointer); Marshal.FreeCoTaskMem(indices); }
            }
            if (ArchiveVolumeInput.IsSplit(path)) return new(true, new(ArchiveFailureCode.ArchiveDamaged,
                "分卷压缩包不完整或已损坏，请检查最后一卷是否缺失"));
            return new(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new(true, new(ArchiveFailureCode.OperationCancelled, "操作已取消")); }
        catch (ArchiveReadException exception) { return new(true, EngineFailureClassifier.TryMap(exception)); }
        catch (UnauthorizedAccessException) { return new(true, new(ArchiveFailureCode.PermissionDenied, "访问被拒绝")); }
        catch (InvalidDataException) { return new(true, new(ArchiveFailureCode.ArchiveDamaged, "压缩数据无效")); }
        catch (ArgumentException) { return new(true, new(ArchiveFailureCode.InvalidPath, "压缩包包含无效路径")); }
        catch (IOException exception)
        { return new(true, token.IsCancellationRequested ? new(ArchiveFailureCode.OperationCancelled, "操作已取消") : EngineFailureClassifier.TryMap(exception)); }
        catch (COMException)
        { return new(true, new(token.IsCancellationRequested ? ArchiveFailureCode.OperationCancelled : ArchiveFailureCode.IoFailure, "原生压缩引擎读写失败")); }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class Callback(string? root, IReadOnlyDictionary<uint, ArchiveEntryInfo> entries,
        ExtractionStatistics? stats, ArchivePassword? password, CancellationToken token,
        Func<ArchiveEntryInfo, SequentialOutput>? sink = null, Action<ArchiveEntryInfo>? completed = null, bool overwrite = false,
        IArchiveExtractionOutputPlan? outputTransaction = null, ArchiveOperationControl? control = null,
        string temporaryFilePrefix = ".archive-extract-")
        : NativeExtractCallback, RatioCallback, NativeArchiveInterop.NativeReadPassword, IDisposable
    {
        private const int Abort = unchecked((int)0x80004004);
        private readonly NativeReadSession.OpenCallback crypto = new(password, token, control: control);
        private readonly string prefix = ValidatePrefix(temporaryFilePrefix);
        private static string ValidatePrefix(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("Invalid temporary file prefix.", nameof(value));
            return value;
        }
        private FileStream? file;
        private ArchiveEntryInfo? current;
        private string? currentPath;
        private string? temporaryPath;
        private bool createdFile;
        public ArchiveFailure? Error { get; private set; }
        public string? CleanupDiagnostic { get; private set; }
        private bool disposed;
        public ArchiveReadStatistics? ReadProgress { get; set; }
        private int operationMode;
        private int CaptureManagedFailure(Exception exception)
        {
            // Preserve the managed category before SDK/COM collapses it to an
            // HRESULT; never copy arbitrary exception text into the result.
            Error ??= (exception is OperationCanceledException
                ? new ArchiveFailure(ArchiveFailureCode.OperationCancelled, "操作已取消")
                : EngineFailureClassifier.TryMap(exception) ?? new ArchiveFailure(ArchiveFailureCode.EngineFailure,
                    "原生解压回调失败", Diagnostic: EngineFailureClassifier.Diagnostic(exception)))
                with { Path = current?.Path.Value };
            return exception is OperationCanceledException ? Abort
                : exception.HResult < 0 ? exception.HResult : unchecked((int)0x80004005);
        }
        private int CheckStop()
        {
            return (control?.TryCheckStop(token) ?? !token.IsCancellationRequested) ? 0 : Abort;
        }
        public int SetTotal(ulong total)
        {
            try { var value = checked((long)total); stats?.SetNativeTotal(value); ReadProgress?.SetTotal(value); return CheckStop(); }
            catch (Exception exception) { return CaptureManagedFailure(exception); }
        }
        public int SetCompleted(nint completed)
        {
            try { var value = completed == 0 ? (long?)null : Marshal.ReadInt64(completed); stats?.SetNativeCompleted(value); ReadProgress?.SetCompleted(value); return CheckStop(); }
            catch (Exception exception) { return CaptureManagedFailure(exception); }
        }
        public int SetRatioInfo(nint input, nint output)
        {
            try
            {
                var inputSize = input == 0 ? (long?)null : Marshal.ReadInt64(input);
                var outputSize = output == 0 ? (long?)null : Marshal.ReadInt64(output);
                stats?.SetNativeRatio(inputSize, outputSize);
                ReadProgress?.SetRatio(inputSize, outputSize);
                return CheckStop();
            }
            catch (Exception exception) { return CaptureManagedFailure(exception); }
        }
        public int FinishProgress()
        {
            if (Error is not null) return Abort;
            try { var stop = CheckStop(); if (stop != 0) return stop; ReadProgress?.Finish(); return CheckStop(); }
            catch (Exception exception) { return CaptureManagedFailure(exception); }
        }
        public int PrepareOperation(int mode)
        {
            try
            {
                var stop = CheckStop(); if (stop != 0) return stop;
                operationMode = mode;
                if (mode is 0 or 1 && current is not null)
                {
                    ReadProgress?.BeginFile(current);
                    stats?.BeginFile(current.Path);
                }
                return CheckStop();
            }
            catch (Exception exception) { return CaptureManagedFailure(exception); }
        }
        public int GetStream(uint index, out SequentialOutput? stream, int mode)
        {
            stream = null;
            try
            {
                if (!Cleanup()) { Error ??= new(ArchiveFailureCode.IoFailure, "无法清理上一条目的解压暂存", Diagnostic: CleanupDiagnostic); return Abort; }
                current = null; currentPath = null; createdFile = false;
                if (CheckStop() != 0) return Abort;
                if (!entries.TryGetValue(index, out current)) return 0;
                if (sink is not null && mode == 0 && !current.IsDirectory)
                { var output = sink(current); stream = new CallbackOutput(ReadProgress?.Wrap(output) ?? output, this); return 0; }
                if (root is null || mode != 0) return 0;
                if (outputTransaction is not null)
                {
                    var output = outputTransaction.OutputEntry(current);
                    if (output is null) { current = null; return 0; }
                    outputTransaction.ValidateOutput(output);
                    currentPath = current.IsDirectory ? outputTransaction.OutputPath(current) : outputTransaction.BeginFile(current);
                }
                else
                {
                    var unsafeOutput = ExtractionOutputPolicy.Validate(root, [current]);
                    if (unsafeOutput is not null) { Error = unsafeOutput; return Abort; }
                    currentPath = Path.GetFullPath(Path.Combine(root, current.Path.Value.Replace('/', Path.DirectorySeparatorChar)));
                }
                var pathFailure = ValidateCurrentPath();
                if (pathFailure is not null) { Error = pathFailure; return Abort; }
                if (current.IsDirectory) { Directory.CreateDirectory(currentPath); return 0; }
                if (outputTransaction is null && !overwrite && File.Exists(currentPath)) { current = null; return 0; }
                Directory.CreateDirectory(Path.GetDirectoryName(currentPath)!);
                temporaryPath = Path.Combine(Path.GetDirectoryName(currentPath)!, prefix + Guid.NewGuid().ToString("N") + ".tmp");
                file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                createdFile = true;
                stream = new ExtractionOutputStream(file, stats!, token, control, CaptureManagedFailure); return 0;
            }
            catch (Exception exception) { return CaptureManagedFailure(exception); }
        }
        public int SetOperationResult(int result)
        {
            var stop = CheckStop();
            if (stop != 0) return stop;
            if (result != 0)
            {
                var passwordError = result == 9 || current?.IsEncrypted == true && result is 2 or 3;
                Error ??= new(passwordError ? password is null ? ArchiveFailureCode.RequiresPassword : ArchiveFailureCode.IncorrectPassword
                        : result == 1 ? ArchiveFailureCode.UnsupportedOperation : result is >= 2 and <= 8 ? ArchiveFailureCode.ArchiveDamaged : ArchiveFailureCode.EngineFailure,
                    passwordError ? "数据密码不正确或加密数据已损坏" : result switch
                    {
                        1 => "不支持此压缩方法",
                        2 => "压缩数据损坏",
                        3 => "CRC 校验失败",
                        4 => "所需压缩数据或分卷不可用",
                        5 => "压缩数据意外截断",
                        6 => "压缩数据结束后包含额外数据",
                        7 => "数据不是有效归档",
                        8 => "归档头损坏",
                        _ => "原生压缩引擎返回未知操作结果"
                    },
                    current?.Path.Value);
                if (createdFile && currentPath is not null) Cleanup();
                return Abort;
            }
            // SDK failure is already primary before closing an owned output.
            // On SDK success, a close/flush failure remains a real operation failure.
            try { file?.Dispose(); file = null; }
            catch (Exception exception) { return CaptureManagedFailure(exception); }
            if (sink is not null && current is { IsDirectory: false })
            {
                try { completed!(current); }
                catch (Exception exception) { return CaptureManagedFailure(exception); }
                stop = CheckStop();
                if (stop != 0) return stop;
            }
            try
            {
                if (current is { IsDirectory: false } && operationMode is 0 or 1 && root is null)
                {
                    ReadProgress?.ValidatedFile();
                }
                if (current is not null && root is not null)
                {
                    var metadataPath = current.IsDirectory ? currentPath : temporaryPath;
                    if (current.Modified is { } modified && metadataPath is not null)
                    {
                        if (current.IsDirectory)
                        { if (outputTransaction is null || outputTransaction.OwnsDirectory(metadataPath)) Directory.SetLastWriteTimeUtc(metadataPath, modified.UtcDateTime); }
                        else File.SetLastWriteTimeUtc(metadataPath, modified.UtcDateTime);
                    }
                    if (!current.IsDirectory && temporaryPath is not null && currentPath is not null)
                    {
                        var attributes = (FileAttributes)0;
                        if (current.Attributes.HasFlag(ArchiveItemAttributes.ReadOnly)) attributes |= FileAttributes.ReadOnly;
                        if (current.Attributes.HasFlag(ArchiveItemAttributes.Hidden)) attributes |= FileAttributes.Hidden;
                        if (current.Attributes.HasFlag(ArchiveItemAttributes.System)) attributes |= FileAttributes.System;
                        if (current.Attributes.HasFlag(ArchiveItemAttributes.Archive)) attributes |= FileAttributes.Archive;
                        File.SetAttributes(temporaryPath, attributes == 0 ? FileAttributes.Normal : attributes);
                        stats?.ReportStage(EngineStage.Validating);
                        if (CheckStop() != 0) return Abort;
                        if (outputTransaction is null)
                        {
                            var unsafeOutput = ExtractionOutputPolicy.Validate(root, [current]);
                            if (unsafeOutput is not null) { Error = unsafeOutput; return Abort; }
                            if (!overwrite && File.Exists(currentPath)) { Cleanup(); return CheckStop(); }
                        }
                        stats?.ReportStage(EngineStage.Committing);
                        if (CheckStop() != 0) return Abort;
                        var pathFailure = ValidateCurrentPath();
                        if (pathFailure is not null) { Error = pathFailure; return Abort; }
                        if (outputTransaction is not null) outputTransaction.Publish(current, temporaryPath, currentPath, token);
                        else File.Move(temporaryPath, currentPath, overwrite);
                        temporaryPath = null; createdFile = false;
                        stats!.RecordCompletedFile();
                    }
                }
                return CheckStop();
            }
            catch (Exception exception) { return CaptureManagedFailure(exception); }
        }
        public int CryptoGetTextPassword(out nint value)
        {
            var result = crypto.CryptoGetTextPassword(out value);
            if (result != 0 && !token.IsCancellationRequested) Error ??= new(ArchiveFailureCode.RequiresPassword, "压缩包需要数据密码");
            return result;
        }
        private ArchiveFailure? ValidateCurrentPath()
        {
            if (root is null || currentPath is null) return null;
            if (!DiskPathPolicy.IsSameOrDescendant(currentPath, root))
                return new(ArchiveFailureCode.InvalidPath, "解压路径超出目标目录", current?.Path.Value);
            var failure = OutputSafetyValidator.Validate(root, [ArchivePath.From(Path.GetRelativePath(root, currentPath))]);
            if (failure is not null) return failure;
            if ((File.Exists(currentPath) || Directory.Exists(currentPath))
                && (File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
                return new(ArchiveFailureCode.InvalidPath, "解压目标条目包含重解析点", current?.Path.Value);
            return null;
        }
        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        private sealed class CallbackOutput(SequentialOutput output, Callback owner) : SequentialOutput
        {
            public int Write(nint data, uint size, nint processed)
            {
                try { return output.Write(data, size, processed); }
                catch (Exception exception)
                {
                    if (processed != 0) Marshal.WriteInt32(processed, 0);
                    return owner.CaptureManagedFailure(exception);
                }
            }
        }
        private bool Cleanup(bool final = false)
        {
            var notify = final || file is not null || temporaryPath is not null;
            try
            {
                file?.Dispose(); file = null;
                if (notify) stats?.ReportCleanup();
                if (temporaryPath is not null && File.Exists(temporaryPath))
                {
                    File.SetAttributes(temporaryPath, FileAttributes.Normal);
                    File.Delete(temporaryPath);
                }
                temporaryPath = null; return true;
            }
            catch (Exception exception)
            {
                CleanupDiagnostic ??= "Native extraction cleanup failed: " + EngineFailureClassifier.Diagnostic(exception)
                    + "; RetainedTemporaryFile=" + (temporaryPath is not null);
                return false;
            }
        }
        public void Dispose() { if (disposed) return; disposed = true; Cleanup(final: true); }
    }
}
