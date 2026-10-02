# Pricing 查询缓存与跨实例失效：一手资料研究

资料核验日期：2026-10-02。客户端按 **StackExchange.Redis 2.13.17** 固定版本源码核验，数据库语义按 PostgreSQL 18。本文是研究与设计建议，不是已经通过的验收，也不代表已确定 Redis 生产版本、部署拓扑或性能收益。外部事实附官方来源；协议判断是结合本仓代码作出的推论。

**建议：普通 Pricing 展示查询采用“本地事务登记失效意图 + 共享 Redis 缓存 + 填充令牌 + 固定 TTL”，明确最终一致；需要提交后立即读新值的调用直接查所属数据库。** 每次先查数据库 `Version` 再读版本地址的方案也能成立，但当前查询只是单行主键读取，不能把它说成减少数据库请求的缓存。

## 1. 仓库约束与选择

研究时的 [PriceQuote](../../src/Services/Pricing/NexusStackNext.Pricing.Domain/PriceQuote.cs) 包含成本、费率、输入版本、计算版本、保本价和上游成本版本；[PriceQuoteView](../../src/Services/Pricing/NexusStackNext.Pricing.Application/PricingRequests.cs) 全部来自这个聚合。[GetPriceQuote 现有实现](../../src/Services/Pricing/NexusStackNext.Pricing.Infrastructure/PricingServices.cs) 是 `AsNoTracking` 单行主键查询。[ADR-0011](../adr/0011-optimistic-concurrency-in-the-aggregate.md) 要求每次可观察状态改变才增加聚合 `Version`。

因此缓存缝当前只属于 Pricing；依 [AGENTS.md](../../AGENTS.md) 的第二消费者规则，不应为了这一个消费者抽取万能缓存框架。缓存只能承载查询结果，命令、任务领取、业务幂等、成本来源所有权和权限校验仍执行原有协议。

| 方案 | 缓存命中仍查 PostgreSQL | 提交后新请求 | 主要代价 |
|---|---|---|---|
| A：每请求读取 `Version`，再读版本地址 | 是，至少一次 | 主库版本查询快照以前的已提交变化可见 | 多一次 Redis RTT；当前小查询未必更快；旧版本 key 占容量 |
| B：同事务失效记录 + 后台删除 + 填充令牌 | 否 | 删除传播前允许短暂旧值，固定 TTL 兜底 | 增加技术表、worker、重试与积压观察 |
| 直接查库 | 是，一次 | PostgreSQL 单次读取语义 | 无 Redis 命中收益，也没有缓存一致性协议 |

