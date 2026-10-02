# 日历计划：Cron、显式时区、DST 与有界漏跑

核验日期：2026-10-02。本文是下一轮实现建议，不是完成验收。旧项目只读源码；没有读取配置值、访问生产服务或数据库、安装包、修改产品代码、运行集成测试。另用本机已经缓存的 Cronos 0.13.0 做了无 I/O 的时间计算探针；这只证明本机 Windows 的计算结果，不能代替 Linux CI 或宿主旅程。

源码快照：NS `81831672aefc1f84db3ee5596a445df88ad26bdb`，PoS `6cb62274491ee50600d4103dc01a8a0a1bea114d`；NSN 研究时 HEAD 为 `12f130b12837654c5a6470e892a0ed02895783ed`，issue #43 的审查修正仍可能继续。前序能力盘点见 [NS / PoS 能力路线](2026-10-02-ns-pos-capability-parity.md)，本轮直接前置是 [持久计划触发 #43](https://github.com/yongpengW/NexusStackNext/issues/43)。

## 1. 结论与本轮边界

建议沿用已有 Scheduling 自有数据库、Occurrence、Outbox 与 Costing 接受回执，引入 **Cronos 0.13.0 作为日历计算实现**。公开支持五字段和六字段 Cron、显式 IANA 时区、月度指定日不足则落月末、可预览的下次时刻、明确的 FireOnce / Skip 漏跑规则。领域聚合仍不引用第三方包。

这能在同一条完整竖切里保留 PoS 已证明需要的月初、每日、工作时间内周期、秒偏移与月末钳制。无需引入第二套调度持久状态，也无需自写 Cron parser 或 DST resolver。五字段只是初始建议，不能当成用户限制；只做五字段会丢失已有生产用法。

本轮不引入 Quartz 调度宿主、不自动迁移旧计划、不调用 PoS 业务或供应商、不实施多机 HA。业务样板仍是受权创建日历计划 → Scheduling 发生 → Costing 当前输入重算 → Pricing；它证明基础能力，不等于移植 MasterPricing 月度业务公式。

## 2. 旧项目事实：哪些能力不能丢

| 观察 | 源码依据 | 对 NSN 的约束 |
|---|---|---|
| NS 使用 Cronos 0.13.0；PoS 使用 0.11.1 | [NS csproj](D:/LeoProject/NexusStack/NexusStackBackend/Domain/NexusStack.Core/NexusStack.Core.csproj)、[PoS csproj](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/POS.Core.csproj) | 是复用成熟依赖，不是首次引进完全不同的计划语言 |
| 两者均 `IncludeSeconds` + `TimeZoneInfo.Local` | [NS CronScheduleService](D:/LeoProject/NexusStack/NexusStackBackend/Domain/NexusStack.Core/Schedules/CronScheduleService.cs)、[PoS CronScheduleService](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Schedule/CronScheduleService.cs) | 六字段有真实用户；机器本地时区应改成每计划的显式配置 |
| 每月 1 日 01:00 登记 MasterPricing 后续任务：`0 0 1 1 * ?` | [CostFirstDayOfMonthSchedule](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Schedule/CostFirstDayOfMonthSchedule.cs) | 月初计划必须先登记持久业务意图；不能将“调度循环跑了”当成业务完成 |
| 每分钟第 10 秒、当地 07–23 点：`10 * 7-23 * * ?` | [ZZDeclarationQuerySchedule](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Schedule/Zhengzhou/ZZDeclarationQuerySchedule.cs) | 不能直接去掉秒字段，也不能用每分钟 Interval 代替其墙上时刻语义 |
| 定时邮件扫描逐项创建持久发送执行，单项异常不阻断同批后续项 | [EmailNotificationDispatcherSchedule](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Schedule/EmailNotificationDispatcherSchedule.cs) | 继续保留有界、逐计划隔离失败的触发方式 |
| 管理修改的表达式由后台读取并应用 | [ScheduleTaskService](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/Schedules/ScheduleTaskService.cs)、[后台表达式刷新](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Schedule/CronScheduleService.cs) | 规则更新要有管理授权、预期版本与生效边界，不能只把表达式写进启动配置 |
| 邮件月度指定日通过 `min(day, DaysInMonth)` 钳到月底，另有每日计算 | [EmailNotificationScheduleCalculator](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/EmailNotifications/EmailNotificationScheduleCalculator.cs) | `31 日`的产品规则不等于标准 Cron 的 `31`；需分别命名 |

