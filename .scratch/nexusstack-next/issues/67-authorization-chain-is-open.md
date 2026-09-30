# 67 — 授权链的第一环是断的：没有人能拿到权限

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 71

## 现象

**所有受权限保护的端点永远返回 403，因为没有用户能拿到任何权限。**

第 28 轮 review 走真实 HTTP 时发现的。一个自注册用户登录成功后，
调 `GET /api/identity/users/1/permissions` 得到 **403**——不是因为边缘挡住了，
而是因为它**真的一无所有**。

## 证据链

| 环节 | 状态 |
|---|---|
| 创建**菜单** | ❌ **没有用例，也没有端点**。`MenuTree.AddRoot` 在领域层存在，**没有任何地方调用它** |
| 创建 api-resource | ✅ `CreateApiResourceCommand` 有，端点也有 |
| 把 api-resource 挂到菜单 | ❌ 做不到——没有菜单可挂（`MenuId` 是可空参数，但挂不上就没有权限键） |
| 角色授予菜单 | ✅ `GrantMenuToRoleCommand` 有——但没有菜单 ID 可传 |
| **授予内置根账号** | ❌ **没有任何种子/引导逻辑**。`User.Create` 的 `isBuiltIn` 参数永远是 `false` |

Identity 的用例清单（全部）：

```
CreateUser  AssignRole  GetUserPermissions  CreateRole  GrantMenuToRole
CreateApiResource  Authorize  Login  Logout  RefreshToken
```

**没有 `CreateMenu`。**

于是：

- 菜单建不出来 ⇒ api-resource 挂不上 ⇒ 角色授不到菜单 ⇒ **权限集合永远为空**；
- 没有根账号 ⇒ `IsRoot` 那条旁路**不可达**（`AccessPolicy.Decide` 里它是有测试的，但没有真实用户能走上去）。

## 为什么之前没人发现

- **单元测试是绿的**：`AccessPolicy` 的判定、`Role` 的授予/撤销、`UserPermissionReader`
  都对——它们直接构造聚合，绕过了"能不能建出那个聚合"；
- **集成测试是绿的**：它们用 `WebApplicationFactory` 直打宿主，验的是 401/403/404 这类边界，
  **没有一条走到过"授予权限之后 200"**；
- **端到端旅程刚修好**（票据 27 那一轮），而它走到第 3 步就停在 403——那时候我只把它读成
  "新用户本来就没权限"，**没有往下问一句"那权限从哪儿来"**。

> 这正是评审 19 那条教训的另一半：**只走到 403 就停，等于只验了失败路径。**

## 等一个决定：见 [71 决定：根账号从哪儿来，菜单从哪儿来](71-decide-root-account-and-menus.md)

**那两个决定住在 71，不在这里。** 这一票原来把"根账号三条路 / 菜单三条路"抄在自己身上，
于是同一个决定有了两个家——而两个家迟早会不一致。

- 71 是 **HITL 的决定票**（`Type: grilling`）：它记选项、代价，以及**人选了哪一个**；
- 67 是**执行票**：71 一落定，它就开工（所以它标 `ready-for-agent`——**阻塞由图表达，不由标签表达**）。

> 这处拆分同时消掉了一个机器看得见的问题：`needs-info` 在面板上没有对应状态
> （封闭集只有 `resolved|completed|closed|done`），所以标着它的票会显示成 **frontier**——
> 也就是"现在可以开工"。而它其实在等人。现在它带着 `Blocked by: 71`，
> 面板的依赖图把它算成**被阻塞**，那才是事实。

## 不在本票范围内

- 不含前端。
- 不含权限的**缓存失效**策略——那部分（`UserPermissionCache`）已经建好并测过，
  它只是永远拿不到非空的结果。

## 验收标准

- [x] 一条**真实 HTTP** 的旅程：从零开始 → 建出根账号 → 建菜单 → 建 api-resource 并挂上 →
      建角色并授予菜单 → 把角色给一个普通用户 → 该用户登录后调那个受保护端点得到 **200**。
      → `tests/HostIntegration.Tests/AuthorizationChainJourneyTests.cs`（2 条测试，已通过）
- [x] 那条旅程里，普通用户**不能**给自己授权（越权尝试必须 403）。
- [x] `check-tracker` 的检查仍绿。

## Comments

### local

**第 1 轮：断链不是一处，是四处（2026-09-30）**

本票写的现象是"没有根账号 + 没有菜单用例"。真去实现时发现**四处**，每一处都单独足以让链路不通：

