# Pricing 普通查询缓存

对应票据：[定价查询的 Redis 缓存与跨实例失效](https://github.com/yongpengW/NexusStackNext/issues/29)。
设计取舍见 [Pricing ADR-0002](../src/Services/Pricing/docs/adr/0002-durable-query-cache-invalidation.md)，技术核验见 [研究](research/2026-10-02-pricing-redis-cache.md)。

## 接入与归属

Pricing 宿主在 `Pricing:Cache:Enabled=true` 时接入 Redis 并启动失效投递器。连接配置只从
`Pricing:Cache:ConnectionString` 读取，不放进仓库。必须提供 `Pricing:Cache:Namespace`，
例如 `dev-pricing-generation1`；同一数据库的所有副本使用相同命名空间，不同环境/数据库使用不同值。
数据库恢复旧备份、PITR 或重建后，停掉旧副本并统一轮换命名空间，再开放流量。
Aspire 可选读取 `NEXUSSTACK_PRICING_REDIS` 与 `NEXUSSTACK_PRICING_CACHE_NAMESPACE` 并注入 Pricing；
平台默认启动仍不要求 Redis。

只缓存 `GET /api/pricing/items/{itemId}` / `GetPriceQuote` 的完整投影。任务查询、命令、
权限判断均直接使用已有权威存储。新模块接入暂不提供通用缓存 SDK：只有 Pricing 一个消费者，
遵守第二个上下文证明必要后才提取到 BuildingBlocks 的规则。

键为 `{Namespace}:pricing:quote:v1:{ItemId:N}`，Redis 数据库由连接配置选择。
单键保存 `token` 或 `value`；业务方不需要自己管理令牌或调用删除。
结果最多 16 KiB，不缓存不存在的对象。没有本机一级缓存。

## 一致性与恢复

1. Pricing 聚合改变时，DbContext 在同一次保存里登记 `(ItemId, Version)` 失效意图。
   手工成本、费率、上游成本接纳和计算完成都覆盖；回滚同时撤销意图。
2. 后台每轮读取至多 100 条，先删除 Redis 键，再确认这条记录。进程中止或 Redis 故障后继续重试。
   重复或乱序删除只会增加未命中；确认不能按 ItemId 删除其他版本的意图。
3. 缓存未命中时先原子取得随机填充令牌，再读数据库主库。回填只在令牌仍相同时成功。
   删除、过期、淘汰及清空会使旧令牌失效；旧回填不会重新创建已经失效的键。

这是普通查询的**最终一致性**：提交成功至失效投递之间可能读到旧值，不提供立即读到自己写入的保证。
默认固定 TTL 为 60 秒，命中不续期。默认填充租期为回源预算加两次 Redis 操作预算，
所以失效一直失败时，从旧数据库快照开始计算的保底窗口最多为填充租期加 TTL，而非仅 TTL。
网络命令超时不代表底层操作被取消；迟到回填仍执行令牌比较。
Redis 主从切换本轮未验收，不承诺已确认删除在切换后仍存在；固定 TTL 是必要兜底。

关闭缓存的副本仍登记失效意图，因此它的写入也能被其他开启缓存的副本传播。
全部副本关闭缓存时意图会积累，重新启用后逐批处理；不能把“缓存关闭”当作清理数据库意图的授权。
运维应监控 `pricing.cache_invalidations` 的积压；永久关闭缓存的部署可在确认无缓存读者后制定归档清理策略。

## 预算与容量

| 配置 | 默认 | 限制 |
|---|---|---|
| `MaxConcurrentLoads` | 16 | 每宿主 1～256 个回源；无等待队列 |
| `LoadTimeout` | 5 秒 | 100ms～30s |
| `RedisTimeout` | 250ms | 每次连接等待/操作的预算 50ms～2s；不等于请求总时长 |
| `Ttl` | 60 秒 | 1s～5min；固定过期 |
| `InvalidationPollInterval` | 250ms | 50ms～5s；不是失效完成 SLA |

Redis 超时或不可达时共享连接继续恢复，缓存操作退避一秒，查询按预算回源。
回源满返回 HTTP 503 / `pricing.query_busy`，回源预算用完返回 503 / `pricing.query_timeout`；
调用方取消不会被吞掉。数据库故障不由 Redis 伪装为健康；已缓存的普通查询可在 TTL 内返回已有投影，
但 `/health/ready` 仍按数据库状态报告，任务可靠性不会降级到缓存。

每宿主预算意味着 N 个副本最多 N 倍回源并发，扩容时需按数据库容量配置。
缓存键固定按对象归属，数量随活跃对象增长；固定 TTL 与值大小上限不是 Redis 总内存硬上限。
专用缓存 Redis 应配置明确 `maxmemory` 并选择 `allkeys-lru` 或 `allkeys-lfu`，同时给进程缓冲保留余量；
共享 Redis 的全局策略由运维决定，应用绝不执行 `CONFIG SET` 或 `FLUSHALL`。

## 验证与迁移

先执行 `migrate-pricing` 加入技术表，普通启动只检查迁移。Redis 不参与数据库迁移。
本机测试配置为 `env/test.dev` 的 `NEXUSSTACK_TEST_REDIS`，仅使用开发/测试实例；
故障代理测试需要专用非 TLS 连接，产品连接本身支持客户端的 TLS 配置。
测试只删除随机命名空间内自己的键，绝不清空共享 Redis。

HTTP 双进程旅程验证共享命中、授权、费率与计算完成传播、断线后重启恢复、键丢失回源和旧回填竞态。
缓存命中通过临时停用测试数据库后读取验证；竞态代理只暂停真实 Redis 的网络请求，业务结论仍从 HTTP 读取。
CI 启动独立 Redis 7.2.14 和 PostgreSQL，保证这些旅程实际执行。
