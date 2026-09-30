# 65 — 测试工程命名一半带前缀、一半不带

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: —

> 本轮我在自己的命令里**连续三次**把测试工程路径写错，于是去数了一遍。

## 现象

```
Architecture.Tests.csproj                                  不带前缀
Auditing.Application.Tests.csproj                          不带前缀
NexusStackNext.Auditing.Domain.Tests.csproj                带前缀   ←
BuildingBlocks.Application.Tests.csproj                    不带前缀
BuildingBlocks.Domain.Tests.csproj                         不带前缀
NexusStackNext.BuildingBlocks.Infrastructure.IntegrationTests.csproj  带前缀
BuildingBlocks.Infrastructure.Tests.csproj                 不带前缀
NexusStackNext.Composition.Tests.csproj                    带前缀   ←
Files.Application.Tests.csproj                             不带前缀
NexusStackNext.Files.Domain.Tests.csproj                   带前缀   ←
Gateway.Routing.Tests.csproj                               不带前缀
NexusStackNext.HostIntegration.Tests.csproj                带前缀   ←
Identity.Application.Tests.csproj                          不带前缀
Identity.Domain.Tests.csproj                               不带前缀
NexusStackNext.Identity.IntegrationTests.csproj            带前缀   ←
NexusStackNext.IntegrationSupport.csproj                   带前缀
NexusStackNext.IntegrationSupport.Tests.csproj             带前缀
Platform.Application.Tests.csproj                          不带前缀
NexusStackNext.Platform.Domain.Tests.csproj                带前缀   ←
Scheduling.Application.Tests.csproj                        不带前缀
NexusStackNext.Scheduling.Domain.Tests.csproj              带前缀   ←
NexusStackNext.TestSupport.csproj                          带前缀
```

**22 个工程里 10 个带前缀、12 个不带**，而且看不出规律——
`Identity.Domain.Tests` 不带而 `Files.Domain.Tests` 带，`*Application.Tests` 全不带而
`*Domain.Tests` 一半带。

## 为什么值得修

它不是风格问题，而是**它让"写对路径"变成一件要靠记忆的事**。

代价在本轮就发生了：我在同一个会话里**连续三次**把路径写错，
而每次的失败都是 `MSB1009: 项目文件不存在`——那条错误只说"没有这个文件"，
不说"你是不是把前缀搞反了"，所以每次都要重新去 `ls` 一遍。

**一个有规律的名字只需记住规律；一个没规律的名字每次都要查。**

## 建议

统一成**不带前缀**（多数派，且 `src/` 下的工程名本身就是 `NexusStackNext.X`——
测试工程的程序集名与目录名不必重复那个前缀）：

| 现在 | 改为 |
|---|---|
| `NexusStackNext.Auditing.Domain.Tests` | `Auditing.Domain.Tests` |
| `NexusStackNext.Composition.Tests` | `Composition.Tests` |
| `NexusStackNext.Files.Domain.Tests` | `Files.Domain.Tests` |
| `NexusStackNext.HostIntegration.Tests` | `HostIntegration.Tests` |
| `NexusStackNext.Identity.IntegrationTests` | `Identity.IntegrationTests` |
| `NexusStackNext.IntegrationSupport` | `IntegrationSupport` |
| `NexusStackNext.IntegrationSupport.Tests` | `IntegrationSupport.Tests` |
| `NexusStackNext.Platform.Domain.Tests` | `Platform.Domain.Tests` |
| `NexusStackNext.Scheduling.Domain.Tests` | `Scheduling.Domain.Tests` |
| `NexusStackNext.TestSupport` | `TestSupport` |

**注意两处不是纯改名**：
- `IntegrationSupport` 与 `TestSupport` 是**库**不是测试工程，被多方引用；
- `AssemblyName` 与 `RootNamespace` 也要跟着改，否则 `InternalsVisibleTo` 之类的
  按程序集名的引用会断。

改完要跑全量（含模板端到端），因为这是 10 个工程 × 引用方的一次性改动。

## 它是怎么来的

`NexusStackNext.*.Domain.Tests` 与两个 `IntegrationSupport*` 是本会话里我先后新建的，
而其余是更早按 `.Tests` 建的。**每次新建时我都在看它旁边那个**——
于是先建的一批各带各的前缀，后来照着建的一批又各自照抄。

这正是"局部一致、全局不一致"的典型成因：**每个决定单看都合理，但它们没有同一个依据。**

## 第 9 轮完成

### 做了什么

**22 个测试工程全部统一成"文件名 = 目录名、零前缀"。**
11 个 csproj 改名，15 个文件（csproj + slnx）的引用跟着改。

```
带前缀的还剩        0
文件与目录不同名的   0
```

### 一件让这件事变简单的事

**目录名本来就已经是对的**——不一致只在 **csproj 文件名**上。
于是不需要搬目录，只改文件名与引用。目录搬迁是这类改动里最容易出事的部分
（相对路径、`..\` 层数、被 `.gitignore` 忽略的文件），而这次完全绕开了。

### 验证方式是编译器

批量的正则/文本替换在本会话里已经咬过我四次（括号、`using`、方法签名都被弄坏过）。
但**改名不一样**：引用错了就**构建不过**。

所以这次的流程是"改 → `dotnet build` → 全量测试 → 模板端到端"，
而不是"改 → 人工检查"。**有编译器兜底的批量改动是安全的；没有的那种才危险。**

### 一处刻意留下的不一致

`RootNamespace` **没改**——它仍然是 `NexusStackNext.*`，与 `src/` 下的工程一致。

理由：`RootNamespace` 是**代码命名空间的默认值**，不是程序集身份；改它意味着
编辑那 11 个工程里的每一个 `.cs` 文件（几千行），换来的是零功能收益。
而票据原文担心的那条——"`InternalsVisibleTo` 之类的按程序集名的引用会断"——
**不成立**：全仓没有任何 `InternalsVisibleTo`（已核实）。

**`AssemblyName` 则删掉了**，让它跟着文件名走——于是程序集名与工程名一致，
测试输出里不会再出现 `NexusStackNext.HostIntegration.Tests.dll` 这种与工程名对不上的东西。

### 这次改名的直接收益

本会话里我因为路径写错而失败过**四次**（`MSB1009: 项目文件不存在`）。
那条错误只说"没有这个文件"，不说"你是不是把前缀搞反了"。

**一个有规律的名字只需记住规律；一个没规律的名字每次都要查。**
现在 22 个工程都遵守同一条规律。
