# 04 消息/缓存/后台任务/鉴权与设计文档评审

## 概览

- **评审对象**：`D:\NexusStack\NexusStackBackend`（`dotnet new` 模板 NexusStack，.NET 10，作者 Leo Wang），只读评审，未修改任何文件。
- **评审范围**：`Domain/NexusStack.RabbitMQ/**`、`Domain/NexusStack.Redis/**`、`BackgroundServices/**`、`Docs/**`，以及可追溯到的 SignalR Hub 与 RBAC/鉴权实现（`Domain/NexusStack.Core/**`、`Host/NexusStack.WebAPI/**`）。
- **下文路径均为仓库相对路径**，`:` 后为行号。
- **一句话结论**：**运维/技术管道（RabbitMQ + Redis + SignalR + RBAC）是这份模板里质量最高的部分**——消费幂等、重试、DLQ、权限缓存预计算都真实存在且经过认真推敲，作者自己的《MQ 幂等检查报告》5 轮结论中**约 8 成能在代码里逐条对上**。但**它不是一个"集成关注点被隔离在接缝之后"的架构**：事件契约以 CLR 类型全名为路由键、发布前必须先有消费端 Handler 程序集、所有服务共用一套交换机/队列/Redis 键空间、SignalR 无 Backplane；同时**PlanTaskService 在默认模板下不执行任何任务**（其调度缓存从未被写入），**没有 outbox/inbox、没有任何测试项目**。按现状拆分 N 个服务会立刻撞上共享队列名、共享 Redis 键空间、跨实例推送丢失三类问题。

---

## 发现

### F1【严重】重试消息经 DLX 回主交换机后按"事件名"扇出到**所有** Handler 队列，而非仅失败的那个

**证据**
- 重试入队方向是对的：`Domain/NexusStack.RabbitMQ/EventSubscriber.cs:219`（`retryQueueName = $"{queueName}.retry"`）、`:258-261`（retry 交换机上以 `routingKey = queueName` 绑定）、`:621-633`（`RepublishForRetryAsync` 用 `routingKey: queueName` 发布）。
- 但重试队列的**死信出口**用的是事件全名：`EventSubscriber.cs:246-256`，尤其 `:255` `["x-dead-letter-routing-key"] = eventName`，而 `eventName = eventType.FullName`（`:194`）。
- 主交换机上**每个 Handler 各有一个队列**，且都以同一个事件全名绑定：`EventSubscriber.cs:196`（`queueName = $"{eventHandlerName}"`）、`:294-297`（`QueueBindAsync(queue: queueName, exchange: ExchangeName, routingKey: eventName)`）。
- 因此"失败 → retry 队列 → TTL 到期 → DLX 回主交换机（routingKey=eventName）"会把这条消息**投给该事件类型的所有 Handler 队列**。

**影响**：一个 Handler 失败，会顺带让同事件的其它 Handler 再收一遍消息（重复执行）。今天只有 2 个 Handler 且事件类型不重叠（`Domain/NexusStack.Core/EventHandler/NotificationEventHandler.cs:17`、`OperationLogEventHandler.cs:12`），所以尚未爆雷；一旦某事件有 2 个订阅者，B 端重复执行的**唯一屏障**是它自己的 Redis 幂等键，而 `RabbitOptions.EnableConsumerIdempotency` 默认可被关掉（`Domain/NexusStack.RabbitMQ/RabbitOptions.cs:67`）。作者文档只验证了"进 retry 队列"的方向：`Docs/MQ-Idempotency-Review.md:21`（"重试发布路由 正确：RepublishForRetry …避免误路由"）、`:64`（"重试路由正确性 …确保进入对应 retry 队列"）——**没有覆盖 DLX 回程这一段**。这是 5 轮检查的真实盲区，不是笔误。

**建议**：重试回程不要复用主交换机的"事件名"路由。方案 A：给每个 Handler 队列在主交换机上**额外**绑定一个以队列名为 routing key 的 binding，retry 队列的 `x-dead-letter-routing-key` 改为 `queueName`；方案 B：retry 队列的 DLX 指向默认交换机（`""`）+ routing key = 队列名，RabbitMQ 直接投回原队列，语义最清晰。拆分服务后应改为"每服务独立重试交换/队列 + 稳定事件契约名"，并把这条路径纳入回归用例。

### F2【严重】不可路由的消息会被静默丢弃，甚至"发布成功 + ACK 成功"

**证据**
- 主发布通道：`EventPublisher.cs:36-41` 注册了 `BasicReturnAsync`，**只写 Error 日志，不抛异常**；发布用 `mandatory: true`（`:215-220`）。调用方（如 `Domain/NexusStack.Core/Services/AsyncTasks/AsyncTaskService.cs:54`）认为发布成功。
- 重试发布通道：`EventSubscriber.cs:384-393`（`CreateRetryPublishChannelAsync`）**没有注册任何 `BasicReturnAsync`**，却用 `mandatory: true` 发布（`:628-633`）；发布成功后立刻 ACK 原消息（`:505-510`）。
- 触发条件真实存在：一旦入口无法解析 `consumerQueueMappings`，`queueDimension` 会退化为 `eventArgs.RoutingKey`（`EventSubscriber.cs:438-440`），重试发布就会用事件全名去路由 retry 交换机——那里只有 `queueName` 绑定，**必然不可路由**。

**影响**：文档把这一情形写成轻描淡写的"retry 可能无法路由；属异常配置/生命周期边界"（`Docs/MQ-Idempotency-Review.md:81`）。代码的真实后果更糟：**消息被 broker 退回 → 无人处理 → 原消息被 ACK 掉 → 消息永久丢失（at-most-once）**，与文档第 3 节宣称的"至少一次"（`Docs/MQ-Idempotency-Review.md:62`）相矛盾。

**建议**：发布端把 `BasicReturn` 视为失败（抛 `InvalidOperationException` 或写入失败计数并让调用方感知）；retry 通道同样注册 `BasicReturnAsync`，不可路由时**不许 ACK**，直接 Nack 进 DLQ；顺序上应先确认可路由再 ACK。

### F3【严重】PlanTaskService 在默认模板下**不执行任何任务**，且每秒空转

**证据**
- `BackgroundServices/NexusStack.PlanTaskService/Program.cs:1-10`：仅 10 行，调用 `builder.InitAppliation(moduleKey, moduleTitle, CoreServiceType.PlanTaskService)`。
- `Domain/NexusStack.Core/ServiceCollectionExtensions.cs:257-263`：PlanTaskService 分支只做 `AddCronTask()`；**`//builder.Services.AddHostedService<ExecuteSeedDataService>();` 在 `:262` 被注释掉**。
- 唯一会写调度缓存的入口是种子数据：`Domain/NexusStack.Core/SeedData/ScheduleTaskSeedData.cs:29`（`scheduleTaskService.InitializeAsync()`），而它只能由 `ExecuteSeedDataService` 触发（`HostedServices/ExecuteSeedDataService.cs:87-93`）。
- 调度循环**只认 Redis，不回落 DB**：`Domain/NexusStack.Core/Schedules/CronScheduleService.cs:101-107`——`scheduleTask is null || !scheduleTask.IsEnable` 就 `Task.Delay(1000)` 跳过，无日志、无告警。
- 缓存本身还永不设 TTL：`Domain/NexusStack.Core/Services/Schedules/ScheduleTaskService.cs:61`、`:87` 都是 `SetAsync(key, value)`（无过期）。

**影响**：模板使用者拿到 `--IncludePlanTaskService true` 后，这个"计划任务服务"是一个 1Hz 空转进程：`DailySchedule`（日志/记录清理，`Schedules/DailySchedule.cs:31-50`）永远不会跑；一旦 Redis 被清空/无持久化重启，**即便曾经初始化过，调度也会静默停止**——"任务是否启用"这个事实的唯一来源是一个可丢弃的缓存键。这与"后台服务是真服务还是空壳"的答案一致：**MQService 是真的（消费 + 推送），PlanTaskService 是事实上的空壳**，且空壳原因不是代码没写，而是启动装配被注释掉了。

**建议**：把"调度定义 + 启用状态"的真相放回 DB 或配置，Redis 只做加速；`InitializeAsync` 必须在进程启动时幂等执行（不依赖被注释的种子服务）；缓存键加 TTL + miss 时回落 DB 重建；`scheduleTask is null` 必须 `LogWarning`（否则今天连"没跑"都看不出来）。

### F4【高】Cron 调度每秒轮询、每轮新建 DI Scope 并打一次 Redis

**证据**：`Schedules/CronScheduleService.cs:90-114`——`while (!stoppingToken.IsCancellationRequested)` 内**先** `serviceFactory.CreateAsyncScope()`（`:93`）再解析 5 个服务（`:95-99`），然后一次 `redisService.GetAsync<ScheduleTaskExecuteDto>`（`:101`），未到点则 `Task.Delay(1000)`（`:112`）。Scope 在循环内、延迟在 `using` 作用域内，所以**每秒一次 Redis RTT + 一次完整 Scope 构造**，直到 `nextExcuteTime` 到达。

**影响**：单进程 86,400 次 Redis GET/天/任务（一天只执行一次的任务也一样），且与 Redis 的连接池、Scope 分配、DI 解析全部压在 1Hz 心跳上；多实例部署时是 N 倍。这是典型的"用轮询换实现简单"，在微服务重建里会被放大成可观的基础设施成本。

**建议**：改为"算准下一个到期时间 → `Task.Delay(直到到期)`"或用 `PeriodicTimer`；只有到点时才创建 Scope；把"启用状态/表达式"通过配置变更事件（AgileConfig 已有推送能力）主动刷新，而不是每秒拉。

### F5【高】Cron 分布式锁硬编码 60s TTL 且不续租——长任务会被两个节点同时执行

**证据**：`Schedules/CronScheduleService.cs:116`（`lockName = $"ScheduleTask:{code}.{nextExcuteTime}"`）、`:136`（`redisService.SetAsync(lockName, null, TimeSpan.FromMinutes(1), RedisExistence.Nx)`）。锁 TTL 固定 1 分钟，与 `scheduleTask.Timeout`/实际执行时长无关；`ProcessAsync(stoppingToken)` 期间**没有任何续租**（`:131`、`:138`）。

