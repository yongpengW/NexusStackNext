# 配置中心基线

这里是可以**导入 AgileConfig** 的配置模板，**不含任何凭据**——真实值由你自己填。

划分依据见 [`docs/adr/0012-agileconfig-app-layout.md`](../docs/adr/0012-agileconfig-app-layout.md)。

## 三个应用

| 文件 | 对应应用 | 放什么 |
|---|---|---|
| `nexusstack_shared.template.json` | **基座** | `AllowedHosts`、`DatacenterId`、`Serilog` 级别开关与 enricher、`RabbitMQ`、`Redis` |
| `nexusstack_platform.template.json` | **平台宿主** | 连接串、`WorkerId`、`Files:StorageRoot`、`Serilog:WriteTo` |
| `nexusstack_gateway.template.json` | **网关** | `Cors`、`Gateway:RouteTablePath`、`Serilog:WriteTo` |

两个宿主应用都要**关联（继承）** `nexusstack_shared`。

**为什么只有三个**：五个平台能力（Identity / Platform / Scheduling / Auditing / Files）
是通用子域，一起演进、一起部署，因此是**一个进程、一个库**（库内按 schema 分开）——
见 [ADR-0013](../docs/adr/0013-platform-capabilities-are-one-host.md)。
未来的**业务上下文**各自独立成服务，那时才各自需要自己的应用与库。

## 两条不要越过的线

1. **基座里不放 `ConnectionStrings`** —— 数据边界不靠"大家自觉"。
2. **基座里不放 `WorkerId`** —— 雪花 WorkerId 必须每进程唯一，共享会产生重复 ID，
   而**它不会报错**，只在数据里留下两条同 ID 的记录。

## 数组不合并

AgileConfig 的继承按配置项合并，**数组整体替换、不逐元素追加**。
所以两个宿主模板里都重复了 Console sink：一旦宿主应用定义了 `Serilog:WriteTo`，
基座里的那个数组就整个失效，而"丢掉 Console sink"的表现是**一行日志都看不到**。

加 sink 时请在每个宿主应用里重复它。

## 导入后还要做的

1. 把各文件里的空值填成真实值——`Host`、`Username`、`Password`、`Redis.ConnectionString`
   这几个键目前都是空的
2. 建**一个** PostgreSQL 库 `nexusstack_platform`；库内按 schema 分开
   （`identity` / `platform` / `scheduling` / `auditing` / `files`），由 EF 迁移自动创建
3. 在两个宿主的启动环境里设 `AgileConfig__AppId` / `__Secret` / `__Nodes`
4. 确认日志里出现"已接入 AgileConfig：AppId=…"

## 离线仍然可用

没配 AgileConfig 时应用**照常启动**（走各自的 `appsettings.json` 兜底形态），
配置中心不可达时同样不致命。这是 ADR-0006 要求的降级路径。
