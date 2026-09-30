# 01 核心与基础设施评审

评审范围（只读，未修改仓库）：

- `Infrastructure/**` — 70 个 `.cs`，3359 行
- `Domain/NexusStack.Core/**` — 182 个 `.cs`，9377 行

---

## 概览

`NexusStack.Core` 名叫 Domain，实际内容是**一个技术性的"应用宿主"库**：认证、过滤器、网关配置、种子数据、SignalR 中转、以及一整套针对"用户 / 角色 / 菜单 / 权限 / 区域 / 文件 / 定时任务 / 审计日志"的系统管理 CRUD 服务。整个模板没有业务领域，这一点在下面反复出现。

体量分布（`Domain/NexusStack.Core`）：

| 目录 | 文件 | 行数 | 实质内容 |
|---|---|---|---|
| `Dtos` | 69 | 1997 | 纯数据载体，占全项目 21% |
| `Services` | 39 | 2504 | 15 个接口 + 若干空实现 + 5 个有真实逻辑的服务 |
| `Entities` | 23 | 1123 | 23 个 POCO，**零行为** |
| `Gateway` | 8 | 680 | YARP 动态代理，独立于领域，质量明显高于其他部分 |
| `Filters` | 5 | 568 | 全局 MVC 过滤器：鉴权、异常、响应包装、操作日志 |
| `SeedData` | 8 | 545 | 硬编码菜单/权限/用户（含真实邮箱） |
| `Schedules` + `HostedServices` | 5 | 477 | Cron 轮询基类 + 种子执行器 |
| `Authentication` | 4 | 264 | 3 个 Scheme Handler，其中 1 个已死 |
| `SignalR`/`EventData`/`EventHandler` | 7 | 289 | 事件 DTO + 2 个 Handler |

`Infrastructure` 则是杂物间：字符串扩展、MD5/RSA 加密、SkiaSharp 验证码、FFmpeg 视频、阿里云 OSS、雪花 ID、类型扫描、Excel/序列化转换器，以及一批 **来自前一个业务系统（连锁门店/零售 ERP）的残留**。

**一句话结论**：这是一份组织良好、注释用心的**单体脚手架**，但它把"领域无关的技术设施"和"系统管理的 CRUD 应用"混在同一个叫 Domain 的项目里，并且用静态服务定位器、进程内事件、单一 `MainContext` 把两者焊死。要拆成 DDD 微服务，需要的是**分层重排而非重构**——但其中约有 5 处设计（见"值得保留"）直接可用。

---

## 发现

### 1. `Domain` 里没有领域：只有 CRUD + DTO + 泛型服务

**证据**

`grep -E 'AggregateRoot|ValueObject|IDomainEvent|BoundedContext'` 在整个仓库 `.cs` 文件中 **0 命中**。唯一出现的 `IRepository`/`Specification` 位于技术库 `Domain\NexusStack.EFCore\Repository\Base\IRepositoryBase.cs:13`、`Domain\NexusStack.EFCore\Repository\Specifications.cs:8`，与领域无关。

实体全部是贫血 POCO，继承 `AuditedEntity`，属性只有 `get; set;`：

- `Domain\NexusStack.Core\Entities\Users\User.cs:12-100` — 104 行，13 个属性 + 3 个集合导航，**没有一个方法**。密码哈希在 `Services\Users\UserService.cs:33-35` 完成，不在实体内部。
- `Domain\NexusStack.Core\Entities\Users\Role.cs:13-77` — 只有 `Name/Code/Platforms/IsSystem/Order` 与 3 个 `virtual List<>`。`IsSystem`（系统内置角色）这个**不变量**没有在实体上守卫，而是散落在 `Services\Users\RoleService.cs:32-35`。
- `Domain\NexusStack.Core\Entities\SystemManagement\Menu.cs:13-111` — 19 个属性，其中 `IdSequences`（`.1.5.9.` 形式的祖先路径，**一个典型的物化路径不变量**）只靠 `Services\SystemManagement\MenuService.cs:38-62` 在 `InsertAsync` 覆写里写字符串拼接维护。任何绕过 `MenuService` 的写入都会破坏它。
- `Domain\NexusStack.Core\Entities\Users\UserToken.cs:14-83` — 同时存在 `LoginMethodType LoginMethodType`（:67）和 `LoginStatus LoginType`（:82）两个语义重叠的字段，后者被命名为 `LoginType` 却装 `LoginStatus`，且 `LoginStatus` 含 `logout` 值（见 `Services\Users\UserTokenService.cs:202` 用它做**登出判断**）。Token 生命周期状态没有建模，靠字段组合表达。

服务层是三层套娃：`IServiceBase<TEntity>`（在 `NexusStack.EFCore`）→ `ServiceBase<TEntity>` → `XxxService`，绝大多数子类**零方法**。9 个服务文件总行数 ≤ 24：

- `Services\OpenAppConfigs\AppConfigService.cs:16-23`（接口空、实现空）
- `Services\OpenAppConfigs\AppEventConfigService.cs:13-18`
- `Services\OpenAppConfigs\AppNotificationConfigService.cs:13-18`
- `Services\OpenAppConfigs\AppWebhookConfigService.cs:13-18`
- `Services\Users\UserDepartmentService.cs:13-20`
- `Services\Schedules\SeedDataTaskCoreService.cs:14-17`

唯一的"领域逻辑"是事务脚本，写在服务里。最厚的一处是 `Services\Users\PermissionService.cs:26-128`（`ChangeRolePermissionAsync`，103 行）：校验菜单跨平台 → 计算缺失父节点 → 补 `DataRange.All` → 开事务 → 批量删 → 批量插 → 提交 → 遍历失效缓存。这是一段**没有名字的业务规则**（"角色授权"），以 103 行线性过程存在。

**影响**

- 无法为"角色授权"、"菜单树不变量"、"Token 生命周期"写单元测试——它们不是对象，只是过程。
- 6 个空服务 + 69 个 DTO + 23 个贫血实体构成约 4400 行（占本区域 34%）的**纯管道代码**，迁移到微服务时几乎 100% 要重写，沉没成本高。
- 实体无行为 → 无领域事件可发 → 只能发技术事件（见发现 8）。

**建议**

重建时按职责切三刀：`NexusStack.Core` 拆成 (a) `Platform.Security`（认证/鉴权/Token）、(b) `Platform.Files`（存储/导出/转码）、(c) `Platform.Scheduling`（Cron/异步任务/MQ 中继）。三者都是**通用支撑域**，不是核心域。真正的业务微服务另起，并首次引入 `AggregateRoot`/`ValueObject`/领域事件。`Menu.IdSequences`、`Role.IsSystem`、`User.PasswordHash+Salt` 这三个不变量应当移入实体方法（`Menu.Move(parent)`、`Role.Rename()`、`User.SetPassword()`），`PermissionService.ChangeRolePermissionAsync` 整体下沉为一个 `RolePermissionAssignment` 聚合上的行为。

---

### 2. 实体构造依赖静态 `App.ServiceProvider`：领域对象无法脱离进程创建

**证据**

```csharp
// Domain\NexusStack.EFCore\Entities\AuditedEntity.cs:10-13
public AuditedEntity() : base(SnowFlake.Instance.NextId()) { }
```

（`EntityBase.cs:11`、`Entity.cs:15` 同样。）

`SnowFlake.Instance` 来自：

```csharp
// Infrastructure\SnowFlake\SnowFlake.cs:17-24
private static readonly Lazy<IdWorker> _instance = new(() =>
{
    var commonOptions = App.Options<CommonOptions>();   // ← 静态服务定位器
    return new IdWorker(commonOptions.WorkerId, commonOptions.DatacenterId);
});
public static IdWorker Instance = _instance.Value;
```

而 `App.Options<T>` 要求 `App.Init(serviceProvider)` 被调用过，否则抛裸 `Exception`：

```csharp
// Infrastructure\App.cs:22-37
public static void Init(IServiceProvider serviceProvider) { ServiceProvider = serviceProvider; }
public static TOptions Options<TOptions>() where TOptions : class, new()
{
    if (ServiceProvider is null) throw new Exception("使用前请先使用 App.Init() 方法初始化");
    using var scope = ServiceProvider.CreateScope();          // ← 每次调用新建并销毁 scope
    return scope.ServiceProvider.GetRequiredService<IOptionsSnapshot<TOptions>>().Value;
}
```

`App.Init` 只在 `Domain\NexusStack.Core\ServiceCollectionExtensions.cs:280`（`UseApp`，**`builder.Build()` 之后**）被调用一次。

**影响**

1. **`new User()` 会触发静态解析** → 任何单元测试、任何控制台工具、任何"在 DI 容器外构造实体"的场景都会抛 `Exception("使用前请先使用 App.Init() 方法初始化")`。这是本区域对 DDD 最致命的一条：聚合根不可独立实例化。
2. **N 个微服务 = N 份冲突的 `WorkerId`**。雪花 ID 的正确性依赖 `WorkerId` 全局唯一，而 `CommonOptions.WorkerId/DatacenterId`（`Infrastructure\Options\CommonOptions.cs:23-28`）来自本地 `appsettings.json`。拆成 10 个服务后，谁保证不撞？没有任何机制——`SnowFlake.cs:15` 的注释只是说"通过静态类只实例化一次否则会有重复"。
3. `App.Options<T>` 每次调用 `CreateScope()` 再 `Dispose()`。它被调用的地方包括**每次异常**（`Filters\ApiAsyncExceptionFilter.cs:74`）和**每次 FFmpeg 路径解析**（`Infrastructure\Video\VideoHelper.cs:95,131`）。高频路径上反复建/毁 scope。
4. 注释说"IOptionsSnapshot 可以获取到最新的配置"（`App.cs:35`），但 `InitHostAndConfig` 显式配置了 **`reloadOnChange: false`**（`ServiceCollectionExtensions.cs:349-350`）。注释与实现相反。

**建议**

重建时 ID 生成必须是**注入的端口**：`public interface IIdGenerator { long NextId(); }`，实现由 DI 以单例提供；实体的 ID 由仓储/工厂在 `Add` 时赋，而不是构造时。`WorkerId` 的来源改为基础设施协作（K8s StatefulSet 序号、Redis `INCR` 租约、或直接改用 UUIDv7 以彻底消除协调）。**彻底删除 `App.cs`**——它是本区域最大的架构负债。

---

### 3. 深模块 vs 浅模块：`IFileStorage` 是一个 12 成员宽的漏斗

**证据**

```csharp
// Infrastructure\FileStroage\IFileStorage.cs
FileStorageType StorageType { get; }                                   // :14
string GetAbsolutePath(string relativePath);                           // :16
Task<byte[]> GetAsync(string key);                                     // :23
Task<Result<string>> UploadAsync(Stream, string, string, bool);        // :34
string GeneratePresignedUri(string key);                               // :42
Task<string> UploadAsync(byte[] bytes, string key);                    // :44
string GetDownloadCenterUri(string key);                               // :46
byte[] GetDownloadCenterFile(string key);                              // :48
bool DoesObjectExist(string fileKey);                                  // :56
void DeleteObject(string fileKey);                                     // :63
byte[] GetFile(string bucket, string key);                             // :72
byte[] GetConfigFile(string key);                                      // :79
Task<Result<string>> UploadConfigAsync(Stream, string, string);        // :88
string GeneratePresignedConfigUri(string key);                         // :95
```

14 个成员，**没有任何两个实现同时支持它们**：

| 成员 | `LocalFileStorage` | `AliyunFileStorage` |
|---|---|---|
| `GetAsync` | ✅ `:51-61` | ❌ `throw new NotImplementedException()` `:134-138` |
| `GetAbsolutePath` | ✅ `:80-90` | ❌ `NotImplementedException` `:145-148` |
| `UploadAsync(Stream,…)` | ❌ `:92-95` | ✅ `:22-60` |
| `DoesObjectExist` | ❌ `:97-100` | ✅ `:69-73` |
| `DeleteObject` | ❌ `:102-105` | ✅ `:75-86` |
| `UploadConfigAsync` | ❌ `:107-110` | ✅ `:192-217` |
| `GeneratePresignedConfigUri` | ❌ `:112-115` | ✅ `:220-225` |
| `GetDownloadCenterUri` / `GetDownloadCenterFile` | ❌ `:122-130` | ✅ `:163-190` |
| `GetConfigFile` | ❌ `:46-49` | ✅ `:111-132` |

`LocalFileStorage.cs:117-120` 甚至显式重复实现了一遍 `IFileStorage.UploadAsync(byte[],string)` 并抛异常——说明编译器已警告成员未实现，是被"补"上去的。

而且 `AliyunFileStorage` 把**三个不同的 OSS 账号**（`UploadCenter` / `ConfigurationCenter` / `DownloadCenter`）藏在同一个类里，靠选择哪个方法隐式决定用哪个账号（`:41`、`:115`、`:152`、`:165`、`:203`）。调用方必须知道"`UploadConfigAsync` 走配置中心账号、`UploadAsync(byte[])` 走下载中心账号"——**这是接口没表达的隐藏规则**。

`Enums\FileEnums.cs:11-19` 声明了 6 种存储类型：

```csharp
Local = 0, Aliyun = 1, Huawei = 2, Tencent = 3, Aws = 4, Oss = 5
```

只有 2 种有实现。`FileStorageFactory.GetStorage(FileStorageType)`（`:19-31`）对另外 4 种抛 `Exception($"暂不支持 …")`。

**影响**

- 接口宽度 ≈ 实现宽度 → **典型的浅模块**。换一个存储后端需要实现 14 个成员、其中一大半会抛异常。这是"接口几乎和实现一样复杂"的定义。
- 调用方无法从类型判断某个操作是否可用 → 生产环境出现 `NotImplementedException` 而不是可诊断的错误。真实可复现路径：`FileService.UploadAsync` 在处理视频时调用 `storage.GetAbsolutePath(relativePath)`（`Services\SystemManagement\FileService.cs:204`）；若 `StorageOptions.Type = Aliyun`（`Options\StorageOptions.cs:31`），则走到 `AliyunFileStorage.cs:145-148`，**上传视频必然崩**。
- `FileService.UploadAsync(byte[], …)` 之所以能工作，是因为它同时用 `GetAsync`（→ 仅 Local）和 `GetAbsolutePath`（→ 仅 Local、部分 Aliyun），实际被**隐式绑定到 Local**。

