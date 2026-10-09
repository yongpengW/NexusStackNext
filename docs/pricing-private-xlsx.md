# Pricing 私有 XLSX 导出

票据 [#57](https://github.com/yongpengW/NexusStackNext/issues/57) 在[私有 CSV 导出](pricing-private-exports.md)的同一入口增加 XLSX。继续本机开发，整体 NS/PoS 目标仍暂停；本轮不包含文件导入、任意报告或对象存储。

## 请求与成果

```json
{"requestId":"11111111-1111-1111-1111-111111111111","itemIds":[],"calculationState":"Any","format":"xlsx","formatVersion":1,"columnSetVersion":1}
```

通过网关提交 `POST /api/pricing/exports`，格式只允许小写 `csv` / `xlsx`，省略为 CSV。格式和版本进入请求摘要；同 Owner / RequestId 改格式为 409。筛选、当前会话、操作许可、本人查询/恢复与下载授权沿原契约。固定快照不会因重试、报价改变或宿主重启更新。

状态的 `format` 标识成果格式；`snapshotDigest` / `snapshotLength` 描述固定 CSV v1 快照文本。选定候选后 `artifactDigest` / `artifactLength` 描述实际文件字节，生成前为空。XLSX 不用快照摘要冒充 ZIP 摘要。Int64 元数据继续以十进制字符串传输。

工作簿只含一个 `Quotes` 工作表，第一行九列表头，后续行保持冻结顺序：ItemId、Version、Cost、FeeRate、InputRevision、CalculatedRevision、CostingRevision、BreakEvenPrice、CalculationState。全部写成显式 `inlineStr` 文本，金额用 invariant 四位小数，空结果为空文本；没有宏、公式、外链、自动超链接、任意列或模板名称。

Excel 直接打开即可保留 `9223372036854775807` 与 `99999999999999.9999` 的完整文本。文本金额不会自动作为数值求和；使用者转换为 Excel 数值时须自行选择精度和舍入，转换不能保证精确原值。下载类型为 `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet`，附件名 `export.xlsx`，缓存为 private / no-store。

## 生成容量与完整性

最多 5000 数据行、九列，每宿主一个生成名额；整个生成/检查/上传服从原固定执行期限。实际 ZIP 写入、定位、扩展和关闭包受最多 32 MiB 限制，临时文件独占、自动删除，Unix 写前 unlink、权限仅当前用户。部署仍须给专用临时卷配置物理容量及内存限制。

关闭包后逐条解压核验完整长度与 CRC，固定且唯一的五个 ZIP 条目、展开合计最多 16 MiB；再检查 Office 2007 工作簿结构、工作表关系、精确行列/引用、文本类型、每格最多 64 字符及冻结值。收尾写入失败、取消、残缺 ZIP 或检查失败不能选定为成功成果。验证会读取有界 DOM；Open XML SDK 的 ZIP 实现也可能缓存数据，顺序写入不等于恒定内存。

最大样本测试使用 5000 行、long.MaxValue、decimal(18,4) 上边界，并以独立 ZIP/XML 读取器检查完整行列和精确值。测量范围是生成及完整性检查，不含随后测试读取器的内存；工作集为同测试进程观测值而非分配上限，5ms 采样可能遗漏短峰。临时文件测量为逻辑长度而非文件系统分配块，单样本不能替代生产容量规划。

Windows 本轮初始样本：ZIP 173800 字节、展开 XML 3571087 字节、临时文件峰值 173800 字节；观测工作集约 86.3 → 163.0 MiB，生成/检查 1440ms。最终 Windows 和 Linux 数据以票据资格记录为准，环境和同进程前序测试会影响工作集与耗时。Linux 完整 CI 的 `pricing-xlsx-capacity` 只上传已核验的固定数字/OS，原始日志与配置不上传。

这些单份/单执行限制不限制全部待办快照、历史委托或 Files 持久字节的总容量；全局准入和历史保留仍需后续治理。

## 未知结果与迁移

UploadId 绑定原文件实际摘要。恢复先查原回执：Staged 直接选定原文件，无需临时目录或重新渲染；Pending 只有新字节与原摘要/长度相同才继续。ZIP 字节不同则 `pricing.export.candidate_bytes_conflict`，委托停机且本人 retry 返回 409，使用新 RequestId 另建委托。Publishing 只恢复原 FileId / PublicationId，响应丢失、登出和进程死亡不会改变成果。到期/删除保留历史，下载拒绝，不续期或复活。

部署执行独立 `--migrate-pricing`，增量 `20261009160110_PrivateXlsxExports` 保留全部原迁移、报价及委托；旧格式为 CSV，已有 FileId 的 CSV 回填实际摘要/长度。存在任何委托或发布历史时拒绝降级；空导出库允许回退并重新升级。

实现依赖 Open XML SDK / Framework 3.5.1、System.IO.Packaging 10.0.12，均 MIT，中央版本固定。通知随模板、构建和发布产物保留，见 [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md)。关系及身份决定见 Pricing [ADR-0005](../src/Services/Pricing/docs/adr/0005-export-format-and-sealed-artifact-identity.md)。

## 验证边界

| 场景 | 公开边界与证据 |
|---|---|
| 默认 CSV / XLSX 请求身份冲突 | ISender，PricingExportAcceptanceTests |
| 长整数、小数、零、空值、Stale、最大容量 | PricingXlsxV1 输出流与独立 ZIP/XML 读取器，PricingExportXlsxTests |
| 关闭失败、取消、残缺 ZIP | 调用方拥有的故障输出流，PricingExportXlsxTests |
| 本人 XLSX 下载、MIME/附件/缓存 | 真 HTTPS，GeneratedFilesHttpsTests |
| 原快照、丢发布响应、进程死亡、另一用户/撤销会话/删除 | 真网关与三独立宿主，PricingExportJourneyTests |
| 暂存后进程死亡且临时目录不可写 | 真 HTTPS 断点及 OS 进程结束，原回执恢复，PricingExportJourneyTests |
| Pending 原身份字节冲突、到期不复活、资源释放 | 真 HTTPS / ISender，GeneratedFilesHttpsTests 与 PricingExportResourceRecoveryTests |
| 旧 CSV 回填、历史拒降级、空库回退再升级 | 真实迁移命令及公开查询，PricingExportMigrationTests |

完整资格为 Windows 相关 build → tests → format、凭据/原生票据/模板检查、双轴评审及最终候选完整 Linux CI。定向通过不能代替完整 CI 或声称所有 NSN 能力已完成。