**影响**：节点 A 与节点 B 持有同一个 `nextExcuteTime`，A 拿到锁开始执行；若 A 的执行超过 60s（清理三个月操作日志 + 半年异步任务，见 `DailySchedule.cs:41-48`，越界概率不低），锁自动过期，B 下一轮（1 秒后）即可拿到同一把锁并**重复执行同一 occurrence**——而 `DailySchedule` 的 `Singleton => true`（`DailySchedule.cs:29`）恰恰是为了防止这件事。清理类任务重复执行通常只是浪费，但同一模式被模板用户套用到"发券/结算/对账"就是资损。

**建议**：锁 TTL 必须大于预期最长执行时间并做续租（看门狗），或改用带 fencing token 的租约；任务本身也要幂等（见 F6/F9）。

### F6【严重】没有 outbox/inbox：DB 写入与消息发布之间没有事务或补偿

**证据**
- 生产端：`Domain/NexusStack.Core/Services/AsyncTasks/AsyncTaskService.cs:43-56`——先 `InsertAsync(task)`（`:47`），再 `publisher.PublishAsync(eventInstance)`（`:54`）。两者之间无事务、无 outbox 表、无失败补偿；`PublishAsync` 自身也没有重试（`EventPublisher.cs:185-231` 一次失败直接抛）。
- 消费端：`EventSubscriber.cs:395-480` 直接调 Handler，Handler 内部各自开 Scope 写 DB（`NotificationEventHandler.cs:33-48`），**没有 inbox 表**，消费幂等只靠 Redis 键（`:560`）。
- 交叉验证：仓库内 `PublishAsync` 的调用点只有 3 处（`Filters/ApiAsyncExceptionFilter.cs:180`、`Filters/OperationLogActionFilter.cs:85`、`AsyncTaskService.cs:54/69/86`），全部是"先写库/先执行，再发消息"的形态，没有任何 `BeginTransaction` 跨越发布（`BeginTransactionAsync` 只出现在用户/菜单/权限的非消息路径：`UserController.cs:185/235/338`、`MenuController.cs:197`、`PermissionService.cs:98`）。

**影响**：`Insert` 成功而 `Publish` 失败（网络抖动、RabbitMQ 重启、F2 的不可路由）时，**AsyncTask 永久停在 Pending 且没有任何消息会来救它**——没有对账、没有补偿任务、没有"孤儿 Pending"清理（`DailySchedule` 只删 6 个月前的记录，`DailySchedule.cs:47-48`）。反向也不成立：不存在"消息先到、DB 没写"的 inbox 兜底。

**建议**：模板里就应内置 **Outbox 模式**（与业务写在同一 `DbContext`/事务里落 `outbox` 表 + 后台发布器 + 至少一次重投）以及**Inbox 表**（`message_id` 唯一键，替代/叠加 Redis 幂等键，使幂等不依赖易失的 Redis，见 F9）。这也是拆分服务后跨库一致性的唯一可行解。

### F7【高】消费幂等键绑的是"业务标识"，不是"消息标识"

**证据**：`EventPublisher.cs:112-126`——`BuildMessageId` 在 `TaskId > 0` 时返回 `$"{message.TaskCode}:{eventBase.TaskId}"`；消费端把它当幂等键：`EventSubscriber.cs:547-568`，键为 `mq:idempotent:{queueDimension}:{messageId}`（`:560`），**成功路径不释放键**（只有失败才删，`:489`），默认存活 24h（`RabbitOptions.cs:72`）。

**影响**：同一个 `(TaskCode, TaskId)` 在 24 小时内**合法地**再次发布（业务重跑、状态机二次触发、人工补发）会被消费端判为 `DUPLICATE` 并 `ACK` 跳过（`EventSubscriber.cs:448-453`）。作者已经踩到过这个坑并打了补丁：`Docs/MQ-Idempotency-Review.md:90-93` 承认"AsyncTask 失败后 RetryAsync 重新发布…若 MessageId 仍为 `TaskCode:TaskId`，会命中首次消费时写入的幂等键，被判 DUPLICATE 并跳过，导致重试消息不执行"，于是加了 `messageIdOverride`（`EventBus/IEventPublisher.cs:18-19`，调用点 `AsyncTaskService.cs:85-86`）。**这说明缺陷仍在，只是被调用方逐个绕开**——模板用户每新增一条"重发"路径都得记得传 override，漏一次就是静默丢事件。

**建议**：幂等键应基于**消息身份**（发布端生成的 `messageId` = `Guid`/ULID，或 `{TaskCode}:{TaskId}:{attempt}`）而非业务身份；业务级去重若确有需要，应作为**独立的业务幂等**（业务表唯一键）表达，不要和传输层去重混在一个 Redis 键里。契约里再加 `eventId` + `occurredAt` 便于排障。

### F8【高】通道/消费者没有自愈：channel 级故障后永久停止消费或发布

**证据**
- 订阅端：`EventSubscriber.cs:329-356`（`OnConsumerShutdownAsync`）与 `:358-368`（`RemoveConsumerMappings`）在通道关闭/消费者注销时**只清理字典**，没有任何重订阅；`OnConsumerUnregisteredAsync`（`:319-327`）同样只清理。
- 发布端：`EventPublisher` 持有单个 `publisherChannel`（`:24`，构造时创建 `:34`），**没有订阅 `ChannelShutdownAsync`**，也不在发布失败后重建通道；`EventSubscriber.retryPublishChannel`（`:36`）同理。
- 连接层恢复能力是有的：`Connection.cs:72-78` 打开了 `AutomaticRecoveryEnabled` / `TopologyRecoveryEnabled` / `NetworkRecoveryInterval=10s`。

**影响**：客户端自动恢复能兜住**连接级**故障，但兜不住**通道级**终止（如 406 PRECONDITION_FAILED、队列参数不一致、`basic.consume` 被取消、通道被 broker 关闭）。那之后：订阅端该队列再无人消费（消息堆积无人知），发布端 `BasicPublishAsync` 持续抛 `AlreadyClosedException`（业务接口报错），**都要等人重启进程**。模板用户会在"为什么消息不消费了"上花很久。

**建议**：注册 `ChannelShutdownAsync`/`CallbackExceptionAsync`，对可恢复原因做指数退避重建通道并重订阅（含队列重声明）；发布端把通道放进一个可重建的持有者（或按发布使用短生命周期通道 + 通道池）；加健康检查暴露"消费者数量/发布成功率"。

### F9【高】缓存层无击穿保护：miss → 查 DB → 回写，无单飞/无锁

**证据**：`Domain/NexusStack.Core/Services/Users/UserContextCacheService.cs:38-49`——`GetAsync` → `if (cached != null) return` → `BuildFromDbAsync`（`:70-131`，内含 4~5 次 DB 查询）→ `SetAsync(key, context, ttl)`。没有分布式锁、没有 `SemaphoreSlim` 单飞、没有逻辑过期/软过期、没有空值缓存；`SetAsync` 也是无条件覆盖（`:47`，未用 `Nx`）。TTL 固定 10h（`:29`）。

**影响**：这个缓存的**调用点正好是鉴权热路径**——每个到达的请求都会 `GetOrSetAsync`（`Authentication/RequestAuthenticationTokenHandler.cs:47`、`Authentication/RequestAuthenticationSignalRTokenHandler.cs:41`）。缓存失效或被逐出后，同一用户的并发请求会**同时**重建（登录突发、前端首屏并发接口、Gateway 重试都会放大），把 4~5 条 DB 查询乘以并发数，即经典的 cache stampede。这层缓存存在的唯一目的就是"别打 DB"（`Docs/RBAC-Design.md:291`"鉴权热路径为纯内存操作，O(1) 时间复杂度"），而击穿瞬间它恰恰把压力全导向 DB。

**建议**：单飞（进程内 `Lazy<Task<T>>`/`SemaphoreSlim` 合并同键并发回源）+ 逻辑过期（值内带 `expiresAt`，过期后由一个请求异步重建、其余用旧值）；对"用户不存在/已禁用"做短 TTL 空值缓存；重建失败要按文档要求**安全优先**（宁可 403，见 F13）。

### F10【高】缓存失效是"删键"，与并发回源存在竞态；"禁用用户立即生效"只是尽力而为

**证据**
- 失效实现：`UserContextCacheService.cs:58-68`——`InvalidateAsync(userId[, platformType])` 只做 `DeleteAsync(CacheKey(...))`。
- 回源实现：`GetOrSetAsync`（`:38-49`）在**读 miss 之后、写回之前**完成 DB 读取。
- 竞态序列：请求 T1 读 miss → T1 查 DB（此时读到 `IsEnable=true`、旧角色）→ 管理员在 T2 提交禁用并调用 `InvalidateAsync`（`Host/NexusStack.WebAPI/Controllers/UserController.cs:317`）→ **T1 才把旧快照写回，TTL 10h**。
- 文档的承诺：`Docs/RBAC-Design.md:559`"禁用用户立即生效 ✅ 已完成"、`:560`"全链路缓存失效覆盖 ✅ 已完成"、`:354-372` 的失效矩阵。

**影响**：失效矩阵本身**确实落地了**（见下表），但"立即生效"在存在并发回源时不成立——被禁用/被回收权限的用户可能在最长 10 小时内继续通过鉴权（`Filters/RequestAuthorizeFilter.cs:131` 只读缓存里的 `ApiPermissionKeys`）。对 RBAC 语义来说这是"权限回收延迟"，属于安全等级问题，而文档给出的是一句无条件的 ✅。

**建议**：引入**版本号/世代键**（`usercontext:{userId}:{platform}:v{n}`，失效即 `INCR` 版本，回源时校验版本，旧版本写回失败）或"删除 + 延迟二次删除（double-delete）"；把 TTL 从 10h 收敛到与业务可接受的回收集合匹配的区间；把该竞态写进文档，别用 ✅ 掩盖。

### F11【中高】失效成本 O(用户 × 平台)：角色权限变更逐用户发 DEL

