# Detailed usage

Standalone Windows C# bindings for `7z.dll`.
Project and namespace: `SevenZip.CSharp`; target framework: .NET 9.

## Current scope

DLL discovery/loading, shared archive COM ABI and input/output streams,
native Variant, format/codec catalogs, hash ABI,
password memory ownership, explicit operation context, pause/timing, native
statistics, archive read sessions, format/volume policies, inspection, extraction,
integrity testing, decoded hashing, creation, single-pass updates, split output and
SFX resource handling are provided by this library. Native sessions,
RCWs, pointers and callback implementations are internal. Necessary COM callback
interface declarations are implementation details hidden from IntelliSense.
See the [API and ownership contract](API.md). No package is published.

The library has no application, UI, CLI or third-party managed dependency. It does
not bundle native binaries. Supply a compatible, process-architecture-matching
7-Zip DLL; the existing integration baseline uses 25.01. Windows is required at
runtime even though the managed project targets net9.0. Native archive sessions
currently preserve the existing Windows x86/x64 runtime boundary; ARM64 builds
must not be interpreted as verified ARM64 session support.

## Build and run

From the repository root:

```powershell
dotnet build SevenZip.CSharp.slnx
dotnet run --project samples/BasicUsage -- 'C:\sdk\7zip\7z.dll'
dotnet run --project samples/BasicUsage -- 'C:\sdk\7zip\7z.dll' 'C:\archives\sample.7z'
```

The sample loads the explicitly supplied DLL and prints its format catalog.
With an archive argument it also inspects and lists entries. It never starts an executable.

```csharp
using SevenZip.CSharp;

var library = SevenZipNativeLibrary.Load(dllPath);
var zip = library.FindFormat("zip");

var context = new ArchiveOperationContext(password: password, control: control, cancellationToken: token);
var snapshot = await ArchiveInspector.InspectAsync(dllPath, archivePath, context);
// snapshot can be null when none of the permitted handlers accepts the input.
// ArchiveReadException distinguishes damage, unsupported metadata and password errors.
```

Inspection returns immutable managed entry/physical-index snapshots, offsets,
source volumes and update support. No RCW escapes that operation. It rejects
parent traversal and duplicate canonical physical paths, and releases its native
source handles before returning. A host can supply an immutable copied
`allowedFormats` policy in the context; null uses the DLL catalog, not a host's
format list. Reported update support preserves the currently implemented ZIP/7Z update
boundary, not every SDK handler's theoretical ability to write.

## Extract, test and hash

```csharp
var extracted = await ArchiveExtractor.ExtractAsync(dllPath, archivePath, outputDirectory,
    context, overwrite: false,
    extractionProgress: new ActionProgress<EngineExtractionProgress>(value =>
        Console.WriteLine($"Written bytes: {value.ProcessedBytes}")));
var tested = await ArchiveExtractor.TestAsync(dllPath, archivePath, context);
var hashed = await ArchiveHasher.ComputeAsync(dllPath, archivePath, [ArchivePath.Root], "SHA256", context);
```

`EngineProgress` reports SDK work/ratio counters and phase statistics. They are
not interchangeable with physical bytes written. `EngineExtractionProgress`
separately reports successful output writes, including the first write, so
cooperative cancellation does not depend on the SDK's next completed-work callback.

Default disk extraction rejects unsafe Windows names, links/reparse paths,
type conflicts and output beyond the destination. It validates and closes a
temporary sibling before publishing each file by rename after SDK validation.
Existing files are skipped unless overwrite is explicit. Cancellation removes
only owned temporary output; previously published files are not globally rolled
back by this default per-file operation. No password retry or conflict UI is shown.

Advanced callers can provide `IArchiveExtractionOutputPlan` for resolved output
names and transactional publication. The host owns decisions, backup/rollback,
and final transaction commit. The engine still validates its actual output paths
before writing/publishing. Plan callbacks execute on the native worker. Separate
header and data password overloads borrow both handles, with null data deliberately
meaning missing rather than fallback to the header key.

Hashing feeds decoded data directly into native hashers without temporary
extraction. `ArchiveEngineOutcome`/`ArchiveFailure` distinguish cancellation,
unsupported operations, CRC/data errors and actionable I/O categories; advisory
progress/cleanup diagnostics do not silently replace a primary result. Public
callback interfaces in `NativeArchiveInterop` are not a consumer extension API.

## Create and update

