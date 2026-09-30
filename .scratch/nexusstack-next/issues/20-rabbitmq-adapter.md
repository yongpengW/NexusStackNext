# 20 — RabbitMQ 拓扑规划与消费决策

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 04

> **2026-09-29 拆分说明**：原票据要求用「假 `IChannel`」验证发布失败语义与通道重建。
> 落地时发现两件事：`IChannel` 是 60+ 成员的大接口，为它写替身的成本远高于它证明的东西；
> 而"不可路由是否算失败"取决于客户端的发布确认跟踪行为，**只有真 broker 能验证**。
> 因此按可验证性拆开：
> - 本票据：**拓扑规划与消费决策**——纯逻辑，可完整离线验证（含 review/04 发现 1 的回归）。
> - 票据 21：**通道绑定与消费循环**——真实 `IChannel` 上的胶水，运行时行为待 broker。

## Comments

### local

**Answer**

**产出（2 个源码文件 + 25 个测试）**

| 文件 | 内容 |
|---|---|
| `Events/RabbitMq/TopologyPlan.cs` | `TopologyPlan` / `QueuePlan` / `BindingPlan` + `RabbitTopologyPlanner.Plan()` |
| `Events/RabbitMq/ConsumePolicy.cs` | `DeliveryOutcome` / `DeliveryDecision` / `ConsumePolicy` |

**为什么把这两块做成纯函数**

它们是容易写错的部分，而胶水（几行 `BasicPublishAsync` / `BasicNackAsync`）不容易写错。
把"该不该重试、重试进哪个队列"从"怎么 Nack"里拆出来，前者就能被完整验证，后者留给真 broker。
参照仓库恰恰是**在这里**出错的——它的重试路由逻辑没有测试，一个缺陷活了很久，
连作者自己那份五轮检查的文档都把这块标成"通过最终审查，无修改建议"。

**验收标准**

| 原验收项 | 结果 |
|---|---|
| 拓扑声明与 `EventTopology` 一致（无隐式队列） | ✅ `PlanDeclaresNoQueueOutsideTheTopologyDeclaration` |
| 一个消费端失败时，重试只回到**该消费端**的队列 | ✅ 见下（规划层面已证明；运行时见票据 21） |
| 假 `IChannel`：`BasicReturn` → `PublishAsync` 返回失败 | → **移至票据 21**（该行为由客户端确认跟踪决定，无 broker 无法验证） |
| 假 `IChannel`：通道关闭后能重建 | → **移至票据 21** |
| 真实 broker 端到端 | → **移至票据 21**（等你的 broker） |

**review/04 发现 1 的修复（本票据的核心）**

参照仓库（`EventSubscriber.cs:255`）把重试队列的死信寄回主交换机，路由键用**事件名**；
而主交换机上每个处理器各有一个以该事件名绑定的队列（`:196`、`:294-297`）。
于是一个处理器失败，同事件的**所有**处理器都会重收一遍——重复执行的代价被放大成 N 倍。

新拓扑的做法：

- 消费队列：`x-dead-letter-exchange` 不参与，仅按**事件名**绑定到主交换机（接收正常投递）。
- 每个重试档位一个独立队列：`x-message-ttl` = 该档延迟，
  `x-dead-letter-exchange` = **默认交换机（空字符串）**，
  `x-dead-letter-routing-key` = **该消费端自己的队列名**。
  AMQP 规定默认交换机按队列名精确投递，因此重试只会回到它自己那一个队列。

**反向验证（把参照仓库的 bug 原样注入）**

把 `x-dead-letter-routing-key` 从"消费队列名"改回"事件名"——即参照仓库的原始写法：

```
失败! - 失败: 2，通过: 60
  ✗ RetryQueue_DeadLettersBackToItsOwnConsumerQueue_NotToTheEventName
  ✗ TwoConsumersOfTheSameEvent_NeverShareARetryQueue
```

两条断言精确命中，就是守着这个缺陷的那两条。改动已还原。

**消费决策的语义（纯函数，可穷举验证）**

| 输入 | 处置 |
|---|---|
| 处理成功（即使此前失败过 2 次） | `Ack` |
| 第 1 次失败 | 进最短档位 `…retry.5s` |
| 第 2 次失败 | 进次长档位 `…retry.30s` |
| 档位用尽 | `DeadLetter` |
| 没有配置档位 | 直接 `DeadLetter` |

尝试次数通过消息头 `nexusstack-attempts` 传递，解析对 `int` / `long` / `byte[]` / 字符串都宽容。
**解析失败按 0 处理**，宁可多重试一次，也不要因为一个头值读不出来就把消息直接扔进死信。

**当前状态**：构建 0 警告 0 错误；测试 **211/211**。