**证据**：`Domain/NexusStack.Core/Services/Users/PermissionService.cs:119-127`——变更角色权限后查出所有 `UserRole.UserId`，`foreach` 里逐个 `await userContextCacheService.InvalidateAsync(userId)`（无平台参数）；`UserContextCacheService.cs:66-67` 内部再 `foreach (var p in Enum.GetValues<PlatformType>())` 逐个 `DeleteAsync`。同一模式见 `Host/NexusStack.WebAPI/Controllers/RoleController.cs:153`、`MenuController.cs:121-122`、`:251`。

**影响**：一个 1 万人的角色改一次权限 = 1 万次串行 Redis DEL × 平台数（还要 +1 次 `All=0`，见 F12），全部在**HTTP 请求线程里串行**完成，接口 RT 随角色规模线性增长；且没有 pipeline/批量、没有失败重试，中途异常会导致"部分用户已失效、部分没有"（缓存不一致）。文档选择 `(UserId, PlatformType)` 作为键维度（`Docs/RBAC-Design.md:342-344`）时**没有评估失效扇出成本**。

**建议**：失效不要按用户枚举。改成"角色版本号"间接层（`role:{roleId}:v` 参与用户上下文缓存键的构成，或缓存值里带 `roleVersions` 并在读取时校验），一次写即可让全量用户失效；若必须枚举，用 Redis pipeline 批量 + 后台任务异步失效，并对失败项重试。

### F12【中高】`PlatformType.All = 0` 在两条代码路径上语义冲突，且文档未定义该值

**证据**
- 实际枚举：`Infrastructure/Enums/PlatformType.cs:10-16`——`[Flags]` 且 **`All = 0`**；`Admin=1, Pc=2, Mini=4, Android=8`。
- 路径一（当做"全平台"）：`Services/Users/PermissionService.cs:48`、`:94`——`role.Platforms == PlatformType.All || (role.Platforms & a.PlatformType) != 0`。
- 路径二（按位与，`All=0` 永远匹配不上）：`Services/Users/UserRoleService.cs:28`——`(platformType == PlatformType.All || (r.Platforms & platformType) != 0)`；而登录用的是真实平台值，于是 `Platforms=0` 的角色在登录时被过滤掉，直接命中"在当前平台下未分配任何角色，无权限登录"（`Services/Users/UserTokenService.cs:124-128`）。
- 文档：`Docs/RBAC-Design.md:164` 明确写 `enum PlatformType { Admin = 1, Pc = 2, Mini = 4, Android = 8 }`，**没有 `All`**；而 `:261` 又要求按位过滤。

**影响**：同一个"全平台角色"，在权限配置页被当成全平台（能勾选任意平台菜单），在登录时却完全无效——用户会看到"角色配了却登不进去"或"某些菜单拿不到权限"。`All = 0` 还让 `InvalidateAsync` 多删一个 `...:0:v2` 键（无害但暴露语义不严谨）。

**建议**：`[Flags]` 枚举里不要用 `0` 表示"全部"。要么引入 `PlatformType.All = Admin|Pc|Mini|Android` 显式位组合，要么把"全部"表达为 `null`/`HasValue=false` 的独立语义；两条路径必须共用同一个过滤函数（现在 `PermissionService` 与 `UserRoleService` 各写了一份），并把该语义写入文档。

### F13【高】鉴权存在"配置化 fail-open"，且配置来自远端配置中心

**证据**：`Filters/RequestAuthorizeFilter.cs:87-97`——`ApiPermissionMode.Disabled`（只 `LogWarning("⚠️ API 权限校验处于完全开放模式")`）与 `Relaxed` 都**直接放行**；`:100-111` `RootOnly`/`EnableRootBypass` + `userContext.IsRoot` 也直接放行。配置默认值：`Infrastructure/Options/ApiAuthorizationOptions.cs:21` `ApiPermissionMode = RootOnly`、`:27` `EnableRootBypass = false`。而 `IsRoot` 来自角色 Code 字符串比对：`UserContextCacheService.cs:89`（`roleCode.Any(code => string.Equals(code, SystemRoleConstants.Root, ...))`）。运行配置实际由 AgileConfig 远端下发（`Domain/NexusStack.Core/ServiceCollectionExtensions.cs:354-407`）。

**影响**：模板默认是 `RootOnly`（不是文档推荐的 Strict，`Docs/RBAC-Design.md:556` 只写"实现完整 API 级权限校验 ✅"），意味着只要有一个 `IsRoot` 角色，该用户**绕过全部 API 权限**；而 `Disabled`/`Relaxed` 一旦被配置中心改成（或被误提交成）任意值，整个后端就变成"只要登录就能调一切"，日志里只有一行 Warning。这违反了作者自己写下的原则："缓存重建失败时应采取安全优先策略（宁可 403 也不要放行）"（`Docs/RBAC-Design.md:583`）。

**建议**：权限模式只允许编译期/启动期决定（生产环境禁止 `Relaxed/Disabled`，检测到即启动失败或忽略并告警），不要让它成为运行时可热改的普通配置；`IsRoot` 不要用可变的 `Role.Code` 字符串在运行时判断，改为数据库标记位 + 审计；每次绕过都要留审计日志。

### F14【严重】SignalR 无 Backplane，而消费队列名 = Handler 全名 ⇒ 多实例下推送必然丢失

**证据**
- 队列名只由 Handler 类型全名决定：`EventSubscriber.cs:194-196` `queueName = $"{eventHandlerName}"`。同一份代码的多个 MQService 实例因此是**同一队列的竞争消费者**（消息被负载均衡到某一个实例）。
- SignalR 只注册了服务本体，**没有任何 Backplane**：`ServiceCollectionExtensions.cs:194-197`（只有 `AddSignalR(options => EnableDetailedErrors)`）；全仓库检索 `AddStackExchangeRedis`/`Backplane` 无任何命中，`Domain/NexusStack.Redis/ServiceCollectionExtensions.cs:18-45` 也只做连接初始化。
- 推送目标组是**进程内**的：`Domain/NexusStack.Core/SignalR/NotificationHub.cs:19`、`:25`（`Groups.AddToGroupAsync` 加的是当前实例的连接组），推送侧 `NotificationEventHandler.cs:91/95/106/110` 用 `IHubContext` 在**消费所在进程**发。
- 文档承认这一约束并要求 Backplane：`Docs/SignalR-Notification-Design.md:244-253`（7.2 多实例需要 Backplane，推荐 Redis）、`:307`（"多实例下未启用 Backplane 导致'部分用户收不到消息'"）、`:238-242`（V1 默认单实例）。

**影响**：文档是诚实的（列了风险），但**代码里没有任何东西保证单实例**：把 MQService 扩到 2 个副本，用户连在实例 A、消息被实例 B 消费，推送就静默丢失（用户只看到"没有通知"，没有任何错误日志在丢失侧）。这是"能否拆成 N 个服务"的直接否定项——**今天的推送可用性依赖于一个未被强制的部署约束**。

**建议**：按文档 7.2 引入 Redis Backplane（`AddStackExchangeRedis`）；或改为 fan-out 语义（每实例独立队列 + 事件广播）以回避竞争消费；在 Backplane 到位前，至少加启动自检/文档强约束（副本数=1），并加"推送目标实例不存在"的可观测指标。

### F15【中高】NotificationEventHandler 吞掉全部异常 ⇒ 推送失败永不重试，与文档第 6.2 节矛盾

**证据**：`NotificationEventHandler.cs:124-130`——`catch (Exception ex) { task.State = Fail; task.ErrorMessage = ex.Message; await Update; LogError; }`，**不重新抛出**。上游 `EventSubscriber.ProcessEvent` 因此认为处理成功（`EventSubscriber.cs:692-705` 返回 `true`）→ `BasicAckAsync`（`:461`）→ **不会重试**。而文档要求区分两类失败：`Docs/SignalR-Notification-Design.md:231`"解析失败（Data 非法）视为不可重试：直接 Fail，避免无限重试污染队列"、`:232`"网络瞬时失败可重试：**让异常抛出以触发 MQ 重试**"。代码实现了前半句，**完全没有实现后半句**。

**影响**：SignalR 推送遇到瞬时网络/背压失败时不会重试；同时 `ProcessEvent` 把"Handler 吞异常"一律视为成功，让 **MQ 层的重试/DLQ 形同虚设**（`MaxRetryCount`/DLQ 只对"抛出异常的 Handler"生效）。文档 6.1 声称的"MQ → Handler：至少一次"（`SignalR-Notification-Design.md:214`）在业务 Handler 这一层就降级成了 at-most-once。

**建议**：明确 Handler 的失败契约：只把"不可重试的解析错误"转成 `Fail` 并正常返回，可重试错误必须抛出（或定义 `TransientException`，由订阅器区分"抛异常=重试"与"记录 Fail=终态"）；把这条契约写进 `IEventHandler<T>` 的文档注释；DLQ 需要有消费者/告警兜底（见 F16、缺失项）。

### F16【高】推送路由完全信任消息体，可越权向任意用户/组/全员推送

**证据**
- 消费端直接采信 `Data` 里的目标：`NotificationEventHandler.cs:80-111`——`target.type=="user"` → `Clients.Group($"user:{userId}")`（`:91`），`"group"` → `Clients.Group(group)`（`:106`，**组名完全来自消息体**），`"all"` → `Clients.All`（`:110`）。
- 模型无任何权限字段：`Domain/NexusStack.Core/SignalR/NotificationRelayModels.cs:12-28`（`Target.Type/UserId/Group` 皆为字符串）。
- 生产侧封装同样无权限校验：`Services/SignalR/NotificationRelayService.cs:12-26`（`NotifyToUserAsync`）、`:28-42`（`NotifyToGroupAsync`）、`:44-57`（`NotifyToAllAsync`）。
- 文档要求相反：`Docs/SignalR-Notification-Design.md:194`"只允许连接加入'自己有权'的 group，group 名称由服务端依据 Claim 计算"、`:195`"对广播（all）设置更高权限要求（仅系统管理员可触发生产消息）"。

