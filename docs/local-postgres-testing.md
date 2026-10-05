# 开发测试环境与本地 PostgreSQL 工具退役

2026-10-05 用户明确选择：开发测试继续使用 `env/test.dev` 中共用的 PostgreSQL，保留网络和共享环境下的真实故障表现。此前 #84 的免安装实例不再作为开发验证目标；其历史交付记录保留在 GitHub。

## 当前测试入口

在仓库根目录完成 build → tests → format：

```powershell
dotnet build NexusStackNext.slnx -c Release
pwsh -File scripts/run-tests.ps1 -Configuration Release -NoBuild
pwsh -File scripts/check-format.ps1
```

runner 从现有私有配置或已注入测试环境变量读取 PostgreSQL，缺少配置时失败。旧 `-LocalPostgresConnectionFile` 参数即使指向合法文件，也在读取配置、构建和启动负载前拒绝；不自动下载、初始化或启动本机数据库。真实配置值不改写、不输出、不进入 Git 或模板。

共用服务器同时承担配置中心负载：完整测试仍逐工程串行，并保留跨工作区负载所有权保护。运行前检查其他连接与负载；临时数据库/schema、故障注入和清理只针对本次测试拥有的资源，不操作业务库或配置中心库。故障等待、事务和业务断言保持，环境问题不能用切换数据库或反复重跑掩盖。

Redis / RabbitMQ 继续使用现有测试配置。GitHub CI 各组仍独占 PostgreSQL、Redis、RabbitMQ，四组并行、组内串行；完整发现清单与统一合并门禁保持。

## 已停用的本地入口

`local-postgres.ps1` 和模块的 Start/Test 一律拒绝，且先于任何实例访问；历史脚本调用不会偷偷切换测试目标。

已有 Windows 实例保留以下管理操作，不自动删除其数据、程序或凭据：

```powershell
pwsh -File scripts/local-postgres.ps1 -Action Status
pwsh -File scripts/local-postgres.ps1 -Action Stop
```

Status/Stop 沿用进程身份、数据目录、配置摘要和私有目录权限检查。Stop 仍受测试负载所有权保护；未知状态拒绝，不能按进程名停止数据库。当前工具不提供卸载或递归删除操作。

## 检查义务的变更

`scripts/check-local-postgres.ps1` 保留原有 CI 入口名称，职责改为经真实 CLI 与外部 dotnet 适配器验证：共用 PostgreSQL、Redis、RabbitMQ 配置正确传递、配置文件不变、缺配置失败、负载失败退出、旧本地覆盖在构建及负载前拒绝。CLI/模块 Start/Test 使用无效实例路径验证拒绝先于路径解析；代码中的入口检查也在实例访问之前。

旧本地连接解析、配置覆盖及 Windows 启动、重启、测试生命周期探针退役，原因是对应操作已经禁止；不留永远通过的空检查。Status/Stop 仍适用的身份保护探针保留：Windows 使用无数据库的进程身份装置验证 PID/启动时间不符拒绝 Stop、摘要缺失拒绝 Status；Linux 验证不支持的管理操作拒绝且不创建状态。本轮实际 Status 核对与这些探针分别记录，不将 Linux CLI 拒绝探针当成 Windows 生命周期资格。
