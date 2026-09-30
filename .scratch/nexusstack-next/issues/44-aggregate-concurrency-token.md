# 44 — 聚合并发令牌（评审 07 第一节）

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 02

> **这是架构自查里最难看的一条**：我在 `review/02` 第 349 行把"无并发令牌 → 静默丢更新"
> 写成了参照仓库的缺陷，然后在自己的 `AggregateRoot` 里原样犯下——全仓 0 处并发令牌，
> 而且它不在票据 42 的"没做什么"清单里。

## 让契约"看得见"

`Changed()` 不只是自增——它是**给读代码的人看的**：

| 场景 | 写法 |
|---|---|
| `Result` 路径改状态 | `return Changed();` |
| `void` 路径改状态 | `BumpVersion();` |
| 空操作 | `return Result.Success();`（**不带** `Changed`） |

于是不需要记住每个方法的语义：**末尾是 `Changed()` 就是改了，是 `Result.Success()` 就是没改。**

## 空操作不得自增——这是这条契约真正难守的一半

空操作若自增，乐观并发会在**没有冲突的情况下**误报冲突。
而误报的代价是调用方开始重试、或者干脆忽略冲突——**那时这个机制就废了，且废得很安静**：
它还在"工作"，只是没人再信它。

因此逐个聚合找出了空操作路径：`AssignRole` 重复调用、`ChangeValue` 同值写入、
`Grant` 已授权、`SetContact` 相同值、已禁用再 `Disable`、已删除再 `Delete`、
路径未变的 `Move`、集合未变的 `ReplaceGrants`、间隔未变的 `ChangeInterval`……

## 反向验证（两个方向）

| 变异 | 结果 |
|---|---|
| `ScheduledTask.MarkExecuted` 漏掉自增 | `ScheduledTask_ExecutionAndIntervalChange_Bump` **变红** |
| `User.Disable` 的空操作路径**误**自增 | `User_DisableAndEnable_BumpOnce_RepeatsDoNot` **变红** |

第二个方向更重要。只验证"漏掉自增"会漏掉这条契约一半的失效方式。
改动已还原，**字节级核对一致**。

## 一处顺手改掉的设计

`AggregateVersionTests` 里我最初写了个 `NoOp()` 方法——它不碰实例数据，
编译器报 CA1822。与其压制警告，不如换成一个**真实的**空操作形态：把别名改成它已经是的那个值。
测试因此更像真实代码，警告也自然消失。

## 当前状态

构建 0 警告 0 错误；测试 **419/419**（新增 27）；解决方案 37 个项目。

## 接 EF Core 时会怎样

`Version` 直接映射成并发令牌列（`IsConcurrencyToken`），**领域层一个字都不用改**——
这正是现在做这件事的理由：等那时再改，动的是九个聚合的每一个改变状态的方法。

## Comments

### local

**Answer**

**产出**

| 文件 | 内容 |
|---|---|
| `docs/adr/0011-optimistic-concurrency-in-the-aggregate.md` | 为什么版本号是领域概念而不是 EF 的 `[Timestamp]` 配置 |
| `BuildingBlocks.Domain/AggregateRoot.cs` | `Version` + `Changed()` + `Changed<T>()` + `BumpVersion()` |
| 九个聚合 | 每条改变路径自增；每条空操作路径**不**自增 |
| 三个测试工程 | 27 条版本测试 |
| `AGENTS.md` | 把契约写成规则 |