**影响**：任何能构造 `Notification` 异步任务的业务代码（`IAsyncTaskService.CreateTaskAsync(..., "Notification")`）都能把 `target.group` 填成 `"user:123"` 向**指定用户**推送伪造内容，或用 `NotifyToAllAsync` 全员广播——即中继侧没有做二次授权。文档 5.3 把授权列为"V1 可先只做身份校验，授权作为增强项"（`:196`），但至少"group 名由服务端计算"这一条与代码相反：`NotificationGroupNames` 只在 Hub 侧用于加入（`NotificationHub.cs:19/25`），消费侧却接受任意字符串。伪造成本低、影响是钓鱼/骚扰类安全事件。

**建议**：中继侧只接受**枚举 + 服务端可解析的目标**（如 `TargetUserId` 必须与 `AsyncTask.CreatedBy`/租户校验；`Group` 名必须由服务端函数生成，禁止透传字符串）；`all` 广播走独立的、有权限要求的生产接口并留审计；对 `group:` 前缀命名空间做白名单校验。

### F17【中】发布/重试通道全局串行，吞吐被单通道 + Semaphore 封顶

**证据**：`EventPublisher.cs:23`（`publishLock`）、`:192-231`（所有 `PublishAsync` 串行 + 单 `publisherChannel`）、`:64-106`（延迟发布同一把锁）；`EventSubscriber.cs:33`、`:625-638`（重试发布同样全局串行）。默认 `ConsumerDispatchConcurrency` 未在模板配置中给出（`RabbitOptions.cs:52`），失败即 `0`。

**影响**：每个进程的**发布吞吐上限 = 1 个 in-flight publish**（发布是网络 RTT 级别操作）。操作日志过滤器在**每个被注解的请求**里同步 `await PublishAsync`（`Filters/OperationLogActionFilter.cs:85`），所以高并发下日志发布本身会成为请求路径上的串行瓶颈；生产者确认（publisher confirm）开启又让每次发布必须等 broker 回执（`Connection.cs:100-104`）。

**建议**：用通道池（每线程/每分区一个 channel，channel 非线程安全但可池化）替代全局锁；操作日志改为"入本地队列 + 后台批量发布"，不要阻塞 HTTP 请求；给发布路径加超时与背压指标。

### F18【中】事件契约 = CLR 类型全名，消息无版本、无 ContentType

**证据**：路由键就是类型全名（`EventPublisher.cs:201`、`:211`、`:217`），订阅端同样以 `FullName` 建队与绑定（`EventSubscriber.cs:194`、`:294-297`）；`BasicProperties` 未设置 `ContentType`/`ContentEncoding`（`EventPublisher.cs:206-213`，重试复制时又原样搬回 `EventSubscriber.cs:609-619`）；消息体无 `schemaVersion`/`eventName` 稳定字段（`EventBus/EventBase.cs:10-17` 仅 `Id/TaskCode/TaskId/Data/CreateTime`）；序列化用默认 `JsonSerializer.Serialize(message)`（`:202`），反序列化用 `PropertyNameCaseInsensitive = true`（`:653-656`）。

**影响**：任何重命名/移动事件类（含程序集拆分——这正是"重建为微服务"必然发生的事）都会**改变路由键**：生产端发到没人订阅的 routing key（且按 F2 静默丢弃），消费端声明出新的空队列，旧队列积压无人处理。同时"发布端必须先加载消费端 Handler 程序集"是隐式耦合：`AddRabbitMQCodeManager`（`RabbitMQ/ServiceCollectionExtensions.cs:58-80`）是通过**扫描 `IEventHandler<>` 实现类的泛型参数**来注册事件类型，再用 `Activator.CreateInstance(eventType, task)`（`AsyncTaskService.cs:49`）构造消息——即生产者的契约认知来自消费者的 Handler 类型。

**建议**：引入显式契约注册（`eventName` 常量 + 版本，如 `notification.v1`），路由键用契约名而非 CLR 全名；消息带 `eventName`/`eventVersion`/`eventId`/`occurredAt`；序列化集中到一个 `IEventSerializer`（同时解决 F19、F24 的版本与可测试性问题）；生产者只依赖契约程序集，不再依赖 Handler 程序集。

### F19【中】消息体全量进日志 + 全文件插值字符串，PII 风险且无法结构化检索

**证据**：`EventSubscriber.cs:400`（`$"Message Received: {eventName} => {message}"`，Information 级，含完整消息体）、`:690`（`$"开始执行 {eventName} 事件, Handler: ..., 内容：{message}"`，第二条全量）、`:696`（异常分支再次带 `{message}`）；`EventPublisher.cs:39`（`BasicReturn` 里把退回的完整 body 打进 Error 日志）。同一文件大量使用 `LogInformation($"...")` 插值而非结构化占位符（`:209`、`:450`、`:474`、`:513`、`:518`、`:623`、`:702` 等）。

**影响**：操作日志事件的 `Data`/`Json` 字段包含请求参数（`Filters/OperationLogActionFilter.cs:75-84`——`Code/Content/Json/UserId/IpAddress/UserAgent`），通知事件包含业务文案与 userId；这些会被完整写进日志（再经 Serilog 落到 Seq/文件），构成 PII/敏感数据外泄面，并把日志体积放大一个数量级。插值字符串还让日志失去结构化字段、并在级别关闭时仍然构造字符串。

**建议**：日志只记录 `messageId/eventName/handler/queue/attempt` 等元数据，body 只在 Debug 且经过脱敏/截断（如 `BodyHash` + 前 N 字符）；统一改用结构化占位符；给 Serilog 加脱敏 enrich。

### F20【中】配置与密钥：仓库内没有 RabbitMQ/Redis/CORS 配置，但提交了配置中心明文密钥和生产内网地址

**证据**：全仓库 `*.json` 检索 `"RabbitMQ"`、`"Redis"`、`"Cors"` **零命中**——也就是说这个模板的 `appsettings*.json` **不含**任何消息/缓存配置，全部依赖 AgileConfig 远端下发（`ServiceCollectionExtensions.cs:346-407`）；而 AgileConfig 的引导配置（含 `secret`）是明文入库的：`BackgroundServices/NexusStack.MQService/appsettings.Development.json:2-12`（`"secret": "<已脱敏>"`、`"nodes": "http://<服务器IP>:8010"`）、`appsettings.Staging.json:4-5`（`"secret": "yF2mM4sS8gZ0uA0v"`、`http://<服务器IP>:5000`）、`appsettings.Test.json:4-5`（同一 secret）；生产模板 `appsettings.Production.json:4` 也是 `<已脱敏>`。

**影响**：① 模板开箱**跑不起来**（没有任何本地可用的 Redis/RabbitMQ 配置，也没有 docker-compose 起的依赖；必须先有 AgileConfig 且配好 appId/secret）；② 明文 secret 与内网拓扑进了模板仓库，任何基于此模板的项目都会继承这套凭据（"secret 是模板值"几乎必然被原样带进真实环境）；③ `Cors` 键缺失意味着 `WithOrigins(空)`（`ServiceCollectionExtensions.cs:157-167`），浏览器端连 Hub/Gateway 会被 CORS 拦掉，除非远端配置刚好补上；④ 配置在远端可热改，直接放大了 F13 的 fail-open 风险。

**建议**：模板给出**可本地运行**的一组默认配置（含 docker-compose：RabbitMQ + Redis + PostgreSQL）与 `.env`/user-secrets 占位；AgileConfig 的 appId/secret/nodes 改从环境变量读取并在缺失时降级为本地配置；把已泄露的 secret 视为已失效并轮换。

### F21【中】死代码与失效注册（会误导模板使用者）

**证据**
- `ServiceCollectionExtensions.cs:132-141`：注册 `AddMemoryCache` 并注释"SignalRNotificationService 依赖此服务"，但**全仓库不存在 `SignalRNotificationService` 类**（检索无命中），该注册无任何消费者。
- `ServiceCollectionExtensions.cs:262`、`:267`：`ExecuteSeedDataService`、`InitApiResourceService` 两个 `AddHostedService` 都被注释掉——前者是 F3 的直接原因，后者意味着 **`ApiResource` 表不会自动注册**，而 RBAC 的 API 权限键完全依赖它（`UserContextCacheService.cs:108-117`）；文档却宣称"API 资源自动注册 ✅ 已完成"（`Docs/RBAC-Design.md:561`）。
- `Services/Schedules/ScheduleTaskService.cs:37`：`CoreRedisConstants.ScheduleTaskCache.Format(cacheKey)` 对**已格式化**的键再格式化一次（`Infrastructure/Constants/CoreRedisConstants.cs:40` 模板为 `ScheduleTask:{0}` ⇒ 实际删的是 `ScheduleTask:ScheduleTask:{code}`），删错了对象。
- `Domain/NexusStack.Redis/IRedisService.cs:15` / `RedisService.cs:23-26`：`bool PingAsync()` 是同步方法却以 `Async` 命名（返回 `bool` 而非 `Task<bool>`）。
- `Infrastructure/Utils/StringExtensions.cs:108-113`：`GenerateToken(string userName, DateTimeOffset expirationDate)` **忽略两个入参**，只用 `RandomNumberGenerator` 生成 64 字节随机串——熵没问题，但签名撒谎，调用方会以为 token 绑定身份/过期。

**影响**：模板的第一批读者会相信注释与文档（"内存缓存被 SignalR 通知服务使用""种子与 API 资源自动注册""这个键被正确清除"），据此做出错误推断；`InitApiResourceService` 被注释这一条尤其危险——**没有任何 API 资源记录时，`ApiPermissionKeys` 为空，而 `RequestAuthorizeFilter.cs:131` 的判定是 `!Contains(apiKey)` → 403，于是 Strict 模式下所有接口全部 403**。这类"看起来能跑的静默失效"是模板质量的核心问题。

**建议**：删除无用注册与过期注释；被注释的 HostedService 要么启用要么删除（不要留 `//` 版本）；`InitializeAsync`/`InitApiResourceService` 必须在启动时幂等执行并输出结果日志；给出"从零到可用"的启动校验（启动时检测 ApiResource 为空并告警）。

### F22【中】可测试性极差：无任何测试项目、构造体内 sync-over-async、依赖静态 RedisHelper

