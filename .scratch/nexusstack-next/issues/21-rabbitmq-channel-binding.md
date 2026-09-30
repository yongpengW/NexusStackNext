# 21 — RabbitMQ 通道绑定与消费循环

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 20

## **阻塞点已解除**（第 10 轮实测）

`nexusstack_api.json` 里的 RabbitMQ **是真的、可用的**。用 `RabbitMQ.Client 7.2.2` 实连验证：

```
✅ 已连接            端点 amqp://<服务器IP>:5672（vhost=/nexusstack）
✅ 拓扑声明成功      交换机 / 队列 / 绑定
✅ 发布 / 取回消息
✅ 不可路由被拒绝    RabbitMQ.Client.Exceptions.PublishReturnException
                     312 NO_ROUTE Exchange: … Routing Key: …
```

**最后那条正是本票说"替身证明不了"的行为**：开了发布确认与确认跟踪 + `mandatory: true` 之后，
不可路由的消息会让发布**抛异常**，而不是静默成功。参照仓库那里是只记日志后照样 ACK
（`EventPublisher.cs:36-41/505-510`）——**"以为发出去了、其实没有任何人收到"**。

Redis（6379）与 PostgreSQL（5432）同样可达。所以本票不再有任何环境阻塞。

### 实测带回来的一条约束（只有真 broker 会告诉你）

第一次声明队列直接失败：

```
541 INTERNAL_ERROR - Feature `transient_nonexcl_queues` is deprecated.
By default, this feature is not permitted anymore.
```

**临时的、非独占的队列被这台 broker 拒绝了。** 队列必须 `durable: true` **或** `exclusive: true`。

这条会直接影响 `RabbitMqTopologyBootstrapper`：`RabbitTopologyPlanner.Plan()` 的产物里
如果按"短期队列"设计（durable=false + exclusive=false），声明会**当场失败**——
而用替身测试永远发现不了，因为替身照单全收。

## 原始阻塞点（保留，供追溯）

本票是**胶水层**：把票据 20 已证明的纯逻辑接到真实 `IChannel` 上。其中两条验收**只有真 broker 能验证**：

- 「不可路由 → 发布失败」取决于客户端的**发布确认跟踪**（`CreateChannelOptions.PublisherConfirmationsEnabled` +
  `PublisherConfirmationTrackingEnabled`，不可路由的 mandatory 消息会让发布任务以 `PublishException`
  且 `IsReturn = true` 失败）。这个行为无法用替身有意义地证明——替身只会证明我自己写的行为。
- 「通道关闭后重建并继续工作」本质上是与 broker 的生命周期交互。

请提供一组可用的 RabbitMQ 连接信息（票据 14 一起给即可）。

**能先做的**：代码可以对着 `RabbitMQ.Client 7.2.2` 编译（本机缓存已有），
编译能验证 API 名称与签名正确，但**不能**验证运行时行为。本票在拿到 broker 之前不开工，
以免又产出"看起来对"的代码。

## 要做什么

1. **`RabbitMqEventBus : IEventBus`**
   - 通道开启发布确认与确认跟踪；`mandatory: true`。
   - 不可路由 ⇒ **抛出/返回失败**，绝不静默成功（参照仓库 `EventPublisher.cs:36-41/505-510` 是只记日志后照样 ACK）。
   - 消息属性：`Persistent = true`、`MessageId = envelope.MessageId`、
     `ContentType = application/json`、头里带 `EventName`。
2. **`RabbitMqTopologyBootstrapper`**：把 `RabbitTopologyPlanner.Plan()` 的产物**幂等**地声明到 broker
   （交换机、队列、绑定）。不得声明计划之外的任何队列。
3. **`RabbitMqConsumer`**：
   - 手动 ACK（`autoAck: false`）。
   - 先走 `IInboxStore.TryBeginProcessingAsync`，重复消息直接 ACK 跳过。
   - 失败时按 `ConsumePolicy.Decide` 决定：Nack 进对应重试队列（带递增的 `nexusstack-attempts` 头），
     或进死信。
   - 死信队列要有消费者与指标——参照仓库**没有** DLQ 消费者。
4. **故障自愈**：`ChannelShutdownAsync` / `CallbackExceptionAsync` 要触发重建，
   而不是像参照仓库那样没有任何通道级恢复处理。

