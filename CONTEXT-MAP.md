# Context Map

五个限界上下文 + 一个不持有数据的边缘。每个上下文的词表在它自己的 `CONTEXT.md`，术语一经确认即就地写入——
本文件只定义**边界**与**关系**。

## Contexts

- [Identity](./src/Services/Identity/CONTEXT.md) — 身份与访问：谁可以登录、登录后能做什么。
  拥有用户、角色、权限、菜单、令牌。
- [Platform](./src/Services/Platform/CONTEXT.md) — 平台配置：与业务无关、却被所有上下文读取的元数据。
  目前拥有全局设置（`GlobalSetting`）；开放应用配置与区域还在计划里。
- [Scheduling](./src/Services/Scheduling/CONTEXT.md) — 任务编排：异步任务与计划任务的定义、触发与生命周期。
- [Auditing](./src/Services/Auditing/CONTEXT.md) — 审计与日志：谁在什么时候做了什么（`AuditEntry`，
  只写不可改）。令牌流转记录还在计划里。
- [Files](./src/Services/Files/CONTEXT.md) — 文件与下载中心：文件的存储、归属与分发。

**Gateway** — `src/Gateway/` — 不是上下文。它不持有数据、不持有不变量，只做路由、边缘鉴权、
限流与关联 ID。任何落到网关的业务判断都是设计错误。

## Relationships

> **这一节分成两半：已经成立的，和还没建的。**
>
> 第 24 轮 review 之前，这一节整个用**现在时**写着，读起来每一条都已经成立——
> 而实测下来，下面六个名字**在代码里根本不存在**（`TokenLog`、`Region`、`TaskDue`、
> `OperationPerformed`、`TokenIssued`、`TokenRevoked`），`*.Contracts` 工程一个也没建，
> 没有任何上下文发布过集成事件。
>
> 一份读起来像事实、实际是意图的关系图，比没有关系图更糟：
> 下一个人会照着它去 `grep` 一个不存在的事件，然后怀疑自己的搜索方式。

### 已经成立的

- **Identity → 其他上下文**：Identity 是所有其他上下文的**上游**——它们不自己判断"你是谁"，
  只消费 Identity 给出的结论（`ICurrentUser`、`PermissionKey`）。这条是**运行时成立**的。
- **Platform → 所有上下文**：Platform 拥有配置的**唯一真相**。其他上下文只按 key 读取，
  **绝不** join Platform 的表。
- **Gateway → 所有上下文**：唯一入口。网关做身份**认证**（验签），各上下文做**授权**——
  两件事分开，每加一个上下文都不用改网关的授权逻辑。这条**已实现并测过**。
- **不变量**：跨上下文引用是**被测试禁止**的（`Contexts_MustNotReferenceOtherContexts`，
  两层都查：编译产物 + `csproj`），且**八条不变量全部反向验证过**。

### 还没建的（**不要照着它 grep**）

当前**没有任何上下文之间发生异步通信**：五个上下文各自在自己的进程里被同一个宿主组装，
彼此通过端口与 `ICurrentUser` 协作。事件总线（RabbitMQ）与发件箱**已经建好并验过**
（6 条真 broker 验收 + 投递循环），但**还没有生产者**——所以发件箱永远是空的。

以下是**设计意图**，不是现状：

- **Identity → Auditing**：Identity 计划发出 `UserLoggedIn`、`LoginFailed`、
  `RefreshTokenIssued`、`RefreshTokenRevoked`（这些**领域事件已经存在**，在
  `IdentityDomainEvents.cs` 里），Auditing 消费后落审计。**消费端还没做。**
- **所有上下文 → Auditing**：计划有一个统一的 `OperationPerformed` 事件。**不存在。**
- **Scheduling → 其他上下文**：计划发出 `TaskDue` 一类的集成事件，由目标上下文自己决定要不要响应——
  否则 Scheduling 会变成上帝服务。**不存在。**
- **`*.Contracts` 程序集**：跨上下文通信只允许走它们（不变量 2），但**目前一个都还没建**，
  因为还没有跨上下文通信可言。

**谁把这些做出来，请把对应的条目从这一节移到上面那一节。**

## False friends

这些词在不同上下文里含义不同，读到时必须先确认在说哪一个：

- **Token**：Identity 里指登录签发的访问/刷新令牌（`RefreshToken`、`AccessToken`）；
  **Auditing 里计划指令牌的流转记录**——那个类型（曾写作 `TokenLog`）**还没建**。
- **Permission**：Identity 里指"角色对某个路由+HTTP 方法的授权"，是一个**授权判定**
  （`PermissionKey`，形如 `路由模板:HTTP方法`）。
- **Task**：Scheduling 里指被调度的**任务定义与执行记录**（`ScheduledTask`）；
  不要与 .NET 的 `Task` 混用命名。
- **Platform**：在 Identity 的词表里指"用户可登录的端"（后台/PC/小程序/POS）；
  在 `CONTEXT-MAP` 里指 **Platform 这个上下文**。同一个词，两个意思——
  这类撞名正是这一节存在的理由。
- **Region**：**计划中**。Platform 打算拥有行政区划字典；目前 Platform 只有全局设置
  （`GlobalSetting`）。Identity 的数据权限若将来按区域过滤，引用的会是它的 ID。
