# 02 数据与持久化评审

评审批次：NexusStack 模板 v2 重建前置评审（第 2 区：`Domain\NexusStack.EFCore\**` + `SqlMigration\**`）
评审方式：全量逐文件精读 + 与 `Host\NexusStack.WebAPI\Migrations\**`（迁移与模型快照）、`Domain\NexusStack.Core\Entities|Services|SeedData`（实体与调用方）交叉比对。仓库只读，未做任何修改。

---

## 概览

### 区域清点（路径 → 实际职责）

| 文件 | 行数 | 真实职责 |
|---|---|---|
| `Domain\NexusStack.EFCore\DbContexts\MainContext.cs` | 55 | 唯一的 `DbContext`。**零 `DbSet` 属性**（`:11-53`）；三个 `OnConfiguring`/`ConfigureConventions` 重写全是空壳（`:22-34`），真实配置只有 `OnModelCreating` 的 4 行（`:45-52`） |
| `DbContexts\ModelBuilderExtensions.cs` | 121 | 反射注册实体（`:21-44`）、软删除全局过滤器（`:53-66`）、全库外键改 `Restrict`（`:73-83`）、从 XML 文档生成表/列注释（`:89-118`） |
| `Entities\IEntity.cs` / `ISoftDelete.cs` / `IAuditedEntity.cs` | 22/17/32 | 三个纯数据契约接口，其中 `IEntity` 是**空接口**（`:10-13`），只作为"要被注册进模型"的标记 |
| `Entities\Entity.cs` | 41 | `Entity`（`:12-18`）与 `Entity<TKey>`（`:24-40`）。主键在**构造函数里用雪花算法生成**（`:15`） |
| `Entities\EntityBase.cs` | 34 | 追加 `IsDeleted`（`:32`），继承 `ISoftDelete`（`:22`） |
| `Entities\AuditedEntity.cs` | 53 | 追加 `CreatedAt/CreatedBy/UpdatedAt/UpdatedBy/Remark`（`:31-51`），`CreatedBy/UpdatedBy` 默认值是 **0 而非 null**（`:36,46`） |
| `MainSaveChangeInterceptor.cs` | 116 | `SaveChangesInterceptor`：软删除改写（`:58-72`）+ 审计字段填充（`:78-114`）。**每次 `SaveChanges` 创建一次 DI scope**（`:84`） |
| `Mapping\IMappingConfiguration.cs` | 18 | 映射配置契约，`RegisterFromAssembly` 靠它反射发现配置类（`ModelBuilderExtensions.cs:25-35`） |
| `Mapping\MapBase.cs` | 36 | 映射基类，默认 `HasKey(a => a.Id)`（`:30-34`） |
| `MigrationsSqlGenerator.cs` | 34 | **继承 `NpgsqlMigrationsSqlGenerator`**（`:12`），构造依赖 `INpgsqlSingletonOptions`（`:15`）；把 `CreateTableOperation` 的外键全删（`:21-25`） |
| `ServiceCollectionExtensions.cs` | 69 | 唯一注册入口 `AddEFCoreAndPostgreSQL`（`:18-56`）：取连接串（`:21`）、挂拦截器（`:29`）、剥外键（`:32`）、**全局 `NoTracking`**（`:35`）、`UseNpgsql`（`:38-46`）、`poolSize: 1024`（`:49`）、注册 `IServiceBase<,> → ServiceBase<,>`（`:52-53`） |
| `Repository\Query\IQueryRepository.cs` | 297 | **36 个公开成员**（含 11 组重载），把 `IQueryable<T>`、`IIncludableQueryable<T,object>`、`ISpecification<T>` 全暴露出去 |
| `Repository\Query\QueryRepository.cs` | 450 | 上述 36 个成员的实现，纯 `DbContext` 转发 |
| `Repository\Query\SqlQueryExtensions.cs` | 177 | 原生 SQL → `List<T>`；`:20-77` 反射填充 + 手动开连接（**不关**）；`:79-175` 两个私有方法用 EF 内部 API，**无调用方（死代码）** |
| `Repository\Base\IRepositoryBase.cs` / `RepositoryBase.cs` | 109/119 | 追加 12 个成员：`InsertAsync/UpdateAsync/DeleteAsync/ExecuteSqlCommandAsync×3/BeginTransactionAsync/RollbackAsync/ResetContextState`。**每个方法内部各自 `SaveChangesAsync`**（`:34,44,52,60,68,76`） |
| `Repository\Mapping\IMappingRepository.cs` / `MappingRepository.cs` | 43/169 | 追加 9 个 Mapster `ProjectToType<T>` 投影方法，含 2 个 `ExpressionStarter<T>` 重载（`:159-167`，**全仓零调用**） |
| `Repository\IServiceBase.cs` / `ServiceBase.cs` | 69/297 | 追加 `InsertOrUpdateAsync`、批量增删改（Z.EntityFramework.Extensions）、`GetDbContext` 公开属性（`IServiceBase.cs:67`）、`ExecutePagedStoredProcedureAsync(params NpgsqlParameter[])`（`ServiceBase.cs:156-160`）、`FillTableByReader`（`:221-295`） |
| `Repository\Specifications.cs` / `SpecificationExtensions.cs` | 27/16 | `Specifications<T>.Create()` 工厂；后者**忽略调用者、永远返回空规格**（`:10-15`），是一个 API 陷阱 |
| `SqlMigration\InitDatabase.sql` | 722 | EF 生成的 PostgreSQL 建库脚本：22 张表（`:8-693`）、11 个索引（`:696-716`）、**0 个外键**、0 个唯一约束、0 个 CHECK，`__EFMigrationsHistory` 里硬编码 `'20260307033208_InitialDatabase'/'10.0.7'`（`:718-719`） |

**数量事实**：整个数据层 1 个 `DbContext`、22 个实体（映射 22 张表）、**2 个映射配置类**（`Domain\NexusStack.Core\Mapping\UserMapping.cs`、`ScheduleTaskMapping.cs`，合计 41 行）、1 个拦截器、0 个种子（种子在 `Domain\NexusStack.Core\SeedData\` 的 8 个 `ISeedData` 里）、1 套迁移（在 `Host\NexusStack.WebAPI\Migrations\`，不在数据层项目内）。

**接口 vs 实现的体量比**：接口 518 行 / 实现约 1212 行（`QueryRepository` 450 + `RepositoryBase` 119 + `MappingRepository` 169 + `ServiceBase` 297 + `SqlQueryExtensions` 177），一个具体业务服务可用的公开成员约 **64 个**，其中 **10 个全仓零调用**（`HeadDeleteAsync`、`ExecutePagedStoredProcedureAsync`、`GetFromRawSqlAsync×3`、`ExecuteSqlCommandAsync×3`、`FillTableByReader`、`GetConnection()`、`GetIgnoreQueryFiltersExpandable`）——典型的"宽而浅"。

---

## 发现

### 1. 「EF Core 多数据库支持（PostgreSQL / MySQL / SQL Server）」在代码里完全不存在

**证据**

README 的参数表把它列为三选一：

```markdown
README.md:158  | `--DatabaseProvider` | choice | PostgreSQL | PostgreSQL/MySQL/SqlServer |
README.md:120    --DatabaseProvider MySQL \
```

而模板定义里 `DatabaseProvider` **只有一个取值**，并且它的 switch 两个分支都指向同一个方法：

```json
.template.config\template.json:61-72   "choices": [ { "choice": "PostgreSQL", ... } ], "defaultValue": "PostgreSQL"
.template.config\template.json:80      { "condition": "(DatabaseProvider == 'PostgreSQL')", "value": "AddEFCoreAndPostgreSQL" },
.template.config\template.json:81      { "condition": "true", "value": "AddEFCoreAndPostgreSQL" }
```

代码侧只有一条路径，且被注释掉的"MySQL 版本"**没有任何定义**（全仓 grep `AddEFCoreAndMySql` 只命中这一行注释，解注释即编译失败）：

```csharp
Domain\NexusStack.Core\ServiceCollectionExtensions.cs:143   //builder.Services.AddEFCoreAndMySql(builder.Configuration);
Domain\NexusStack.Core\ServiceCollectionExtensions.cs:144   builder.Services.AddEFCoreAndPostgreSQL(builder.Configuration);
```

提供程序耦合是**编译期**的，不是配置期：

```csharp
Domain\NexusStack.EFCore\MigrationsSqlGenerator.cs:12-19   class MigrationsSqlGenerator : NpgsqlMigrationsSqlGenerator
                                                            ctor(MigrationsSqlGeneratorDependencies, INpgsqlSingletonOptions)