## 验收标准

- [x] 真 broker：不可路由的 mandatory 消息 → `PublishAsync` 返回**失败**——**带反向验证**，见下。
- [x] 真 broker：两个消费端消费同一事件，只有失败的那个收到重试消息——断言的是**两个队列的深度**：失败者的重试队列 1 条、成功者的 0 条。
- [x] 真 broker：通道关闭后自动恢复——从 **broker 侧**（管理 API）掐断连接，而不是调用测试钩子；断言三段：连接**确实被杀了**、消费者重建了、**重建之后还在继续消费**。
- [x] 真 broker：同一 `MessageId` 投递两次业务只生效一次——处理器只被调用一次，消费者的 `DuplicateCount` 为 1。
- [x] 真 broker：档位用尽的消息出现在死信队列里——并断言处理器**恰好被调用 3 次**（首次 + 两档重试），那一并证明了**档位是递增的**：不递增的话消息会在第一档无限循环，永远到不了死信。
- [x] 拓扑声明幂等：连续声明两次都成功，且计划里的每个队列都能 `passive` 查到。

## Comments

### local

**第 10 轮：验证脚本的落点**

上面那组验证是一次性的探针（`%TEMP%\rmqprobe`）。**它不该只活在那里**——
等本票开工时，那几条断言要变成 `RabbitMqBrokerFacts` 之类的集成测试（`[PostgresFact]` 的同类：
缺连接信息就**响亮地跳过**，而不是伪装成通过）。


### local

**第 11 轮：发布端与拓扑（6 条验收里的 2 条）**

#### 交付

| 东西 | 位置 |
|---|---|
| `RabbitMqEventBus : IEventBus` | `BuildingBlocks.Infrastructure/Events/RabbitMq/` |
| `RabbitMqTopologyBootstrapper` | 同上 |
| `RabbitMqOptions`（与生产配置同一个形状） | 同上 |
| `RabbitMq.IntegrationTests`（真 broker） | `tests/RabbitMq.IntegrationTests/` |

#### 反向验证：这条测试到底在验谁

开发布确认是 `RabbitMqEventBus` 里的一行：

```csharp
publisherConfirmationsEnabled: true,
publisherConfirmationTrackingEnabled: true,
```

把它改成 `false` 之后：

```
[FAIL] AnUnroutableMandatoryMessage_FailsInsteadOfSilentlySucceeding
→ 不可路由的消息竟然发布成功了——发布确认没起作用。
```

**这证明这条测试验的是 broker 的行为，不是我自己的代码** —— 正是本票一开始担心的事
（"替身只会证明我自己写的行为"）。

#### `[RabbitMqFact]`：跳过与通过必须能被区分

新增的 `[RabbitMqFact]` 与 `[PostgresFact]` 同一处理：没有连接信息就**跳过**，
并把"跳过了什么、为什么"写在跳过原因里。

那不只是为了新克隆的仓库不红——更是因为**一个伪装成通过的跳过，与一个不会失败的检查是同一件事**。

#### 一处刻意的设计：测试用唯一前缀的交换机

broker 是**共享的**（上面还跑着别的服务）。测试每次生成唯一前缀，
且**改掉** `ExchangeName`，所以不会碰到那个正在被使用的交换机。

否则两次运行的测试会撞在同一个队列上，而那种失败看起来像"消费者有问题"，
不像"两个测试在抢同一个队列"。

#### 还剩 4 条（全部在消费端）

- 两个消费端消费同一事件，**只有失败的那个**收到重试消息
- 手动关闭通道后自动恢复并继续消费
- 同一 `MessageId` 投递两次，业务只生效一次（与票据 19 的 Inbox 联动）
- 档位用尽的消息进死信队列，且被 DLQ 消费者记录

`RabbitMqConsumer` 还没写，所以本票标 `claimed`。


### local

**第 12 轮：消费端（6/6）**

#### 交付

| 东西 | 位置 |
|---|---|
| `RabbitMqConsumer`（手动 ACK / Inbox 去重 / 档位重投 / 通道恢复） | `BuildingBlocks.Infrastructure/Events/RabbitMq/` |
| `IIntegrationEventProcessor`（处理器的缝） | 同上 |
| 6 条真 broker 验收测试 | `tests/RabbitMq.IntegrationTests/` |

