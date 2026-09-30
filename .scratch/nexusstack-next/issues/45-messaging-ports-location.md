# 45 — 消息端口上移到 Application（评审 07 第二节）

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 04

> 整份引用矩阵里**唯一一处** `Application → Infrastructure`，原因就是三个端口住错了程序集。
> 而守住这条规则的测试**此前根本不存在**——所以那处破例一直没被发现。

## Answer

**上移到 `BuildingBlocks.Application/Events/`（契约侧）**

| 文件 | 为什么属于契约侧 |
|---|---|
| `IEventBus` | 端口 |
| `IInboxStore` | 端口 |
| `IOutboxStore` | 端口 |
| `EventEnvelope` + `MessagingErrors` | **端口两侧共用的消息形状** |
| `OutboxEntry` | 端口签名里的数据类型 |
| `IIntegrationEventSerializer` | 端口（"变的是序列化格式"） |

**留在 `BuildingBlocks.Infrastructure`（实现侧）**

`SystemTextJsonIntegrationEventSerializer`、`EventTopology`、`OutboxDeliveryOptions`、
`OutboxPublisher`、`InMemoryInboxStore`、`RabbitMq/*`

**这一步顺带拆开了一个文件**：`IntegrationEventSerializer.cs` 里原本同时放着接口与实现，
现在接口在 Application、实现在 Infrastructure。这正是这道缝该有的形状。

## 结果

```
BuildingBlocks.Application           干净（BuildingBlocks.Domain）
Auditing.Application                 干净（Auditing.Domain, BuildingBlocks.Application）
Files / Identity / Platform / Scheduling.Application   全部干净
```

`Auditing.Application.csproj` 去掉了 `BuildingBlocks.Infrastructure`，
`Auditing.Infrastructure` 补一个 `using` 即可——**没有一行业务代码改动**。

## 【重要】我第一版的测试是"不会失败的测试"

新加的 `ApplicationAssemblies_MustNotReferenceInfrastructure` 第一次写完，
我照例做反向验证——**把引用注回去，测试照样全绿。**

原因：C# 只为**实际用到**的程序集发出 AssemblyRef。
`Auditing.Application` 已经不使用任何 Infrastructure 类型了，
所以把 `ProjectReference` 加回去**在编译产物里不留任何痕迹**。

而那道测试原本读的是编译产物，于是它抓的是"真的用了"，
抓不到"埋了一颗随时可以用的枪"——**后者才是更常见、更危险的形态**。

修法：**两层都查**——

| 层 | 查什么 | 手段 |
|---|---|---|
| 类型级 | 真的引用了基础设施的程序集 | 读编译产物（`PEReader`） |
| **工程级** | 埋着一个没被使用的 `ProjectReference` | 读 `csproj` |

补上第二层之后重做反向验证：**精确变红，只有那一条。** 改动字节级还原。

**这条教训与票据 44 的"两个方向都要验"是同一类**：
一个只验证了一侧的保护，会给人一种已经守住了的错觉。

## 当前状态

构建 0 警告 0 错误；架构测试 **10/10**（新增 1 条）；全量测试 **420/420**。
`AGENTS.md` 已把"端口在里、实现在外"写成规则，并注明**两层都要查**的理由。
