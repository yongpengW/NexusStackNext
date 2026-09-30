# 04 — BuildingBlocks.Infrastructure：消息基座

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 02, 03

## Answer

**产出（14 个源码文件 + 37 个测试）**

`BuildingBlocks.Application` 新增 `Ids/IIdGenerator.cs`；新建 `BuildingBlocks.Infrastructure`：

| 文件 | 内容 |
|---|---|
| `Ids/IdGeneratorOptions.cs`、`Ids/SnowflakeIdGenerator.cs` | 应用侧 ID 生成（ADR-0009） |
| `Events/EventEnvelope.cs` | 消息封套 + `MessagingErrors`。**封套里没有 CLR 类型**——链路上任何地方都没见过类型名，"换命名空间不断链"因此是结构必然 |
| `Events/IEventBus.cs` | 发布契约，**核心是失败语义** |
| `Events/IntegrationEventSerializer.cs` | System.Text.Json 实现（按运行时类型序列化） |
| `Events/EventTopology.cs` | 拓扑集中声明 + 校验 |
| `Events/OutboxEntry.cs` | 待投递记录（含状态迁移方法） |
| `Events/IOutboxStore.cs`、`Events/IInboxStore.cs` | 存储契约 |
| `Events/OutboxDeliveryOptions.cs`、`Events/OutboxPublisher.cs` | 投递器：退避重试、死信、单条失败不阻塞整轮 |
| `InfrastructureServiceCollectionExtensions.cs` | 显式注册；WorkerId 与投递策略由宿主传入 |

**验收标准**

| 验收项 | 结果 |
|---|---|
| 不可路由返回**失败**且不标记已投递 | ✅ `PublishPending_Unroutable_IsFailure_NotSilentSuccess` |
| 失败按退避重试；跨两次运行不重复投递已成功的 | ✅ `PublishPending_Failure_SchedulesRetry_AndSkipsUntilDue`、`PublishPending_SecondRun_DoesNotRepublishDeliveredEntries` |
| 超过上限进死信且不再重试 | ✅ `PublishPending_ExceedingMaxAttempts_GoesToDeadLetter_AndIsNeverRetried` |
| 同一 `(EventName, MessageId, Consumer)` 第二次返回 `false` | ✅ 见下方说明 |
| 拓扑拒绝重复 `(事件, 消费端)`；两消费端重试队列不同 | ✅ `Create_RejectsDuplicateSubscription`、`RetryQueues_DifferPerConsumer_EvenForTheSameEvent` |
| 换命名空间/类型名路由键不变 | ✅ 票据 03 的 `RoutingKey_IsStableWhenNamespaceAndTypeNameChange`（票据 04 的 `EventEnvelope` 直接复用同一命名规则） |
| ID 单调唯一、WorkerId 生效、时钟回拨抛错 | ✅ `SnowflakeIdGeneratorTests`（9 条） |
| **不新增对数据库或 broker 的依赖** | ✅ 全部用替身验证 |

**关于 Inbox 去重的处理方式（值得说明）**

`IInboxStore` 是一个接口，测替身等于测测试自己。所以我把它写成**可继承的契约**：

```csharp
public abstract class InboxStoreContract      // 5 条断言
public sealed class InMemoryInboxStoreContractTests : InboxStoreContract   // 参照实现，现在跑
```

票据 19 的 EF Core 实现只要继承 `InboxStoreContract` 并提供 `CreateStore()`，就**自动**接受同一套断言。
这把"消费端幂等"从一句口号变成了可机械继承的义务——比写一个针对假对象的测试有意义得多。

**被自己发现并修掉的设计缺陷**

第一版 `SnowflakeIdGenerator` 在同一毫秒内序列号用尽时"借用下一毫秒"。这个优化有个致命副作用：
借用后 `_lastTimestamp` 会跑到真实时钟**前面**，于是下一次调用立刻被自己的回拨检测判定为时钟回拨并抛错——
生成器在密集负载下会自己把自己锁死。

改成教科书版本：**回拨抛错、序列号耗尽也抛错**，并写明为什么不用"借用"（会让内部时间跑到真实时钟前）
也不用"自旋等待"（依赖真实时间，会让测试挂死）。序列号耗尽的阈值是单实例 400 万 ID/秒，
正常负载下不会触发；真触发了也应该是调用方退避，而不是让生成器偷偷造出一个偏离真实时间的 ID。

**反向验证（代码变异）**

把投递器的成功判定 `if (result.IsSuccess)` 反转为 `if (result.IsFailure)`：

```
失败! - 失败: 7，通过: 30
```

7 条红，其中包含 `PublishPending_Unroutable_IsFailure_NotSilentSuccess`——
本轮的头号承诺（不可路由不得被当成成功）确实被守住了。判定已还原。

**当前状态**：构建 0 警告 0 错误；测试 **94/94**（领域 28 + 应用 21 + 基础设施 37 + 架构 8）。
