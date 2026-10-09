# Pricing 私有报价 CSV 导出

实现对应 [#150](https://github.com/yongpengW/NexusStackNext/issues/150)，依赖已交付的 [Files 生成成果协议](private-generated-files.md)。完整故障矩阵由 [#151](https://github.com/yongpengW/NexusStackNext/issues/151) 承载；父票 [#56](https://github.com/yongpengW/NexusStackNext/issues/56) 的完成以原生验收记录为准。继续本机开发，整体 NS/PoS 目标仍暂停。

## 接受一次委托

所有入口经过网关、当前会话与 Identity 单操作许可，具体归属由 Pricing 再按当前用户裁决。根身份也不能查看或操作他人的导出。

```json
{
  "requestId": "11111111-1111-1111-1111-111111111111",
  "itemIds": [],
  "calculationState": "Any",
  "formatVersion": 1,
  "columnSetVersion": 1
}
```

`POST /api/pricing/exports` 返回统一响应包中的 202 和稳定 `exportId`。`itemIds=[]` 表示全部报价；对象标识最多 5000 个，去重并排序后参与规范化。状态仅允许 Any / Pending / Stale / Current。Owner 来自当前身份，正文不能指定 Owner、列或自由 SQL。重复字段、未知字段、无效版本与空 UUID 为 400，非 JSON 为 415。请求按实际字节限制 256 KiB，包括没有 Content-Length 的分块请求；超限为 413，读取总预算十五秒，超时为 408。

一次本地短事务从 Pricing 自己的报价表读取一致的 MVCC 快照。筛选结果为零或超过 5000 行则拒绝，不截断、不遗留工作。事务操作共享十五秒预算，锁两秒、语句五秒、闲置事务五秒。规范化请求和固定 CSV 字节分别使用版本化 SHA-256 摘要，完整快照持久化在 Pricing 库，后续报价与缓存变化不影响它。

客户端保留原 RequestId：同 Owner / RequestId、同规范化内容重放原 Export、接受时间和快照；不同内容为 409。响应丢失或 503 不证明回滚，使用原 RequestId 重试。重新生成 RequestId 会创建新委托。

## 本人下载中心

| 操作 | 入口 |
|---|---|
| 本人列表 | `GET /api/pricing/exports?limit=50&state=Succeeded` |
| 原委托详情 | `GET /api/pricing/exports/{exportId}` |
| 条件取消 | `POST /api/pricing/exports/{exportId}/cancel` |
| 恢复停机委托 | `POST /api/pricing/exports/{exportId}/retry` |
| 当前成果可用性 | `GET /api/pricing/exports/{exportId}/artifact` |
| 实际私有字节 | artifact 给出的 `/api/files/{fileId}` |

取消和恢复正文为 `{"expectedVersion":"1"}`；Int64 使用[十进制字符串契约](http-int64-contract.md)。每个入口都需登记自身路径模板及 HTTP 方法的操作许可。取消只允许 Queued / Generating；Publishing 之后为 409，不能撤回已经提交的发布意图。已取消的重复取消是空操作。恢复只接受 Failed 或 Publishing 下的 Stopped 交付；每阶段最多十次本人恢复，每次仍有有限自动预算。版本过期、已完成或不能恢复的快照返回 409。

列表默认 50、最多 200，按不可变 AcceptedAt / ExportId 降序使用 keyset 游标；可按固定状态、acceptedFrom / acceptedThrough 筛选。后续请求原样携带 nextCursor 及原筛选；游标绑定 Owner 和筛选。新接受的导出不挤乱已翻页的接受顺序，执行状态变化仍按当下筛选。元数据查询只读取明确列，不读冻结行、请求规范化文本或执行来源载荷。

状态提供 RowCount、SnapshotLength、ConfirmedGeneratedRows、执行代次、固定期限、审计信息及白名单错误码。ConfirmedGeneratedRows 在完整封存并选定成果后才为 RowCount，之前为零；它不把生成中的局部写入包装成完成百分比。Delivery 另有 Pending / Delivering / Stopped / Delivered、交付代次和自动/本人恢复预算。暂存成果只能通过内部协议访问，不向本人入口开放。

Succeeded 记录原发布历史；artifact 查询 Files 的当前 Available / Expired / Deleted / StorageUnavailable，只有 Available 返回相对下载地址。到期、删除及迟到回执不改写原 PublishedAt / ExpiresAt、不复活、不续期。Files 下载重新验证当前会话及精确 Owner；登出后已接受委托继续运行，旧会话查看与下载均为 401。

## 生成、发布与恢复

每个宿主一个生成名额。领取持久化单调 Epoch、LeaseUntil 与本次固定 MaxLeaseUntil；独立作用域续租最多到首次总期限。过期、取消或被接管的执行者不能提交成果。数据库时间裁决租约，HTTP、临时盘写入与哈希计算不持有数据库事务。

CSV v1 为 UTF-8 无 BOM、CRLF、固定九列：ItemId、Version、Cost、FeeRate、InputRevision、CalculatedRevision、CostingRevision、BreakEvenPrice、CalculationState。UUID、Int64 与四位小数采用 invariant 文本；空结果金额为空列。列中只有这些固定值及状态，不含用户自由文本或公式，所以无逗号、引号或换行转义需求。实际输出逐行计数，最大 32 MiB。每次私有临时文件以独占句柄创建并 DeleteOnClose，Unix 权限仅当前用户读写。

应用层的 IExportFiles 由真实 HTTPS 适配器实现，只依赖 Files.Contracts。客户端使用明确根、中间 CA、clientAuth 证书和私钥，保留默认主机名检查，拒绝重定向；单次网络预算默认二十秒、响应实际最多 16 KiB。Producer 必须为 pricing，Owner、SourceExportId、摘要、长度、版本、UploadId / FileId / PublicationId 与时间结构完整核验。

UploadId 在首次领取时持久化，跨自动接管和本人恢复保持不变。响应未知先查询原候选，不能创建新身份绕过未知结果。当前生成代次在同一短事务选定唯一 FileId / PublicationId，并创建只含引用与交付元数据的发布 Outbox；该交付记录属于同一个 PricingExport 聚合。

后台发布先查询原 PublicationId，缺失才提交同一意图。得到完整匹配回执后，本地原子提交 Succeeded 与 Delivered。断网、响应丢失和重启沿原意图恢复；预算耗尽为可查询的 Failed / Stopped，由本人条件恢复。已经进入 Publishing 的恢复只交付原成果，不再生成。Files 的外部时间历史按 UTC ticks 精确保留，包括合法的 100ns 精度；本地审计与接受时刻仍采用 PostgreSQL 时间精度。

接受时保存原执行来源；后台生成和发布使用系统 Actor，保留 RootOperationId、原 Initiator 与接受操作的父级关系。日志来源 journal 属于 Auditing 模块，仍与业务事务和就绪分组独立。导出尚未新增业务已提交事实契约，不能把这些操作观察称为导出事实的完整审计覆盖。

## 配置与迁移

PricingHost 默认 `Pricing:Exports:Enabled=false`。启用时显式装配端口、证书校验、端点和后台执行；`Pricing:Exports:Worker:Enabled` 默认为 true，可在诊断时关闭后台，保留本人查询。配置片段见 [Pricing 导出模板](../config/nexusstack_pricing.exports.template.json)，仅放在独立 Pricing 应用中，不放共享基座。真实证书、私钥、连接串和服务地址留在私有配置。

| `Pricing:Exports:Execution` 键 | 默认 | 上限 |
|---|---:|---:|
| LeaseDuration | 30 秒 | 2 分钟 |
| MaxExecutionDuration | 2 分钟 | 10 分钟且大于租约 |
| MaxAttempts | 3 | 10 |
| MaxOutputBytes | 33554432 | 32 MiB |
| PollInterval | 1 秒 | 30 秒 |

生产只允许 `Files:RevocationMode=Online`，未知撤销状态拒绝，私有 CA 必须提供可验证的 CRL / OCSP。Development / Testing 才可显式 NoCheck。客户端和 Files 生产者许可分别配置，不把用户 JWT 转成服务身份；业务 listener 仅私网可达，网关是唯一公共入口。

部署前运行 PricingHost 独立 `--migrate-pricing`，普通启动不迁移。增量 `20261009093209_PrivatePricingExports` 新建本上下文 exports / export_publications，保留旧报价、任务、审计与事实 Outbox，不重置已合并历史。存在任何 Export 历史时拒绝降级，约束名 `pricing_exports_history_retained`；取消和终态也保留身份。检查与删除在同一事务持排他表锁，锁两秒、语句五秒。没有 Export 的库可降级并重新升级，旧报价保留。

领域依据见 [Pricing ADR-0004](../src/Services/Pricing/docs/adr/0004-private-exports-own-frozen-snapshots-and-publication-intents.md)。已补公开 HTTP / ISender / 字节测试，包括 5000 行边界、未知上传/发布回执、并发取消与选择、真实网关普通用户下载和 Pricing 进程重启；最终完整回归、双轴评审和 PR / dev CI 资格单独记录在 #150。#151 将继续扩充跨宿主故障、迟到回执与生产验收矩阵。
