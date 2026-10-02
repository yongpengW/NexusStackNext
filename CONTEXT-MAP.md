# Context Map

五个平台限界上下文 + 两个独立业务参考上下文 + 一个不持有数据的边缘。每个上下文的词表在它自己的 `CONTEXT.md`，术语一经确认即就地写入——
本文件只定义**边界**与**关系**。

## Contexts

- [Identity](./src/Services/Identity/CONTEXT.md) — 身份与访问：谁可以登录、登录后能做什么。
  拥有用户、角色、权限、菜单、令牌。
- [Platform](./src/Services/Platform/CONTEXT.md) — 平台配置：与业务无关、却被所有上下文读取的元数据。
  目前拥有全局设置（`GlobalSetting`）；开放应用配置与区域还在计划里。
- [Scheduling](./src/Services/Scheduling/CONTEXT.md) — 计划触发：计划的定义与到期时刻；业务任务由所属上下文拥有。
- [Auditing](./src/Services/Auditing/CONTEXT.md) — 审计与日志：谁在什么时候做了什么（`AuditEntry`，
  只写不可改）。令牌流转记录还在计划里。
- [Files](./src/Services/Files/CONTEXT.md) — 文件与下载中心：文件的存储、归属与分发。
- [Pricing](./src/Services/Pricing/CONTEXT.md) — 独立定价样板：定价输入、派生结果与持久化重算，独立宿主和数据库。
- [Costing](./src/Services/Costing/CONTEXT.md) — 独立成本核算样板：成本组成、计算结果与成本事件，独立宿主和数据库。

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

- **Scheduling → Costing**：通过 Scheduling.Contracts 的 `ScheduleTriggeredV1` 交付已登记的发生；
  计划、发生和 Outbox 同事务。Costing 事务保存 Inbox、稳定接受/拒绝回执及自己的重算任务，
  后续结果沿 Costing → Pricing 传播；调度交付不代表业务完成。

- **Platform → Auditing**：`Platform.Contracts` 的 `SettingCommittedV1` 只传设置标识、提交版本、
  认证 Actor 与关联信息；状态和 Outbox 同事务，Auditing 的 Inbox 与不可变记录同事务。
  不传设置值或说明，调查查询要求显式权限，HTTP 不接受审计写入。

- **Costing → Pricing**：通过 `CostCalculatedV1` 传递完整成本快照及来源版本。Costing 的结果与 Outbox 同事务；
  Pricing 的 Inbox、成本投影与重算任务同事务，费率归 Pricing。契约在 Costing.Contracts 中。

- **Identity → 其他上下文**：Identity 是所有其他上下文的**上游**——它们不自己判断"你是谁"，
  只消费 Identity 给出的结论（`ICurrentUser`、`PermissionKey`）。这条是**运行时成立**的。
- **Platform → 所有上下文**：Platform 拥有配置的**唯一真相**。其他上下文只按 key 读取，
  **绝不** join Platform 的表。
- **Gateway → 所有上下文**：唯一入口。网关做身份**认证**（验签），各上下文做**授权**——
  两件事分开，每加一个上下文都不用改网关的授权逻辑。这条**已实现并测过**。
- **不变量**：跨上下文引用是**被测试禁止**的（`Contexts_MustNotReferenceOtherContexts`，
  两层都查：编译产物 + `csproj`），且**八条不变量全部反向验证过**。

### 还没建的（**不要照着它 grep**）

Platform → Auditing 与业务样板 Costing → Pricing 已使用真实消息协作，其余事件生产者按真实需求逐个接入。
各自的数据与执行任务仍由所属上下文持有。

以下是**设计意图**，不是现状：

- **Identity → Auditing**：Identity 计划发出 `UserLoggedIn`、`LoginFailed`、
  `RefreshTokenIssued`、`RefreshTokenRevoked`（这些**领域事件已经存在**，在
  `IdentityDomainEvents.cs` 里），Auditing 消费后落审计。**消费端还没做。**
- **其余上下文 → Auditing**：尚未接入；不以通用原始载荷替代上下文自己的最小事实契约。
- **Scheduling → 其他业务目标**：除已接入的 Costing 重算外，按目标上下文自己的契约逐项扩展。

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
