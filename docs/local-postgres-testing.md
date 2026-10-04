# 本地隔离 PostgreSQL 测试

本工具只管理当前 Windows 用户的免安装测试实例。业务与配置中心数据库不参与初始化，
`env/test.dev` 保留；测试仍通过原来的 `run-tests.ps1` 逐工程串行执行。
Redis / RabbitMQ 继续使用已有测试配置，因此这不是整套依赖的本地隔离。
本轮发布资格见 [本地隔离 PostgreSQL 票据](https://github.com/yongpengW/NexusStackNext/issues/84)。

## 运行时与入口

从 [PostgreSQL Windows 官方入口](https://www.postgresql.org/download/windows/)
指向的 [EDB 二进制下载](https://www.enterprisedb.com/download-postgresql-binaries) 获取 Windows x86-64 包。
解压到仓库之外；工具接受其 `pgsql/bin` 路径，不安装服务，不修改注册表、PATH 或防火墙。
支持 PostgreSQL 18；每个实例记录具体版本及四个可执行文件的 SHA-256，复用时核对摘要。

本轮实际使用 `postgresql-18.6-5-windows-x64-binaries.zip`，384620317 字节，`postgres --version` 为 18.6。
本地下载摘要为 `E2246BA91D22345BC3D017586C09EDE52D9DF180B1EEB480F050445F1CAD84E2`。
这是本地文件的摘要记录，不能当成发行方签名或发行方公布的校验值。

```powershell
$bin = 'C:\Tools\pgsql\bin' # 使用自己解压后的路径
./scripts/local-postgres.ps1 -Action Start -RuntimeDirectory $bin
./scripts/local-postgres.ps1 -Action Status
./scripts/local-postgres.ps1 -Action Test
./scripts/local-postgres.ps1 -Action Test -Filter 'FullyQualifiedName~SomeTest' -NoBuild
./scripts/local-postgres.ps1 -Action Stop
```

默认状态目录为当前用户 LocalApplicationData 下的 `NexusStackNext/TestPostgres`。
需要另一份独立实例时，显式指定 `-StateDirectory`，且四个操作使用同一路径。
目录必须位于当前用户 LocalApplicationData 或系统 TEMP 下，不能指向仓库、业务库或其他数据目录。
路径及其祖先不能是重解析点；在创建文件前拒绝目录别名，防止绕到允许范围之外。
首次启动须指定运行时；以后从所有权记录读取。运行时变更须先正常停止，再使用新的独立状态目录。

`Start` 初始化一次、选择空闲回环端口并启动；已启动且验证通过的实例可复用。
`Status` 验证状态，输出版本、端口和私有连接文件路径，连接值不输出。
`Test` 始终使用原 runner，保留构建、过滤和退出码语义；默认 Release，`-NoBuild` 要求先完成同配置构建。
测试结束后实例保留，减少重复 `initdb`；`Stop` 干净停止后保留数据、凭据和证据供下次复用。
没有自动递归删除或重置数据库的操作。

## 安全与故障语义

状态、数据、凭据和诊断文件位于仅当前用户可访问的目录；初始化口令经 `--pwfile`，
客户端认证经 `PGPASSFILE`，真实测试经私有连接文件，口令不进入命令参数或公共输出。
连接文件只允许回环 PostgreSQL 目标；显式空路径、无效内容、非回环目标或缺少凭据时拒绝启动，
不能退回原共享目标。CI 分片及 `-Init` 也拒绝这项本地覆盖。

监听只绑定 `127.0.0.1`，不建立 Unix socket；保留 `fsync`、`synchronous_commit`、
`full_page_writes`，时区为 UTC，认证为 SCRAM。就绪验证读取真实服务的数据目录、端口、监听、
持久化设置、角色与数据库，不能只根据 PID 文件存在报告可用。
配置、认证和连接文件的摘要保留在实例记录中，漂移必须在重启或测试前拒绝。

每个实例的独占文件句柄覆盖整个生命周期操作，`Test` 持有到真实 runner 结束。
启动、停止与测试准备还使用 [全局测试负载保护](local-test-ownership.md)，
防止已有真实测试运行时变更实例。真实 runner 自己取得同一份全局保护；本机不会启用 CI 并行模式。
PID、启动时刻、执行文件及数据目录必须吻合；停止期间保留 Windows 进程句柄，
不按进程名停止，不因文件年龄或 PID 消失接管未知状态。

后台启动将输出定向到私有文件，并临时禁止上层输出句柄被继承；否则 Windows 后代可能保持管道，
使已经退出的 CLI 无法完成输出排空。继承标志在启动后恢复。
相关行为见 [Win32 句柄继承](https://learn.microsoft.com/en-us/windows/win32/procthread/inheritance)。

`LOCAL_POSTGRES_REJECTED` 后保留现场，不删文件绕过保护。
初始化、启动或命令超时可能留下未知状态；`pg_ctl` 超时也不证明后台操作终止，
参见 [pg_ctl 文档](https://www.postgresql.org/docs/18/app-pg-ctl.html)。
先查原工具 session、实例记录、私有日志、精确进程身份及其负载；未证明结束时不重新初始化或停止。
原测试异常退出的恢复仍按全局保护文档执行；不能把后台服务进程结束当成测试子进程全部结束。

## 验证范围与实测

`scripts/check-local-postgres.ps1` 通过真实 runner CLI 和外部 dotnet 适配器验证配置选择、
保留其他配置、拒绝回退、CI / 初始化拒绝及原 runner 的失败退出语义。
加 `-RuntimeDirectory <pgsql/bin>` 才执行真实 Windows PostgreSQL 生命周期与所有权回归。
未指定运行时的便携探针不能证明 Windows 生命周期已通过。
生命周期探针的数据目录与 TEMP 均为独立私有目录，既有业务与配置中心数据库不参与。

先前同为 23 工程、1293 项全部通过的单次比较：共享 PostgreSQL 工程累计 2498.3 秒，
私有 PostgreSQL 累计 1080 秒，约减少 57%；Host 为 12 分 53 秒。
这些是累计工程耗时，不是整条工具调用墙钟，也不是重复基准或每次保证。
可复用工具另经完整 Release 验证：23 工程、1293 项通过，零失败 / 跳过，`env/test.dev` 摘要未变。
工程累计 1138.8 秒，Test 工具墙钟 1147.7 秒，构建加 Test 墙钟 1206.2 秒；Host 为 13 分 53 秒。
与上一轮共享库的工程累计耗时相比减少约 54%，仍是不同环境的两次实测，不是受控重复基准。
双轴评审和最终 Linux CI 仍须在票据中取得证据，不能由这份说明代替。
