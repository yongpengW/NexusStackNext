# 69 — 两次 review 出来的一切：处理与证据

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: —

## 目标

第 30/31 两轮 review（**通读全仓**与 **MattSkills 符合性审计**）列出的问题，逐条处理，
每条都要有**可复现的证据**——脚本、真库/真 broker 的测试、或变异（制造违规看它红不红）。

## 一、代码正确性（第 30 轮 review）

### 1. 消息重试链是死的（P0）

幂等三段式只有两段：**缺"失败删键"**。消费者在调处理器**之前**占掉去重名额，失败后不归还 →
重投被判重复而 ACK 跳过 → 重试档位永不重入、处理器失败永远到不了死信队列，
而计数器还在显示"重试过"。

- 修：`IInboxStore.ReleaseAsync`（接口 + 内存 + EF 三个实现）；`RabbitMqConsumer` 在 `!handled` 时归还；
  `AuditIngestion` 落库抛错时归还；契约测试新增 3 条（归还后能再进、作用域精确、未知消息无操作）。
- **变异**：摘掉归还 → 死信验收 **红 36 秒**（`depth=0 count=1`，处理器只被调用一次）。
- **修复**：`depth=1 count=3`，耗时 9 秒（正好 2 秒 + 4 秒两档）；真 broker 6/6 通过。
- 顺带修掉**这条验收本身的假绿**：队列名不含前缀 → 跨运行共享 → 死信里的 39 条残留让断言凭空成立。
  详见票据 21 的第 16 轮补记。

### 2. 连接表主键只有一列（P0，数据层）

`PK_user_roles (role_id)`、`PK_role_menus (menu_id)` → "全库只能存在一条谁有哪个角色"成了数据库的事实。
内存适配器不拦、领域测试不拦（各自只分配一次）。

- 修：`HasKey("user_id","Value")` / `HasKey("role_id","Value")`；删旧迁移、`dotnet ef` 重新生成。
- **DDL 证据**：`PK_user_roles (user_id, role_id)`、`PK_role_menus (role_id, menu_id)`。
- 新测试 `JoinTables_AllowMoreThanOneRow`：两个用户共用一个角色、两个角色共用一张菜单，并数连接表行数。
- **变异**：改回单列并重生成 → 测试 **1 秒内变红**；还原后 Identity 集成 **24/24**。

### 3. 拦截器写完了但没有注册点（P1）

审计字段在生产里从不写（`CreatedAt` 恒为 default）、领域事件也不进发件箱——而"没写"与"没有要写的"看起来一样。

- 修：`UseNexusStackInterceptors(IServiceProvider)` 把两个拦截器接在**装配的缝**上；
  `AddIdentityEntityFrameworkStorage` 改用它；新增 `NoIntegrationEventsMapper`——
  把"当前还没有上下文发布集成事件"从一次**缺席**变成一条**写下来的决定**。
- `ICurrentUser` 用 `GetService` 降级到匿名（基座不注册它；用 `GetRequiredService` 会让 Identity 的 16 条测试一起红——
  这条是实测出来的）。
- 新测试两条：走生产装配缝的行为测试（审计字段被写 + 映射得到的事件入队、无映射器时一条都不入队而审计照写）；
  一条结构测试（Identity 上下文的选项里确实带着两个拦截器）。
- **变异**：摘掉 Identity 的装配调用 → 结构测试 `Assert.Contains` 失败。

### 4. 结构检查的空枚举守卫（P1）

5 处 csproj 层与 `Hosts_MustComposeExplicitly` 在"什么都没扫到"时会**静默通过**——
正是 AGENTS.md 纪律第一条的形状。

- 修：`SolutionAssemblies.SourceProjectPaths(pattern, subDirectory)` 带守卫的取数，补在**源头**。
- 顺带修掉两层判据不一致：第一层（AssemblyRef）此前**没有 `.Contracts` 豁免**，而不变量 2 正允许它。
- **变异**：把 pattern 改成不存在的名字 → 两条检查**响亮失败并点名**。

### 5. 聚合契约与四处丢弃（P2）

- `MenuTree.Update` 无条件 `Changed()` → 内容相同即空操作（+ 同位测试）。
- `ScheduledTask.Enable` 无幂等 → 同一个 `from` 是空操作、不同 `from` 才是改变（+ 测试）。
- `StoredFile.MarkStored` 无幂等 → 同句柄同大小是空操作（+ 测试）。
- `Role.ReplaceGrants` 的 XML 文档承诺"返回失败"而代码抛异常 → 判据提到建集合之前、文档改成抛。
- `ScheduleRunner` 丢弃 `MarkExecuted` 的结果 → 不再丢（今天到不了那条路，但"结果被丢掉"是会腐烂的形状）。
- **变异**：三处空操作守卫同时摘掉 → 三条测试各自变红。

### 6. 假通过的断言（P2）

