# 54 — 【严重】OutboxPublisher 依赖两个零实现的端口，`dotnet run` 从未跑起来过

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 51

> 由用户问"配置中心的密钥写到哪"引出：填好密钥后启动，**进程 3 秒就退出**。

## 现象

```
System.AggregateException: Some services are not able to be constructed
  ServiceType: OutboxPublisher  Lifetime: Scoped
  Unable to resolve service for type 'IOutboxStore'
```

修掉之后立刻冒出第二个：

```
  Unable to resolve service for type 'IEventBus'
```

`AddNexusStackInfrastructure` 注册了 `OutboxPublisher`，而它需要的**两个端口在整个仓库里都没有实现**
（`IOutboxStore` 只在测试里有个 `FakeOutboxStore`，`IEventBus` 只在测试里有个 `FakeEventBus`）。

## 为什么这么久没被发现——这才是最值得记的一条

`ValidateOnBuild` 不在我们代码里（grep `ValidateOnBuild` 零命中），它是 .NET 的默认行为：
**只有 Development 环境才打开**。

而 **`dotnet run` 默认跑 Development**（有 `launchSettings.json`），
**我此前所有验证都是直接跑 DLL、环境是 Production**。

于是：

| 运行方式 | 环境 | 容器验证 | 结果 |
|---|---|---|---|
| 我做的所有验证 | Production | **关** | 照常启动，一切"正常" |
| `dotnet run`（README 里写的方式） | Development | **开** | **从未跑起来过** |

**验证方式与被验证方式不一致，形成了一个系统性的盲区。**
这不是"某次疏忽"，而是我反复用同一种跑法，于是同一种缺陷永远照不到。

## 修法：三条路，只有一条对

| 方案 | 结果 |
|---|---|
| 在 `AddNexusStackInfrastructure` 里继续注册 `OutboxPublisher` | 依赖缺失，Development 下起不来 |
| 塞一个**空实现的事件总线** | **比不注册更糟**——它会把每条消息静静丢掉，而"发了但没人收到"正是这个项目反复批判的失败模式（评审 04 F2） |
| **把投递器做成显式开关**（本方案） | 没有总线就没有投递器，事实清楚 |

具体：

- 新增 `InMemoryOutboxStore`——**补上一处不对称**：收件箱一直有 `InMemoryInboxStore`，发件箱却一直没有对应物，那处不对称就是这个缺陷的来源。
- `OutboxPublisher` 从 `AddNexusStackInfrastructure` **移出**，改为独立的 `AddNexusStackOutboxDelivery()`。
  "有没有投递循环"取决于"有没有能投递的总线"，那是**宿主级的显式决定**，不该藏在基础设施注册里。
- 总线接上之后（票据 21），宿主再调 `AddNexusStackOutboxDelivery()`。

## 补上了那条本该存在的检查

新增 `tests/BuildingBlocks.Infrastructure.Tests/CompositionTests.cs`，
用**与宿主相同**的验证选项（`ValidateOnBuild` + `ValidateScopes`）构建容器：

| 测试 | 断言 |
|---|---|
| `AddNexusStackInfrastructure_ProducesAConstructibleContainer` | 只调基础设施注册，容器必须能建起来 |
| `ApplicationAlone_ProducesAConstructibleContainer` | 只调应用层，同样 |
| `OutboxDelivery_WithoutAnEventBus_FailsLoudly` | **没有总线就注册投递器，必须当场失败**（而不是等到第一条消息要发时静静丢掉） |

**反向验证**：把 `OutboxPublisher` 加回 `AddNexusStackInfrastructure`（即原来的错误状态）
→ `AddNexusStackInfrastructure_ProducesAConstructibleContainer` **精确变红**；还原 → 3/3 绿。

## 验证

```
dotnet run（Development，之前一直失败的方式）
  第 2 秒就绪
  [INF] 已接入 AgileConfig：AppId=nexusstack_platform，节点=<服务器IP>:8010
  [INF] Hosting environment: Development
  [INF] Content root path: ...\src\Hosts\NexusStackNext.PlatformHost
  /api/identity/ -> identity
```

**AgileConfig 真实拉取成功**（顺带确认）：客户端缓存 5626 字节，
里面是**合并后**的配置——基座的 RabbitMQ / Redis / Serilog 级别开关 + 平台自己的连接串 / WorkerId。
继承在**服务端**完成，所以 `env` 文件里只需要宿主自己的 AppId 与密钥。

## 这件事对"验证"的教训

本仓此前反复出现过"**一个不会失败的检查**"（票据 44、45、53）。
这次是它的镜像形态：**一个永远照不到缺陷的验证方式**。

两者都指向同一件事：**验证要覆盖"用户实际会怎么用"，而不只是"我怎么方便怎么跑"。**
README 里写的是 `dotnet run`，而我从头到尾跑的是 `dotnet <dll>`。