Domain\NexusStack.EFCore\Repository\ServiceBase.cs:156-160  ExecutePagedStoredProcedureAsync<T>(..., params NpgsqlParameter[] parameters)
Domain\NexusStack.EFCore\Repository\ServiceBase.cs:252      if (rdr[i].GetType().Name == "DateTime" || rdr[i].GetType().Name == "MySqlDateTime")
Domain\NexusStack.EFCore\ServiceCollectionExtensions.cs:38  options.UseNpgsql(connectionString, pgOptions => ...)
Domain\NexusStack.EFCore\ServiceCollectionExtensions.cs:42  pgOptions.EnableRetryOnFailure();
Domain\NexusStack.EFCore\NexusStack.EFCore.csproj:16        <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.3" />
```

项目文件里**没有任何** MySQL / SQL Server 提供程序包（全仓 grep `UseMySql|UseSqlServer|Pomelo` 仅命中 `ServiceBase.cs:252` 的 `MySqlDateTime` 字符串比较）。README 同表里的 `--EnableSignalR / --EnableRabbitMQ / --EnableRedis`（`README.md:154-156`）在 `template.json` 中也不存在。

**影响**

- 这是一条**对外承诺与实现不符**的卖点。用户按 README 执行 `--DatabaseProvider MySQL` 会直接被模板引擎拒绝（参数没有该 choice）；即使绕过，代码里也没有第二条 SQL 生成器路径。
- 迁移文件是 PostgreSQL 单套（见发现 2），`InitDatabase.sql` 通篇 PG 方言：`GENERATED BY DEFAULT AS IDENTITY`、`timestamp with time zone`、`COMMENT ON`、`character varying`、双引号标识符（`InitDatabase.sql:9,19,26,43-58`）。切库必须重写 DDL 与生成器，不是改配置。
- `DateTimeOffset`（`AuditedEntity.cs:31,41`）在 PG 上是 `timestamp with time zone`（`InitDatabase.sql:19,21`），在 MySQL 上退化为无时区的 `datetime(6)`——**换库会静默改变审计时间的语义**。
- `spec.Query.Search(...)`（`MenuService.cs:91-92`、`RegionService.cs:86-88`）最终落到 `LIKE`，PG 区分大小写、SQL Server 默认排序规则不区分——**同一个搜索接口在不同库上行为不同**。

**建议**

- 重建时把"多提供程序"降级为"一个提供程序 + 一层薄适配"：`DbContext` 与迁移按服务固定提供程序；真要多库，用 **每提供程序一套迁移程序集**（`MigrationsAssembly` 每库一个）+ 提供程序无关的表达式（禁用 `NpgsqlParameter` 进出公共 API）。
- 模板参数要么实现（含 MySQL/SqlServer 的 csproj 条件、迁移、DDL），要么从 README 与 `template.json` 中删除。**不要保留"文档有、代码无"的第三条路**。

---

### 2. 迁移只有一套，且同一份 schema 有三份产物、三个版本号、且没有任何执行路径

**证据**

```csharp
Domain\NexusStack.EFCore\ServiceCollectionExtensions.cs:40   pgOptions.MigrationsAssembly("NexusStack.WebAPI");
```

- 迁移实体：`Host\NexusStack.WebAPI\Migrations\20260307033208_InitialDatabase.cs`（760 行）+ `.Designer.cs` + `MainContextModelSnapshot.cs`。**只此一套**，且归属在 Host 项目里——数据层项目自己不能生成/拥有迁移。
- 版本号互相矛盾：快照 `ProductVersion = "10.0.3"`（`MainContextModelSnapshot.cs` 内），迁移脚本 `'10.0.7'`（`InitDatabase.sql:719`），而 `NexusStack.EFCore.csproj:14-16` 引用的是 EF Core / Relational `10.0.12` + Npgsql `10.0.3`。
- `InitDatabase.sql` 是 EF 迁移脚本的产物（含 `__EFMigrationsHistory` 建表与 INSERT），却被当成"初始化 SQL"签入 `SqlMigration\`，形成**第二事实源**：它与迁移文件之间没有任何 CI 校验。
- **全仓没有任何 `Database.Migrate()` / `MigrateAsync()` / `EnsureCreated()`**（grep 仅命中 `ServiceCollectionExtensions.cs:18/21/144` 无关行）。迁移既不由应用执行，也没有 CI/CD 步骤引用 `InitDatabase.sql`。

**影响**

- 生产建库只能靠人手动跑 `InitDatabase.sql` 或 `dotnet ef database update`，而 `InitDatabase.sql` 里写死的迁移 ID/版本一旦与后续迁移不匹配，`__EFMigrationsHistory` 就会与实际结构脱节——EF 认为"已应用"，DBA 认为"已建库"，两边都对，结构却不同。
- 快照 `10.0.3` / 脚本 `10.0.7` / 包 `10.0.12` 说明**迁移是在旧 SDK 上生成的、之后没再重新生成**；下一次 `migrations add` 会产出一个混入历史漂移的迁移。
- 迁移在 Host 项目里，重建为微服务时每个服务要么共享这个迁移集（回到单库），要么把 `MigrationsAssembly` 指向自己——后者需要先解决模型发现的程序集前缀问题（发现 16）。

**建议**

- 迁移程序集归属**服务/上下文自己**（`MigrationsAssembly` 指向该服务的 Infrastructure 项目）；`SqlMigration\InitDatabase.sql` 改为 CI 里 `dotnet ef migrations script --idempotent` 生成，禁止手工签入并加一致性校验。
- 应用启动显式执行迁移（或独立的 migrate Job），并在 CI 里用"空库 + 迁移"和"旧库 + 迁移"两条路径验证。

---

### 3. 主键生成权有两个"权威"：构造函数里的雪花 ID 与数据库的 IDENTITY 列

**证据**

```csharp
Domain\NexusStack.EFCore\Entities\Entity.cs:12-18     public abstract class Entity : Entity<long>
                                                      { public Entity() : base(SnowFlake.Instance.NextId()) { } }
Domain\NexusStack.EFCore\Entities\AuditedEntity.cs:8-16  同上
```

而数据库侧 22 张表全部声明为自增：

```sql
SqlMigration\InitDatabase.sql:9    "Id" bigint GENERATED BY DEFAULT AS IDENTITY,
SqlMigration\InitDatabase.sql:44,77,110,141,172,203,236,277,302,351,386,421,454,475,498,541,564,589,610,633,656  同上
```

模型侧同样带自增注解（`MainContextModelSnapshot.cs` 中 23 处 `IdentityByDefaultColumn`、22 处 `ValueGeneratedOnAdd`；迁移里 `20260307033208_InitialDatabase.cs:19-20` 显式 `.Annotation("Npgsql:ValueGenerationStrategy", IdentityByDefaultColumn)`）。全仓**没有** `ValueGeneratedNever()`。且 `InitDatabase.sql` 里**没有任何 `ALTER TABLE` / `SEQUENCE` 语句**（22 个匹配全是 IDENTITY 行），也没有列级 `DEFAULT`。

同时，种子数据手工写死了主键：

```csharp
Domain\NexusStack.Core\SeedData\UserSeedData.cs:30    Id = 1,                     // admin
Domain\NexusStack.Core\SeedData\UserSeedData.cs:46    Id = 2029562151177424896,   // 雪花值
```

**影响**

- 两个生成者同时声明所有权：构造函数总是给出非 0 的雪花值（EF 对 `ValueGeneratedOnAdd` 的哨兵值是 `default(long)=0`，非 0 会显式写入 `INSERT`，`GENERATED BY **DEFAULT**` 允许显式值），于是**序列永远停在 1 不动**；一旦某个路径产出了 `Id = 0` 的实体（例如从 DTO 映射而来、或手工 `new` 后被 Mapster 覆盖），EF 会请数据库发号 → 拿到 `1` → 与 `UserSeedData` 的 admin（`Id=1`）在主键上**冲突**（同表内）。
- 反向风险同样存在：只要有人误把该列当成"由库生成"（例如加一个 `HasDefaultValue` 或换 provider），雪花 ID 会被丢弃，同一个服务在两种 ID 区间里各插一半数据，跨库合并/追溯时无法判断来源。
- 主键语义漂移直接污染业务字段：`Menu.IdSequences` 把主键拼成物化路径（`MenuService.cs:43,47`），ID 生成策略一变，全部路径失效。

**建议**

- 二选一并写进契约：要么 `builder.Property(x => x.Id).ValueGeneratedNever()` + 去掉 DDL 的 `IDENTITY`（推荐，微服务下客户端生成 ID 可离线、可跨库唯一），要么删除 `Entity` 构造函数里的雪花调用、改由库发号（此时 `long` 需要 BIGSERIAL/IDENTITY 且不能手工写 1）。
- 种子数据**禁止硬编码主键**，改为按业务键（`Code`/`UserName`）幂等 upsert。
- 加一条架构测试：断言模型里不存在 `ValueGeneratedOnAdd` 的主键，或断言所有 `IEntity.Id` 均由生成器赋值。

---

### 4. 实体基类的构造函数依赖静态服务定位器 —— 领域对象无法脱离进程创建

**证据**

```csharp
Domain\NexusStack.EFCore\Entities\Entity.cs:15        : base(SnowFlake.Instance.NextId())
Infrastructure\SnowFlake\SnowFlake.cs:17-24           private static readonly Lazy<IdWorker> _instance = new(() =>
                                                      { var commonOptions = App.Options<CommonOptions>(); return new IdWorker(...); });
