# Costing 展示查询缓存

对应票据：[Costing 展示查询的第二个 Redis 消费者与持久失效](https://github.com/yongpengW/NexusStackNext/issues/168)。

Costing 保留 `GET /api/costing/items/{itemId}` 作为权威读取。它用于编辑前的当前版本、任务结果和写后立即读，不经过 Redis。
展示场景使用 `GET /api/costing/items/{itemId}/display`，该入口允许在固定 TTL 内返回旧的 `CostSheetView`，但仍先经过业务授权。
展示值只包含成本对象公开视图，不包含凭据、请求正文或任务执行权。

## 配置

缓存默认关闭。启用时，宿主从配置中心或环境读取 `Costing:Cache`：

| 配置 | 默认 | 说明 |
| --- | --- | --- |
| `Enabled` | `false` | 是否启用展示 Redis |
| `ConnectionString` | 空 | Redis 连接；不写入仓库、日志或命令行 |
| `Namespace` | 空 | 部署与数据库世代唯一，恢复数据库后应轮换 |
| `MaxConcurrentLoads` | `16` | 每个进程同时回源上限，无等待队列 |
| `LoadTimeout` | `5s` | 一次 PostgreSQL 展示回源上限，范围 100ms–30s |
| `Ttl` | `1m` | 固定 TTL，读取不续期，范围 1s–5m |
| `RedisTimeout` | `250ms` | 单次 Redis 操作预算，范围 50ms–2s |
| `InvalidationPollInterval` | `250ms` | 持久失效扫描间隔，范围 50ms–5s |

Costing 自己拥有 `costing.cache_invalidations`。成本输入或计算结果改变时，`CostingDbContext` 在同一个事务中登记 `(ItemId, Version)`；空操作、失败提交和回滚不会留下新的意图。后台投递器成功删除键后才删除意图，数据库或 Redis 故障会保留记录等待下一轮。

Redis 使用复用的 `ConnectionMultiplexer`。读取通过 Lua 原子取得填充令牌，回源结束后只有仍持有令牌的请求可以写入；删除、淘汰、过期或旧进程迟到都不能把旧快照重新发布。值大小限制为 16 KiB，回源并发和 Redis 等待均有界。

Redis 不参与权限、任务领取、成本写入或版本裁决。Redis 不可用时展示查询在预算内回源或返回稳定的繁忙/超时错误；权威入口仍按 PostgreSQL 的原有语义工作。这里的 TTL 和最终一致失效延迟不是多机高可用或 Redis 主从切换承诺。

本机测试继续使用受控的共用测试 PostgreSQL，并为 Redis 使用独立命名空间；CI 使用隔离环境。测试只删除自己的键，不执行 `FLUSHALL`。
