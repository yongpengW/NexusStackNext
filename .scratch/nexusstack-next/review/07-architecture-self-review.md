# 07 — 架构自查：这套设计还有什么问题

**日期**：2026-09-29
**范围**：`NexusStackNext` 全部 `src/`（37 个项目、79 个源码文件）
**方法**：对抗性自查——**这套架构是我自己设计的**，所以这次不看它做对了什么，
专门找它做错了什么。每条都带实测证据。

---

## 结论先说

**骨架是稳的**：引用矩阵零跨上下文引用，五个 `*.Domain` 全部零包、只引用 `BuildingBlocks.Domain`。
但查出**八条问题**，其中一条是**我在评审里批评过参照仓库、自己却照犯的**。

---

## 一、【最严重】并发令牌：我批评过的缺陷，我自己也有

**证据**

`review/02-data-and-persistence.md` 第 348 行，我写的是：

> ### 9. 全局 `NoTracking` + `Update(entity)` 全列覆盖 + **无并发令牌** + 无唯一索引 = **静默丢更新**与重复业务键

而本仓库实测：

```
AggregateRoot 含 Version/RowVersion/ConcurrencyToken: False
全仓 RowVersion/ConcurrencyToken 出现: 0 处
```

`AggregateRoot<TId>` 的公开面只有 `DomainEvents` / `ClearDomainEvents` / `Raise`——**没有版本概念**。

**后果**

与参照仓库**同一条**：两个请求同时改一个聚合 → 后写覆盖先写 → **静默丢更新**，
没有任何报错，也没有任何痕迹。内存适配器现在不会暴露它，所以它不会在跑测试时出现。

更要紧的是：**接 EF Core 时没有东西可以映射成并发令牌。** 那不是"加个配置"，
是要改 `AggregateRoot` ——也就是每一个聚合的基类。

**而且它不在票据 42 的"没做什么"清单里。** 那份清单漏了它。
一个自认为诚实的清单漏掉了自己评审里的头号发现，这条本身就是发现。

**处置建议**：把 `Version`（乐观并发序号）加进 `AggregateRoot`，
每个改变状态的方法自增它。领域层不需要知道 EF 怎么用，只需**存在**这个事实。

---

## 二、【架构】消息端口住在 Infrastructure，把 Application 拽了下去

**证据**——整份引用矩阵里，**唯一一处** `Application → Infrastructure`：

```
Auditing.Application -> Auditing.Domain, BuildingBlocks.Application, BuildingBlocks.Infrastructure
```

原因：三个端口住在实现程序集里。

```
BuildingBlocks\BuildingBlocks.Infrastructure\Events\IEventBus.cs
BuildingBlocks\BuildingBlocks.Infrastructure\Events\IInboxStore.cs
BuildingBlocks\BuildingBlocks.Infrastructure\Events\IOutboxStore.cs
```

**后果**

其余四个 Application 都是干净的（只引用自己的 Domain + `BuildingBlocks.Application`）——
说明这不是"约定如此"，而是**只有一处破例**。

依赖方向反了：DDD / 六边形说"端口在里、实现在外"。
端口住在 Infrastructure 里，等于让"定义契约"和"提供实现"共用一个程序集，
于是**任何要用契约的人都必须依赖实现**。

**处置建议**：把三个端口（以及它们引用的 `OutboxEntry`）上移到 `BuildingBlocks.Application`。
应用层只认得"我要一个收件箱"，不认得"收件箱是怎么实现的"。

---

## 三、【一致性】不变量 7 说"第二个消费者就上移"，而我有四五个消费者没上移

**证据**

| 重复的东西 | 副本数 | 位置 |
|---|---|---|
| `static IResult Failure(Error)` | **4** | Files / Identity / Platform / Scheduling 的 `Program.cs` |
| `SequentialIdGenerator`（测试替身） | **4** | 四个 `*.Application.Tests` |
| `FixedClock`（测试替身） | **5** | 五个测试工程 |

**诚实的判断（分两种情况）**

- `Failure(Error)` 那 6 行是**每个服务自己的 HTTP 契约**。不同服务将来对"错误码 → 状态码"
  的映射很可能不同（Identity 的 `not_found` 与 Files 的 `not_found` 未必同义）。
  **不抽取是合理的。**
- 但**测试替身**没有这个理由。三个替身代码完全一样，散在 4–5 个工程里，改一处要改五处。

**处置建议**：把测试替身收进一个共享的测试辅助工程（`tests/TestSupport`）。
`Failure` 保持各服务自己写——但**在 `AGENTS.md` 里写明为什么它是个例外**，
否则下一个人会以为不变量 7 被违反了。

---

## 四、【缺失】没有 OpenAPI

**证据**：`src/` 下所有 csproj 里 `OpenApi|Swagger` 引用数：**0**。