Infrastructure\App.cs:27-37                           public static TOptions Options<TOptions>() { if (ServiceProvider is null) throw new Exception("使用前请先使用 App.Init() 方法初始化"); using var scope = ServiceProvider.CreateScope(); ... }
```

也就是说：`new User()` → `Entity()` → `SnowFlake.Instance` → 首次访问触发 `App.Options<CommonOptions>()` → 需要**已初始化的全局 `IServiceProvider` + 配置**（`WorkerId`/`DatacenterId` 来自 `Infrastructure\Options\CommonOptions.cs:23-28`；`App.cs:29-32` 在未初始化时直接抛异常）。该 scope 只在静态初始化时创建一次，此后每次 `new` 实体都会走静态字段 + `IdWorker.NextId()`。

**影响**

- 任何实体都**无法在单元测试里 `new`**（会抛"使用前请先使用 App.Init() 方法初始化"），也无法被 Dapper 读模型、Excel 导入器或**没有 DI 容器的消息反序列化**直接实例化——数据层与领域类型被永久绑在 Web 主机上。这是重建为 DDD 时**必须先拆掉**的一根钉子（与 01 报告第 2 节同一根，此处给出数据层结论）。
- `SnowFlake` 的 worker/datacenter 取值来自启动期配置，多副本部署若配置相同则**会产生重复 ID**（分布式 ID 的核心前提缺失；`CommonOptions.cs:23-28` 无取值范围校验）。

**建议**

- 实体回归 POCO：主键改为 `ValueGeneratedNever` + 由**应用层 ID 生成器服务**赋值（构造函数或工厂方法显式传入 `Id`），静态定位器只保留在组合根。
- 重建时给 ID 生成器一个显式的 `IIdGenerator` 端口与"workerId 从环境/租约分配"的实现，并加一个"两个进程不会拿到同一 workerId"的运行期校验。

---

### 5. 没有工作单元：每个写方法各自 `SaveChanges`；全仓只有一处显式事务；而重试策略与显式事务互相冲突

**证据**

```csharp
Domain\NexusStack.EFCore\Repository\Base\RepositoryBase.cs:29-37   InsertAsync → AddAsync + SaveChangesAsync
RepositoryBase.cs:39-45   InsertAsync(range) → AddRangeAsync + SaveChangesAsync
RepositoryBase.cs:47-61   UpdateAsync(×2) → Entities.Update(Range) + SaveChangesAsync
RepositoryBase.cs:63-77   DeleteAsync(×2) → Remove(Range) + SaveChangesAsync
```

全仓唯一的显式事务：

```csharp
Domain\NexusStack.Core\Services\Users\PermissionService.cs:98    using var trans = await BeginTransactionAsync();
PermissionService.cs:102-104                                     await dbContext.Set<Permission>().Where(...).DeleteFromQueryAsync();
PermissionService.cs:108                                         await InsertAsync(permissions);
PermissionService.cs:111-116                                     await trans.CommitAsync(); / await RollbackAsync(trans);
```

而 `BeginTransactionAsync` 用的是裸事务（`RepositoryBase.cs:23-27`），同时连接启用了重试策略：

```csharp
Domain\NexusStack.EFCore\ServiceCollectionExtensions.cs:42   pgOptions.EnableRetryOnFailure();
```

**影响**

- 一个用例需要写多张表时（例如"改角色权限 + 失效缓存 + 写审计"），只有两个选择：**每个仓储方法各自提交**（部分失败留下半截数据），或者拿到 `GetDbContext`（`IServiceBase.cs:67`）自己开事务。没有 UoW 抽象，事务边界是**调用者的自由裁量**——目前全仓只有 `PermissionService` 一处做了这件事（它的缓存失效还在事务提交之后、循环内逐个 `await`，见 `:120-127`）。
- EF Core 在配置了可重试执行策略时禁止用户自建事务：会抛 `InvalidOperationException: The configured execution strategy 'NpgsqlRetryingExecutionStrategy' does not support user-initiated transactions. Use the execution strategy returned by 'DbContext.Database.CreateExecutionStrategy()'...`（同类案例见 [discordbot#1887](https://github.com/cpike5/discordbot/issues/1887)、[Npgsql.Bulk#63](https://github.com/neisbut/Npgsql.Bulk/issues/63)）。**`PermissionService` 恰好是这条规则的受害者**——保留重试就必须改成 `CreateExecutionStrategy().ExecuteAsync(...)` 包裹事务，否则"改角色权限"在运行期直接抛异常。

**建议**

- 引入显式 UoW（或把 `SaveChanges` 从仓储里彻底拿掉，交由应用用例统一提交），并给"跨聚合原子写"一个唯一入口：`IUnitOfWork.ExecuteAsync(Func<CancellationToken, Task>)` 内部用 `CreateExecutionStrategy()` 包裹事务。
- 明确写出事务边界规则：一次 HTTP 请求 = 一个事务 = 一次提交；批量/长任务单独定义。

---

### 6. 仓储接口是 64 成员的浅穿透，EF 概念全部泄漏到调用方

**证据**

泄漏类型逐一列出（全部出现在公共接口上）：

```csharp
Domain\NexusStack.EFCore\Repository\Query\IQueryRepository.cs:26,34,40   IQueryable<TEntity> GetQueryable() / GetExpandable() / GetIgnoreQueryFiltersExpandable()
IQueryRepository.cs:42-46,59-65                                          Func<IQueryable<TEntity>, IIncludableQueryable<TEntity, object>> includes
IQueryRepository.cs:88-139                                               Task<TEntity> GetByIdAsync(object id, ..., bool asNoTracking, ...)
Domain\NexusStack.EFCore\Repository\Base\IRepositoryBase.cs:21          Task<IDbContextTransaction> BeginTransactionAsync(IsolationLevel ...)
IRepositoryBase.cs:105                                                   void ResetContextState();
Domain\NexusStack.EFCore\Repository\Mapping\IMappingRepository.cs:39,41  ExpressionStarter<TEntity>（LinqKit 类型出现在接口上）
Domain\NexusStack.EFCore\Repository\IServiceBase.cs:67                   MainContext GetDbContext { get; }     // 直接把 DbContext 递给调用方
ServiceBase.cs:156-160                                                   params NpgsqlParameter[] parameters   // 直接把 provider 类型递给调用方
IQueryRepository.cs:59-78,88-139                                         返回类型 X.PagedList 的 IPagedList<T>
```

纯转发的实现（示例）：

```csharp
QueryRepository.cs:53-56   public IQueryable<TEntity> GetQueryable() { return Entities; }
QueryRepository.cs:341-345 public virtual IQueryable<TEntity> ApplySpecification(ISpecification<TEntity> specification) { return specificationEvaluator.GetQuery(GetQueryable(), specification); }
QueryRepository.cs:269-273 ExistsAsync → GetQueryable().Where(condition).AnyAsync(...)
```

**影响**

- **深模块视角：接口比行为宽。** 64 个成员没有带来任何业务语义（没有"注册用户""授予角色"这类动词），调用方必须自己知道"要不要 `Include`""要不要 `AsNoTracking`""这行 `Update` 会不会写全列""什么时候 `SaveChanges`"。这正是"浅模块 + 高认知负荷"的组合。
- 因为 `GetQueryable()` 直接返回 `IQueryable`，**规格（Specification）与 LINQ 是两条并行通道**：同一批查询一半用 `Specifications<T>.Create()`（`MenuService.cs:68`、`RegionService.cs:68`、`FileService.cs:257`、`PermissionService.cs:244`），一半用裸 LINQ（`UserRoleService.cs:24`、`PermissionService.cs:143`）。规格没有成为唯一入口。
- 接口里出现 `IncludableQueryable`/`ExpressionStarter`/`IDbContextTransaction`/`IsolationLevel`/`IPagedList`/`NpgsqlParameter`，意味着**换 ORM、换 provider、换分页库都要改业务代码**。
- 10 个成员零调用（见概览），包括 `ExecutePagedStoredProcedureAsync`、`GetFromRawSqlAsync`、`ExecuteSqlCommandAsync`、`HeadDeleteAsync`、`GetConnection()`——接口是被"想象出来的需求"撑起来的。

**建议**

- 重建时按**用例**设计端口，而不是按实体设计泛型仓储：读侧 `I<UseCase>Query`（返回已投影的 DTO，`AsNoTracking` 内置、不暴露 `IQueryable`），写侧 `I<Aggregate>Repository`（只暴露 `Get`/`Add`/`Remove` + 领域语义方法）。
- 删除全部死成员与 provider 类型；`IIncludableQueryable` 参数改为"读模型自带所需字段"或显式的 `include` 枚举。
- 规格（Ardalis.Specification）值得保留（见"值得保留"），但要收窄为**唯一**的查询组装入口，禁止业务代码拿 `IQueryable`。

---

### 7. 业务服务本身就是 EF 仓储，控制器直接 join 数据表 —— 应用层与领域层都不存在

**证据**

30+ 个服务把 `MainContext` 注入构造函数并**同时继承 `ServiceBase<TEntity>`**：

```csharp
Domain\NexusStack.Core\Services\SystemManagement\MenuService.cs:18   class MenuService(MainContext dbContext, ...) : ServiceBase<Menu>(dbContext, ...), IScopedDependency
Domain\NexusStack.Core\Services\Users\UserService.cs:15              class UserService(MainContext dbContext, ...) : ServiceBase<User>(dbContext, ...)
Domain\NexusStack.Core\Services\Users\PermissionService.cs:19        class PermissionService(MainContext dbContext, ..., IMenuService menuService, IServiceBase<ApiResource> apiResourceService, ...) : ServiceBase<Permission>(...)
```

任何服务都能改任何表：

```csharp
Domain\NexusStack.Core\Services\Users\PermissionService.cs:102   await dbContext.Set<Permission>().Where(p => p.RoleId == model.RoleId && menuIdsQuery.Contains(p.MenuId)).DeleteFromQueryAsync();
```

更彻底的越层：**HTTP 控制器直接对数据表做 LINQ join + 分页**：

```csharp
Host\NexusStack.WebAPI\Controllers\OperationLogController.cs:26     var filter = PredicateBuilder.New<OperationLog>(true);
OperationLogController.cs:56-58                                     var query = from log in operationLogService.GetExpandable().Where(filter)
                                                                                join user in userService.GetQueryable() on log.CreatedBy equals user.Id into lu
                                                                                from s in lu.DefaultIfEmpty()
OperationLogController.cs:74                                        return query.ToPagedList(model.Page, model.Limit);   // 同步执行数据库查询
Host\NexusStack.WebAPI\Controllers\TokenController.cs:129-131       PredicateBuilder.New<Menu>(true)... join m in menuService.GetExpandable()...
```

**影响**

- 数据访问策略（跟踪、投影、分页、事务）**写在控制器里**：审计日志列表跨越了"日志"与"用户"两个未来限界上下文，且用 `ToPagedList`（同步重载）在 async 管道中阻塞线程。
- 服务既不是应用用例、也不是领域对象：它是"某个实体表的操作集合 + 业务校验碎片"（`MenuService.cs:40-59` 的父级存在性/Code 查重就是典型的服务层不变量）。没有聚合，因此没有"哪个服务能改这张表"的边界。
- `IServiceBase<,>` 被注册成 `ServiceBase<,>`（`EFCore\ServiceCollectionExtensions.cs:52-53`），于是同一个请求里存在**两个共享同一 `MainContext`、互不知情的"仓储"对象**（业务服务实例与按需解析的 `IServiceBase<T>`），事务/跟踪状态全靠 scoped DbContext 兜着。

**建议**

- 重建时按限界上下文切分：每个上下文一个 `DbContext`（或至少一个 schema），只有该上下文的聚合仓储能引用它；**控制器禁止出现 `IQueryable`/`PredicateBuilder`/表名**，只调用应用用例。
- 把"父级存在""Code 唯一"这类校验移入聚合方法或领域服务，仓储只负责持久化。

---

### 8. `InsertOrUpdateAsync` 会清空整个变更跟踪器；批量操作绕过审计拦截器；批量能力来自商业库

**证据**

```csharp
Domain\NexusStack.EFCore\Repository\ServiceBase.cs:28-41
    var exists = await this.GetByIdAsync(entity.Id, true);   // NoTracking 读一次
    if (exists is null) await this.InsertAsync(entity); else await this.UpdateAsync(entity);   // 各自 SaveChanges
    this.ResetContextState();                                 // ← ChangeTracker.Clear()，清空整个 scoped 上下文
