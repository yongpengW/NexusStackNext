# 60 — launchSettings 用随机端口，VS 里 F5 直接得到坏系统

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 59

> 用户问："我用 VS 的话直接启动网关项目就可以是吧"
> 答案是**不行**，而且查证时发现 F5 本身也是坏的。

## 两个问题

### ① 只启动网关不够（这是用户问的那件事）

平台宿主是**后端**，网关是**边缘**。只启动网关，得到的是一个没有后端的边缘：

| 请求 | 结果 |
|---|---|
| `/health/live` | 200（进程活着） |
| `/health/ready` | **503**（cluster 不可达） |
| `GET /api/identity` | **502**（转发失败） |
| `/openapi/v1.json` | 只剩网关自己的 3 条，`complete=false` |

**两个进程都要启动。** 好在"只有一半"这件事是**看得见**的：
就绪检查 503、来源状态 `complete=false`——这正是票据 59 把状态暴露出来的理由。

### ② 更糟的：`launchSettings.json` 里的端口是随机的

```
PlatformHost:  https://localhost:58473;http://localhost:58474
Gateway:       https://localhost:54199;http://localhost:54200
```

而网关的 `routes.json` 里写死了：

```json
{ "name": "primary", "address": "http://127.0.0.1:5191" }
```

**所以即便两个都 F5 起来，网关也会把请求转发到空气。**
这两个文件是 `dotnet new` 脚手架生成的随机端口，从来没跟路由表对过。

**这类问题的共同点**：它们都不影响构建、不影响测试，
只影响"**照文档把东西跑起来**"——而那是用户做的第一件事。

## 修法

`launchSettings.json` 改用**约定端口**，与 `routes.json`、README、AGENTS.md 一致：

| 宿主 | applicationUrl | launchUrl |
|---|---|---|
| PlatformHost | `http://127.0.0.1:5191` | `scalar/v1` |
| Gateway | `http://127.0.0.1:5190` | `scalar/v1` |

顺带把 `launchBrowser` 指向 `/scalar/v1`：F5 之后直接看到 API 参考界面，
而不是一个 404 的根路径。也去掉了 https——仓库里没有配证书，
而 `routes.json` 与文档用的都是 http，多一个监听地址只会多一处对不上的地方。

## ③ 查证时又发现：VS 的下拉里原本有 **7 个**候选，其中 5 个是坏的

模板生成物里有 **7 个 `launchSettings.json`**，不是 2 个：

```
T60Probe.Gateway         http://127.0.0.1:5190      ← 真能启动
T60Probe.PlatformHost    http://127.0.0.1:5191      ← 真能启动
T60Probe.Auditing.Api    https://localhost:54193…   ← 类库，启动不了
T60Probe.Files.Api       https://localhost:54194…
T60Probe.Identity.Api    https://localhost:54195…
T60Probe.Platform.Api    https://localhost:54201…
T60Probe.Scheduling.Api  https://localhost:54202…
```

那五个 `*.Api` 是**票据 51 合并前的遗留**——合并之后它们成了
`Microsoft.NET.Sdk` **类库**、`Program.cs` 已删，**根本不可启动**，
而 `Properties/launchSettings.json` 原封不动地留着。

**这直接回答用户的问题**："我在 VS 里启动哪个？"——
下拉里会列出 7 个，其中 5 个是死的。用户没有理由知道哪 5 个是死的。

**修法**：删掉那 5 个文件与空掉的 `Properties/` 目录。

```
剩余 launchSettings:
  NexusStackNext.Gateway        http://127.0.0.1:5190
  NexusStackNext.PlatformHost   http://127.0.0.1:5191
```

**这条比端口那两条更值得记**：它是"合并成类库"这个动作的**未清理残留**。
合并时改了 `csproj`、删了 `Program.cs`、改了 `slnx`——
**但没人想到那些项目还带着一份"我要怎么被启动"的声明**。
残留物不会报错，只会让人困惑。

## 验证

```
A. 只启动网关（用 dotnet run，走 launchSettings）
   网关 /health/live   -> 200      ← 约定端口生效，不需要 --urls
   网关 /health/ready  -> 503
   GET /api/identity   -> 502
   聚合文档路径数: 3

B. 再启动平台宿主
   平台宿主 /health/live -> 200
   网关 /health/ready    -> 200
   GET /api/identity     -> 200
   聚合文档: complete=True  来源 gateway(3), platform-host(20)
```

**注意 A 里那三个数字就是"只启动一半"的完整症状**——
它们让这件事可诊断，而不是"页面打不开"。

## VS 里怎么同时启动

`launchSettings.json` **不能**声明"一个解决方案里哪些项目一起启动"——
那是 VS 的解决方案级设置，存在用户目录（`.vs/`），不进仓库。

所以在 VS 里需要手动设一次：

1. 解决方案资源管理器 → 右键**解决方案** → 属性
2. 通用属性 → 启动项目 → **多个启动项目**
3. 把 `NexusStackNext.PlatformHost` 和 `NexusStackNext.Gateway` 都设为「启动」

这一次设置是**每台机器**的，所以它写进了 README 而不是某个配置文件里。
