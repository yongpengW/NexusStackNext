# CI 隔离并行

共享开发 PostgreSQL 同时承担配置中心负载，本机测试继续由 `scripts/run-tests.ps1` 串行执行。
CI 将并行放在独立 GitHub 托管 runner 之间；每组独占 PostgreSQL、Redis、RabbitMQ，
每个端口只绑定回环地址。测试类内部的原有并发设置和故障断言保持不变。

## 分组与安全边界

- 最多四个测试任务：0 组执行除 `HostIntegration.Tests` 外的全部工程；1–3 组分担宿主测试。
- 同一个测试类留在同一组，按历史类耗时分配；新增类按发现的用例数估计权重。
  `scripts/ci-test-weights.json` 来自初始化优化后的 Linux [CI 37136909253](https://github.com/yongpengW/NexusStackNext/actions/runs/37136909253)
  的 458 条宿主用例报告（80 个类；该运行全部产品测试和覆盖校验通过，随后在票据探针启动器处失败），
  只影响负载均衡，不决定是否执行。权重变旧会影响速度，不会漏测试。
- `-CiShard` 入口先确认 GitHub 托管 runner、没有 `env/test.dev`、固定回环测试配置，
  再检查工作流传入的三个容器 ID、健康状态及端口绑定。失败时不读取私有配置，不启动测试。
- 单组超时 25 分钟，组内逐工程串行。CI 的 `-NoBuild` 使用同一任务中刚完成的 Release 构建；
  本机仍默认先构建。未知脚本参数直接失败。

这层检查防止误配置连接到共享服务；GitHub 托管 runner 的隔离是部署事实，不能靠伪造环境变量
在本机获得相同保证。不要把矩阵改到共享机器上，也不要解除本机串行约束来追求相同速度。

## 完整性与必需检查

各组通过真实 `dotnet test --list-tests` 发现所有工程的测试，按完整方法名筛选执行。
理论测试各行的显示身份被哈希，实际 TRX 逐项匹配；方法名可见，参数、异常和原始 TRX 不上传。
暂不支持发现时不能确定用例身份的动态数据：清单不一致会失败，需要明确设计后再支持。

最终任务仍叫 **构建与测试**，保留现有保护规则所需的检查名。它会核实：

1. 四组任务全部成功，报告属于当前提交、运行和尝试。
2. 四份完整发现清单及确定分组一致；每个发现的用例恰好执行一次并通过。
3. 空清单、缺组、重复、错组、漏跑、失败、跳过或陈旧报告均失败。
4. 完成后才执行格式、凭据、仓库跟踪器、GitHub 票据及模板构建检查。

脱敏报告保存为 Actions artifact，保留七天；最终日志列出最慢的十五个测试类和十五个用例，
以及各组用例数和累计测试耗时。累计耗时不等于 job 墙钟时间；后者还包括容器启动、构建和发现。
用例以方法名和身份哈希区分，不输出理论测试参数。
PR、dev/main push 和手动触发均保留，当前没有路径过滤，也没有减少测试覆盖范围。

## 初始化复用与构建边界

普通宿主旅程通过 `IdentityJourneyDatabase.MigrateAsync` 在测试进程内执行真实 EF 迁移，
复用运行时和 EF 模型缓存，省去每条旅程六次进程启动。每条旅程依然独立建库、迁移、释放连接池和删库，
不共享数据、DbContext 或运行中的宿主，也不以 `EnsureCreated` 替代迁移。
真实提交与重启测试需要这种数据隔离；参考 [EF 数据库测试指南](https://learn.microsoft.com/en-us/ef/core/testing/testing-with-the-database)。

验证独立迁移、重复迁移与重启的旅程显式使用 `MigrateThroughCliAsync`，仍从空库启动六个真实迁移命令。
各上下文的未迁移启动拒绝与 CLI 错误诊断仍由原测试验证，故障恢复的等待预算和业务断言不变。

每台隔离 runner 只构建一次；发现与执行都使用 `--no-build --no-restore`（`--no-build` 本身也隐含不还原）。
暂不跨 runner 传输整套构建产物：四个构建本来并行，改成前置构建会增加依赖链和产物传输，
是否更快需另外测量。生成模板后的构建验证重命名后的项目，不能用仓库构建结果替代。

仓库跟踪器和 GitHub 票据检查分成独立步骤，模板安装、生成、构建逐条检查退出码，
避免同一步内后续成功命令覆盖前面的失败。统一必需检查名称和 build → tests → format 顺序保留。
票据依赖读取最多四个并发请求，完整分页；请求失败或响应无效时门禁失败，不能静默跳过该票。
这部分并行只访问 GitHub API，不改变数据库测试的并发度。

## 票据 API 的有界恢复

GitHub 只读门禁统一通过 `scripts/github-read.psm1` 发起 GET；issue 列表、父子关系、
依赖和标签保持完整分页。每次 gh 调用默认最多 20 秒，所有读取共用一个从门禁启动计时的
120 秒预算，依赖仍最多四并发。可通过 `check-issues.ps1` 的 `-RequestTimeoutSeconds`
和 `-TotalTimeoutSeconds` 指定更小的验证预算；参数范围分别为 1–60 秒和 1–300 秒。

只有 gh stderr 上明确的 HTTP 502/503/504 诊断会重试，每个读取最多三次，间隔 250/500 毫秒；
等待也占用总预算。永久错误、不可识别错误、超时、无效 JSON 或缺少分页结构均失败。
超时会终止调用进程树，最多另用五秒确认退出；不会输出原始 API 错误或响应内容。
预算是 API 读取预算，进程启动、终止确认和本地验证另有少量开销，不代表整个 CI 的耗时上限。
HTTP 429 不自动重试，避免忽略限流策略。

当前 gh 2.102.0 的 [API 请求源码](https://github.com/cli/cli/blob/v2.102.0/pkg/cmd/api/http.go)
和其所用 [go-gh 2.16.1 HTTP 客户端](https://github.com/cli/go-gh/blob/v2.16.1/pkg/api/http_client.go)
没有内置 503 重试；这项策略没有叠加另一套应用层重试。
后续 gh 版本变化时仍由外层进程时限限制整次分页调用，避免新增内部等待突破预算。

`check-issue-fetching.ps1` 通过真实脚本 CLI 和隔离 gh 适配器验证四条读取路径恢复、
持续故障、永久错误、stdout 假诊断、异常分页、脱敏、单次超时与累计预算。
只运行部分探针可在 PowerShell 中使用 `./scripts/check-issue-fetching.ps1 -Modes @('request-timeout')`；
CI 默认运行全部探针，未知探针名称失败。本轮实现和 Linux 资格验证由
[有界恢复票据](https://github.com/yongpengW/NexusStackNext/issues/82)记录，不能仅凭本节声明通过。

## 验证

`pwsh -File scripts/check-ci-tests.ps1` 验证脚本 CLI 的分组、坏报告拒绝、环境保护和脱敏，
不连接数据库。真实运行仍须在 Linux CI 验证容器隔离、过滤器匹配、全部用例与端到端耗时。
`pwsh -File scripts/check-issue-fetching.ps1` 通过临时 `gh` 适配器执行真实票据检查 CLI，
验证成功、请求失败、无效/缺失响应、未知依赖和分页要求；不访问 GitHub。
产品代码、迁移文件及既有业务断言未变；第二轮调整测试准备方式和耗时诊断。

优化前基线：[CI 37129218785](https://github.com/yongpengW/NexusStackNext/actions/runs/37129218785)：
整体 33 分 5 秒、测试 28 分 9 秒，其中宿主工程 23 分 30 秒；1265 项全部通过。
优化后的实测与双轴评审记录在[CI 优化票据](https://github.com/yongpengW/NexusStackNext/issues/71)及关联 PR。
第一轮 [PR #72](https://github.com/yongpengW/NexusStackNext/pull/72) 完整 CI 14 分 26 秒，1265 项通过；
第二轮 [PR #74](https://github.com/yongpengW/NexusStackNext/pull/74) 已合并；
[CI 37137608993](https://github.com/yongpengW/NexusStackNext/actions/runs/37137608993) 完整耗时 **10 分 11 秒**，
1265 项全部通过，无跳过、重复或漏跑。相比原基线减少约 69%，相比第一轮减少约 29%。
后续按慢测试报告维护权重；跨 runner 构建产物复用仍需测量收益后决定。
