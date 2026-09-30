# 27 — 深模块评审与设计用语规范

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 01

> **2026-09-29 说明**：目标要求新项目"严格遵循 MattSkills 规范"，
> 其中一条是**深模块设计语言**。项目此前只有八条架构不变量，没有把这套用语立成规范。
> 本票把它补上，并**用这把尺子量自己**。

## Comments

### local

**Answer**

**产出（4 个文件）**

| 文件 | 内容 |
|---|---|
| `docs/agents/design-vocabulary.md` | 设计用语规范：模块 / 接口 / 实现 / 深度 / 缝 / 适配器 / 杠杆 / 局部性 + 三条判据 |
| `AGENTS.md`（修订） | 新增 "Design vocabulary" 一节；并修正过时的 Layout（Identity 已有宿主、Gateway 已存在） |
| `.scratch/nexusstack-next/review/05-deep-modules.md` | **用深模块的尺子量自己**——前四份 review 量的都是参照仓库 |
| `tests/Contexts.Tests/FileStoreSeamTests.cs` | 用两个语义不同的适配器验证 `IFileStore` 那道缝 |

#### 评审的结论

**大的地方是深的。** `Sender` 259 行藏在 `ISender` 三个方法后面；
`GatewayRouteTable` 230 行藏在 `Create`/`FromJson`/`ToJson` 后面；
`MenuTree` 304 行。删除测试都通过——删掉它们，复杂度会在 N 个调用方那里重新出现。

**`IOutboxStore` 是刻意深的**：4 个方法，而且**刻意没有 `Add`**——
写入是聚合持久化的一部分，给投递器一个"写"的口子会让"同事务"这条保证变得可疑。
把能力切成两半、各给一个接口，比给一个大接口再靠约定约束调用方要深。

#### 一处我先判错、代码纠正了我的地方

`IFileUrlProvider` 单看是"1 个方法、零适配器"的假设缝典型嫌疑。但代码里的理由站得住：
**本地磁盘存储不实现它，对象存储才实现**——缝上确实有具名的一对适配器在变化。

参照仓库的 `IFileStorage` 有 14 个成员，而**没有任何一个实现支持全部成员**：
`AliyunFileStorage.GetAbsolutePath` 抛 `NotImplementedException`，偏偏视频上传路径会调到它。
**做不到的能力不给接口，就没有"声明了却抛异常"的中间状态。**

#### 当场执行了评审自己的建议

评审写"值得给最可疑的一道缝写第二个适配器"。同一轮里做掉了：
本地磁盘版与对象存储版跑**同一组契约断言**，`IFileStore` 三个方法一个都不用改。

其中一条断言还**被编译器证明了一次**：直接在具体类型上写 `is IFileUrlProvider`，
编译器算出它是常量并报 **CS0184**（"给定表达式始终不是所提供的类型"）——
编译器自己说这两者毫无关系。改成对运行时实例判定。

#### 用语偏差（已记录，未全改）

- `边界` 出现 4 次：属于 DDD"限界上下文"的对；指"模块边界"的应改成**缝**。
- `服务` 14 次：指可部署微服务时对；指"模块"时不对。
- `组件` 1 次：应改成**模块**。
- 英文 `component` / `boundary` 各 0 次。

用语不改代码，但决定讨论的分辨率。规范已固化，逐处改写留作后续。

#### 最大的一条遗留

**除 `IFileStore` 外，其余几道缝的形状仍未被真实适配器检验**
（`IOutboxStore` / `IInboxStore` / `IEventBus` / `IUnitOfWork` 需要 EF Core 与 RabbitMQ）。
缓解措施恰好是深模块规则的另一条——**接口就是测试面**：测试替身跨的是与将来真实适配器
**同一道缝**，因此接口至少被"跨过"过。但"跨过"不等于"验证过"。

**当前状态**：构建 0 警告 0 错误；测试 **329/329**。
