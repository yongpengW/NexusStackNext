# 52 — 评审 08 的遗留：不可重现的验证与两处命名

Status: resolved
Type: task
Labels: needs-info
Blocked by: 51

> 评审 08 查出六条，四条当场修了。这里是**不适合当场做**的那两条，
> 加上一条**必须记下来**的缺口。
>
> **一与三已完成（本届）。二的第一项已完成；第二项需要你先做一个判断。**

## 一、SignalR 的验证不可重现（最重要）—— ✅ 已完成

**`tests/HostIntegration.Tests` 建好了**，`dotnet test` 现在真的守着那个 hub。

### 做法：生产代码一行没改

`ClusterReachabilityProbe` 是 `sealed` 的，但**不用动它**——它走
`IHttpClientFactory.CreateClient(nameof(ClusterReachabilityProbe))`，一个**具名**客户端。
测试只要给那个名字换上一个可控的 `HttpMessageHandler`：

```csharp
builder.ConfigureServices(services =>
    services.AddHttpClient(nameof(ClusterReachabilityProbe))
        .ConfigurePrimaryHttpMessageHandler(() => Health));
```

真正的探针原样跑起来，被测的就是生产里那段代码。
**这比"为了测试把探针抽成接口"更好**：不必让生产代码为测试让步。

### 断言的契约（两个方向都测）

```
1. 连接 hub，把"后端可达"翻成不可达
2. 等到一条 clusterStatus，healthy=false        ← 证明「变化时会推」
3. 静置 9 秒（三个轮询周期），断言没有第二条      ← 证明「不变时不推」
4. 翻回可达，等到第二条，healthy=true           ← 反向再证一次
5. 再静置，断言没有第三条
```

**只测第 3 步是不够的**：一个彻底坏掉的广播器同样不会推。
第 2 步与第 4 步是它的阳性对照，两边都过，"只在变化时推"才算被证明。

### 反向验证

把 `ClusterStatusBroadcaster` 里的 `if (HasChanged(last, current))` 改成 `if (true)`：

```
失败! - 失败: 1，通过: 0，总计: 1，持续时间: 6 s
  [FAIL] ... PushesOnChange_AndStaysQuietWhileUnchanged
  静置 9 秒（三个轮询周期）里状态没变，却又推了一条。
```

还原后 24 秒通过。**报错信息正好指向坏掉的那条契约。**

### 一个我自己踩的坑（值得记）

第一版测试把 `ControllableHealthHandler.Healthy` 的初值设成 `false`，
然后把"设为 false"当成一次变化——**那是空操作**，广播器读到的本来就是"不可达"。

而它与"这个可控处理器**根本没接上**"（真实探针打不到 `127.0.0.1:5191`，同样报不可达）
**症状完全一样**：收不到推送。两种原因，一个现象。

初值改成 `true` 之后，第一次翻转必然是真实变化，于是这个测试**顺带证明了
可控处理器确实替换掉了真实探针的网络调用**。

### 关于标记类型

加了 `GatewayHostMarker`（`src/Gateway/.../GatewayHostMarker.cs`）。
**平台宿主的标记类型暂时没加**——目前没有测试需要它，加了就是"假设的缝"。
等第一个同时引用两个宿主的测试出现时再加，那时它会真的被 `Program` 歧义逼出来。

## 二、两处命名

### 第一项：`src/Hosting/` → `src/Composition/` —— ✅ 已完成

```
src/Hosting/NexusStackNext.Hosting/     →  src/Composition/NexusStackNext.Composition/
tests/Hosting.Tests/                    →  tests/Composition.Tests/
```

改了 14 个文件（命名空间、ProjectReference、slnx、文档）。
模板端到端：45 个项目、构建 0 警告 0 错误、`Hosting` 残留 0。

**清理时踩到的**：`ArchitectureInvariantTests` 一开始红了三条，
不是改名漏了，而是**旧的 `NexusStackNext.Hosting.dll` 还躺在 `bin` 里**
被那次扫描捡到。删掉 92 个 `bin`/`obj` 重建后全绿——
**架构测试正确地捕捉到了改名**，只是先捕捉到了编译产物的残留。

### 第二项：`NexusStackNext.<Ctx>.Api` → `.<Ctx>.Endpoints` —— ✅ 已完成

票据原来写着"**先确认读的人怎么理解，再改**"。你就是读的人，所以这一项停在这里。

| 现在 | 改名候选 |
|---|---|
| `NexusStackNext.Identity.Api` | `NexusStackNext.Identity.Endpoints` |
| `NexusStackNext.Identity.Api` | `NexusStackNext.Identity.Http` |

**支持改的理由**：它们已经是 `Microsoft.NET.Sdk` **类库**（票据 51 合并的结果），
`.Api` 在 .NET 习惯里通常指"那个能跑的服务"。你已经因此困惑过一次——
VS 的启动项目下拉里曾有 7 个候选，其中 5 个是这些**根本启动不了**的 `.Api`。
那个下拉里的 `launchSettings.json` 已经删了，但**项目名本身还在误导**。

**支持不改的理由**：`.Api` 描述的是"这个上下文的 HTTP 面"，那仍然准确；
而"能不能启动"由 `Sdk` 与有没有 `Program.cs` 决定，不是由名字决定。

**用户选了 `.Endpoints`**（取舍见上）。5 个上下文 × 目录/csproj/命名空间/ProjectReference/slnx/文档，共 18 个文件。

改名暴露了 `check-tracker.ps1` 里一条**早已过期**的规则——见票据 63。

## 三、`AddFilesLocalDiskStorage` 在注册期做 I/O —— ✅ 已完成

**改法**：`LocalDiskFileStore` 的构造期不再做任何 I/O，
目录创建变成一个**显式的启动步骤** `FileStoreInitializer`。

```
之前：AddFilesLocalDiskStorage 里 new LocalDiskFileStore(...) → 构造函数 CreateDirectory
      错误信息：Unhandled exception（看不出是哪个键）
之后：注册期纯内存；启动步骤 EnsureCreated()
      错误信息：文件存储根目录不可用：'C:\...\sub'。请检查配置键 Files:StorageRoot。
```

**快速失败本身保留**——AGENTS.md 里记着那是有意行为（与"运行期掉线 → 就绪检查 503"互补）。
变的是它现在**说得出原因**。配置键名 `Files:StorageRoot` 提成常量
`FilesModule.StorageRootConfigurationKey`，只写一次。

启动步骤放在**模块**（`Files.Api`）而不是基础设施（`Files.Infrastructure`）——
"这个路径来自哪把键"是模块的知识，而错误信息要说出它就得在那里组装。
顺带避免了给一个叶子类库加两个 `Microsoft.Extensions.*` 包引用。

### 端到端验证

把 `Files__StorageRoot` 指到一个"父级是文件"的路径：

```
进程还活着吗: False                                     ← 快速失败保留
Unhandled exception. System.InvalidOperationException:
  文件存储根目录不可用：'C:\...\nsn-not-a-dir\sub'。请检查配置键 Files:StorageRoot。
  ---> System.IO.IOException: Cannot create 'C:\...\nsn-not-a-dir' because a file
       or directory with the same name already exists.
```

## 验收

- ✅ SignalR 的验证变成 `dotnet test` 里会红的东西，反向验证已做
- ✅ 两处改名都完成
- ✅ `Files` 存储路径无效时的报错能指明是 `Files:StorageRoot`