**建议**

按操作能力拆成小接口（端口）：`IFileReader`（`GetAsync`/`Exists`）、`IFileWriter`（`PutAsync`）、`IUriSigner`（`GeneratePresignedUri`）、`IFileDeleter`。三个 OSS 账号变成三个**具名配置的适配器实例**（`IFileReader` × 3，按名字注入），而不是一个类里的三个代码路径。删除 `IFileStorage` 与 `LocalFileStorage.cs:117-120` 的重复成员。`FileEnums.FileStorageType` 的 4 个幽灵值要么实现要么删掉——它们目前只在错误消息里出现。

---

### 4. `FileStorageFactory.GetStorage()` 返回一个已被释放的 scope 里的实例

**证据**

```csharp
// Infrastructure\FileStroage\FileStorageFactory.cs:19-31
public virtual IFileStorage GetStorage(FileStorageType storageType)
{
    using var scope = scopeFactory.CreateScope();          // ← scope 在方法返回时 Dispose
    var storage = scope.ServiceProvider.GetServices<IFileStorage>()
                       .FirstOrDefault(a => a.StorageType == storageType);
    if (storage == null) throw new Exception($"暂不支持 {Enum.GetName<FileStorageType>(storageType)}");
    return storage;                                        // ← 返回已被 Dispose 的 scope 所拥有的实例
}
```

`IFileStorage` 实现均注册为 `IScopedDependency`（`AliyunFileStorage.cs:18`、`LocalFileStorage.cs:16`），即 **scoped**，其生命周期归属刚被销毁的那个 scope。

**影响**

- 这是"作用域逃逸"（captive dependency 的反向版本）。当前 `LocalFileStorage`/`AliyunFileStorage` 只注入 `IOptionsSnapshot<T>`（scoped）且不持有资源，所以**碰巧**不会立刻炸——但这是一个定时炸弹：任何注入 `MainContext`（`DbContext`，scoped）的存储实现会在第一次使用时抛 `ObjectDisposedException`。
- 更实际的后果：`FileService.UploadAsync` 在视频分支里**先取一次** `storage`（`FileService.cs:132`），然后在 `:204` 用；同一方法里每个分支都独立调 `GetStorage()`。调用方必须在脑中跟踪"我手上这个 storage 变量来自一个已经关掉的 scope"。

**建议**

`IFileStorageFactory` 本身已是 `IScopedDependency`（`FileStorageFactory.cs:11`），完全没必要再建子 scope——直接注入 `IEnumerable<IFileStorage>` 到构造函数（DI 会解析到当前 scope 的实例），或改用 keyed services（.NET 8+ `AddKeyedScoped`）。若确实需要独立生命周期，则应把存储实现注册为 **Singleton** 并让它只依赖 `IOptionsMonitor`，同时用 `IServiceScopeFactory` 在**方法内部**执行完整操作而非返回对象。

---

### 5. `App.Init` 的隐式时序 + 静态初始化 + 反射扫描 = 一组未声明的启动契约

**证据**

启动契约没有任何地方声明，但存在以下**隐式顺序**：

1. `AddBuilderServices` 里 `builder.Services.AddServices<…>()` 触发 `TypeFinders.SearchTypes`（`ServiceCollectionExtensions.cs:151-153`）——纯反射，不碰 `App`。
2. `builder.Build()`（`:64`）——此时**任何 `new SomeEntity()` 都会抛异常**，因为 `App.Init` 尚未调用。
3. `app.UseApp(...)` → `App.Init(app.Services)`（`:280`）。
4. 之后 `ExecuteSeedDataService`（`HostedServices\ExecuteSeedDataService.cs:111`）构造 `new SeedDataTask { … }` → 触发 `SnowFlake.Instance` 静态初始化 → 读 `CommonOptions.WorkerId`。

第 4 步依赖第 3 步先完成。这在单进程里被 `HostedService` 的启动时机侥幸掩盖了。同时：

```csharp
// ServiceCollectionExtensions.cs:340-344
var assemblyFiles = Directory.GetFiles(AppContext.BaseDirectory, "NexusStack.*.dll");
foreach (var assemblyFile in assemblyFiles)
    AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyFile);
```

```csharp
// Infrastructure\TypeFinders\TypeFinders.cs:74
var assemblies = AppDomain.CurrentDomain.GetAssemblies()
    .Where(item => item.FullName.StartsWith("NexusStack.")).ToList();
```

**影响**

- **命名即契约**：任何不以 `NexusStack.` 开头的程序集，其 `ITransientDependency`/`IScopedDependency` 实现、`IOptions` 实现、`IRegister` 映射、`CronScheduleService` 子类**全部静默不被注册**。重建时若把服务命名为 `Sales.Domain`、`Billing.Api`，会得到运行时"服务未注册"而不是编译错误。
- `SearchTypes` 的 `catch` 只 `Console.WriteLine`（`TypeFinders.cs:90,120`），启动日志既没进 Serilog 也不致命 → 静默丢失注册。
- `ConfigureOptions`（`ServiceCollectionExtensions.cs:497-539`）用反射把每个 `IOptions.SectionName` 绑到配置节。`SectionName` 拼错时 `GetSection` 返回空节，绑定出全默认值，`catch` 里同样只 `Console.WriteLine`（`:534`）。**配置缺失不会被发现**。
- `Infrastructure\Options\StorageOptions.cs:16` 的 `SectionName => "Storage"` 是**实例属性**（不是 `const`），`CommonOptions.cs:13` 返回 `""`（表示根节点）——这个"空字符串表示根"的约定没有任何类型保护。

**建议**

重建时用**显式注册 + 编译期可检查**的方式：

- 每个微服务用 `[assembly: …]` 或显式 `services.AddScoped<IMenuService, MenuService>()`（或引入 Scrutor 并显式指定程序集，不靠名字前缀）。
- 用 `services.AddOptions<T>().Bind(config.GetRequiredSection("Storage")).ValidateDataAnnotations().ValidateOnStart()`——**`ValidateOnStart` 是关键**，让配置缺失在启动时炸而不是在第一次请求时给出错误结果。
- 删除 `AppDomain.GetAssemblies()` 扫描与 `LoadFromAssemblyPath` 循环。

---

### 6. 鉴权是隐式约定的两段式：`HttpContext.Items` 是未声明的接口

**证据**

`RequestAuthenticationTokenHandler` 在认证阶段把用户上下文塞进 `HttpContext.Items`：

```csharp
// Authentication\RequestAuthenticationTokenHandler.cs:46-48
var userContext = await userContextCacheService.GetOrSetAsync(userToken.UserId, userToken.PlatformType);
Context.Items[CoreClaimTypes.UserContextItemsKey] = userContext;
```

`RequestAuthorizeFilter` 在授权阶段把它读回来，并且**把它当作硬前置条件**：

```csharp
// Filters\RequestAuthorizeFilter.cs:62-69
// 无论任何权限模式，用户上下文都必须存在
if (!context.HttpContext.Items.TryGetValue(CoreClaimTypes.UserContextItemsKey, out var ctxObj)
    || ctxObj is not UserContextCacheDto userContext)
{
    context.Result = new RequestJsonResult(new RequestResultModel(401, AuthorizationConstants.ErrorMessages.UserContextMissing, null));
    return Task.CompletedTask;
}
```

注意 `:73-80` 的注释「无论任何权限模式，禁用状态都必须拦截」——说明作者清楚这是一条**跨组件的隐式不变量**。

**影响**

- 任何新的认证方案（例如把 `RequestAuthenticationHandler` 的开放 API 认证重新启用，`ServiceCollectionExtensions.cs:173-177` 目前是注释掉的）如果忘了写 `Context.Items[...]`，所有接口会以 `401 UserContextMissing` 失败，而错误信息指向"请重新登录"——**误导排查方向**。
- `CurrentUser`（`CurrentUser.cs:97-109`）同样从这个 `Items` 键读取 `RoleIds`/`RegionIds`，也就是说**服务层读用户角色也依赖 HTTP 上下文**。领域服务无法脱离 HTTP 请求运行。
- `RequestAuthorizeFilter.cs:41-51` 存在一条针对 `"OpenAPIAuthentication"` 的**硬编码字符串分支**；该方案当前未注册，是死分支。

**建议**

把这个隐式契约变成一个**显式类型**：定义一个 `ICurrentUser` 的 scoped 实现，在认证阶段通过 `AuthenticationTicket` 的 `Properties` 携带，或直接在 `ICurrentUser` 上暴露 `Task<UserContext> GetAsync()` 并让它自己负责缓存+回落。删除 `HttpContext.Items` 这个隐式通道，删除 `"OpenAPIAuthentication"` 字符串分支。若确实需要第二认证方案，用 `IAuthorizationHandler` 注册到策略里，而不是在全局 Filter 里 `if` 判断。

---

### 7. 单例 `MainContext` 假设 / 单一数据库假设 —— 以及拆库时会直接断掉的代码

**证据**

所有服务（30+ 个）都注入同一个 `MainContext`：

```csharp
// Services\Users\PermissionService.cs:19
public class PermissionService(MainContext dbContext, …) : ServiceBase<Permission>(dbContext, …) …
```

`Domain\NexusStack.Core\ServiceCollectionExtensions.cs:144` 写死一个数据库：

```csharp
//builder.Services.AddEFCoreAndMySql(builder.Configuration);
builder.Services.AddEFCoreAndPostgreSQL(builder.Configuration);
```

`Domain\NexusStack.EFCore\Repository\ServiceBase.cs:160` 的方法签名直接把 Npgsql 类型暴露到"技术库"的公共 API 上：

```csharp
public async Task<PagedResult<T>> ExecutePagedStoredProcedureAsync<T>(
    int page, int pageSize, string storedProcedure, params NpgsqlParameter[] parameters)
```

**影响**

- 只要 `MainContext` 是 scoped-per-request 的单一上下文，跨服务的数据访问就只能靠**在同一个进程里注入别人家的 DbContext**——这恰恰是微服务拆分时最先断掉的东西。
- `UserContextCacheService.BuildFromDbAsync`（`Services\Users\UserContextCacheService.cs:70-131`）在**一个方法里跨 5 张表**（`User`、`UserRole`、`Role`、`UserDepartment`、`Permission`、`MenuResource`、`ApiResource`）拼出鉴权上下文。这是典型的"一个查询横跨多个未来限界上下文"。
- `PermissionService.ChangeRolePermissionAsync` 的 `dbContext.Set<Permission>()` 直接删除（`PermissionService.cs:102-104`），绕过服务层——跨服务后无法保持。

**建议**

每个微服务独立 `DbContext` + 独立 schema/database。鉴权所需的 `UserContextCacheDto` 应由一个**专门的 Identity 服务**构建并通过 Redis/缓存契约发布，其他服务只消费，不再自己 join 用户表。`ExecutePagedStoredProcedureAsync` 因其 Npgsql 硬绑定应直接从技术库删除（本区域内无调用方）。

---

### 8. 进程内事件 + `CoreServiceType` 分支：WebAPI 模式下操作日志被静默丢弃

**证据**

`UseApp` 按服务类型条件性地挂载事件总线：

```csharp
// ServiceCollectionExtensions.cs:313-324
if (coreServiceType == CoreServiceType.MQService)
{
    app.MapHub<NotificationHub>("/hubs/notification");
    app.AddRabbitMQEventBus();               // ← 只有 MQService 启动消费端
}
else if (coreServiceType == CoreServiceType.Gateway) { app.MapReverseProxy(); }

app.AddRabbitMQCodeManager();
```

而**发布端在所有模式都注册**（`:252 builder.Services.AddRabbitMQ(builder.Configuration);`）。发布点遍布请求管线：

- `Filters\OperationLogActionFilter.cs:85` — `await publisher.PublishAsync(pushData)`，每个被记录的操作都会发一条。
- `Filters\ApiAsyncExceptionFilter.cs:180` — 每个未处理异常发一条。
- `Services\AsyncTasks\AsyncTaskService.cs:54,69,86`。

消费端只有一个 Handler：

```csharp
// EventHandler\OperationLogEventHandler.cs:12-22
public class OperationLogEventHandler(IServiceScopeFactory scopeFactory) : IEventHandler<OperationLogEventData>
```

它由 `AddRabbitMQEventBus()` 挂载 → **只在 `CoreServiceType.MQService` 下运行**。

**影响**

- `CoreServiceType.WebService`（默认值，`Enums\PlatformType.cs:86`）部署时，操作日志与异常日志**发布到队列但无人消费**，最终进死信或堆积。这在开发环境（通常同时跑 MQService）不会暴露，生产只部署 WebAPI 时静默丢日志。
- 这同时说明整个日志链路是**进程外最终一致**的：`OperationLogActionFilter` 在请求线程发布，落库由另一个进程完成。若一次请求成功但日志丢失，没有补偿。
- `CoreServiceType` 这个枚举（`WebService/MQService/PlanTaskService/Gateway`）本身就是**把 4 个可独立部署的服务塞进一个 `Program.cs` 的开关**。`AddBuilderServices` 里 `if/else if` 链（`:169-223`、`:257-268`）决定了注册哪些服务。这是"单进程多角色"的直接证据。

**建议**

微服务化后 **WebAPI 进程自己消费自己的审计事件**（或改为 Outbox 表 + 独立审计服务订阅 CDC）。`CoreServiceType` 枚举应彻底消失，替换为每个服务自己的 `Program.cs` + 独立的 `IEndpointRouteBuilder` 扩展方法。`AddRabbitMQEventBus()` 应无条件在当前进程挂载其 Handler 集合。

---

### 9. Cron 调度：每秒空轮询 + 进程内静态表达式 + 锁 TTL 与任务时长脱钩

**证据**

