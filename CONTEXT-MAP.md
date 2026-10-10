# Context Map

五个平台限界上下文 + 两个独立业务参考上下文 + 一个边缘宿主。每个上下文的词表在它自己的 `CONTEXT.md`，术语一经确认即就地写入——
本文件只定义**边界**与**关系**。

## Contexts

- [Identity](./src/Services/Identity/CONTEXT.md) — 身份与访问：谁可以登录、登录后能做什么。
  拥有用户、角色、权限、菜单、令牌。
- [Platform](./src/Services/Platform/CONTEXT.md) — 平台配置：与业务无关、却被所有上下文读取的元数据。
  目前拥有全局设置（`GlobalSetting`）；开放应用配置与区域还在计划里。
- [Scheduling](./src/Services/Scheduling/CONTEXT.md) — 计划触发：计划的定义与到期时刻；业务任务由所属上下文拥有。
- [Auditing](./src/Services/Auditing/CONTEXT.md) — 已提交事实与操作调查：不可变 `AuditEntry` 保存已提交事实；
  `OperationObservation` 覆盖四个宿主的普通 HTTP 入口、命令、业务任务及调度裁决。令牌流转通过 Identity 的最小提交事实记录。
- [Files](./src/Services/Files/CONTEXT.md) — 文件与下载中心：文件的存储、归属与分发。
- [Pricing](./src/Services/Pricing/CONTEXT.md) — 独立定价样板：定价输入、派生结果与持久化重算，独立宿主和数据库。
- [Costing](./src/Services/Costing/CONTEXT.md) — 独立成本核算样板：成本组成、计算结果与成本事件，独立宿主和数据库。

**Gateway** — `src/Gateway/` — 不是业务上下文。它负责路由、边缘鉴权、限流与关联 ID，
显式组合 Auditing 拥有的来源 journal；路由配置与操作观察不赋予网关业务数据所有权。任何落到网关的业务判断都是设计错误。

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

- **Auditing → Files（可选调查导出）**：Auditing 冻结已收到的事实或操作证据，持有本人委托及原发布意图；
  经 Files.Contracts 的证书 HTTPS 协议交付固定 XLSX。Files 独占字节、当前可用性与下载归属，
  下载同时重新验证 Identity 当前导出许可。双方不读取对方表，启用与限制见[审计调查导出](docs/audit-private-exports.md)。

- **Pricing → Files（可选导出）**：Pricing 自己冻结报价、持有导出及原发布意图；
  通过 Files.Contracts 的真实受认证 HTTPS 暂存/发布协议提交固定 CSV 或 XLSX，双方独立数据库。
  Files 拥有字节、下载归属与到期历史，Pricing 不共享其路径、令牌或表；
  历史成功与当前下载可用性分别查询。启用与阶段资格见 [Pricing 私有报价导出](docs/pricing-private-exports.md)和 [XLSX 契约](docs/pricing-private-xlsx.md)。

- **Scheduling → Costing**：通过 Scheduling.Contracts 的 `ScheduleTriggeredV1` 交付已登记的发生；
  计划、发生和 Outbox 同事务。Costing 事务保存 Inbox、稳定接受/拒绝回执及自己的重算任务，
  消费尝试有独立操作；新任务以首次成功受理为直接来源，上游调度为父级，重复投递不改写来源。
  后续结果沿 Costing → Pricing 传播；调度交付不代表业务完成。

- **Platform → Auditing**：`Platform.Contracts` 的 `SettingCommittedV1` 只传设置标识、提交版本、
  认证 Actor 与关联信息；状态和 Outbox 同事务，Auditing 的 Inbox 与不可变记录同事务。
  不传设置值或说明，调查查询要求显式权限，HTTP 不接受审计写入。

- **Identity → Auditing（部分覆盖）**：PostgreSQL 适配器与 User、Role、MenuTree、ApiResource、RefreshToken 的已知持久变化同事务保存
  `Identity.Contracts` 的 `IdentityEntityCommittedV1`，经过本上下文 Outbox 交付；不传用户名、密码或哈希。
  权限成员差异、菜单节点变化、资源所属 Menu 与令牌所属 User 使用显式安全关联；Memory 已有原子提交适配器，完整故障治理尚未完成，
  逐项状态见[事实覆盖矩阵](docs/committed-audit-coverage.md)。

- **Files → Auditing（部分覆盖）**：PostgreSQL 元数据仓储将文件登记、存储、删除请求、清理延期和字节移除的最小事实
  与状态同事务写入本上下文 Outbox，按 `Files.Contracts` 交付中央；不传文件名、句柄或字节。
  已验证来源故障回滚与重启后的 RabbitMQ 交付，Memory 已有原子提交适配器，完整容量/故障治理尚未完成。

- **Scheduling → Auditing（治理未完成）**：Memory 与 PostgreSQL 将计划管理变化、失败退避和调度决定的最小事实
  与状态原子保存，经 `Scheduling.Contracts` 的 `PlanCommittedV1` 交付；触发、合并和跳过关联本上下文决定。
  真实重启 / RabbitMQ 验证执行和原发起关系；来源容量及专门的审计恢复仍待完成，不把触发当作目标业务完成。

