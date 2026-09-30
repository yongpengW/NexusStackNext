# 14 — Aspire AppHost 与 ServiceDefaults

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 01

## **阻塞点已解除**（第 13 轮核实）

原阻塞点是"需要一组可用的中间件连接信息"。那些信息**现在就摆在那里**，
而且是第 10–12 轮逐一**实测**过的：

| 中间件 | 地址 | 实测 |
|---|---|---|
| PostgreSQL | `<服务器IP>:5432` | ✅ 真库跑着 507 条测试 |
| Redis | `<服务器IP>:6379` | ✅ 端口可达 |
| RabbitMQ | `<服务器IP>:5672`（管理 15672） | ✅ 6 条真 broker 验收 |
| AgileConfig | `<服务器IP>:8010` | ✅ 管理界面可用 |
| Seq | `<服务器IP>:5341` | ✅ 端口可达 |

**而且数据库只有一套**：ADR-0013 把"五个上下文各一个库"改成了"一个库、库内按 schema 分开"，
所以本票原文说的"5 个库"已经过时——**一个 PostgreSQL 就够了**。

### 两处与原描述不符的现状

**一、机器上有 WSL。** 原证据写"无 Docker / Podman / WSL"，实测 `wsl.exe` 存在
（有没有可用的发行版是另一回事）。不过按 ADR-0005，**这本来就不需要**：
"Aspire 只编排应用进程，中间件走外部依赖"——中间件已经在外面跑着了。

**二、Aspire 的包不在本地缓存里**，但**网络可用**——第 8 轮 `Microsoft.AspNetCore.Authentication.JwtBearer
10.0.12` 就是从 nuget.org 还原下来的（它不在本地缓存里，缓存只到 10.0.6）。所以装 Aspire 的包应该可行。

## 原始阻塞点（保留，供追溯）

ADR-0005 已改为"**Aspire 只编排应用进程，中间件走外部依赖**"。因此本票需要一组可用的中间件连接信息：

- PostgreSQL（5 个库：`nexus_identity` / `nexus_platform` / `nexus_scheduling` / `nexus_auditing` / `nexus_files`）
- Redis、RabbitMQ、Seq、（可选）AgileConfig

**请提供主机/端口/账号**，或告诉我复用哪一套现有环境。连接信息一律走环境变量/用户机密，**不入库**。

## 要做什么

- `aspire/NexusStackNext.AppHost`：编排 5 个服务 + 网关（本地进程），中间件以**外部连接**方式注入。
- `aspire/NexusStackNext.ServiceDefaults`：OpenTelemetry、健康检查、HTTP 韧性、服务发现。
- 本地注入路径与生产注入路径**都可用**，且不把 Aspire 的注入方式写进业务代码（ADR-0005 后果）。

## 验收标准

- [x] `dotnet run --project aspire/NexusStackNext.AppHost` 能拉起全部服务，Aspire 面板可见 ——
      **实测通过（第 18 轮）**：三个进程（AppHost + 平台宿主 + 网关）、5190/5191 都在听且
      `/health/live` **双 200**、面板 `http://localhost:49210/login` 返回 **200**、OTLP 49208/49209 在听；
      跑完按名字清掉那三个进程，五个端口**全部释放**。
- [x] 未设置连接信息时给出**可操作的**错误提示——实测：退出码 1，消息点名缺哪四个变量、去哪儿找、以及"只想跑单个服务不需要 AppHost"。
- [x] 把 AppHost 项目整体删除后，各服务仍可用环境变量独立启动——**实测**：先从 slnx 移除、再把整个 `aspire/` 目录移走，解决方案仍然**构建通过**（exit 0）。
- [x] OTel 追踪的**跨服务关联 ID** —— **实测通过**，见下。（"Seq 或 Aspire 面板"里的 Seq 在本仓不成立，见下。）

## 证据

- `review/03` 发现 3：网关因无条件 `UseRedis` + 空连接串在生产起不来。
- 本机环境：无 Docker / Podman / WSL，无本机 PostgreSQL/Redis/RabbitMQ；`dotnet new list` 也没有 Aspire 模板（需先装模板包）。

## Comments

### local

**第 14 轮：AppHost（4 条验收里的 2 条）**

#### 交付

`aspire/NexusStackNext.AppHost` + `scripts/run-apphost.ps1`。

#### 三处与票据原文不符的现状（都已纠正）

