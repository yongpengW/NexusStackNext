# 47 — 测试工程整理（评审 07 第三、六节）

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 39

## 一、`Contexts.Tests` 拆掉了

**改之前**：Identity 有独立的领域测试工程，其余四个上下文的领域测试全挤在 `Contexts.Tests` 里——
而那句"当某个上下文开始有真实业务时，它应当拆出独立的测试工程"是我自己写在它的 csproj 里的。
**五个上下文都早有真实业务了。**

**改之后**：

| 新工程 | 内容 |
|---|---|
| `Platform.Domain.Tests` | `PlatformTests` + `GlobalSetting` 版本测试 |
| `Auditing.Domain.Tests` | `AuditingTests` + `AuditEntry` 版本测试 |
| `Files.Domain.Tests` | `FilesTests` + `FileStoreSeamTests` + `StoredFile` 版本测试 |
| `Scheduling.Domain.Tests` | `ScheduledTask` 版本测试 |

**拆的过程中发现一处归错**：`SchedulingTests.cs` 测的是 `TaskRegistry` + `IScheduledTaskStore`——
那是**应用层**，不是领域。它去了 `Scheduling.Application.Tests`，不是新建的领域工程。
如果不看它引用了什么就按文件名归类，它会进错工程。

## 二、三个替身收进 `tests/TestSupport`

| 替身 | 改之前 | 改之后 |
|---|---|---|
| `SequentialIdGenerator` | 4 处 | **1 处** |
| `FixedClock` | 5 处 | **1 处** |
| `MutableClock` | 2 处 | **1 处** |

各调用点原本的起始值/时刻**逐个保留**（`new SequentialIdGenerator(9000)` 等），
不是简单地换成默认值——那会悄悄改掉测试的输入。

`MutableClock` 原来有两份**形状不同**的定义：`BuildingBlocks.Infrastructure.Tests` 那份多了
`Advance()`。合并时把它并进共享版本，而不是留两份。

**顺带去掉了冗余 API**：那份还有个 `Set(x)`，与可写的 `UtcNow` 属性完全重复。
我改的是唯一那个调用点，而不是往共享替身上再加一个重复方法。

## 三、一处保持不动的决定

`Program.cs` 里的 `Failure(Error)` **四份不合并**——它是每个服务自己的 HTTP 契约。
这个决定连同理由写进了 `AGENTS.md`，否则它看起来像违反不变量 7。

## 四、我自己的一次误报，值得记下来

改完三个本地替身后跑测试，我的求和脚本报"**410 条，比 420 少了 10**"。
这违反验收条件，所以我停下来查——结果是**我的脚本解析错了**：
`-replace '.*通过:\s*(\d+).*','$1'` 在多行输出上取错了字段。
改用按 `总计:` 逐个累加后，**420 条，一条没少**。

**教训**：验收脚本本身也会说谎。差点就把一次成功的重构当成"丢了 10 条测试"——
反过来说，如果我没查，就会把一个错误结论写进这份结案。

## 验收

| 项 | 结果 |
|---|---|
| `Contexts.Tests` 是否还存在 | **否** |
| 测试工程数 | 12 → **15** |
| 解决方案项目数 | 37 → **41** |
| 测试总数 | **420**，一条没少 |
| 构建 | 0 警告 0 错误 |
| 每个替身的定义数 | **各 1 处** |
