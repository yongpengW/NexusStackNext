# 审计调查私有 XLSX 导出

对应 [审计调查导出与本人下载 #68](https://github.com/yongpengW/NexusStackNext/issues/68)。导出只读取 Auditing 已收到的证据，不联查来源业务表。事实和操作观察分别申请，工作簿不包含请求正文、业务原值、异常正文、凭据或任意选择列。

## 申请与查询

```json
{
  "requestId": "11111111-1111-1111-1111-111111111111",
  "facts": { "source": "platform", "subjectId": "example.setting" }
}
```

`POST /api/auditing/exports` 接受本人委托，返回统一响应中的 202、exportId、固定窗口、行数和版本。`facts` 与 `operations` 必须且只能提供一种，筛选字段与[调查接口](committed-auditing.md)对应；分页字段不参与导出。操作筛选使用最终已观察阶段；缺失开始或完成阶段的单元格为空，只有开始证据时 Outcome 为 unconfirmed，不推断成功或耗时。

省略时间默认最近七天，最大三十一天。快照一次 SELECT 读取，最多 5000 行、序列化文本最多 8 MiB；空结果或超限拒绝，不截断。请求实际最多 16 KiB，包括分块请求，读取总预算十五秒；未知、重复字段为 400，非 JSON 为 415，超限为 413，超时为 408。Owner 来自当前身份。

同一 Owner / RequestId、同一规范化筛选重放原委托；省略窗口不会在重放时按新时间重算。不同筛选为 409。响应未知或 503 时保留 RequestId 重试；新 RequestId 表示新委托。接受事务总预算十五秒、锁两秒、语句五秒。

| 操作 | 入口 |
|---|---|
| 本人中心 | `GET /api/auditing/exports?page=1&limit=50` |
| 原委托状态 | `GET /api/auditing/exports/{exportId}` |
| 条件取消 | `POST /api/auditing/exports/{exportId}/cancel` |
| 条件恢复 | `POST /api/auditing/exports/{exportId}/retry` |
| 当前成果可用性 | `GET /api/auditing/exports/{exportId}/artifact` |
| 私有字节 | artifact 中的相对 `/api/files/{fileId}` |

所有导出入口和审计成果下载使用同一独立操作许可 `/api/auditing/exports:POST`，普通调查读取许可不能代替它。Identity 当前许可每次重新裁决；权限撤销影响原令牌，登出后为 401。所有入口按精确 Owner 隔离，根身份也不能读取他人的委托或文件。元数据不读取冻结行；中心分页最多 200，最多第 1000 页，按接受时间及身份降序，属于当前列表，不承诺跨页快照。

取消/恢复正文为 `{"expectedVersion":"1"}`。取消只允许 Queued / Generating，重复取消不推进版本；Publishing 后只能恢复原成果。自动尝试默认三次、最多十次，耗尽为 Failed；本人恢复最多十次，版本过期或不能恢复时为 409。七天后释放未完成的生成快照并停止重新生成；已经选定的原成果仍可继续发布恢复，无须保留快照。原请求身份及发布历史长期保留。

## 工作簿与交付

固定 XLSX v1 事实列为 EvidenceKind、EntryId、MessageId、EventName、Source、Action、SubjectType、SubjectId、SubjectVersion、ActorId、OccurredAt、RecordedAt、TraceId、CorrelationId、OperationId、OperationSource、RootOperationId、RootSource、InitiatorId、RelatedContext、RelatedSubjectType、RelatedSubjectId。

操作列为 EvidenceKind、OperationId、Source、Kind、Action、ExecutionRole、SubjectType、SubjectId、ActorId、TraceId、CorrelationId、StartedAt、FinishedAt、Outcome、HttpMethod、RouteTemplate、StatusCode、DurationMs、RootOperationId、RootSource、ParentOperationId、ParentSource、InitiatorId、TaskId、TaskEpoch、SchedulePlanId、ScheduleExpectedVersion、ScheduleDecisionId。

所有单元格为 inline string，长整数、UUID 和时间使用精确 invariant 文本，公式状文本不会执行。事实与操作各有独立工作表名，未知证据为空。实际 ZIP 包含收尾写入最多 32 MiB；完整包经允许部件、CRC、OpenXML 及读回检查后才封存。

数据库领取使 Auditing 全局最多一份有效执行权，固定期限默认两分钟，不续租；过期执行者不能写入状态。HTTP、临时盘和哈希不持有数据库事务。每次生成使用独占私有临时文件，关闭或进程退出清理；Unix 写前移除目录项。七天内按原快照恢复，不重新分页读取活表。

Files 交付沿用[生成成果协议](private-generated-files.md)，Producer 固定 auditing。UploadId、SourceExportId、PublicationId 都由原 exportId 确定，处于不同协议身份空间。发布前先持久化原 FileId / 摘要 / 长度；内容或发布回执丢失、进程重启后查询同一候选或同一发布，不创建替代文件。Succeeded 保存原 PublishedAt / ExpiresAt，外部时间以 UTC ticks 保持精度；当前 Available / Expired / Deleted / StorageUnavailable 由 Files 决定，只有 Available 提供下载路径。过期或删除不改写原成功历史、不续期。

每份请求、快照、工作簿、执行预算和有效生成并发均有界。累计请求数与全部冻结快照没有全局准入配额，不能据此声称总数据库或磁盘容量有界。

## 配置与迁移

PlatformHost 默认 `Auditing:Exports:Enabled=false`；需要 Auditing PostgreSQL。模板见 [审计导出配置](../config/nexusstack_platform.audit-exports.template.json)，真实地址与证书路径留在私有平台配置。启用时校验 HTTPS 服务证书配置，`Worker:Enabled=false` 仅关闭处理，保留申请和本人中心。

`Execution` 配置：LeaseDuration 默认两分钟（1 秒至 2 分钟）、MaxAttempts 默认 3（1 至 10）、MaxOutputBytes 默认 33554432（最多 32 MiB）、PollInterval 默认 1 秒（100ms 至 30 秒）、TemporaryDirectory 默认系统临时目录，指定时须绝对路径。生产临时目录可使用带容量限制的专用卷，不与 Files 持久卷混用。

`Files` 客户端配置沿用 Pricing 已验证的 HTTPS 协议：明确根/中间 CA、clientAuth 证书和私钥，检查主机名，拒绝重定向，单次网络默认二十秒、响应最多 16 KiB。生产撤销模式必须 Online，Development / Testing 才允许显式 NoCheck。Files 端独立映射证书到 auditing 生产者；用户 JWT 不作为生产者身份。

先执行 PlatformHost 的 `migrate-auditing` 独立迁移，普通宿主启动不迁移。增量 PrivateInvestigationExports 新增本上下文 exports 表，保留既有事实、观察、指纹及 Inbox，不重置已合并迁移。存在任何导出历史时拒绝 Down（auditing_exports_history_retained），检查与删除在同一排他锁事务内。

Pricing 与 Auditing 已成为两个真实消费者，现共用 Files.Contracts 的 IExportFiles、基础设施 HTTPS 适配器和精确文本 XLSX 写入实现；请求、筛选、状态及列集仍分别属于各上下文，没有通用报表框架。