**证据**
- 全仓库 **没有任何测试项目**（`**/*Test*.csproj` 零命中，仓库无 `tests/`）。
- 构造函数里同步等待异步：`EventPublisher.cs:34`（`CreateChannelAsync().GetAwaiter().GetResult()`）、`EventSubscriber.cs:51`（`CreateRetryPublishChannelAsync().GetAwaiter().GetResult()`）；`EventSubscriber` 还持有 `IServiceScopeFactory`，Handler 解析有 `GetService` + `Activator.CreateInstance` 双路径（`EventSubscriber.cs:665-666`）。
- Redis 是**进程级静态单例**：`Domain/NexusStack.Redis/ServiceCollectionExtensions.cs:42-43`（`new CSRedisClient(...)` → `RedisHelper.Initialization(...)`），`RedisService` 所有方法都是静态 `RedisHelper.*` 薄封装（`RedisService.cs:13-112`）——无法注入 fake，无法做多实例/多租户隔离测试。
- 消费路径全部依赖反射 + `Type.Find`（`EventSubscriber.cs:645`、`:682-692`）与静态时间（`DateTimeOffset.UtcNow`，`:564`、`EventPublisher.cs:83/212`）。

**影响**：模板宣称的"回归验证"（`Docs/superpowers/specs/2026-04-22-mapster-migration-design.md:253-259`：至少完成 WebAPI 启动验证、RBAC 接口正常返回）**没有任何自动化证据**；而幂等/重试/DLQ 这类最容易出错、也最值得测试的逻辑（F1/F2/F7）恰恰完全不可单测。重建时若保留这套结构，测试成本会一直高到没人写。

**建议**：把发布/订阅拆成可注入的接口（`IEventPublisher` 已有，需去掉构造期同步 I/O 与静态依赖）；`IRedisService` 背后用 `IConnectionMultiplexer`（StackExchange.Redis）或至少允许注入 `ICSRedisClient`；时钟注入 `TimeProvider`；至少补三类测试：多 Handler 重试路由（F1）、不可路由不 ACK（F2）、并发同键幂等（F7）；用 Testcontainers 跑 RabbitMQ/Redis 集成测试。

### F23【中】CSRedis 3.8.807 与自建封装的取舍（技术债提示）

**证据**：`Domain/NexusStack.Redis/NexusStack.Redis.csproj:10`（`CSRedisCore 3.8.807`）；`RedisService.cs` 暴露 `ScanAsync`（`:28-44`）以字符串键名 + `MGet` 组装 `ExpandoObject`（`:38-43`）、`SetAsync(..., int expireSeconds = -1)` 与 `SetAsync(..., TimeSpan)` 两套重载（`:51-59`）；`SAddAsync`（`:62-67`）与 `HSetAsync`（`:84-88`）在写完数据后**无条件**调用 `RedisHelper.ExpireAsync(key, expireSeconds)`，而 `expireSeconds` 的默认值是 `-1`（`:62`、`:84`，接口同：`IRedisService.cs:69`、`:102`）。CSRedis 的 `Expire(string,int)` 文档只写"过期(秒单位)"（NuGet 包 XML 文档：`CSRedisClient.Expire(System.String,System.Int32)`），即**直接透传 `EXPIRE`**——`-1` 的"不过期"约定只存在于 `Set` 重载里（`SetAsync` 自己处理了 `-1`），`Expire` 没有。而 Redis 对**负数 TTL 的语义是立即删除该键**，不是"设为永久"。

**影响**：`ScanAsync` 返回 `dynamic`（`:28`）——调用方失去类型与编译期检查。更值得注意的是 `SAddAsync/HSetAsync` 的默认参数：不传过期时间地调用，等于"写入集合/哈希后立刻 `DEL` 掉"，是个"看起来是'不过期'、实际是'删掉'"的陷阱签名（好消息是当前仓库内**这两个方法没有任何调用点**——检索 `SAddAsync|HSetAsync` 仅命中接口与实现自身，属潜在陷阱而非现存故障）。模板用户一旦照抄这个签名去写"永不过期的哈希缓存"，会在排查很久之后才发现数据消失。

**建议**：重建时选 `StackExchange.Redis`（生态/可观测性/与 Backplane 一致）或 `Microsoft.Extensions.Caching.StackExchangeRedis`；把"是否设置 TTL"改为显式 API（`SetWithTtlAsync` / `SetForeverAsync`），彻底禁用 `-1` 这类魔法值，尤其不要在"顺手 `Expire`"里用默认值；不要返回 `dynamic`。

---

## 文档承诺 vs 代码实现

> "承诺"取自文档原文，"实现"以代码为准。✅=兑现；⚠️=部分兑现/有条件；❌=未兑现。

