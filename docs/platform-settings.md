# 全局设置访问

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

本轮仍使用 Platform 内存适配器，重启持久化将由下一切片完成。权限缓存目前仍限同一个平台宿主的进程内失效，
不能从共享了 HTTP 过滤器推导出多副本即时撤权或独立业务宿主远程授权已经完成。

验证接口：实际平台 HTTP、三份已发布路由的真实 YARP HTTP、独立测试数据库故障后的授权拒绝与恢复。
`scripts/verify-write-paths.ps1` 使用临时内存宿主的测试管理员验证设置写入和读回，不访问共享配置中心。

参考：[ASP.NET Core Minimal API 认证与授权](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/security?view=aspnetcore-10.0)。
框架的 RequireAuthorization 负责要求认证，模块的权限规则仍须显式注册；只要求登录不能代替业务授权。