```csharp
var created = await ArchiveWriter.CreateAsync(dllPath, inputDirectory,
    new ArchiveWriteOptions
    {
        ArchivePath = outputArchive,
        Format = ArchiveWriteFormat.SevenZip,
        Compression = new() { Level = 5 }
    }, context);

var updated = await ArchiveUpdater.UpdateAsync(dllPath, outputArchive,
    new ArchiveUpdateRequest
    {
        Deleted = [ArchivePath.From("obsolete.txt")],
        Moves = [new(ArchivePath.From("old.txt"), ArchivePath.From("renamed.txt"))],
        Sources = [ArchiveSourceItem.FromFileSystem(inputFile, ArchivePath.From("new.txt"))]
    }, context);
```

Source lists separate physical files from archive paths and validate source size,
timestamp, directory kind and reparse status before opening data streams. Default
root enumeration does not follow reparse points. A request snapshots mutable lists
before starting work. Compression options contain native settings only, never CLI
switches or serializable password strings. Encryption uses the borrowed context
password and `ArchiveEncryptionSettings`.

Creation supports ZIP/7Z and existing TAR/WIM/GZip/BZip2/XZ handlers within their
validated parameter limits; updates support writable, single-volume ZIP/7Z only.
`ArchiveSplitSettings` expresses bytes, not a textual command expression. SFX
settings accept an explicitly supplied module/icon/compiled version resource;
no stub is executed to inspect or edit it. Native modules are not bundled here.

Creation writes private same-volume output before publication. Split publication
checks volume identity/version without re-decoding payload and rolls back only
volumes successfully moved by that attempt. Updates retain encoded archive data
through one `UpdateItems` plan; they do not extract/re-add files to rename them.
Cancellation/failure cleans owned output and preserves the original archive.
Unexpected source changes retain recovery output instead of overwriting it.

The library's named writer lease prevents simultaneous library writers.
Application admission and browse-lease coordination remain host policy.
Advanced `TransactionOutputPath` is caller-owned transaction output: the host
must reserve it and perform its final source protection/commit. Independent
default updates perform their own source-identity check before replacement.
This is not global coordination with unrelated archive programs.

`ArchiveWriteResult` returns typed status/error, elapsed time, native HRESULT and
separate cleanup/advisory diagnostics; there are no standard-output/error streams.
Finalizing/committing notifications cannot turn published success into failure;
actual cancellation is still checked before publication. Detailed recovery paths
are result data, not default log payloads.

Modules/catalogs are shared for the process lifetime. Do not unload the module
while native objects can reference it. Native hashers are disposable and must
not be shared concurrently. Architecture/load failures remain typed exceptions.

## Tests and transition limits

```powershell
$env:SEVENZIP_CSHARP_DLL_PATH = 'C:\sdk\7zip\7z.dll'
dotnet test tests/SevenZip.CSharp.Tests
```

Discovery defaults to the supplied environment path or paths under
AppContext.BaseDirectory, never parent directories. Parent probing is explicitly
opt-in for host adapters. Configure discovery with `SEVENZIP_CSHARP_DLL_PATH`
or an explicit DLL path. No test binaries or SDK files are copied into this
project. Native tests require the supplied DLL, not an application source/project
reference or an incidental parent SDK.

`ArchivePassword` owns clearable memory and must be disposed by its creator.
`ArchiveOperationContext` borrows that password and an optional
`ArchiveOperationControl`; it does not dispose either. Controls are per job,
have no ambient Current, and check cancellation while waiting on pause.

The managed code, tests, examples and documentation use
[Apache License 2.0](../LICENSE.txt). See [license scope](../LICENSE-SCOPE.md)
and [NOTICE](../NOTICE).
Native binary redistribution remains subject to its independent upstream terms.
See [third-party notices](../THIRD-PARTY-NOTICES.md) before distributing native
components. Packing remains disabled.

## Passwords, pause and cancellation

```csharp
using var password = new ArchivePassword("example-password");
using var cancellation = new CancellationTokenSource();
var control = new ArchiveOperationControl();
var operation = new ArchiveOperationContext(password, control, cancellation.Token);
var task = ArchiveExtractor.ExtractAsync(dllPath, archivePath, outputDirectory, operation);
// From the caller's UI/events while task is running:
// control.SetPaused(true);  // only succeeds when CanPause is true
// control.SetPaused(false);
// cancellation.Cancel();   // also interrupts a paused checkpoint
var outcome = await task;
```

Keep borrowed handles alive until `task` completes. Do not block the native worker
inside a callback to wait for your UI; marshal notification instead.

## ZIP update password semantics

ZIP updates reuse unchanged encrypted bytes without decrypting or performing a
full integrity test. A supplied password does not
authenticate retained entries. Newly added/replaced entries use the supplied
password and can therefore differ from retained entries. Without a session
password, ZIP metadata updates proceed and newly added/replaced data is not
encrypted. Header decryption and solid-data
recompression can still require a password; missing credentials fail through the SDK.
Use extraction or an explicit archive test to verify passwords and payload integrity.
See the [API contract](API.md#zip-update-password-semantics).