Domain\NexusStack.EFCore\Repository\ServiceBase.cs:43-57     第二个重载同样 ResetContextState()，并直接 entity.Id = exists.Id;
Domain\NexusStack.EFCore\Repository\Base\RepositoryBase.cs:94-97  public void ResetContextState() { DbContext.ChangeTracker.Clear(); }
```

批量路径全部绕开 `SaveChangesInterceptor`（因此 `UpdatedAt/UpdatedBy` 不会更新）：

```csharp
ServiceBase.cs:66-71    BatchDeleteAsync    → GetQueryableIgnoreQueryFilters().Where(condition).DeleteFromQueryAsync(cancellationToken);
ServiceBase.cs:80-93    BatchSoftDeleteAsync→ Entities.Where(condition).UpdateFromQueryAsync(data);      // data 只含 IsDeleted=true
ServiceBase.cs:102-105  UpdateFromQueryAsync(condition, updateExpression, ct)
ServiceBase.cs:137-154  DeleteAsync(long id) 软删除分支 → Entities.Where(a => a.Id == id).UpdateFromQueryAsync(data);
Domain\NexusStack.Core\Services\Users\UserTokenService.cs:147   await userService.UpdateFromQueryAsync(a => a.Id == user.Id, a => new User { ... });
Domain\NexusStack.Core\Schedules\DailySchedule.cs:42-48         BatchDeleteAsync(x => x.ExecuteEndTime <= delDate) / BatchDeleteAsync(x => x.CreatedAt <= delDate2/3)
Domain\NexusStack.EFCore\NexusStack.EFCore.csproj:21            Z.EntityFramework.Extensions.EFCore 10.105.6
```

**影响**

- `ResetContextState()` 清的是**整个 scoped DbContext** 的跟踪状态，不只是当前实体。同一请求里若另一处已加载并持有跟踪实体（例如 `ScheduleTaskService.UpdateScheduleTaskStatusAsync` 先 `GetAsync` 再改属性），在任意一次 `InsertOrUpdateAsync` 之后，那些改动**会被静默丢弃**（`SaveChanges` 找不到变更）。这是一个"没有报错、数据没写进去"级别的隐患。
- `InsertOrUpdateAsync(entity, condition)` 直接改写 `entity.Id`（`:52`），把"已存在实体的身份"强行搬到入参对象上，掩盖了调用方传错实体的问题。
- 软删除走 `UpdateFromQuery`，**审计字段不会写入**：用 `DeleteAsync(id)` 软删一行后，`UpdatedAt/UpdatedBy` 保持旧值，`IsDeleted=true` 与审计时间互相矛盾（拦截器 `MainSaveChangeInterceptor.cs:103-110` 只在 `SaveChanges` 路径生效）。
- `Z.EntityFramework.Extensions.EFCore` 是**商业授权**库（`:21`），且它的 `DeleteFromQueryAsync/UpdateFromQueryAsync` 语义与 EF 原生 `ExecuteDelete/ExecuteUpdate` 重叠——后者自 EF Core 7 起内置且支持拦截器/事务一致性。

**建议**

- 删除 `ResetContextState()` 的隐式调用；需要"读-改-写"就用跟踪查询，`InsertOrUpdateAsync` 这种"通用 upsert"从仓储上移除，改为各聚合显式的 `Add`/`Update` 用例。
- 批量软删除统一走 `ExecuteUpdate`（或 `SaveChanges` 循环），保证审计与领域事件一致；`BatchDeleteAsync`/`HeadDeleteAsync` 二者留一个并命名清楚（当前 `BatchDeleteAsync` 会连软删除数据一起物理删除，`ServiceBase.cs:68-70` 的注释也承认这点）。
- 用 EF 原生批量 API 替换商业库（同时去掉一条授权风险）。

---

### 9. 全局 `NoTracking` + `Update(entity)` 全列覆盖 + 无并发令牌 + 无唯一索引 = 静默丢更新与重复业务键

**证据**

```csharp
Domain\NexusStack.EFCore\ServiceCollectionExtensions.cs:35   options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
Domain\NexusStack.EFCore\Repository\Base\RepositoryBase.cs:47-53   public virtual Task<int> UpdateAsync(TEntity entity, ...) { Entities.Update(entity); return DbContext.SaveChangesAsync(...); }
Domain\NexusStack.Core\Services\SystemManagement\MenuService.cs:150-153   var entity = await GetAsync(a => a.Id == id) ?? throw ...; entity = Mapper.Map(model, entity); return await UpdateAsync(entity);
```

全仓**没有**并发令牌、没有显式唯一索引、没有 CHECK：

```
grep IsConcurrencyToken|UseXminAsConcurrencyToken|ValueGeneratedNever|HasCheckConstraint|IsUnique|unique: true  → 0 命中
迁移里的 11 个索引全部是 EF 按外键约定自动生成的普通索引（20260307033208_InitialDatabase.cs:634-687；InitDatabase.sql:696-716）
模型里没有任何 HasIndex(...)（MainContextModelSnapshot.cs 中 11 处 HasIndex 全部来自 FK 约定）
```

**影响**

- `Entities.Update(entity)` 对**分离实体**执行"整行标记为已修改"：写回全部列，包括 `CreatedAt/CreatedBy`、`Remark`（DTO 未提供时被写成 null）、`IsDeleted`。两个请求并发编辑同一行 → **后写覆盖先写**，且无任何冲突检测（PostgreSQL 的 `xmin` 并发令牌、`rowversion` 都没用）。
- 没有唯一索引意味着业务键可以重复：`UserName`（`User.cs:29-31`）、`Menu.Code`（`Menu.cs:27`）、`Role.Code`、`AppConfig.AppKey`、`ApiResource(RoutePattern+RequestMethod)` 全都没有唯一约束。应用层查重是**可绕过的**：`MenuService.cs:50` 的 `GetAsync(a => a.Code == entity.Code)` 受软删除全局过滤器影响（`MainContext.cs:48`），软删一条同名菜单后可以再建一条同名记录，而 `GetAsync` 会 `FirstOrDefault` 随机返回其一。
- 全局 `NoTracking`（`:35`）还有个反向陷阱：任何"取出来改一改、不显式 `Update` 就指望自动保存"的代码会**静默不生效**。默认值与调用方直觉相反（`GetByIdAsync` 的 `asNoTracking` 默认值也是 `true`，`IQueryRepository.cs:88-126` 的文档注释还写着 "Default value is false"，与实现相反）。

**建议**

- 写路径一律用跟踪查询 + 显式 `SaveChanges`（或 `ExecuteUpdate` 精确更新指定列），禁止 `Update(分离实体)` 作为通用更新。
- 每个聚合加并发令牌（PG 用 `xmin`，或加 `rowversion` 列），失败时返回 409。
- 业务唯一键加**唯一索引**（软删除场景用部分索引 `WHERE "IsDeleted" = false`），并把"查重"降级为体验优化而非正确性保证。

---

### 10. 软删除的全局过滤器做得干净，但与唯一性、租户、审计的组合不完整

**证据**

正确的部分（值得保留，见下节）：

```csharp
Domain\NexusStack.EFCore\DbContexts\MainContext.cs:48                    modelBuilder.ApplyGlobalFilterAsDeleted<ISoftDelete>(a => !a.IsDeleted);
Domain\NexusStack.EFCore\DbContexts\ModelBuilderExtensions.cs:53-66      用 ReplacingExpressionVisitor 把接口上的表达式重写到具体实体类型
Domain\NexusStack.EFCore\Repository\Query\QueryRepository.cs:63-66,275-279  GetQueryableIgnoreQueryFilters() / ExistsWithSoftDeleteAsync 提供逃生舱
```

不完整的部分：

```csharp
// 租户列存在，但从未参与过滤
Domain\NexusStack.Core\Entities\SystemManagement\GlobalSettings.cs:17   public long AppId { get; set; }
Domain\NexusStack.Core\Entities\SystemManagement\Menu.cs:110             public long SystemId { get; set; } = 0;
Domain\NexusStack.Core\Entities\Users\Role.cs:76                         public long SystemId { get; set; } = 0;
Domain\NexusStack.Core\Services\SystemManagement\GlobalSettingService.cs:75-99   所有按 AppId 过滤的代码都被注释掉（//spec.Query.Where(w => w.AppId == "-1"); 等）
// 唯一索引缺席（见发现 9）；软删除与审计的组合缺口见发现 8
```

**影响**

- `SystemId`/`AppId` 是**多租户/多应用隔离的化石**：写入了值、没有任何查询过滤、没有索引、没有组合进唯一键。多应用共用一套菜单/角色表时，A 应用的管理员能看到并修改 B 应用的菜单（`MenuService.cs:66` 的 `GetListAsync()` 不带任何 `SystemId` 条件）。
- 软删除 + 无唯一索引 + `FirstOrDefault` 是"同名同码"数据的温床（见发现 9）。
- 全局过滤器对**原生 SQL 路径无效**：`QueryRepository.GetFromRawSqlAsync` / `ServiceBase.BatchDeleteAsync` 的 `IgnoreQueryFilters` 分支都不经过过滤器，语义要调用方自己记。

**建议**

- 决定要不要多租户：要，就把租户列提升为 EF 的**命名查询过滤器**（EF 10 支持多过滤器）+ 组合进所有唯一索引 + 强制在仓储层注入；不要，就删除 `SystemId`/`AppId`，别留半成品。
- 软删除统一策略：部分唯一索引 + `DeletedAt/DeletedBy` 字段（当前只有 `IsDeleted` 布尔，无法回答"谁在何时删的"）。

---

### 11. 「移除外键」只做了一半：`CreateTable` 剥掉，`AlterTable` 是空实现，未来的 `AddForeignKey` 会照样落库

**证据**

```csharp
Domain\NexusStack.EFCore\MigrationsSqlGenerator.cs:21-25
    protected override void Generate(CreateTableOperation operation, IModel? model, MigrationCommandListBuilder builder, bool terminate = true)
    { operation.ForeignKeys.RemoveAll(a => true); base.Generate(operation, model, builder, terminate); }

MigrationsSqlGenerator.cs:27-32
    protected override void Generate(AlterTableOperation operation, IModel? model, MigrationCommandListBuilder builder)
    { // AlertTable 取消外键
      base.Generate(operation, model, builder); }      // ← 什么都没做
```

模型里外键**依然存在**（11 个），并被统一改成 `Restrict`：

```csharp
Domain\NexusStack.EFCore\DbContexts\ModelBuilderExtensions.cs:73-83   foreignKey.DeleteBehavior = DeleteBehavior.Restrict;
Host\NexusStack.WebAPI\Migrations\20260307033208_InitialDatabase.cs:283,462,468,496,502,528,555,561,588,594,630  onDelete: ReferentialAction.Restrict
```

产物侧确认外键确实被剥掉：`InitDatabase.sql` 中 `FOREIGN KEY`/`REFERENCES` **0 命中**（`InitDatabase.sql:696-716` 只有索引）。

**影响**

- EF 对"给已存在的表加外键"生成的是 `AddForeignKeyOperation`（不是 `CreateTableOperation`），而生成器**没有覆盖它**；同时 `AlterTableOperation` 的覆盖是空实现（注释写着要取削外键，实际 `base.Generate` 原样输出）。因此**下一次给老实体加导航属性时，DDL 会突然多出外键约束**，而其它表没有——"无外键"策略会在第一次 schema 演进时破功，且是静默破功。
- 库侧无外键 + 应用侧无聚合 = 没有任何一层保证引用完整性：`Menu.ParentId`、`Permission.RoleId`、`UserDepartment.DepartmentId` 都可以指向不存在的行（`InitDatabase.sql` 无 FK 可证）。EF 侧的 `Restrict` 只在"删除被跟踪实体"时生效。
- `RemoveAll(a => true)`（`:23`）是把**所有**外键无差别删除，包括未来可能需要强约束的审计/账务表——策略是全局的，没有例外出口。

**建议**

- 把这个决策**显式化**：要么在模型层用 `ExcludeForeignKey` 之类的约定/`MigrationsSqlGenerator` 覆盖 `AddForeignKeyOperation` 并加测试；要么干脆保留外键（微服务下每个服务自己一个库，外键是廉价的正确性保障）。
- 用 `[DbContext]` 级别的测试断言"生成脚本里没有 FOREIGN KEY"，防止策略回归。

---

### 12. 连接与读取器生命周期靠调用方自觉：开连接不关、重复开、无 `try/finally`、反射填充与列名强耦合

**证据**

```csharp
Domain\NexusStack.EFCore\Repository\Query\SqlQueryExtensions.cs:32-50
    using DbCommand command = dbContext.Database.GetDbConnection().CreateCommand();
    ...
    await dbContext.Database.OpenConnectionAsync(cancellationToken);      // ← 打开后从不关闭
Domain\NexusStack.EFCore\Repository\ServiceBase.cs:171-215
    await GetDbContext.Database.OpenConnectionAsync();                    // 第一次
    using (var command = ...CreateCommand()) { ... await GetDbContext.Database.OpenConnectionAsync();   // 第二次（重复）
        ... await GetDbContext.Database.CloseConnectionAsync(); }         // 关闭在 using 内部，异常路径不会执行
Domain\NexusStack.EFCore\Repository\ServiceBase.cs:118-121   public virtual DbConnection GetConnection() => this.DbContext.Database.GetDbConnection();   // 裸连接外泄，全仓零调用
Domain\NexusStack.EFCore\Repository\Query\SqlQueryExtensions.cs:56-64
    obj = Activator.CreateInstance<T>();
    foreach (PropertyInfo prop in obj.GetType().GetProperties())
        if (!Equals(result[prop.Name], DBNull.Value)) prop.SetValue(obj, result[prop.Name], null);   // 列名必须严格等于属性名；无类型转换