以上是源码行为，不代表旧生产实例当前启用了每条计划，也没有核验其数据库记录、时区或实际运行结果。

## 3. 依赖选型与官方事实

| 候选 | 已核验版本、许可和能力 | 判断 |
|---|---|---|
| Cronos | NuGet 当前稳定版 0.13.0，2026-04-29 发布，MIT；包含 net6.0 / netstandard2.0 目标，NuGet 将 net10.0 列为兼容；本身是表达式及发生时刻计算库，不提供任务调度器。[包元数据](https://www.nuget.org/packages/Cronos/0.13.0)、[该版本项目文件](https://github.com/HangfireIO/Cronos/blob/v0.13.0/src/Cronos/Cronos.csproj) | 推荐锁定该版本，在 Scheduling 内使用；实际 NSN 构建与跨平台行为仍须验收 |
| Quartz.NET | 当前稳定版 4.3.0，2026-09-29 发布，Apache-2.0，直接面向 net10.0；提供完整调度、宿主、存储等能力。[包元数据](https://www.nuget.org/packages/Quartz/4.3.0) | 当前需求主要缺日历计算；整体接入会增加计划状态、事务及宿主协调面，收益不足以抵消迁移成本，这是项目判断 |
| Noda Time | 当前稳定版 3.3.5，2026-09-30 发布，Apache-2.0；net8.0 / netstandard2.0。[包元数据](https://www.nuget.org/packages/NodaTime/3.3.5) | 当需要应用固定 TZDB 版本、复杂民用日历领域模型时有价值；本轮不为已经由 Cronos + BCL 完成的计算再加一套时间模型 |
| 自写 Cron / 时区算法 | 要自行负责格式、稀疏日期搜索、闰年、夏令时缺口、重叠和跨平台差异；现成库已有这些代码与测试。[Cronos parser](https://github.com/HangfireIO/Cronos/blob/v0.13.0/src/Cronos/CronParser.cs)、[时区实现](https://github.com/HangfireIO/Cronos/blob/v0.13.0/src/Cronos/TimeZoneHelper.cs) | 不推荐；NSN 应测试自己的承诺，不再承担一个 Cron 引擎的维护面 |

Quartz 4 已能显式加入应用自己的数据库事务，不能声称它“做不了原子提交”。不过其触发获取、漏跑处理等后台工作仍有自己的连接和状态；采用它仍需重新设计与 NSN Occurrence / Outbox 的衔接。[Quartz 4 Job Stores](https://www.quartz-scheduler.net/documentation/quartz-4.x/tutorial/job-stores.html#joining-an-existing-transaction)。这也是选轻量计算库的项目依据，而非笼统判断 Quartz 不可靠。

Noda Time 随包携带 TZDB，且支持单独更新数据文件；这能提供部署可控的规则来源，但要自行管理更新策略。[Noda TZDB 更新](https://nodatime.org/3.2.x/userguide/tzdb)。其 LenientResolver 对缺口采用“按缺口长度平移”，与 Cronos 的“下一个有效时刻”不同；不能随手替换而声称语义不变。[Noda Resolvers](https://nodatime.org/3.2.x/api/NodaTime.TimeZones.Resolvers.html)。

## 4. 建议的日历定义契约

以下是建议值，需由下一票规格固定，不能冒充当前实现。

- 保留现有 Interval 请求及“迟到只登记一次、从实际登记时刻起算”的行为。新增 `Cron` 与 `MonthlyDay` 两种日历规则；在领域和持久层明确区分，拒绝同时填写多个规则的对象。
- Cron 输入接受恰好五或六个字段；按字段数分别映射 `CronFormat.Standard` / `IncludeSeconds`，将规范化表达式与推导出的格式持久化并返回。若 HTTP 还暴露显式 format，则不一致必须拒绝，不能猜测。六字段没有 year；七字段、宏 `@...`、随机 `H` 本轮拒绝。长度建议上限 256 字符；规范化仅处理分隔空白与名称大小写，不把范围改成列表。
- 允许 Cronos 已有的 `L/W/#/?`、范围、步进和月份/星期缩写；文档明确 day-of-month 与 day-of-week 同时限制是 AND。`?` 在 Cronos 中等同通配。它不是 Quartz 方言，也不保证 Unix crontab 的 OR 语义。[Cronos 格式说明](https://github.com/HangfireIO/Cronos/tree/v0.13.0#cron-format)、[格式枚举](https://github.com/HangfireIO/Cronos/blob/v0.13.0/src/Cronos/CronFormat.cs)
- 每个日历计划显式保存 IANA ID，例如 `Asia/Shanghai`；UTC 可统一规范为 `Etc/UTC`。不能默认取服务机器的时区。查找失败、非 IANA 标识、不可能的未来表达式都返回稳定业务错误，不能落成 `DateTime.MinValue` 或启用后静默空转。
- Preview 使用 UTC 起点、最多 10 个结果，返回 UTC instant、时区 ID 和含 offset 的当地显示时间；不注册任务、不改版本、不发送消息。创建、修改、预览和运行期必须使用同一计算实现。
- 创建与恢复从当前时刻之后的第一个有效日历时刻开始；恢复不追补主动暂停期间。更新采用 `expectedVersion`，只重排未来时刻，已经提交的 Occurrence / Outbox 不变；相同规范化定义不改版本，也不重排已有 NextRunAt。
- 规则更新时建议递增 `ScheduleRevision`，触发历史保存当时规则/时区/策略的快照或可追溯修订引用。聚合 Version 仍只在可观察状态改变时递增；两种版本用途不同。

Cronos 在秒、分、时字段的 `*` / 范围 / 步进会设置 interval 标记，影响 DST 重叠行为；枚举列表与范围看似匹配同样的普通日期，DST 时却可能不同。因此不能“美化表达式”时把一种语法改写成另一种。[该版本 parser 的 interval 标记](https://github.com/HangfireIO/Cronos/blob/v0.13.0/src/Cronos/CronParser.cs#L251-L355)。

## 5. 时区与 DST：采用一个明确策略

### 跨 Windows / Linux

.NET 6 起 Windows 支持 IANA 查找，前提是没有启用 NLS 或 globalization invariant；.NET 8 起 `FindSystemTimeZoneById` 返回缓存对象。[Microsoft 查找 API](https://learn.microsoft.com/en-us/dotnet/api/system.timezoneinfo.findsystemtimezonebyid?view=net-10.0)。`HasIanaId` 可用于确认解析对象的 ID 类型；保留显式 UTC 别名处理，不能仅凭字符串包含 `/` 判定有效时区。[Microsoft HasIanaId](https://learn.microsoft.com/en-us/dotnet/api/system.timezoneinfo.hasianaid?view=net-10.0)。

建议宿主启动核验所需的全球化/时区数据能力，发布镜像保留其依赖；不能让无法解析 IANA 的进程回退到 UTC。Windows 开发机与 Linux CI 都运行同一组日历例子，跨平台结果属于验收，不由一次 Windows 成功推出。ICU 的选择及 invariant 开关见 [Microsoft globalization/ICU](https://learn.microsoft.com/en-us/dotnet/core/extensions/globalization-icu)。

OS / ICU / tzdata 更新可能改变未来规则。建议已经持久化的下一次 UTC instant 保持稳定；新计算使用当时部署的规则版本，升级前预览差异，历史发生保持不变。若业务要求历史时区规则完全可重演，再选择固定 TZDB 的方案；本轮不要承诺 OS 时区数据库永远不变。

### 明确沿用 Cronos 的 DST 行为

采用 Cronos 既有规则：缺口内的时刻落到后续首个有效时刻；日固定时刻的秋季重叠只取较早一次；周期型表达式保留重叠两侧不同的 UTC instant。调用只传 UTC `DateTime` 或 `DateTimeOffset`，下一次计算使用 exclusive 边界，避免同一瞬间循环。[Cronos DST 说明](https://github.com/HangfireIO/Cronos/tree/v0.13.0#daylight-saving-time)、[该版本 UTC/offset 计算路径](https://github.com/HangfireIO/Cronos/blob/v0.13.0/src/Cronos/CronExpression.cs#L281-L373)。

以下是本机缓存 DLL 的纯计算实测输出；同一系列还应写入 Windows / Linux 的产品契约测试。上游对纽约和 Lord Howe 已有对应转换测试，研究没有运行上游测试套件。[上游 DST 测试](https://github.com/HangfireIO/Cronos/blob/v0.13.0/tests/Cronos.Tests/CronExpressionFacts.cs#L1066-L1410)。

| 规则与起点 | 实测下一次及含义 |
|---|---|
| `30 2 * * *`，America/New_York，从 `2026-03-08T06:59Z` | 当地 `2026-03-08 03:00 -04:00` = `07:00Z`；不是 03:30。下一天回到 02:30 |
| `30 1 * * *`，America/New_York，从 `2026-11-01T04:00Z` | 首次 `01:30 -04:00` = `05:30Z`；下一次为次日 `01:30 -05:00`，当日第二个 01:30 不触发 |
| `*/30 * * * *`，同区，从 `2026-11-01T04:30Z` exclusive | `01:00 -04:00`、`01:30 -04:00`、`01:00 -05:00`、`01:30 -05:00`、`02:00 -05:00`；UTC 依次 05:00 / 05:30 / 06:00 / 06:30 / 07:00 |
| `15 2 * * *`，Australia/Lord_Howe，从 `2026-10-03T15:00Z` | 当地 `2026-10-04 02:30 +11:00` = `2026-10-03T15:30Z`，证明不能把缺口写死为一小时 |
| `0 0 1 1 * ?`，Asia/Shanghai，从 `2026-09-30T00:00Z` | 当地 `2026-10-01 01:00 +08:00` = `2026-09-30T17:00Z` |
| `10 * 7-23 * * ?`，Asia/Shanghai，从 `2026-10-02T00:00Z` | UTC 00:00:10、00:01:10；保留 PoS 的秒偏移 |

DST 时区转换与“进程来晚了”的 misfire 是两个步骤：先由日历规则确定真实 ScheduledAt，再比较 UTC 迟到量。一个不存在的当地时间不是两次补跑，也不能以 `localDateTime` 作为唯一身份。

## 6. 月度日钳制：保留产品语义，不篡改 Cron31

本机探针确认：`0 9 31 * *` 从 2027 年 2 月初开始，下次是 3 月 31 日；`0 9 L * *` 则是 2 月 28 日。前者正确执行 Cron，后者正确表达每月末日，两者不能混称。

建议新增明确的 `MonthlyDay` 规则：`day=1..31`、`hour=0..23`、`minute=0..59`、显式时区，秒为 0；该规则承诺所选日超出月份时采用月末。可在日历计算适配器内复用最多两条 Cron，避免自建 DST 解析器：

| 请求日 | 内部计算（示例时间 09:00） |
|---|---|
| 1–28 | `0 9 <day> * *` |
| 29 或 30 | `0 9 <day> 1,3-12 *` 与 `0 9 L 2 *` 分别求下一次，取较早 UTC instant |
| 31 | `0 9 L * *` |

这是本项目提出的有限转换，不是 Cronos 自带的新计划类型。已做 16 个纯计算探针：日 28/29/30/31 × 非闰年二月、闰年二月、四月、五月，结果均为所选日与月底较小者；正式实现仍需正反向测试，尤其 DST、刚好边界和规则更新。内部多个候选只产生一个下一时刻、一条计划和一次发生，不能拆成两个独立平台计划。

月度成本结转或报表以后若依赖“哪个账期”，应由业务上下文从明确的 ScheduledAt、日历定义与自己的规则决定，必要时扩展专用契约；不能在重放时用 `DateTime.Now.Month` 重新解释旧意图。当前 Costing 目标是重算现有对象，不包含账期，这个限制要保留在能力说明中。

## 7. FireOnce / Skip：给出可测的有限规则

Quartz 官方也区分继续时触发一次、略过和全部追赶，并单独定义迟到阈值；不能把普通轮询延迟都当成 misfire。[Quartz 4 misfire](https://www.quartz-scheduler.net/documentation/quartz-4.x/tutorial/crontriggers.html#crontrigger-misfire-instructions)、[阈值与有界批处理](https://www.quartz-scheduler.net/documentation/quartz-4.x/configuration/reference.html#persistent-job-store)。以下是建议的 NSN 契约，不是调用 Quartz API：

设 `due` 为已经保存的 NextRunAt，`now` 为本轮唯一 UTC 时钟读数，`grace` 为明确持久化的容忍迟到时长。建议日历默认 30 秒，允许 1 秒至 1 小时；这组值属于产品取舍，应写入规格及 OpenAPI。

| 条件 | 决策与持久化 |
|---|---|
| `due > now` 或暂停 | 无操作；版本不变 |
| `0 <= now-due <= grace` | 正常登记一次发生，ScheduledAt=due，OccurredAt=now；下一时刻为规则中严格大于 now 的第一个 instant |
| `now-due > grace`，FireOnce | 将整个过期窗口合并为一次发生；仍使用最早未处理的 due 作为 ScheduledAt，记录 coalesced 决策；下一时刻严格大于 now |
| `now-due > grace`，Skip | 不创建业务发生/Outbox；保存“从 due 到 now 已略过”的调度决策及下一时刻，版本递增，TriggerSequence 不变 |

恢复落在下一计划时刻的精确边界也只允许一条决定；例如每分钟计划 due=10:00，恢复 now=10:03，FireOnce 产生一条 ScheduledAt=10:00 的发生，下次 10:04；Skip 产生一条跳过记录，下次同为 10:04。不会另外再补 10:03 一条。该选择避免启动瞬间爆发，但必须通过预览/文档解释清楚。

不枚举全部错过次数，也不为每个错过时刻建一行。每计划每轮最多一个决定、最多一条消息。跳过记录应是 Scheduling 自己的可查询事实，与计划推进同事务；不要制造一个“Delivered 的空消息”冒充跳过。发生历史和调度决定可用不同视图，但管理端必须能解释为何没有执行。

固定间隔继续使用已有行为，不无声套用新的 Skip。手工恢复暂停的计划从当前之后重新安排，不把主动暂停记为故障漏跑。规则变更或恢复与扫描竞争，以聚合预期版本裁决；落败者不得留下发生、跳过记录或 Outbox。

## 8. 秒级能力、模块接口与持久性

当前 [SchedulingWorker](../../src/Services/Scheduling/NexusStackNext.Scheduling.Endpoints/SchedulingWorker.cs) 的 TickInterval 为 **10 秒**；[ScheduleRunner](../../src/Services/Scheduling/NexusStackNext.Scheduling.Application/ScheduleRunner.cs) 单轮最多 50 项、每项一次，时间归一到 PostgreSQL 微秒精度。支持六字段时建议把常规扫描节拍设为 1 秒，禁止更短；最小表达精度仍是 1 秒，禁止亚秒计划。

1 秒扫描不保证每个发生恰好在该秒发出，更不保证业务在那一秒完成。单循环串行、事务耗时和队列会造成迟到；预算耗尽时按明示规则合并/跳过并可观测，不能无限排队补跑。保持每轮 50 项上限，有限耗时和取消；补测试证明一条失效规则或时区故障不会让其他计划永久饥饿，必要时持久退避到 RetryAt，而非每轮拿同样一批毒计划。

接口的缝上会变化的是日历表达式引擎、时区数据及其解析；计划状态转换、版本和持久发生不是这条缝。建议 Application 定义小的日历计算接口，Infrastructure 用 Cronos + BCL 实现，宿主显式装配。接口返回经过验证的日历定义或下一 UTC 时刻，不泄露 Cronos 类型；Domain 仅存规则值并检查推进严格向前等不变量。测试在该公开计算接口使用真实适配器，不靠 mock 证明 DST。

不要把日历抽象提到 BuildingBlocks：只有 Scheduling 消费。Domain 不引入 Cronos、Quartz、Noda Time 或系统本地时区查询。参考现有 [领域聚合](../../src/Services/Scheduling/NexusStackNext.Scheduling.Domain/Tasks/ScheduledTask.cs)、[持久发生决定](../../src/Services/Scheduling/docs/adr/0002-durable-occurrences-and-business-delegation.md)。

延续 #43：OccurrenceId 在重试事务外生成并固定；`(PlanId, TriggerSequence)` 唯一，已提交的发生重投/人工重驱不换 ID。计划推进、发生/跳过决定、规则修订引用及 Outbox 使用本上下文原子提交。UTC 时刻或重复的当地时间都不能替代发生身份。调度交付、Costing 接受、业务执行仍是三个状态；漏跑策略不改变消费者 Inbox 去重或稳定业务拒绝语义。

## 9. 建议验收清单

1. 经真实 HTTP / 网关授权创建五字段、六字段、MonthlyDay；未授权管理、预览和查询拒绝，注销后旧令牌立即失效。旧 Interval 请求和响应契约继续通过。
2. 非法字段数、超长表达式、宏/H、非法数值、未知/不支持时区、不可能表达式、混合规则、无效 grace 返回稳定错误；没有半条计划或后台日志异常循环。五字段与等价前置 0 秒的六字段预览一致，10 秒偏移得到保留。
3. 上表纽约春秋、Lord Howe 半小时、上海月初在 Windows / Linux 得到相同 UTC 结果；覆盖秒型表达式的秋季重叠及 exclusive 边界。Preview 与实际推进一致。
4. MonthlyDay 覆盖 28/29/30/31、二月闰年/平年、30/31 天月份；Cron31 在短月继续跳过；`L` 是每月末；同时限制日和星期按 AND。不同语法不能被不安全地规范化成同一规则。
5. 可控时钟验证 grace 前、等于、超过边界，FireOnce 一次、Skip 零业务消息但有决定，长停机不按分钟/秒枚举历史，next > now；主动暂停/恢复、同定义更新不改版本、有效更新与扫描竞争。
6. 独立临时 PostgreSQL 迁移可重复，旧 Interval 数据迁移后语义不变；重启保留规则、时区、策略、修订、历史与 next；缺迁移启动失败，readiness 检查所需对象。
7. 两个扫描者竞争、计划推进保存失败、发生/跳过记录插入失败、进程在提交前/后被杀均没有半成品；过期窗口只被裁决一次。重驱保留原 ID，历史按当时规则解释。
8. 一条日历计划经真实 broker 到 Costing/Pricing；broker/业务宿主停止后恢复，原发生最终接受且只产生一个逻辑任务。使用可控时钟或测试进程边界，不在测试中等到真实月初或 DST。
9. 1 秒扫描的边界、批量上限、异常隔离与退避有行为证据；日志不包含原始配置、连接串或任意业务载荷。跳过/合并计数与最老 due 年龄可查询。
10. 文档、CONTEXT / ADR、模板迁移说明与包锁定一致；本地 build → 串行 tests → format、票据/凭据检查、模板生成构建、精确提交 Linux CI、Standards / Spec 双轴审查均通过后才合并。

## 10. 尚未验证与后续边界

已核验的是官方资料、源码结构、包元数据和本机纯计算样例。没有验证新 API、迁移、事务、并发、Linux、容器全球化配置、所有 IANA 历史规则或部署 SLA；本文不将它们标为通过。

多机故障域、共享调度时钟偏差、跨版本 tzdata 滚动部署、容量、保留策略和灾备演练留到用户指定的 HA 轮次。秒级 Cron 支持不能自动成为高吞吐实时调度承诺。需要按账期逐期补全、节假日交易日历、每月第 N 个工作日的公司规则、无界补跑、任意长任务进度/取消，以及外部邮件发送的结果未知模型，也不由本轮日历计算自然获得；必须单独有业务票据和验收。