| 文档与承诺（原文位置） | 代码实现 | 结论 |
|---|---|---|
| `Docs/MQ-Idempotency-Review.md:8` 幂等键 `mq:idempotent:{queueDimension}:{messageId}`，`SET NX` 获取 | `Domain/NexusStack.RabbitMQ/EventSubscriber.cs:560-568` 完全一致（含 `RedisExistence.Nx`） | ✅ |
| `:7` messageId 优先 MessageId → CorrelationId → body SHA256 降级 | `EventSubscriber.cs:547-558` 一致 | ✅ |
| `:15` 重复消息（重投/并发）仅一个 Nx 成功，其余 ACK 跳过 | `EventSubscriber.cs:448-453` 一致，返回 `ACQUIRED|/DUPLICATE|` 前缀驱动分支 | ✅ |
| `:16` 失败时 Delete 幂等键后重试 | `EventSubscriber.cs:489` + `:505` 一致 | ✅ |
| `:17` 业务成功但 ACK 失败：不 Nack、保留键、依赖重投去重 | `EventSubscriber.cs:471-476` 一致（有注释说明） | ⚠️ 逻辑在，但未 ACK 的消息在通道存活期间**不会被重投**，去重假设要等连接断开才生效（文档 82 行只写"假定未 ACK 的消息可能被再次投递"） |
| `:18` 未启用幂等时返回空串、不写 Redis、不删 key | `EventSubscriber.cs:542-545` + `:485` 一致 | ✅ |
| `:19`／`:61` 多 Handler 同事件按队列维度隔离，queueDimension 入口固定贯穿 | `EventSubscriber.cs:438-440` + `:447` + `:465/478` 一致 | ✅ |
| `:21`／`:64` **重试发布路由正确，与幂等维度一致，避免误路由** | 入 retry 队列正确（`:621-633`），但 retry 队列 DLX 回程用 `eventName`（`:255`）⇒ 扇出到该事件的**所有** Handler 队列 | ❌ 只覆盖了一半方向（见 F1），5 轮检查未发现 |
| `:22` Redis 删除失败 → 不重试、直接 DLQ | `EventSubscriber.cs:487-498`（`canRetry=false`）一致，且是安全取向 | ✅ |
| `:23` 重试已发布但 ACK 失败 → Nack 进 DLQ，避免与 retry 副本并存 | `EventSubscriber.cs:506-519` 一致 | ✅ |
| `:80` Redis 不可用：TryAcquire 抛错则重试/Nack（符合至少一次） | `EventSubscriber.cs:467-479`：异常落到 `HandleFailureAsync`，一致 | ✅ |
| `:81` queueDimension 退化为 RoutingKey 时"retry 可能无法路由" | 代码后果是"不可路由 + 无 return handler + 仍 ACK" ⇒ **消息丢失**（F2）；文档低估了严重度 | ⚠️ 描述与后果不符 |
| `:88` "最终结论：通过最终审查，无新增修改建议；可作为上线依据" | 至少 F1/F2 两处结论不成立 | ❌ 结论过强 |
| `:90-93` §5.6 手动重试需带 `messageIdOverride`，否则被幂等拦截 | `IEventPublisher.cs:18-19` + `AsyncTaskService.cs:85-86` 已实现（`{Code}:{Id}:retry:{Guid:N}`） | ✅（但见 F7：靠调用方自觉） |
| `:102`／`:109` 延迟消息用 TTL+DLX、按档位独立队列、无队头阻塞 | `EventPublisher.cs:142-183`：队列名 `delay.{tierKey}.{sanitized}`、队列级 `x-message-ttl`、DLX 回主交换机，与文档描述一致 | ✅ |
| `:110` 延迟队列无消费者 | `EnsureDelayedExchangeAndQueueAsync` 只声明不消费，一致 | ✅ |
| `:112` routing key 与绑定一致，DLX 用事件全名与订阅端 binding 一致 | `:173` 与 `EventSubscriber.cs:294-297` 一致 | ✅（同一"事件名扇出"特性，延迟场景是设计意图，重试场景是缺陷） |
| `:113` 并发声明幂等、安全 | `:146-182` 用 `delayedExchangeDeclared` + `declaredDelayQueues` 做了进程内去重（比文档描述更强） | ✅ |
| `Docs/RBAC-Design.md:3` "V1 实现已完成" | `Services/Users/UserContextCacheService.cs`、`Filters/RequestAuthorizeFilter.cs`、`Authentication/RequestAuthenticationTokenHandler.cs`、`Dtos/Users/UserContextCacheDto.cs` 均存在且逻辑与文档 5.3 一致 | ✅ |
| `:65` `UserToken.RoleId` 已完全移除 | 仓库内检索 `RoleId` 仅出现在 `UserRole/Permission` 语境外无 Token 用法；`Authentication/RequestAuthenticationTokenHandler.cs:50-60` 只放 `UserId/TokenId/PlatformType` | ✅ |
| `:164` `PlatformType` 为 `[Flags]`，成员 `Admin=1,Pc=2,Mini=4,Android=8` | `Infrastructure/Enums/PlatformType.cs:16` 额外有 **`All = 0`**，且两条代码路径语义冲突（F12） | ⚠️ |
| `:261` 登录只取 `r.IsEnable && (r.Platforms & platformType) != 0` 的角色 | `Services/Users/UserRoleService.cs:26-29` 一致 | ✅ |
| `:262` 无可用角色 → 拒绝登录 | `Services/Users/UserTokenService.cs:124-128` 一致 | ✅ |
| `:296`／`:595` 缓存 Key = `usercontext:{userId}:{platform}:v2` | 实际为 `{AssemblyName}:UserContext:{userId}:{platform}:v2`（`CoreRedisConstants.cs:10/30` + `UserContextCacheService.cs:33-36`），版本后缀 `:v2` 在 | ⚠️ 字面格式与文档不符（前缀由程序集名决定），机制在 |
| `:307` `ApiPermissionKeys` 形如 `routetemplate:HTTPMETHOD` | `UserContextCacheService.cs:108-117` 与 `RequestAuthorizeFilter.cs:120-130` 三方一致 | ✅ |
| `:354-372` 缓存失效矩阵（10 行逐项） | 逐项核对：`PermissionService.cs:119-127`、`RoleController.cs:153`、`UserController.cs:278/295/317/360/400`、`MenuController.cs:101/117-122/251`、`TokenController.cs:66` | ✅（**机制在**，但见 F10/F11：竞态与扇出成本未评估） |
| `:369` 退出登录只失效"该用户当前平台" | `TokenController.cs:66` 传了 `(PlatformType)CurrentUser.PlatformType` | ✅ |
| `:556` `RequestAuthorizeFilter` 实现完整 API 级校验、纯内存 O(1) | `Filters/RequestAuthorizeFilter.cs:113-138` 一致 | ✅ |
| `:559` 禁用用户立即生效 | 依赖 `DeleteAsync`（F10 竞态 + 10h TTL） | ⚠️ 尽力而为，非"立即" |
| `:561` "API 资源自动注册 ✅ 已完成"（`InitApiResourceService` 启动时扫描路由） | `HostedServices/InitApiResourceService.cs` 存在，但**注册被注释**：`ServiceCollectionExtensions.cs:267` ⇒ 默认模板不执行；无资源记录时 Strict 模式全量 403 | ❌ |
| `:583` 缓存重建失败"安全优先（宁可 403 也不要放行）" | 重建抛异常时认证处理器直接抛出（500），未转 403；同时存在 `Disabled/Relaxed` 配置化放行（F13） | ⚠️ 与原则不完全一致 |
| `Docs/SignalR-Notification-Design.md:3` "V1 设计已确认（**待实现**）" | Hub/中继/认证 Handler/中继服务均已实现：`SignalR/NotificationHub.cs`、`EventHandler/NotificationEventHandler.cs`、`Authentication/RequestAuthenticationSignalRTokenHandler.cs`、`Services/SignalR/NotificationRelayService.cs`、`ServiceCollectionExtensions.cs:187-197/315` | ✅ 且**已超出文档状态**（文档说"待实现"，代码已实现） |
| `:173`／`:189` UserIdentifier 必须来自服务端验证的 Claims，Hub 用专用 Scheme | `NotificationHub.cs:8-9`（`[Authorize(AuthenticationSchemes="Authorization-SignalR-Token")]`）+ `RequestAuthenticationSignalRTokenHandler.cs:44-58` 只从 Token 派生 Claim | ✅ |
| `:186` 优先 querystring `access_token`、回退 Bearer，且仅限 Hub 路径 | `RequestAuthenticationSignalRTokenHandler.cs:61-90`，`:64` 限定 `/hubs/notification` | ✅ |
| `:194` 只允许加入"自己有权"的 group，组名服务端按 Claim 计算 | Hub 侧符合：`NotificationHub.cs:19/25` 组名由服务端算，**且 Hub 无任何客户端可调用方法**（无越权 JoinGroup 面） | ✅（连接侧） |
| `:194` 同上，但**消费侧**组名来源 | `NotificationEventHandler.cs:98-111` 直接采信消息体 `target.group`／`all`，无授权 | ❌（推送侧，F16） |
| `:195` 广播需更高权限 | `NotificationRelayService.cs:44-57` 无任何权限校验 | ❌ |
| `:214` MQ → Handler 至少一次 | Handler 吞异常 ⇒ 不再重试（F15） | ⚠️ |
| `:231` 解析失败不可重试、直接 Fail | `NotificationEventHandler.cs:124-130` 一致 | ✅ |
| `:232` 网络瞬时失败"让异常抛出以触发 MQ 重试" | 所有异常一律被吞（`:124-130`） | ❌ |
| `:238-242` V1 单实例 MQService，无需 Backplane | 未注册 Backplane（`ServiceCollectionExtensions.cs:194-197`），且无单实例约束（F14） | ⚠️ 代码符合"未做 Backplane"，但风险无缓解 |
| `:244-253` 多实例需引入 Redis Backplane | 全仓库无 `AddStackExchangeRedis`/Backplane | ❌（文档已标注为后续项） |
| `:287-290` Core 侧 `NotificationEventHandler` 完成"解析 → 推送 → 更新状态" | `NotificationEventHandler.cs:37-122` 一致；`:290` 推荐的 `INotificationRelayService` 生产侧封装也已实现 | ✅ |
| `:311-312` 回滚策略：可关闭 Hub 映射、降级为仅记日志 | `ServiceCollectionExtensions.cs:313-317` 按 `CoreServiceType.MQService` 分支，可在装配层关闭 | ✅ |
| `Docs/superpowers/specs/2026-04-22-mapster-migration-design.md:3` 状态"已实施" | 全仓库 `AutoMapper` 零命中；`Repository/Mapping/MappingRepository.cs:41/113/151` 全部 `ProjectToType<T>(MapperConfig)`；`ServiceCollectionExtensions.cs:465-478` `AddAllMapster` | ✅ 迁移真实完成 |
| 同上 `:256` 完成标准"源码中不存在 `using AutoMapper;`/`ProjectTo<`/`CreateMap(`/`Profile`" | 检索 `AutoMapper` 无命中 | ✅ |
| 同上 `:258-259` "至少完成核心 WebAPI 项目启动验证""RBAC 基础接口正常返回 DTO" | 仓库**无任何测试项目**，无验证产物可查 | ⚠️ 无法证实（仅有人工/CI 之外的口头状态） |
| 同上 `:264` 后续可选：为关键映射加单元测试 | 未做 | ❌（列为可选，如实） |
| `Docs/superpowers/plans/2026-04-22-mapster-migration.md:34-725` 全部 40 个步骤复选框 | 全部仍是 `- [ ]`（未勾选），包括 `:680`"Mark design as implemented"本身 | ⚠️ 计划文档状态与实际（spec 称已实施、代码已迁移）**不一致**，作为"计划追踪"已失效 |
| `Docs/superpowers/specs/2026-07-06-remove-messagepack-design.md:13-16` 移除 MessagePack 包/注册/载荷转换 | `Domain/**` 内 `MessagePack` 零命中；`ServiceCollectionExtensions.cs:194-197` 只注册默认 JSON 协议；`NotificationEventHandler.cs:71-78` 已是 JSON 分支 | ✅ |
| 同上 `:24` "源码中不存在 MessagePack 包、注册或兼容引用" | 验证通过 | ✅ |
| `Docs/superpowers/plans/2026-07-06-remove-messagepack.md:26-91` 六个步骤 | 全部 `- [x]`，且 `:64` 的 `Payload = (string)null` 与代码 `NotificationEventHandler.cs:76` `(string?)null` 仅有可空注解差异 | ✅（本仓库中唯一一份状态与代码一致的执行计划） |
| `README.md:155/159/160` `--EnableRabbitMQ` / `--IncludeMQService` / `--IncludePlanTaskService` 参数 | `.template.config` 未在本区域核对（超出范围）；但 `--IncludePlanTaskService=true` 产出的服务在默认装配下不执行任何任务（F3） | ⚠️ 交付物与承诺不符（F3） |

---

## 值得保留