Domain\NexusStack.EFCore\Repository\Query\SqlQueryExtensions.cs:41   dbParameter.ParameterName = "@p" + index;   // 参数名硬编码，且不设置 DbType
Domain\NexusStack.EFCore\ServiceCollectionExtensions.cs:49           AddDbContextPool<MainContext>(..., poolSize: 1024)
```

**影响**

- `GetFromQueryAsync` 打开的连接**不会关闭**；`ExecutePagedStoredProcedureAsync` 在异常路径上同样不会关。由于上下文是池化的（`AddDbContextPool`，池 1024）且连接串里 `Maximum Pool Size=100`，一旦走到这些路径就可能出现"连接被池化上下文长期占用"的连锁反应。
- 反射填充对 **列名 = 属性名** 的隐式契约、对枚举/`decimal`/`DateTimeOffset` 等类型零转换、列缺失直接 `IndexOutOfRangeException`；`SqlQueryExtensions.cs:79-175` 的另两个实现（含 `pi.DeclaringType`（应为 `PropertyType`）的明显 bug）是纯死代码，却仍然引用 EF 内部 API（`IConcurrencyDetector`、`IRawSqlCommandBuilder`）。
- `GetConnection()` 把裸 `DbConnection` 交给业务层（当前无人调用），是重建时应当直接删除的口子。

**建议**

- 删除 `GetFromQueryAsync` 与两个死方法；需要原生 SQL 就用 `Database.SqlQuery<T>`（EF 8+，支持映射与参数化，生命周期由 EF 管理）。
- 若必须手写 ADO，一律 `await using var conn = ...` 或 `CreateCommand` + `try/finally`，并把 `GetConnection()` 从公共接口移除。

---

### 13. 邻接证据：启动期数据操作是 N+1，缓存键被"格式化两次"，种子流程有 `break` 语义错误

> 说明：这三处代码在 `Domain\NexusStack.Core`，不属于本区文件，但它们全部由本区的仓储/跟踪语义放大，故作为邻接证据列出，供重建时一并处理。

**证据**

```csharp
Domain\NexusStack.Core\Services\Schedules\ScheduleTaskService.cs:30-62
    foreach (var cronService in TypeFinders.SearchTypes(...)) {          // 每个任务类型
        await redisService.DeleteAsync(CoreRedisConstants.ScheduleTaskCache.Format(cacheKey));   // ← cacheKey 已 Format 过
        var exists = await GetAsync(a => a.Code == code);                // ← 每个类型一次查询（N+1）
        if (exists is null) await this.InsertAsync(exists);              // ← 每个类型一次 SaveChanges
        await redisService.SetAsync(cacheKey, cacheValue); }             // ← 每个类型一次 Redis 写
Infrastructure\Constants\CoreRedisConstants.cs:40   public static string ScheduleTaskCache = $"ScheduleTask:{{0}}";
Domain\NexusStack.Core\Services\Schedules\ScheduleTaskService.cs:34   var cacheKey = CoreRedisConstants.ScheduleTaskCache.Format(code);   // "ScheduleTask:{code}"
                                                                      // :37 再 Format("ScheduleTask:{code}") → "ScheduleTask:ScheduleTask:{code}" → 删错键
Domain\NexusStack.Core\HostedServices\ExecuteSeedDataService.cs:104-107
    if (model is not null && !model.IsEnable) { break; }                 // ← 应为 continue：一个禁用的种子会中断其后全部种子
Domain\NexusStack.Core\HostedServices\ExecuteSeedDataService.cs:91,113   list = await cronTaskCoreService.GetListAsync(); (全表) / DocsHelper.GetTypeComments(...)
Domain\NexusStack.Core\HostedServices\InitApiResourceService.cs:82       await apiResourceService.InsertOrUpdateAsync(resource, a => a.Code == resource.Code);   // 每次启动 ×2 次往返 + 清跟踪器
```

**影响**

- 定时任务初始化在启动路径上做 O(任务数) 次数据库查询 + 写入 + Redis 往返；缓存失效因为键被格式化两次而**永远删不到正确的键**（`ScheduleTask:ScheduleTask:{code}`），任务启用状态在进程内可能长期读旧值。
- 种子 `break` 让"禁用第 N 个种子"变成"跳过第 N 个之后的所有种子"，且不报错（日志里看不出来）。
- `InsertOrUpdateAsync` 在启动路径批量调用 → 反复 `ChangeTracker.Clear()`（发现 8）。

**建议**

- 批量读一次（`GetListAsync(a => codes.Contains(a.Code))`）+ 字典比对，写入用一次 `SaveChanges`；缓存键只 `Format` 一次（把 API 改成 `BuildKey(code)` 返回最终键，避免二次格式化）。
- 种子的启用/去重语义显式化（`continue` + 版本号），并把"启动期数据迁移"从 Web 进程里移出去。

---

### 14. 树结构用 `LIKE '%父ID%'` 匹配物化路径：既不正确也不可索引，还要把全表读进内存建树

**证据**

```csharp
Domain\NexusStack.Core\Services\SystemManagement\MenuService.cs:64-139
    var menus = await GetListAsync();                                        // 全表读入内存
    if (model.IncludeChilds) spec.Query.Search(a => a.IdSequences, $"%{model.ParentId}%");   // LIKE '%12%' 会命中 123、312
    while (flag) { ... menus.Where(x => ids.Contains(x.ParentId)) ... }       // 内存里循环补父级
    List<MenuTreeDto> getTree(long parentId) { ... }                          // 递归在内存里建树
Domain\NexusStack.Core\Services\SystemManagement\RegionService.cs:68-88      同名模式（Region 树）
Domain\NexusStack.Core\Services\SystemManagement\MenuService.cs:43,47        entity.IdSequences = $"{parent.IdSequences}{entity.Id}."; / $".{entity.Id}."
Domain\NexusStack.Core\Services\SystemManagement\MenuService.cs:179-181      parentIds = menu.IdSequences.Split('.').Select(Convert.ToInt64); GetListAsync(a => parentIds.Contains(a.Id))
```

**影响**

- `%12%` 的子串匹配让"父级 = 12"的过滤同时命中 123/312/1024…，**返回错误的树**；同时前导通配符让索引失效（`IdSequences` 在 DDL 里是 `text`，也没有索引）。
- 每次取树都把整张 `Menu`（含 `SystemId`=全部应用）读进内存，再做 O(n·树深) 的后处理；数据量上去后这是接口级慢查询。
- 物化路径把**雪花主键拼进业务字段**，与发现 3 的 ID 策略变化耦合；`Split + Convert` 每次都解析字符串。

**建议**

- 用 `ltree`/`hierarchyid`/`ParentId + 递归 CTE`（PG 用 `WITH RECURSIVE`）取子树，或至少用**带分隔符的规范编码**（`.12.` 而不是 `%12%`）并建 GIN/前缀索引。
- 树形查询放读模型（Dapper/视图），不要让领域实体承担展示层树结构（`MenuTreeDto` 已经是展示模型，`MapsterProfile.cs:62-65` 也承认这是投影）。

---

### 15. 表/列注释链路是断的：没有 `GenerateDocumentationFile`，于是生成 200+ 条空注释

**证据**

```csharp
Domain\NexusStack.EFCore\DbContexts\ModelBuilderExtensions.cs:89-118
    var typeComment = DocsHelper.GetTypeComments(entityType.ClrType.Assembly.GetName().Name, entityType.ClrType);
    builder.Entity(entityType.ClrType).HasComment(typeComment);
    foreach (var property in entityType.GetProperties()) { ... property.SetComment(memberComment); }
Infrastructure\Utils\DocsHelper.cs:13-44
    Docs = [.. Directory.GetFiles(baseDir, "*.xml")];     // 只扫输出目录里的 XML
    var docPath = Docs.FirstOrDefault(...); if (docPath.IsNullOrEmpty()) return string.Empty;   // 找不到就返回空串
```

而全仓**没有任何项目设置 `GenerateDocumentationFile`**（grep `GenerateDocumentationFile|DocumentationFile` 只命中 `NexusStack.Template.csproj:17` 的 `NoWarn`），仓库里也不存在任何 `.xml` 文档文件（全仓 `*.xml` 搜索 0 命中，排除 bin/obj）。结果直接体现在产物里：

```sql
SqlMigration\InitDatabase.sql:26   COMMENT ON TABLE "ApiResource" IS '';
SqlMigration\InitDatabase.sql:27-41  COMMENT ON COLUMN "ApiResource"."Id"/"Name"/... IS '';   （每列一条，全文 200+ 条空注释）
```

**影响**

- 注释机制的成本（每次模型构建都反射读类型/成员、DDL 里几百条 `COMMENT ON`）全部付出，收益为零；更糟的是**下游会把空注释当成"作者没写文档"而不是"管道坏了"**（`ExecuteSeedDataService.cs:113` 用同一个 `DocsHelper` 给种子任务取 Name，于是 `SeedDataTask.Name` 也会落成空串）。
- `ModelBuilderExtensions.cs:107` 用 `property.PropertyInfo?.DeclaringType?.Assembly == Assembly.GetExecutingAssembly()` 判断"属性是否来自 EFCore 基类"，耦合了"基类所在程序集 == `Assembly.GetExecutingAssembly()`"这一实现细节，重构程序集边界时会静默退化。

**建议**

- 立刻补 `<GenerateDocumentationFile>true</GenerateDocumentationFile>` 与 `<NoWarn>$(NoWarn);CS1591</NoWarn>`（或删掉整套注释生成，二选一，不要维持"看起来有、实际为空"的中间态）。
- 注释应在**迁移生成时**由 CI 校验"非空"，否则给出告警。

---

### 16. 模型发现依赖程序集名前缀，且没有"空模型"防线；失败模式是 `NullReferenceException`

**证据**

```csharp
Domain\NexusStack.EFCore\DbContexts\ModelBuilderExtensions.cs:37-43
    var types = TypeFinders.SearchTypes(typeof(TEntity), TypeFinders.TypeClassification.Interface).Where(a => !a.IsAbstract && a.IsClass).Where(modelTypePredicate).ToList();
    foreach (var type in types) { builder.Entity(type).HasNoDiscriminator(); }
Infrastructure\TypeFinders\TypeFinders.cs:74
    var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(item => item.FullName.StartsWith("NexusStack.")).ToList();
Domain\NexusStack.EFCore\DbContexts\MainContext.cs:45   modelBuilder.RegisterFromAssembly<IEntity>(a => !a.IsDefined(typeof(NotMappedAttribute), true));
Domain\NexusStack.EFCore\Repository\Query\QueryRepository.cs:138-140
    IEntityType entityType = DbContext.Model.FindEntityType(typeof(TEntity));
    string primaryKeyName = entityType.FindPrimaryKey().Properties.Select(p => p.Name).FirstOrDefault();   // entityType 为 null 时 NRE
