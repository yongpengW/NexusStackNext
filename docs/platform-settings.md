# 全局设置访问与持久化

GlobalSetting 是受限的平台元数据。它可能描述邮件、短信或外部应用接入，不能因为是配置就默认公开。
秘密保存在环境或配置中心；设置中的接入信息应使用秘密引用，不能把该模块作为秘密管理器。

所有设置接口都需要有效登录会话和对应操作权限。根管理员用于初始化授权；普通用户通过现有
菜单 → API 资源 → 角色 → 用户链取得权限。读取分组和读取单值是两个独立的授权项。

| 操作 | 注册的路由模板 | 方法 |
|---|---|---|
| 读取单值 | `/api/platform/settings/{key}` | GET |
| 分页读取分组 | `/api/platform/settings` | GET |
| 创建或更新 | `/api/platform/settings/{key}` | PUT |
| 清空值 | `/api/platform/settings/{key}` | DELETE |

权限当前针对操作，不按 scope 或配置键隔离。不要给不应读取整个设置存储的账号授予读取权限。
匿名返回 401，有效登录但无对应权限返回 403；已登出、禁用或缺少会话版本的令牌不能因持有根声明而继续访问。
身份来源故障时请求失败，不使用 Pricing 普通查询缓存或旧会话状态放行。

三份网关路由表均要求 Platform GET 认证；模块内部仍校验会话与权限，直连也必须满足相同要求。
这是原匿名读取契约的有意收紧。需要公开站点名称等信息的项目应提供明确的公开投影，不要重新开放整个设置仓储。
根账号播种和授权操作见 Identity HTTP 接口；`PlatformSettingsAccessTests` 提供完整的授权、读取和登出旅程。

Identity 与 Platform 是共同授权模块的两个真实消费者，HTTP 过滤器因此移到 BuildingBlocks.Web。
会话校验端口由 Identity 实现；Platform 不读取 Identity 表，也不引用它的实现程序集。
普通接口、分页、ProblemDetails 及 OpenAPI 的既有响应保持一致。

权限缓存目前仍限同一个平台宿主的进程内失效，
不能从共享了 HTTP 过滤器推导出多副本即时撤权或独立业务宿主远程授权已经完成。

验证接口：实际平台 HTTP、三份已发布路由的真实 YARP HTTP、独立测试数据库故障后的授权拒绝与恢复。
`scripts/verify-write-paths.ps1` 使用临时内存宿主的测试管理员验证设置写入和读回，不访问共享配置中心。

## 存储与迁移

Platform 默认使用 PostgreSQL，配置 `ConnectionStrings__Platform`（配置中心键为 `ConnectionStrings:Platform`）。
它可与 Identity 的连接指向同一物理库，但拥有独立的 `platform` schema、DbContext、事务和迁移历史。
两个连接配置都须显式提供；Aspire 将同一数据库参数显式注入两处。

首次或升级先执行 `pwsh -File scripts/migrate-platform.ps1`。脚本只从私有 `env/platform.dev`
或进程环境读取 Platform 连接；发布产物使用 `dotnet NexusStackNext.PlatformHost.dll migrate-platform`。
独立命令不启动 Web、配置中心、broker 或账号播种，重复执行幂等。身份迁移仍独立执行
`scripts/migrate-identity.ps1`；正常宿主启动只检查，不自动迁移。

缺连接配置、未应用迁移或数据库不可用会阻止启动。运行期 `/health/ready` 检查 Platform 表能否读取，
`/health/live` 只检查进程。日志中的迁移和启动诊断不输出原始连接异常。
内存演示须显式选择 `Platform__Storage__Provider=Memory`，仅允许 Development / Testing；
无库平台宿主还须显式设置 `Identity__Storage__Provider=Memory`、`Files__Storage__Provider=Memory` 与 `Auditing__Storage__Provider=Memory`。

## 写入与并发

写入成功的 204 表示该配置聚合已提交。稳定键有唯一索引，值、说明和版本在同一次保存中提交；
清空只移除值，保留键和说明。Scope 按第一段精确匹配，`mail` 不会命中 `mail-temp`。

单值和列表返回 `version`，单值同时返回 `description`。未知键返回 `value=null, version=0`；
已注册但清空的键仍有正版本。同值、同说明写入保持版本；值与说明各自变化时各递增一次。

管理端读取后编辑，应在 PUT JSON 中提交 `expectedVersion`，DELETE 则用查询参数 `expectedVersion`。
0 表示只创建尚未注册的键，正数表示只修改该版本；负数返回 400。版本过期或竞争创建返回
409 / `platform.setting.conflict`，调用方重新读取并决定如何处理，不会在后台自动覆盖冲突。

省略客户端版本保留原先的“设置为这个值”语义；它不能识别调用方在请求之前看过的旧数据，
因此不要用它实现用户的读后编辑。即便省略版本，服务器读取到提交之间仍有数据库版本条件；
并发修改不会把不同请求的值与说明拼成一条成功记录。内存适配器也使用快照和版本比较提交，
从前一次读取获得的状态不会随另一个请求的修改而改变。

`GlobalSettingChanged` 目前仍是领域内事实，没有已登记的跨上下文消费者，因此没有对外集成事件映射。
基础表包含 Outbox/Inbox 不代表设置可靠通知或持久审计已完成；真实审计消费者在后续票据接通。
受限元数据不会因为本轮持久化而被广播。

验证涵盖独立临时数据库、授权 HTTP 重启读写、重复迁移、竞争创建和条件更新、保存失败回滚、
独立于 Identity 的数据库掉线/恢复，以及默认持久化和开发内存模式的启动约束。
重叠更新验收用临时库的行锁保证两个请求都读到旧版本；移除数据库版本条件后测试会失败，
恢复后只允许一个请求成功。结果通过 HTTP 检查，锁和活动查询只用于建立重叠条件。

依据：[EF Core 事务](https://learn.microsoft.com/en-us/ef/core/saving/transactions)、
[EF Core 乐观并发](https://learn.microsoft.com/en-us/ef/core/saving/concurrency)。

参考：[ASP.NET Core Minimal API 认证与授权](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/security?view=aspnetcore-10.0)。
框架的 RequireAuthorization 负责要求认证，模块的权限规则仍须显式注册；只要求登录不能代替业务授权。