1. **消费幂等的"先占键、失败删键、成功留键"三段式**（`EventSubscriber.cs:447-479`）：这是本仓库最成熟的一段代码，正确处理了"业务成功但 ACK 失败"这一最容易被忽略的分支（`:471-476` 有明确注释），并显式承认"至少一次"语义。重建时必须原样保留这套状态机（改用 inbox 表落库后语义不变）。
2. **幂等键的队列维度隔离 + 入口一次性解析**（`EventSubscriber.cs:196`、`:438-440`）：键里带 `queueDimension` 让多 Handler 不互相误判；`:437` 的注释解释了为什么要与 `handlerType` 在同一次"视图"下解析，避免竞态。这种"把竞态窗口写进注释"的做法值得保留为团队习惯。
3. **重试/DLQ/延迟队列的完整拓扑**（主交换机 + `.dlx` + `.retry` + `.delayed`，`EventSubscriber.cs:216-272`、`EventPublisher.cs:142-183`）：Direct 交换机 + 每 Handler 独立队列 + `x-message-ttl` 死信重投 + 队列级 DLQ（7 天）是教科书式的组合；延迟消息用"每档位独立队列"规避了 TTL 队头阻塞（`Docs/MQ-Idempotency-Review.md:109` 的结论经代码核对成立），这比常见的"一个延迟队列混多档 TTL"高一个档次。
4. **DLQ 与重试的失败取向是"安全优先"**：`Redis 删键失败 → 不重试、进 DLQ`（`EventSubscriber.cs:487-498`）、`重试已发布但 ACK 失败 → Nack 进 DLQ`（`:506-519`）、`consumerTag/handler 映射缺失 → 降级 Nack 进 DLQ`（`:402-435`）——都选择"宁可进 DLQ 也不重复/丢失"，且每处都有日志与理由注释。
5. **`ACQUIRED|` / `DUPLICATE|` 前缀协议**（`EventSubscriber.cs:568`、`:448`、`:455`）：用一个字符串前缀把"幂等等价于"三态结果（重复/已获取/未启用）从 Redis 层传到失败处理层，简单、可读、可测——重建时用枚举替代字符串即可，思想保留。
6. **RBAC 的"预计算权限集 + 纯内存校验"**（`UserContextCacheService.cs:96-119` 预计算 `ApiPermissionKeys`；`RequestAuthorizeFilter.cs:113-138` O(1) 查表）：把 DB 查询从鉴权热路径彻底移出，方向完全正确；路由模板（而非 Action 名）作为权限键还顺手解决了同名重载 Action 冲突（`Docs/RBAC-Design.md:345-348`，代码 `RequestAuthorizeFilter.cs:120-130` 一致）。
7. **缓存版本后缀 `:v2`**（`UserContextCacheService.cs:31-36`）：改 Key 格式即自动淘汰旧缓存，不需要 `FLUSHDB`，是个便宜且好用的演进机制。
8. **SignalR Hub 的最小暴露面**：`SignalR/NotificationHub.cs` 只重写 `OnConnectedAsync`，**没有任何客户端可调用的 Hub 方法**，组名由服务端从 Claim 计算（`:19`、`:25`），连接强制走专用认证方案（`:8`）；querystring token 只在 `/hubs/notification` 路径生效（`RequestAuthenticationSignalRTokenHandler.cs:64`）。默认配置是安全的——客户端无法自行加组，也拿不到别人的组。
9. **专用 SignalR 认证 Scheme 与 HTTP 认证 Scheme 分离**（`ServiceCollectionExtensions.cs:179-192`）：`Authorization-Token`（WebAPI）与 `Authorization-SignalR-Token`（MQService）互不污染，正是 `Docs/SignalR-Notification-Design.md:183-189` 的落地，避免了为 Hub 改动既有 HTTP 认证行为。
10. **登录前置校验顺序**：`验密 → IsEnable → 平台角色非空`（`UserTokenService.cs:113-128`）有明确的安全意图（不向被禁用账号暴露角色信息），且注释写明了理由；`GenerateToken` 用 `RandomNumberGenerator` 生成 64 字节随机 token（`StringExtensions.cs:108-113`），熵是够的。
11. **密码哈希与盐**：`SHA256(salt ‖ password)` + 每用户 32 字节随机盐（`StringExtensions.cs:53-66`、`:119-123`），比模板里常见的 MD5 明文强；重建时应升级为 PBKDF2/Argon2，**保留 per-user salt 与"先取盐再哈希"的结构**——同时注意当前比对用的是 `user.Password.Equals(pass)`（`Services/Users/UserTokenService.cs:113`），并非常量时间比较，切换算法时应一并改掉。
12. **Mapster 迁移的干净收尾**：全仓库 `AutoMapper`/`MessagePack` 零残留，`ProjectToType<T>(MapperConfig)` 在通用仓储里全量替换（`Repository/Mapping/MappingRepository.cs:41/82/113/151/161/166`），并给出了命名中性化（`IAutoMapperRepository`→`IMappingRepository`）。**这是"设计文档 → 计划 → 代码"链条在本仓库里唯一完整闭环的例子**，流程本身值得继承。
13. **Cron 抽象本身的分层是对的**：`CronScheduleService`（`Schedules/CronScheduleService.cs:23`）把"表达式解析（Cronos）+ 单例开关 + 分布式锁 + 执行记录 + 失败隔离"集中在基类，子类只写 `Expression`/`Singleton`/`ProcessAsync`（`DailySchedule.cs:27-50`），并支持运行时从缓存热更新表达式（`:187-190`）；异常隔离在循环内（`:163-179`），单任务失败不会拖垮进程。骨架可留，按 F4/F5 修实现。
14. **`RabbitOptions` 把幂等/重试参数配置化**（`RabbitOptions.cs:57-77`）：`MaxRetryCount`、`RetryDelayMilliseconds`、`EnableConsumerIdempotency`、`ConsumerIdempotencyExpireHours`、`DelayedExchangeName` 都是可配项，且对 `ConsumerIdempotencyExpireHours <= 0` 有保护（`EventSubscriber.cs:561`）——边界意识在。

---

## 缺失项

1. **Outbox / Inbox**（F6）：无出站消息表、无入站去重表，DB 与消息的一致性靠"运气 + 调用方顺序"。
2. **DLQ 消费者与重投工具**：DLQ 有 7 天 TTL、**无任何消费者**（`EventSubscriber.cs:231-244`），全仓库没有查看/重投/清理 DLQ 的入口（既无 API 也无命令）。生产上等于"毒消息静默消失"，与文档 §5.4 对 DLQ 的依赖（`Docs/MQ-Idempotency-Review.md:74/76`）不匹配。
3. **消息可观测性**：无事件计数/失败率/重试次数/队列深度的指标暴露（无 OpenTelemetry/Meter/健康检查），`ProcessEvent` 的埋点只有文本日志（`EventSubscriber.cs:690-702`）；RabbitMQ 的 `BasicReturn`（F2）也只是日志。grep 全仓库无 `ActivitySource`/`Meter`/`AddHealthChecks`。
4. **分布式追踪与关联 ID 贯通**：`CorrelationId` 只放 `message.Id`（`EventPublisher.cs:210`），没有把 HTTP `traceId`/`x-correlation-id` 从入口带到 MQ 再到日志/DLQ（`Docs/SignalR-Notification-Design.md:133` 提到的 `meta.traceId` 也只是"可选"且仅存在于通知载荷）。
5. **每事件类型的可靠性策略**：重试次数/延迟/是否幂等/DLQ-TTL 全是全局单值（`RabbitOptions.cs:57-72`），无法对"通知"和"操作日志"分别设定；也没有退避（固定 `RetryDelayMilliseconds=5000`，无指数/抖动）。文档只描述单一策略（`Docs/MQ-Idempotency-Review.md:63`）。
6. **消费顺序/分区语义**：仅有 `ConsumerDispatchConcurrency`（`EventSubscriber.cs:378` 还被复用为 QoS prefetch，见 F17 附注——把"分发并发"当"预取数"是两个不相关的旋钮），无单分区有序消费选项，也没有文档声明"同一实体的消息可能乱序"这一事实（`Docs/MQ-Idempotency-Review.md` 全篇未讨论顺序）。
7. **消息大小与背压策略**：无最大消息体限制、无 `BasicQos` 之外的流控、无"日志类事件改用批量/采样"的开关（F17/F19）。
8. **认证/授权的生产级能力**：无刷新令牌轮换/复用检测（`UserTokenService.cs:229-254` 只按字符串查 + 一次性置 `RefreshTokenIsAvailable=false`）、无登录失败限流/锁定、无 token 主动吊销列表、无审计日志落库的权限变更轨迹（`Docs/RBAC-Design.md:579` 只把它写成"建议"）。Token 直接明文存库（`UserTokenService.cs:167-171`：`Token` 字段写明文，另存 MD5 `TokenHash`），DB 泄露即等于账号被接管。
9. **数据级授权（DataRange）的执行**：文档设计了 `DataRange`（`Docs/RBAC-Design.md:200-207`）与"多角色取最宽松"的合并规则（`:210-215`），但**只落在 `Permission.DataRange` 字段与 DTO**；检索 `DataRange` 的全部命中仅 4 类：实体/枚举定义（`Core/Entities/Users/Permission.cs:27/43`）、种子数据（`SeedData/PermissionSeedData.cs:33/40/55`）、写入映射（`Services/Users/PermissionService.cs:71-88/158/221`）、DTO（`Dtos/Permissions/PermissionDto.cs:29`、`ChangeRolePermissionDto.cs:19`）——**没有任何"按 DataRange 过滤查询"的实现**。配套的 `ICurrentUser.RegionIds` 同样只被"定义 → 填充 → 暴露"三步走完就断了（`Infrastructure/ICurrentUser.cs:18`、`Core/CurrentUser.cs:102`、`UserContextCacheService.cs:127`，无查询侧消费点）。这是文档里最大的一处"设计了但没实现"，也是重建时必须真正落地的一块（否则"多平台 + 多角色 + 层级组织 + 数据范围"四维度只兑现了三维度）。
10. **测试与契约测试**（F22）：无单元/集成/契约测试；没有"事件契约"快照或兼容性校验。
11. **本地可运行的开发栈**：无 docker-compose（只有两个只 `COPY publish/` 的 Dockerfile：`NexusStack.MQService/Dockerfile:1-7`、`PlanTaskService/Dockerfile`），无 RabbitMQ/Redis 默认配置（F20），模板开箱不可运行。
12. **多租户/多服务命名空间**：队列名、交换机名、Redis 键前缀（`CoreRedisConstants.cs:10` 只按程序集名，`ScheduleTask:{0}` 甚至完全没前缀，`:40`）都没有 service/tenant 维度——拆分服务时必然冲突（F14 延伸）。
13. **优雅关闭的消息语义**：`EventSubscriber.DisposeAsync`（`:116-180`）关闭通道但**不等待在途 Handler 完成**（Handler 在 `ProcessEvent` 的独立 Scope 里，`:664`），关闭瞬间在途消息会被 broker 重投（幂等键在，尚可），但没有 drain/shutdown 超时配置；`MassTransit`/`RabbitMQ.Client` 的 `ConsumerDispatchConcurrency` 也没有配套的关闭参数。
14. **WebAPI/生产者的 RabbitMQ 断言**：`AddRabbitMQ` 对 WebAPI 也注册了单例 `EventPublisher`（`RabbitMQ/ServiceCollectionExtensions.cs:24-25`，WebAPI 分支不订阅但会发布），而**拓扑声明（交换机/队列）分散在发布端与订阅端两处重复**（`EventPublisher.cs:236-239` 与 `EventSubscriber.cs:373-376` 各自 `ExchangeDeclare`）——没有集中式拓扑声明（`ITopologyProvisioner`），拆服务后极易出现"生产者先启动、用不同参数声明同名交换机"导致 406（F8 的触发源）。

---

## 风险清单

