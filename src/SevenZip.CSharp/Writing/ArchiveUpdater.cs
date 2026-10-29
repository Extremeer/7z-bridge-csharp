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
    public static Task<ArchiveEngineOutcome> UpdateItemsAsync(string libraryPath, string path, ArchiveItemUpdateRequest request,
        ArchiveOperationContext? context = null, IProgress<EngineProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        context ??= new();
        var operation = context;
        var plan = request.Snapshot();
        var relay = new EngineProgressRelay(progress);
        return Task.Run(() => Run(libraryPath, path, plan, operation, relay));
    }

    private static ArchiveEngineOutcome Run(string libraryPath, string path, ArchiveItemUpdateRequest request,
        ArchiveOperationContext context, EngineProgressRelay relay)
    {
        try
        {
            var result = RunProtected(libraryPath, path, request, context, relay);
            return result with { Diagnostic = EngineProgressRelay.CombineDiagnostics(result.Diagnostic, relay.ObserverDiagnostic) };
        }
        catch (OperationCanceledException) { return Fail(ArchiveFailureCode.OperationCancelled, "操作已取消"); }
        catch (UnauthorizedAccessException) { return Fail(ArchiveFailureCode.PermissionDenied, "无法获取压缩包写入权限"); }
        catch (ArchiveWriterBusyException) { return Fail(ArchiveFailureCode.ArchiveBusy, "压缩包正在被另一任务修改"); }
        catch (IOException exception) { return Fail(EngineFailureClassifier.FromIOException(exception), "无法锁定或读取压缩包"); }
    }

    private static ArchiveEngineOutcome RunProtected(string libraryPath, string path, ArchiveItemUpdateRequest request,
        ArchiveOperationContext context, EngineProgressRelay relay)
    {
        var token = context.CancellationToken;
        using var writer = NativeArchiveWriteLock.AcquireAsync(path, token).GetAwaiter().GetResult();
        var original = request.TransactionOutputPath is null ? ArchiveFileIdentity.CaptureIfExists(path) : null;
        var output = new PlanOutput(request.TransactionOutputPath is not null && !File.Exists(request.TransactionOutputPath)
            ? request.TransactionOutputPath : (request.TransactionOutputPath ?? path) + ".native-plan-" + Guid.NewGuid().ToString("N"));
        ArchiveEngineOutcome result;
        try { result = RunCore(libraryPath, path, request, token, output, original, context, relay); }
        catch { Cleanup(output, relay); throw; }
        var cleanup = Cleanup(output, relay);
        if (cleanup is null) return result;
        return new(true, result.Error is { } primary ? primary with
        { Message = primary.Message + "; " + cleanup.Message,
          Diagnostic = EngineProgressRelay.CombineDiagnostics(primary.Diagnostic, "Cleanup=" + cleanup.Code + "; " + cleanup.Diagnostic) } : cleanup,
          relay.ObserverDiagnostic);
    }
    private static ArchiveFailure? BuildItemPlan(NativeReadSession session, ArchiveInspector.Snapshot snapshot,
        ArchiveItemUpdateRequest request, Callback callback, CancellationToken token, EngineProgressRelay relay,
        out List<Item> items, out HashSet<ArchiveEntryId> deleted, out HashSet<ArchiveEntryId> moved)
    {
        items = [];
        deleted = request.Deleted?.Ids.ToHashSet() ?? [];
        moved = request.Moves?.Select(move => move.Source).ToHashSet() ?? [];
        if (request.Deleted is not null && !snapshot.IsCurrent(request.Deleted)
            || moved.Any(id => id.Revision != snapshot.Revision || !snapshot.ById.ContainsKey(id)))
            return new(ArchiveFailureCode.ConflictDetected, "条目选择已失效，请刷新归档后重试");
        if (request.Moves is not null && moved.Count != request.Moves.Count)
            return new(ArchiveFailureCode.InvalidOperation, "同一归档条目不能同时移动到多个目标");
        if (deleted.Overlaps(moved)) return new(ArchiveFailureCode.ConflictDetected, "不能同时删除和移动同一归档条目");
        var destinations = request.Moves?.ToDictionary(move => move.Source, move => move.Destination) ?? [];
        var finalNames = new Dictionary<string, ArchivePath>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in snapshot.PhysicalEntries)
        {
            token.ThrowIfCancellationRequested();
            var entry = pair.Value;
            if (deleted.Contains(entry.Id)) continue;
            var renamed = destinations.TryGetValue(entry.Id, out var destination);
            var next = renamed ? destination! : ArchivePath.From(entry.Path.Value);
            if (next.IsRoot) return new(ArchiveFailureCode.InvalidOperation, "不能将归档条目重命名为根目录");
            var invalid = WindowsOutputNamePolicy.ValidateEntry(next, next.Value);
            if (invalid is not null) return invalid;
            // Moving both members of an existing duplicate pair preserves that pair.
            // Two different original names must never newly acquire the same destination.
            if (finalNames.TryGetValue(next.Value, out var prior)
                && !string.Equals(prior.Value, entry.Path.Value, StringComparison.OrdinalIgnoreCase))
                return new(ArchiveFailureCode.ConflictDetected, "目标路径已存在：" + next.Value);
            finalNames.TryAdd(next.Value, entry.Path);
            var properties = new Dictionary<uint, object?>();
            if (renamed) foreach (var id in new uint[] { 3, 6, 7, 9, 10, 11, 12, 15, 19, 21, 28, 53 })
                properties[id] = id == 3 ? next.Value : session.GetProperty(pair.Key, id);
            items.Add(new(pair.Key, entry, next, properties));
        }
        if (request.Sources is not null || request.StagingRoot is not null)
        {
            var sources = request.Sources ?? ArchiveSourceEnumerator.EnumerateDirectory(request.StagingRoot!, token, relay);
            var explicitReplacementPaths = deleted.Select(id => snapshot.ById[id].Path).ToHashSet();
            var groups = items.GroupBy(item => item.Destination.Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
            if (sources.Any(source => groups.TryGetValue(source.Path.Value, out var matches)
                && matches.Length > 1 && matches.Any(item => !item.Entry.IsDirectory) && !explicitReplacementPaths.Contains(source.Path)))
                return new(ArchiveFailureCode.ConflictDetected, "更新名称对应多个归档条目，请先明确要替换的条目");
            var physical = items.ToDictionary(item => (item.Destination, item.Index));
            var byName = groups.ToDictionary(group => group.Key, group => group.Value[0], StringComparer.OrdinalIgnoreCase);
            foreach (var source in sources)
            {
                token.ThrowIfCancellationRequested(); source.ValidateSource();
                var next = source.Path;
                var invalid = WindowsOutputNamePolicy.ValidateEntry(next, next.Value);
                if (invalid is not null) return invalid;
                byName.TryGetValue(next.Value, out var old);
                if (explicitReplacementPaths.Contains(next)) old = null; // Explicit IDs selected the replaced records; retain same-named siblings.
                if (old is not null)
                {
                    if (source.IsDirectory != old.Entry.IsDirectory)
                        return new(ArchiveFailureCode.ConflictDetected, "文件与目录路径冲突：" + next.Value);
                    if (source.IsDirectory || !request.AddOptions!.Overwrite || request.AddOptions.WriteMode == ArchiveUpdateMode.Update
                        && old.Entry.Modified is { } prior && source.Modified <= prior) continue;
                }
                var index = old?.Index ?? uint.MaxValue;
                var entry = new ArchiveEntryInfo(next, source.IsDirectory, source.Size, 0, source.Modified,
                    source.IsDirectory ? ArchiveItemAttributes.Directory : ArchiveItemAttributes.None,
                    !source.IsDirectory && callback.Encrypted, null);
                var properties = new Dictionary<uint, object?> { [3] = next.Value, [6] = source.IsDirectory,
                    [7] = (ulong)source.Size, [9] = (uint)source.Attributes, [12] = source.Modified };
                if (old is not null) physical.Remove((old.Destination, old.Index));
                var item = new Item(index, entry, next, properties, source.SourcePath, source);
                physical[(next, index)] = item; byName[next.Value] = item;
            }
            items = physical.Values.ToList();
            callback.AddedFiles = items.Count(item => item.FilePath is not null && !item.Entry.IsDirectory);
            callback.AddedBytes = items.Where(item => item.FilePath is not null && !item.Entry.IsDirectory).Sum(item => item.Entry.UncompressedSize);
        }
        return null;
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
    private static ArchiveEngineOutcome RunCore(string libraryPath, string path, ArchiveItemUpdateRequest request,
        CancellationToken token, PlanOutput plan, ArchiveFileIdentity? original, ArchiveOperationContext context, EngineProgressRelay relay)
    {
        var transactionOutput = request.TransactionOutputPath;
        var output = plan.Path;
        using var controlScope = context.Control?.EnterNativeOperation();
        try
        {
            token.ThrowIfCancellationRequested();
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
                var failure = BuildItemPlan(session, snapshot, request, callback, token, relay,
                    out var items, out var deletedIds, out var movedIds);
                if (failure is not null) return new(true, failure);
                bool deletedItem(ArchiveEntryInfo entry) => deletedIds.Contains(entry.Id);
                bool movedItem(ArchiveEntryInfo entry) => movedIds.Contains(entry.Id);
                var filePaths = items.Where(i => !i.Entry.IsDirectory).Select(i => ArchivePath.From(i.Destination.Value)).ToHashSet();
                foreach (var item in items)
                    for (var parent = item.Destination.Parent; !parent.IsRoot; parent = parent.Parent)
                        if (filePaths.Contains(parent)) return Fail(ArchiveFailureCode.ConflictDetected, "文件与目录路径冲突：" + parent.Value);
                callback.Items = items.ToArray();
                callback.OriginalPaths = snapshot.PhysicalEntries.ToDictionary(pair => pair.Key, pair => pair.Value.Path.Value);
                callback.AffectedFiles = callback.AddedFiles + snapshot.Entries.Count(entry =>
                    !entry.IsDirectory && (deletedItem(entry) || movedItem(entry)));
                using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    plan.Owned = true;
                    callback.BeginWriting();
                    // Notify deleted names while constructing
                    // the update plan; these are not completed-file increments.
                    foreach (var entry in snapshot.PhysicalEntries.Values.Where(deletedItem))
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