**一、只有两个进程，不是六个。** 票据写"编排 5 个服务 + 网关"。而 ADR-0013 已经把
五个平台能力合成**一个宿主**——所以是**平台宿主 + 网关**两个。

**二、不需要容器运行时。** ADR-0005 已改成"Aspire 只编排应用进程，中间件走外部依赖"，
所以这里**没有** `AddPostgres()` / `AddRedis()` / `AddRabbitMQ()`——那些会尝试拉容器。
现成的中间件本来就在外面跑着。

**三、Aspire 的版本号跳过了 10.x。** 第一版按 .NET 版本直觉写了 `9.5.1`，
它拖进来的 `MessagePack 2.5.192` **有已知高危漏洞**（NU1903，.NET 10 默认把审计当错误，
还原直接失败）。而 `Aspire.Hosting.AppHost` 根本没有 10.x —— 从 9.5.2 直接跳到 **13.x**。
改用 **13.6.0** 之后 0 警告、0 审计命中。

**顺带一条**：`IsAspireHost` 属性会让 SDK 报 `NETSDK1228`（已弃用的 Aspire **工作负载**路径）。
正解是 `<Project Sdk="Aspire.AppHost.Sdk/13.6.0">`——Aspire 现在通过 NuGet 包与 MSBuild SDK 提供。

#### 验收 3 验得很硬

先把 AppHost 从 slnx 移除 → 构建通过；再把**整个 `aspire/` 目录**移走 → 仍然构建通过。
**"没有焊死"不是声明，是实测。** 这正是 ADR-0005 想要的那个性质：
编排是编排，服务是服务，删掉编排服务照跑。

#### "可操作的错误"长什么样

```
缺少下面这些环境变量，AppHost 无法给出可用的连接信息：

  NEXUSSTACK_DB
  NEXUSSTACK_REDIS
  NEXUSSTACK_RABBITMQ
  NEXUSSTACK_SEQ

它们的值在 env/ 目录下的 *.dev 文件里（那些文件不进仓库）。
本地起法：先 `./scripts/run-apphost.ps1`，它会替你读进来。
只想跑单个服务的话，不需要 AppHost —— 见 README 的"两个进程"一节。
```

参照仓库的网关死在这一点：它抛的是"空连接串"，而那句话不告诉你缺哪个变量。

**写这段提示时我发现自己在引用一个不存在的脚本**（`run-apphost.ps1`）——
于是把它补上了。**提示里给出的路径必须是能用的，否则它比不提示更糟。**

#### 还剩两条

- **`dotnet run --project aspire/...` 真的拉起两个进程、面板可见**——没跑。
  它会占用 5190/5191 并留下长驻进程，值得单独一轮做完并清理。
- **ServiceDefaults + OTel 跨服务关联 ID**——还没写。

所以本票标 `claimed`。


### local

**第 15 轮：ServiceDefaults 与跨服务关联（验收 3/4）**

#### 交付

`aspire/NexusStackNext.ServiceDefaults`：OpenTelemetry（追踪 + 指标）、健康检查、HTTP 韧性、服务发现。
两个宿主都接上了。

**它刻意不依赖 Aspire**：有 OTLP 端点就导出，没有就只是不导出。
这是 ADR-0005 那条"不把 Aspire 的注入方式写进业务代码"的直接落实。

#### 验收 4 实测通过（这是本轮最有价值的一条）

起了**两个真进程**（平台宿主 5191、网关 5190），经网关发一个请求：

```
经网关 GET /api/identity        → HTTP 200
X-Correlation-Id                → 6c470604a22d4ff880efd3d0e7625aa5

平台宿主 TraceId  …  3958cfe0f71ff1dde32e76d20de07c9e  …
网关 TraceId          3958cfe0f71ff1dde32e76d20de07c9e

✅ 两个进程共享 1 个 TraceId
```

脚本留在 `scripts/verify-cross-service-trace.ps1`，可重复跑。

#### 一个**前提是错的**：本仓不用 Seq

票据写"OTel 追踪能在 **Seq** 或 Aspire 面板看到"。核实结果：

- 本仓两个宿主的 `appsettings.json` 里 Serilog 只有 **Console** 一个 sink；
- 端口 5341（参照仓库配置里那个）**不是 HTTP 服务**——原始 HTTP 请求没有任何响应。

**Seq 是参照仓库的东西，不是本仓的。** 而本仓的日志模板配了 `Enrich: WithSpan` ——
**每条日志自带 TraceId**，所以"跨服务关联"在控制台上就能验。