| 严重度 | 问题 | 位置 |
|---|---|---|
| 严重 | 重试消息经 retry 队列 DLX 回主交换机时以事件全名路由，扇出给该事件的**所有** Handler 队列（非仅失败者），重复执行只靠各 Handler 的 Redis 幂等键兜底 | `Domain/NexusStack.RabbitMQ/EventSubscriber.cs:255`、`:294-297`、`:621-633`；文档盲区 `Docs/MQ-Idempotency-Review.md:21/64` |
| 严重 | 不可路由消息被静默丢弃：发布端 `BasicReturn` 只记日志；重试通道无 return 处理却 `mandatory:true`，发布后仍 ACK 原消息 ⇒ 消息丢失但"成功" | `Domain/NexusStack.RabbitMQ/EventPublisher.cs:36-41`、`EventSubscriber.cs:384-393`、`:505-510`、`:628-633` |
| 严重 | 无 outbox/inbox：`Insert` 成功后 `Publish` 失败即产生永久 Pending 的孤儿 AsyncTask，无补偿/对账 | `Domain/NexusStack.Core/Services/AsyncTasks/AsyncTaskService.cs:47-54` |
| 严重 | PlanTaskService 默认不执行任何任务（`ExecuteSeedDataService` 注册被注释 ⇒ 调度 Redis 键永不写入），且 miss 时静默跳过 | `Domain/NexusStack.Core/ServiceCollectionExtensions.cs:257-263`、`:262`；`Schedules/CronScheduleService.cs:101-107`；`SeedData/ScheduleTaskSeedData.cs:29` |
| 严重 | SignalR 无 Backplane + 队列名=Handler 全名 ⇒ 多实例 MQService 竞争消费，跨实例推送静默丢失；无单实例约束 | `Domain/NexusStack.Core/ServiceCollectionExtensions.cs:194-197`、`:313-317`；`EventSubscriber.cs:196`；`Docs/SignalR-Notification-Design.md:244-253` |
| 严重 | 推送路由完全信任消息体的 `target.group`/`all`，可越权向任意用户/组/全员推送（伪造通知） | `Domain/NexusStack.Core/EventHandler/NotificationEventHandler.cs:98-111`；`SignalR/NotificationRelayModels.cs:12-28`；`Services/SignalR/NotificationRelayService.cs:44-57`；对照 `Docs/SignalR-Notification-Design.md:194-195` |
| 高 | 消费幂等键绑定业务标识（`TaskCode:TaskId`，24h），合法的同键重发被静默丢弃；作者已用 `messageIdOverride` 逐个绕开 | `EventPublisher.cs:112-126`；`EventSubscriber.cs:560-568`；`AsyncTaskService.cs:85-86`；文档 `MQ-Idempotency-Review.md:90-93` |
| 高 | 通道/消费者无自愈：通道关闭后只清字典、不重订阅、发布端不重建通道 ⇒ 需人工重启 | `EventSubscriber.cs:319-368`、`EventPublisher.cs:24/34`（对照 `Connection.cs:72-78` 仅有连接级恢复） |
| 高 | 缓存击穿无单飞/无锁：鉴权热路径 miss 后并发回源打 DB（4~5 次查询/请求） | `Domain/NexusStack.Core/Services/Users/UserContextCacheService.cs:38-49`、`:70-131`；`Authentication/RequestAuthenticationTokenHandler.cs:47` |
| 高 | 缓存失效为裸 DEL，与并发回源竞态 ⇒ 被禁用/被回收权限的用户最长 10h 仍可通行，与"禁用立即生效"承诺不符 | `UserContextCacheService.cs:29/58-68`；`Host/NexusStack.WebAPI/Controllers/UserController.cs:317`；文档 `RBAC-Design.md:559` |
| 高 | Cron 分布式锁硬编码 60s TTL 且不续租，执行超时 ⇒ 另一节点重复执行同一 occurrence | `Domain/NexusStack.Core/Schedules/CronScheduleService.cs:116`、`:136` |
| 高 | 鉴权存在配置化 fail-open（`Relaxed`/`Disabled` 直接放行且仅 Warn；默认 `RootOnly` 而非 Strict），配置由远端 AgileConfig 热下发 | `Domain/NexusStack.Core/Filters/RequestAuthorizeFilter.cs:87-111`；`Infrastructure/Options/ApiAuthorizationOptions.cs:21/27`；`ServiceCollectionExtensions.cs:354-407` |
| 高 | Handler 吞掉全部异常 ⇒ 推送失败永不重试，"网络瞬时失败抛异常触发重试"的文档承诺未实现；MQ 重试/DLQ 对业务 Handler 失效 | `Domain/NexusStack.Core/EventHandler/NotificationEventHandler.cs:124-130`；`EventSubscriber.cs:692-705`、`:461`；对照 `Docs/SignalR-Notification-Design.md:232` |
| 高 | "API 资源自动注册"承诺未兑现（`InitApiResourceService` 注册被注释）；无资源记录时 Strict 模式全量 403 | `Domain/NexusStack.Core/ServiceCollectionExtensions.cs:267`；`HostedServices/InitApiResourceService.cs:23`；对照 `Docs/RBAC-Design.md:561` |
| 中高 | 角色权限变更按用户 × 平台逐条 DEL（无 pipeline/无重试/请求内串行），失效成本随用户规模线性上升；`PlatformType.All=0` 再多删一键 | `Domain/NexusStack.Core/Services/Users/PermissionService.cs:119-127`；`UserContextCacheService.cs:66-67`；`Host/NexusStack.WebAPI/Controllers/RoleController.cs:153` |
| 中高 | `PlatformType.All = 0` 语义冲突：配置侧当"全平台"、登录侧按位与永远不匹配 ⇒ 该角色无法登录；文档未定义 `All` | `Infrastructure/Enums/PlatformType.cs:16`；`Services/Users/PermissionService.cs:48/94`；`Services/Users/UserRoleService.cs:28`；`Docs/RBAC-Design.md:164` |
| 中高 | 事件契约=CLR 全名，消息无 `eventName`/版本/`ContentType`；重命名或拆程序集即静默断链（叠加发布端不可路由） | `EventPublisher.cs:201/211/217`、`:206-213`；`EventSubscriber.cs:194/294-297`；`EventBus/EventBase.cs:10-17` |
| 中 | 发布/重试通道全局串行（单通道 + Semaphore），操作日志在每个请求内同步发布 ⇒ 成为请求路径瓶颈 | `EventPublisher.cs:23/192-231`；`EventSubscriber.cs:33/625-638`；`Filters/OperationLogActionFilter.cs:85` |
| 中 | 消息体全量写日志（含操作日志参数、请求 JSON、userId、IP）+ 大量插值字符串 ⇒ PII 外泄与日志膨胀、无结构化检索 | `EventSubscriber.cs:400/690/696`；`EventPublisher.cs:39`；`Filters/OperationLogActionFilter.cs:75-84` |
| 中 | 明文密钥与生产内网地址入库，且模板内无 RabbitMQ/Redis/CORS 配置 ⇒ 开箱不可运行、凭据随模板扩散、浏览器连接被 CORS 阻断 | `BackgroundServices/NexusStack.MQService/appsettings.Development.json:2-12`、`appsettings.Staging.json:4-5`、`appsettings.Test.json:4-5`、`appsettings.Production.json:4`；`ServiceCollectionExtensions.cs:157-167/346-407` |
| 中 | 死代码/失效注释误导：不存在的 `SignalRNotificationService`、被注释的 `ExecuteSeedDataService`/`InitApiResourceService`、双 `Format` 删错键、`PingAsync` 同步、`GenerateToken` 忽略入参 | `ServiceCollectionExtensions.cs:132-141/262/267`；`Services/Schedules/ScheduleTaskService.cs:37`；`Domain/NexusStack.Redis/IRedisService.cs:15`；`Infrastructure/Utils/StringExtensions.cs:108-113` |
| 中 | 可测试性差：无任何测试项目、构造体内 sync-over-async、Redis 为进程级静态单例、时间/反射硬依赖 ⇒ 幂等与重试逻辑无法回归 | 仓库无 `*Test*.csproj`；`EventPublisher.cs:34`；`EventSubscriber.cs:51/645/682`；`Domain/NexusStack.Redis/ServiceCollectionExtensions.cs:42-43` |
| 中 | 数据级授权（`DataRange`）只在 DB/DTO 与文档中存在，无任何查询过滤实现；`RegionIds` 无消费点 | `Docs/RBAC-Design.md:200-215`；`Dtos/Users/UserContextCacheDto.cs:33`；`UserContextCacheService.cs:91-94`（仓库内无 DataRange 查询过滤） |
| 中 | Token 明文入库（同时存 MD5 `TokenHash`）；RefreshToken 无轮换/复用检测、无登录限流 | `Services/Users/UserTokenService.cs:167-171`、`:229-254`；`Infrastructure/Utils/StringExtensions.cs:73-89` |
| 中 | 无 DLQ 消费者/重投工具、无指标与健康检查、无 traceId 贯通 ⇒ 毒消息静默消失、故障不可观测 | `EventSubscriber.cs:231-244`；全仓库无 `ActivitySource/Meter/AddHealthChecks` 命中；`EventPublisher.cs:210` 仅放 `message.Id` |
| 中 | `ConsumerDispatchConcurrency` 被同时当作 QoS prefetch（两个无关旋钮耦合），配置缺失时默认 0→prefetch=10 | `EventSubscriber.cs:378`；`Connection.cs:96-104`；`Domain/NexusStack.RabbitMQ/RabbitOptions.cs:52` |
| 低 | 拓扑声明分散在发布端与订阅端重复（无集中 provisioner），生产者先启动且参数不一致即 406（并触发 F8） | `EventPublisher.cs:236-239`；`EventSubscriber.cs:373-376`；`RabbitMQ/ServiceCollectionExtensions.cs:24-26` |
| 低 | `RedisService.SAddAsync/HSetAsync` 写完即无条件 `ExpireAsync(key, expireSeconds)`，默认 `-1` 被透传为 `EXPIRE key -1`（Redis 负 TTL = **立即删除该键**），即"默认调用=写完就删"；`ScanAsync` 返回 `dynamic`（当前仓库内这两个方法无调用点，属潜在陷阱） | `Domain/NexusStack.Redis/RedisService.cs:62-67`、`:84-88`、`:28-44`；`IRedisService.cs:69`、`:102` |
| 低 | 计划文档复选框 40 处全未勾选（含"Mark design as implemented"），与 spec"已实施"及代码状态不一致 | `Docs/superpowers/plans/2026-04-22-mapster-migration.md:34-725`；对照 `Docs/superpowers/specs/2026-04-22-mapster-migration-design.md:3` |
| 低 | 优雅关闭不 drain 在途 Handler（关闭通道即返回），在途消息依赖 broker 重投 | `EventSubscriber.cs:116-180`、`:664` |
| 低 | 泛型事件类型的 queue/routing 名会带上 `[[...]]`（`SanitizeEventTypeName` 只替换空格/逗号/反引号） | `EventPublisher.cs:136-140`；`EventSubscriber.cs:194` |
