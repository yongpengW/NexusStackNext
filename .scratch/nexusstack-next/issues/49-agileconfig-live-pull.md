# 49 — AgileConfig 真实拉取：需要你的凭据

Status: resolved
Type: task
Labels: needs-info
Blocked by: 48

> 接入代码、优先级顺序与降级路径都已完成并验证（票据 48）。
> **只差把它指向一个真实的 AgileConfig 服务器**——那需要你的地址与凭据。

## 已经做完的

- 配置源接入（`AddAgileConfig`）。~~插到环境变量之前~~ —— **这个说法已被票据 56 证伪，见文末**
- 优先级有测试守着：环境变量 > AgileConfig > appsettings.{Env}.json > appsettings.json
- 降级路径实跑验证：未配置时不致命；**不可达时也照常启动**
- `README.md` 写明了顺序与凭据的提供方式

## 需要你做的

### 方式一：你自己设环境变量（推荐，凭据不进对话）

在**每个你想连配置中心的宿主**的启动环境里设：

```powershell
$env:AgileConfig__AppId  = "your-app-id"
$env:AgileConfig__Secret = "your-secret"
$env:AgileConfig__Nodes  = "http://your-agileconfig:5000"
```

然后告诉我"设好了"，我来验证真实拉取。

### 方式二：把连接信息给我

我会写进环境变量跑验证，**不写入仓库**（`assert-no-credentials.ps1` 会挡）。
但凭据会出现在这次对话里——你自行判断是否接受。

## 验收（拿到凭据后我要做的）

1. 在 AgileConfig 里建一个**只有本模板用得到**的配置项，例如
   `Serilog:MinimumLevel = Warning`
2. 启动宿主（环境变量已设），确认：
   - 启动日志出现"已接入 AgileConfig：AppId=…"
   - **该配置项真的生效**（INFO 行数应变成 0——与票据 48 的变异验证同一个手法）
3. 制造**冲突**验证优先级：同一个键在 `appsettings.json` 与 AgileConfig 里都设成不同的值，
   确认 AgileConfig 赢；再设一个**同名环境变量**，确认环境变量赢
4. 记录到票据与 `map.md`

## 一个已知的局限（不是我能解决的）

`AgileConfig.Client` 是 `netstandard2.0`、依赖 `Microsoft.Extensions.*` **3.1.x**。
在 .NET 10 上程序集版本会向上统一，实测能跑（降级路径与不可达路径都验证过），
但上游依赖已经落后很多版本。**如果真实拉取时出现版本冲突，那会是原因。**

另：该客户端的日志走的是**它自己的 logger**，不经过 Serilog 的模板——
所以它的 `fail:` / `trce:` 行格式与其它日志不同。这不是配置错误。

## 结清（由票据 55 / 56 完成）

用户提供了真实的 AgileConfig 地址、AppId 与密钥之后：

- **真实拉取成功**：`?env=DEV` 拉到 30 个键，含从 `nexusstack_shared` 继承的 RabbitMQ / Redis / Serilog
- **选项映射补全**：`AgileConfig` 配置节 → `ConfigClientOptions` 的 13 个属性（票据 55）
- **优先级冲突验证**（票据 56）：**失败** —— 见下

### 更正：本票里那句"插到环境变量之前"是错的

原文写着"配置源接入（`AddAgileConfig`），并插到**环境变量之前**"，
优先级那 4 条测试也全绿。**但那不是真的。**

`WebApplicationBuilder.Configuration` 是 `ConfigurationManager`，
对它 `Sources` 的**重排不生效**——配置中心实际盖过了环境变量。
那 4 条测试用的是普通 `ConfigurationBuilder`，**测的不是宿主真正用的那个类型**。

改用"追加一个环境变量来源"之后才真正成立，端到端验证：
设 `Files__StorageRoot` 为某个目录 → 该目录被创建（修之前不会）。

详见票据 56。**这条更正留在这里，因为读到本票的人会先看到那句话。**
