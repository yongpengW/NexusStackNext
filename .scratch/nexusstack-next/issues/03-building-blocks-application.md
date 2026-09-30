# 03 — BuildingBlocks.Application：用例编排基座

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 01, 02

## Comments

### local

**Answer**

**产出（9 个源码文件 + 21 个测试）**

| 文件 | 内容 |
|---|---|
| `Time/IClock.cs` | `IClock` + `SystemClock`。替换掉参照仓库的静态时间（`CronScheduleService` 因此无法测试） |
| `Messaging/Requests.cs` | `ICommand` / `ICommand<TResult>` / `IQuery<TResult>` 标记 |
| `Messaging/Handlers.cs` | 三个处理器接口，全部返回 `Result` |
| `Messaging/ISender.cs` | 用例入口，三个方法 |
| `Messaging/Sender.cs` | 分发器：校验 → 事务 → 处理器 → 成功才刷库 → 提交 |
| `Validation/IRequestValidator.cs` | 校验器（可选注册）+ 非泛型标记 |
| `Transactions/IUnitOfWork.cs` | 工作单元抽象，承载"一个聚合一个事务" |
| `Events/IntegrationEvent.cs` | 集成事件契约基类 + 命名规则（ADR-0007） |
| `ApplicationServiceCollectionExtensions.cs` | **全显式**注册，零程序集扫描 |

**验收标准**

| 验收项 | 结果 |
|---|---|
| 处理器抛异常时事务回滚 | ✅ `SendAsync_HandlerThrows_RollsBackAndPropagates`：日志 `[begin, handler:Exploding, rollback]`，无 `commit`、无 `save` |
| 重试与事务二选一已写明 | ✅ 见下 |
| EventName 强制声明 | ✅ `abstract string EventName`，不实现就编译不过 |
| 改命名空间/换类型名不改变路由键 | ✅ `RoutingKey_IsStableWhenNamespaceAndTypeNameChange` |
| 处理器能拿到确定的时钟 | ✅ `QueryAsync_UsesInjectedClock` |

**关于"重试与事务只能选一条"——答案是：保留执行策略，由执行策略包裹事务。**

参照仓库开了 `EnableRetryOnFailure()`，同时在 `PermissionService.cs:98` 用裸 `BeginTransactionAsync()`，
EF Core 直接抛"执行策略不支持用户自建事务"，那条用例必然失败（review/02 发现 2）。

本项目选定的策略写在 `IUnitOfWork` 的文档注释里，就在实现者会读到的地方：
**实现方必须把整个事务放进 `IExecutionStrategy.ExecuteAsync` 里**，而不是先开事务再让重试去撞它。
代价是处理器可能被执行多次，因此它必须是幂等的或纯计算的，且不得有事务外的副作用——这条也写进了注释。

**管线设计：为什么不做可插拔行为链**

只有三步（校验 / 事务 / 执行），显式写出来读得更清楚，也不需要引入一整套抽象。
换来的直接好处是"失败不落库"这条保证**可证明**：

```
成功 → [validate, begin, handler, save, commit]
业务失败 → [validate, begin, handler, commit]      ← 不 save，未刷新的改动随事务结束丢弃
校验失败 → [validate]                              ← 连事务都没开，处理器没被调用
异常 → [begin, handler, rollback]                  ← 不 commit
查询 → [handler]                                   ← 读路径不经过工作单元
```

**被工具教会并改掉的两个设计错误（本票最有价值的部分）**

1. **"完全不用反射"是错的。** 我最初把 `ISender` 设计成
   `QueryAsync<TQuery, TResult>(TQuery query)`，声称靠类型推断就能免掉反射。
   编译器打回来了：**C# 的类型推断不看约束、也不看接口实现**，`TResult` 永远推不出来，
   调用方每次都得写 `QueryAsync<CountUsersQuery, int>(...)`。
   改为参数取请求的**接口**（`IQuery<TResult>`），推断才成立；
   处理器包装器仍需一次 `MakeGenericType`，于是把反射收进包装器 + 每类型一份缓存。
   调用侧保持 `await sender.QueryAsync(new CountUsersQuery())` 这样干净。

2. **缓存工厂用错了泛型元数。** `QueryHandlerWrapper<,>` 是两个类型参数，我只传了请求类型，
   运行时抛 `The number of generic arguments provided doesn't equal the arity`。
   6 个测试同时红。修法是缓存键带 `(请求类型, 开放包装器类型)`——必须带开放类型，
   否则同一返回值类型下命令包装器与查询包装器会串。

**反向验证（代码变异）**

"失败不落库"是票据的头号承诺，所以我把成功守卫 `if (result.IsSuccess)` 直接删掉，跑测试：

```
失败! - 失败: 1，通过: 20    ← 只有 SendAsync_FailureResult_DoesNotSaveChanges 变红
```

精确制导，没有大面积误伤。守卫已还原（已核验两处均在位）。

**顺带：票据 05 的哨兵按设计触发了**

新增 `BuildingBlocks.Application` 程序集时，`SourceAssemblyInventory_MatchesDeclaredSet` 与
`BuildingBlocks_MustStayAtTheFoundingSet` 立即变红——这正是它们存在的意义。顺势做了一处语义修正：

原实现断言"BuildingBlocks 集合恰好等于创始的那一个"，这会在每次正常新增共享内核时误报。
改为 **`BuildingBlocks_MustNotGrowBeyondTheDeclaredSharedKernel`**：断言实际集合是
ADR-0001 目录布局里声明的四个（Domain / Application / Infrastructure / Contracts）的**子集**。
语义更准：不是"不许新增"，而是"**不许长出声明之外的东西**"。

**当前状态**：构建 0 警告 0 错误；测试 **57/57**（领域 28 + 应用 21 + 架构 8）。
