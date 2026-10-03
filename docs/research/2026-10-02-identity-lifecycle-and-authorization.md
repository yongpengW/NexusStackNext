# Identity 生命周期、跨服务撤权与第二个 Redis 消费者

核验日期：2026-10-02。本文是源码研究和后续实现建议，**不是测试执行报告，也不是生产安全验收**。没有读取 env、连接配置或凭据，没有访问旧项目的生产系统，没有运行构建、测试或攻击请求，没有修改产品代码。

源码基线：NSN `dev` 为 `4df714a737ad1e55bcbdf0508b86510e148b6a38`；研究工作区 HEAD 为 `a24c47d4a4a536fb31228ada2806a4896f6c2a0d`，日历 PR #49 的修正正在进行。已用 `git diff --name-only 4df714a -- <本研究涉及的 Identity/Platform/Costing/Pricing/授权过滤器/业务宿主路径>` 确认这些实现未被日历分支改变。NS 为 `81831672aefc1f84db3ee5596a445df88ad26bdb`，PoS 为 `6cb62274491ee50600d4103dc01a8a0a1bea114d`。以下源码链接指向工作区文件；后续变更时应以这些提交重新核对。

前置约束来自 [AGENTS.md](../../AGENTS.md)、[Identity 语言](../../src/Services/Identity/CONTEXT.md)、[Platform 语言](../../src/Services/Platform/CONTEXT.md) 和 [能力补齐规格 #34](https://github.com/yongpengW/NexusStackNext/issues/34)：上下文只通过 Contracts 交流；数据归所属上下文；一个聚合一个事务；第二个真实消费者出现后才提取 BuildingBlocks；多机 HA 最后。旧企业身份提供方、组织和店铺模型不整体搬入模板。

## 1. 结论与优先级

1. **先封闭 API 资源登记的现存扩权入口。** 当前任何有效登录用户都能创建 API 资源；有一个已授权菜单、知道该菜单 ID、目标管理权限键尚未登记的用户，可把管理权限挂到自己的菜单。该结论已形成窄修复票 [#51](https://github.com/yongpengW/NexusStackNext/issues/51)，排在日历 #45 之后、任务管理 #48 之前。研究只证明源码路径，实际 HTTP 红测试由 #51 承担。
2. **平台内的会话撤销已有实现，但不是全系统撤权。** 平台请求过滤器每次读取 Identity 的权威会话版本；独立 Costing/Pricing 以及网关自身的路由管理只验证 JWT 和 root claim。后续应以这些真实消费者补齐当前授权查询，不能把一条平台登出测试当作跨服务验收。
3. **生命周期不能只把现有 Domain 方法接上 HTTP。** 禁用和改密尚不推进会话版本，菜单删除尚未接入权限推导，若直接开放会产生旧会话恢复或“菜单已删、权限还在”的语义缺口。
4. **第二个 Redis 消费者优先选择普通 Costing 展示查询。** 现有 CostSheetView 已有真实读入口、独立数据库和可声明的旧值边界。Platform 任意设置和外部令牌都不是天然可安全复用的普通缓存；前者混合元数据和受限配置，后者还受供应商令牌轮换/限额影响。先证明两个读模型，再提取窄缓存模块。

## 2. NS / PoS 真正值得保留的能力

| 已读到的源码行为 | 依据 | NSN 应保留的价值与改进 |
|---|---|---|
| NS 有用户列表、详情、自身资料、创建、修改、启用、禁用、删除、重置口令、自助改密；修改角色/状态后清理用户上下文缓存 | [NS UserController](D:/LeoProject/NexusStack/NexusStackBackend/Host/NexusStack.WebAPI/Controllers/UserController.cs)、[UserService](D:/LeoProject/NexusStack/NexusStackBackend/Domain/NexusStack.Core/Services/Users/UserService.cs) | 管理闭环、显式自助入口值得保留；不要复制手机号后六位初始/重置口令，不把 HTTP Controller 的字段赋值当领域不变量 |
| NS 按用户和平台缓存角色、区域、API 权限，权限键为路由模板加 HTTP 方法；默认 TTL 为 10 小时，失效枚举平台删除键 | [UserContextCacheService](D:/LeoProject/NexusStack/NexusStackBackend/Domain/NexusStack.Core/Services/Users/UserContextCacheService.cs) | 保留“一次问出所需授权结论”的深度；不能把提交数据库后尽力删 Redis 的窗口当立即撤权 |
| NS 可更新角色菜单授权，并查询受影响用户进行缓存失效 | [PermissionService.ChangeRolePermissionAsync](D:/LeoProject/NexusStack/NexusStackBackend/Domain/NexusStack.Core/Services/Users/PermissionService.cs) | 授权和撤权都要可操作；失效意图不能依赖每个外层调用者记得补一步 |
| PoS 成本、MasterPricing 的危险操作用明确的操作权限，检查登录、启用状态、角色启用和菜单代码 | [OperationPermissionAuthorizationService](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/Authorization/OperationPermissionAuthorizationService.cs)、[CostManagePermissionConstants](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/CostManage/CostManagePermissionConstants.cs)、[MasterPricingPermissionConstants](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/MasterPricing/MasterPricingPermissionConstants.cs) | 成本导入、删除、强制重算、定价维护不是“一旦登录都能做”；NSN 应在对应业务端点声明权限，保留业务上下文对对象和操作的最终判断 |
| PoS 成本导入/新增/修改/删除在 Controller 内实际调用操作权限服务；导出登记异步任务并携带执行用户 | [CostManageController，cost-product-info 一组端点](D:/CWChina/CWChinaERP/CWChinaPoS/Host/POS.WebAPI/Controllers/CostManageController.cs:1127)、[MasterPriceController](D:/CWChina/CWChinaERP/CWChinaPoS/Host/POS.WebAPI/Controllers/MasterPriceController.cs) | 这是实际调用证据，不只是接口定义；NSN 的批次和导出权限应覆盖提交、查看、重试、取消、最终下载各入口 |
| PoS 现有身份代码含外部 subject 映射、Redis 用户声明、区域/店铺派生，以及登录后业务操作的数据库权限检查 | [PosClaimsTransformation](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Authentication/PosClaimsTransformation.cs)、[AuthentikUserMappingService](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/Authentication/AuthentikUserMappingService.cs)、[操作权限服务](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/Authorization/OperationPermissionAuthorizationService.cs) | 不能把旧 UserTokenService 的存在当作 PoS 当前唯一身份路径；本研究不读取部署配置，不能断言生产启用哪种方案。外部身份映射可成为将来的适配器，不复制企业区域/店铺关联或整套 IdP 管理 |

以上只说明仓库支持这些路径，不能证明生产当前开启状态、实际调用频率、缓存命中率或撤权延迟。特别是 PoS 的通用 RequestAuthorizeFilter 中旧菜单授权分支已注释；成本模块后来的显式操作授权才是本次核实的真实入口。[PoS RequestAuthorizeFilter](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Filters/RequestAuthorizeFilter.cs)。

## 3. NSN 当前完成到哪一层

下表中的“测试证据”仅指读到测试代码及其边界，**本研究没有执行这些测试**。

| 能力 | 当前实现 | 测试/持久化证据与缺口 |
|---|---|---|
| 注册、登录、失败锁定 | CreateUser / Login 用例及 HTTP 已有；错误次数在拒绝时仍提交；默认五次失败锁十五分钟 | [LoginUseCase](../../src/Services/Identity/NexusStackNext.Identity.Application/LoginUseCase.cs)、[IdentityApiTests](../../tests/HostIntegration.Tests/IdentityApiTests.cs)、[IdentityPersistenceJourneyTests](../../tests/HostIntegration.Tests/IdentityPersistenceJourneyTests.cs) 含跨重启错误次数；尚不是用户管理 CRUD |
| 刷新与登出 | 刷新秘密只存 hash；一次性消费；重放推进 User.SessionVersion；登出撤销该用户全部会话 | [TokenUseCases](../../src/Services/Identity/NexusStackNext.Identity.Application/TokenUseCases.cs)、[LogoutUseCase](../../src/Services/Identity/NexusStackNext.Identity.Application/LogoutUseCase.cs)、[Session ADR](../../src/Services/Identity/docs/adr/0003-persist-user-session-version.md)。[持久化旅程](../../tests/HostIntegration.Tests/IdentityPersistenceJourneyTests.cs) 含真实平台进程终止、重启后旧凭据拒绝；未覆盖独立业务宿主 |
| 禁用/启用 | `User.Disable/Enable` 有领域状态、事件、幂等及 built-in 禁用保护；无对应 command / HTTP | [User](../../src/Services/Identity/NexusStackNext.Identity.Domain/Users/User.cs)、[UserTests](../../tests/Identity.Domain.Tests/UserTests.cs)。[PlatformSettingsAccessTests](../../tests/HostIntegration.Tests/PlatformSettingsAccessTests.cs:113) 直接通过内存仓储安排 Disable，再用设置 HTTP 验拒绝；这不证明禁用管理用例已完成 |
| 自助改密/管理重置 | `User.ChangePassword` 只改 hash、发事件、增加 Version；没有公开用例；不改变 SessionVersion | [User.ChangePassword](../../src/Services/Identity/NexusStackNext.Identity.Domain/Users/User.cs:164)。[RepositoryTests](../../tests/Identity.IntegrationTests/IdentityRepositoryTests.cs) 有 hash 更新持久化，[PersistenceTests](../../tests/Identity.IntegrationTests/IdentityPersistenceTests.cs) 有并发冲突；没有改密后旧 access/refresh 拒绝旅程 |
| 用户角色 | AssignRole 有 command、HTTP、持久化、提交后缓存失效；RevokeRole 仅领域方法 | [IdentityUseCases](../../src/Services/Identity/NexusStackNext.Identity.Application/IdentityUseCases.cs:180)、[IdentityModule](../../src/Services/Identity/NexusStackNext.Identity.Endpoints/IdentityModule.cs:190)、[User](../../src/Services/Identity/NexusStackNext.Identity.Domain/Users/User.cs:125)。赋值用例只检查用户存在，不检查目标角色存在；角色 ID 是聚合内集合，不能由注释推导引用已完整校验 |
| 角色及授权 | 创建 Role、GrantMenu 有完整用例；Rename / ChangePlatforms / Revoke / ReplaceGrants / EnsureDeletable 只在 Domain；Role 尚无启停状态 | [Role](../../src/Services/Identity/NexusStackNext.Identity.Domain/Roles/Role.cs)、[IdentityRepositories](../../src/Services/Identity/NexusStackNext.Identity.Application/IdentityRepositories.cs)。无角色列表/修改/撤销/删除 HTTP。Grant 只确认 Role 存在，不确认 Menu 存在 |
| 菜单 | 创建及全树列表有 HTTP/存储；Move / Update / Remove(仅叶子) 是 Domain 方法 | [MenuTree](../../src/Services/Identity/NexusStackNext.Identity.Domain/Menus/MenuTree.cs)、[IdentityUseCases](../../src/Services/Identity/NexusStackNext.Identity.Application/IdentityUseCases.cs:398)。[菜单 ADR](../../src/Services/Identity/docs/adr/0001-menu-tree-is-one-aggregate.md) 要求整树一个聚合；删除尚无管理用例及撤权语义 |
| API 资源 | 创建有用例、HTTP、Postgres 唯一键；RoutePattern、Method、MenuId 创建后只读；无修改、停用、删除用例 | [ApiResource](../../src/Services/Identity/NexusStackNext.Identity.Domain/ApiResources/ApiResource.cs)、[映射](../../src/Services/Identity/NexusStackNext.Identity.Infrastructure/Persistence/IdentityDbContext.cs:214)。创建入口授权过宽，见 #51 |
| 权限读取/缓存 | User→Role→GrantedMenuIds→ApiResource；进程内五分钟 TTL、代次失效、同代单飞；提交成功后失效 | [UserPermissionReader](../../src/Services/Identity/NexusStackNext.Identity.Application/UserPermissionReader.cs)、[UserPermissionCache](../../src/Services/Identity/NexusStackNext.Identity.Application/UserPermissionCache.cs)、[事务](../../src/Services/Identity/NexusStackNext.Identity.Application/IdentityCommandTransaction.cs)。[CommandTransactionTests](../../tests/Identity.IntegrationTests/CommandTransactionTests.cs) 读到提交竞态、旧回填隔离测试；不是 Redis 共享授权权威状态 |
| root | IsBuiltIn 由根播种设置，JWT 含 root claim，普通角色名为 root 不会变成根账号；领域拒绝禁用 built-in | [播种处理器](../../src/Services/Identity/NexusStackNext.Identity.Application/IdentityUseCases.cs:484)、[签发器](../../src/Services/Identity/NexusStackNext.Identity.Infrastructure/IdentityTokenAdapters.cs)、[RootAccountSeeder](../../src/Services/Identity/NexusStackNext.Identity.Endpoints/RootAccountSeeder.cs)。没有用户删除路径，不能说删除保护已完整验收；已有同名普通用户时播种仅跳过，不核实其 IsBuiltIn，这也应进生命周期验收 |
| 普通用户授权旅程 | 从菜单/API/角色/用户构造权限链，先拒绝后授权成功；尝试新增菜单被拒绝 | [AuthorizationChainJourneyTests](../../tests/HostIntegration.Tests/AuthorizationChainJourneyTests.cs)。此类测试直打平台宿主，末尾没有尝试 API resource 扩权；[verify-user-journey](../../scripts/verify-user-journey.ps1) 另有真进程+网关路径，但同样不是完整生命周期或独立业务撤权测试 |

此外，`Role.Platforms` 虽存在，当前 Login 没有平台参数、UserPermissionReader 也不按平台过滤。不能把 NS 的“按端分配角色”能力视作已迁移；是否确需多端登录，应由下一真实客户端决定，避免先添加通用租户/组织/终端体系。[Role](../../src/Services/Identity/NexusStackNext.Identity.Domain/Roles/Role.cs)、[LoginUseCase](../../src/Services/Identity/NexusStackNext.Identity.Application/LoginUseCase.cs)、[权限读取](../../src/Services/Identity/NexusStackNext.Identity.Application/UserPermissionReader.cs)。

## 4. #51 的确证路径及有限结论

当前 Identity 分组挂 `NexusStackAuthorizationFilter`，但 `POST /api/identity/api-resources` 明确标注 `RequireAuthenticated()`。其注释以初始引导为理由，允许任何登录用户登记。如今 root 播种和 `PermissionKey` 模式的 root 旁路均已存在，因此引导无需依赖这个开放入口。[IdentityModule](../../src/Services/Identity/NexusStackNext.Identity.Endpoints/IdentityModule.cs:240)、[AccessPolicy](../../src/BuildingBlocks/BuildingBlocks.Application/Authorization/AccessPolicy.cs:86)。

源码可达路径如下，未发送请求：

1. 一个启用且持当前会话的普通用户，有 Role→Menu 的现存授权，并知道该 MenuId。合法获授 `GET /api/identity/menus` 的用户可以读取全树及 ID；也可能从正常管理分配得知，ID 不是安全凭据。**未证明一个完全无角色用户能凭空得到菜单授权。**
2. 管理路由 `/api/identity/roles` 的 POST 键尚未登记。普通用户提交该 Path、`POST`、自己的 MenuId；应用只校验路由形状和方法，创建资源并在提交后失效权限缓存。没有“调用者可管理菜单/目录”的附加判定。[创建处理器](../../src/Services/Identity/NexusStackNext.Identity.Application/IdentityUseCases.cs:318)。
3. 下次用户 POST `/api/identity/roles`，端点声明的是 `RequirePermission`，权限读取会把新增资源收进集合，`AccessPolicy` 对匹配键放行。还可针对尚未登记的 `/api/identity/users/{userId}/roles/{roleId}:POST`、Platform 设置写入键形成类似路径。[端点](../../src/Services/Identity/NexusStackNext.Identity.Endpoints/IdentityModule.cs:190)、[权限读取](../../src/Services/Identity/NexusStackNext.Identity.Application/UserPermissionReader.cs:78)、[Platform 端点](../../src/Services/Platform/NexusStackNext.Platform.Endpoints/PlatformModule.cs:101)。
4. 自带网关的 Identity 前缀路由只要求认证，不含 root 限制，所以网关不会额外截断这一链。[routes.json](../../src/Gateway/NexusStackNext.Gateway/routes.json:49)。

范围必须说准：PostgreSQL 对 `(RoutePattern,HttpMethod)` 有唯一索引，已占用目标键会阻止重复登记；内存适配器没有相同约束，因此只跑内存复现可能夸大生产范围。`RoutePattern` 和权限键都规范化大小写/尾斜杠，不应把这些形式差异当作已证明的唯一键绕过。该路径既非匿名攻击，也不修改 IsBuiltIn/root claim，不能直接绕过 Costing/Pricing 和 Gateway 的真正 root claim 策略。[数据库约束](../../src/Services/Identity/NexusStackNext.Identity.Infrastructure/Persistence/IdentityDbContext.cs:234)、[内存资源仓储](../../src/Services/Identity/NexusStackNext.Identity.Infrastructure/InMemoryIdentityStores.cs)、[RoutePattern](../../src/Services/Identity/NexusStackNext.Identity.Domain/ValueObjects/MenuPath.cs:111)。

#51 已采用的窄修复方向：资源定义要求显式的 `POST /api/identity/api-resources` 管理权限；root 可在空库引导，并可明确委派资源管理员。维持现有权限模型，不顺便重做用户管理。至少覆盖：有已授权菜单的普通用户攻击前后均 403、资源和权限无残留；匿名 401；root 初始登记；显式委派管理员成功；真实网关+Postgres+重启拒绝；Int64/响应和已授权管理路径不回退。完整标准见 [#51](https://github.com/yongpengW/NexusStackNext/issues/51)。

## 5. JWT 的撤权边界

| 入口 | 本地 JWT 验证 | 当前安全状态查询 | 现在可作的结论 |
|---|---|---|---|
| 平台模块的受保护端点 | 平台宿主验证签名、issuer、audience、有效期 | 过滤器调用 ISessionValidator，读 User.IsEnabled / SessionVersion；普通权限再走权限缓存 | 已登出/禁用用户的下一次请求被拒；root 也先过会话检查。数据库不可用不放行；现有设置故障测试期望 500，不要写成已有稳定 503 |
| Costing | HS256 白名单、签名、issuer、audience、有效期，30 秒 skew | 无；仅 `costing-operator` 要求 root claim | 在本机代码条件下，登出不会使该 JWT 立即失效；没有普通业务操作者路径 |
| Pricing | 同 Costing | 无；仅 `pricing-operator` 要求 root claim | 同上，Pricing Redis 不是授权缓存也不会解决此问题 |
| Gateway 转发路由 | JWT 验证，声明认证的路由要求已认证 | 无；授权责任在后端 | 平台后端会检查会话，业务后端目前不会；经过网关不是撤权保证 |
| Gateway 自身路由管理 | JWT 验证 + root claim | 无 | 旧 root JWT 登出后仍可能调用网关自有管理端点，直到到期/验签拒绝 |

依据：[平台过滤器](../../src/BuildingBlocks/BuildingBlocks.Web/NexusStackAuthorizationFilter.cs:64)、[权威会话读取](../../src/Services/Identity/NexusStackNext.Identity.Application/SessionUseCases.cs)、[平台宿主](../../src/Hosts/NexusStackNext.PlatformHost/Program.cs:107)、[Costing 宿主](../../src/Hosts/NexusStackNext.CostingHost/Program.cs:46)、[Pricing 宿主](../../src/Hosts/NexusStackNext.PricingHost/Program.cs:46)、[网关验证](../../src/Gateway/NexusStackNext.Gateway/Program.cs:113)、[网关管理策略](../../src/Gateway/NexusStackNext.Gateway/GatewayRouteAdmin.cs:83)、[设置故障测试](../../tests/HostIntegration.Tests/PlatformSettingsAccessTests.cs:41)。这是代码推导，未实测各服务的登出后窗口。

官方边界：JWT bearer 的基础验证包含签名、发行方、受众和过期时间，不会自动读取 NSN 用户表的禁用或会话版本。[Microsoft JWT bearer 文档](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-10.0)。RFC 7009 第 3 节区分自包含令牌与服务器查询型令牌，立即撤销自包含令牌需要额外后台交互，短有效期只是限制窗口。[RFC 7009](https://www.rfc-editor.org/rfc/rfc7009.html#section-3)。

RFC 7662 描述受保护资源向授权服务器查询当前令牌状态，并要求保护查询入口；其第 4 节明确指出缓存查询结果会产生已撤销令牌仍被使用的窗口。NSN 当前自定义 JWT/SessionVersion 协议**不是已经实现了 RFC 7662**，这里引用的是状态查询与新鲜度的设计边界。[RFC 7662](https://www.rfc-editor.org/rfc/rfc7662.html#section-4)。

## 6. 建议的生命周期与授权切片

以下是后续建议，不是新建票据或已经接受的具体 API。顺序保持 #51 → #48 → #50，再进入这里的两条 Identity 竖切；Redis 切片可在它们之后独立交付。

### 6.1 竖切 A：账户状态/凭据变更到所有在线入口撤销

用“普通用户登录 → 管理禁用 → 平台/Costing/Pricing 拒绝 → 重启 → 启用后必须重新登录”和“本人改密 → 两类旧令牌失效”证明完整能力。

- 明确管理读面：按 ID 的受限用户信息和有界列表，仅返回管理需要的字段、Version、启用状态；不返回 hash、令牌、秘密。禁用/启用和管理重置用预期 Version；自助改密的主体从认证身份取，校验旧口令，不能由请求体选任意 UserId。公共自注册是否保留仍是独立产品决定，不因本票暗改。
- User 的禁用、口令变化应在**同一聚合变更**中推进 SessionVersion；启用不恢复旧版本。相同启停操作不增加 Version，失败口令/提交失败不留下半完成状态。可以在领域内部一次改变多个字段后只做一次 `Changed()`，不要处理器串两个会各自 Bump 的方法后假定一次状态变化只加一。
- 口令相同不能靠比较两次加随机盐后的 hash 识别；应用先用 IPasswordHasher.Verify 比对旧 hash，再生成新 hash。保留领域不接触明文的边界。[现有 Hash 实现](../../src/Services/Identity/NexusStackNext.Identity.Infrastructure/Pbkdf2PasswordHasher.cs)。管理员重置应要求新凭据或后续单次重置流程，不能恢复 NS 的可推测默认口令。
- root 可自助轮换并使旧会话失效；不得禁用或删除 built-in，不允许普通资料/角色修改入口更改 IsBuiltIn。播种遇同名非 built-in 应明确失败，不能默默宣称已有可用根管理员；重启不得覆盖管理员已经轮换的口令。
- 已接受任务属于业务服务的持久执行意图，不随提交者 JWT 到期或登出丢失；后台使用服务自身权限执行，不持久化/转发用户 JWT。新的人为提交、查询、重试、取消以及最终私有文件下载必须重新授权。已接收任务是否另需撤销，由业务取消命令定义，不能把会话撤权说成任务取消。

跨进程的缝是“**谁提供当前会话和操作许可结论**”，不是“哪个服务能直接查询 Identity 表”。建议 Identity.Contracts 暴露窄、带版本的当前访问判定契约，两个业务宿主使用 HTTP 适配器，平台内可由本地适配器回答；业务端只提交自己在代码中声明的操作键，不相信客户端传来的 allow/root/用户 ID。

可以先做一个当前用户自己的访问判定 HTTP：继续携带原请求的 Bearer token，经 Identity 自己验签、取 sub/session，再读取权威状态，返回有限判定；它不是允许来者查询任意人的 `/authorize(UserId,...)` 管理接口。固定目标地址、正常 TLS 校验、有限超时/并发，令牌不放 URL/日志；不得从请求体给定回调地址。若将来需要机器查询任意主体，则另定义受认证的服务身份与允许查询的范围，不把内部网络等价于授权。

实现可复用 ASP.NET Core 的 policy/requirement/handler 或现有过滤器，让授权与执行有明确边界；不需要自造策略语言。[官方 policy API](https://learn.microsoft.com/en-us/aspnet/core/security/authorization/policies?view=aspnetcore-10.0)、[IHttpClientFactory](https://learn.microsoft.com/en-us/dotnet/core/extensions/httpclient-factory)。同一请求内共享一次判定结果可以减少重复网络调用，但不跨请求缓存“允许”。依赖失败必须拒绝执行并返回可辨识的服务不可用，不能把旧 allow 值当降级。

验收最少包括：同 JWT 在三个宿主及网关管理的撤销前后对照；缺失/旧 session、错 issuer/audience/签名、禁用、重新启用、改密、刷新重放、重启；Identity 不可用/响应超时/错误响应均不执行业务写；root 同样检查会话；失败提交不撤销成功的新会话。真实网关和独立进程验证，不能只使用伪造 ICurrentUser。

承诺应是“撤销成功返回后**新开始授权的请求**拒绝”。已经完成授权、正在执行的并发请求仍可能提交；没有分布式事务或业务栅栏时不能声称撤销瞬间回滚所有在途操作。会话查询和权限查询应在同一权威请求中给出清楚的一致性边界；这是下一切片需设计和用并发测试确认的地方。

### 6.2 竖切 B：普通成本/定价操作者与完整授权变更

保留 `User.RoleIds`、`Role.GrantedMenuIds` 和路由权限键的既有语言；PoS 的操作代码说明操作应可独立授予，不强迫 NSN 同时维护一套菜单码 ACL。先将业务端点按读、提交、重试/取消、交付管理、批次、导出拆明许可，不把 Costing 用户自动视作 Pricing 管理员。

- 用户角色撤销、角色授权集合替换、角色启停/删除保护、API 资源映射修改/停用应有预期版本和真实 HTTP 回路。管理权限可委派，但普通业务权限不能修改授权定义；#51 的防扩权回归继续保留。
- 授角色前核实角色存在且可用，授菜单/绑定资源前核实菜单存在。跨聚合只读是允许的，跨聚合一锅修改不允许。并发删除造成的悬空引用，应由权威读取忽略不可用项并拒绝，后续清理由事件处理，不能只靠检查时存在。
- **菜单展示修改与撤权分开。** 当前权限读取根本不读取 MenuTree，所以只接 `MenuTree.Remove` 再清缓存仍会重新算出旧权限。建议删除叶节点后，权威权限投影明确排除不存在的菜单；Grant/API 绑定也验证存在。历史角色/API 引用可以异步清理，不应为了删除一个节点把所有用户和角色放进同一事务。移动/改标题并不自动撤销操作权，除非产品另有明确语义。[MenuTree.Remove](../../src/Services/Identity/NexusStackNext.Identity.Domain/Menus/MenuTree.cs:236)、[权限读取](../../src/Services/Identity/NexusStackNext.Identity.Application/UserPermissionReader.cs)。
- 对需立即撤权的远端判定，初版可直接从 Identity 权威仓储计算，不用 Redis 或跨实例 L1 缓存作为最终事实。现有单进程权限缓存可以保留到它的既定范围，但不能让远端授权兜回一个未证明能跨实例立即失效的缓存。性能需求出现后再评估持久权限版本或一致快照优化。
- 业务拥有对象级规则：文件归属、批次所有者、成本对象的可见范围由对象所属上下文判定；全局操作许可不等于可以读任意人的私有导出。ASP.NET Core 的资源授权支持在载入对象后做判定，不能只靠路由上的角色标记。[官方资源授权](https://learn.microsoft.com/en-us/aspnet/core/security/authorization/resource-based?view=aspnetcore-10.0)。

验收以普通用户现有 JWT 为主：无权 403 → 授读仍不可写 → 授提交可登记任务 → 撤销用户角色/Role grant/停用角色/API 映射变化分别在下一次新授权拒绝 → 原 JWT 无需重新登录即可观察授权增减；被撤角色不删除其任务/历史。加入热缓存、被挂住的旧读取、失败提交、进程退出后恢复；验证对象所有权和匿名/root 边界；新增用例仍一聚合一事务、无跨上下文 SQL。

角色停用、API 停用等领域状态目前尚无实现，必须先写具体语义和迁移，而不是把这份建议误当现有模型。用户物理删除涉及已有任务/审计中的 ActorId，建议先满足禁用闭环，再明确可追溯的删除/脱敏政策；不能用级联删除业务上下文记录来“补齐 CRUD”。

## 7. 第二个 Redis 消费者的选择

### 7.1 现有 Pricing 的边界值得保留

Pricing 的 Redis 缓存只用于普通 PriceQuoteView 查询。状态改变时同一数据库事务登记失效意图，后台删除键后确认；随机填充令牌防迟到回填；固定 TTL、无本机 L1；Redis 故障走有并发/时间预算的数据库回源；命令、任务执行、权限均不靠 Redis。[Pricing ADR](../../src/Services/Pricing/docs/adr/0002-durable-query-cache-invalidation.md)、[PricingRedisCache](../../src/Services/Pricing/NexusStackNext.Pricing.Infrastructure/PricingRedisCache.cs)、[查询预算](../../src/Services/Pricing/NexusStackNext.Pricing.Infrastructure/PricingQuoteQueries.cs)。

[PricingCacheTests](../../tests/Pricing.IntegrationTests/PricingCacheTests.cs) 已包含跨进程共享、Redis 断线后进程重启重放失效、迟到旧回填、淘汰重建、受限降级这些测试代码。Redis 最终一致性不能升级为全局权限权威性：Redis Pub/Sub 断线期间消息会丢失，默认异步复制也不等于强一致；Lua 的原子性只覆盖 Redis 内执行，不让 PostgreSQL 和 Redis 获得一个事务。[Redis keyspace notifications](https://redis.io/docs/latest/develop/pubsub/keyspace-notifications/)、[Redis replication](https://redis.io/docs/latest/manual/replication/)、[Redis Lua](https://redis.io/docs/latest/develop/programmability/eval-intro/)。

### 7.2 候选比较

| 候选 | 已证读取价值 | 一致性与降级边界 | 建议 |
|---|---|---|---|
| **Costing 的 CostSheetView 普通展示查询** | 已有 `GET /api/costing/items/{itemId}`、GetCostSheet 和独立 Postgres；返回版本、输入及已计算结果 | 展示允许固定 TTL 内的旧快照；提交/任务/事件仍读主库。Redis 不可用有界回源，预算耗尽 503；不缓存任务状态或执行权 | **优先第二消费者**。需先对现有读后写测试和客户端约定明确 eventual 与 strong 的边界 |
| Platform 任意设置值缓存 | NS/PoS 确有 Email、SMS、日志归档配置读取；NSN 当前 SettingStore 消费仅在 Platform 模块，尚无独立业务配置客户端 | 设置可能包含秘密或控制开关，不能默认所有 key 均可旧读；设置编辑页写后立即读应仍权威，不把凭据复制到通用缓存 | 暂不首选。若真实出现只读、非秘密且允许延迟的展示元数据，再做明确允许键/类型的查询适配器 |
| 外部 access token 缓存 | PoS WechatProgramsService 按 AppId 缓存 token；miss 回源、按 expires_in 留余量 | 供应商是否使旧 token 失效、刷新限流/多调用幂等、失败是否允许继续用旧 token 都是供应商事实；缓存值本身敏感 | 随受控外部调用竖切做。供应商未明确时只能证明本地受控适配器，不能把通用 GetOrSet 包一层就声称接好 |
| 用户权限/会话缓存 | 旧 NS/PoS 确有热路径需求 | 旧 allow、禁用状态恢复、迟到回填直接改变安全；不能降级为“最后一次有效权限” | 不作为普通最终一致查询缓存的第二消费者；按第 6 节的权威判定处理 |
| Region 元数据缓存 | NS/PoS 有 Region 服务，但 PoS 区域还参与组织/店铺可见范围；NSN 当前只有语言条目 | 纯展示区域字典可缓存，授权范围不可复用同一个旧值策略；数据导入源/更新频率未确立 | 等业务样板真消费后再实现，不能为满足两个消费者而空造目录 |

依据：[Costing 请求与视图](../../src/Services/Costing/NexusStackNext.Costing.Application/CostingRequests.cs:71)、[GetCostSheet](../../src/Services/Costing/NexusStackNext.Costing.Infrastructure/CostingServices.cs:113)、[Costing 端点](../../src/Services/Costing/NexusStackNext.Costing.Endpoints/CostingModule.cs:87)、[SettingStore](../../src/Services/Platform/NexusStackNext.Platform.Application/SettingStore.cs)、[PoS WeChat 读取路径](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/Wechat/WechatProgramsService.cs:51)、[PoS SMTP](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/EventAlert/SMTPEmailService.cs)、[PoS 归档设置](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/Archive/OperationLogArchiveService.cs)、[Platform 语言](../../src/Services/Platform/CONTEXT.md)。

这一选择修正了前序 [能力路线](2026-10-02-ns-pos-capability-parity.md) 中“Platform 元数据或外部令牌可作为下一候选”的笼统建议：二者仍有价值，但进一步读代码后，**已存在的 Costing 查询语义更具体**。这不是已测得的性能瓶颈，也不代表所有成本读取应改成缓存。

### 7.3 建议的缓存切片与验收

- 先明确接口：当前 GET 成本对象同时携带 Version，若调用者用于编辑和条件写，需要可请求权威读取，或保留原 GET 权威语义并新增明确的展示查询。不要静默削弱“写后马上读到新版本”的已有契约。最终一致展示响应继续返回真实快照 Version；过期版本被命令的 ExpectedVersion 拒绝，不能绕过 CAS。
- Costing 自己持有缓存 key、序列化视图和失效记录；保存输入、完成计算等所有改变快照路径同事务登记失效。到时由两个上下文的真实实现证明共性，再提取连接、固定 TTL、填充令牌 Lua、有界负载等窄机制；不共享 DbContext、实体或跨库失效表，不把领域序列化为通用 object。
- 不新增覆盖 Redis 所有命令的万能 `IRedisService`。接口只描述本轮证明需要的快照读取/取得填充权、受条件保护的填充、失效。StackExchange.Redis 文档要求复用 ConnectionMultiplexer，不应每次请求新建连接。[官方 Basic Usage](https://seredis.dev/Basics)。相同 Redis 实例可复用连接机制，命名空间必须继续区分部署/数据库世代/上下文/格式版本。
- 两个独立进程验证共享缓存命中；每个上下文的表不可被另一个直接读取；Redis 断开、超时、淘汰、重启、删除失败、提交后进程退出都不能丢失权威数据或业务任务。迟到 Fill 在失效/淘汰后不能复活旧快照；回源数量/时间有硬上限；不能用 mock Redis 证明 Lua 行为。
- 授权必须先于数据缓存命中。即使已经缓存整份成本/定价视图，撤权用户也不能读；缓存 outage 不能转成匿名访问。强读路径和任务/命令完全不使用缓存，真实故障测试验证其结果不受缓存旧值影响。
- 固定 TTL 和持久失效的传播延迟须在文档显式标明；Redis 故障切换可能回退已确认缓存操作，因此 TTL 是收敛边界，不能声称零旧值。多机 HA/容量结论留到最后一轮演练。

## 8. 仍需实现时解决的未知

- 没有核验生产账号、权限、菜单目录、调用频率或 Redis 拓扑，不能断言 #51 已被利用，不能断言真实缓存性能收益。
- “立即撤权”的精确请求/事务边界、Identity 查询失败的稳定错误码、业务入口的操作粒度，需要写进下一票验收；不可用测试验证关闭访问，不能将系统整体高可用等价于 fail-closed。
- 生命周期全量 CRUD 还涉及有界查询、角色/API停用、删除历史、资料修改、权限目录重复和失效范围。应分上述真实旅程处理，不先造任意实体通用管理框架。
- 本地 Identity 的 CAPTCHA 目前默认适配器名为 `NoCaptchaValidation`，已有端口不代表生产验证码完成。[LoginUseCase](../../src/Services/Identity/NexusStackNext.Identity.Application/LoginUseCase.cs)。外部 OIDC/MFA、非对称签发和密钥轮换、按设备会话管理不是本研究已交付能力；若下一生产要求包含它们，应另定明确范围，不私自接公司 IdP。
- 所有建议必须继续完成 HTTP/ISender、持久化、真网关/独立进程、Windows/Linux、模板生成和 Standards/Spec 两轴验收。本文只为实施提供证据和缝的位置，不能用于勾选 #34 中尚未完成的能力。