- **来源宿主 → Auditing**：PlatformHost、PricingHost、CostingHost 与 Gateway 显式组合 Auditing 拥有的 SourceJournal，
  以独立连接和事务保存操作 Started / Finished，再交付中央观察存储。journal 是来源宿主中 Auditing 模块的数据，
  不归 Platform / Pricing / Costing 业务上下文或网关；这是操作观察链路，不改变上面的 Platform 已提交事实关系。
  异步日志依赖的诊断与来源业务就绪分开，日志调查或交付故障不作为摘除仍可执行业务的宿主的依据。
  网关转发与下游处理有独立执行标识、明确角色并通过追踪关联；验收范围见[操作日志](docs/operation-logging.md)。

- **Costing → Auditing（治理未完成）**：成本输入创建/修改与结果应用的最小事实同本地状态提交，
  通过 `Costing.Contracts` 的 `CostSheetCommittedV1` 交付；不传金额，过期结果与空操作不生成对象变化事实。
- **Pricing → Auditing（治理未完成）**：报价创建、手工输入变化、上游成本接纳和结果应用通过
  `Pricing.Contracts` 的 `PriceQuoteCommittedV1` 交付，与本地状态同事务；成本接纳引用 Costing 对象，不传金额。
  保存失败、取消及租约过期回滚与重启后的真实 RabbitMQ 交付已验证，来源容量及专门的审计恢复仍待完成。

- **Costing → Pricing**：通过 `CostCalculatedV1` 传递完整成本快照及来源版本。Costing 的结果与 Outbox 同事务；
  Pricing 的 Inbox、成本投影与重算任务同事务，费率归 Pricing。契约在 Costing.Contracts 中。

- **Identity → 其他上下文**：Identity 是所有其他上下文的**上游**——它们不自己判断"你是谁"，
  只消费 Identity 给出的结论（`ICurrentUser`、`PermissionKey`）。这条是**运行时成立**的。
- **Platform → 所有上下文**：Platform 拥有配置的**唯一真相**。其他上下文只按 key 读取，
  **绝不** join Platform 的表。
- **Gateway → 所有上下文**：唯一入口。网关做身份**认证**（验签），各上下文做**授权**——
  两件事分开，每加一个上下文都不用改网关的授权逻辑。这条**已实现并测过**。
- **Identity → Costing / Pricing / Gateway 管理面**：当前会话判定包含有效性与权威根身份。
  独立宿主每次授权通过固定 HTTP 契约取得结论，平台模块由 Identity 本机适配器供给；
  登出后的新授权拒绝旧版本，已经接受的后台工作继续。见 [ADR-0027](docs/adr/0027-current-session-authority.md)。
- **Identity → Costing / Pricing 操作面**：`CurrentAccessV1` 绑定主体、会话与代码声明的单操作键；
  普通角色许可、根旁路和当前会话来自 Identity 的同一次已提交读取，不缓存最终允许。
  撤回成功后的新授权拒绝，已经受理的后台工作继续。见 [ADR-0028](docs/adr/0028-current-operation-authority.md)。
- **不变量**：跨上下文引用是**被测试禁止**的（`Contexts_MustNotReferenceOtherContexts`，
  两层都查：编译产物 + `csproj`），且**八条不变量全部反向验证过**。

### 还没建的（**不要照着它 grep**）

Platform → Auditing 与业务样板 Costing → Pricing 已使用真实消息协作，其余事件生产者按真实需求逐个接入。
各自的数据与执行任务仍由所属上下文持有。

以下是**设计意图**，不是现状：

- **其余上下文 → Auditing 的 AuditFact**：尚未接入；不以通用 HTTP 观察或原始载荷替代上下文自己的最小已提交事实契约。
- **Scheduling → 其他业务目标**：除已接入的 Costing 重算外，按目标上下文自己的契约逐项扩展。

**谁把这些做出来，请把对应的条目从这一节移到上面那一节。**

## False friends

这些词在不同上下文里含义不同，读到时必须先确认在说哪一个：

- **审计**：业务行的四个元数据字段、操作执行观察与已提交事实是三种证据。Auditing 的 `OperationObservation`
  不能替代 `AuditFact`，HTTP 202 受理也不能替代 Costing / Pricing 的任务完成。
- **Token**：Identity 里指登录签发的访问/刷新令牌（`RefreshToken`、`AccessToken`）；
  Auditing 接收 RefreshToken 签发/消费/撤销的最小事实，只保留内部标识，不保存令牌、哈希或自由文本原因；没有独立 TokenLog 聚合。
- **Permission**：Identity 里指"角色对某个路由+HTTP 方法的授权"，是一个**授权判定**
  （`PermissionKey`，形如 `路由模板:HTTP方法`）。
- **Task**：Scheduling 里指被调度的**任务定义与执行记录**（`ScheduledTask`）；
  不要与 .NET 的 `Task` 混用命名。
- **Platform**：在 Identity 的词表里指"用户可登录的端"（后台/PC/小程序/POS）；
  在 `CONTEXT-MAP` 里指 **Platform 这个上下文**。同一个词，两个意思——
  这类撞名正是这一节存在的理由。
- **Region**：**计划中**。Platform 打算拥有行政区划字典；目前 Platform 只有全局设置
  （`GlobalSetting`）。Identity 的数据权限若将来按区域过滤，引用的会是它的 ID。