- `PermissionKeyTests`：`... is false || true`（**恒真**）→ 改成真断言。
- `IdentityPersistenceTests`：按 `attname='Id'` 扫，列名一折成就零行 → 补"至少扫到 6 列"的守卫。
- `RefreshTokenTests`：`DoesNotContain("Token", members)` 是**元素精确匹配**，`PlainTokenValue` 照样过 →
  改成"名字里带 Token 的成员**恰好只有** TokenHash"。
- **变异**：加一个 `PlainTokenValue` → 目标测试红，`User` 那条不受影响。

### 7. 四个上下文在进程内没有授权判定（P1）

只有 Identity 挂了授权过滤器，宿主注释却说"授权过滤器由模块自己挂在它的分组上"。

- 修：Platform（读匿名 / 写要求认证）、Scheduling（信息面匿名 / 管理面要求认证）、
  Files（全部要求认证）、Auditing（显式匿名 + "经边缘不可达"的说明）——
  用框架的 `RequireAuthorization()` / `AllowAnonymous()`，与边缘路由表**逐条对应**。
  Identity 仍用自己的过滤器（它要算权限键与会话版本）。
- 新测试 `ContextAuthorizationTests`：公开读匿名可达、写路径无令牌 401。
- **变异**：摘掉 Files 上传的 `RequireAuthorization()` → 未认证直连拿到 **201 Created**，测试红。

## 二、MattSkills 符合性（第 31 轮 review）

| 差距 | 处理 |
|---|---|
| 6 行决策写在 `Decisions so far` 但面板读不到 | 移入 `## Notes`（它们是 standing preference，不是走过的路线） |
| `Type: bug` 不在 wayfinder 四类里 | 改回 `task`（`bug` 是 triage 类别，不是 wayfinder 类型） |
| 13 张票的 triage 标签与状态矛盾 | 按状态归位：9 张 `resolved` → `ready-for-agent`；18 号 → `needs-info`；14 号 → `ready-for-human` |
| wayfinder 的**执行型 override** 没声明 | `## Notes` 里写明"本 effort 把执行纳入地图" |
| 无编码标准单源 / 无 format 门禁 / spec 缺 to-spec 的 7 节 / CONTEXT.md 含实现细节 / ADR Status 形状 / 评论线程迁移 | **未做**，见下 |

同时把符合性**变成了会失败的检查**：`scripts/check-tracker.ps1` 从 10 项扩到 18 项
（Type 取值 、Labels 与 Status 自洽、地图五节逐字、决策索引行形式、字段不得写成粗体、
Blocked by 形状、后端探测锚点、调色盘 11 键、CONTEXT.md 术语必须有 `_Avoid_`），
六条做过反向验证（制造违规 → 精确变红 → 还原）。`docs/agents/issue-tracker.md` 新增
「面板能读到什么（实测）」一节，把机器那一侧的行为逐条写清。

## 三、验证

```
pwsh -File scripts/check-tracker.ps1          → 干净（68 张票据、5 个上下文、6 个 ADR 目录）
pwsh -File scripts/assert-no-credentials.ps1  → 324 个文件、8 条规则、0 命中
pwsh -File scripts/run-tests.ps1              → 21 个工程全部通过，约 104 秒，0 跳过
                                                  （含真 PostgreSQL 与真 RabbitMQ）
```

变异记录：**6 次**，每次都先确认变异能编译，再确认目标检查变红，最后还原并确认绿。
探针残留 **0 处**——变异用的标记是 `MUTATION-` 与 `DIAG-` 开头的那两种，
写这一句的时候注意别把标记本身留在代码里（本行是说明文字，不是探针）。

## 四、没做完的（诚实清单）

| 未做 | 为什么 |
|---|---|
| 票据 67：菜单管理端点与根账号播种 | 那是产品决策（三条路各有代价），等用户选；本票不含 |
| `spec.md` 补 to-spec 的 7 个逐字 H2 | 要动 256 行历史文档，属"内容迁移"而非"改个形状"，单独一轮做 |
| `docs/agents/coding-standards.md`（标准单源）、`dotnet format` 门禁 | 都是新增文件/脚本，不与本票的修复耦合 |
| `CONTEXT.md` 收紧为纯词表、ADR 补 `Status:` frontmatter、`## Comments` 迁移 | 同上；迁移会动 36 张票的标题层级 |
| 反射扫描遗漏的判据（`TimeProvider`、`DateTime.Now`、`Guid.CreateVersion7` 等） | 已记录在架构测试的注释里，属已知边界 |
| 队列残留清理（broker 上的测试队列不再删除，只是名字唯一） | 名字唯一之后残留只是占地方，不再影响结论 |

## Comments

### local

**第 1 轮：为什么这一票同时动了代码与规范**

两次 review 的发现**指向同一件事**：*声明与事实之间的距离，只有在有人去量的时候才存在*。
代码侧最严重的两条（重试链、连接表主键）都是"测试全绿而路走不通"；
规范侧最严重的一条（评论线程、决策行）也是"写在那里而机器看不见"。
所以这一票的每一处修复都配一个**能失败**的检查，而不是一句"已修"。
