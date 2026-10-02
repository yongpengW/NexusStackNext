// 逐工程串行不足以限制 xUnit 的类间并发：本工程包含共享 PostgreSQL 上的建库/迁移/删库旅程。
// 真实竞争由单个用例显式启动多个执行者；不同旅程必须串行，避免互相放大目录和 I/O 压力。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
