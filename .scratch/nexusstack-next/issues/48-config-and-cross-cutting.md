# 48 — 配置与横切关注点：决策已记，实现为零

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 01

> 用户问"集成好 AgileConfig 了嘛"。答案是**没有，一处都没有**——
> 而且查下去发现：锁定的一组技术选型里，**没有一项真正接上了**。

## 实测（改之前）

| 技术 | 包引用 | 真实实现 | 说明 |
|---|---|---|---|
| AgileConfig | 0 | 0 | 全仓唯一的 "Agile" 命中是 `.template.config/template.json` 里的 **排除规则** |
| Serilog + Seq | 0 | 0 | 第一次查报"15 处命中"，那是 `Seq` 误匹配了 `SequenceBits` / `SequenceEqual` |
| Redis | 0 | 0 | 2 处命中全是**注释** |
| SignalR | 0 | 0 | 零 |
| RabbitMQ | 0 | 拓扑规划（纯逻辑） | csproj 里那 2 处 "RabbitMQ" 是**注释** |

六个宿主一个 `appsettings.json` 都没有。

## 这是我的报告方式出的问题

票据 42 的"**保留了什么**"里写着"技术选型：Serilog + Seq、AgileConfig、RabbitMQ、Redis"，
而"**没有做什么（诚实清单）**"里这五项**只出现了 RabbitMQ 一项**。

**"决定了"与"做完了"之间的落差，被我放进了"保留了什么"。**
与并发令牌（票据 44）同一失效形态，这次覆盖四项技术。

## Answer

### 配置优先级（ADR-0006 要求"一次定清楚"）

```
环境变量  >  AgileConfig  >  appsettings.{Environment}.json  >  appsettings.json
```

**有测试守着**（`tests/Hosting.Tests/ConfigurationPriorityTests.cs`，4 条）——
因为它写错了不会报错，只会静默取到错的值。

机制是"把 AgileConfig 来源插到第一个环境变量来源之前"，因为 `AddAgileConfig` 是**追加**的，
直接用它会让配置中心盖过环境变量，与既定优先级相反。

**反向验证做了两个方向**：把插入改成追加 → `EnvironmentVariables_OverrideAgileConfig`
与位置断言**同时变红**；还原后恢复。

### 降级路径（实跑验证）

| 场景 | 结果 |
|---|---|
| 未配置 `AppId` / `Nodes` | 启动，`/health/live` 200，日志明确说明走了降级路径 |
| `Nodes` 指向**不可达**地址 | **照常启动**，`/health/live` 200、`/openapi/v1.json` 200 |

配置中心是**运行时**的便利，不该变成**启动时**的单点。

### Serilog（实跑验证）

控制台 sink **永远打开**——参照仓库的 `UseSerilog` 收到空配置就等于没有 sink，
而那恰恰发生在最需要日志的时候。Seq 只在配置了 `Serilog:Seq:ServerUrl` 时接入。

**变异验证**：把 `appsettings.json` 的 `MinimumLevel` 从 `Information` 改成 `Warning`
→ INFO 行数从 **6 变成 0**；还原后回到 6。这同时证明了 `appsettings.json` **真的被加载并生效**
（第一次验证时工作目录不对，`Content root` 指向了仓库根，那次其实没读到文件——所以重做了一次）。

### SignalR（真客户端验证）

Hub 在**网关**上，不在业务服务上：客户端只到得了边缘（部署不变量）。
放任何一个上下文里，它都会变成第二个对外入口。

推送时机是**后端可达性变化时**——复用了就绪检查的探测，
于是"探测"这件事有了**两个消费者**，才真正成为一道缝。
**只在变化时推**，理由与聚合版本号的"空操作不自增"是同一条：
不断重复同一内容的通道会让客户端学会忽略它，于是真正要传达的那一次也会被忽略。

```
[READY] state=Connected
[RECV #1] healthy=False clusters=4 unreachable=[platform（1 个目标都不可达）]
[RECV #2] healthy=True clusters=4 unreachable=[]
```

50 秒、3 秒一探，**只发了 2 条**——"只在变化时推"这条契约被实测到了。

### 一个新增的工程与它的边界

`src/Hosting/NexusStackNext.Hosting`：六个宿主**完全相同**的那几行收在一处。
它**不是**不变量 8 禁止的"隐藏组装"——宿主仍逐行声明自己由什么组成，
而随宿主不同的东西（健康检查、路由、存储根目录）都留在各自 `Program.cs` 里。
**只允许宿主引用它**，Domain / Application / Infrastructure 一律不得。

## 当前状态

构建 0 警告 0 错误；测试 **424/424**（新增 4）；架构 **10/10**；
解决方案 **43** 个项目；`README.md` 新增「## 配置」小节。

## 仍未做

**AgileConfig 的真实拉取**——需要你的服务器地址与凭据。见票据 49。
