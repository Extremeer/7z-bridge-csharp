<div align="center">

# 7z-bridge-csharp

**直接连接 7-Zip 原生引擎的 C# 桥接库，无需 CLI 子进程**

简体中文 | [English](../README.md)

![.NET](https://img.shields.io/badge/.NET-9-512BD4?logo=dotnet&logoColor=white)
![Platform](https://img.shields.io/badge/Platform-Windows-0078D4)
![Native runtime](https://img.shields.io/badge/Native_runtime-x86_%7C_x64-555555)

[API 与所有权](API.md) · [详细用法](USAGE.md) · [运行示例](../samples/BasicUsage)

</div>

**7z-bridge-csharp** 通过 C# 直接调用 `7z.dll`，提供压缩包检查、解压、完整性测试、哈希、创建与更新能力。
这是可独立使用的类库，不依赖 UI 框架或托管 CLI 包装器。
项目、程序集及命名空间仍为 **`SevenZip.CSharp`**。

> [!IMPORTANT]
> 本仓库提供托管源码，不捆绑 7-Zip 原生二进制，也没有发布 NuGet 包。
> 使用时需要自行提供与进程架构匹配的兼容 DLL。当前仍禁用打包（`IsPackable=false`）。

---

## 🗂️ 功能概览

| 能力 | 入口 | 范围 |
| :--- | :--- | :--- |
| DLL 加载与能力目录 | `SevenZipNativeLibrary` | 模块查找、架构检查、格式与编解码器目录 |
| 压缩包检查 | `ArchiveInspector` | 托管条目快照、分卷信息及更新能力 |
| 解压与完整性测试 | `ArchiveExtractor` | 输出路径校验、类型化错误及自定义输出计划 |
| 解码数据哈希 | `ArchiveHasher` | 直接使用原生哈希器，无需临时解压 |
| 创建压缩包 | `ArchiveWriter` | 在已支持的参数范围内创建 ZIP、7Z、TAR、WIM、GZip、BZip2 和 XZ |
| 更新压缩包 | `ArchiveUpdater` | 对可写单卷 ZIP/7Z 添加、替换、删除及重命名 |
| 操作控制 | `ArchiveOperationContext` / `ArchiveOperationControl` | 密码所有权、协作式暂停／取消及原生统计 |

可读取的格式取决于调用方提供的 DLL 及格式策略。格式出现在目录中，不代表所有操作、结构和参数均受支持。
分卷输出及调用方提供的 SFX 资源通过显式写入设置使用；本仓库不包含原生模块。

## 🚀 快速开始

### 1. 环境准备

- 原生操作需要 Windows。当前原生会话支持边界为 x86/x64；ARM64 能够构建不代表已通过 ARM64 实机运行验证。
- 支持 .NET 9 及 `.slnx` 解决方案格式的 .NET SDK。
- 自行提供与进程架构匹配的 `7z.dll`；现有集成基线使用 **7-Zip 25.01**。

托管类库目标为 `net9.0`，没有第三方托管包或项目依赖，不启动 `7z.exe` 或其他 CLI 进程。

### 2. 构建与引用

在仓库根目录执行：

```powershell
dotnet build SevenZip.CSharp.slnx
```

仅构建类库：

```powershell
dotnet build src/SevenZip.CSharp/SevenZip.CSharp.csproj
```

应用引用 `src/SevenZip.CSharp/SevenZip.CSharp.csproj` 即可。
调用方可按自己的应用结构安排仓库目录。
当前不提供 NuGet 安装命令。

### 3. 运行示例

将路径替换为实际的 DLL 和压缩包：

```powershell
dotnet run --project samples/BasicUsage -- 'C:\sdk\7zip\7z.dll'
dotnet run --project samples/BasicUsage -- 'C:\sdk\7zip\7z.dll' 'C:\archives\sample.7z'
```

第一条命令打印格式目录；第二条还会检查压缩包并列出条目。两者都不会启动原生可执行程序。

## 💻 使用方式

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

使用输出前必须检查返回的操作结果；`await` 返回或进度条完成不等于成功。
类库不显示密码提示或冲突对话框，回调通知需要由调用方调度至自己的 UI。

更多示例与行为说明：

- [解压、完整性测试与哈希](USAGE.md#extract-test-and-hash)
- [创建、更新、分卷输出与 SFX](USAGE.md#create-and-update)
- [密码、暂停与取消](USAGE.md#passwords-pause-and-cancellation)
- [API 生命周期、所有权及提交边界](API.md)

## 🛡️ 重要边界

> [!NOTE]
> ZIP 更新不会预先执行全量密码／CRC 校验。保留条目保持原有加密状态；新增或替换的数据使用本次传入的密码，
> 没有传入密码则不加密，因此同一 ZIP 可能包含密码不同的条目。文件头解密或固实数据重压缩仍可能需要密码。
> 验证数据应使用解压或显式完整性测试。详见 [ZIP 密码语义](API.md#zip-update-password-semantics)。

- **默认解压按文件提交。** 输出路径经过校验，文件先暂存再发布；未明确允许覆盖时跳过已有文件。
  取消会清理本次临时输出，不会全局回滚此前已发布的文件。宿主可通过自定义输出计划提供事务策略。
- **写入具有明确的提交边界。** 创建先写私有输出；默认更新在替换前检查源身份，发现意外源变化时保留恢复输出。
  提供 `TransactionOutputPath` 时，最终保护与提交由宿主负责。写入租约不能协调其他压缩软件。
- **统计口径不能混用。** 原生工作量／比例进度与实际输出写入量不同。未知统计保持未知，清理及附加诊断与主要结果分离。
- **资源所有权必须明确。** 上下文只借用密码和控制器，创建者负责保持存活及释放自己拥有的资源。
  回调运行在原生工作线程，不应从该线程同步等待 UI；仍有原生对象存活时不能卸载模块，原生哈希器不能并发共享。

[详细使用说明](USAGE.md)保留完整的解压、写入、模块查找及恢复约束。
原生会话和 COM 对象不作为消费者公开 API。

## 🧪 测试

在仓库根目录指定与被测进程架构匹配的真实 DLL：

```powershell
$env:SEVENZIP_CSHARP_DLL_PATH = 'C:\sdk\7zip\7z.dll'
dotnet test tests/SevenZip.CSharp.Tests
```

默认从配置路径或 `AppContext.BaseDirectory` 查找 DLL，不向父目录探测。
通过 `SEVENZIP_CSHARP_DLL_PATH` 或显式 DLL 路径配置查找。测试自行生成输入，不捆绑应用测试素材或原生二进制。
当前验证范围不包含 ARM64 实机、UI、签名和部署验收。

## 📚 文档

- [详细使用说明与示例](USAGE.md)
- [API 与所有权合同](API.md)
- [最小运行示例](../samples/BasicUsage)
- [第三方声明](../THIRD-PARTY-NOTICES.md)

## 🙏 致谢与许可

本项目通过调用方提供的 `7z.dll` 使用 7-Zip 引擎。

Copyright 2026 Marz。本项目的托管代码、测试、示例及文档统一采用 [Apache License 2.0](../LICENSE.txt)。
详见[许可范围](../LICENSE-SCOPE.md)和 [NOTICE](../NOTICE)。

原生二进制许可独立适用，详见[第三方声明](../THIRD-PARTY-NOTICES.md)。本仓库尚未发布 NuGet 包。