```csharp
// Schedules\CronScheduleService.cs:90-114
while (!stoppingToken.IsCancellationRequested)
{
    using var scope = serviceFactory.CreateAsyncScope();      // ← 每秒新建一个 scope
    var scheduleTaskService = scope.ServiceProvider.GetRequiredService<IScheduleTaskService>();
    var recordService       = scope.ServiceProvider.GetRequiredService<IServiceBase<ScheduleTaskRecord>>();
    var redisService        = scope.ServiceProvider.GetRequiredService<IRedisService>();
    var logger              = scope.ServiceProvider.GetRequiredService<ILogger<CronScheduleService>>();
    var mapper              = scope.ServiceProvider.GetRequiredService<IMapper>();

    var scheduleTask = await redisService.GetAsync<ScheduleTaskExecuteDto>(…Format(code));
    if (scheduleTask is null || !scheduleTask.IsEnable) { await Task.Delay(1000, stoppingToken); continue; }
    if (DateTimeOffset.UtcNow < nextExcuteTime)         { await Task.Delay(1000, stoppingToken); continue; }
```

每个任务实例每秒创建 5 个 scoped 服务并读一次 Redis。

分布式锁：

```csharp
// :116,136
var lockName = $"ScheduleTask:{code}.{nextExcuteTime}";
if (await redisService.SetAsync(lockName, null, TimeSpan.FromMinutes(1), CSRedis.RedisExistence.Nx))
```

TTL 固定 1 分钟，**从不释放**，也不与 `ProcessAsync` 的实际耗时挂钩。

表达式被进程内静态字段持有并运行时改写：

```csharp
// :187-189
if (!scheduleTask.Expression.IsNullOrEmpty() && scheduleTask.Expression != this.Expression)
    this.Expression = scheduleTask.Expression;        // ← 写回抽象类的实例属性
```

`Expression` 声明为 `protected abstract string Expression { get; set; }`（`:28`）。

**影响**

- **锁 TTL bug**：任务若运行超过 1 分钟，锁自动过期，第二个副本会在同一 `nextExcuteTime`（key 里含 `nextExcuteTime`，所以同一批次内 key 相同）抢到锁并**并发执行同一任务**。对"清理三个月前的操作日志"这类任务影响小，对任何写操作就是数据损坏。
- **N 个任务 = N 个每秒轮询循环**。`AddCronTask` 反射注册所有 `CronScheduleService` 子类为 `IHostedService`（`ServiceCollectionExtensions.cs:562-571`），每个实例一个 `BackgroundService` 一个 while 循环。
- `Expression` 可被运行时替换意味着**同一个类的行为取决于数据库行**，而该属性是抽象成员——`DailySchedule.cs:27` 的默认值 `"0 5 0 * * ?"` 只是初值。排查"为什么任务在 3 点跑"需要同时看代码和 DB。
- `GetNextTime()` 只用 `TimeZoneInfo.Local`（`:42-43`），无时区配置 → 容器 `TZ=UTC` 时所有 cron 语义静默改变。

**建议**

改用 **Quartz.NET 或 `IHostedService` + `PeriodicTimer` 到最近一次执行时刻**（不再每秒轮询）；或直接用 K8s CronJob。分布式锁改为：唯一键带 `TaskCode + 计划执行时间`，`SET NX PX` 且 TTL **大于最坏执行时间**，或引入 fencing token + 心跳续租。`Expression` 必须是**只读的类级常量**；DB 行只存 `IsEnable` 和覆盖用的 `CronOverride`，且启动时校验一次。时区必须是显式配置项。

---

### 10. 种子数据：生产凭据硬编码在源码里

**证据**

```csharp
// SeedData\UserSeedData.cs:28-43
new()
{
    Id = 1,
    Mobile = "13256873823",
    RealName = "Leo Wang",
    UserName = "admin",
    Password = "jrKi7/1uKUMH5GVGmUMKS+xLRjZ+RXYHt3cjWTAYe0k=",
    PasswordSalt = "qc6SX81B0DvBE32FnldeLt4UvMUlOGshbJTSOnUbZ9E=",
    Email = "leowang.interesting@gmail.com",
    …
},
new()
{
    Id = 2029562151177424896,
    Mobile = "17854213431",
    RealName = "王勇彭",
    UserName = "leowang",
    Password = "0LR+hUWEPJDdzDVbe0b1XEWIlyPr9TKvosi5VlXiK40=",
    PasswordSalt = "BKPMh40hR9IXWYI9lSlAu1L9JmLn5TMn2p5x8dAkHXc=",
    Email = "67603960@qq.com",
    …
}
```

**同时** `UserSeedData.ApplyAsync` 每次都强制覆盖密码：

```csharp
// :71-85
exists.Mobile = item.Mobile;
…
exists.Password = item.Password;        // ← 每次启动重置管理员密码为硬编码值
exists.PasswordSalt = item.PasswordSalt;
exists.IsDeleted = false;
await userService.UpdateAsync(exists);
```

`ApplyAsync` 由 `ExecuteSeedDataService` 在启动时调用（`HostedServices\ExecuteSeedDataService.cs:127`），且 `GetExecuteStatus`（`:46-75`）在 `currentConfigPath` 为空时**无条件返回 true**。本区域所有 8 个种子类的 `ConfigPath` 均为 `string.Empty`：

```
SeedData\RegionSeedData.cs:18   public string ConfigPath { get; set; } = string.Empty;
SeedData\RoleSeedData.cs:20     = string.Empty;
SeedData\UserSeedData.cs:19     = string.Empty;
SeedData\MenuSeedData.cs:20     = string.Empty;
SeedData\PermissionSeedData.cs:19 = string.Empty;
SeedData\UserRoleSeedData.cs:19 = string.Empty;
SeedData\UserDepartmentSeedData.cs:19 = string.Empty;
SeedData\ScheduleTaskSeedData.cs:22 = string.Empty;
```

`ISeedData.ConfigPath` 的文档（`ISeedData.cs:19-21`）说"如果初始化数据是一个文件则要进行设置，如果不需要文件则设置为 null"——实现用的是 `string.Empty`，而 `GetExecuteStatus` 判断的是 `string.IsNullOrEmpty(currentConfigPath)` → **所有 8 个种子任务每次启动都会重新执行**，即**每次启动把 admin 密码重置为那个固定的哈希**。

**影响**

- 这是**真实的生产环境接管漏洞**：任何读得到本仓库的人都能算出/直接使用 `admin` 的固定凭据；且运维改了密码后，下一次重启会被重置。
- 硬编码雪花 ID（`2029562151177424896` 等）散落在 `MenuSeedData.cs:65-99`（35 个菜单 ID）、`PermissionSeedData.cs:30-41`、`RoleSeedData.cs`、`UserRoleSeedData.cs`、`UserDepartmentSeedData.cs`。这些 ID 编码了**创建时刻的时间戳**，是原始开发者本机生成后粘贴的。重建时它们与新 `WorkerId`/时间无关，但作为外键被其他种子引用，形成一张隐式的 ID 关系网。
- 真实姓名、手机号、邮箱是**个人信息**，进入版本库。

**建议**

- 种子账号改为环境变量/密钥管理注入，或首次启动生成随机密码并仅打印一次。
- 种子数据必须有**幂等版本标记**（`ISeedData.Version`），且 `ConfigPath` 语义要么实现要么删掉这个属性。
- 种子数据从"代码里的 C# 对象列表"改为**版本化的 SQL/JSON 迁移脚本**（与 EF Core Migrations 同源），这样 ID 与结构变更一起演进。
- 菜单/权限的固定 ID 改为按 `Code` 幂等 upsert，不再依赖硬编码雪花值。

---

### 11. `RequestAsyncResultFilter` 与 `ApiAsyncExceptionFilter`：响应契约被两处重复定义

**证据**

同一套"统一响应"逻辑写在两个地方：

```csharp
// Filters\RequestAsyncResultFilter.cs:68-111  — 成功路径走 JSON 包装
else if (context.Result is ObjectResult result)
{
    if (result.Value is null) { … Message = "未请求到数据" … }
    else if (result.Value is IPagedList pagedList) { … new RequestPagedResultModel … }
    else { … new RequestResultModel { Success = code == 200, … } }
}
```

```csharp
// Filters\ApiAsyncExceptionFilter.cs:45-79  — 失败路径自己拼 RequestResultModel
var resultModel = new RequestResultModel();
if (exception is ErrorCodeException e)      resultModel.Code = e.ErrorCode;
else if (exception is ForbiddenException)   resultModel.Code = 403;
else if (exception is UnauthorizedException)resultModel.Code = 401;
else                                        resultModel.Code = 500;
resultModel.Message = exception.Message;
…
if (App.Options<CommonOptions>().ShowStackTrace) resultModel.Data = exception.StackTrace;
context.Result = new RequestJsonResult(resultModel);
```

第三处：`Middlewares\ExceptionHandlerMiddleware.cs:22-40` 只用 `RequestResultModel` 处理 `AuthenticationFailureException`，**其他异常只 `logger.LogError` 然后吞掉**：

```csharp
catch (Exception ex)
{
    if (ex is AuthenticationFailureException authEx) { … WriteAsJsonAsync(result); }
    logger.LogError(ex, ex.Message);        // ← 没有 else 分支，不写响应
}
```

**影响**

- 三个位置定义同一契约 → 加一个字段要改三处。
- **`ExceptionHandlerMiddleware` 吞异常不写响应**：MVC 过滤器**覆盖不到**的异常（SignalR Hub 内、`MapReverseProxy` 内、`UseStaticFiles` 内、认证 Handler 内抛出的非 `AuthenticationFailureException`）会得到一个状态码正常但 body 为空的响应，客户端无法区分"成功但无数据"和"服务端崩了"。
- 异常信息**原样返回给客户端**（`:64 resultModel.Message = exception.Message`），并追加所有内部异常消息（`:67-71 GetAllInnerExceptionMessages`）。EF Core 的 `DbUpdateException` 消息包含**表名、列名、约束名**；连接失败消息包含**主机名与端口**。这是一个信息泄露面。
- `ShowStackTrace` 关闭时 `Data` 为 `null`，配合 `:74` 的 `App.Options<>()` 静态解析——异常路径上多一次 scope 创建 + 配置解析。
- `RequestAsyncResultFilter` 注入了两个**完全未使用**的依赖：`IOperationLogService operationLogService, ICurrentUser currentUser`（`:20`）——每次请求多解析两个服务。
- `RequestResultModel.Timestamp` 用属性初始化器（`Models\RequestResultModel.cs:45`），构造即固定，序列化时正确；但 `Result<T>.Message`（`Models\Result.cs:24`）与 `RequestResultModel.Message`（`:35`）都是**非可空声明却无初始化**（`Nullable` 已启用），实际可为 null。

**建议**

响应包装收敛为**一个** `IActionResult` 实现或 `System.Text.Json` 的 `JsonConverter`，异常只在一处（`IExceptionHandler`，.NET 8+）处理。用户可见消息必须来自**已声明的错误码表**，绝不透传 `exception.Message`；内部异常只进日志。`ExceptionHandlerMiddleware` 必须有兜底 `else` 分支写 500 + 通用消息。删除 `RequestAsyncResultFilter` 的未使用依赖。

---

### 12. `PermissionService` 的双重循环与空列表异常

**证据**

```csharp
// Services\Users\PermissionService.cs:194-228
public async Task<List<PermissionDto>> GetRolePermissionAsync(List<long> roleIds, PlatformType? platformType, bool isRoot)
{
    var roles = await roleService.GetListAsync(x => roleIds.Contains(x.Id));
    if (roles.Count == 0)
    {
        throw new BusinessException("当前角色不存在");     // :199 ← 这是"正常"场景
    }
    …
    foreach (var role in roles)                            // :204 ← 每个角色一次查询
    {
        var query = from m in menuService.GetQueryable().Where(...)
                    join p in GetQueryable().Where(a => a.RoleId == role.Id) … ;
        permissions.AddRange(await query.ToListAsync());   // :224 ← N 次往返
    }
```

调用方：

```csharp
// :230-240
public async Task<List<MenuTreeDto>> GetUserMenuTreeListAsync(ICurrentUser currentUser, PlatformType platformType)
{
    var roleIds = currentUser.RoleIds.ToList();
    if (!roleIds.Any()) { return new List<MenuTreeDto>(); }   // ← 这里已处理空列表
    var permissions = await GetRolePermissionAsync(roleIds, platformType, currentUser.IsRoot);
```

**影响**

- **N+1 查询**：一个拥有 R 个角色的用户，登录后拉菜单要发 R 次菜单全表 join 查询。R=5 时是 5 次大查询，然后再在内存里做**递归 O(N²) 过滤**（`:255-271` 的 `getChildren` 对每一层都 `.Where(a => a.ParentId == parentId)` 遍历整个 `menus`）。
- `:196-200` 的 `roles.Count == 0` 抛异常与 `:235-238` 的空列表返回空树**互相矛盾**。一个 token 里 `RoleIds` 非空但角色已被删除/禁用的用户，走 `GetRolePermissionAsync(roleId, …)` 单角色重载（`:135-141`）会拿到 `BusinessException("当前角色不存在")` → 500，而走列表重载会拿到 403。同一语义两种结果。
- `GetRolePermissionAsync(long roleId, …)`（`:135`）与 `GetRolePermissionAsync(List<long>, …)`（`:194`）**返回语义不同**：前者构建完整树（`Children`/`Operations`），后者返回**扁平列表**。同名方法返回不同形状，调用方必须读实现才知道。
- `:242` 之后 `menuIds` 转到 `GetListAsync(spec)`，`spec.Query.Where(a => a.IsVisible && menuIds.Contains(a.Id))`（`:251`）——若 `menuIds` 有数千元素，生成巨大 `IN (...)`。

**建议**

一次查询取回：`roleIds` + `platformType` 直接 join 出 `MenuId` 集合，再**一次**取菜单，树在内存构建一次（用 `ILookup<long, Menu>` 按 `ParentId` 分组，把 O(N²) 降到 O(N)）。空角色应返回空权限集合，不抛异常。合并两个重载为一个返回树的 `Task<MenuTree>`，扁平列表场景由调用方从树上取。

---

### 13. `IFileStorage` 之外的浅模块清单

以下模块的**接口复杂度 ≈ 实现复杂度**，或实现是纯转发：

