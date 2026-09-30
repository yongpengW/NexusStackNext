# 02 — BuildingBlocks.Domain：领域基元

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 01

## Answer

**产出**

`src/BuildingBlocks/BuildingBlocks.Domain/`：`Entity.cs`（含 `IEntity` 标记）、`AggregateRoot.cs`
（含 `IDomainEvent` / `IHasDomainEvents`）、`StronglyTypedId.cs`、`ValueObject.cs`、`Result.cs`、`Auditing.cs`、`DomainException.cs`。

`tests/BuildingBlocks.Domain.Tests/`：`TestDoubles.cs`（一个带不变量的 `User` 聚合 + `UserId` 强类型 ID + `UserName` 值对象）
与 4 个测试文件，**28 个测试全部通过**。

**验收标准**

| 验收项 | 结果 |
|---|---|
| `Domain` 项目无任何 `PackageReference` | ✅ 不变量 3 成立（`.csproj` 里一个包都没有） |
| 单元测试可显式构造聚合 | ✅ 见下 |
| 违反不变量抛领域异常 | ✅ `Disable_BuiltInAccount_ThrowsDomainException` |
| 领域事件可读可清空 | ✅ 4 个测试覆盖收集 / 清空 / 只读快照 / 事件 ID 唯一 |

**本票要证明的核心**

原项目 `review/01` 的阻断级缺陷是"实体无法在进程外构造"——构造函数里调 `SnowFlake.Instance`，
依赖 `App.Init()` 之后的静态容器，于是 `new User()` 在任何单测里都抛，**整个项目从结构上无法做单元测试**。

新实现里对应的证据是 `Aggregate_NeedsNoGlobalState`：不初始化任何东西，一次构造 100 个聚合，全部正常。
`Entity<TId>` 的 ID 只能由调用方传入，且默认值（`null` / `0` / `Guid.Empty`）在构造处就被拒绝。

**被测试抓出来的设计错误（值得记录）**

第一版把基类写成 `record StronglyTypedId<TValue>(TValue Value)`，派生写成
`record UserId(long Value) : StronglyTypedId<long>(Value)`。结果是：

- 基类与派生 record **各有一个 `Value` 属性互相隐藏**；
- 派生 record 会**重新生成 `ToString()`**，把基类的重写覆盖掉，`new UserId(5).ToString()` 返回 `"UserId { Value = 5 }"` 而不是 `"5"`。

`StronglyTypedId_KeepsUnderlyingValue` 失败了，才把这个错误暴露出来。改为基类独占 `Value`
（`sealed override ToString()`），派生类型只提供构造函数；相等性不受影响，因为 record 生成的 `Equals`
会沿继承链比较基类字段。

**顺带确立的两条纪律**

1. 业务失败用 `Result`/`Error` 返回值表达，异常只留给真正的意外；`Result` 的构造函数强制
   "成功不带错误、失败必带错误"，公开工厂无法造出自相矛盾的结果。
2. 分析器豁免必须用 `[SuppressMessage]` + Justification 关在源头，不许在 props 里成批 `NoWarn`。