参照仓库有 `NexusStack.Swagger` 项目。我把它去掉了，理由是"技术选型先不做"——
但一个**对外提供 HTTP 接口的模板**没有机器可读的契约，消费者只能读源码。

**处置建议**：加 `Microsoft.AspNetCore.OpenApi`（.NET 10 内置，无第三方依赖）+ 每个宿主暴露 `/openapi/v1.json`。

---

## 五、【缺失】健康检查不检查依赖

**证据**——六个宿主全部只报自己的状态：

```
Gateway / Auditing.Api / Files.Api / Identity.Api / Platform.Api / Scheduling.Api
  只有自身 /health: True（无一含 AddCheck<> / AddUrlGroup）
```

**后果**：**网关在所有 cluster 都挂掉时仍然报健康。** 编排系统会继续把流量送进来。

标准做法是把两者分开：

- `/health/live` —— 进程活着（不查依赖）
- `/health/ready` —— 可以接流量（**查依赖**）

**处置建议**：加 readiness 检查；网关的 readiness 检查它的 cluster 是否可达。

---

## 六、【不一致】测试工程的组织只迁移了一半

**证据**

| 上下文 | 领域测试在哪 | 应用测试在哪 |
|---|---|---|
| Identity | `Identity.Domain.Tests` | `Identity.Application.Tests` |
| Platform / Auditing / Files / Scheduling | **`Contexts.Tests`**（混在一起） | `<上下文>.Application.Tests` |

`Contexts.Tests.csproj` 里自己写着迁移约定：

> 当某个上下文开始有真实业务时，它应当拆出独立的测试工程。

五个上下文**都已经有真实业务了**，但只有 Identity 的领域测试拆了出来。

**处置建议**：把 `Contexts.Tests` 按上下文拆成四份，然后删掉它。
或者反过来（更省事）：把 `Identity.Domain.Tests` 并回 `Contexts.Tests`，并**改掉那句约定**——
但那是把不一致固化下来，我不推荐。

---

## 七、【小事但真实】`Scheduling.Api` 重复引用同一个项目

**证据**：csproj 第 11 行与第 14 行都是 `BuildingBlocks.Infrastructure`。

构建**不报错**。它来自我某次用锚点插入引用时，锚点里已经有一条了。

**处置建议**：删掉重复那行。已在本轮修掉。

---

## 八、【未被执行的不变量】不变量 2 至今是空转

**证据**：`src/` 下名为 `*Contracts*` 的 csproj：**0 个**。

`AGENTS.md` 不变量 2 写着"上下文之间只通过 `*.Contracts` 通信"，
而**上下文之间目前毫无通信**——所以那条测试通过，是因为**没有任何东西可测**。

这不是缺陷，是**一条尚未被真正执行过的规则**。它第一次被真正检验，
会是第一个需要跨上下文通信的时刻。**那之前它只是一句话。**

**处置建议**：在 `AGENTS.md` 里注明它当前是"未经检验的规则"，
与不变量 4、6 的处理方式一致（那两条也标了"没有结构测试"）。

---

## 没问题的部分（明确说，不是客套）

| 项 | 实测 |
|---|---|
| 跨上下文引用 | **0** |
| 五个 `*.Domain` 的包引用 | **0**（只引用 `BuildingBlocks.Domain`） |
| 五个 `*.Domain` 的出向引用 | 只有 `BuildingBlocks.Domain` |
| `Application → Infrastructure` | 除第二节那一处外，**0** |
| 构建基线 | 中央包管理 + 警告即错误 + XML 文档 + 分析器，**0 警告 0 错误** |
| 测试 | **392 条全绿** |

---

## 处置优先级

| 优先级 | 问题 | 理由 |
|---|---|---|
| **高** | 一、并发令牌 | 静默丢数据；且拖得越久越贵（要改聚合基类） |
| **高** | 二、端口位置 | 依赖方向反了，且已有一处实际破例 |
| 中 | 五、readiness | 编排系统会依赖它做决策 |
| 中 | 四、OpenAPI | 模板的门面 |
| 中 | 三、测试替身 | 复制粘贴的成本在累积 |
| 低 | 六、测试工程组织 | 影响可读性与定位，不影响正确性 |
| 低 | 七、重复引用 | 已修 |
| — | 八、不变量 2 | 不是缺陷，是待检验 |

---

## 这份评审最该记住的一条

**第一节那条，是我在自己的评审里写下的、然后在自己代码里原样犯下的。**

评审 02 是我写的，第 348 行是我写的，`AggregateRoot` 也是我写的。
三件事之间隔了 27 轮，而**没有任何机制把它们联系起来**——
没有"评审发现 → 是否处置"的对照表，所以那条发现就那么飘着。

这正好是这个项目反复出现的那条：**不受检查的声明会腐烂**，
而"我做过的评审"和"我写的代码"之间，此前恰恰没有检查。
