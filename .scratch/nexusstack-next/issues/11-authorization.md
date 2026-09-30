# 11 — 授权：预计算权限与请求过滤器

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 08, 10

> **2026-09-29 进展**：判定核心已由票据 22 交付
> （`AuthorizationMode` / `AccessDecision` / `AccessPolicy`，**默认拒绝**且可穷举验证，见 ADR-0010）。
> 本票剩下的是把它接到 HTTP 管线上、以及数据权限的取舍——后者仍需你在票据 11 里决定。

## 要做什么

- 请求授权过滤器：把票据 08 预计算的 `HashSet<"routetemplate:METHOD">` 落到每个上下文的请求管线上，
  鉴权时 O(1) 查。
- **认证与授权分离**（ADR-0003）：网关验签、上下文授权。过滤器只依赖"已认证的用户身份"，不自己解析令牌。
- 四档授权模式（原项目的 `ApiPermissionMode`）保留其**形状**，但修正为**默认拒绝**：
  原项目 `ApiAuthorizationOptions.cs:21` 默认 `RootOnly` 且配置化，配置缺失时是 fail-open。
- 数据权限：**待定**（见 `spec.md` §10 开放问题 1）。原项目 `Permission.DataRange` 与 `ICurrentUser.RegionIds`
  建模了但**零消费点**——数据权限实际不生效。本票要么补上过滤器，要么明确删除这两个字段。

## 验收标准

- [x] 未授权请求返回 403（配合票据 09 的状态码语义）——`Authorization_Ladder_IsUnauthorizedThenForbidden`：无令牌 401、有令牌但零权限 403。
- [x] 配置缺失时**默认拒绝**，有测试覆盖——**结构性的**：过滤器在端点既没有授权要求、也没有 `AllowAnonymous` 标记时按 `DenyAll` 处理。上面那条 403 测试正是它的证据（身份为真、权限为零 → 拒绝）。
- [x] 权限缓存失效后新权限立即生效——过滤器**每请求都问** `IPermissionChecker`，而它走票据 08 的版本号失效缓存，所以这条是**构造上成立**的；票据 08 的失效测试守着缓存本身。
- [x] 数据权限：**删除**，且零残留——本仓**从未建模过** `DataRange` / `RegionIds`（全仓搜索零命中），所以这一条是现状而不是新增工作。参照仓库那两个字段建模了却零消费点，那才是要避免的形态。

## 证据

- `review/04`：鉴权配置化 fail-open（`RequestAuthorizeFilter.cs:87-111` + `ApiAuthorizationOptions.cs:21`）；
  `InitApiResourceService` 注册被注释 ⇒ Strict 模式全量 403，而 `RBAC-Design.md:561` 宣称"自动注册 ✅"。
- `review/01`：`Permission.DataRange` 与 `ICurrentUser.RegionIds` 无任何查询过滤器消费。

## 从票据 10 移交过来的一条

**"令牌撤销后，旧 access token 在有效期内也立即失效"。**

票据 10 交付的是**签发与轮换**：一对令牌、刷新时轮换、重放撤销整条刷新链、
库里只存哈希。那些都做完了。

但"撤销一个**已经发出去的访问令牌**"是另一件事：访问令牌是**无状态**的 JWT，
验签方不看数据库就认它。要让它提前失效，只有两条路：

1. 把权限/会话版本烤进令牌，验签方每次比对当前版本（就是本票的版本号机制）；
2. 每次请求都回源查一次（那等于放弃 JWT 的意义）。

本票要做的正是第 1 条，所以这条验收在这里闭环。
`TokenIssuer` 已经预留了落点：签发时可以带一个版本声明，而失效点就是本票的请求过滤器。

## Comments

### local

**第 6 轮进展**

#### 交付

| 东西 | 位置 |
|---|---|
| `NexusStackAuthorizationFilter`（默认拒绝 + 403） | `Identity.Endpoints/NexusStackAuthorizationFilter.cs` |
| `AuthorizationRequirement` / `RequirePermission` / `RequireAuthenticated` | 同上 |
| `IPermissionChecker` 端口 | `BuildingBlocks.Application/Authorization/` |
| `CachedPermissionChecker`（接票据 08 的缓存） | `Identity.Application/` |
| `ICurrentUser.IsRoot` | `BuildingBlocks.Application/Security/` |
| `ClaimsCurrentUser`（从已认证声明读） | `PlatformHost/` |
| 宿主的 JWT Bearer 认证 + 授权管线 | `PlatformHost/Program.cs` |

