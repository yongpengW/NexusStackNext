# 私有文件访问与持久化

文件默认私有，上传归属取当前有效会话。只有归属者能够读取元数据、下载和删除；知道文件标识、伪造归属参数或持有根账号都不授予他人文件访问权。其他归属者与不存在的文件统一返回 404，已撤销的会话返回 401。

| 操作 | HTTP |
|---|---|
| 上传原始字节 | `POST /api/files?name=report.xlsx` |
| 查询元数据 | `GET /api/files/{id}/metadata` |
| 下载 | `GET /api/files/{id}` |
| 删除 | `DELETE /api/files/{id}` |
| 查询删除是否完成 | `GET /api/files/{id}/deletion` |

上传成功返回 `fileId`、`name`、`size`；元数据和上传响应都不返回 `StorageKey` 或磁盘路径。下载保留原始字节，不套 JSON，使用附件响应及 `private, no-store`、`nosniff` 响应头。文件名拒绝路径分隔符、相对路径段和控制字符；Content-Type 必须是有效、具体的媒体类型。

## 存储与迁移

配置 `ConnectionStrings__Files`（配置中心键 `ConnectionStrings:Files`）。Files 默认使用 PostgreSQL，拥有独立的 `files` schema、DbContext 和迁移历史；允许与 Identity、Platform 指向同一物理数据库，但三个连接都必须显式配置。

先执行 `pwsh -File scripts/migrate-files.ps1`，再启动平台宿主。脚本只读取私有 `env/platform.dev` 或环境中的 Files 连接；发布产物运行 `dotnet NexusStackNext.PlatformHost.dll migrate-files`。命令不启动 HTTP、配置中心、消息总线或账号播种，重复执行幂等。

普通启动只检查已应用的迁移和数据库可用性；未迁移或不可用会拒绝启动。运行期数据库或磁盘故障使 `/health/ready` 返回 503，`/health/live` 仍只检查进程。

字节根目录由 `Files__StorageRoot` 配置；部署应使用持久卷或应用发布目录之外的专用目录，并同时备份数据库元数据与字节。开发默认目录为应用目录下的 `file-storage`。不要把它作为静态站点根目录公开。

目录内的 `.nsn-storage-id` 是持久存储身份，备份、迁移时必须与字节一起保留。句柄绑定此身份；挂载丢失或路径被空目录替换时，不得把“找不到文件”认作删除成功。重启后即使新目录可以写入，旧文件的删除仍保持待恢复，直到原存储恢复。此前内存模式产生的无持久归属文件不自动导入，也不自动删除。

仅 Development / Testing 允许显式选择 `Files__Storage__Provider=Memory`。无库平台演示还需同时选择 Identity 和 Platform 的 Memory 模式；内存元数据会在重启后丢失，已有磁盘字节不能因此被认作公开文件。

## 上传资源限制

`Files__Upload__MaxBytes` 默认 64 MiB；`Files__Upload__MaxConcurrentUploads` 默认每宿主 4 个。必须为正数。上传逐段读写，不要求请求体可定位，不把整份内容缓存在内存中；缺少 Content-Length 也会按实际字节计数。

超限返回 413，名额用完返回 429，不进入无限等待队列；取消或失败会释放名额。文件端点使用同一配置设置 Kestrel 大小限制；外部反向代理仍需单独配置其请求限制。进程能够正常处理失败时会尝试删除部分文件，文件完整写入并提交元数据后才返回 201。

## 删除与孤儿回收

删除先持久停止提供文件，再尝试清除字节。完成时返回 204；存储暂不可用时返回 202，`Location` 指向删除状态接口，响应中的 `completed=false` 表示还有持久待办。只有归属者可以查询；后台在存储恢复或进程重启后继续处理，完成状态也会保存。重复删除已完成的文件返回 204。404 仅表示不可访问，不能作为物理字节已清除的证明。

恢复配置：`Files__Cleanup__IntervalSeconds=30`、`Files__Cleanup__BatchSize=64`、`Files__Cleanup__RetryDelaySeconds=30`、`Files__Cleanup__OrphanAgeSeconds=3600`。每轮只扫描有界候选；失败删除有持久重试时间。每份文件单独提交，不将整批文件并入一个聚合事务。

上传使用独立且永不复用的句柄，在完整字节写入及元数据保存期间持有写入保护。回收只扫描本适配器管理的句柄，先取得同一保护，再与元数据保存共用事务锁确认无引用，并保存退役标记。只有确认退役后才删除字节；数据库不可用或提交结果未知时保留字节并重试。退役句柄不允许再次发布元数据，记录不自动过期。

“HTTP 失败”不能证明“数据库未提交”。故障验收将数据库停在 COMMIT 阶段，再结束宿主进程；原事务若提交，下载必须仍返回原字节。移除回收事务锁后，这个测试会复现元数据已提交但字节被误删，恢复锁后通过。设计取舍见 [ADR-0003](../src/Services/Files/docs/adr/0003-file-recovery-fences-late-publication.md)。

故障验收还覆盖空目录替换、活跃慢速上传、写入中途结束进程、并发删除以及无法清理的单个孤儿。恢复循环不会因一个坏文件而停止处理后续候选；失败会留下日志并继续重试。HTTP 验收覆盖归属与根账号隔离、会话撤销、独立迁移、真实进程重启、数据库故障、超限、上传取消及真实网关的二进制响应。

依据：[PostgreSQL 事务级 advisory locks](https://www.postgresql.org/docs/current/explicit-locking.html#ADVISORY-LOCKS)、[EF Core 提交失败与结果未知](https://learn.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency#transaction-commit-failure-and-the-idempotency-issue)。