| 模块 | 证据 | 说明 |
|---|---|---|
| `IAppConfigService` / `IAppEventConfigService` / `IAppNotificationConfigService` / `IAppWebhookConfigService` | `Services\OpenAppConfigs\AppConfigService.cs:16-23` 等 4 个文件 | 接口体为空，实现体为空，各自 19–24 行 |
| `IUserDepartmentService` | `Services\Users\UserDepartmentService.cs:13-20` | 同上，且接口定义在同文件而非 `Services\Interfaces\`（其它接口都在那），目录约定被破坏 |
| `ISeedDataTaskCoreService` | `Services\Schedules\SeedDataTaskCoreService.cs:14-17` | 同上，18 行 |
| `IDownloadService` | `Services\SystemManagement\DownloadService.cs:25-35`，`InitExportTypeMap` 返回 `[ // To Do ]` | 导出类型表**为空数组** → `GetExportTypeMap`（`:38-44`）对**任何** `typeName` 抛 `ArgumentException`。整个 DownloadService 不可用 |
| `IGlobalSettingService` | `Services\SystemManagement\GlobalSettingService.cs:73-103` | 4 个方法返回 `null`，真实实现被注释（`:75-83`、`:90-92`、`:98-101`）。`GlobalSettingController` 暴露它们 → 经 `RequestAsyncResultFilter.cs:70-79` 变成 `Success=false, "未请求到数据"` |
| `IProxyConfigStore` | `Gateway\IProxyConfigStore.cs:9-16`，实现 `JsonProxyConfigStore.cs` | 接口 8 个成员中 `GetRouteAsync`/`GetClusterAsync`（`:202-212`）是 `GetConfigAsync()` + LINQ `FirstOrDefault` 的**单行转发** |
| `EnumService` | `Services\SystemManagement\EnumService.cs:12-27` | `arrDesc[0].Description` 在枚举成员**无 `Description` 特性时抛 `IndexOutOfRangeException`**（如 `Enums\GlobalSettingKey.cs:7-11` 的 `SMS`/`Email` 就没有） |
| `IContentTypeProvider` → `FileExtensionContentTypeProvider` | `ServiceCollectionExtensions.cs:255` | 直接注册 BCL 类型为服务，无适配层 |

**影响**

空接口 + 空实现构成的"服务"没有任何封装价值，只是给 `IServiceBase<T>` 加了个名字。它们会**稀释代码库信号**——用 N 个文件表达"这里什么都没有"。

**建议**

删除 6 个空服务接口，控制器直接注入 `IServiceBase<AppConfig>`。`DownloadService` 若确实要用，导出类型表应由 DI 收集 `IExportHandler` 实现（而不是一个空的内联数组）。`GlobalSettingService` 的 4 个 `return null` 必须在重建前决定是删除还是实现——**半实现的公开接口比缺失更危险**。

---

### 14. 安全面：登录无防爆破、验证码非密码学随机、Refresh Token 明文且非原子

**证据**

**(a) 密码登录路径完全不校验验证码。** `IUserTokenService` 同时声明了 `GenerateCaptchaAsync()` 和 `ValidateCaptchaAsync()`（`Services\Interfaces\IUserTokenService.cs:20,28`），实现存在（`Services\Users\UserTokenService.cs:45-77`），但 `LoginWithPasswordAsync`（`:87-131`）**没有任何验证码或失败计数**：

```csharp
// :89-116
var user = await userService.GetAsync(a => a.UserName == userName);
…
if (!user.Password.Equals(pass)) { throw new UnauthorizedException("账号或密码错误"); }
```

`ValidateCaptchaAsync` 全仓唯一引用是它自己的接口声明（grep 结果：`IUserTokenService.cs:28` 与 `UserTokenService.cs:67`，无调用方）。是没有锁定、没有指数退避、没有 IP 限制的裸暴力破解面。

**(b) 验证码字符来自非密码学随机源**：

```csharp
// Services\Users\UserTokenService.cs:48
var captchaCode = Randomizer.Next(4, exceptChar: new char[] { 'o','O','0','1','I','l' }, hasSpecialChars: false);
```

```csharp
// Infrastructure\Utils\Randomizer.cs:52
var random = new Random();          // ← System.Random，非 RandomNumberGenerator
```

而 `StringExtensions.GeneratePasswordSalt`（`Infrastructure\Utils\StringExtensions.cs:119-124`）和 `GenerateToken`（`:108-113`）**正确使用了** `RandomNumberGenerator` —— 说明作者知道区别，验证码这里漏了。

**(c) Refresh Token 明文存储 + 单次使用语义存在竞态**：

```csharp
// :229-256
var userToken = await GetAsync(a => a.RefreshToken == refreshToken);     // ← 明文比对
if (userToken is null || !userToken.RefreshTokenIsAvailable || userToken.UserId != userId)
    throw new UnauthorizedException("Refresh Token 无效");
…
var token = await GenerateUserTokenAsync(user, userToken.PlatformType);  // :248 ← 中间有 await
user.LastLoginTime = DateTimeOffset.UtcNow;
await userService.UpdateAsync(user);                                     // :251 ← 又一个 await
userToken.RefreshTokenIsAvailable = false;
await UpdateAsync(userToken);                                            // :254 ← 才作废
```

检查（`:230`）与作废（`:254`）之间跨越 3 个 `await`，无事务、无乐观并发标记（`UserToken` 上无 `[Timestamp]`/`RowVersion`，见 `Entities\Users\UserToken.cs:14-83`）。两个并发请求携带同一 refresh token 可以**都成功**，各拿到一个新 access token。这是 refresh token rotation 的经典重放漏洞。

注意 `Token`/`TokenHash` 的对比：`ValidateTokenAsync`（`:190-214`）用 `EncodeMD5(token)` 查 `TokenHash`——**token 本身已做哈希**；但 `RefreshToken` 没有对应的 `RefreshTokenHash` 字段，只能明文查。

**(d) 无密码复杂度/历史校验、无登录审计的失败记录。** `UserService.ChangePasswordAsync`（`Services\Users\UserService.cs:96-123`）只校验"新旧不同"，无长度/复杂度要求。

**(e) 静态文件服务放开未知类型 + 信任所有转发头**：

```csharp
// ServiceCollectionExtensions.cs:427-444
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    ForwardLimit = null
};
forwardedHeadersOptions.KnownIPNetworks.Clear();      // ← 信任任意来源的 X-Forwarded-For
forwardedHeadersOptions.KnownProxies.Clear();
…
app.UseStaticFiles(new StaticFileOptions
{
    ServeUnknownFileTypes = true,                     // ← 任意扩展名都按原样提供
    RequestPath = "/static",
    FileProvider = new PhysicalFileProvider(staticDirectory)
});
```

上传入口无扩展名白名单：`FileService.UploadAsync`（`:130-231`）只做 MIME 猜测与类型分类，不拒绝任何扩展名。

**影响**

- (a) 一个未认证攻击者可以在无速率限制下爆破任何已知用户名（用户名可通过登录错误信息区分：`"账号或密码错误"` vs `"该用户还未设置密码"`（`:104`）——**用户名枚举**）。
- (b) 4 位验证码来自可预测 PRNG；若被用于任何安全决策则是可绕过的。
- (c) refresh token 重放可无限延长会话。
- (e) `ServeUnknownFileTypes = true` + 无白名单 + 上传目录同源 → 上传 `.html`/`.svg` 即得到**存储型 XSS**。`KnownProxies.Clear()` 意味着 `IpAddress`（写入 `OperationLog.IpAddress`，见 `Services\SystemManagement\OperationLogService.cs:33`）与 `UserToken.IpAddress`（`Entities\Users\UserToken.cs:51`）**可被客户端伪造** → 审计日志与风控形同虚设。

**建议**

(a) 登录加验证码强制校验 + 基于 `UserName`/IP 的失败计数器（Redis `INCR` + TTL）+ 统一化错误信息（"账号或密码错误"，不区分用户是否存在与是否设过密码）。(b) `Randomizer` 改用 `RandomNumberGenerator.GetItems<char>`。 (c) 增加 `RefreshTokenHash` 列并按哈希查询；rotation 用 `UPDATE … WHERE Id=@id AND RefreshTokenIsAvailable=1` 的影响行数判定成功（乐观并发），或用 `RowVersion` + `DbUpdateConcurrencyException`。(e) `KnownProxies`/`KnownIPNetworks` 必须按实际网段显式配置；上传目录用独立域名/`Content-Disposition: attachment`，并维护扩展名 + MIME 白名单；`ServeUnknownFileTypes` 改为 `false`。

---

### 15. `DistributedLock` 反模式：未获锁者删除持有者的锁

**证据**

```csharp
// HostedServices\ExecuteSeedDataService.cs:125-146
if (await redisDatabaseProvider.SetAsync(code, taskId, TimeSpan.FromMinutes(5), CSRedis.RedisExistence.Nx))
{
    await seed.ApplyAsync(model);
    model.ExecuteStatus = ExecuteStatus.Success;
    model.ExecuteTime = DateTimeOffset.UtcNow;
}
…
finally
{
    await UpdateCronTask(model);
    await redisDatabaseProvider.DeleteAsync(code);      // ← 无条件删除，不看是否是我加的锁
}
```

`taskId`（`:97 var taskId = Guid.NewGuid().ToString();`）作为锁的值被写入，却**从未被读回校验**——这是典型的 fencing 信息被采集但未使用。

**影响**

多副本部署（`hostedServices` 在 WebService 与 PlanTaskService 都会跑）时：副本 B 没抢到锁，跳过执行，然后在 `finally` 里把副本 A 的锁**删掉**。副本 C 随即可以抢到锁并并发执行同一份种子数据。种子写入包含 `InsertAsync`（`:67`、`:107`），并发会造成重复主键或重复业务数据。

同一文件还有另一处错误：

```csharp
// :104-107
if (model is not null && !model.IsEnable)
{
    break;                       // ← 应为 continue
}
```

一个被禁用的种子任务会**中断其后所有种子任务**（按 `Order` 排序，见 `Enums\PlatformType.cs`… 实际是 `ISeedData.Order`，`ExecuteSeedDataService.cs:93`）——因为遇到第一个 `IsEnable=false` 就退出整个 `foreach`。

**建议**

释放锁必须校验所有权（Lua 脚本 `if redis.call("get",KEYS[1])==ARGV[1] then del` 或直接换用成熟的 `RedLock`/`DistributedLock` 库）。`break` 改 `continue`。种子执行器的并发语义应显式建模（"只允许一个实例执行种子迁移"），而不是靠一个随手加的 `SET NX`。

---

### 16. 文件落盘逻辑存在静默截断/垃圾填充

**证据**

```csharp
// Infrastructure\Utils\FileHelper.cs:24-43
if (length > 1024)
{
    byte[] buffer = null;
    while (i * 1024 < length)
    {
        buffer = new byte[1024];
        stream.Read(buffer, 0, 1024);       // ← 忽略返回值
        fs.Write(buffer, 0, buffer.Length); // ← 恒写 1024 字节
        i++;
    }
    buffer = new byte[1024];
    stream.Read(buffer, 0, (int)length - ((i - 1) * 1024));
    fs.Write(buffer, 0, (int)length - ((i - 1) * 1024));
}
else
{
    byte[] buffer = new byte[1024];
    stream.Read(buffer, 0, buffer.Length);
    fs.Write(buffer, 0, buffer.Length);     // ← 小文件写出 1024 字节
}
```

**影响**

三点确定性缺陷：

1. `Stream.Read` 不保证读满请求长度（socket/网络流、`GZipStream`、部分 `FileStream` 场景）。返回 `n < 1024` 时，`buffer[n..]` 是**上一轮的残留或零**，被原样写入 → **文件静默损坏**。
2. 文件长度恰好等于 1024 的整数倍时，`while (i*1024 < length)` 退出后 `length - (i-1)*1024 == 1024`，最后一步再读一次，此时流已 EOF，`Read` 返回 0，但仍写入 1024 字节 → **尾部多出 1024 字节垃圾**。
3. `length <= 1024` 分支永远写 1024 字节 → 任何小于 1KB 的文件都被填充到 1KB。`stream.Read` 的返回值同样被忽略。

此外 `:13 string tempDir = …` 声明后未使用（死代码），`:20 FileMode.CreateNew` 在文件已存在时会抛异常（外层 `:18 if (!File.Exists(...))` 掩盖了它）。

**建议**

直接改为 `await stream.CopyToAsync(fs)`（或 `fs.WriteAsync` 循环带返回值校验）。整个方法只有 40 行有效逻辑，重建时应替换为 `IFormFile.CopyToAsync` 或 `IFileStorage` 的流式 `PutAsync(Stream)`。

---

### 17. `EncryptionHelp` / `StringExtensions`：加密工具混装、部分 API 在 .NET 10 上已不可用

**证据**

```csharp
// Infrastructure\Utils\EncryptionHelp.cs:34
var md5Hasher = new MD5CryptoServiceProvider();          // 已过时；未 Dispose
// :128-133
using (var rsaProvider = new RSACryptoServiceProvider())
{
    var key = RsaPublicKeyToXml(publicKey);
    rsaProvider.FromXmlString(key);                      // ← .NET 10 非 Windows 上 PlatformNotSupportedException
```

`RsaPublicKeyToXml`（`:52-107`）用 `catch { publicKeyParam = null; }` **吞掉所有异常**（`:66-69`、`:88-91`），最后"如果都解析失败则返回原串"（`:94-95`）。调用方无法区分成功与失败。

同类中还有：`GenerateBatchNumber()`（`:161-167`）——域概念（批次号）出现在基础设施加密工具类里，且用 `new Random().Next(0,999)` **只有 999 种取值**且日期偏移 `AddDays(-5)`（`:163`）无解释；`GenerateCainiaoSha256Signature`（`:214-229`）是**菜鸟开放平台**的签名，属于某个具体业务集成。

`StringExtensions.cs` 内混装：

- `ToShopIdString(this long[])` / `ToShopIdString(this long)` / `ToShopIdArray(string)`（`:181-210`）——**门店 ID** 的逗号拼接，纯业务概念。
- `toBigIntList(this string?)`（`:146-151`）——**小写命名违反 C# 约定**；用 `Split(".")` 分割后 `Convert.ToInt64`，遇非数字抛 `FormatException`。
- `IsNullOrEmpty`（`:131-134`）语义是 `IsNullOrWhiteSpace || equals "null"` —— **名字与实际行为不符**，把字面量字符串 `"null"` 当空值。
- `GenerateUniqueFileName`（`:152-159`）只精确到毫秒（`"yyyyMMddHHmmssffff"`），同毫秒同名文件会**覆盖**（`AliyunFileStorage.cs:36` 在 `isOverride: false` 分支使用它作为唯一名）。
- `GenerateToken`（`:108-113`）与 `GeneratePasswordSalt`（`:119-124`）都 `RandomNumberGenerator.Create()` **未 Dispose**。
- `ToMaskSensitiveInfo`（`:166-179`）保留首字符 → 对 2 字符手机号段类信息几乎无遮蔽。

`CollectExtensions.cs:17-21` 是一个必然返回 `false` 的函数（`dynamic` 方法里 `return false;`，局部 `redisKeys` 字典是死代码）：

```csharp
public static dynamic GetRedisKeys()
{
    Dictionary<string, object> redisKeys = new Dictionary<string, object>();
    return false;
}
```

**影响**

- `RSACryptoServiceProvider.FromXmlString` 在 .NET 10 + Linux 上会抛 `PlatformNotSupportedException`；`RsaEncrypt` 属**模板在目标平台不可用**的 API（模板主打 Linux 容器部署）。
- 基础设施层混入门店/菜鸟等业务概念 → 拆微服务时**无法判断哪个服务该带走这个文件**。这正是"模板无业务领域"最直接的污染证据。
- `GenerateUniqueFileName` 的同毫秒覆盖是真实数据丢失路径（批量上传同名文件）。
- 死代码（`GetRedisKeys`、`toBigIntList` 无调用方、`GenerateBatchNumber` 无调用方）会误导重建者以为是活代码。

**建议**

按用途拆分：`Crypto`（哈希/AES/RSA，全部异步/`using`/`RandomNumberGenerator`）、`Signing`（`SignMD5Request`、`GenerateCainiaoSha256Signature` → 后者应移入对应的集成适配器）、`Text`（字符串扩展）。`RsaEncrypt` 重写为 `RSA.Create()` + `ImportSubjectPublicKeyInfo`（PEM/Base64 DER 原生支持），彻底删除 XML 密钥格式与 BouncyCastle PEM 解析。门店/菜鸟相关成员全部移出或删除。死代码直接删。

---

### 18. 网关子系统：本区域质量最高，但锁是进程内的

**证据**

`JsonProxyConfigStore` 是本区域少数认真写的类：`SemaphoreSlim` 双检锁（`:68-96`）、`FileSystemWatcher` 带 500ms/300ms 双重防抖（`:313-338`）、`IOException` 重试 3 次（`:148-154`）、`Dispose` 时 1 秒超时后强制释放（`:400-426`）。

`DynamicProxyConfigProvider` 有序发布新配置后再延迟释放旧配置：

```csharp
// :77-88
var oldConfig = _config;
_config = new DynamicProxyConfig(routes, clusters);
oldConfig.SignalChange();                    // 先通知
_ = Task.Run(async () => { await Task.Delay(1000); oldConfig.Dispose(); });  // 延迟释放
```

但并发保护是**进程内的**：

```csharp
// Gateway\ProxyConfigLockService.cs:7-19
/// 代理配置并发锁服务（单例）
/// 用于在多个请求之间协调配置修改操作，防止并发修改导致数据不一致
public class ProxyConfigLockService : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    public SemaphoreSlim Lock => _lock;
```

`Host\NexusStack.Gateway\Controllers\ProxyManagementController.cs:55,82,104,154,182,205` 用它包裹"读-改-写"，`:70,97,120,170,198,221` 手动 `Release()`（**8 处裸 `WaitAsync`/`Release` 配对，无一处 `try/finally`**）。

**影响**

- 多个 Gateway 副本时，各自持有独立的 `SemaphoreSlim`，同时写**同一个 `proxy-config.json` 文件**（`JsonProxyConfigStore.cs:44` 路径为 `AppContext.BaseDirectory`，容器内通常挂在共享卷或各自独立）→ 丢失更新。在 K8s 多副本网关下这是**必然发生**的配置漂移。
- 若各副本 `BaseDirectory` 独立（非共享卷），则 `JsonProxyConfigStore` 的文件监听只在本地生效，**副本间配置永久不一致**——每个副本的 `proxy-config.json` 各自演化。
- 控制器中 8 处非 `try/finally` 的 `Release()`：任何中间 `throw`（`_configProvider.ReloadConfigAsync()` 会抛 `ObjectDisposedException`，`DynamicProxyConfigProvider.cs:42-45`）都会**永久泄漏信号量**，之后所有网关配置变更请求挂起。
- `IProxyConfigStore` 唯一实现是 `JsonProxyConfigStore`（`ServiceCollectionExtensions.cs:215`）→ **只有 1 个适配器的缝**。这是"投机性抽象"：接口存在但无真实变化。

**建议**

网关配置应存**配置中心 / 数据库 / K8s ConfigMap + 控制器**，取"单一事实来源"，副本只读缓存。进程内锁换成 Redis/etcd 分布式锁或乐观并发版本号（`proxy-config.json` 加 `version` 字段，写入时 CAS）。若坚持文件存储，则必须所有副本共享同一卷。控制器里的 `WaitAsync`/`Release` 一律改成 `await using var _ = await _lockService.Lock.LockAsync()`（`.NET 9+` `SemaphoreSlim.WaitAsync` 无 `IDisposable`，需自建 helper）。`IProxyConfigStore` 若确定不会有多实现，重建时可直接用具体类型，减少一层。

---

### 19. `RequestAuthenticationHandler` 与其它死代码：约占本区域 5–8% 的行数

**证据**

| 死代码 | 证据 | 判定依据 |
|---|---|---|
| `RequestAuthenticationHandler` | `Authentication\RequestAuthenticationHandler.cs:21-98`（98 行） | 仅出现在 `ServiceCollectionExtensions.cs:175` 与 `:205` 的**注释**里（grep 全仓，无 `AddScheme` 活引用）。同时使用已过时的 `ISystemClock` 构造参数（`:25`），在 .NET 10 上 `AuthenticationHandler(..., ISystemClock)` 已 obsolete |
| `RequestAuthenticationSchemeOptions` | `Authentication\RequestAuthenticationSchemeOptions.cs:11-14` | 仅被上面的死 Handler 使用 |
| `CustomerAuthenticationSchemeOptions` | `Authentication\RequestAuthenticationSchemeOptions.cs:32-35` | 全仓无引用 |
| `SnowFlake.SalesOrderInstance` | `Infrastructure\SnowFlake\SnowFlake.cs:30-36` | 全仓无调用方（grep 仅命中声明处）。注释"销售订单种子为8" |
| `SnowFlake.ReturnOrderInstance` | `Infrastructure\SnowFlake\SnowFlake.cs:42-48` | 全仓无调用方。注释"退货订单种子为6" |
| `SignalRHubAttribute` | `Infrastructure\Attributes\SignalRHubAttribute.cs:7-21` | 只被 `NotificationHub.cs:9` 贴上，**无任何反射读取方**（grep 全仓只有声明与那一处使用）。注释声称"用于自动映射 Hub 端点"，实际映射是硬编码 `app.MapHub<NotificationHub>("/hubs/notification")`（`ServiceCollectionExtensions.cs:315`） |
| `CollectExtensions.GetRedisKeys` | `Infrastructure\Utils\CollectExtensions.cs:17-21` | 无条件 `return false` |
| `CollectExtensions.RebuilderMetaDataQueryParemeter` / `CheckIdColumnAndAddRow` / `ConvertExpandoObject` | `CollectExtensions.cs:28-96` | `DataTable`/`DataRow` 时代的 API，与 EF Core 无关，全仓无调用方 |
| `IProxyConfigStore.GetRouteAsync/GetClusterAsync` | `Gateway\JsonProxyConfigStore.cs:202-212` | 无调用方 |
| `IServiceBase.ExecutePagedStoredProcedureAsync` / `FillTableByReader` | `Domain\NexusStack.EFCore\Repository\ServiceBase.cs:156,221` | 全仓无调用方；且硬绑 Npgsql |
| `Middleware\ExceptionHandlerMiddleware` 的 `else` 缺失 | `Middlewares\ExceptionHandlerMiddleware.cs:22-41` | 见发现 11 |
| `FileStorageFactory.GetStorage(FileStorageType)` 的 4 个幽灵枚举值 | `Enums\FileEnums.cs:15-18` | `Huawei/Tencent/Aws/Oss` 无实现 |
| `ISeedData.ConfigPath` | `ISeedData.cs:21` + 8 个实现全为 `string.Empty` | 接口成员与实现语义不一致（见发现 10） |

**影响**

死代码占据本区域约 400–700 行。更重要的是其中 3 处会**主动误导**：`SignalRHubAttribute` 声称自动映射（实际不）、`SnowFlake` 的销售/退货订单实例暗示存在订单领域（实际不存在）、`RequestAuthenticationHandler` 是一个完整可用的开放 API 签名认证实现（模板使用者很可能以为它已启用——`appsettings` 里配 `AgileConfig`/开放平台却拿不到鉴权）。

**建议**

重建时先做一轮"删除清单"评审。要保留 `RequestAuthenticationHandler` 的签名校验逻辑（`:58-89` 的 `appKey/session/timestamp/sign` 校验顺序与时间窗判断是合理的），但必须**实际注册并配测试**；否则删除。`SnowFlake` 的三个实例全部删掉，只留一个注入式 `IIdGenerator`。`SignalRHubAttribute` 要么实现自动映射（反射扫描 `Hub` 子类）要么删除。

---

### 20. 其它正确性问题（按严重度递减）

**20.1 操作日志导出按错误字段排序**

```csharp
// Services\SystemManagement\OperationLogService.cs:129
return ExportExcelHelper.ExportToExcel(logs.OrderByDescending(x => x.CreatedBy), columnsMapping, "OperationLogs");
```

`CreatedBy` 是操作人 ID。应为 `CreatedAt`。导出的"日志时间"列（`:121 { "日志时间", "CreatedAt" }`）因此**不是时间序**。

**20.2 `OperationLogService.LogAsync` 把 JSON 塞进内容字段**

```csharp
// :35
OperationContent = content.IsNotNullOrEmpty() ? content : json,
```

参数名是 `OperationContent`，混入 `json`。`Remark = json` 被注释掉（`:38`）。同时 `:36-37` 与 `:34` 把同一个 `code` 写进两个字段：

```csharp
OperationMenu = code ?? "",      // :34
MenuCode = code ?? "",           // :37
```

两个字段语义相同、值相同 → 冗余列。

**20.3 `EnumService.EnumToList<T>` 对无描述枚举抛异常**

```csharp
// :18-23
DescriptionAttribute[] arrDesc = (DescriptionAttribute[])myEnum.GetCustomAttributes(typeof(DescriptionAttribute), false);
list.Add(new EnumOptionDto { value = (int)myEnum.GetValue(null), label = arrDesc[0].Description });   // ← IndexOutOfRange
```

`Enums\GlobalSettingKey.cs:7-11`（`SMS`/`Email` 无 `[Description]`）、`Enums\FileEnums.cs:11-19`（`FileStorageType` 全部无 `[Description]`）、`Enums\FileEnums.cs:24-31`（`FileType`）都会崩。`EnumToList<T>` 应以 `EnumExtensions.GetDescription()`（`Infrastructure\Utils\EnumExtensions.cs:10-15`，**已正确处理 null**）为唯一实现。

**20.4 `PermissionService.JudgeHasPermissionAsync` 空引用**

```csharp
// :276-283
var menu = await menuService.GetAsync(a => a.Code == menuCode);
var resource = await apiResourceService.GetAsync(a => a.Code == code);
return await menuResourceService.ExistsAsync(a => a.MenuId == menu.Id && a.ApiResourceId == resource.Id);
```

`menu` 或 `resource` 为 null 时 `:282` 抛 `NullReferenceException` → 500。应为"未找到即无权限"返回 `false`。

**20.5 `CaptchaHelper` 每次调用新建 `Random` + 每次调用读字体文件**

```csharp
// Infrastructure\Captcha\CaptchaHelper.cs:31,104
var random = new Random();                    // :31
private static SKColor GetRandomDeepColor() { var random = new Random(); … }   // :104
// :43
using var typeface = SKTypeface.FromFile(Path.Combine(AppContext.BaseDirectory, "Fonts/SpecialElite-Regular.ttf"));
```

`SKTypeface.FromFile` 每次请求读盘解析 TTF（`Infrastructure\Fonts\SpecialElite-Regular.ttf` 5191 行/约 100KB+）。应静态缓存 `SKTypeface`。注意 `SKTypeface` 在 SkiaSharp 3.x 中已是 `IDisposable`——静态缓存会引入生命周期问题，需谨慎。

**20.6 `UserContextCacheService.InvalidateAsync` 遍历全部枚举值**

```csharp
// Services\Users\UserContextCacheService.cs:66-67
foreach (var p in Enum.GetValues<PlatformType>())
    await redisService.DeleteAsync(CacheKey(userId, p));
```

`PlatformType`（`Infrastructure\Enums\PlatformType.cs:11-37`）的 `All = 0` 是**哨兵值不是平台**，却被当作平台查询；`Admin=1,Pc=2,Mini=4,Android=8` → 5 次串行 `DEL`。且这里是**串行 await**，用户角色变更时若有 100 个受影响用户则 500 次往返（`PermissionService.cs:126-127` 的调用方循环）。

**20.7 `UserContextCacheService.BuildFromDbAsync` 无 null 检查**

```csharp
// :72-75
var user = await dbContext.Set<User>().Where(u => u.Id == userId)
    .Select(u => new { u.UserName, u.Email, u.IsEnable }).FirstOrDefaultAsync(cancellationToken);
// :123-125
UserName = user?.UserName ?? string.Empty,
IsEnable = user?.IsEnable ?? false,
```

用户已被删除但 token 仍有效时 → 缓存 `IsEnable=false` → `RequestAuthorizeFilter.cs:73-80` 返回 **403 "该用户[]已被禁用"**（用户名为空字符串）。语义错位：应为 401 会话失效。

**20.8 `FileService` 的两处流处理问题**

```csharp
// Services\SystemManagement\FileService.cs:124-126
var bytes = new byte[stream.Length];      // ← 非可寻址流抛 NotSupportedException
stream.Position = 0;                      // ← 非可寻址流抛 NotSupportedException
await stream.ReadAsync(bytes, 0, bytes.Length);   // ← 单次 ReadAsync 不保证读满
```

`ReadAsync` 的返回值被忽略 → 大文件可能只读了一部分。`IFormFile.OpenReadStream()` 对已缓冲的请求可寻址，但其他来源（网络流转发、`GZipStream`）不行。

**20.9 `Debugger.IsAttached` 决定生产行为**

```csharp
// :85-95
if (Debugger.IsAttached)
{
    var client = httpClientFactory.CreateClient();
    var downloadBytes = await client.GetByteArrayAsync(file.Url);   // ← 走 HTTP 下载
    …
}
var bytes = await GetContentAsync(file);                            // ← 走存储适配器
```

"是否附加调试器"改变数据来源。本地调试与生产走**不同的代码路径**，意味着本地验证过的逻辑与线上不一致。

**20.10 `Dtos` 命名与目录约定破坏**

- 接口分散在两处：`Services\Interfaces\I*.cs`（15 个）与实现文件内联（`Services\Users\UserDepartmentService.cs:13`、`Services\OpenAppConfigs\AppConfigService.cs:16`、`Services\SystemManagement\GlobalSettingService.cs:17`、`Services\OpenAppConfigs\AppEventConfigService.cs:13` 等）。
- 拼写错误进了公共 API：`IApiResrouceCoreService`（应为 `Resource`）—— `Services\Interfaces\IApiResrouceCoreService.cs:10`、`Services\SystemManagement\ApiResrouceCoreService.cs:15`、`HostedServices\InitApiResourceService.cs:44`。类名同样拼错。
- `Attributes\` 目录存在两套：`Infrastructure\Attributes\SignalRHubAttribute.cs`（1 个）与 `Core\Attributes\`（`NoLoggingAttribute.cs`、`OperationLogActionAttribute.cs`、`SkipResponseWrapperAttribute.cs`）。重建时需决定归属。

**20.11 `Public` 可变静态字段**

```csharp
// Infrastructure\SnowFlake\SnowFlake.cs:24,36,48
public static IdWorker Instance = _instance.Value;
public static IdWorker SalesOrderInstance = _salesOrderInstance.Value;
public static IdWorker ReturnOrderInstance = _returnOrderInstance.Value;
```

`public static` **非 readonly** —— 任何代码都能替换 ID 生成器。应为 `public static IdWorker Instance { get; } = …`（或直接删除）。

---

### 21. 微服务就绪度：四个可部署单元共享同一整套技术栈与同一套抽象

**证据**

四个"服务"的入口文件是**单行转发**：

```csharp
// BackgroundServices\NexusStack.MQService\Program.cs（全文 11 行）
var moduleKey = "nexusstack-mq";
var moduleTitle = "NexusStack MQ Service";
var builder = WebApplication.CreateBuilder(args);
await builder.InitAppliation(moduleKey, moduleTitle, CoreServiceType.MQService);
```

`BackgroundServices\NexusStack.PlanTaskService\Program.cs` 同形，仅 `CoreServiceType.PlanTaskService` 不同；`Host\NexusStack.WebAPI` 与 `Host\NexusStack.Gateway` 同样走 `InitAppliation`。

四个 `.csproj` 的依赖图：

```
Host\NexusStack.Gateway\NexusStack.Gateway.csproj           → ProjectReference NexusStack.Core
Host\NexusStack.WebAPI\NexusStack.WebAPI.csproj             → ProjectReference NexusStack.Core
BackgroundServices\NexusStack.MQService\NexusStack.MQService.csproj       → ProjectReference NexusStack.Core
BackgroundServices\NexusStack.PlanTaskService\…csproj       → ProjectReference NexusStack.Core
```

而 `NexusStack.Core` 传递引入全部 7 个技术库：

```xml
<!-- Domain\NexusStack.Core\NexusStack.Core.csproj:30-38 -->
<ProjectReference Include="..\..\Infrastructure\NexusStack.Infrastructure.csproj" />
<ProjectReference Include="..\NexusStack.EFCore\NexusStack.EFCore.csproj" />
<ProjectReference Include="..\NexusStack.Excel\NexusStack.Excel.csproj" />
<ProjectReference Include="..\NexusStack.RabbitMQ\NexusStack.RabbitMQ.csproj" />
<ProjectReference Include="..\NexusStack.Redis\NexusStack.Redis.csproj" />
<ProjectReference Include="..\NexusStack.Serilog\NexusStack.Serilog.csproj" />
<ProjectReference Include="..\NexusStack.Swagger\NexusStack.Swagger.csproj" />
```

**影响 —— 这解释了本报告绝大多数问题，是"单进程假设"的根本机制**

1. **网关进程被强制携带整个业务栈**。`NexusStack.Gateway` 只需要 YARP + `IProxyConfigStore`（`Domain\NexusStack.Core\Gateway\` 共 680 行），却因这一个 `ProjectReference` 同时链接了 EF Core（PostgreSQL/MySQL/SqlServer 驱动）、阿里云 OSS SDK、SkiaSharp、Xabe.FFmpeg、NPOI/Excel、AgileConfig、SignalR、Swagger。**无法独立升级、无法独立裁剪、无法独立评估漏洞面。**
2. **`IAppConfigService`、`IUserService`、`IMenuService` 等 30+ 个服务在网关进程里都被注册**（`Domain\NexusStack.Core\ServiceCollectionExtensions.cs:151-153` 的反射全量扫描，对四个 `CoreServiceType` 一视同仁，无任何筛选）。网关进程因此持有一个指向业务库的 `MainContext` 连接池。
3. **`CoreServiceType` 枚举是"单进程多角色"的自白书**（`Infrastructure\Enums\PlatformType.cs:81-99`）。它只在 `AddBuilderServices`/`UseApp` 的 `if/else if` 链里被消费（`ServiceCollectionExtensions.cs:169-223`、`:257-268`、`:313-322`）。把"我是什么服务"编码成一个**配置枚举**而不是**独立的程序集边界**，意味着任何跨服务误用（网关调用 `IUserService`）都能编译通过。
4. **`Program.cs` 只有 11 行，说明"服务"没有自己的组合根**。每个服务应有的"我要哪些依赖、暴露哪些端点、连哪个库"完全没有表达——全部由 `InitAppliation(moduleKey, moduleTitle, coreServiceType)` 三参数决定。想给 PlanTaskService 加一个独立的健康检查或换一个消息中间件，必须修改共享库。
5. **`moduleKey` 只用于日志/线程名**（`ServiceCollectionExtensions.cs:337 Thread.CurrentThread.Name = moduleKey;`、`:376-378 Console.WriteLine`），不参与任何隔离。四个进程共享同一份 `appsettings.json` 结构（`:349-350`），包括同一个数据库连接串。

**建议**

`NexusStack.Core` 必须被拆成可独立引用的**技术包**，且每个部署单元只引用它真正需要的：

- `Platform.Security`（认证/鉴权/Token）— 被所有需要认证的服务引用
- `Platform.Files`（存储/导出/转码）— 只被需要文件的服务引用
- `Platform.Scheduling`（Cron/AsyncTask）— 只被 PlanTaskService 引用
- `Platform.Messaging`（EventBus/EventData/SignalR 中继）— 只被生产者与 MQService 引用
- `Platform.Gateway`（`Gateway\**` 680 行）— **只被 Gateway 引用**，且只依赖 ASP.NET Core + YARP

每个 `Program.cs` 变成真正的组合根（显式 `AddXxx()` 列表），`CoreServiceType` 枚举删除。`appsettings.json` 每个服务独立，连接串不共享。这是把"12 个技术库 + 1 个巨型 Core"变成"6 个能力包 + N 个服务"，也是本区域重建的第一块砖。

---

### 22. DDD 就绪度判定与限界上下文切分建议

**领域关注 vs 技术设施的当前归属**（这是重建时最重要的一张表）：

| 当前路径 | 真实性质 | 重建归属 |
|---|---|---|
| `Core\Entities\Users\{User,Role,UserRole,Permission,UserToken,UserDepartment}` | **支撑域的领域模型**（身份与访问） | `Identity` 服务（首次出现真正的聚合/值对象） |
| `Core\Services\Users\*`, `Core\Authentication\*`, `Core\Filters\RequestAuthorizeFilter.cs`, `Core\CurrentUser.cs` | 认证鉴权（支撑域） | `Identity` 服务 |
| `Core\Entities\SystemManagement\{Menu,MenuResource,ApiResource,Region,GlobalSettings,DownloadItem}` | **技术/配置**，不是领域（`Region` 勉强算参考数据） | `Platform.Config`（菜单/权限资源/全局设置）+ `Platform.ReferenceData`（行政区划） |
| `Core\Services\SystemManagement\{MenuService,PermissionService,ApiResrouceCoreService,EnumService}` | 权限资源 CRUD（技术） | `Platform.Config` |
| `Core\Entities\SystemManagement\{File,OperationLog}` + `Core\Services\SystemManagement\{FileService,OperationLogService,DownloadService}` + `Infrastructure\FileStroage\*`, `Infrastructure\Video\*` | 文件与审计（技术） | `Platform.Files` + `Platform.Audit` |
| `Core\Entities\{AsyncTasks,Schedules}\*` + `Core\Services\{AsyncTasks,Schedules}\*` + `Core\Schedules\*`, `Core\HostedServices\*`, `Core\ISeedData.cs`, `Core\SeedData\*` | 调度与异步执行（技术） | `Platform.Scheduling` |
| `Core\Gateway\*` | 网关（技术，独立部署单元） | `Gateway`（已有独立 Host 项目） |
| `Core\EventData\*`, `Core\EventHandler\*`, `Core\SignalR\*` | 消息与实时推送（技术） | `Platform.Messaging` |
| `Infrastructure\Options\*`, `Infrastructure\Converters\*`, `Infrastructure\Utils\{String,Collect,Object,Enum,Money}Extensions`, `Infrastructure\Models\*`, `Infrastructure\Dtos\*`, `Infrastructure\Exceptions\*`, `Infrastructure\Enums\*` | 真正的横切基础设施 | `BuildingBlocks`（NuGet 包，不是项目引用） |
| `Infrastructure\Utils\{EncryptionHelp,Randomizer}`, `Infrastructure\Captcha\*` | 安全原语 | `BuildingBlocks.Security` |
| `Infrastructure\SnowFlake\*`, `Infrastructure\TypeFinders\*`, `Infrastructure\App.cs`, `Infrastructure\ServiceCollectionExtensions.cs` | **应删除**（见发现 1、2、5） | — |
| `Infrastructure\Utils\{ToShopId*, GenerateCainiao*, GenerateBatchNumber}`, `SnowFlake.{Sales,Return}OrderInstance`, `Enums\OpenAppConfigs\WebHookType.cs`（菜鸟/退货订单/门店）、`ICurrentUser.{CustomerId,CustomerPhone,WechatUnionID,CustomerTokenHash}` | **上一代业务残留** | 删除或移入具体业务服务 |

**有没有任何聚合/实体/值对象/领域事件/仓储词汇？**

- 聚合根 `AggregateRoot`：**0 处**
- 值对象 `ValueObject` / `record`：**0 处**
- 领域事件 `IDomainEvent` / `DomainEvent`：**0 处**。仅有 `EventData\{AsyncTaskEventData,NotificationEventData,OperationLogEventData}` —— 三者都继承 `NexusStack.RabbitMQ.EventBus.EventBase`（`EventData\OperationLogEventData.cs:12`），是 **MQ 传输契约**，不是领域事件。
- 仓储 `IRepository`：有 `IRepositoryBase<TEntity,TKey>`（`Domain\NexusStack.EFCore\Repository\Base\IRepositoryBase.cs:13`），但它是**泛型 CRUD 仓储**（`ServiceBase.cs` 暴露 `GetQueryable()`、`UpdateFromQueryAsync`、`BatchDeleteAsync`），不是"按聚合根设计的仓储"。且它住在**技术库 EFCore** 里，不在 Core。
- 实体：有，但是贫血 POCO。
- `Specifications<T>`（`Domain\NexusStack.EFCore\Repository\Specifications.cs:8`）是**查询规约**（Ardalis.Specification），与领域规约模式无关。

**结论：完全是 CRUD + DTO + 泛型服务。`Domain` 目录名是误导。**

**自然出现的限界上下文接缝**（按耦合强度排序，越靠前越容易切）：

1. **Gateway** — 已经独立（`Host\NexusStack.Gateway`），零领域耦合。`DynamicProxyConfigProvider` 只依赖 `IProxyConfigStore`。
2. **File/Storage** — 边界清晰：`IFileService`（`Services\Interfaces\IFileService.cs:14`）+ `IFileStorage` + `VideoHelper`。唯一外部依赖是 `ICurrentUser`（未实际使用）与 `SnowFlake`。
3. **Scheduling / Messaging** — `CronScheduleService`、`AsyncTask`、`IEventPublisher`。与业务通过 `EventBase.Code` 字符串解耦（`AsyncTaskService.cs:29,45`）。
4. **Audit / OperationLog** — 通过 `OperationLogEventData` 异步解耦，切分成本最低（但注意发现 8：当前只有 MQService 消费）。
5. **Identity（用户/角色/权限/Token）** — **耦合最深**。`UserContextCacheService.cs:72-131` 一个方法跨 `User`+`UserRole`+`Role`+`UserDepartment`+`Permission`+`MenuResource`+`ApiResource` 七张表。这是唯一一处需要**真正的数据所有权重构**才能切分的地方：`MenuResource`/`ApiResource`（配置域）与 `Permission`（授权域）必须归到同一侧，否则鉴权上下文无法一次构建。

---

## 值得保留

以下 8 项在新架构中应原样或近乎原样存活——它们是本区域真正的资产：

1. **`UserContextCacheService` + `UserContextCacheDto.ApiPermissionKeys` 的预计算鉴权设计**
   `Services\Users\UserContextCacheService.cs:96-119` 把 RBAC 判定所需的全部数据在认证阶段一次性算成 `HashSet<string>`，键格式 `routetemplate:HTTPMETHOD`（`:114`），使每次请求的鉴权退化为 `HashSet.Contains`（`Filters\RequestAuthorizeFilter.cs:131`，注释标注 O(1)）。这是**用一个小接口（"读用户上下文"）隐藏了大量实现（跨 7 张表的权限推导）——本区域唯一称得上"深"的模块**。`CacheVersion = "v2"`（`:33`）的设计（改 Key 格式即自动失效全部旧缓存，无需手工清 Redis）尤其成熟。拆微服务后这个模式应保留，只是"构建者"从本地 DB join 变成一个 Identity 服务。

2. **认证 Handler 与授权 Filter 的职责分离**
   `Authentication\RequestAuthenticationTokenHandler.cs:22-68` 只做"token → 身份 + 上下文"，`Filters\RequestAuthorizeFilter.cs:34-140` 只做"身份 + 上下文 → 放行/拒绝"。`ApiAuthorizationOptions.ApiPermissionMode`（`Options\ApiAuthorizationOptions.cs:21`）的 `Strict/Relaxed/RootOnly/Disabled` 四档 + `EnableRootBypass`（`:27`）是**生产可控的开关**，且注释明确写出"身份认证、用户上下文加载、用户启用状态校验在所有模式下均强制执行"（`:14-15`）——这条边界划得很准。这是可以带进新架构的鉴权骨架。

3. **操作日志走 MQ 异步落库**
   `Filters\OperationLogActionFilter.cs:85` 发布、`EventHandler\OperationLogEventHandler.cs:14-21` 消费。请求线程不为审计日志付数据库往返代价。**模式正确**，只是当前部署配置有缺陷（发现 8）。新架构应保留"审计异步化"，并把传输换成 Outbox 表以保证不丢。

4. **`CronScheduleService` 的抽象形状**
   尽管实现有缺陷（发现 9），其**接口设计**是对的：子类只提供 `Expression`（`:28`）、`Singleton`（`:34`）、`ProcessAsync(CancellationToken)`（`:61`）三个成员，其余（下次执行时刻计算、`Cronos` 解析、`DateTimeKind` 归一化到 UTC `:52-58`、执行记录写入、失败记录、表达式热更新）全在基类。这是**小接口 + 大实现**的正面例子。`DateTimeKind` 那段归一化处理（`:53-58`，注释说明"避免 Kind 与 offset 冲突"）是踩过坑的产物，应保留。

5. **`FileStorageFactory` 的按类型解析 + `IScopedDependency` 自动注册**
   工厂 + `Enums\FileEnums.FileStorageType` 的组合（`Infrastructure\FileStroage\FileStorageFactory.cs:13-31`）在设计意图上是对的：调用方说"我要 Local"，不关心实现。**去掉内部 scope 逃逸**（发现 4）后，这就是一个可用的小端口。`IFileStorage.StorageType` 自描述属性（`IFileStorage.cs:14`）让注册表无需额外元数据——这个技巧值得保留。

6. **`JsonProxyConfigStore` 的并发与容错处理**
   `Gateway\JsonProxyConfigStore.cs` 的双检锁（`:68-96`）、`FileSystemWatcher` 双重防抖（`:327-338`，注释解释了两层防抖各自的理由）、`IOException` 3 次重试（`:148-154`）、`Dispose` 先停监听再带超时释放锁（`:392-426`）。这是全仓**并发考虑最周全的文件**，即便换成 DB 存储，这套"快速路径无锁 + 慢速路径有锁 + 外部变更防抖"的结构可以直接迁移。

7. **`PlatformType` 的 `[Flags]` 建模 + 跨平台角色并集**
   `Infrastructure\Enums\PlatformType.cs:10-37`（`Admin=1,Pc=2,Mini=4,Android=8`）配合 `Role.Platforms`（`Entities\Users\Role.cs:34`）与 `UserRoleService.GetUserRoles` 的位运算过滤（`Services\Users\UserRoleService.cs:28`：`(r.Platforms & platformType) != 0`）。一个用户在不同平台拥有不同角色集合，这是**真实存在的多端语义**，建模干净。`PlatformType.All = 0` 作为查询哨兵的约定在 `UserContextCacheService.cs:66` 被误用（发现 20.6），但模型本身正确。

8. **`AuditedEntity` 的审计字段约定 + `DateTimeOffset` 统一使用**
   全仓时间字段统一 `DateTimeOffset`（`Domain\NexusStack.EFCore\Entities\AuditedEntity.cs:31,41`，`User.LastLoginTime` `:78`，`UserToken.ExpirationDate` `:45`），且多处显式使用 `DateTimeOffset.UtcNow`。这避免了时区 bug 的一大类来源，值得在新架构中强制延续（配合发现 9 的时区配置项）。

---

## 缺失项

按"生产级模板必须有而现在完全没有"的优先级排列：

1. **没有任何测试项目、测试用例或测试基础设施。** 全仓无 `*.Tests`、`*.UnitTests`、`*.IntegrationTests` 项目；无 `xunit`/`nunit`/`mstest` 引用。一个 `dotnet new` 模板不附带测试，使用者会继承零测试。这是**模板最大的单点缺陷**。
2. **没有自动审计字段填充。** `AuditedEntity.CreatedBy/UpdatedBy` 默认为 `0`（`Domain\NexusStack.EFCore\Entities\AuditedEntity.cs:36,46`），只有 `OperationLogService` 手工传 `userId`（`Services\SystemManagement\OperationLogService.cs:41-42`）。缺少 `SaveChangesInterceptor` 统一写入 `CreatedAt/CreatedBy/UpdatedAt/UpdatedBy`，导致任何忘记赋值的实体静默留下 `CreatedBy=0`。
3. **没有配置校验（ValidateOnStart / DataAnnotations / IValidateOptions）。** `Options\AliyunOSSOption.cs:24-32` 的 `AccessKeyId/AccessKeySecret/BucketName` 全为可空无校验；`Options\StorageOptions.cs:21-26` 的 `Path/TempPath` 同样。发现 5 已说明绑定失败是静默的。
4. **没有健康检查端点（`AddHealthChecks`/`MapHealthChecks`）。** 无 `/health`、`/ready`、`/live`。K8s 部署无法配置探针。
5. **没有可观测性接入（OpenTelemetry / 指标 / 分布式追踪）。** 唯一相关的是 `Filters\RequestAsyncResultFilter.cs:31-34` 写入 `X-TraceId`，依赖 `Activity.Current` 存在——但没有注册任何 `ActivitySource`，所以该头在生产几乎恒不写入。`AddHttpLogging`（`ServiceCollectionExtensions.cs:124-130`）设置为 `RequestBodyLogLimit = 1024*1024` + `LoggingFields.All`，会把**每个请求的完整 body（含密码）**写进日志，而没有任何字段脱敏。
6. **没有限流（Rate Limiting）与幂等（Idempotency-Key）中间件。** 这两项对"生产级模板"是硬需求，尤其在微服务化后。
7. **没有自动数据库迁移。** 有一个手工脚本 `SqlMigration\InitDatabase.sql`（全仓唯一的 schema 权威），但无 EF Core `Migrations/` 目录、无 `dotnet ef` 调用、无启动时 `Migrate()`/`MigrateAsync()`。本项目区域内两个 `HostedServices`（`ExecuteSeedDataService`、`InitApiResourceService`）都只灌**数据**，不管结构。结论：schema 演进路径是"人肉维护一个 SQL 文件"，与 23 个实体（`Domain\NexusStack.Core\Entities\**`）的 C# 定义**没有任何自动一致性保障**——改实体忘改 SQL 不会被任何检查发现。
8. **没有多租户 / 数据权限的执行机制。** `Permission.DataRange`（`Entities\Users\Permission.cs:27`，枚举定义 `:43`）建模了数据范围，`UserContextCacheDto.RegionIds`（`Dtos\Users\UserContextCacheDto.cs:33`）也缓存了用户所属区域——但**没有任何全局查询过滤器或仓储层拦截器实际用它**。`ICurrentUser.RegionIds` 的唯一消费方是 `CurrentUser.cs:102` 自身。这是"建了模但没接线"的典型。
9. **没有软删除的自动查询过滤器。** `ServiceBase.BatchSoftDeleteAsync`（`Domain\NexusStack.EFCore\Repository\ServiceBase.cs:80-93`）与 `GetQueryableIgnoreQueryFilters`（`:70`）暗示存在 `IsDeleted` 拦截器，但 `DeleteAsync` 依赖 `typeof(TEntity).IsAssignableTo(typeof(ISoftDelete))` 运行期判断（`:139`），`AuditedEntity` 本身不实现 `ISoftDelete`——哪些实体可软删完全靠继承关系隐式决定，且种子数据每个都手工写 `exists.IsDeleted = false`（`SeedData\UserSeedData.cs:83` 等 5 处）。
10. **没有 API 版本管理。** `ApiControllerBase` 的路由固定为 `Route("api/[controller]")`（`Core\ApiControllerBase.cs:16`），无版本段。微服务化后 `RoutePattern` 作为权限键（发现 6）会与版本策略冲突。
11. **没有 Outbox / Inbox 模式。** `AsyncTaskService.CreateTaskAsync`（`Services\AsyncTasks\AsyncTaskService.cs:43-56`）先 `InsertAsync` 再 `PublishAsync`：进程在两步之间崩溃 → 任务行永远 `Pending` 且消息从未发出。**无补偿、无扫描器**。`RetryAsync`（`:76-88`）用 `messageIdForRetry = $"{task.Code}:{task.Id}:retry:{Guid.NewGuid():N}"` 绕过消费端幂等检查——注释坦承（`:74`），但这意味着**重试路径放弃了幂等保证**。
12. **没有 `ICurrentUser` 的测试替身 / 无 `HttpContext` 的抽象。** `CurrentUser` 直接依赖 `IHttpContextAccessor`（`CurrentUser.cs:51-56`），`RoleIds` 依赖 `HttpContext.Items`（`:104-109`）。后台任务、控制台工具、测试中"当前用户"无法表达。
13. **没有统一错误码体系。** `Exceptions\` 下 4 个异常类型（`BusinessException`/`ErrorCodeException`/`ForbiddenException`/`UnauthorizedException`）中，`BusinessException` 只有消息字符串（`BusinessException.cs:12`），无错误码；`ErrorCodeException` 用 `-1` 表示"文件不存在"（`LocalFileStorage.cs:41,57`）等。客户端无法按码分支。
14. **没有 `Directory.Build.props` / `global.json` / 集中包版本管理。** 每个 `.csproj` 各自声明版本（`NexusStack.Core.csproj:16-27`、`NexusStack.Infrastructure.csproj:14-28`），`Snowflake.Core` 在两者中重复声明。`Microsoft.AspNetCore.Mvc` **2.3.13**（`Infrastructure\NexusStack.Infrastructure.csproj:16`）是一个 .NET Core 2.x 时代的包被引入 net10.0 项目——`Infrastructure\Models\RequestJsonResult.cs:11` 继承的 `JsonResult` 来自这个包，与 ASP.NET Core 10 的同名类型存在混淆风险。
15. **没有 CI 配置。** 4 个 Host/BackgroundService 项目各自带 `Dockerfile`（`Host\NexusStack.WebAPI\Dockerfile`、`Host\NexusStack.Gateway\Dockerfile`、`BackgroundServices\NexusStack.MQService\Dockerfile`、`BackgroundServices\NexusStack.PlanTaskService\Dockerfile`）且有 `.dockerignore`，容器化是有的；但 `.github\` 为空目录——**没有任何流水线**，因此本报告发现 1–20 中的问题不会被任何自动化检查拦住。结合发现 17 的 `RSACryptoServiceProvider.FromXmlString`（Linux 上抛 `PlatformNotSupportedException`）与发现 9 的 `TimeZoneInfo.Local`（容器 `TZ=UTC` 时 cron 语义静默改变），"能在本地 Windows 跑通、在 Linux 容器里行为不同"的路径没有任何守卫。

---

## 风险清单

| 严重度 | 问题 | 位置 |
|---|---|---|
| 严重 | 四个可部署单元全部 `ProjectReference NexusStack.Core`，而 Core 传递引入全部 7 个技术库 → 网关进程被迫携带 EF Core/阿里云 OSS/SkiaSharp/FFmpeg/Excel 等全部依赖；30+ 业务服务在网关进程也被注册 | 四个 `.csproj` 各自 `<ProjectReference Include="..\..\Domain\NexusStack.Core\NexusStack.Core.csproj" />`；`Domain\NexusStack.Core\NexusStack.Core.csproj:30-38`；`ServiceCollectionExtensions.cs:151-153` |
| 严重 | `CoreServiceType` 枚举把"我是什么服务"编码为运行时配置而非程序集边界，跨服务误用可编译通过；四个 `Program.cs` 仅 11 行，无独立组合根 | `Infrastructure\Enums\PlatformType.cs:81-99`；`BackgroundServices\NexusStack.MQService\Program.cs`（全文 11 行）；`Domain\NexusStack.Core\ServiceCollectionExtensions.cs:169-223,257-268,313-322,335-412` |
| 严重 | 种子数据硬编码管理员密码/手机/邮箱，且**每次启动强制重置密码**（`ConfigPath` 全为 `string.Empty` → `GetExecuteStatus` 恒返回 true） | `Domain\NexusStack.Core\SeedData\UserSeedData.cs:28-59,71-85`；`HostedServices\ExecuteSeedDataService.cs:46-75,127`；8 个种子类 `ConfigPath = string.Empty` |
| 严重 | 实体构造依赖静态 `App.ServiceProvider` 解析雪花 WorkerId；未 `App.Init` 时 `new User()` 抛异常 | `Domain\NexusStack.EFCore\Entities\AuditedEntity.cs:10-13`；`Infrastructure\SnowFlake\SnowFlake.cs:17-24`；`Infrastructure\App.cs:29-32`；`Domain\NexusStack.Core\ServiceCollectionExtensions.cs:280` |
| 严重 | WebAPI 模式不启动 MQ 消费端 → 操作日志与异常日志**发布后无人消费，静默丢失** | `Domain\NexusStack.Core\ServiceCollectionExtensions.cs:252,313-317`；`Filters\OperationLogActionFilter.cs:85`；`Filters\ApiAsyncExceptionFilter.cs:180`；`EventHandler\OperationLogEventHandler.cs:12` |
| 严重 | 登录无验证码校验、无失败锁定、无速率限制；错误信息可枚举用户名 | `Domain\NexusStack.Core\Services\Users\UserTokenService.cs:87-131`（`:99,104,115` 三种不同消息）；`ValidateCaptchaAsync`（`:67`）无调用方 |
| 严重 | Refresh Token 明文存储 + 检查与作废间隔 3 个 await 且无并发保护 → 可重放 | `Domain\NexusStack.Core\Services\Users\UserTokenService.cs:229-255`；`Entities\Users\UserToken.cs:40`（无 `RefreshTokenHash`） |
| 严重 | 未获分布式锁的副本在 `finally` 中删除持有者的锁 → 多副本并发执行种子数据 | `Domain\NexusStack.Core\HostedServices\ExecuteSeedDataService.cs:125,145` |
| 严重 | `ExceptionHandlerMiddleware` 对非 `AuthenticationFailureException` 异常只记日志不写响应 → 客户端收到空 body | `Domain\NexusStack.Core\Middlewares\ExceptionHandlerMiddleware.cs:22-41` |
| 严重 | `AppDomain.GetAssemblies()` + `StartsWith("NexusStack.")` 决定所有 DI 注册；重命名程序集即静默丢失全部服务注册 | `Infrastructure\TypeFinders\TypeFinders.cs:74`；`Domain\NexusStack.Core\ServiceCollectionExtensions.cs:151-153,340-344,467,504,566` |
| 高 | 异常 `Message` 与全部内部异常消息原样返回客户端（含表名/列名/主机名） | `Domain\NexusStack.Core\Filters\ApiAsyncExceptionFilter.cs:64,67-71`；`Infrastructure\Utils\EncryptionHelp.cs:84,107,130,188`（同类模式） |
| 高 | `SerializeUnknownFileTypes = true` + 上传无扩展名白名单 + 上传目录同源 → 存储型 XSS | `Domain\NexusStack.Core\ServiceCollectionExtensions.cs:439-444`；`Services\SystemManagement\FileService.cs:130-231` |
| 高 | `KnownProxies.Clear()` + `KnownIPNetworks.Clear()` → `X-Forwarded-For` 可伪造，审计日志中的 IP 不可信 | `Domain\NexusStack.Core\ServiceCollectionExtensions.cs:427-435`；消费点 `Services\SystemManagement\OperationLogService.cs:33`、`Entities\Users\UserToken.cs:51` |
| 高 | `IFileStorage` 14 个成员中两实现互不覆盖；`AliyunFileStorage.GetAbsolutePath` / `GetAsync` 抛 `NotImplementedException`，而 `FileService` 在视频路径调用它 → 上传视频必崩 | `Infrastructure\FileStroage\IFileStorage.cs:14-95`；`AliyunFileStorage.cs:134-148,145`；`LocalFileStorage.cs:46-49,92-130`；`Services\SystemManagement\FileService.cs:204` |
| 高 | `FileStorageFactory.GetStorage()` 在 `using` scope 内解析实例后返回 → 作用域逃逸 | `Infrastructure\FileStroage\FileStorageFactory.cs:21-30` |
| 高 | `CronScheduleService` 分布式锁 TTL 固定 1 分钟且从不释放，与任务耗时脱钩 → 长任务可被并发执行 | `Domain\NexusStack.Core\Schedules\CronScheduleService.cs:116,136` |
| 高 | 每个 Cron 任务每秒新建 scope 并解析 5 个 scoped 服务、读一次 Redis | `Domain\NexusStack.Core\Schedules\CronScheduleService.cs:90-114` |
| 高 | `FileHelper.SaveFile` 忽略 `Stream.Read` 返回值并恒写 1024 字节 → 文件静默损坏 / 小文件被填充 / 1KB 整数倍尾部多 1KB 垃圾 | `Infrastructure\Utils\FileHelper.cs:24-43` |
| 高 | 网关配置并发锁是进程内 `SemaphoreSlim`，多副本下丢失更新；控制器 8 处 `Release()` 不在 `finally` 中 | `Domain\NexusStack.Core\Gateway\ProxyConfigLockService.cs:13`；`Host\NexusStack.Gateway\Controllers\ProxyManagementController.cs:55,70,82,97,104,120,154,170,182,198,205,221` |
| 高 | `DownloadService` 导出类型表为空数组 → 任何导出请求抛 `ArgumentException` | `Domain\NexusStack.Core\Services\SystemManagement\DownloadService.cs:28-44` |
| 高 | `GlobalSettingService` 4 个公开方法 `return null`，真实实现被注释；经响应包装变成 `Success=false "未请求到数据"` | `Domain\NexusStack.Core\Services\SystemManagement\GlobalSettingService.cs:73-103`；`Host\NexusStack.WebAPI\Controllers\GlobalSettingController.cs:14` |
| 高 | `RSACryptoServiceProvider.FromXmlString` 在 .NET 10 / Linux 上抛 `PlatformNotSupportedException`；`MD5CryptoServiceProvider` 已过时 | `Infrastructure\Utils\EncryptionHelp.cs:128-133,34` |
| 高 | `AsyncTaskService` 先落库再发消息（无 Outbox）；重试路径用新 MessageId 明确绕过消费端幂等 | `Domain\NexusStack.Core\Services\AsyncTasks\AsyncTaskService.cs:47-54,76-88` |
| 中 | 验证码字符来自 `System.Random`（非密码学随机）；`SKTypeface.FromFile` 每次请求读盘 | `Infrastructure\Utils\Randomizer.cs:52`；`Services\Users\UserTokenService.cs:48`；`Infrastructure\Captcha\CaptchaHelper.cs:31,43,104` |
| 中 | `ChangeRolePermissionAsync` 跨 7 表 / 大批量写；`InvalidateAsync` 串行遍历枚举含哨兵值 `All=0`，调用方再套一层用户循环 | `Domain\NexusStack.Core\Services\Users\UserContextCacheService.cs:70-131,66-67`；`Services\Users\PermissionService.cs:120-127` |
| 中 | `GetRolePermissionAsync(List<long>)` 每角色一次大查询（N+1）+ 内存 O(N²) 建树；空角色列表抛 `BusinessException` 与单角色重载语义矛盾 | `Domain\NexusStack.Core\Services\Users\PermissionService.cs:196-200,204-225,255-271`；`Services\Users\UserContextCacheService.cs:235-238`（对照） |
| 中 | `EnumService.EnumToList<T>` 对无 `[Description]` 的枚举抛 `IndexOutOfRangeException` | `Domain\NexusStack.Core\Services\SystemManagement\EnumService.cs:18-23`；触发枚举 `Infrastructure\Enums\GlobalSettingKey.cs:7-11`、`Enums\FileEnums.cs:11-31` |
| 中 | `OperationLogService.ExportLogAsync` 按 `CreatedBy`（操作人 ID）排序而非 `CreatedAt` | `Domain\NexusStack.Core\Services\SystemManagement\OperationLogService.cs:129` |
| 中 | `JudgeHasPermissionAsync` 在菜单或接口不存在时抛 `NullReferenceException` | `Domain\NexusStack.Core\Services\Users\PermissionService.cs:276-283` |
| 中 | `FileService.UploadAsync(Stream,…)` 忽略 `ReadAsync` 返回值，且要求可寻址流（`stream.Length`/`Position`） | `Domain\NexusStack.Core\Services\SystemManagement\FileService.cs:124-126` |
| 中 | `Debugger.IsAttached` 改变文件下载代码路径（本地调试 ≠ 生产行为） | `Domain\NexusStack.Core\Services\SystemManagement\FileService.cs:85-99` |
| 中 | `ExecuteSeedDataService` 用 `break` 处理被禁用的种子 → 中断其后所有种子 | `Domain\NexusStack.Core\HostedServices\ExecuteSeedDataService.cs:104-107` |
| 中 | `PublishAsync` 在请求管线中同步 await（日志/异常路径增加 MQ 往返，MQ 故障即请求失败） | `Domain\NexusStack.Core\Filters\OperationLogActionFilter.cs:85`；`Filters\ApiAsyncExceptionFilter.cs:180` |
| 中 | `AddHttpLogging` 记录全部字段含完整请求体（最大 1MB），密码入日志且无脱敏 | `Domain\NexusStack.Core\ServiceCollectionExtensions.cs:124-130` |
| 中 | `RequestAsyncResultFilter` 注入但完全未使用的两个依赖；`UseApp` 中 `AddRabbitMQCodeManager()` 对所有服务类型无条件调用 | `Domain\NexusStack.Core\Filters\RequestAsyncResultFilter.cs:20`；`ServiceCollectionExtensions.cs:324` |
| 中 | `IProxyConfigStore` 仅有 1 个实现（投机性抽象）；`GetRouteAsync`/`GetClusterAsync` 是单行转发且无调用方 | `Domain\NexusStack.Core\Gateway\IProxyConfigStore.cs:11-12`；`JsonProxyConfigStore.cs:202-212`；`ServiceCollectionExtensions.cs:215` |
| 中 | `RequestAuthenticationHandler`（98 行开放 API 签名认证）为死代码，且使用 obsolete 的 `ISystemClock`；`CustomerAuthenticationSchemeOptions` 无引用 | `Domain\NexusStack.Core\Authentication\RequestAuthenticationHandler.cs:21-98`；`RequestAuthenticationSchemeOptions.cs:11-14,32-35`；`ServiceCollectionExtensions.cs:175,205`（注释） |
| 中 | `SnowFlake.SalesOrderInstance`/`ReturnOrderInstance`、`SignalRHubAttribute`（无反射读取方）为死代码，误导架构理解 | `Infrastructure\SnowFlake\SnowFlake.cs:30-48`；`Infrastructure\Attributes\SignalRHubAttribute.cs:7-21`；`SignalR\NotificationHub.cs:9` 对照 `ServiceCollectionExtensions.cs:315` |
| 中 | `CollectExtensions` 大部分 API 依赖 `DataTable`/`DataRow` 且无调用方；`GetRedisKeys()` 无条件返回 `false` | `Infrastructure\Utils\CollectExtensions.cs:17-21,28-96` |
| 中 | 上传文件唯一名仅到毫秒 → 同毫秒同名文件覆盖 | `Infrastructure\Utils\StringExtensions.cs:152-159`；`AliyunFileStorage.cs:36` |
| 中 | `Configuration` 中 `reloadOnChange: false` 与 `App.Options` 注释"可获取最新配置"矛盾；AgileConfig 动态配置对 `IOptionsSnapshot` 无效 | `Domain\NexusStack.Core\ServiceCollectionExtensions.cs:349-350`；`Infrastructure\App.cs:35` |
| 中 | `Permission.DataRange` 与 `ICurrentUser.RegionIds` 已建模但**无任何查询过滤器/拦截器消费** → 数据权限不生效 | `Entities\Users\Permission.cs`（`DataRange`）；`Infrastructure\ICurrentUser.cs:18`；`Domain\NexusStack.Core\Dtos\Users\UserContextCacheDto.cs:33`；唯一消费方 `CurrentUser.cs:102` |
| 中 | `Microsoft.AspNetCore.Mvc` 2.3.13（.NET Core 2.x 时代包）被 net10.0 基础设施项目引用，`RequestJsonResult` 继承其 `JsonResult` | `Infrastructure\NexusStack.Infrastructure.csproj:16`；`Infrastructure\Models\RequestJsonResult.cs:11` |
| 低 | `IApiResrouceCoreService` / `ApiResrouceCoreService` 拼写错误（Resource）进入公共 API | `Domain\NexusStack.Core\Services\Interfaces\IApiResrouceCoreService.cs:10`；`Services\SystemManagement\ApiResrouceCoreService.cs:15`；`HostedServices\InitApiResourceService.cs:44` |
| 低 | `public static` 非 readonly 的 ID 生成器可被任意替换；`ISeedData.ConfigPath` 接口成员在 8 个实现中语义不一致 | `Infrastructure\SnowFlake\SnowFlake.cs:24,36,48`；`Domain\NexusStack.Core\ISeedData.cs:21` |
| 低 | `Mark` 类杂乱：`toBigIntList` 小写命名且 `Split(".")` 后 `Convert.ToInt64` 会抛；`ToShopIdString` 等门店业务成员混在通用字符串扩展中 | `Infrastructure\Utils\StringExtensions.cs:146-151,181-210` |
| 低 | `SystemRoleConstants` 与 `CoreRedisConstants` 均为 `public static` 可变字段（非 `const`/`readonly`）；`ScheduleTaskCache` 键**不含程序集前缀**，与其他键命名不一致 | `Infrastructure\Constants\CoreRedisConstants.cs:15,20-45`；`Constants\SystemRoleConstants.cs:15,19` |
| 低 | `OperationLog` 中 `OperationMenu` 与 `MenuCode` 写入同一值；`Remark = json` 被注释而内容混入 `OperationContent` | `Domain\NexusStack.Core\Services\SystemManagement\OperationLogService.cs:34,35,37,38` |
| 低 | `RegionService` 中 `existsNmae` 变量（拼写错误）已计算但校验被注释 → 行政区划重名不拦截 | `Domain\NexusStack.Core\Services\SystemManagement\RegionService.cs:52-56` |
| 低 | `CaptchaHelper.cs` 引入未使用的 `System.Drawing` 与 `static …MediaTypeNames`；`App.cs` 无 XML 文档注解被反射读取；`DownloadService`/`FileService` 引入未使用的 `LinqKit` | `Infrastructure\Captcha\CaptchaHelper.cs:4,8`；`Services\SystemManagement\DownloadService.cs:3`；`Services\SystemManagement\FileService.cs:25` |

---

## 交付给重建的三条硬性前置动作

1. **删除 `App.cs` + `TypeFinders.cs` + `Infrastructure\ServiceCollectionExtensions.cs` 的程序集前缀扫描**，改为显式注册 + `ValidateOnStart`。这三者是本区域所有"隐式契约"的根源（发现 2、5）。
2. **先决定 `Identity` 的边界**，因为鉴权上下文构建（`UserContextCacheService.BuildFromDbAsync`）横跨 7 张表，是唯一无法靠改名切分的地方（发现 22）。
3. **`RequestResultModel` 响应契约 + 异常处理收敛到一处**，并在收敛前先补上 `ExceptionHandlerMiddleware` 的兜底分支（发现 11）——否则微服务化后 4 个进程会各自演化出 4 套错误格式。