**连带修掉一处我自己造的假**：AppHost 第一版往宿主注入了
`Serilog__WriteTo__0__Args__serverUrl`——那是我照参照仓库想当然写的，
本仓没有任何东西消费它。已删，换成 `OTEL_EXPORTER_OTLP_ENDPOINT`（配了才导出）。

#### 验收 1 没做成：卡在一张**未受信任**的开发证书

`dotnet run --project aspire/...` 起不来：

```
Unable to configure HTTPS endpoint. No server certificate was specified,
and the default developer certificate could not be found or is out of date.
   at Aspire.Hosting.Dashboard.DashboardServiceHost.ConfigureListen
```

**Aspire 的面板自己要用 HTTPS。** 核实过：证书**存在且有效**，但**未受信任**：
`dotnet dev-certs https --check` 说 "A valid certificate was found"，而 Aspire 要的是受信任的。

`dotnet dev-certs https --trust` 会**把证书装进受信任的根存储**——那是机器级的安全改动，
**我没有擅自做**。所以这条验收的状态是"被环境挡住，且修法需要你点头"。

两个选择：信任那张开发证书，或者用别的只看"服务起没起"的方式验（面板本身看不到）。

#### 三条验收

| 验收 | 状态 |
|---|---|
| AppHost 拉起全部服务、面板可见 | ❌ 被未受信任的开发证书挡住（见上） |
| 未设连接信息时给出可操作的错误 | ✅ 实测 |
| 删掉 AppHost 后各服务仍能独立启动 | ✅ 实测（整个 `aspire/` 目录移走仍构建通过） |
| OTel 跨服务关联 ID | ✅ **实测**（两个进程共享一个 TraceId） |


### local

**第 17 轮：验收 1 的四个环境阻塞，逐个定位**

第一次失败之后我没有停在"证书受信任"这一条上，而是**继续往下试**——因为每修一个就会露出下一个，
而"它起不来"这句话对下一个接手的人没有任何用。

| # | 症状 | 处理 |
|---|---|---|
| 1 | 面板要 HTTPS，开发证书**未受信任** | `ASPIRE_ALLOW_UNSECURED_TRANSPORT=true` **绕开**（不动你的证书存储 ✓） |
| 2 | `C:\Users\Administrator\.aspire\cli\bch` **拒绝访问** | 手动建出该目录 ✓ |
| 3 | `Cannot open log for source '.NET Runtime'` —— **致命**，进程直接崩 | `Logging__EventLog__LogLevel__Default=None` ✓ |
| 4 | `Directory.CreateTempSubdirectory` **拒绝访问** | **诊断后停止**，见下 |

阻塞 3 值得单独说：Aspire 默认**顺便**往 Windows 事件日志写一份。写不进去时它不是警告，
而是 `Unhandled exception` 把整个进程带走。**一条附带日志失败杀掉主程序**，而那行错误
（"Cannot open log for source"）与"AppHost 起不来"之间的联系需要读完整堆栈才看得出来。

#### 阻塞 4：诊断过，**不是 ACL 问题**

`Directory.CreateTempSubdirectory` 在 **4 个不同目录**下都失败——包括我刚用
`New-Item` 建出来的、自己拥有的目录。所以它不是目录权限。

用 `diagnose-windows-sandbox-acl` 技能跑了一遍（只读诊断）：

```
writeDac: true    writeOwner: true    denies: []    packageAllowSids: []
verdict: NOT_THIS_CLASS
reason: No package allow ACE was observed and both required rights are available.
        Other ACL restrictions and causes of the original failure are not ruled out.
```

**不是 ACL 这一类。** 技能的规矩是这种情况下**报告并停止，不要推断修复**——照办了。

**一条精确观察**：同一个目录里，
- `New-Item -ItemType Directory` → **成功** ✓
- .NET 的 `Directory.CreateTempSubdirectory` → **拒绝** ✗

**只有那一个 API 失败。** 而 Aspire 的 DCP 正好用它（`Locations.GetOrCreateDcpSessionDir`
→ `TempFileSystemService.CreateTempSubdirectory`），所以这不是我能绕开的。

诊断报告留在 `.scratch/nexusstack-next/review/acl-report/`。

#### 结论

验收 1 **在本机无法完成**，卡点与代码无关，且已经定位到**具体一行 API**。
换一台 `CreateTempSubdirectory` 正常的机器，或等这台机器的该问题被解决之后，再来跑这条。
**其余三条验收全部实测通过**，包括那条最有价值的跨服务关联 ID。

