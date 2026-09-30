# 50 — 配置中心接入方案：一个基座 + 六个宿主

> **2026-09-29 已被 [票据 51](51-platform-capabilities-merge.md) 部分取代。**
> 本文按"五个平台能力各自一个宿主"设计了 7 个应用；随后用户质疑"这五个是基础服务、
> 该共用一个基础设施库"，质疑成立——现在它们合并为**一个宿主**，
> 应用数从 7 变成 **3**（基座 + 平台宿主 + 网关）。见 [ADR-0013](../../docs/adr/0013-platform-capabilities-are-one-host.md)。
>
> **下面保留原文**：两条硬规则（基座不放连接串、不放 WorkerId）与"数组不合并"这个坑
> **仍然完全有效**，是本文真正的价值所在。

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 48

> 用户给出旧项目的 AgileConfig 后台截图（3 个应用：API/MQ/PlanTask，后两者**关联** API）
> 与一份导出的 `nexusstack_api.json`，问"新版本拆成多个宿主，配置需要怎么接入"。

## 决定

**7 个应用**：一个基座 + 六个宿主，宿主关联（继承）基座。
沿用参照仓库已验证的继承模式，只是应用数从 3 变成 7（可部署单元从 3 变成 6）。
见 `docs/adr/0012-agileconfig-app-layout.md`。

## 两条硬规则（都是旧配置里现在就有问题的地方）

### 基座里绝不放 `ConnectionStrings`

用户导出的是 `Database=nexusstack`——**一个库**。而 ADR-0002 写的是"每个上下文独占一个数据库"。
放进基座，六个宿主就继承到同一个库，等于在**配置层**把不变量 1 抹掉。

### 基座里绝不放 `WorkerId`

旧配置 `WorkerId=1` 是共享的。六个进程共用会**产生重复的雪花 ID**——
不立刻报错，只在数据里留下两条同 ID 的记录。

## 查出的三个真问题

### ① 我上一轮的 Serilog 接线**吃不下**他的配置（会静默失效）

他的配置是标准形态（`Using` / `WriteTo` / `LevelSwitches` / `Enrich`，
且 `MinimumLevel` 是 `{ControlledBy: "$controlSwitch"}` **对象**）。
我上一轮手写读 `Serilog:MinimumLevel`（字符串）——`Enum.TryParse` 会失败，
**静默退回 Information，LevelSwitches 整个失效**。典型的"配置写对了但不生效"。

**改成 `ReadFrom.Configuration`**（+ `Serilog.Settings.Configuration`）。
兜底条件也改了：不是"永远加控制台"，而是"**配置里零个 sink 时**才加"——
否则同一个事件会被打印两遍。

**三种情形实跑验证**：

| 情形 | INFO 行数 |
|---|---|
| `$controlSwitch = Information` | 5（未走兜底） |
| `$controlSwitch = Warning` | **0** ← 证明 `ControlledBy` 生效 |
| 无 Serilog 节 | 6，且日志明说走了兜底 |

### ② 他配置里的 `Enrich` 会让宿主**启动时抛异常**

`Enrich: [WithSpan, WithThreadId, WithThreadName]` 需要
`Serilog.Enrichers.Span` 与 `Serilog.Enrichers.Thread`。缺了它们，
`ReadFrom.Configuration` 报"找不到 enricher"并**在启动时失败**。

已补两个包并实证：日志里 `[2]`（ThreadId）与请求期间的 `[51e9af52…]`（TraceId）都被填充。

### ③ 凭据规则漏掉了公网地址

拿现有规则扫他的真实文件：`Password=` 与 `<已知口令样本>` 命中，
但 `<服务器IP>`（出现 3 次）与用户名 `leowang` **零命中**——
因为"内网地址"规则只覆盖 `10/172/192.168`。

**规则从 6 条扩到 8 条**：加了"配置里的真实 IP"（排除回环与未指定）与两条用户名规则。

**过程中修掉两次我自己的错误**：
- 第一版用户名规则写成 `Username\s*=\s*...`，**误伤 3 处模板代码**（`userName = UserName`，
  `Select-String` 默认忽略大小写）→ 收紧成只在连接串或 JSON 键形态下命中
- 收紧后 `连接串用户名` 在真实文件上又 **0 命中**——规则里 `[^;]` 把连接串中间的 `;` 排除了 → 改成 `[^"]`

**两个方向都验**：真实文件 5 类命中；模板生成物 237 文件、8 规则、**0 误伤**。

**还顺带抓到一次真阳性**：我自己的 `config/README.md` 里写了 `` `Password=` `` 这种措辞，
被规则判成凭据。**规则没错，是措辞有问题**——改措辞而不是放宽规则。

## 产出

| 文件 | 内容 |
|---|---|
| `config/nexusstack_shared.template.json` | 基座：Serilog 级别开关、RabbitMQ、Redis、AllowedHosts、DatacenterId |
| `config/nexusstack_<6 个宿主>.template.json` | 各自的连接串、WorkerId、WriteTo（**重复 sink，因为数组不合并**） |
| `config/README.md` | 导入步骤、两条不要越过的线、数组不合并的坑 |
| `docs/adr/0012-agileconfig-app-layout.md` | 决定与备选方案 |
| 六个宿主的 `appsettings.json` | 离线兜底形态（无凭据、可离线启动） |
| `README.md` | 「## 配置」补上应用划分与两条硬规则 |

## 一个我没等到答复就按 ADR 执行的点

问数据库怎么分时用户没选。我按 **ADR-0002 原样执行**：
五个库 `nexusstack_identity` / `_platform` / `_auditing` / `_files` / `_scheduling`，
五个连接串只有库名不同。**若实际想共用一个库，需要同步修改 ADR-0002**，不是改几个字符串。

## 当前状态

构建 0 警告 0 错误；测试 **424/424**；模板生成物 237 文件、8 规则、0 凭据命中；
规范检查 50 张票据、12 份系统 ADR，全部一致。