此处取舍是设计建议。Microsoft 的 Cache-Aside 文档明确指出一般缓存旁路不保证与数据库始终一致，并提醒区分授权等敏感数据以及缓存失效策略。[Microsoft：Cache-Aside](https://learn.microsoft.com/en-us/azure/architecture/patterns/cache-aside)

## 2. 方案 A：版本地址的正确承诺

建议算法：在 PostgreSQL 主库通过新的无跟踪投影读取 `Version`；不存在则直接返回不存在。构造 `环境/数据库代次/pricing/视图格式/ItemId/Version` 键。命中时验证载荷 ItemId、Version、格式；未命中时读完整聚合，**以该完整行实际携带的 Version 写键**，不能把后一次查到的新数据写到前一次探测的旧版本键。

PostgreSQL 默认 Read Committed 的普通 `SELECT` 看到语句开始前已提交的数据；前后两条 `SELECT` 可以看到不同快照。因此版本探测后若发生更新，当前读允许返回探测时的旧版本，也可能在回源时返回新版本；它不承诺“响应发出那一刻绝对最新”。前提是读主库、使用新的语句快照，不把长事务旧快照或复制延迟隐藏在该承诺里。[PostgreSQL：Read Committed](https://www.postgresql.org/docs/18/transaction-iso.html#XACT-READ-COMMITTED)

**推论。** 不需要 Pub/Sub：提交后新请求取得新版本地址，旧填充只能写旧地址，不会污染新地址。若 `Version` 遗漏了一种可见变化、视图以后依赖别的聚合、同 ID 被删除重建或版本因数据库恢复而重复，这个推论就不再成立。当前小视图已经是单行读取；版本投影仍要查数据库，收益必须由负载测试证明，不能宣称“命中零数据库访问”。

## 3. 方案 B：最终一致且防止旧填充复活

### 写入与传播

建议每次 Pricing 聚合变化，在保存业务数据的同一 PostgreSQL 事务内登记失效记录，记录至少包含稳定 ID、ItemId、Version、登记时间。技术记录留在 Pricing 数据库。成本事件消费、费率更新、手工成本更新、任务计算完成均需覆盖；回滚时业务与失效记录一起回滚。新建也登记，避免未来引入负缓存后漏掉创建路径。失效仅针对本上下文自己的视图。

后台 worker 的顺序固定为：读取待处理记录 → 向 Redis 删除对象缓存键 → 确认成功后删除或标记那一条数据库记录。Redis 错误或结果未知时保留记录重试；Redis 已删而进程在数据库确认前崩溃，会重复删除，功能上安全。Redis 返回“键不存在”也表示删除目的已达到。确认条件必须指向实际领取的记录，不能顺手清掉同对象刚刚登记的新版本意图。

以上是本地事务与至少一次处理的设计推论，不是 PostgreSQL/Redis 跨系统事务。Redis 官方说明复制通常异步，即使用 `WAIT` 也不能把 Redis 变成强一致系统，切换时仍可能丢失已确认写入。因此“已确认失效”后 Redis 恢复旧副本，可能重新出现旧值；固定 TTL 仍不可省略。[Redis：复制与确认限制](https://redis.io/docs/latest/operate/oss_and_stack/management/replication/)

### 读取与填充

建议一对象一键，键名包括环境、数据库代次、上下文、视图格式和 ItemId。值与填充令牌使用同一个 key；可以选带明确状态的 hash，也可选区分令牌/结果的字符串封装：

1. 有合法结果则返回；GET 不续 TTL。不合法载荷按 miss 处理，并记录固定类别指标。
2. 没有 key 时，原子创建随机填充令牌并设短 TTL；得到令牌后才开始新的 PostgreSQL 主库读取。
3. 没取得令牌的请求可以短暂等待后重读，也可以受限回源，但不能自行写入缓存。等待次数、时间和并发都有上限。
4. 取得令牌的请求读取并构建视图后，用单 key Lua 比较令牌；仅相同才写结果并设置固定 TTL。令牌不存在或不同必须失败，不能在 CAS 失败后无条件 `SET`。
5. 失败或取消只允许比较令牌后删除自己的占位；也可让短 TTL 回收。查无此项首轮不缓存，避免引入额外负缓存契约。

Redis `SET` 支持 `NX` 与过期参数；Lua 执行在 Redis 中具有原子性。脚本必须短小，key 通过 `KEYS` 参数传入，参数通过 `ARGV` 传入；不要为每个 ItemId 拼接新脚本源码。脚本缓存可能因重启/切换丢失，不能假设 `EVALSHA` 永远命中。[Redis：SET](https://redis.io/docs/latest/commands/set/)、[Redis：Lua](https://redis.io/docs/latest/develop/programmability/eval-intro/)

**竞态判断：**

| 时间交错 | 正确结果 |
|---|---|
| 旧查询先获令牌、读旧数据；新提交的失效随后删 key | 旧查询 CAS 失败，不能重新写旧值 |
| 旧查询先完成填充；随后处理新提交的失效 | 删除旧结果，下次重新填充 |
| 失效先删 key；新查询再获令牌、读主库新快照 | 查询得到失效所对应的提交或更晚状态 |
| TTL、淘汰或人工删 key 发生在填充中途 | 旧令牌失效，旧填充不能重建 key |
| 删除与 worker 确认之间崩溃 | 重试删除；可能误删刚填的新值，影响命中率，不使旧值变成正确值 |
| 多实例重复或乱序处理失效 | 无条件删除仍安全；不需要按 Version 在 Redis 实现另一套业务状态机 |

**边界。** 数据库读取必须在获令牌之后使用新快照；若沿用早已开始的 Repeatable Read 事务，第三行推论失效。Redis failover 回滚删除或令牌状态也超出这张“单一 Redis 连续历史”的竞态表。限制填充总时长、令牌 TTL 和结果 TTL，且禁止命中续期，才能避免长期保留历史值。实际旧值窗口包括传播积压、并发旧读取、短填充租约及 Redis 故障恢复；没有测量与前提约束时不得给出严格毫秒级上限。

普通查询在提交后允许传播窗口；对提交结果有立即读取要求的接口应直接查库，或提供显式强读路径。方案 B 不自动提供 read-your-writes，不应用于撤权、扣款判断、任务所有权判断。即使在 UI 展示了旧 Version，后续写命令也必须保留原有乐观并发检查。

### 为什么不把 Pub/Sub 当成可靠失效

Redis Pub/Sub 是至多一次投递，订阅端断线时消息会丢失；重连不会补发断线期间通知。建议首轮不加 L1 本地缓存，所有实例读同一个 Redis key，后台可靠删除该 key 即可；不需要额外广播协议。将来需要 L1 时再单独设计断线期间的失效补偿或短 TTL。[Redis：Pub/Sub 交付语义](https://redis.io/docs/latest/develop/pubsub/#delivery-semantics)

## 4. 恢复数据库与容量

**恢复风险。** PostgreSQL PITR 可恢复到过去某一时刻，恢复后的业务 Version 不具有“跨数据库历史唯一”保证。[PostgreSQL：PITR](https://www.postgresql.org/docs/18/continuous-archiving.html) 本仓 Version 是普通聚合属性，因此 Redis 留存的 `Version=8` 可能对应另一条数据库历史；方案 B 的旧结果与已确认失效状态同样可能错配。

建议设置由部署配置管理的 **CacheGeneration**，在数据库恢复、复制为新环境、重建库或允许同 ID 重建时统一换代，再开放流量。代次不能只存在于会被一起还原的备份表里。所有实例共享新代次；旧实例先摘流并停止其 worker。不要在共享 Redis 上 `FLUSHALL`；新命名空间直接隔离旧 key，旧 key 由 TTL 回收。协议/序列化格式升级另用格式版本，不能假设 ItemId 和聚合 Version 足够隔离应用版本。

Redis 的 `maxmemory` 与 `maxmemory-policy` 控制淘汰；默认或部署配置不保证业务预期的容量上限。TTL 管键寿命，不能单独限制流量高峰的总占用。[Redis：Key eviction](https://redis.io/docs/latest/develop/reference/eviction/) 建议缓存专用容量、固定 TTL 加有界随机抖动、单结果字节上限。方案 A 需预算“活跃对象 × TTL 内版本数 × 载荷/键开销”；方案 B 约为“活跃对象 × 载荷/鍵开销”，但还要预算 PostgreSQL 失效积压。

Redis 过期使用绝对时间戳，时钟跳变会影响过期行为；TTL 不是跨错误时钟的业务正确性证明。[Redis：EXPIRE](https://redis.io/docs/latest/commands/expire/) 不给历史结果做滑动续期。建议先以明确可配置值实现，例如结果 30 秒、填充 5 秒，再按允许陈旧程度与负载调整；这些数字是起始建议，不是生产实测结论。

## 5. StackExchange.Redis 2.13.17 接入核验

该版本 NuGet 包存在，并提供 .NET 10 目标；固定版本源码 LICENSE 为 MIT。客户端许可不能用来代表 Redis 服务端许可。Redis 官方当前区分：7.2 及以前 BSD-3-Clause，7.4 系列 RSALv2/SSPLv1，8 起增加 AGPLv3 选择；生产镜像版本和许可需独立登记，不能沿用客户端 LICENSE 附注中旧的服务端 BSD 描述。[NuGet 2.13.17](https://www.nuget.org/packages/StackExchange.Redis/2.13.17)、[客户端固定版本 LICENSE](https://github.com/StackExchange/StackExchange.Redis/blob/2.13.17/LICENSE)、[Redis 官方许可表](https://redis.io/legal/licenses/)

| 项目 | 核验事实与建议 |
|---|---|
| 生命周期 | `ConnectionMultiplexer` 线程安全、设计为共享重用；宿主注入单例，宿主结束时释放，不能每请求创建连接。[固定版本 Basics](https://github.com/StackExchange/StackExchange.Redis/blob/2.13.17/docs/Basics.md) |
| 初次掉线 | 配置 `AbortOnConnectFail=false`，让无法立即连通时仍能保留 multiplexer 重连；不表示 Connect 立即返回或任何配置错误都被吞掉。初次连接设置有限 ConnectTimeout/ConnectRetry，并避免请求热路径循环创建连接。[固定版本 Configuration](https://github.com/StackExchange/StackExchange.Redis/blob/2.13.17/docs/Configuration.md) |
| 断线排队 | `BacklogPolicy.FailFast` 的源码设置 `QueueWhileDisconnected=false`、`AbortPendingOnConnectionFailure=true`；适合尽快回源，而非积压缓存操作。它不等于对已发出的操作有硬时限。[固定版本 BacklogPolicy](https://github.com/StackExchange/StackExchange.Redis/blob/2.13.17/src/StackExchange.Redis/BacklogPolicy.cs) |
| 重连 | `ReconnectRetryPolicy` 管理失败后的重连间隔，默认基于 ConnectTimeout 的指数策略；不手工每请求 Dispose/Connect。[固定版本 Configuration](https://github.com/StackExchange/StackExchange.Redis/blob/2.13.17/docs/Configuration.md) |
| 超时 | AsyncTimeout 检测受 HeartbeatInterval 影响，默认心跳 1 秒；设 200ms 不代表每次恰在 200ms 返回。请求预算可另外界定，但应测量实际端到端延迟。[固定版本 Configuration](https://github.com/StackExchange/StackExchange.Redis/blob/2.13.17/docs/Configuration.md) |
| API | 有 `StringGetAsync`、`StringSetAsync` 与 `ScriptEvaluateAsync`；这些常用签名没有 CancellationToken。2.13.17 的 SET 新签名含 Expiration/ValueCondition，同时保留 TimeSpan/When 重载，实现时明确选择匹配签名。[固定版本 IDatabaseAsync](https://github.com/StackExchange/StackExchange.Redis/blob/2.13.17/src/StackExchange.Redis/Interfaces/IDatabaseAsync.cs) |

客户端超时不保证已经发往服务器的操作停止。`WaitAsync` 可以界定调用者等待预算，但不能把它解释成撤回 Redis 命令；后到的填充、删除也必须符合令牌协议。避免 FireAndForget，使写失败可观察，并把缓存故障与业务失败区分。[固定版本 Timeouts](https://github.com/StackExchange/StackExchange.Redis/blob/2.13.17/docs/Timeouts.md)、[Task.WaitAsync](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.waitasync?view=net-10.0)

## 6. 故障降级与最小验收面

建议 Redis 连接失败、超时、内存拒绝写入、缓存格式不兼容时受控回源；原始请求取消仍应传播，不能当缓存故障吞掉。数据库失败应保留数据库故障语义，不能一律转换成缓存 miss。查询入口或数据库回源门需要限制**正在执行数量和等待数量/时间**；只用无限等待的 SemaphoreSlim 不能防请求积压。多实例的本地限额会相加，不能声称它是全局数据库并发上限。

可设置短时间跳过缓存的熔断状态，降低故障期间反复等待成本；恢复探测和重连继续运行。Redis 是可选查询加速依赖，故障应体现在缓存降级指标；是否影响宿主 readiness 需和路由摘除策略一致，不能让本来可以回源的服务因缓存掉线全部被摘除。建议首轮保留关闭缓存开关，采用相同 HTTP/ISender 契约对比启用与关闭行为。

建议验收通过公开查询与真实 PostgreSQL/Redis 边界观察，不把“模拟 adapter 返回某值”当协议成立：

- 两个独立实例读取同一对象，成本输入、费率、计算结果、Costing 消费都能传播失效；回滚不会发出失效意图。
- 用屏障暂停旧查询，提交新版本并处理失效后恢复旧填充，确认旧填充 CAS 被拒绝；另测过期/删 key 后旧填充不能复活。
- worker 在 Redis 删除后、数据库确认前崩溃，重启重放成功；重复/乱序失效不会使内容倒退。
- Redis 不可达和命令超时期间仍可受限查库，取消可传播；超过回源门容量时有明确响应，恢复后重新使用缓存。
- 错误格式、ItemId 不匹配或超大载荷不直接作为业务结果返回；固定 TTL 真的存在，命中不续期；不同环境/数据库代次/格式版本不能串键。
- 记录 hit/miss、回源耗时/数量、失效最老记录年龄、重试失败、CAS 拒绝、并发门拒绝、Redis 故障次数；标签不放 ItemId 等无界值，日志不打印连接串或载荷。
- 实测比较直查库、方案 A、方案 B 的 P50/P95/P99 和数据库请求数；未测前只宣称协议能力，不宣称性能提升。

以上验收清单是建议；本文未运行 Redis、故障注入或基准测试，也未改产品代码。
