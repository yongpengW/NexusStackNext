# 分阶段测试策略

2026-10-07 用户确认：日常开发按影响范围验证，PR合并前保留完整CI与统一门禁。
目标是减少重复全量等待，同时保留事务、消息、迁移与故障恢复的真实验证。

## 选择范围

| 变更或阶段 | 本机验证 | 合并验收 |
|---|---|---|
| 模块内部业务规则 | 所属Domain/Application测试、相关集成测试与宿主旅程 | 完整Linux CI |
| 公共事务、消息、鉴权、缓存、HTTP契约 | 全部受影响上下文及跨上下文旅程；影响不明则扩大到全量 | 完整Linux CI |
| 迁移、进程启动/崩溃/重启、Windows路径或命令行为 | 本机执行相关真实数据库/进程专项；广泛影响则本机全量 | 完整Linux CI |
| 测试调度、所有权、报告及CI脚本 | 对应公开CLI无服务护栏、架构测试；改变真实负载行为时补充真实旅程 | 完整Linux CI |
| 纯文档 | 对应跟踪器、规范、凭据及格式检查 | 当前仍完整Linux CI |

先在PR描述或票据记录：修改面、受影响测试工程/宿主类、选择理由、执行结果。
按接口、依赖关系和调用链选择，测试名相似不是唯一依据。新故障需要重现它的回归测试。
同一冻结实现的证据可复用；修改了执行源、配置、依赖或出现新失败后，重验受影响范围。

## 执行

本机代码变更仍按 **build → tests → format** 执行；任一失败即停。
先构建相同Configuration，再以`-NoBuild`避免重复构建。工程名必须精确匹配csproj文件名。
多个工程逐个调用同一入口，失败即停；宿主通过Filter指定相关类或方法。

```powershell
dotnet build NexusStackNext.slnx -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
pwsh -File scripts/run-tests.ps1 -Configuration Release -NoBuild -Scope Focused -Project Identity.Application.Tests
if ($LASTEXITCODE -ne 0) { throw 'Selected tests failed' }
pwsh -File scripts/run-tests.ps1 -Configuration Release -NoBuild -Scope Focused -Project HostIntegration.Tests -Filter 'FullyQualifiedName~IdentityTransactionTests|FullyQualifiedName~MemoryIdentityTransactionTests'
if ($LASTEXITCODE -ne 0) { throw 'Selected host journeys failed' }
pwsh -File scripts/check-format.ps1
if ($LASTEXITCODE -ne 0) { throw 'Format failed' }
```

`-Scope Focused`要求一个精确`-Project`，可附加`-Filter`。
旧`-Project`/`-Filter`调用仍可用，输出同样标为Focused；仅Filter会遍历工程，优先指定Project。
零用例、缺失/损坏/计数不一致的TRX、失败或跳过不能得到定向通过。
不输出筛选表达式、参数值、异常原文或原始报告；私有诊断留在本机。
定向成功明确显示“定向验证（不代表全量验收）”，不能用于声称1935项完整通过。

无选择器仍跑全量；显式`-Scope Full`与选择器冲突会在私有配置/负载前拒绝：

```powershell
pwsh -File scripts/run-tests.ps1 -Configuration Release -NoBuild -Scope Full
```

共享依赖仍需有效`env/test.dev`；单所有者、工程逐个执行、重操作单许可、特殊旅程独占保持。
受控普通4/2路、取消和恢复先读[共用测试库受控并发](local-test-concurrency.md)与
[负载所有权](local-test-ownership.md)，不绕过脚本直接并行`dotnet test`。

## 完整门禁

PR的最终候选必须经过当前完整Linux CI：真实发现全部用例、四隔离组、每项恰好一次通过、
仓库检查及统一“构建与测试”门禁。Focused不能进入CiShard，也不能用定向报告代替完整报告。
合并代码须与受测代码一致；代码未变化且本机相关检查已通过时，不为等CI再跑一轮本机全量。
CI触发、完整清单、分组与依赖隔离本轮保持；纯文档的CI轻量路径尚未实现。

本机定向与Linux完整CI覆盖不同层次。CI不能代替Windows专项；定向结果也不能证明未执行的场景。
这条用户确认的策略覆盖Matt implement里“结束时本机全量”的默认建议，完整验收由CI承担。
