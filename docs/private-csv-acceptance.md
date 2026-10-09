# 私有报价 CSV：父规格与故障验收

对应 [#56](https://github.com/yongpengW/NexusStackNext/issues/56) 与 [#151](https://github.com/yongpengW/NexusStackNext/issues/151)。延续已确认的 HTTP / ISender / 实际字节边界，独立宿主、网关、迁移 CLI 是真实运行边界。数据库仅用于本例准备及故障屏障，业务裁决从公开接口读取。覆盖位置不等于执行通过；精确提交、测试身份、两轴评审、四份 CI 报告与合并后 dev 资格由原生票据记录。

## 十二条义务逐项对应

`PricingExportAcceptanceTests`、`PricingExportCsvTests`、`PricingExportMigrationTests` 在 `tests/Pricing.IntegrationTests`；其他下列旅程在 `tests/HostIntegration.Tests`。

| #56 条目 | 公开边界与真实覆盖 |
|---|---|
| 1. 许可、本人归属、白名单、请求/行数边界 | `PricingExportJourneyTests.Ordinary_owner_recovers_lost_https_publication_after_pricing_process_death_through_gateway_and_deletion_cannot_resurrect_it`：真网关、普通角色、未知/重复字段、无效版本/状态、非 JSON、实际 chunked 256 KiB 超限、空选择；根用户与另一同权限用户的详情/成果/元数据/字节/取消/恢复/删除拒绝。`Empty_or_over_limit_selection_creates_no_work_but_exactly_five_thousand_rows_are_accepted`：零、5001、5000 行与无残留列表。 |
| 2. 一致冻结、规范化重放、冲突、摘要、接纳未知提交 | `Concurrent_normalized_request_retries_share_one_snapshot_but_another_owner_has_a_separate_identity`、`Repeated_export_after_quote_change_and_restart_returns_the_original_frozen_snapshot`、`PricingExportCommitRecoveryTests.Server_committed_acceptance_with_lost_wire_reply_replays_original_snapshot_after_quote_change`：原请求和公开列表观察同一时间/标识/摘要，新请求才观察后续报价变化。 |
| 3. 当前代次、有限租约/生成、无长事务、精确 CSV、资源释放 | `Renewal_and_expired_takeover_keep_original_upload_identity_and_cannot_revive_old_epoch`；`PricingExportGenerationRecoveryTests.Generation_process_death_releases_temp_bytes_and_restart_reuses_pending_upload_while_stale_upload_cannot_select_publication`；`PricingExportResourceRecoveryTests` 三条实际生成用例：磁盘不可用、输出上限、单名额 busy、上传暂停时用户取消能提交、调用取消及后续工作恢复。`Frozen_quotes_produce_exact_utf8_crlf_csv_without_float_or_locale_conversion` 核对已知 Int64/decimal/空值字节。 |
| 4. Files 契约与真实 HTTPS 身份 | 架构不变量项目/程序集引用检查；`GeneratedFilesHttpsTests` 的私有根、中间链、生产者映射、普通 JWT/伪造头/错根/未授权叶/过期或错误用途、生产撤销策略、内部 OpenAPI、明文伪造头拒绝旅程。新回复屏障两侧均为真实 HTTPS。 |
| 5. 原候选、实际字节/摘要、封存不可下载、暂存未知恢复 | `Concurrent_registration_and_complete_seal_replays_keep_one_file_and_first_receipts`、`Corrupt_complete_content_is_rejected_without_sealing_then_same_upload_can_recover`、`Interrupted_stream_preserves_pending_identity_and_releases_shared_upload_slot_for_retry`、`Process_death_during_seal_commit_recovers_the_same_upload_without_losing_committed_bytes`。生成退出旅程观察真实 Staging 收据、本人下载 404，重启仍为原 FileId / UploadId。 |
| 6. 唯一发布、取消竞争、旧执行者不能替换 | `Concurrent_cancel_and_publication_selection_have_one_durable_winner`；`Current_worker_selects_one_persistent_publication_and_cancel_then_conflicts_without_replacing_snapshot`；真网关在 Files 已发布而 Pricing 仍 Publishing 时取消 409。生成退出旅程对旧代次真实上传回放，旧选择不能替换当前成果，公开状态和字节不变。 |
| 7. 双提交未知、有限恢复、不重新冻结 | 两条 `PricingExportCommitRecoveryTests.Server_committed_*_with_lost_wire_reply*`；完整网关旅程在 Files HTTPS 发布成功后截住回复并杀死 Pricing，同地址重启后保持原 FileId / PublicationId / 摘要 / 内容。`Stopped_publication_is_retried_by_owner_with_the_original_intent_and_no_new_generation` 与 `PricingExportRecoveryTests` 验证有限预算后的本人恢复和原操作关联。 |
| 8. 有界本人列表、当前授权、撤权不取消已接纳工作 | `Owner_list_uses_immutable_acceptance_cursor_despite_new_exports_and_rejects_foreign_or_changed_filters`、精确日期边界、完整网关旅程。旧会话在字节、元数据、成果和删除上拒绝；根与同权限用户也不能越过归属；服务身份继续执行。 |
| 9. 原期限、当前可用性与历史分开、迟到终态收据 | `Expiry_blocks_download_before_cleanup_and_recovery_preserves_expired_publication_history`；`Late_original_publication_receipt_after_owner_deletion_records_success_history_without_reviving_bytes`、`Late_original_publication_receipt_after_expiry_records_success_history_without_extending_lifetime`。关闭清理仍先阻止下载；迟到回执补记原历史，artifact 为 Deleted/Expired，无下载路径、无续长。 |
| 10. 墓碑、防复活、旧普通文件兼容、独立文件事务 | `Published_receipt_survives_restart_and_deleted_content_cannot_be_revived_by_replay`、`Expired_unpublished_candidate_keeps_private_tombstone_and_cannot_win_publication`、Files 持久化迁移/孤儿回收旅程；两条迟到收据与真网关删除回放。旧 RequestId 重放原工作，终态不能恢复/再领取，新 RequestId 才新建。没有跨库原子提交或 Publishing 后撤销接口。 |
| 11. 完整故障矩阵 | 上述各条及 `Publication_that_wins_before_stage_deadline_cannot_be_expired_by_a_stale_cleanup_scan`、`Unread_publication_response_recovers_after_restart_and_storage_outage_keeps_original_receipt`、暂存/普通上传 COMMIT 崩溃恢复。端口装饰器丢回复与本轮真实协议/HTTPS/进程中断分别记录，不用成功直连样板替代故障。 |
| 12. 全旅程、回归、迁移/模板、交付资格 | 普通用户网关旅程串起接纳→进度→已发布/未确认→中断→重启→原内容→他人/旧会话拒绝→删除→清理→原请求回放；期限与迟到回执由独立真实用例验证。Pricing 空迁移降级/重建、宿主迁移 CLI、重命名模板继续验证；Costing / Pricing 批次/取消/续租/缓存、Files 未知提交/孤儿回收回归保留。build→相关 tests→format、凭据/票据、完整 Linux CI、合并后 dev CI 都是交付门禁。 |

## 故障屏障的实际含义

`PostgresCommitReplyFault` 只转发本例数据库的回环连接，不记录认证或数据包。解析到 PostgreSQL `CommandComplete("COMMIT")` 后才断开，应用没有收到回复；不把一次连接异常推测为已提交。依据 [PostgreSQL 消息格式](https://www.postgresql.org/docs/current/protocol-message-formats.html#PROTOCOL-MESSAGE-FORMATS-COMMANDCOMPLETE)。协议夹具仅在测试连接关闭 SSL，不改产品或配置中心。

`GeneratedFileReplyFault` 以临时私有根和精确映射叶证书运行回环 HTTPS；Files 仍执行实际生产者许可。BeforeContent 在注册完成后阻断上传，AfterPublication 在 Files HTTPS 成功回复后阻断返回；未到达屏障即失败。只杀死本例持有的 Pricing 进程，重启原端口/数据库，不重置业务状态。

资源用例仅观察本例拥有的临时根，不改全局 TEMP，不清理其他宿主。Unix 的 .NET 10 `DeleteOnClose` 在托管释放中删除文件，[运行时源码](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Unix.cs) 说明本机 Windows 通过不能证明 Linux 强制退出清理。产品在写字节前 unlink，Linux 真实进程退出用例才是行为资格。

## 资源与容量边界

每例数据库、端口、TLS 根/叶、网关路由文件、文件根和子进程由本例持有。准备/迁移/复制/删除仍持重操作许可。新三类保留未声明类的独占回退，没有扩大本机并发或改 CI 隔离。原 `PricingExportJourneyTests` 的扩展继续使用独立资源及有界等待。

现有界限是请求 256 KiB、快照 5000 行、产物 32 MiB、每宿主一个生成名额和有限租约/重试。没有全局待办或永久历史配额，不声称总数据库容量有界；准入与保留策略需保持历史身份防复活，另轮设计。多机高可用仍在后续部署票中，整体 NS/PoS 目标继续暂停。
