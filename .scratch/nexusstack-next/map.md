# Map: NexusStackNext

Labels: wayfinder:map

## Destination

在 `D:\LeoProject\DotNetProject\NexusStackNext\` 建成一套**真正的微服务 + DDD 面向领域**的后端模板：
以 5 个限界上下文承载原 NexusStackBackend 的系统管理能力，保留其已验证的工程资产，
补齐微服务与 DDD 的缺口，且仓库本身仍可作为 `dotnet new` 模板复用。

参照物：`D:\NexusStack\NexusStackBackend`（只读；唯一例外见票据 16）。

## Notes

- 参照物是 .NET 10 的**单 WebAPI 分层应用**：4 个可部署单元全部 `ProjectReference` 同一个 `NexusStack.Core`，
  "我是哪个服务" 用运行时枚举 `CoreServiceType` 表达，而非程序集边界 → 网关被迫携带 EF Core/FFmpeg/Excel。
- 评审证据在 `review/`（**6 份**）：`01`–`04` 量参照仓库（核心/基础设施、数据/持久化、宿主/集成、
  消息/缓存/鉴权/设计文档），逐条带 `file:line`；`05` 用深模块的尺子量自己；
  `06` 是覆盖率核对（结论：01–04 **没有缺口**，并记录了一次假发现）。
- 方案主体：`spec.md`
- 票据：`issues/01..41-*.md`
- 本仓库自带 MattSkills 规范：`.scratch/` 跟踪器、`docs/agents/*`、`CONTEXT-MAP.md`、`docs/adr/`。
- 架构不变量写在 `AGENTS.md`，违反即 bug，并由 `Architecture.Tests`（票据 05）断言。

## Decisions so far

- [0001 按限界上下文切分](../docs/adr/0001-five-bounded-contexts.md) — Identity / Platform / Scheduling / Auditing / Files，不做公共 Core。
- [0002 每上下文独占 PostgreSQL，放弃多库支持](../docs/adr/0002-postgres-per-context.md) — 顺带把 README 的空头承诺写成事实。
- [0003 YARP 作为独立边缘，认证在网关、授权在上下文](../docs/adr/0003-yarp-edge.md)
- [0004 跨上下文只走契约与集成事件 + Outbox/Inbox](../docs/adr/0004-contracts-and-events.md)
- [0005 Aspire 只编排应用进程，中间件走外部依赖](../docs/adr/0005-aspire-local-orchestration.md) — 因本机无任何容器运行时而修订。
- [0006 保留 AgileConfig 作为配置中心](../docs/adr/0006-agileconfig.md)
- [0007 事件契约名显式版本化，不用 CLR 类型名](../docs/adr/0007-versioned-event-contracts.md) — 否则拆程序集即静默断链。
- [0008 不引入需要商业授权的依赖](../docs/adr/0008-no-commercial-dependencies.md) — 商业批量库会绕过审计。
- [0009 主键唯一权威：应用侧生成，数据库不做 IDENTITY](../docs/adr/0009-application-generated-ids.md)
- **定位**：模板 + 一个参考业务域。
- **第一轮完成定义**：骨架成立 + Identity 端到端跑通 + 四层测试；其余四个上下文只建骨架。（用户 2026-09-29 选定）
- **密钥处理**：给参照仓库加打包排除规则，密钥文件本体保留。（用户 2026-09-29 选定，见票据 16）
- **落盘**：`D:\LeoProject\DotNetProject\NexusStackNext\`，独立 git 仓库。
- **节奏**：先出方案（评审 → ADR → spec → 票据）给用户过一遍，再动代码。
- **2026-09-29：目标达成。** 三条原始要求（全面评审 / 真正的微服务 + DDD 新项目 / MattSkills 规范）
  逐条验收通过，用户定义的第一轮完成标准**已达成并超出**（五个上下文全部端到端）。
  交付状态、保留项、补齐项与**没有做什么**的诚实清单见票据 42。

## Ticket index

| # | 票据 | Blocked by | 状态 |
|---|---|---|---|
| 01 | 解决方案骨架与构建基线 | — | ✅ resolved |
| 02 | BuildingBlocks.Domain 领域基元 | 01 | ✅ resolved |
| 03 | BuildingBlocks.Application 用例编排基座 | 01, 02 | ✅ resolved |
| 04 | 消息基座（Outbox/Inbox/拓扑/失败语义/ID 生成） | 02, 03 | ✅ resolved |
| 05 | Architecture.Tests 把不变量变成断言 | 01 | ✅ resolved |
| 06 | Identity 领域模型 | 02 | ✅ resolved |
| 07 | Identity 基础设施 | 04, 06 | ✅ resolved |
| 08 | Identity 应用层与权限预计算 | 03, 06, 07 | ✅ resolved |
| 09 | Identity API 与正确 HTTP 语义 | 08 | ✅ resolved |
| 10 | 认证：令牌签发与登录防护 | 06, 09 | ✅ resolved（签发+轮换完成） |
| 11 | 授权：预计算权限与过滤器 | 08, 10 | ✅ resolved（过滤器 + 撤销） |
| 12 | Gateway 真正的边缘 | 01, 10 | ✅ resolved（管理 API + 单写者设计；配置中心升级待凭据） |
| 13 | 其余四个上下文骨架 | 01, 02 | ✅ resolved |
| 14 | Aspire AppHost | 01 | 🔄 claimed（3/4；验收 1 卡在开发证书信任） |
| 15 | 测试工程与测试库接线 | 04, 05 | ✅ resolved |
| 16 | 参照仓库凭据打包口子 | — | ✅ resolved |
| 17 | 新项目模板化与 CI | 01, 13 | ✅ resolved |
| 18 | 【严重】appsettings 里提交了 AgileConfig secret | — | ⏸️ needs-info（决定轮换；等新 secret） |
| 19 | EF Core 基座（DbContext/主键/软删/审计/UoW/Outbox-Inbox 存储） | 04, 15 | ✅ resolved |
| 20 | RabbitMQ 拓扑规划与消费决策 | 04 | ✅ resolved |
| 21 | RabbitMQ 通道绑定与消费循环 | 20 | ✅ resolved（6/6 真 broker 验收） |
| 22 | RBAC 授权核心：权限键与判定 | 03 | ✅ resolved |
| 23 | 网关路由表模型与校验 | 01 | ✅ resolved |
| 24 | 网关宿主：路由、关联 ID、fail-closed 认证 | 23 | ✅ resolved |
| 25 | Identity 宿主骨架 | 01, 06 | ✅ resolved |
| 26 | 路由表持久化：端口与原子写 | 23 | ✅ resolved |
| 27 | 深模块评审与设计用语规范 | 01 | ✅ resolved |
| 28 | 不变量可追溯性：把声明变成被检查的声明 | 05 | ✅ resolved |
| 29 | 跟踪器一致性检查 | 28 | ✅ resolved |
| 30 | 覆盖率核对，以及一次假发现的更正 | 16 | ✅ resolved |
| 31 | 角色归属：Identity 纵向切片的第一刀 | 06 | ✅ resolved |
| 32 | 路由表文件存储的并发缺陷 | 26 | ✅ resolved |
| 33 | 权限投影：把四段链路接起来 | 31 | ✅ resolved |
| 34 | Identity 端到端跑通（不需要 PostgreSQL） | 33 | ✅ resolved |
| 35 | 客户端 → 网关 → Identity 拓扑打通 | 34, 24 | ✅ resolved |
| 36 | Platform 端到端：验证这套模式可复制 | 34, 35 | ✅ resolved |
| 37 | Auditing 端到端：消息基座第一次有真实调用方 | 36 | ✅ resolved |
| 38 | Files 端到端：第一个真实的生产形态适配器 | 36 | ✅ resolved |
| 39 | 补齐 Auditing 与 Files 的应用层单元测试 | 37, 38 | ✅ resolved |
| 40 | Scheduling 端到端：五个上下文全部跑通 | 36 | ✅ resolved |
| 41 | 五个服务 + 一个边缘：作为一个系统的验证 | 40, 35 | ✅ resolved |
| 42 | 交付状态与验收证据 | 41 | ✅ resolved |
| 43 | 解决方案分组：让 VS 里看得清 | 42 | ✅ resolved |
| 44 | 聚合并发令牌（评审 07 第一节） | 02 | ✅ resolved |
| 45 | 消息端口上移到 Application（评审 07 第二节） | 04 | ✅ resolved |
| 46 | 可观测性补缺：readiness 与 OpenAPI | 01 | ✅ resolved |
| 47 | 测试工程整理 | 39 | ✅ resolved |
| 48 | 配置与横切关注点：决策已记，实现为零 | 01 | ✅ resolved |
| 49 | AgileConfig 真实拉取：需要你的凭据 | 48 | ✅ resolved |
| 50 | 配置中心接入方案：一个基座 + 六个宿主 | 48 | ✅ resolved |
| 51 | 平台能力合并为一个宿主与一个库 | 50 | ✅ resolved |
| 52 | 评审 08：合并之后的遗漏与不可重现的验证 | 51 | ✅ resolved |
| 53 | 本机环境变量目录 env/ | 50 | ✅ resolved |
| 54 | 【严重】OutboxPublisher 依赖零实现端口，dotnet run 从未跑起来过 | 51 | ✅ resolved |
| 55 | AgileConfig 客户端选项只映射了 3 个字段 | 54 | ✅ resolved |
| 56 | 【严重】配置优先级在真实宿主里不成立 | 55 | ✅ resolved |
| 57 | AgileConfig 的 .serviceid 会随模板分发 | 56 | ✅ resolved |
| 58 | OpenAPI 有文档没有界面；补上 Scalar（仅开发环境） | 51 | ✅ resolved |
| 59 | 生产也要看文档：网关聚合各后端 OpenAPI | 58 | ✅ resolved |
| 60 | launchSettings 用随机端口，VS 里 F5 直接得到坏系统 | 59 | ✅ resolved |
| 61 | 界面从 Scalar 换成 Swagger UI | 60 | ✅ resolved |
| 62 | 跟踪器检查器不查地图的状态列，于是它漂了 | 61 | ✅ resolved |
| 63 | 上下文规范检查查着一个早已不存在的形状 | 52 | ✅ resolved |
| 64 | 测试 schema 会残留，且没有清理路径 | 19 | ✅ resolved |
| 65 | 测试工程命名一半带前缀、一半不带 | — | ✅ resolved（22 工程全部统一） |
| 66 | 测试套件耗时完全由远端数据库延迟决定 | — | ✅ resolved（测试从 1 小时降到 78 秒） |
| 67 | 授权链的第一环是断的 | 28 | ⏸️ needs-info（等产品决策） |
| 68 | Platform 的设置写入经边缘不可达 | 30 | ⏸️ needs-info（等产品决策） |

**剩余部分几乎全部阻塞在外部输入上。** 已经切完的"不需要外部输入"的片段：
票据 20、22、23、24、25、26、27、28、29、以及 12 的限流与持久化。
每一条都由实跑或变异验证过，见下面的证据段。

**下一轮**：把 Platform 也做成端到端（设置的读写 + 配置解析），验证这套模式**可复制**。
再往下就需要你的输入了。

## 已完成阶段的证据

- 构建：`dotnet build NexusStackNext.slnx` → 成功，**0 警告 0 错误**（35 个项目）。
- 测试：`dotnet test NexusStackNext.slnx` → **382/382 通过**。
- **补测试补出三个真缺陷**（票据 39）：其中一个**会静默丢消息**——
  `AuditIngestion` 原来先登记去重再校验聚合，于是非法消息占掉名额，
  重投时被判为重复而**永久丢弃，且从任何地方都看不出来**。
  另两个是"字节删不掉却返回成功"与"存储读失败直接抛异常"。
  **三条测试的"修复前失败、修复后通过"本身就是变异证据**——它们抓的是真缺陷，不是注入的。
  教训：运行时验证覆盖不了失败分支，而**失败分支才是设计真正要防的东西**。
- **系统级验证**（票据 41）：五个服务 + 一个边缘一起跑，六个 `/health` 全 200；
  边缘六条路由（三条公开、三条要求认证）；**Auditing 刻意不在边缘上**——
  同一个端点经边缘 **404**、直连 **202**。
  **"是一个服务"与"在边缘上可达"是两件事**，而参照仓库从没把这条写下来过。
- **五个上下文全部端到端**（票据 34、36、37、38、40）：
  Identity（权限链路）、Platform（写入 → 经网关读出）、Auditing（事件进入 → **幂等去重** → 落库）、
  Files（上传 → **字节真的落盘** → 下载 → 删除）、
  Scheduling（**定义任务 → 调度节拍触发 → 下次时刻推进**）。
  五个都不一样，但都套在同一个骨架上——**这才是"一套微服务"的完整说法**。
  五个上下文现在都有 `Domain` + `Application` + `Infrastructure` + `Api` 四层与独立测试工程。
- **应用层单测已补齐**（票据 39）：Identity 11 + Platform 11 + Auditing 10 + Files 12 + Scheduling 10。
- **应用层单测已补齐**（票据 39）：四个端到端上下文现在都有应用层单测
  （Identity 11 + Platform 11 + Auditing 10 + Files 12）。
- 测试：`dotnet test NexusStackNext.slnx` → **360/360 通过**
  （领域 28 + 应用 53 + 基础设施 62 + Identity 领域 100 + Identity 应用 11 + Platform 应用 11 +
  上下文骨架 40 + 网关路由 46 + 架构 9）。
- **三个上下文端到端**（票据 34、36、37）：Identity（权限链路）、Platform（写入 → 经网关读出）、
  Auditing（事件进入 → **幂等去重** → 落库）。**消息基座第一次有真实调用方**：
  `IInboxStore` 从"只有测试替身"变成有真实适配器。
  实跑确认幂等键绑的是**消息标识**而非业务标识——内容相同、消息不同的两条都会被记录；
  参照仓库绑业务标识（review/04 F7），后果是"两次合法的相同操作被当成重复而丢掉一次"。
  三者结构相同但内容不同——**同一个骨架装不同的东西，这才是"可复制"的意思**。
  Platform 的实跑里"**直连写、经网关读**"同时证明了转发、存储与接线三层都对。
- **Identity 端到端已跑通**（票据 34），**不需要 PostgreSQL**：
  创建用户 → 建角色 → 注册端点 → 授予菜单 → 分配角色 → 查权限键 → 授权判定。
  实跑：被授予的端点 `Allowed`、同菜单下另一个 HTTP 方法 `Forbidden`、重复用户名 `409`、用户不存在 `404`。
  **用户定义的"第一轮完成"（骨架 + Identity 端到端）由此达成。**
  三处只有把两端接起来才暴露的模型缺口全部补齐：**用户没有角色集合**（31）、
  **菜单与端点没有连接**（33）、**菜单与端点在 API 层无入口**（34）。
- **完整拓扑已跑通**（票据 35）：`客户端 → 网关 → Identity`。同一个端点**直连 201、经网关 401**——
  转发是好的、保护是开的，两件事分开验证。**保护目前只存在于边缘**：
  谁能直连到服务谁就绕过它，因此"业务服务不对外暴露"必须是**显式的部署不变量**，而不是一个假设。
  参照仓库恰好是这条假设失效的样本（四个可部署单元共享一个 Core，
  "网关是唯一入口"从来没被写下来过，也就没人能检查它）。
- **五个上下文各有宿主且实跑通过**：Identity / Platform / Scheduling / Auditing / Files 的
  `/health` 全部 `HTTP 200 Healthy`。
- **网关限流实跑**：窗口配额压到 5，连发 8 次 → 前 5 次到达后端、后 3 次 **429**；
  引用未注册的限流策略名 → **拒绝启动**并指出是哪个名字。
- **网关实跑验证**：架在 Platform/Files 两个真实后端前面 —— 公开路由被转发（HTTP 200）、
  响应头转换生效（`X-Gateway: nexusstack`）、关联 ID 生成并回传、带 `trace-abc-123` 时沿用而不覆盖、
  受保护路由在**未配置认证**时返回 **401**（fail-closed）、`/gateway/routes` 可查；
  配置无效时**拒绝启动**并指出具体问题码。
- **运行时验证**（不只是编译过）：五个上下文宿主实跑，`/health` 全部 `HTTP 200 Healthy`；
  `Platform` 的非法配置键返回 400；`Files` 的目录穿越文件名返回 400；
  `Scheduling` 在 35 秒内留下 `调度一轮：检查 1 个，触发 1 个，跳过 0 个` 的真实触发记录；
  `Auditing` 的 `GET /api/auditing/entries` 现在返回 **405**（不再是当时的 404）——
  那条路径已被写入口占用，**ADR-0001 里"得到 404 是故意的"这句因此过期，已更正**。
- **聚合并发令牌已落地**（票据 44，ADR-0011）：`AggregateRoot.Version` + `Changed()`，
  九个聚合的每条改变路径自增、每条空操作路径不自增。
  **契约是"Version 改变当且仅当可观察状态改变了"**——空操作自增会让乐观并发在没有冲突时误报冲突，
  那时这个机制就废了。**编译器管不了这条**，所以每个聚合都有测试。
  **反向验证做了两个方向**：漏掉自增 → 变红；空操作误自增 → 也变红。
- **配置与横切关注点补上了**（票据 48）：用户问"集成好 AgileConfig 了嘛"，
  实测答案是**零**——而且查下去发现锁定的五项技术里**没有一项真正接上**，
  而票据 42 把"技术选型"列进了"**保留了什么**"、没列进"没做什么"。
  **"决定了"与"做完了"之间的落差被放错了位置**，与并发令牌同一失效形态。
  现已完成：配置优先级（**有测试守着**）+ AgileConfig 接入与降级路径 + Serilog + SignalR。
- **架构自查**（评审 07，`review/07-architecture-self-review.md`）：对抗性地查自己的设计，
  查出八条问题。骨架是稳的（零跨上下文引用、五个 `*.Domain` 零包），
  但最严重的一条是**我在 `review/02` 第 348 行批评过参照仓库"无并发令牌 → 静默丢更新"，
  然后在自己的 `AggregateRoot` 里原样犯下**——全仓 0 处并发令牌，
  而且它**不在票据 42 的"没做什么"清单里**。已开票据 44–47。
- **解决方案分组**（票据 43，用户反馈"VS 里 37 个项目平铺没法看"）：`.slnx` 改成
  扁平 `<Folder>` + 路径式名字，镜像磁盘布局（`/src/BuildingBlocks/`、五个 `/src/Services/<上下文>/`、
  `/src/Gateway/`、`/tests/`、`/Solution Items/`）。
  实测确立了 `.slnx` 的**两种失败形态**：
  ① `<Folder>` 套 `<Folder>` → 被套住的项目**被静默吞掉**（37 → 35，`sln list` 与 `build` 都不报错）；
  ② `<Folder Name>` 少首尾 `/` → 整份文件加载失败（`MSB4025`，响亮）。
  两者现在都由 `check-tracker.ps1` 的第 10 项（解决方案 ↔ 磁盘双向比对）守着，
  **反向验证**：把嵌套注回去 → 精确报出 2 个丢失项目。
- 反向验证 1（构建基线）：注入 `CS0219`/`CS0169`/`IDE0005`/`CA1822` 探针后构建**失败**，移除后恢复。
- 反向验证 2（架构不变量）：注入 4 类违规，对应 4 条测试**全部失败**；探针已删净。
- 反向验证 3（事务语义）：删除分发器的成功守卫后，只有 1 条测试变红。
- 反向验证 4（消息失败语义）：反转投递器成功判定后 **7 条测试变红**。
- 反向验证 5（菜单环检测）：去掉 `Move` 的祖先判断后，只有 1 条测试变红。
- 反向验证 6（重试路由）：把死信路由键改回"事件名"（参照仓库原 bug）后 **2 条测试变红**。
- 反向验证 7（调度推进）：去掉执行后的下次时刻推进后 **3 条测试变红**。
- 反向验证 8（授权 fail-open）：把"未声明要求"改回放行（参照仓库原行为）后 **1 条测试变红**。
- 反向验证 9（网关吞异常）：把 JSON 解析失败改回"当作空表成功"（参照仓库原行为）后 **2 条测试变红**。
- 反向验证 10（可追溯性腐烂）：把当初那句真实的腐烂注回去（表里引用一个不存在的测试名）后
  **1 条测试变红**——正是新加的覆盖检查。
- 反向验证 11（角色分配幂等）：去掉 `AssignRole` 的幂等守卫后 **1 条测试变红**，精确命中。
- **不稳定的测试查出三层真缺陷**（票据 32）：顺着"全量并行偶尔红一条"查下去，依次是
  ① `File.Move(overwrite: true)` 在读者持有目标时抛 `Access denied`——**读流量一稳定写者就永久失败**；
  ② 旧测试忽略了写者的返回值，所以"写者一直失败、测试却绿"是可能的；
  ③ 旧测试断言"读者一次都不许失败"，**比实现保证的更强**——`File.Replace` 期间目录项是空窗的。
  更正后的契约：保证**内容完整性**，不保证**目录项连续性**。全量测试连跑 **4 遍全绿**。
- **跟踪器一致性检查**：`scripts/check-tracker.ps1` → 28 张票据、5 个上下文、6 个 ADR 目录全部一致。
  它**第一次运行就抓到一张自相矛盾的票据**（17 号：状态 resolved、却仍写被未解决的 15 阻塞，
  活了 15 轮）。三类变异（非规范状态 / 悬空依赖 / 地图索引缺行）各自精确命中，改动字节级还原。
- **规范完整性**：5 个上下文各有 `CONTEXT.md` + `docs/adr/` + 宿主；`CONTEXT-MAP.md` 覆盖全部；
  10 份系统级 ADR + 5 份上下文级 ADR；6 份 review；`docs/agents/` 4 份约定 + 设计用语规范。
- **参照仓库评审覆盖率**：review 01–04 覆盖核心/基础设施、数据/持久化、宿主/集成、消息/缓存/鉴权/文档。
  实测数据：参照仓库 **343 个 .cs / 28,130 行 / 测试项目 0 个 / CI 空目录 / 4 个 Dockerfile 均为 VS 调试模板**。
  review 06 是一次**覆盖率核对**：结论是**没有缺口**（"测试"在 01–04 里出现 60 次，Dockerfile 23 次，CI 19 次），
  只补了三处此前未提及的小项（中央包管理 / `.editorconfig` / Dockerfile 无 `USER`）。
  **该评审同时记录了一次假发现**：上一轮声称"测试出现 0 次"，实际是 `-Filter '0[1-4]*.md'` 匹配不到任何文件所致。
- **测试自证的第五次**：网关路由持久化的并发测试（持续读写 2 秒）抓出"原子写还要求读取方
  允许 delete 共享"——Windows 上覆盖一个正被打开的文件会失败。修法已写明理由。
- **模板端到端验证**：`dotnet new install` → 生成 `DemoShop` → 名字残留 **0** → 构建成功 →
  **279/279 测试通过**（含检查 `DemoShop.*` 命名空间的架构不变量测试）→ 宿主 `/health` = HTTP 200。
- **凭据断言实跑**：`scripts/assert-no-credentials.ps1` → 检查 133 个文件、6 条规则、**0 命中**。
- 测试自证两次：票据 05 的领域白名单在第一个真实上下文出现时**变红**（规格不完整，已修）；
  `Hosts_MustComposeExplicitly` 在 Platform 出现时**变红**（把注释也算进了扫描，已修）；
  网关校验的"一次列全"测试抓出我自己写的 `continue`（把"列全"退化成了"只报第一个"，已修）。
- 参照仓库改动面：`git diff --stat` = 2 个文件、7 insertions / 2 deletions（票据 16，用户已授权）。
- 未验证项（诚实标注）：`.github/workflows/ci.yml` 已写但**未在 GitHub Actions 上跑过**——
  本机无法执行 Actions，其命令是本地逐条验证过的等价命令。

**外部依赖一览**

| 需要你提供 | 阻塞的票 |
|---|---|
| PostgreSQL 测试库 | 15 → 19（EF 基座）→ 07（Identity 基础设施）→ 08 → 09 |
| RabbitMQ broker | 21（通道绑定与消费循环） |
| 认证形态（自研 JWT / OpenIddict / Authentik） | 10 → 11（授权）、12（Gateway） |
| 中间件连接信息 | 14（Aspire 编排） |
| 已提交凭据的处置决定 | 18 |

## Not yet specified

- **数据权限**（`DataRange` + `RegionIds`）：原项目建模了但零消费点。补齐还是砍掉？（票据 11 的阻塞点）
- **认证形态**：自研 JWT / OpenIddict / Authentik（git 里有 `dev-authentik` 分支）。（票据 10 的阻塞点）
- **审计写入路径**：Outbox 事件消费（默认）还是各上下文同步写本地审计表。
- **OSS / 短信 / FFmpeg / Excel 导出**：归 Files 还是拆独立集成层。
- **模板参数面**：`dotnet new` 暴露哪些开关。（票据 17）
- **测试库**：可用的 PostgreSQL 从哪来。（票据 15 的阻塞点）
- **SignalR backplane** 与多实例粘性会话策略。
- **集成测试**在无容器前提下如何做到隔离（独立 schema + 回滚已定，待验证）。

## Out of scope

- 修改 `D:\NexusStack\NexusStackBackend`（**唯一例外**：票据 16 的两处打包排除规则，用户已授权）。
- 前端（NexusStackPro）；本次只做后端。
- 旧库到新库的数据迁移，除非后续单独立项。
