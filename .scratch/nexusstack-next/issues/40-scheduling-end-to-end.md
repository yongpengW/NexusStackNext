# 40 — Scheduling 端到端：五个上下文全部跑通

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 36

> 五个上下文里最后一个没有真实业务链路的。它有一个**在跑的**调度器，
> 但没有办法定义一个任务——端口只有"读到期的 + 保存"。
> 一个只能被内部种子喂食的调度器，从外部看不出它和空转的区别。

## 实跑结果

```
初始任务数                      -> 0
定义任务 demo.tick（间隔 5 秒） -> HTTP 201   {"taskId":98331836111294464,"code":"demo.tick"}
重复编码                        -> HTTP 400
非法间隔（0）                   -> HTTP 400
--- 等调度节拍（10 秒一轮）---
任务被执行过                    -> True
  lastRunAt = 2026-09-29T08:15:38.6414811+00:00
  nextRunAt = 2026-09-29T08:15:43.6414811+00:00
暂停                            -> HTTP 204
  停用后 isEnabled = False   nextRunAt = 空
暂停不存在的任务                -> HTTP 404
```

**核心是那两行时刻**：`nextRunAt = lastRunAt + 5s`。
它同时证明了三件事——调度循环真的在跑、任务真的被触发、以及
**下次时刻是从"实际执行时刻"起算的**（迟到不补跑，否则停机一小时后重启会瞬间涌入几十次）。

## 顺手修掉一处分层走样

`InMemoryScheduledTaskStore` 原先住在 **`Scheduling.Api` 里**——适配器住在宿主里。
后果是"换一个持久化实现"要动宿主，而宿主本该只负责组装。

新建 `Scheduling.Infrastructure` 把它挪了过去，五个上下文的结构这才一致。

## 端口只加了一个方法

`IScheduledTaskStore` 从 2 个方法变成 3 个：加了 `ListAsync`。

**"按标识找"与"编码是否重复"都没有进端口**——它们由 `TaskRegistry` 在 `ListAsync`
的结果上做。任务表是几十条量级的注册表，为一次查找多开两个方法，
换来的只是"把简单的事拆到两个地方"。等它真的变成几万条再拆不迟。

这是深模块的常规动作：**接口保持小，行为收到服务后面。**

## 一个会在启动时炸的注册错误（在它跑起来之前就修了）

`ScheduleRunner` 我最初注册成 `Scoped`。但 `SchedulingWorker` 是托管服务（**单例**），
而单例不能消费 Scoped——宿主会在启动时抛 DI 错误，且错误信息指向 DI 而不是这条意图。

改成 `Singleton` 并在代码里写明理由。**这类错误编译期看不出来，只有跑起来才知道。**

## 停用必须清空下次计划时刻

只把 `IsEnabled` 置 false 而留着 `NextRunAt`，任务在存储眼里仍然是"到期"的——
于是它每轮都被读出来、每轮都被跳过。**日志上看起来一切正常，实际上调度器在空转。**

**反向验证**：去掉清空之后，**两条测试变红**——

| 层 | 测试 |
|---|---|
| 应用层（本票新增） | `Pause_ClearsTheNextRunSoTheTaskStopsBeingDue` |
| 领域层（早先就有） | `Contexts.Tests.SchedulingTests.Disable_ClearsNextRunAt_SoItIsNoLongerDue` |

改动已还原。

## 产出

| 文件 | 内容 |
|---|---|
| `Scheduling.Application/TaskRegistry.cs` | **注册表**：定义 / 列出 / 查找 / 停用 / 启用 |
| `Scheduling.Application/ScheduleRunner.cs`（修订） | 端口加 `ListAsync`，并写明为何只加一个 |
| `Scheduling.Infrastructure/`（新建） | 内存适配器搬出宿主 + 显式注册 |
| `Scheduling.Api/Program.cs` | 五个端点：自述 / 列出 / 定义 / 停用 / 启用 |
| `tests/Scheduling.Application.Tests/` | 10 条测试 |

## 五个上下文全部端到端

| 上下文 | 链路 |
|---|---|
| Identity | 用户 → 角色 → 菜单 → 端点 → 权限键 → 判定 |
| Platform | 写入 → 存储 → 经网关读出 |
| Auditing | 事件进入 → 幂等去重 → 落库 |
| Files | 上传 → 字节落盘 → 下载 → 删除 |
| **Scheduling** | **定义任务 → 调度节拍触发 → 下次时刻推进** |

五个都不一样，但都套在同一个骨架上。**这才是"一套微服务"的完整说法。**

## 当前状态

构建 0 警告 0 错误；测试 **392/392**（新增 10）。