| # | 断链 | 修法 | 变异验证 |
|---|---|---|---|
| 1 | **没有菜单持久化端口**：`MenuTree.AddRoot` 写好、有测试，却没有任何调用者 | 新增 `IMenuTreeRepository` + 内存（**单例**）与 EF 两个适配器 + `CreateMenuCommand`/`GetMenusQuery` + 两个端点 | 内存仓储改 Scoped → 旅程**变红**（跨请求读回是空树） |
| 2 | **没有根账号**：`User.Register` 的 `isBuiltIn` 永远是 `false`，`IsRoot` 旁路不可达 | `SeedRootAccountCommand`（幂等：存在则跳过、绝不重置口令）+ `RootAccountSeeder`（宿主启动时读配置播种） | `isBuiltIn: false` → 旅程**变红**；不注册播种 → 旅程**变红** |
| 3 | **令牌里从不签发 `Root` 声明**：`JwtAccessTokenIssuer` 只放 sub/name/jti/会话版本，而 `ClaimsCurrentUser.IsRoot` 读的正是它 | 签发端口加 `isRoot` 参数，`TokenIssuer` 传 `user.IsBuiltIn` | 去掉那个声明 → 旅程**变红** |
| 4 | **`AssignRoleHandler` 改完角色不失效权限缓存**（菜单授权与新增 api-resource 两处都失效了，只有这一处漏了） | 与那两处同一条规则：成功即 `permissions.Invalidate()` | 把失效挂到恒假条件后 → 旅程**变红**，失败信息正是 `Expected: OK / Actual: Forbidden` |

**第 3 处是本票最值得记的一条**：它让"种一个根账号"这件事**单独做也没用**——
权限判定读的是声明，而声明从来没被签发过。三处断链各自都"看起来没问题"：
单元测试直接构造聚合，绕过了"能不能建出那个聚合"；集成测试只验 401/403/404 这类边界，
**没有一条走到过 200**。

**旅程的顺序是刻意的**：普通用户**先被拒一次**（那一次把"空权限集合"写进缓存），
拿到角色之后再调一次才必须变成 200。少了中间那次，第 4 处断链就抓不到——
而"授权之后仍然 403"正是它唯一的外部症状。

**一处实现上的偏离**（记在票 71 里）：配置里放的是**口令**而不是口令哈希。
本仓的 Pbkdf2 带随机盐，人手算不出可复现的哈希；播种时当场哈希，安全性质相同。

**新增的部署义务**：`Identity:Root:UserName` / `Identity:Root:Password` 写进了
`README.md` 的「要求」、`env/README.md` 的一节、以及 `run-host.ps1 -Init` 的骨架——
因为"配置里有一个能进一切的账号"必须有人知道，且必须知道**上线后要轮换它**。

**反向验证：5 处变异全部变红。** 其中一次（删掉缓存失效调用）**编译失败**——
删掉之后 `permissions` 成了未用参数，分析器直接拒绝。那说明分析器也在守这一处，
但守的是"参数别浪费"，不是"缓存要失效"；所以换成"能编译但恒不失效"的等价变异重跑了一次。
**变异的第一步永远是确认它编译通过**——否则你看到的"没红"只是它压根没跑。

### local

**第 2 轮：经**网关**也验了一遍（2026-09-30）**

第 1 轮那条旅程走的是 `WebApplicationFactory` **直打宿主**——按 `AGENTS.md` 的第四条纪律，
"集成测试直打宿主时，你测的不是用户走的那条路"：网关的路由策略被跳过了。
所以这一轮把**同一条链**加进了 `scripts/verify-user-journey.ps1`（两个真进程 + curl，从 5190 打进来）：

```
6a. 根账号经网关登录    → 200（靠启动播种）
6b. 建菜单（根账号）    → 201   menuId=98717453403967488
6c. 建角色（根账号）    → 201
6d. 菜单授给角色        → 204
6e. 登记 api-resource   → 201
6f. 授权前调受保护端点  → 403（这一次把空权限写进缓存）
6g. 把角色给普通用户    → 204
6h. 授权后再调同一个端点 → 200   ← 本票的验收，经边缘
6i. 普通用户想建菜单    → 403   ← 反向的一半
7.  经网关登出          → 204
8.  登出后同一个令牌    → 401
```

**根账号靠进程环境变量注入**（`Identity__Root__*`），没有改 `env/*.dev`——
那三个文件里是活凭据，而这条脚本只需要一个"能从零把链建起来"的宿主。

**两条防线，一贵一贱**（本仓既有的表）：`AuthorizationChainJourneyTests` 每次构建都跑、直打宿主；
这一条要起两个真进程、走边缘，是"用户到底走不走得通"的那个事实。
**它们刻意重复**——不是冗余，是两条不同的路。

**顺带确认了一件事**：新端点 `POST /api/identity/menus` **经得过边缘**。
它落在网关的 `identity-management` 前缀路由下（`requireAuthentication: true`），
所以"根账号能不能从外向里建菜单"这个问题的答案是能——而这件事直打宿主的测试**永远答不了**。