```

（模板把 `sourceName: NexusStack` 全量替换，所以生成项目的程序集名会变成 `<新名字>.Core`，前缀过滤字面量也被同步替换成 `<新名字>.`——**恰好**仍然匹配；但这是"靠模板文本替换维持的隐式约定"。）

**影响**

- 只要有人加一个不以前缀命名的项目（例如 `Acme.Payments.Domain`）、或某个程序集在模型构建时尚未加载，**它的实体会被静默排除**：没有日志、没有异常，直到运行期 `Set<T>()` 抛 "Cannot create a DbSet for 'X' because this type is not included in the model for the context"。这正是"编译期无关联、运行期才炸"的最坏组合。
- 若一个实体都没被发现，`OnModelCreating` 照样成功，问题推到第一个查询。
- 反射路径的失败模式差：`FindEntityType` 返回 null → `FindPrimaryKey()` 直接 NRE（`QueryRepository.cs:140`、`MappingRepository.cs:55-56` 同样），排查成本极高。
- `RegisterFromAssembly` 用 `Activator.CreateInstance(type)` 创建映射配置（`ModelBuilderExtensions.cs:30`），配置类**不能有构造依赖**（如 `ICurrentUser`、多租户提供者），限制了后续演进。

**建议**

- 改为**显式注册**（每个上下文一个 `IEntityTypeConfiguration` 列表或模块化 `ApplyConfigurationsFromAssembly(typeof(X).Assembly)`），并在最后断言 `builder.Model.GetEntityTypes().Any()`，为空即抛异常并给出清晰信息。
- 给 `FindEntityType` 的空值加显式异常（"实体 X 未注册到上下文 Y"）。
- 前缀约定若必须保留，改为常量 + 启动期校验（列出被发现的程序集与实体数并写日志）。

---

### 17. 生产凭据落在工作区、且模板打包配置没有排除它们

**证据**

工作区里存在 AgileConfig 的本地配置缓存（**未被 git 跟踪**：`git check-ignore` 命中 `.gitignore:244 *.[Cc]ache`；`git status` 干净）：

```
Host\NexusStack.WebAPI\agile\config\nexusstack_api.agileconfig.client.configs.cache    6,795 B / 36 项
Host\NexusStack.WebAPI\agile\config\nexusdtack_api.agileconfig.client.configs.cache    11,697 B / 61 项
```

内容（键值均为明文，`group=ConnectionStrings|RabbitMQ|Redis`）：

```
PostgreSQL     = Host=<服务器IP>;Port=5432;Database=nexusstack;Username=<已脱敏>;Password=<已脱敏>;Pooling=true;...
ConnectionString = <服务器IP>:6379,user=leowang,password=<已脱敏>,defaultDatabase=0        (Redis)
HostName/Password/VirtualHost = <服务器IP> / <已脱敏> / /nexusstack                        (RabbitMQ)
AccessKeySecret = Pq3VCDGLj4rZ96ucGHVqfkRGYhSbE2 / F7V8XDXF5P2MvKufl5YxJtesz2ciGX            (阿里云 OSS)
ApiSecret = orD8t7qgk4UQYad4spFkGAsJYtC7g7
另有一份指向公网：PostgreSQL = Host=<服务器IP>;...;Username=<已脱敏>;Password=<已脱敏>;
```

而模板打包**没有排除 `*.cache`**：

```xml
NexusStack.Template.csproj:18   <NoDefaultExcludes>true</NoDefaultExcludes>
NexusStack.Template.csproj:22   <Content Include="**\*" Exclude="**\bin\**;**\obj\**;**\.vs\**;**\.git\**;**\.idea\**;**\packages\**;**\*.user;**\*.suo" />
.template.config\template.json:124-134   "exclude": [ "**/bin/**", "**/obj/**", "**/.vs/**", "**/.git/**", "**/.idea/**", "**/packages/**", "**/*.user", "**/*.suo", "**/.template.config/**" ]
Install-Template.ps1:47   dotnet new install .
```

同时 appsettings 里带着配置中心凭据（同一套弱口令）：

```json
Host\NexusStack.WebAPI\appsettings.Development.json:2-12   "appId": "nexusstack_api", "secret": "<已脱敏>", "nodes": "http://<服务器IP>:8010"
Host\NexusStack.WebAPI\appsettings.Production.json:2-12    "appId": "nexusdtack_api", "secret": "<已脱敏>", "nodes": "http://agile_config:5000"
Host\NexusStack.WebAPI\appsettings.Test.json:2-16          同上，nodes: http://<服务器IP>:8090
```

**影响**

- `git` 干净、`.cache` 被忽略 ⇒ **不在版本库里**；但 `dotnet pack` / `Install-Template.ps1` 打包的是**工作区文件**，`NoDefaultExcludes=true` + `**\*` + 排除列表里没有 `*.cache`/`agile/**` ⇒ 生产 PostgreSQL 口令、Redis 口令、RabbitMQ 口令、阿里云 AK/SK 会被打进模板 NuGet 包，随 `dotnet new nexusstack` 分发给**每一个使用者**。这是一条对外的凭据泄露通道（发版前必须实测验证，`dotnet pack` 后解压检查 `content\Host\...\agile\config\`）。
- 数据层的可启动性完全依赖远端 AgileConfig（连接串只存在于 `group=ConnectionStrings`，本区代码 `ServiceCollectionExtensions.cs:21` 直接 `GetConnectionString("PostgreSQL")`，**没有 null 校验/失败快启**）：无 docker-compose、无本地 PG、无 User Secrets 模板 ⇒ 新机器上第一次 `dotnet run` 必然失败，且错误信息是 Npgsql 的参数校验异常而非"缺少配置"。

**建议**

- 立即：`NexusStack.Template.csproj` 的 `Exclude` 增加 `**\*.cache;**\agile\**;**\appsettings.*.Local.json`，并在 `template.json` 的 modifiers 中同步排除；把两份缓存文件从工作区删除；**轮换**上述全部口令与 AK/SK（已存在于至少一台开发机与模板打包路径上）。
- 重建：连接串只在环境变量/密钥管理（User Secrets / Key Vault / KMS）里，`AddDbContext` 前做 `string.IsNullOrWhiteSpace` 显式失败；仓库里放 `docker-compose.yml` + `.env.example` 让数据层可离线启动。

---

### 18. DDD 就绪度判定：无聚合、无值对象、无强类型 ID、无领域事件、无不变量；`Region` 兼作"部门"

**证据**

- 实体全是"公开 setter 的数据袋"，且继承自数据层基类：

```csharp
Domain\NexusStack.Core\Entities\Users\User.cs:12      public class User : AuditedEntity          // 继承 EF 基类，非 POCO
User.cs:18-99                                         public string? Mobile { get; set; } … public virtual List<Role>? Roles { get; set; }
Domain\NexusStack.Core\Entities\SystemManagement\Menu.cs:20-110   required string Name / public long ParentId { get; set; } / public virtual List<Menu>? Children { get; set; }
```

全仓 `private set` / `protected set` 命中 **0**（实体属性一律 `{ get; set; }`）；无 `record`、无 `ValueObject`、无 `AggregateRoot`、无 `IDomainEvent`、无 `OwnsOne`（grep 0 命中）。

- 无强类型 ID：`IEntity<TKey>.Id` 是裸 `long`（`Domain\NexusStack.EFCore\Entities\IEntity.cs:15-21`），22 个实体全部用 `long`，外键也是裸 `long`（如 `UserRole.UserId`/`RoleId`，`Domain\NexusStack.Core\Entities\Users\UserRole.cs:16,21`）。
- 无领域事件：事件只有面向集成的 `EventData`（`Domain\NexusStack.Core\EventData\*`）与 HTTP 过滤器里的发布（`Domain\NexusStack.Core\Filters\OperationLogActionFilter.cs:85`），实体内部**没有任何事件收集**。
- 不变量在服务层且可绕过："菜单父级不能是自己""Code 唯一"（`MenuService.cs:50-59`）在服务方法里，任何直接使用 `IServiceBase<Menu>`/`dbContext.Set<Menu>()` 的代码都能绕过。
- 限界上下文混淆的铁证：**组织架构用地理区域表表示**：

```csharp
Domain\NexusStack.Core\Entities\Users\UserDepartment.cs:17-29   public long DepartmentId { get; set; }   // 组织单元（当前指向 Region.Id）
                                                                public virtual Region? Department { get; set; }
Infrastructure\Enums\RegionLevel.cs:11-27                       Country = 0, Province = 1, City = 2, Department = 3    // 国家/省/市/公司部门 同一棵树
```

- 同一关系两种表示：`User.Roles`（跳导航）与 `User.UserRoles`（显式连接实体）并存：

```csharp
Domain\NexusStack.Core\Entities\Users\User.cs:94,99             public virtual List<Role>? Roles / public virtual List<UserRole>? UserRoles
Domain\NexusStack.Core\Mapping\UserMapping.cs:17                builder.HasMany(a => a.Roles).WithMany(a => a.Users).UsingEntity<UserRole>();
```

- 跨上下文导航属性直接连表：`UserToken.User`（`Entities\Users\UserToken.cs:77`）、`Menu.Resources`→`MenuResource`→`ApiResource`（`Entities\SystemManagement\Menu.cs:105`、`MenuResource.cs:26,31`）、`Permission.Menu/Role`（`Entities\Users\Permission.cs:32,37`）、`UserDepartment.Department`→`Region`、`ScheduleTaskRecord.ScheduleTask`（`Entities\Schedules\ScheduleTaskRecord.cs:43`）。

**影响**

- 现状是**贫血实体 + 事务脚本服务 + 泛型仓储**三件套：没有"通过聚合根修改内部状态"的入口，重建时"把服务拆成限界上下文"会直接暴露为"这些 join 该归谁"。
- 限界上下文缝隙可以明确标出（重建时的切分建议）：身份与权限（`User/Role/Permission/UserRole/UserToken`）、组织与区域（`Region/UserDepartment`）、菜单与接口资源（`Menu/MenuResource/ApiResource`）、调度（`ScheduleTask/ScheduleTaskRecord/SeedDataTask`）、异步任务（`AsyncTask`）、文件与下载（`File/DownloadItem`）、开放平台配置（`AppConfig/AppEventConfig/AppNotificationConfig/AppWebhookConfig`）、审计日志（`OperationLog`）。其中**审计→身份**（`OperationLogController.cs:57` 的 join）、**权限→菜单→接口资源**（`UserContextCacheService` 在一个方法里横跨 5+ 表，01 报告第 7 节已列）、**组织→区域**（`UserDepartment.Department`）是三处最脏的耦合。
- **当前强制"共享一个数据库"的东西**：① 唯一 `MainContext` + 无 `DbSet` 属性 + 反射注册"所有 `IEntity`"（`MainContext.cs:45`）⇒ 模型层面就是一个大库；② 导航属性 + 跳导航跨实体（上面列出的 6 处）；③ 控制器/服务直接跨表 join（`OperationLogController.cs:56-58`、`UserRoleService.cs:24-31`、`PermissionService.cs:92-104,143-145`）；④ 单套迁移（`MigrationsAssembly("NexusStack.WebAPI")`）；⑤ 审计/软删除拦截器按全局跟踪器工作（`MainSaveChangeInterceptor.cs:44-51`），一旦拆库，跨库实体同处一个 `ChangeTracker` 的假设立即失效。

**建议**

- 重建时每个限界上下文：独立 `DbContext`（模型里**只有**本上下文的聚合）、独立库/schema、独立迁移、只有聚合根暴露仓储；跨上下文只通过 ID + 集成事件/读模型通信（禁止导航属性）。
- `Region` 拆成"地理区域"与"组织单元"两个概念；`UserDepartment` 改为 `Membership(OrganizationId)`。
- 给每个聚合加不变量入口（工厂方法/行为方法 + `private set` 或 `init`），把服务层的校验搬进模型，并用架构测试阻断"服务直接持有别人的 DbContext"。

---

### 19. 测试性：零测试工程、启动强依赖远端配置、模型构建绑定 Npgsql

**证据**

- 两个解决方案文件里**没有任何测试项目**（`NexusStack.sln` / `NexusStack_Backend.slnx` 中 grep `Test|Spec` 均无命中），全仓无 xunit/nunit/MSTest 引用，无 `Microsoft.EntityFrameworkCore.InMemory`/`Sqlite` 引用。
- 启动链路硬绑远端配置中心：`Domain\NexusStack.Core\ServiceCollectionExtensions.cs:81 builder.Host.InitHostAndConfig(moduleKey, coreServiceType)` + `appsettings.Development.json:2-12`（AgileConfig 节点、appId、secret）⇒ 集成测试必须能连上配置服务，否则连 `IConfiguration` 都不完整。
- 模型构建依赖 Npgsql 类型：`MigrationsSqlGenerator` 的构造函数需要 `INpgsqlSingletonOptions`（`MigrationsSqlGenerator.cs:15`），而它被 `ReplaceService` 全局替换（`ServiceCollectionExtensions.cs:65`）⇒ 换 SQLite/InMemory 提供程序做测试时，这个替换会因缺少 Npgsql 服务而解析失败。
- 实体无法 `new`（发现 4），服务的构造函数要求真实 `MainContext` + `IMapper` + `TypeAdapterConfig`（`MenuService.cs:18`），且 `IMapper` 注册依赖 `AddServices` 反射扫描（`Infrastructure\ServiceCollectionExtensions.cs:17-32`）。

**影响**

- 目前**无法对数据层写任何自动化测试**：单元测试过不了"实体构造要 DI"，集成测试过不了"启动要远端配置"，替换内存库过不了"Npgsql 硬绑定"。这也解释了为什么发现了这么多"运行期才知道"的缺陷（发现 3、5、8、13）。
- 重建时的测试基座必须先把这三件事解开：POCO 实体、`IConfiguration` 可完全由测试提供、提供程序可替换（Testcontainers + 真 PG 是最省事的路径）。

**建议**

- 引入 `Testcontainers.MsSql/Npgsql` + `Respawn` 做数据层集成测试；`MigrationsSqlGenerator` 的替换只在 PG 路径生效（按提供程序条件注册）。
- 每次 schema 变更跑三条测试：空库迁移、旧库迁移、`InitDatabase.sql` 与 `migrations script` 输出一致性。

---

## 值得保留

1. **软删除的全局查询过滤器**（`MainContext.cs:48` + `ModelBuilderExtensions.cs:53-66`）：用 `ReplacingExpressionVisitor` 把"接口上的谓词"重写到具体实体类型，是把横切关注点做成约定的正确姿势；`GetQueryableIgnoreQueryFilters`/`ExistsWithSoftDeleteAsync`（`QueryRepository.cs:63-66,275-279`）留了成体系的逃生舱。重建时保留机制，补上"部分唯一索引 + DeletedAt/DeletedBy"。
2. **`SaveChangesInterceptor` 统一做审计与软删除改写**（`MainSaveChangeInterceptor.cs`）：`DetectChanges` 先跑（`:47`）、软删除先把 `Deleted` 改写成 `Modified`（`:69`）再填审计（`:87-111`）——顺序是对的，且不依赖业务代码自觉。保留这个位置（换成 EF 原生的 `ISaveChangesInterceptor` + 聚合内领域事件派发更佳）。
3. **雪花 ID（客户端生成）的选择本身**（`Entity.cs:15`）：跨库唯一、无数据库往返、天然适合"每服务一个库"。要保留的是**这个属性**，不是它的实现（必须去掉静态定位器、显式声明 `ValueGeneratedNever`、workerId 走租约分配）。
4. **`Ardalis.Specification` 的引入与 `Specifications<T>.Create()` 工厂**（`Specifications.cs:8-16`、`MenuService.cs:68`、`RegionService.cs:68`、`PermissionService.cs:244`、`FileService.cs:257`）：这是全区域里**唯一真正"深"的抽象**——把"查询条件 + 排序 + 分页 + 投影"封装成命名对象，让查询可复用、可测试、可审查。重建时应把它提升为**唯一**的查询入口（并删掉 `IQueryable` 直通道）。
5. **Mapster 投影读路径**（`MappingRepository.cs:38-83` 的 `ProjectToType<T>`、`MapProfiles\MapsterProfile.cs`）：查询直接在 SQL 侧投影到 DTO（`ProjectToType` 生成 `SELECT` 指定列），是天然的 CQRS 读侧雏形；配合 `X.PagedList` 的分页（异步重载 `ToPagedListAsync`）已经把"分页 + 投影"做成了一条流水线。
6. **实体契约的极简分层**：`IEntity<TKey>`（含主键，作为"要建表"的标记）、`ISoftDelete`、`IAuditedEntity` 三个正交接口 + `Entity/EntityBase/AuditedEntity` 三级基类。接口与实现分离得好，重建时把基类改为"接口 + 显式配置"即可（去掉对 EF 基类的继承，让实体成为 POCO）。
7. **"无外键"这个决策本身**（`MigrationsSqlGenerator.cs:21-25` + `ModelBuilderExtensions.cs:73-83`）：在"每服务一库 + 应用层保证引用完整性"的前提下，去掉外键换取写入自由与跨库迁移便利，是一个可以讨论的工程取舍——**要保留的是"把这个取舍做成一层可测试的约定"的能力**（`ReplaceService` 钩子 + 生成器覆盖），不是当前的 Npgsql 继承实现（见发现 11）。
8. **`AddDbContextPool` + 显式关闭敏感日志**（`ServiceCollectionExtensions.cs:26,47-48`）：池化上下文与 `EnableSensitiveDataLogging(false)`/`EnableDetailedErrors(false)` 是生产默认值该有的样子（保留，但 `poolSize: 1024` 与连接串 `Maximum Pool Size=100` 需要对齐审视）。
9. **`RollbackAsync` 里"回滚后清跟踪状态"的意图**（`RepositoryBase.cs:99-117`）：显式回滚后清空 `ChangeTracker` 是很多人会漏的一步（否则回滚的改动会在下次 `SaveChanges` 复活）。保留意图，搬进未来的 UoW。
10. **`AddEntityComments` 的意图**（`ModelBuilderExtensions.cs:89-118`）：把 XML 文档自动变成表/列注释，对 DBA 与数据地图非常友好，PG/SQL Server 都支持。补齐 `GenerateDocumentationFile` 即可生效（见发现 15）。
11. **`SqlMigration\InitDatabase.sql` 作为"可交付给 DBA 的建库脚本"这一产物形态**（722 行、含 `__EFMigrationsHistory` 初始化）：模板项目里能给出"一条命令建库"的东西是对的；重建时保留产物、换成 CI 生成 + 一致性校验。
12. **消费端幂等（Redis 键）**（`Domain\NexusStack.RabbitMQ\RabbitOptions.cs:67-72`、`EventSubscriber.cs:447,540-561`、`Docs\MQ-Idempotency-Review.md`）：在没有 outbox 的前提下，消费者侧至少做了幂等；重建时把它演进为 inbox 表 + outbox 表（见缺失项）。

---

## 缺失项

1. **工作单元 / 事务边界抽象**：现无（写操作各自 `SaveChanges`，唯一事务在 `PermissionService.cs:98`）。需要 `IUnitOfWork`（`ExecuteAsync` + 执行策略包裹 + 一次提交），并禁止仓储内部提交。
2. **领域事件与 Outbox/Inbox**：实体无事件收集，事件由过滤器/服务直接发到 RabbitMQ（`OperationLogActionFilter.cs:85`→`OperationLogEventHandler.cs:16-20` 用**新 scope** 写库），**全仓无 outbox/inbox 表**（grep `[Oo]utbox|Inbox` 仅命中幂等相关文档）。数据库写入与消息发布是双写，进程崩溃即丢事件。
3. **强类型 ID / 值对象 / owned types**：全仓 0 命中；`UserId`/`RoleId`/`MenuId` 都是裸 `long`，互换无编译期保护。
4. **乐观并发控制**：无 `xmin`/`rowversion`/`IsConcurrencyToken`；`UpdateAsync` 是全列覆盖（发现 9）。
5. **唯一约束与 CHECK 约束**：22 张表、11 个索引全部是 FK 约定索引，0 个唯一索引、0 个 CHECK（`InitDatabase.sql:696-716`）；业务键（`UserName`、`Menu.Code`、`Role.Code`、`AppConfig.AppKey`、`ApiResource`）无库级保证。
6. **查询侧索引策略**：无 `CreatedAt`/`IsDeleted`/`SystemId`/`Code` 上的索引，而清理任务正是按这些列全表扫（`DailySchedule.cs:42-48` 对 `ScheduleTaskRecord.ExecuteEndTime`、`OperationLog.CreatedAt`、`AsyncTask.CreatedAt`）。
7. **每服务/每上下文独立的迁移所有权**：当前一套迁移挂在 Host（`ServiceCollectionExtensions.cs:40`），无"迁移与应用账号分离"、无破坏性变更审批、无回滚脚本、无 `--idempotent` 脚本进 CI。
8. **`ValueGeneratedNever` 级别的 ID 契约**（发现 3）：模型与 DDL 对"谁发号"没有一致声明。
9. **多租户过滤器**：`SystemId`/`AppId` 写了不用（发现 10）。
10. **读模型/查询侧（CQRS read side）**：无 Dapper/物化视图/只读副本/键集分页；树查询与跨表列表仍在内存或控制器里做（发现 7、14）。
11. **连接与命令的可观测性/健壮性**：无命令超时配置、无慢查询统计、无 `AddHealthChecks().AddNpgSql(...)`、无 EF 日志的脱敏策略（`NoLoggingAttribute` 只作用于操作日志）。
12. **密钥管理与配置校验**：连接串只在远端 AgileConfig（无本地 compose/`.env.example`），`GetConnectionString("PostgreSQL")` 无 null 校验（`ServiceCollectionExtensions.cs:21`），appsettings 里 `secret: <已脱敏>`；工作区缓存含生产口令（发现 17）。
13. **批量写入能力**：无 `COPY`/`SqlBulkCopy`/`ExecuteInsert` 路径（当前靠商业库 `Z.EntityFramework.Extensions`，见发现 8），大表导入（Excel/文件）没有数据层支撑。
14. **数据保留/归档策略**：只有硬删（`DailySchedule.cs:42-48`），无分区表/归档表/软删保留期设置。
15. **测试基座**：无测试工程、无 Testcontainers、无 Respawn、无内存/可替换提供程序路径（发现 19）。
16. **提供程序无关的公共 API**：`NpgsqlParameter`/`INpgsqlSingletonOptions`/`IConcurrencyDetector` 出现在技术库的公共面与实现里（发现 1、6、12）。
17. **软删除的审计完整性**：只有 `IsDeleted` 布尔，缺 `DeletedAt/DeletedBy`；批量软删不写审计（发现 8、10）。
18. **模型发现的自描述**：没有"发现了哪些程序集/实体"的启动日志或校验（发现 16），也没有"上下文只包含本上下文实体"的断言（发现 18）。

---

## 风险清单

| 严重度 | 问题 | 位置 |
|---|---|---|
| 阻断 | 主键双权威冲突：构造函数生成雪花 ID，DDL 是 `GENERATED BY DEFAULT AS IDENTITY`，序列因显式插入永不前进，`Id=0` 路径会取到 1 并与种子 admin（Id=1）冲突 | `Domain\NexusStack.EFCore\Entities\Entity.cs:15`、`AuditedEntity.cs:11`、`SqlMigration\InitDatabase.sql:9`、`Domain\NexusStack.Core\SeedData\UserSeedData.cs:30` |
| 阻断 | 启用了重试执行策略却用裸 `BeginTransactionAsync()`，唯一的事务用例会抛 `...does not support user-initiated transactions` | `Domain\NexusStack.EFCore\ServiceCollectionExtensions.cs:42`、`Repository\Base\RepositoryBase.cs:23-27`、`Domain\NexusStack.Core\Services\Users\PermissionService.cs:98` |
| 阻断 | 生产 PostgreSQL/Redis/RabbitMQ/阿里云 AK-SK 明文存在于工作区缓存，且模板打包配置（`NoDefaultExcludes` + `**\*`，排除列表无 `*.cache`/`agile`）会把它们打进模板包分发 | `Host\NexusStack.WebAPI\agile\config\*.cache`、`NexusStack.Template.csproj:18,22`、`.template.config\template.json:124-134`、`Install-Template.ps1:47` |
| 严重 | `InsertOrUpdateAsync` 调用 `ChangeTracker.Clear()`，静默丢弃同请求内其它服务的跟踪改动 | `Domain\NexusStack.EFCore\Repository\ServiceBase.cs:40,56`、`Repository\Base\RepositoryBase.cs:96` |
| 严重 | 全局 `NoTracking` + `Update(分离实体)` 全列覆盖 + 无并发令牌 ⇒ 后写覆盖先写且无冲突检测 | `ServiceCollectionExtensions.cs:35`、`RepositoryBase.cs:47-61`、`Domain\NexusStack.Core\Services\SystemManagement\MenuService.cs:150-153` |
| 严重 | 0 个唯一索引 ⇒ 业务键可重复，查重可被软删除绕过，`FirstOrDefault` 随机取一条 | `SqlMigration\InitDatabase.sql:696-716`、`Host\NexusStack.WebAPI\Migrations\20260307033208_InitialDatabase.cs:634-687`、`MenuService.cs:50` |
| 严重 | 批量/软删除走 `UpdateFromQuery/DeleteFromQuery`，绕过审计拦截器（软删后 `UpdatedAt/UpdatedBy` 不更新） | `ServiceBase.cs:66-71,80-93,137-154`、`Domain\NexusStack.Core\Schedules\DailySchedule.cs:42-48`、`Services\Users\UserTokenService.cs:147` |
| 严重 | 「多数据库支持」是文档承诺而非代码能力：`template.json` 只有 PostgreSQL 一个 choice，`AddEFCoreAndMySql` 无定义（解注释即编译失败），无 MySQL/SqlServer 包 | `README.md:158,120`、`.template.config\template.json:61-72,80-81`、`Domain\NexusStack.Core\ServiceCollectionExtensions.cs:143` |
| 严重 | 「移除外键」策略只覆盖 `CreateTableOperation`，`AlterTableOperation` 覆盖是空实现，未来 `AddForeignKeyOperation` 会静默落库；库侧无任何引用完整性 | `Domain\NexusStack.EFCore\MigrationsSqlGenerator.cs:21-32`、`ModelBuilderExtensions.cs:73-83`、`SqlMigration\InitDatabase.sql`（0 FK） |
| 严重 | 控制器直接用 `IQueryable` 跨表 join 并同步分页（审计上下文 join 身份上下文，阻塞线程） | `Host\NexusStack.WebAPI\Controllers\OperationLogController.cs:56-58,74`、`Controllers\TokenController.cs:129-131`、`Controllers\UserController.cs:50` |
| 高 | 实体构造函数依赖静态 `App.ServiceProvider` ⇒ 领域对象无法脱离进程/DI 创建，单测不可行 | `Entities\Entity.cs:15`、`Infrastructure\SnowFlake\SnowFlake.cs:17-24`、`Infrastructure\App.cs:27-37` |
| 高 | 迁移三份产物三个版本号（快照 10.0.3 / 脚本 10.0.7 / 包 10.0.12），且全仓无 `Migrate()`/CI 执行路径；迁移归属 Host | `MainContextModelSnapshot.cs`、`SqlMigration\InitDatabase.sql:718-719`、`NexusStack.EFCore.csproj:14-16`、`ServiceCollectionExtensions.cs:40` |
| 高 | 仓储接口 64 成员、泄漏 `IIncludableQueryable`/`ExpressionStarter`/`IDbContextTransaction`/`IPagedList`/`NpgsqlParameter`/`MainContext`，其中 10 个成员零调用 | `Repository\Query\IQueryRepository.cs`、`Repository\IServiceBase.cs:67`、`Repository\ServiceBase.cs:156-160`、`Repository\Mapping\IMappingRepository.cs:39,41` |
| 高 | 连接生命周期：`GetFromQueryAsync` 开连接不关、`ExecutePagedStoredProcedureAsync` 重复开且异常路径不关；池化上下文（1024）与连接串 `Maximum Pool Size=100` 不匹配 | `Repository\Query\SqlQueryExtensions.cs:48`、`Repository\ServiceBase.cs:171-215`、`ServiceCollectionExtensions.cs:49` |
| 高 | 模型发现依赖程序集名前缀且无空模型防线；失败模式是 `Set<T>()` 运行期异常或 `FindPrimaryKey()` 的 NRE | `DbContexts\ModelBuilderExtensions.cs:37-43`、`Infrastructure\TypeFinders\TypeFinders.cs:74`、`Repository\Query\QueryRepository.cs:138-140` |
| 高 | 树查询用 `LIKE '%父ID%'` 匹配物化路径（结果错误 + 索引失效），并把全表读入内存建树 | `Domain\NexusStack.Core\Services\SystemManagement\MenuService.cs:66,79`、`Services\SystemManagement\RegionService.cs:68-88` |
| 高 | 无 outbox/inbox：数据库提交与事件发布是双写，审计日志由新 scope 异步落库，崩溃即丢 | `Domain\NexusStack.Core\Filters\OperationLogActionFilter.cs:85`、`EventHandler\OperationLogEventHandler.cs:16-20`（全仓无 outbox） |
| 中 | 表/列注释链路断裂（无 `GenerateDocumentationFile`）⇒ DDL 里 200+ 条 `COMMENT ... IS ''`，`SeedDataTask.Name` 也落空串 | `DbContexts\ModelBuilderExtensions.cs:89-118`、`Infrastructure\Utils\DocsHelper.cs:32-44`、`SqlMigration\InitDatabase.sql:26-41` |
| 中 | `MainSaveChangeInterceptor` 每次 `SaveChanges` 建一个 DI scope 解析 `ICurrentUser`；后台服务/无 HttpContext 场景审计人写 0 且 `CreatedBy` 默认 0 而非 NULL | `MainSaveChangeInterceptor.cs:84-85`、`Entities\AuditedEntity.cs:36,46`、`Domain\NexusStack.Core\CurrentUser.cs:70` |
| 中 | 调度缓存键被 `Format` 两次 ⇒ 删除的键与写入的键不同，缓存失效永久失效 | `Domain\NexusStack.Core\Services\Schedules\ScheduleTaskService.cs:34,37,61`、`Infrastructure\Constants\CoreRedisConstants.cs:40` |
| 中 | 启动期 N+1：每个定时任务类型一次查询 + 一次写入 + 两次 Redis 往返 | `Domain\NexusStack.Core\Services\Schedules\ScheduleTaskService.cs:30-62` |
| 中 | 种子流程 `break` 中断后续种子（应为 `continue`），且每次启动重跑 | `Domain\NexusStack.Core\HostedServices\ExecuteSeedDataService.cs:104-107` |
| 中 | 批量能力依赖商业授权库 `Z.EntityFramework.Extensions.EFCore`（EF 原生 `ExecuteUpdate/ExecuteDelete` 已可替代） | `Domain\NexusStack.EFCore\NexusStack.EFCore.csproj:21`、`ServiceBase.cs:70,89,104,115,144` |
| 中 | 组织架构用 `Region`（国家/省/市/部门同一棵树）表示，`UserDepartment.Department` 指向 `Region`；同关系双表示（`User.Roles` + `User.UserRoles`） | `Domain\NexusStack.Core\Entities\Users\UserDepartment.cs:17-29`、`Infrastructure\Enums\RegionLevel.cs:27`、`Entities\Users\User.cs:94,99`、`Mapping\UserMapping.cs:17` |
| 中 | 零测试工程；启动强依赖远端 AgileConfig；模型构建硬绑 Npgsql（`INpgsqlSingletonOptions`）⇒ 数据层当前不可测 | `NexusStack.sln`/`NexusStack_Backend.slnx`（无测试项目）、`Domain\NexusStack.Core\ServiceCollectionExtensions.cs:81`、`MigrationsSqlGenerator.cs:15`、`appsettings.Development.json:2-12` |
| 低 | 死代码：`GetFromQueryAsync2`/`ExecuteSqlQueryAsync`（用 EF 内部 API，含 `pi.DeclaringType` 误用）、`SpecificationExtensions.Create` 忽略调用者返回空规格、`QueryRepository` 6 个成员零调用 | `Repository\Query\SqlQueryExtensions.cs:79-175`、`Repository\SpecificationExtensions.cs:10-15`、`Repository\Query\QueryRepository.cs:308-339` |
| 低 | `ServiceBase.BatchDeleteAsync/BatchSoftDeleteAsync` 声明了 `cancellationToken` 却未传递 | `Repository\ServiceBase.cs:66-71,80-93` |
| 低 | `IQueryRepository` 文档注释与实现相反（称 `asNoTracking` 默认 false，实际为 true），接口命名 `DeleteAsync` 同时表示软删/硬删 | `Repository\Query\IQueryRepository.cs:96-101`、`Repository\IServiceBase.cs:47,56`、`ServiceBase.cs:137-154` |
