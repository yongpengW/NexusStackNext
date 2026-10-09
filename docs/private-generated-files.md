# 私有生成成果

这是 [#56](https://github.com/yongpengW/NexusStackNext/issues/56) 的 Files 协议子票 [#149](https://github.com/yongpengW/NexusStackNext/issues/149)。实现按相关回归、双轴评审、完整 Linux CI 与合并后 dev CI 验收，实际交付资格见原生票据。本协议不代表 Pricing CSV 导出整票交付；Pricing 接受和生成由 #150、完整跨宿主验收由 #151 承载。继续本机开发，NS/PoS 整体目标仍暂停。

## 身份与部署

`Files:Producer:Enabled` 默认为 false。启用后，PlatformHost 显式组装命名证书认证和内部 Files 协议；普通用户仍使用默认 JWT 与当前会话校验。内部入口不属于 `/api`、不加入网关路由，也不出现在公共 OpenAPI。业务 listener 只在私有网络可达，不能向宿主机公开端口。

HTTPS listener 使用 Kestrel 原始客户端证书、限定根集合及完整链校验。`RootCertificatePaths` 为一至八个 PEM CA 证书路径；可另配最多八个 `IntermediateCertificatePaths`，它们只参与建链，不成为信任根。`Certificates` 是一至三十二项 `{Sha256, Producer}`：SHA-256 完整叶证书指纹映射到明确生产者，Producer 为一至三十二个小写 ASCII 字母、数字或连字符。可用两张证书映射同一 Producer 做轮换；配置变更通过重启生效。证书对象由 DI 宿主创建并释放，不安装测试 CA 到系统证书库。

叶证书必须非 CA、具有明确 clientAuth EKU 和 digitalSignature 用途，且链、有效期、指纹许可都通过。CN、Owner、普通 JWT 和 `X-ARR-ClientCert` 不提供生产者身份；没有证书或普通 HTTP 均不能调用。Producer 没有用户 ID，不冒充 Owner，也不能通过公共入口下载任意用户文件。

生产环境只允许 `RevocationMode=Online`，保持 `NoFlag` 和 ExcludeRoot；PKI 必须提供可验证的吊销状态，未知状态也拒绝。Development/Testing 才可显式配置 NoCheck。临时 CA 的测试已分别验证真实链、明确中间 CA、过期/用途/许可拒绝，以及 Production 关闭吊销校验的启动拒绝、无 CRL/OCSP 的未知状态拒绝；它们不等于验证了真实 CA 的 revoked 路径。

TLS 握手上限十秒，TLS 链下载设置两秒且禁止隐式中间证书下载。框架每请求证书认证另有平台链验证预算，不能把握手或 TLS 下载限额称为整个请求的精确吊销超时。框架和跨平台依据见[研究记录](research/2026-10-09-private-export-service-auth.md)。服务器证书和私钥由 Kestrel HTTPS 配置提供，路径及凭据只在私有环境配置，不随模板提交。

## 版本化内部协议

Contracts 属于 Files。调用方只依赖 `NexusStackNext.Files.Contracts`，不读 Files 表、不拼存储路径。所有 Int64 JSON 值使用[十进制字符串契约](http-int64-contract.md)。

| 操作 | 路径与结果 |
|---|---|
| 登记固定描述 | `POST /internal/files/v1/uploads/{uploadId}` → 202 UploadReceipt |
| 提交实际流并封存 | `PUT /internal/files/v1/uploads/{uploadId}/content` → 201 UploadReceipt |
| 原上传历史 | `GET /internal/files/v1/uploads/{uploadId}` |
| 发布封存候选 | `POST /internal/files/v1/uploads/{uploadId}/publish`，正文 `{publicationId}` |
| 原发布历史 | `GET /internal/files/v1/publications/{publicationId}` |
| 当前可用性 | `GET /internal/files/v1/uploads/{uploadId}/availability` |

固定描述包含 OwnerId、SourceExportId、完整 SHA-256、实际预期 Length、`format=csv`、`formatVersion=1`、`columnSetVersion=1`。SHA-256 规范化为小写；身份与描述在首次登记后不变。Files 验实际长度与摘要，CSV 列与内容的生成语义由来源上下文负责。

UploadId 和 PublicationId 均由来源持久工作提供，且非空；两者分别在 Producer 命名空间内唯一。同身份同描述/字节保留原 FileId、时间及期限，异描述、Owner、来源或内容为 409，不能用另一发布身份替换同一成果。其他 Producer 只能看到不存在。UploadReceipt 的 Pending/Staged 是封存历史，PublicationReceipt 是首次发布历史，均不表示现在可下载。

Availability 为 Pending / Staged / Available / Expired / Deleted / StorageUnavailable。`cleanupCompleted` 单独表示已提交字节清除完成，默认 false；到期、删除、404 均不能替代它。发布回执在到期/删除后仍可恢复，重放不得续期或复活。

## 预算与恢复

| `Files:Generated` 键 | 默认 | 允许范围 |
|---|---:|---:|
| MaxBytes | 33554432（32 MiB） | 1–32 MiB |
| MaxConcurrentUploads | 1 | 1–4 |
| UploadTimeoutSeconds | 30 | 1–120 秒 |
| StageLifetimeSeconds | 86400（24小时） | 1–172800 秒 |
| DownloadLifetimeSeconds | 604800（7天） | 1–2592000 秒 |

字节流逐段计数和摘要，不信任 Content-Length，不整份缓存在内存；错误完整流、过长/过短或取消不封存。流写入不占数据库事务，写入保护保持到封存裁决结束。名额用完立即 429，超过当前预算 413，上传自己的时限用尽 408。降低大小预算后，原历史仍可 GET；新的字节提交和重放仍受当前预算约束。

PostgreSQL 登记、封存、发布与到期裁决均重新读取当前聚合，以 `(Producer, UploadId)` 的事务锁裁定；PublicationId 另有唯一约束及裁决锁。聚合、行审计与最小事实在单个 Files 事务保存，重放不推进 Version、审计时间或事实。所有执行策略尝试共享十五秒总预算，锁两秒、语句五秒、闲置事务十秒。总预算用尽为 503 / `files.candidate.unavailable`；它不是回滚证明，调用方须按原身份 GET/重试。

首次接受与首次发布取 Files 数据库时间。到期立即拒绝新下载，即使清理者尚未运行或事实容量暂不可用；普通上传文件不增加期限。后台复用 `Files:Cleanup` 的有界调度，按截止时间索引读取有限候选，每个聚合单独提交到期事实及清理结果。锁后重读防止旧暂存扫描删除刚发布的成果。Memory 在共用写锁内对等裁决，时间来自注入时钟，不能保证跨进程历史。

HTTP 失败或 COMMIT 结果未知不立即删除字节；继续沿用[已有写入保护、退役与孤儿栅栏](../src/Services/Files/docs/adr/0003-file-recovery-fences-late-publication.md)。封存 COMMIT 中进程终止后，按原 UploadId 恢复原 FileId；发布响应未被调用方读取时，重启后按 PublicationId 恢复原裁决。存储掉线只改变当前可用性及字节请求的 503，不覆盖历史；恢复后原 FileId 返回原字节。

## 本人访问、事实和迁移

发布后，当前有效 Owner 通过既有 `/api/files/{fileId}`、`/metadata`、DELETE、`/deletion` 使用私有成果，另有 GET `/api/files/{fileId}/availability` 观察本人已发布成果的终态。根身份不隐式拥有他人文件，撤销会话为 401。他人、未知标识及从未发布的候选均为 404，包括其到期墓碑；生产者仍可恢复自己的历史和清理确认。登出不撤销已经由来源接受的服务委托。

候选沿用 StoredFile 行审计与 Files Outbox，新增固定 `published`、`expired` 生命周期动作；到期同时受理清理，字节移除另有事实。中央通过原 `StoredFileCommittedV1` 消息接收白名单动作，不查询 Files 表。不记录文件名、二进制、句柄、路径或秘密；服务和后台事实的用户 ActorId 保持 null，不从 Owner 伪造操作者。

普通宿主不自动迁移。部署前按[Files 独立迁移入口](private-files.md)执行 `--migrate-files`，增量 `20261009063803_PrivateGeneratedFileProtocol` 在 Files 原存储表追加可空字段、唯一身份及截止索引，保留旧普通文件和审计，不重置已合并迁移历史。开发过程的阶段迁移在提交前收拢为这一份，减少重复模型快照。模板配置默认关闭生产者入口，不提供证书、私钥或连接秘密。

存在任何候选历史时，协议迁移拒绝降级，返回约束名 `files_candidate_history_exists`；包括已删除和到期墓碑。检查前在同一事务取得排他表锁，锁等待最多两秒、语句最多五秒，避免与新登记竞争丢失身份。普通文件且没有候选的库仍可降级。EF 多步降级可能先完成较新的迁移再被旧历史保护拒绝；恢复最新结构后再启动当前模型，不能把一次拒绝理解为整条降级链已原子撤销。

实现与取舍见 [Files ADR-0005](../src/Services/Files/docs/adr/0005-private-generated-files-have-durable-publication-receipts.md)。测试面为真实 HTTPS/HTTP、实际字节、独立迁移与进程恢复；业务结论不靠直接查表。冻结候选整组回归与最终交付证据以 #149 的最新原生记录为准。