#### 写测试时发现的一个真 bug

`ChannelShutdownAsync` 把通道置空了 ✓，但主循环还挂在 `Task.Delay(Timeout.Infinite)` 上——
**它永远不会走到重建那一步**。通道一断，消费者就安静地什么都不做，
而进程活着、健康检查还是绿的。修法是加一个"通道丢了"的信号，
`Task.WhenAny` 让主循环醒来。

**这正是那条验收要防的失效，而它出现在我自己的实现里。**

#### 一条不稳定测试，以及我如何判断它不是产品缺陷

`AnExhaustedMessage_EndsUpInTheDeadLetterQueue` 第一次过了、第二次报 `Actual: 2`（期望 3）。
三次里错一次。

它有两种解释，而**区别是本质的**：
- 「我的测试抢跑」——死信队列能看到消息了，但第三次调用还没返回；
- 「消费端少重试了一次」——那是真 bug。

判断方法：把"立刻断言"换成"**等它到 3**"。连跑五次全过 ✓ → 是我的测试抢跑。
**"等"问的是"最终会不会"，"立刻断言"问的是"此刻是不是"** —— 而消息在队列之间移动需要时间。

#### 一处刻意的克制

恢复测试从 **broker 侧**（管理 API）掐断连接，而不是调用测试钩子——被验证的是
"broker 断我，我自己回来"，那才是生产里会发生的事。

而它**只掐自己那一条**：broker 是共享的，"把所有连接都杀掉"能让测试通过，
代价是顺手断掉别人的生产流量。

第一次跑它失败了，报"没找到那个连接名"——**这是它该有的失败**：
它断言了"杀连接这个动作确实发生了"，所以不会变成空转。修的是匹配方式（轮询 + 包含），
不是把断言删掉。

#### 六条验收

| 验收 | 状态 |
|---|---|
| 不可路由 → 发布失败 | ✅（**反向验证**：关掉发布确认，测试立刻红） |
| 只有失败的消费者收到重试 | ✅ |
| 通道关闭后恢复并继续消费 | ✅ |
| 同一 MessageId 只生效一次 | ✅ |
| 档位用尽进死信 | ✅ |
| 拓扑声明幂等 | ✅ |


### local

**第 16 轮：**上面这张表曾经是空的**（补记）**

第 31 轮 review 把真 broker 打开之后，发现这六条里**有两条是凭空成立的**：

- `UniquePrefix()` 只让**交换机**每次运行不同名。而队列名由 `IntegrationEventNaming.ConsumerQueue
  (EventName, ConsumerName)` 派生——**不含交换机名**，而测试里的消费端名是写死的 `"doomed"/"alpha"/"beta"`。
  于是队列是**跨运行共享的持久队列**，死信里积着历次运行的 39 条残留。
- 后果：`WaitForDepthAsync(死信, 期望 1)` 与 `probe.Count >= 3` 两条断言在 **2 秒内**就成立，
  而它们本该各等 2 秒 + 4 秒的档位。**注释里写着"测试用的交换机与队列必须每次运行都不同名"，
  实现只做了一半。**

同一个文件里还藏着第二个洞：两条失败路径的测试发布时**没有写 `MessageId`**，
而生产发布端（`RabbitMqEventBus.cs:78`）总是写它。不写 `MessageId` 时消费端会**每次投递新生成一个 Guid**，
去重永远不触发——于是那条验收走的是一条**生产上不存在的路**。

#### 修法与证据

- 消费端名字带上前缀（三个测试文件），队列随之每次运行不同名；
- 两条失败路径的测试补上 `MessageId`（走生产形状）；
- 上面两条断言现在**能被证伪**：把"失败删键"摘掉 → 死信验收**红 36 秒**（`depth=0 count=1`）；
  修回来 → `depth=1 count=3`、耗时 **9 秒**（正好两个档位）。
- 全套 6 条重跑：**6/6 通过**，单条耗时 441 ms – 8 s（此前是可疑的 2 秒）。

> 教训与票据 30、64 同源：**"测试通过"与"测试真的跑了那条路"是两件事**，
> 而共享 broker 上的持久队列会让前者伪装成事实。
> 余下一点未做：队列本身没有清理（名字唯一之后残留只是占地方，不再影响结论）。
