<div align="center">

# 7z-bridge-csharp

**A C# bridge to the native 7-Zip engine — no CLI subprocesses**

[简体中文](docs/README_zh.md) | English

![.NET](https://img.shields.io/badge/.NET-9-512BD4?logo=dotnet&logoColor=white)
![Platform](https://img.shields.io/badge/Platform-Windows-0078D4)
![Native runtime](https://img.shields.io/badge/Native_runtime-x86_%7C_x64-555555)

[API & ownership](docs/API.md) · [Detailed usage](docs/USAGE.md) · [Example](samples/BasicUsage)

</div>

**7z-bridge-csharp** calls `7z.dll` directly from C# for archive inspection,
extraction, integrity testing, hashing, creation and updates. It is a standalone
library without a UI framework dependency or a managed CLI wrapper.
Its project, assembly and namespace remain **`SevenZip.CSharp`**.

> [!IMPORTANT]
> This repository provides managed source, not a bundled 7-Zip distribution or
> a published NuGet package. Supply a compatible DLL matching your process
> architecture. Package generation remains disabled (`IsPackable=false`).

---

## 🗂️ Features

| Capability | Entry point | Scope |
| :--- | :--- | :--- |
| DLL loading & catalogs | `SevenZipNativeLibrary` | Module discovery, architecture checks, formats and codecs |
| Archive inspection | `ArchiveInspector` | Managed entry snapshots, volumes and update support |
| Extraction & integrity tests | `ArchiveExtractor` | Validated disk output, typed errors and custom output plans |
| Decoded-data hashing | `ArchiveHasher` | Native hashers; no temporary extraction |
| Archive creation | `ArchiveWriter` | ZIP, 7Z, TAR, WIM, GZip, BZip2 and XZ within supported parameter limits |
| Archive updates | `ArchiveUpdater` | Add, replace, delete and rename in writable single-volume ZIP/7Z |
| Operation control | `ArchiveOperationContext` / `ArchiveOperationControl` | Password ownership, cooperative pause/cancellation and native statistics |

Read-format availability depends on the supplied DLL and the caller's format
policy. A handler appearing in the catalog is not a promise that every operation,
structure or option is supported. Split output and caller-supplied SFX resources
use explicit write settings; native modules are not bundled here.

## 🚀 Getting started

### 1. Prerequisites

- Windows for native operations. The current native-session boundary is x86/x64;
  ARM64 builds do not imply verified ARM64 runtime support.
- A .NET SDK supporting .NET 9 and the `.slnx` solution format.
- An explicitly supplied, architecture-matching `7z.dll`. The existing
  integration baseline is **7-Zip 25.01**.

The library targets `net9.0`, has no third-party managed package or project
dependencies, and does not launch `7z.exe` or another CLI process.

### 2. Build and reference

From the repository root:

```powershell
dotnet build SevenZip.CSharp.slnx
```

For a library-only build:

```powershell
dotnet build src/SevenZip.CSharp/SevenZip.CSharp.csproj
```

Reference `src/SevenZip.CSharp/SevenZip.CSharp.csproj` from your application.
Choose the repository layout that suits your application. There is no NuGet
installation command at this stage.

### 3. Run the example

Replace the paths with your own DLL and archive:

```powershell
dotnet run --project samples/BasicUsage -- 'C:\sdk\7zip\7z.dll'
dotnet run --project samples/BasicUsage -- 'C:\sdk\7zip\7z.dll' 'C:\archives\sample.7z'
```

The first command prints the format catalog. The second also inspects the archive
and lists its entries. Neither launches a native executable.

## 💻 Usage

```csharp
using SevenZip.CSharp;

var dllPath = @"C:\sdk\7zip\7z.dll";
var archivePath = @"C:\archives\sample.7z";
var outputDirectory = @"C:\archives\extracted";
var context = new ArchiveOperationContext();

var snapshot = await ArchiveInspector.InspectAsync(dllPath, archivePath, context);
if (snapshot is null)
    throw new InvalidOperationException("No permitted archive handler accepted the input.");

foreach (var entry in snapshot.Entries)
    Console.WriteLine($"{entry.Path.Value}: {entry.UncompressedSize} bytes");

var extracted = await ArchiveExtractor.ExtractAsync(
    dllPath, archivePath, outputDirectory, context, overwrite: false,
    extractionProgress: new ActionProgress<EngineExtractionProgress>(value =>
        Console.WriteLine($"Written bytes: {value.ProcessedBytes}")));
```

Check the returned operation outcome before using its output. Completion of an
`await` or a progress bar does not establish success. The library does not show
password prompts or conflict dialogs; marshal callbacks to your own UI.

More examples and behavior details:

- [Extraction, integrity tests and hashing](docs/USAGE.md#extract-test-and-hash)
- [Creation, updates, split output and SFX](docs/USAGE.md#create-and-update)
- [Passwords, pause and cancellation](docs/USAGE.md#passwords-pause-and-cancellation)
- [API lifetime, ownership and commit points](docs/API.md)

## 🛡️ Important boundaries

> [!NOTE]
> ZIP updates do not perform a full password/CRC precheck. Retained entries keep
> their original encryption; added/replaced data uses the supplied password, or
> is unencrypted if no password is supplied. One ZIP can contain entries with
> different passwords. Header decryption or solid-data recompression can still
> require credentials. Use extraction or an explicit integrity test to validate
> payloads. See [ZIP password semantics](docs/API.md#zip-update-password-semantics).

- **Extraction is per-file by default.** Output paths are validated and files are
  staged before publication. Existing files are skipped unless overwrite is
  explicit. Cancellation cleans owned temporary output, not all previously
  published files. Host-controlled output plans can provide transaction policy.
- **Writing has explicit commit boundaries.** Creation stages private output;
  default updates check source identity before replacement. Unexpected source
  changes preserve recovery output. With `TransactionOutputPath`, the host owns
  final protection and commit. Writer leases do not coordinate unrelated tools.
- **Counters are not interchangeable.** Native work/ratio progress differs from
  actual output writes. Unknown metrics remain unknown; cleanup/advisory
  diagnostics remain separate from the primary result.
- **Ownership matters.** Contexts borrow passwords and controls; their creator
  keeps them alive and disposes owned resources. Callbacks run on the native
  worker. Do not synchronously wait for UI from that worker, unload modules with
  live native objects, or share native hashers concurrently.

The [detailed usage guide](docs/USAGE.md) preserves the full extraction, writing,
discovery and recovery constraints. Native sessions and COM objects are not a
public consumer API.

## 🧪 Tests

From the repository root, supply a real DLL matching the test process architecture:

```powershell
$env:SEVENZIP_CSHARP_DLL_PATH = 'C:\sdk\7zip\7z.dll'
dotnet test tests/SevenZip.CSharp.Tests
```

Discovery uses the configured path or `AppContext.BaseDirectory`, not parent
directories by default. Configure discovery with `SEVENZIP_CSHARP_DLL_PATH`
or supply an explicit DLL path. Tests generate their own inputs; no application
test assets or native binaries are bundled. ARM64 runtime, UI, signing and
deployment are not covered by the current validation scope.

## 📚 Documentation

- [Detailed usage and examples](docs/USAGE.md)
- [API and ownership contract](docs/API.md)
- [Minimal runnable example](samples/BasicUsage)
- [Third-party notices](THIRD-PARTY-NOTICES.md)

## 🙏 Acknowledgements and license

Uses the 7-Zip engine through a caller-supplied `7z.dll`.

Copyright 2026 Marz. The project's managed code, tests, examples and documentation
use [Apache License 2.0](LICENSE.txt). See [license scope](LICENSE-SCOPE.md)
and [NOTICE](NOTICE).

Native binary licensing is independent; see [third-party notices](THIRD-PARTY-NOTICES.md).
No NuGet package is published by this repository.
