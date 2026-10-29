# API and ownership contract

The consumer entry points are `SevenZipNativeLibrary` (loading/catalogs),
`ArchiveInspector`, `ArchiveExtractor`, `ArchiveWriter`, `ArchiveUpdater` and
`ArchiveHasher`. Requests/results, paths, passwords, progress and output plans
are library-owned managed types. No host application, UI, CLI or COM object is returned.

## Entry identity and versions

`ArchiveEntryId` identifies one item in one `ArchiveRevision`. `ArchivePath` contains
only a name: equal paths can belong to different physical entries. Inspection
preserves those records, exposes `ById`, and returns a versioned snapshot.

Build selections with `snapshot.Select(ids)` or `snapshot.SelectAll()`; selecting a
directory expands its directory records and descendants. Pass the resulting
`ArchiveEntrySelection` to `ExtractItemsAsync` / `ComputeItemsAsync`. Updates use
`ArchiveItemUpdateRequest`: `Deleted` is an ID selection and each `ArchiveItemMove`
contains a source ID plus a new destination path. The previous path-selector update
and hash APIs have been removed.

Reinspect after an update or source-file replacement. Operations reject IDs from a
different revision with `ConflictDetected`; do not retain an ordinal as a persistent
identity. Revisions include source-volume file identities, size and timestamps.
New source paths and output names never identify an existing archive entry.

Explicitly deleting IDs and supplying replacement content at their destination can
replace one duplicate while retaining its same-named siblings, or replace an
explicit group. A name-only addition cannot choose among multiple existing files.
For extraction, map each source ID to a resolved output name in
`IArchiveExtractionOutputPlan`; same-named entries need a replace/skip/keep-both
policy on a case-insensitive output filesystem.

## Native boundary

Native read sessions, archive RCWs, imported archive interfaces, module export
pointers, Variant storage, hashers, stream bridges and SDK callback implementations
are internal. The `NativeArchiveInterop` container retains only the public callback
interfaces/ABI value type needed for COM callable wrappers; it is hidden from
IntelliSense and is not a consumer extension API. These declarations are not an
invitation to create or retain native archive handles. ABI signature tests exercise
them through the existing internal test access; there is no production-host friend
assembly or reverse managed dependency.

## Lifetimes and threading

- The process owns successfully loaded SDK modules for its lifetime. Failed loads
  are evicted for a later retry; modules with escaped native references are never
  unloaded. Discovery validates process architecture and required SDK exports.
- Each asynchronous operation owns and disposes its native session on one worker
  thread. Native objects never escape that worker or get shared between operations.
- A caller creates/disposes `ArchivePassword`. `ArchiveOperationContext` borrows it
  and an optional job-local `ArchiveOperationControl`; keep both alive until the
  operation ends. Header and data password overloads borrow two independent handles.
  A null data password deliberately does not fall back to the header password.
- Progress, output-plan and completion callbacks run synchronously on the native
  worker. They must not synchronously wait for that worker or directly touch UI.
  A caller can marshal updates to its own UI dispatcher.
- Pause/cancellation are cooperative at SDK checkpoints and output writes. No
  ambient context is consulted. Paused time is excluded from active elapsed time.
  Use one control per job; do not share one mutable control across unrelated jobs.

## Progress and results

The product-neutral progress types are `ArchiveProgressMetrics` and
`ArchiveProgressSnapshot`. This naming cleanup does not change the counter
formulas, pause/cancellation checkpoints or timing behavior.

`EngineProgress` carries native work/ratio counters and operation stages.
`EngineExtractionProgress` carries actual successful output writes. Unknown byte
totals, counts, speed and remaining time stay nullable; work percentages are not
physical write percentages. File counts for retained encoded data must not pretend
to be newly encoded files. Advisory progress failures are recorded as auxiliary
diagnostics, not substituted for the operation result.

Typed dependency/read exceptions describe failures before a usable native result
exists. Write/extract/update/hash outcomes carry typed errors and separate advisory
or cleanup diagnostics. Callers must check success and not infer it from a completed
progress bar. Default diagnostics omit arbitrary exception messages, credentials
and source paths; explicit recovery paths are result data, not safe log payloads.

## Outputs and commit points

- Default extraction validates Windows names, canonical paths, actual destination
  boundaries and reparse/link hazards before writing and publication. A temporary
  sibling is closed and SDK-validated before rename. Existing output is skipped
  unless overwrite was explicitly requested. Failure cleans only owned temporary
  files; previously published files are not an all-or-nothing extraction transaction.
- An `IArchiveExtractionOutputPlan` owns resolved conflicts, backup/rollback and
  final transaction commit. The library still checks actual output boundaries.
  Callbacks cannot grant permission to write outside the supplied destination.
- Creation writes private output; split publication validates staged volume
  identity/version and rolls back only volumes published by that attempt.
- Updates reuse encoded data with one `UpdateItems` plan. Standalone updates hold
  a writer lease and verify original identity before replacement; changed sources
  preserve recovery output. Caller-supplied `TransactionOutputPath` instead reserves
  an explicit host transaction boundary whose final commit belongs to that host.
- Only supported single-volume ZIP/7Z structures can be updated. Read-only formats,
  unsafe metadata and unsupported volumes are refused inside the engine itself.
  Malformed input is distinct from a valid read-only archive.

Source lists/creation settings are snapshotted at operation start. Source versions
and reparse status are checked again before opening streams. Paths, entries and
format catalogs are managed data; library loading is not a claim of support for
every theoretical operation in every DLL handler.

## Platform and distribution

.NET 9; Windows x86/x64 native sessions. ARM64 runtime/UI/signing/deployment are
not covered by this extraction milestone. The managed library has no package or
project dependencies and does not bundle native binaries. Supply an architecture-
matching compatible DLL explicitly; SFX modules/resources are opt-in caller inputs.
Packaging and public release are separate decisions; `IsPackable` remains false.

## ZIP update password semantics

Updating ZIP does not run a preliminary full decryption/CRC scan. Retained
entries are copied as encoded data, including during rename/delete operations.
Consequently update success does not prove that a supplied password decrypts
existing entries. ZIP encryption protects contents, not permission to modify
the archive directory.

There is no blanket requirement to supply a password merely because a ZIP has
encrypted entries. Added/replaced entries use the supplied session password;
using a different password can produce entries with different passwords in one
ZIP. Without a session password, new data is unencrypted. Retained entries keep
their original encryption. Header decryption or solid-data recompression can
still require credentials and fail through the SDK. Extraction and explicit
archive testing still validate decoding/integrity; their UI password prompts
are not removed. Existing session credential ownership/retention remains unchanged.