### local

**第 18 轮：验收 1 做成，而根因与上一轮的结论**相反**（2026-09-30）**

上一轮的结论是"**卡点与代码无关，换台机器才能验**"。有人问了一句"这个问题的原因弄清楚了吗"，
于是把它当问题查了一遍——**结论是反的：根因就在代码里，而且能修。**

#### 一、"机器上那个 API 坏了" —— 不成立

`Directory.CreateTempSubdirectory` 在 **.NET 10 宿主**里当场跑通（临时控制台工程，同一个 `TEMP`）：

```
运行时: .NET 10.0.12
CreateTempSubdirectory → 成功: C:\Users\Administrator\AppData\Local\Temp\nexusstack-probe-pc0syqss.h11
Directory.CreateDirectory → 成功（对照）
```

这台机器的那个 API **是好的**。上一轮那次失败是**当时的运行上下文**造成的：最可能是会话的
**文件沙箱**——它拒绝工作区之外的写入，而"沙箱拒绝"与"ACL 拒绝"在消息上长得一样
（这也是为什么 ACL 诊断技能给的是 `NOT_THIS_CLASS`：它没看错，它只是不在那一类里）。
**"换个环境再试"当时被读成了"换台机器才能验"——这两句话不是一回事。**

#### 二、真正挡住验收 1 的，是**代码里一个没人读的变量**

修完三处环境阻塞之后，AppHost 仍然退出 1：

```
缺少下面这些环境变量，AppHost 无法给出可用的连接信息：
  NEXUSSTACK_DB
  NEXUSSTACK_REDIS
```

查下去是**两处**，都在本仓的代码/脚本里：

1. **AppHost 要求 `NEXUSSTACK_REDIS`，而本仓不用 Redis。** 核实过：`src/` 里 "Redis" 只出现在
   **注释**里（讲"要跨实例共享时该换 Redis 之类"的升级路径）、`Directory.Packages.props` 没有
   Redis 客户端、两个宿主的 `appsettings.json` 里连 `Redis` 节都没有——而它还把值注成
   `Redis__Configuration`，**没有任何东西读那个键**。
   这与本票第 15 轮抓到的 Seq 注入是**同一类错误**：照参照仓库的技术栈想当然写的。
2. **启动脚本只读 `env/platform.dev`，而库连接串住在 `env/test.dev`**（`NEXUSSTACK_TEST_POSTGRES`）
   ——于是 `NEXUSSTACK_DB` 永远推导不出来。**错的是读法，不是配置。**

两处都已修：AppHost 不再要求也不再注入 Redis（并在代码里写下"**这个变量有没有人读，是一个能查的事实**"）；
`run-apphost.ps1` 改成"主文件 + 用 `test.dev` 补**没有的**键（不覆盖）"。

#### 三、上一轮定位的三处环境阻塞，现在**写进脚本**了

它们此前只住在这张票的注释里，所以下一个人跑脚本会再撞一遍：

- `ASPIRE_ALLOW_UNSECURED_TRANSPORT=true` —— 面板默认要 HTTPS，而开发证书未受信任；
  **不动你的证书存储**（那是机器级安全改动，得你点头）；
- 建出 `%USERPROFILE%\.aspire\cli\bch` —— 目录不存在时 Aspire 报的是"**拒绝访问**"而不是"不存在"，
  那句话会把人往权限方向带；
- `Logging__EventLog__LogLevel__Default=None` —— **一条附带日志失败会杀掉主程序**。

#### 四条验收

| 验收 | 状态 |
|---|---|
| AppHost 拉起全部服务、面板可见 | ✅ **实测**（三进程 / 双 200 / 面板 200 / 端口释放） |
| 未设连接信息时给出可操作的错误 | ✅ 实测 |
| 删掉 AppHost 后各服务仍能独立启动 | ✅ 实测 |
| OTel 跨服务关联 ID | ✅ 实测 |

#### 留下的教训（与 `AGENTS.md` 那几条同源）

**"我试了三次都失败"和"这件事在这台机器上做不到"是两句话。** 上一轮把它们合并了，
于是一处**本来能在代码里修的问题**被记成了"换台机器才能验"，而它就此停了一轮。
**换环境重试的时候要问"我换掉了什么"**——这次换掉的恰好是问题所在的那一层；
而"换个环境就好了"如果没有解释为什么，那它只是把原因挪出了视野。
