# 51 — 平台能力合并为一个宿主与一个库

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 50

> 用户对"每个上下文一个数据库"提出质疑：Identity / Platform / Scheduling / Auditing / Files
> 是**基础服务**，设计好后基本不变，放进一个基础设施库更符合常理；
> 未来扩展的**业务模型**才该按业务场景独立分库。

**质疑成立，而且指向的不只是数据库。**

## 我犯的推理错误

DDD 本来把子域分三类（核心域 / 支撑子域 / 通用子域），**对三类的建议不同**。
这五个全是通用或支撑子域——不是差异化所在，都有成熟替代品。
而我把同一条规则均匀地套在了五个性质相同、且都不是核心域的东西上。

更根本的是把**三件事当成一件**：限界上下文（建模边界）、可部署单元（运维边界）、
数据库（数据边界）。**DDD 从没说这三者要 1:1:1**——
我从"5 个上下文"直接推到"5 个服务 × 5 个库"，中间那一步没有论证过。

## 推到自洽的形态

只合并数据库、保留五个服务 = **分布式单体**（分布式部署的全部复杂度 + 共享数据的全部耦合）。
**共享一个库的前提是共享一个进程**，二者必须一起选。用户选了包 B。

## Answer

```
src/Services/<Ctx>/{Domain,Application,Infrastructure}   五个模块，程序集边界不变
src/Services/<Ctx>/NexusStackNext.<Ctx>.Api/             模块的 HTTP 面
src/Hosts/NexusStackNext.PlatformHost/                   唯一宿主，显式组装五个模块
数据库：nexusstack_platform，库内五个 schema
```

模块对宿主暴露两个扩展方法：`Add<Ctx>Module(...)` 与 `Map<Ctx>Endpoints(...)`。

**`Failure(Error)` 仍然各模块各写一份**——四个模块的映射**确实不同**：
Platform 全部 400、Files 的 `not_found` 是 404、Scheduling 的 `task.not_found` 是 404、
Identity 还区分 409 冲突。这印证了票据 48 里那个"例外"是决定而非遗漏。

## 什么没有变（决定了风险面）

- 五个上下文的 `Domain` / `Application` / `Infrastructure` 程序集照旧独立
- **跨上下文引用仍然为零**；架构不变量测试**一条没改**
- 网关的路由与授权策略**逐条不变**（公开 200 / 需认证 401，逐条实测）
- **所有路由前缀不变**，因此合并没改任何端点路径

## 分阶段执行，每阶段都验证

| 阶段 | 内容 | 验证 |
|---|---|---|
| 1 | 五个 `*.Api` 抽出 `Add*Module` / `Map*Endpoints`，**仍可独立运行** | 构建 + 424 测试全绿 |
| 2 | 新建宿主组装五个模块 | 一个进程里五个上下文的端点全部可用；真实业务链路（建用户→建角色→授权→审计投递）跑通 |
| 3 | 模块转为库（删入口点与自带 appsettings） | — |
| 4 | 网关指向唯一后端 | 2 进程端到端 |

## 合并暴露的一个真问题

阶段 2 之后 **`/openapi/v1.json` 返回 500**：

```
System.ArgumentException: An item with the same key has already been added. Key: T:Program
```

五个模块各自还留着 `Program.cs`（顶层语句生成全局命名空间里的 `Program` 类），
加上宿主一共**六个同名类型**，OpenAPI 的类型登记撞了。

**删掉模块的入口点**（它们本来就不该有）后恢复 200，文档里 **20 条路径**。

**这正是"每阶段都做运行时验证"的价值**：不验证的话，这个 500 会留到用户第一次
打开 `/openapi/v1.json` 时才被发现——而它是这次合并**唯一**引入的回归。

## 一个顺带消失的配置风险

五个进程时每个都要不同的 Snowflake `WorkerId`，配重了会产生重复 ID 且**不报错**。
现在只有一个进程，只需要一个。

## 实测（2 个进程）

```
平台宿主 /health/live 200  /health/ready 200  /openapi/v1.json 200（20 paths）
网关     /health/live 200  /health/ready 200
经边缘：公开 GET 200 / 需认证 POST·GET 401   ← 与合并前逐条一致
停掉平台宿主 -> 网关 ready 503，live 仍 200；重启 -> 200
```

## 产出

- 新增 `src/Hosts/NexusStackNext.PlatformHost`
- 五个模块改为库形态（普通 SDK + `FrameworkReference` + 显式 ASP.NET 隐式 using）
- `src/Gateway/.../routes.json`：4 个 cluster → **1 个**
- **ADR-0013**（新）、**ADR-0002 标注部分被取代**（保留原文并说明适用范围收窄）
- `AGENTS.md` / `README.md` 布局与运行说明
- `config/`：7 个应用模板 → **3 个**

## 一个仍未处理的命名问题

`src/Hosting/`（宿主组装**库**）与 `src/Hosts/`（**宿主**）并排，名字太近。
我倾向于把前者重命名为 `src/Composition/NexusStackNext.Composition`，
但那是纯改名、涉及十几处引用，留到下一次单独做——**不为顺手的美化冒险**。