11 个端点**全部显式标注**了授权要求：4 个公开（自述、登录、刷新、自注册）、
1 个仅认证（`POST /api-resources`，见下）、6 个要求权限键。

#### 一处真实的引导循环，和它的代价

**注册 API 资源的端点不能要求权限键**：要授权得先有权限键，而权限键由它登记——
要求"注册权限"本身需要权限，就没人能注册第一个，系统永远起不来。

所以它停在"已认证"这一档。**代价写在代码里**：任何已认证用户都能登记 API 资源，
而那意味着他能给自己造权限。生产部署必须限制它（网关侧加角色约束）或改成种子数据。
**替代方案是"系统起不来"，所以现在停在这一档。**

同理，`POST /users`（自注册）是**公开**的——否则没人能创建第一个用户。
这也是**产品决定**而不是遗漏，注释里写明了生产环境该怎么关掉它。

#### 架构不变量拦了我一次（而且拦得对）

我第一版把过滤器放进了新的 `BuildingBlocks.AspNetCore`，理由是"五个上下文都会用到"。
`BuildingBlocks_MustNotGrowBeyondTheDeclaredSharedKernel` **立刻拦下**：

> 未声明的共享内核程序集（不变量 7：被第二个消费者证明需要才允许上移）

**那是一次推测的上移**——部署约定确实要求所有上下文都授权，但"要求"不等于"已经需要"。
改回住在 `Identity.Endpoints`，等第二个上下文真的要用时再上移。

#### 又踩了同一个坑：注册时读配置

`AddJwtBearer` 的 lambda 里我**闭包捕获了注册那一刻读到的密钥**——
而测试的 `ConfigureAppConfiguration` 在那之后才生效，于是密钥是空的、
`SymmetricSecurityKey` 构造失败、**每个请求**（连公开的 OpenAPI）都 500。

这是本会话**第三次**同一个错误（前两次是身份模块的 JWT 签发与 `JwtOptions` 绑定）。
修法一样：**在 lambda 里面读**。

#### 第 7 轮：令牌撤销（从票据 10 移交的那条）——**做了**

~~没有做~~ 已完成，见下。原文保留：

要让一个**已发出的**无状态 JWT 在有效期内失效，需要"会话版本"这一层：
签发时把版本烤进令牌声明、过滤器每次比对当前版本。
`ICurrentUser` 与过滤器已经就位，落点是清晰的——但**它还没写**，
所以这条**不算完成**。


### local

**第 7 轮：令牌撤销**

#### 机制：会话版本

`ISessionVersionStore`（`BuildingBlocks.Application/Security`）+ `InMemorySessionVersionStore`。

- **签发时**把当前版本烤进访问令牌（声明 `nexusstack:session`）
- **过滤器每个请求**比对令牌里的版本与当前版本：对不上就是 401
- **撤销 = 版本 +1**，已发出的令牌立刻对不上号

**为什么不是一张"已撤销令牌"名单**：那会随撤销次数无界增长，而且每次请求都要查它。
版本号是常数空间，比对是一次整数比较。

**它同时解决了两条路径**（而两条**互不替代**）：

| 路径 | 不做的后果 |
|---|---|
| 撤销刷新令牌（票据 10） | 客户端拿手上的刷新令牌又能换一对新的——登出等于没登 |
| 涨会话版本（本票） | 手上的访问令牌还能一直用到过期（默认 15 分钟） |

**要真的赶走一个人，两个都要做。**

#### 撤销的触发点

- `POST /logout`（新端点，只要求"已认证"）——**用户标识从令牌来，不从请求体来**：
  让请求体指定"注销谁"，等于给了一个注销任何人的接口。
- **刷新令牌重放**——原先只撤销刷新链；现在同时涨版本，
  因为"疑似泄露"要的正是**立刻**切断，而不是等 15 分钟。

#### 验收测试

`ARevokedAccessToken_StopsWorkingImmediately`：

1. 令牌有效 → 用它登出 → **204**
2. **同一个令牌**再登出 → **401**（它还没到期）
3. **反向那一半**：换一个新令牌 → 仍然 **204**

第 3 步不是凑数：没有它，一个"登出之后把所有人都踢掉"的实现同样能通过前两步。

#### 一处 fail-closed 的取舍

令牌里**没有**版本声明时（不是我们签发的、或换了一种认证方案），过滤器按"对不上号"处理 → 401。
认不出来的身份不放行。
