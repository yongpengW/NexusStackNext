# 37 — Auditing 端到端：消息基座第一次有真实调用方

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 36

> Auditing 是**只写**上下文（ADR-0001），因此它的端到端不是"增删改查"，
> 而是 **事件进入 → 幂等去重 → 落库**。这恰好是消息基座（票据 04）第一次被真实调用——
> `IInboxStore` 在此之前**只有测试替身**。

## 实跑结果

```
健康检查                          -> HTTP 200
首次投递（msg 40bcbfa3…）         -> HTTP 202   {"outcome":"Accepted"}
重投同一条消息                     -> HTTP 200   {"outcome":"Duplicate"}
内容相同但消息标识不同              -> HTTP 202   {"outcome":"Accepted"}
非法条目（动作空）                  -> HTTP 400
GET /api/auditing/entries         -> HTTP 405   （写入口占用了该路径；不是 404）
GET /api/auditing                 -> HTTP 200
```

**第三条是这一票的核心。**

参照仓库把消费幂等键绑在**业务标识**上（review/04 F7）——后果是"两次合法的相同操作
会被当成重复而丢掉一次"。这里绑的是**消息标识**，因此内容完全一样但消息不同的两条
都会被记录。实跑确认：`202 Accepted`。

## 一条因此过期的声明

ADR-0001 原文写着：

> 访问 `GET /api/auditing/entries` 得到 **404** 是故意的，不是没做完。

加了写入口之后，同一个路径返回的是 **405**。**决策一个字没变，但它描述的可观察事实变了。**

已更正 ADR，并把这个更正本身留在里面——因为这类句子最危险的地方在于：
读起来像结论、像承诺，而且曾经确实是，**没有任何东西会在它过期时提醒你**。

（这是本项目第三次遇到同一形态：票据 28 的不受检查的覆盖注释、票据 30 的假发现、
以及这一次的 ADR 事实声明。）

## 幂等标记与业务改动必须同生共死

`IInboxStore` 的文档写着"必须在与业务改动同一个事务里调用"。本票的内存实现**没有事务**——
它只保证同一进程内不重复处理，崩溃后重投会再处理一次。

这条差异写在 `InMemoryInboxStore` 的类型文档里，**不当生产实现**。

## 产出

| 文件 | 内容 |
|---|---|
| `BuildingBlocks.Infrastructure/Events/InMemoryInboxStore.cs` | **`IInboxStore` 的第二个适配器** |
| `Auditing.Application/AuditIngestion.cs` | 端口 + 待处理消息 + 摄取服务 |
| `Auditing.Infrastructure/InMemoryAuditEntryStore.cs` | 追加只写的条目存储 + 显式注册 |
| `Auditing.Api/Program.cs` | 摄取端点（生产里事件从总线来，此处是无 broker 的入口） |

## 按深模块的判据记一笔

`IInboxStore` 此前只有测试替身——**"一个适配器只是假设的缝，两个才是真的缝"**。
现在它有一个真实适配器，并且被 `AuditIngestion` 真的跨过去了。

`IOutboxStore` 仍然是假设的缝（四个方法、零个真实适配器）。它要等一个**生产者**——
即某个上下文把自己的领域事件写进 Outbox 并投递出去。那是票据 19（EF Core 拦截器）
或票据 21（RabbitMQ）落地后的事。

## 仍未完成（诚实标注）

- `AuditIngestion` 的**应用层单元测试尚未补**——本轮用的是运行时验证。
  运行时验证覆盖了同一批分支，但单测更适合盯"并发重投只有一个成功"这类情形。
- `AuditIngestedMessage` 现在住在 `Auditing.Application`，是**刻意的临时位置**：
  第一个生产者出现时应上移到 `Auditing.Contracts`（架构不变量 7 的同一条道理）。

## 当前状态

构建 0 警告 0 错误；测试 **360/360**；解决方案 **31 个项目**。
