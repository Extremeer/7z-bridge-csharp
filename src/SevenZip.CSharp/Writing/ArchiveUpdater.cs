using System.Diagnostics;
using System.Runtime.InteropServices;
using static SevenZip.CSharp.NativeArchiveInterop;

namespace SevenZip.CSharp;

/// <summary>Retain/delete/rename plans reuse archive data through UpdateItems, never extract/re-add to rename.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class ArchiveUpdater
{
    private sealed record Item(uint Index, ArchiveEntryInfo Entry, ArchivePath Destination, IReadOnlyDictionary<uint, object?> Properties, string? FilePath = null,
        ArchiveSourceItem? Source = null);
    public static Task<ArchiveEngineOutcome> UpdateAsync(string libraryPath, string path, ArchiveUpdateRequest request,
        ArchiveOperationContext? context = null, IProgress<EngineProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        context ??= new();
        var operation = context;
        var plan = request.Snapshot();
        var relay = new EngineProgressRelay(progress);
        return Task.Run(() =>
        {
            var result = Run(libraryPath, path, plan.Deleted, plan.RenameSource, plan.RenameDestination,
                operation.CancellationToken, plan.StagingRoot, plan.AddOptions, plan.Sources, plan.Moves,
                plan.TransactionOutputPath, operation, relay);
            return result.Applicable ? result : new ArchiveEngineOutcome(true,
                new(ArchiveFailureCode.UnsupportedOperation, "此格式不支持原生更新"));
        });
    }

    private static ArchiveEngineOutcome Run(string libraryPath, string path, IReadOnlyCollection<ArchivePath> deleted,
        ArchivePath? source, ArchivePath? destination, CancellationToken token, string? stagingRoot, ArchiveUpdateOptions? addOptions,
        IReadOnlyList<ArchiveSourceItem>? sources, IReadOnlyList<ArchiveMove>? moves,
        string? transactionOutput, ArchiveOperationContext context, EngineProgressRelay relay)
    {
        try
        {
            var result = RunProtected(libraryPath, path, deleted, source, destination, token, stagingRoot, addOptions, sources, moves, transactionOutput, context, relay);
            return result with { Diagnostic = EngineProgressRelay.CombineDiagnostics(result.Diagnostic, relay.ObserverDiagnostic) };
        }
        catch (OperationCanceledException) { return Fail(ArchiveFailureCode.OperationCancelled, "操作已取消"); }
        catch (UnauthorizedAccessException) { return Fail(ArchiveFailureCode.PermissionDenied, "无法获取压缩包写入权限"); }
        catch (ArchiveWriterBusyException) { return Fail(ArchiveFailureCode.ArchiveBusy, "压缩包正在被另一任务修改"); }
        catch (IOException exception) { return Fail(EngineFailureClassifier.FromIOException(exception), "无法锁定或读取压缩包"); }
    }

    private static ArchiveEngineOutcome RunProtected(string libraryPath, string path, IReadOnlyCollection<ArchivePath> deleted,
        ArchivePath? source, ArchivePath? destination, CancellationToken token, string? stagingRoot, ArchiveUpdateOptions? addOptions,
        IReadOnlyList<ArchiveSourceItem>? sources, IReadOnlyList<ArchiveMove>? moves,
        string? transactionOutput, ArchiveOperationContext context, EngineProgressRelay relay)
    {
        using var writer = NativeArchiveWriteLock.AcquireAsync(path, token).GetAwaiter().GetResult();
        var original = transactionOutput is null ? ArchiveFileIdentity.CaptureIfExists(path) : null;
        var output = new PlanOutput(transactionOutput is not null && !File.Exists(transactionOutput) ? transactionOutput
            : (transactionOutput ?? path) + ".native-plan-" + Guid.NewGuid().ToString("N"));
        ArchiveEngineOutcome result;
        try { result = RunCore(libraryPath, path, deleted, source, destination, token, output, stagingRoot, addOptions, transactionOutput, sources, moves, original, context, relay); }
        catch { Cleanup(output, relay); throw; } // Preserve unexpected primary exceptions, but still clean only owned output.
        var cleanup = Cleanup(output, relay);
        if (cleanup is null)
        {
            var diagnostic = EngineProgressRelay.CombineDiagnostics(result.Diagnostic, relay.ObserverDiagnostic);
            return result with { Diagnostic = diagnostic, Error = result.Error is { } error
                ? error with { Diagnostic = EngineProgressRelay.CombineDiagnostics(error.Diagnostic, relay.ObserverDiagnostic) } : null };
        }
        return new(true, result.Error is { } primary ? primary with
        {
            Message = primary.Message + "; " + cleanup.Message,
            Diagnostic = string.Join("; ", new[] { primary.Diagnostic, "Cleanup=" + cleanup.Code + "; " + cleanup.Diagnostic, relay.ObserverDiagnostic }.Where(value => value is not null))
        } : cleanup with { Diagnostic = EngineProgressRelay.CombineDiagnostics(cleanup.Diagnostic, relay.ObserverDiagnostic) }, relay.ObserverDiagnostic);
    }
    private sealed class PlanOutput(string path)
    { internal string Path { get; } = path; internal bool Owned; }
    private static ArchiveFailure? Cleanup(PlanOutput output, EngineProgressRelay relay)
    {
        if (!output.Owned) return null;
        relay.ReportStage(EngineStage.CleaningUp, output.Path);
        try { File.Delete(output.Path); output.Owned = false; return null; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(exception is UnauthorizedAccessException ? ArchiveFailureCode.PermissionDenied : EngineFailureClassifier.FromIOException((IOException)exception),
                "任务暂存清理失败，文件保留在：" + output.Path, output.Path, EngineFailureClassifier.Diagnostic(exception), Stage: EngineFailureStage.Cleanup);
        }
    }
    private static ArchiveEngineOutcome RunCore(string libraryPath, string path, IReadOnlyCollection<ArchivePath> deleted,
        ArchivePath? source, ArchivePath? destination, CancellationToken token, PlanOutput plan, string? stagingRoot, ArchiveUpdateOptions? addOptions,
        string? transactionOutput, IReadOnlyList<ArchiveSourceItem>? sources, IReadOnlyList<ArchiveMove>? moves, ArchiveFileIdentity? original, ArchiveOperationContext context, EngineProgressRelay relay)
    {
        var output = plan.Path;
        using var controlScope = context.Control?.EnterNativeOperation();
        try
        {
            token.ThrowIfCancellationRequested();
            if (deleted.Any(p => p.IsRoot) || source?.IsRoot == true || destination?.IsRoot == true)
                return Fail(ArchiveFailureCode.InvalidOperation, "不能修改压缩包根目录");
            if (source is not null && destination is not null && !source.Equals(destination) && source.IsAncestorOf(destination))
                return Fail(ArchiveFailureCode.InvalidPath, "不能将目录移动到自身内部");
            if (destination is not null)
            {
                var valid = WindowsOutputNamePolicy.ValidateEntry(destination, destination.Value);
                if (valid is not null) return new(true, valid);
            }
            var library = SevenZipNativeLibrary.Load(libraryPath);
            foreach (var format in ArchiveFormatSelector.Candidates(path, library, context))
            {
                var password = context.Password;
                using var callback = new Callback(token, new FileInfo(path).Length, password, context.Control, relay);
                using var session = NativeReadSession.TryOpen(path, library, format, token, password,
                    callback.Read, context.Control);
                if (session is null) continue;
                if (session.IsMultiVolume) return Fail(ArchiveFailureCode.UnsupportedOperation, "分卷归档不支持原地修改");
                if (format.Name.ToLowerInvariant() is not ("zip" or "7z") || !format.CanUpdate)
                    return Fail(ArchiveFailureCode.UnsupportedOperation, "此归档格式不支持修改");
                var snapshot = ArchiveInspector.Map(session, path, token);
                if (snapshot is null) return new(false);
                if (snapshot.Format == "zip" && snapshot.PrefixLength != snapshot.ArchiveOffset)
                    return Fail(ArchiveFailureCode.UnsupportedOperation, "SDK 不支持修改带嵌入桩的 ZIP 归档");
                if (snapshot.Entries.Any(e => (e.Attributes & (ArchiveItemAttributes.SymbolicLink | ArchiveItemAttributes.HardLink
                    | ArchiveItemAttributes.AntiItem | ArchiveItemAttributes.AlternateDataStream | ArchiveItemAttributes.ReparsePoint)) != 0))
                    return Fail(ArchiveFailureCode.UnsupportedOperation, "默认安全策略不修改包含链接、重解析点、反向条目或备用数据流的归档");
                if (!snapshot.CanUpdate)
                    return Fail(ArchiveFailureCode.UnsupportedOperation, "此归档结构或安全策略不支持修改");
                // Only an available session
                // credential encrypts new data. Retained ZIP bytes need no password.
                callback.Encrypted = password is not null;
                if (callback.Encrypted)
                {
                    session.ConfigureUpdate(new ArchiveWriteOptions
                    { ArchivePath = path, Format = format.Name.Equals("zip", StringComparison.OrdinalIgnoreCase) ? ArchiveWriteFormat.Zip : ArchiveWriteFormat.SevenZip,
                        Encryption = new ArchiveEncryptionSettings { EncryptFileNames = session.HeaderPasswordRequested,
                            Method = snapshot.Format == "7z" ? ArchiveEncryptionAlgorithm.Aes256 : UpdateEncryptionMethod(snapshot.Entries) } });
                }
                var existingPaths = snapshot.Entries.Select(e => e.Path).ToHashSet();
                if (moves is not null && moves.Any(m => m.DestinationPath is null || m.DestinationPath.IsRoot
                    || !existingPaths.Contains(ArchivePath.From(m.Source.Value))
                    || ArchivePath.From(m.Source.Value).IsAncestorOf(m.DestinationPath) && !ArchivePath.From(m.Source.Value).Equals(m.DestinationPath)))
                    return Fail(ArchiveFailureCode.InvalidOperation, "内部移动计划无效");
                var deletedPaths = deleted.ToHashSet();
                if (moves is not null && deleted.Any(path => moves.Any(m => path.IsAncestorOf(ArchivePath.From(m.Source.Value)))))
                    return Fail(ArchiveFailureCode.ConflictDetected, "移动计划不能同时删除要移动的来源");
                bool IsDeleted(ArchivePath entry)
                {
                    for (var current = entry; !current.IsRoot; current = current.Parent)
                        if (deletedPaths.Contains(current)) return true;
                    return false;
                }
                if (deleted.Any(p => !existingPaths.Contains(p)) || source is not null && !existingPaths.Contains(source))
                    return Fail(ArchiveFailureCode.EntryNotFound, "未找到请求修改的条目");
                var items = new List<Item>();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in snapshot.PhysicalEntries)
                {
                    token.ThrowIfCancellationRequested();
                    var entry = pair.Value;
                    if (IsDeleted(entry.Path)) continue;
                    var move = moves?.FirstOrDefault(m => ArchivePath.From(m.Source.Value).IsAncestorOf(entry.Path));
                    var oldPath = move is null ? source : ArchivePath.From(move.Source.Value);
                    var newPath = move is null ? destination : move.DestinationPath;
                    var moved = oldPath is not null && oldPath.IsAncestorOf(entry.Path);
                    var next = moved ? ArchivePath.From(newPath!.Value + entry.Path.Value[oldPath!.Value.Length..]) : entry.Path;
                    var validPath = WindowsOutputNamePolicy.ValidateEntry(next, next.Value);
                    if (validPath is not null) return new(true, validPath);
                    if (!names.Add(next.Value)) return Fail(ArchiveFailureCode.ConflictDetected, "目标路径已存在：" + next.Value);
                    var properties = new Dictionary<uint, object?>();
                    if (moved) foreach (var id in new uint[] { 3, 6, 7, 9, 10, 11, 12, 15, 19, 21, 28, 53 })
                        properties[id] = id == 3 ? next.Value : session.GetProperty(pair.Key, id);
                    items.Add(new(pair.Key, entry, next, properties));
                }
                if (stagingRoot is not null || sources is not null)
                {
                    var byPath = items.ToDictionary(i => i.Destination);
                    var inputs = sources ?? ArchiveSourceEnumerator.EnumerateDirectory(stagingRoot!, token, relay);
                    foreach (var inputItem in inputs)
                    {
                        token.ThrowIfCancellationRequested();
                        inputItem.ValidateSource();
                        var next = inputItem.Path;
                        var valid = WindowsOutputNamePolicy.ValidateEntry(next, next.Value);
                        if (valid is not null) return new(true, valid);
                        var directory = inputItem.IsDirectory; var size = inputItem.Size;
                        var modified = inputItem.Modified;
                        var index = uint.MaxValue;
                        if (byPath.TryGetValue(next, out var old))
                        {
                            if (directory != old.Entry.IsDirectory) return Fail(ArchiveFailureCode.ConflictDetected, "文件与目录路径冲突：" + next.Value);
                            if (directory || !addOptions!.Overwrite || addOptions.WriteMode == ArchiveUpdateMode.Update && old.Entry.Modified is { } previous && modified <= previous) continue;
                            index = old.Index;
                        }
                        var entry = new ArchiveEntryInfo(next, directory, size, 0, modified, directory ? ArchiveItemAttributes.Directory : ArchiveItemAttributes.None, !directory && callback.Encrypted, null, null);
                        var properties = new Dictionary<uint, object?> { [3] = next.Value, [6] = directory, [7] = (ulong)size,
                            [9] = (uint)inputItem.Attributes, [12] = modified };
                        byPath[next] = new(index, entry, next, properties, inputItem.SourcePath, inputItem);
                    }
                    items = byPath.Values.ToList();
                    callback.AddedFiles = items.Count(i => i.FilePath is not null && !i.Entry.IsDirectory);
                    callback.AddedBytes = items.Where(i => i.FilePath is not null && !i.Entry.IsDirectory).Sum(i => i.Entry.UncompressedSize);
                }
                var filePaths = items.Where(i => !i.Entry.IsDirectory).Select(i => i.Destination).ToHashSet();
                foreach (var item in items)
                    for (var parent = item.Destination.Parent; !parent.IsRoot; parent = parent.Parent)
                        if (filePaths.Contains(parent)) return Fail(ArchiveFailureCode.ConflictDetected, "文件与目录路径冲突：" + parent.Value);
                // A destination directory itself may be virtual (no physical entry).
                if (source is not null && !source.Equals(destination)
                    && snapshot.Entries.Any(e => e.Path.Equals(destination) && !(source.Equals(e.Path) || source.IsAncestorOf(e.Path))))
                    return Fail(ArchiveFailureCode.ConflictDetected, "目标路径已存在：" + destination!.Value);
                callback.Items = items.ToArray();
                callback.OriginalPaths = snapshot.PhysicalEntries.ToDictionary(pair => pair.Key, pair => pair.Value.Path.Value);
                callback.AffectedFiles = callback.AddedFiles + snapshot.Entries.Count(e => !e.IsDirectory && (IsDeleted(e.Path)
                    || source is not null && (source.Equals(e.Path) || source.IsAncestorOf(e.Path))
                    || moves is not null && moves.Any(m => ArchivePath.From(m.Source.Value).IsAncestorOf(e.Path))));
                using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    plan.Owned = true;
                    callback.BeginWriting();
                    // Notify deleted names while constructing
                    // the update plan; these are not completed-file increments.
                    foreach (var entry in snapshot.PhysicalEntries.Values.Where(entry => IsDeleted(entry.Path)))
                        callback.BeginItem(entry.Path.Value);
                    var hr = session.UpdateItems(new NativeOutputStream(file, token, callback.Written, context.Control), (uint)items.Count, callback);
                    token.ThrowIfCancellationRequested();
                    if (callback.Error is not null) return new(true, callback.Error);
                    if (hr == unchecked((int)0x80004001)) return Fail(ArchiveFailureCode.UnsupportedOperation, "SDK 不支持此归档的更新结构");
                    Marshal.ThrowExceptionForHR(hr);
                    callback.WritingFinished();
                    context.Control?.CheckStop(token);
                    token.ThrowIfCancellationRequested();
                    file.Flush(true);
                }
                session.Dispose(); // All original-file handles must close before replacing the working copy.
                // Do not decode/re-list the entire output before commit. Keep
                // the targeted executable-prefix guard, not a full payload test.
                if (!EmbeddedArchivePolicy.PrefixEquals(path, output, snapshot.PrefixLength, token, context.Control))
                    return Fail(ArchiveFailureCode.ArchiveDamaged, "修改后的嵌入归档前缀不一致，已拒绝提交");
                if (transactionOutput is null) callback.BeginCommit();
                context.Control?.CheckStop(token);
                token.ThrowIfCancellationRequested();
                if (original is not null && original != ArchiveFileIdentity.CaptureIfExists(path))
                {
                    plan.Owned = false; // Recovery output intentionally survives cleanup.
                    return new(true, new ArchiveFailure(ArchiveFailureCode.ConflictDetected,
                        "原压缩包在任务期间发生变化，未覆盖原文件；工作副本保留在：" + output, output));
                }
                if (transactionOutput is null) File.Move(output, path, true);
                else if (!output.Equals(transactionOutput, StringComparison.OrdinalIgnoreCase)) File.Move(output, transactionOutput, true);
                plan.Owned = false;
                callback.Complete();
                return new(true);
            }
            // No handler accepted the input. Readable but unsupported formats
            // return above; do not misreport a damaged archive as read-only.
            return Fail(ArchiveFailureCode.ArchiveDamaged, "无法读取归档，输入无效或已损坏");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return Fail(ArchiveFailureCode.OperationCancelled, "操作已取消"); }
        catch (ArchiveReadException exception) { return new(true, EngineFailureClassifier.TryMap(exception)); }
        catch (ArgumentException) { return Fail(ArchiveFailureCode.InvalidPath, "无效的条目路径"); }
        catch (UnauthorizedAccessException) { return Fail(ArchiveFailureCode.PermissionDenied, "访问被拒绝"); }
        catch (InvalidDataException) { return Fail(ArchiveFailureCode.ArchiveDamaged, "归档元数据无效"); }
        catch (Exception exception) when (exception is COMException or IOException)
        { return Fail(token.IsCancellationRequested ? ArchiveFailureCode.OperationCancelled
            : EngineFailureClassifier.FromIOException(exception as IOException ?? new IOException("Native update I/O", exception.HResult)), "原生归档修改失败"); }
    }
    private static ArchiveEngineOutcome Fail(ArchiveFailureCode code, string message) => new(true, new(code, message));

    // Retained entries keep their encoded data. Newly encoded entries use the
    // existing strength (the strongest one if a ZIP mixes encryption methods).
    public static ArchiveEncryptionAlgorithm UpdateEncryptionMethod(IEnumerable<ArchiveEntryInfo> entries)
    {
        var methods = entries.Where(e => e.IsEncrypted).Select(e => e.CompressionMethod ?? "").ToArray();
        if (methods.Any(m => m.Contains("AES-256", StringComparison.OrdinalIgnoreCase)
            || m.Contains("7zAES", StringComparison.OrdinalIgnoreCase))) return ArchiveEncryptionAlgorithm.Aes256;
        if (methods.Any(m => m.Contains("AES-192", StringComparison.OrdinalIgnoreCase))) return ArchiveEncryptionAlgorithm.Aes192;
        if (methods.Any(m => m.Contains("AES-128", StringComparison.OrdinalIgnoreCase))) return ArchiveEncryptionAlgorithm.Aes128;
        return methods.Any(m => m.Contains("AES", StringComparison.OrdinalIgnoreCase))
            ? ArchiveEncryptionAlgorithm.Aes256 : ArchiveEncryptionAlgorithm.ZipCrypto;
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class Callback(CancellationToken token, long sourceSize, ArchivePassword? password,
        ArchiveOperationControl? control, EngineProgressRelay relay) : NativeUpdateCallback, NativeUpdateFileCallback, RatioCallback, PasswordCallback, NativeArchiveInterop.NativeReadPassword, IDisposable
    {
        private readonly IProgress<EngineProgress>? progress = relay;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly TimeSpan started = control?.ActiveElapsed ?? TimeSpan.Zero;
        private readonly ArchiveProgressMetrics metrics = new();
        private readonly object gate = new();
        public Item[] Items { get; set; } = [];
        public Dictionary<uint, string> OriginalPaths { get; set; } = [];
        private string? currentItem;
        public void BeginItem(string? path) { lock (gate) { currentItem = path; Report(true); } }
        public int AffectedFiles { get; set; }
        public int AddedFiles { get; set; }
        public long AddedBytes { get; set; }
        public bool Encrypted { get; set; }
        private readonly List<NativeInputStream> streams = [];
        private readonly NativeReadSession.OpenCallback crypto = new(password, token, control: control);
        public ArchiveFailure? Error { get; private set; }
        private long written;
        private int encodedFiles;
        private TimeSpan previousReport = TimeSpan.MinValue;
        private EngineStage stage = EngineStage.Preparing;
        private int Stop()
        { try { control?.CheckStop(token); token.ThrowIfCancellationRequested(); return 0; } catch (OperationCanceledException) { return unchecked((int)0x80004004); } }
        // Reads include metadata, seeking and rereads; they are not completed work.
        public void Read(int bytes) { lock (gate) Report(); }
        public void Written(long bytes) { lock (gate) { written = bytes; Report(); } }
        public void Complete()
        {
            lock (gate)
            {
                try { Report(true); }
                catch (Exception exception) { relay.RecordObserverFailure(exception); }
            }
        }
        public void BeginWriting() { lock (gate) { stage = EngineStage.Writing; Report(true); } }
        public void WritingFinished() { lock (gate) Report(true); }
        public void BeginCommit() { lock (gate) { stage = EngineStage.Committing; currentItem = null; try { Report(true); } catch (Exception exception) { relay.RecordObserverFailure(exception); } } }
        private void Report(bool force = false)
        {
            var elapsed = control is null ? clock.Elapsed : control.ActiveElapsed - started;
            if (!force && previousReport != TimeSpan.MinValue && (elapsed - previousReport).TotalMilliseconds < 100) return;
            previousReport = elapsed;
            var work = metrics.Snapshot(elapsed);
            var writing = stage == EngineStage.Writing;
            progress?.Report(new EngineProgress(stage, work.Percentage,
                CurrentItem: currentItem,
                ProcessedBytes: work.Completed ?? 0, TotalBytes: work.Total ?? sourceSize + AddedBytes,
                BytesPerSecond: writing ? work.AverageSpeed : null,
                // Update OperationResult counts newly processed
                // input, not deleted items or retained encoded entries.
                EstimatedRemaining: writing ? work.Remaining : null, ProcessedItemCount: encodedFiles,
                TotalItemCount: AddedFiles > 0 ? AddedFiles : null,
                // Copy-only rewrites can report a zero codec ratio even while
                // retained data is written. Both are real output observations.
                OutputBytes: writing ? Math.Max(work.RatioOutput ?? 0, written) : written,
                HasMeasuredProcessedBytes: work.Completed.HasValue, MetricsScope: "mutation-native",
                IsRemainingEstimated: writing && work.Remaining.HasValue, OutputIncludesExistingEntries: true,
                ActiveElapsed: elapsed, IsPaused: control?.IsPaused == true,
                NativeWorkCompleted: work.Completed, NativeWorkTotal: work.Total,
                RatioInputBytes: work.RatioInput, RatioOutputBytes: work.RatioOutput, AffectedItemCount: AffectedFiles));
        }
        public int SetTotal(ulong total) { lock (gate) { metrics.SetTotal(checked((long)total)); Report(true); } return Stop(); }
        public int SetCompleted(nint completed) { var result = Stop(); if (result != 0) return result; lock (gate) { metrics.SetCompleted(completed == 0 ? null : Marshal.ReadInt64(completed)); Report(); } return 0; }
        public int SetRatioInfo(nint input, nint output)
        {
            var stop = Stop(); if (stop != 0) return stop;
            lock (gate)
            {
                metrics.SetRatio(input == 0 ? null : Marshal.ReadInt64(input), output == 0 ? null : Marshal.ReadInt64(output));
                // The SDK calls SetCompleted immediately after this ratio.
                // Do not spend the sample interval on the previous work count.
            }
            return 0;
        }
        public int GetUpdateItemInfo(uint index, out int newData, out int newProperties, out uint archiveIndex)
        { var item = Items[index]; newData = item.Source is not null ? 1 : 0; newProperties = item.Source is not null || !string.Equals(item.Entry.Path.Value, item.Destination.Value, StringComparison.Ordinal) ? 1 : 0; archiveIndex = item.Index; return Stop(); }
        public int GetProperty(uint index, uint property, out Variant value)
        {
            value = default;
            try
            {
                if (Items[index].Properties.TryGetValue(property, out var data)) value = data switch
                { string text => Variant.Text(text), bool flag => Variant.Boolean(flag), uint number => Variant.Number(number),
                    ulong number => Variant.Size(number), DateTimeOffset time => new Variant { Type = 64, Value = (ulong)time.UtcDateTime.ToFileTimeUtc() }, _ => default };
                return Stop();
            }
            catch (Exception exception) { return Marshal.GetHRForException(exception); }
        }
        public int GetStream(uint index, out SequentialInput? stream)
        {
            stream = null;
            try
            {
                var stop = Stop(); if (stop != 0) return stop;
                var item = Items[index]; BeginItem(item.Destination.Value);
                if (item.FilePath is null || item.Entry.IsDirectory) return 0;
                item.Source?.ValidateSource();
                var input = new NativeInputStream(File.OpenRead(item.FilePath), token, control: control, bytesRead: Read);
                lock (gate) { streams.Add(input); stream = input; } return 0;
            }
            catch (Exception exception) { return Marshal.GetHRForException(exception); }
        }
        public int GetStream2(uint index, out SequentialInput? stream, uint operation) => GetStream(index, out stream);
        public int ReportOperation(uint indexType, uint index, uint operation)
        {
            try
            {
                var stop = Stop(); if (stop != 0) return stop;
                var path = indexType == 1 ? OriginalPaths.GetValueOrDefault(index)
                    : indexType == 3 && index < Items.Length ? Items[index].Destination.Value : null;
                BeginItem(path);
                return Stop();
            }
            catch (Exception exception) { return Marshal.GetHRForException(exception); }
        }
        public int SetOperationResult(int result)
        {
            if (result != 0) { Error = new(ArchiveFailureCode.ArchiveDamaged, "归档更新数据校验失败"); return unchecked((int)0x80004004); }
            var stop = Stop(); if (stop != 0) return stop;
            lock (gate)
            {
                encodedFiles = Math.Min(encodedFiles + 1, AddedFiles);
                Report(true);
            }
            return 0;
        }
        public int CryptoGetTextPassword(out nint value) => crypto.CryptoGetTextPassword(out value);
        public int CryptoGetTextPassword2(out int defined, out nint value)
        { defined = Encrypted ? 1 : 0; value = 0; return Encrypted ? crypto.CryptoGetTextPassword(out value) : Stop(); }
        public void Dispose() { foreach (var stream in streams) stream.Dispose(); }
    }
}
