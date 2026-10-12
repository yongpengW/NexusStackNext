# NS / PoS 与 NSN：暂缓项之外的能力差距复核

研究日期：2026-10-12。源码基线：NS `81831672aefc1f84db3ee5596a445df88ad26bdb`；PoS `bc94c69a6e08192e505929feb8e3fd54b125eb8a`；NSN `2e20b75d38799b6eb611acffe7dc41040a6a96c9`。

本轮只读三个仓库的源代码、领域文档及测试断言，没有读取私有配置值、访问旧项目数据库、启动服务或运行测试。下文的“已有”指可定位的产品入口与实现；读过测试断言不等于本轮重新验收通过。用户已暂缓 OSS、独立实时中心及多机高可用；短信待供应商，这些不计入下一轮建议。公司商品、账期、税费、SMT 与 MasterPricing 公式也不作为模板迁移义务。

## 建议顺序

优先补 **Identity 管理闭环**，随后补 **邮件业务使用闭环**；登录验证码由用户于 2026-10-12 明确暂缓，保留现有失败锁定与限流。无需为了“能力齐全”增加万能任务、报表或外部集成框架。

| 优先级 | 待补切片 | 已有基础 | 尚缺公开使用过程 |
|---|---|---|---|
| 1 | 角色目录与生命周期 | 创建、按 ID 读取、角色菜单整体替换、用户角色授予/撤回 | 角色列表/选择、改名、启停与删除 |
| 2 | 本人菜单与 API 资源管理 | 管理菜单树、菜单编辑/移动/删除、API 资源创建 | 按当前用户授权返回菜单、资源列表与绑定变更 |
| 3 | 邮件业务贯通与可发现状态 | 持久 SMTP、HTML/文本、附件、确定结果/Unknown 人工处置 | 一个真实业务消费者、本人邮件列表、按业务身份关联结果 |
| 待策略确认 | 登录验证码 | 登录调用校验端口、登录失败锁定、边缘限流 | 当前默认校验恒成功，没有生成与实际验证适配器 |

前两行宜拆成小票据，避免再建一个含全部 Identity 管理的总实现票。角色启停/删除如何处理已有用户引用和系统角色，资源重新绑定如何影响当前权限，都须在所属票据明确；不能直接复制旧系统的跨聚合批量修改。

2026-10-12 已发布独立票据，以下是实施顺序，完成状态以 GitHub 原生记录为准：

1. [角色目录与条件改名 #202](https://github.com/yongpengW/NexusStackNext/issues/202)。
2. [角色启停与当前权限收敛 #203](https://github.com/yongpengW/NexusStackNext/issues/203)，依赖角色目录。
3. [角色条件删除与引用保留 #204](https://github.com/yongpengW/NexusStackNext/issues/204)，依赖角色启停。
4. [API资源目录与条件绑定维护 #205](https://github.com/yongpengW/NexusStackNext/issues/205)。
5. [当前用户授权菜单导航 #206](https://github.com/yongpengW/NexusStackNext/issues/206)，依赖角色启停。
6. [本人邮件发送记录列表 #207](https://github.com/yongpengW/NexusStackNext/issues/207)。
7. [定价完成到持久邮件的业务链路 #208](https://github.com/yongpengW/NexusStackNext/issues/208)，首轮正文通知，不扩展公司报表规则。
8. [登录验证码策略与可选实现 #209](https://github.com/yongpengW/NexusStackNext/issues/209)，用户明确暂缓，不阻塞其他能力。

任务挂在总规格 #34 下，总规格仍属于唯一地图 #4。优先级不产生额外阻塞边，后端仍为 GitHub Issues，此处不维护第二份状态表。

## 1. Identity 角色管理：领域方法存在不等于用户可用

NS 的 [RoleController](D:/LeoProject/NexusStack/NexusStackBackend/Host/NexusStack.WebAPI/Controllers/RoleController.cs) 有真实 HTTP 入口：`GetListAsync`（37 行）、`GetRoleSelectorListAsync`（75 行）、`PutAsync`（124 行）、`EnableAsync`（170 行）、`DisableAsync`（190 行）及 `DeleteAsync`（221 行）。列表能按启用状态筛选，选择器仅列启用角色。PoS 的 [RoleController](D:/CWChina/CWChinaERP/CWChinaPoS/Host/POS.WebAPI/Controllers/RoleController.cs) 同样实现列表（31 行）、编辑（118 行）、启用（148 行）、禁用（168 行）及删除（199 行）。这是控制器可调用行为，非只有通用 CRUD 类型定义。

本轮也核对了真实前端消费者：[role.jsx](D:/CWChina/CWChinaERP/cwchina-midERPPoS-web/src/pages/systemManagement/role.jsx) 在 39 / 81 / 96 行调用角色列表、修改、删除；[user.jsx](D:/CWChina/CWChinaERP/cwchina-midERPPoS-web/src/pages/systemManagement/user.jsx) 在 50 行调用角色选择器。因此补目录管理并非为了让 CRUD 方法对称，而是保留已使用的管理过程。

NSN 的 [IdentityModule](../../src/Services/Identity/NexusStackNext.Identity.Endpoints/IdentityModule.cs) 当前角色区只映射：

- `GET /roles/{roleId}`：`GetRoleQuery`。
- `POST /roles`：`CreateRoleCommand`。
- `PUT /roles/{roleId}/menus`：`ReplaceRoleMenusCommand`。
- `POST /roles/{roleId}/menus/{menuId}`：`GrantMenuToRoleCommand`。

用户角色授予和按版本撤回也已存在，**不得将权限替换或撤权再次列为缺失**。但在本轮该基线下，未找到角色列表、改名、启停、删除的 HTTP 入口或应用命令。[Role](../../src/Services/Identity/NexusStackNext.Identity.Domain/Roles/Role.cs) 已定义 `Rename`、`ChangePlatforms` 和 `EnsureDeletable`，定向检索 Identity 产品源码只找到其定义，没有用例调用者；该聚合也没有启用状态。已有领域测试不能证明管理员能完成这些操作。

最小下一票可先交付角色列表/筛选与条件改名，沿已确认的 HTTP / ISender 边界验证；生命周期可独立一票。权限判定应继续使用当前已提交视图，修改必须保留角色版本、系统角色约束、所属事实/事务和权限失效语义。依据：[Identity 词表](../../src/Services/Identity/CONTEXT.md)、[当前操作权限 ADR-0028](../adr/0028-current-operation-authority.md)。

## 2. 菜单导航与 API 资源：管理视图不能代替本人导航

NS [MenuController.GetUserTreeAsync](D:/LeoProject/NexusStack/NexusStackBackend/Host/NexusStack.WebAPI/Controllers/MenuController.cs)（43 行）将 `CurrentUser` 交给 `permissionService.GetUserMenuTreeListAsync`；这是按当前用户授权生成菜单的实际入口。该控制器还提供 `GetResourcesAsync`（133 行）读取资源及勾选关系，以及 `BindResourceAsync`（169 行）修改绑定；后者会核验菜单和资源身份。

PoS 前端 [authContext.jsx](D:/CWChina/CWChinaERP/cwchina-midERPPoS-web/src/contexts/authContext.jsx) 的 96 行调用 `GetUserMenu`，[user-service.js](D:/CWChina/CWChinaERP/cwchina-midERPPoS-web/src/services/user-service.js) 的实现请求 `api/pos/Menu/usertree/2`。这是本人导航接口的真实消费者，不能以管理员有全树查询代替。

NSN `GET /api/identity/menus` 使用 [GetMenusHandler.HandleAsync](../../src/Services/Identity/NexusStackNext.Identity.Application/IdentityUseCases.cs)（464 行起）。它读取菜单树全部节点并排序、投影，不接收当前主体或角色许可；端点要求菜单管理查询许可。因此它是管理树，不能当作普通登录用户自己的导航树。菜单创建、标题/排序编辑、子树移动、叶节点删除均已有公开入口，本轮没有将这些已完成能力重复建议。

NSN API 资源的公开映射在 [IdentityModule](../../src/Services/Identity/NexusStackNext.Identity.Endpoints/IdentityModule.cs) 中只有 `POST /api-resources`；[ApiResource.MenuId](../../src/Services/Identity/NexusStackNext.Identity.Domain/ApiResources/ApiResource.cs) 为只读属性，创建时确定，本轮未找到公开列表、重新绑定、解绑或删除用例。这意味着创建了资源后，管理员缺少不依赖数据库的资源发现与纠错路径。

建议分开做本人菜单读取与资源目录维护。资源绑定的变化归 ApiResource 自身事务；不要照搬旧实现一次修改多个聚合的绑定集合。是否需要平台筛选、隐藏节点与祖先补齐，由第一条导航旅程确定，不凭旧实体字段补齐所有可能的菜单配置。

## 3. SMTP 已可发送，但业务尚未接入

PoS 的 [EmailNotificationController](D:/CWChina/CWChinaERP/CWChinaPoS/Host/POS.WebAPI/Controllers/EmailNotificationController.cs) 提供配置读取/更新、收件选项、`ManualSendAsync`（51 行起）及 `GetExecutionListAsync`（65 行起）。这些调用实际服务，并在控制器检查邮件操作权限。

[EmailNotificationExecutionService.CreateManualAsync / CreateScheduledAsync](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/EmailNotifications/EmailNotificationExecutionService.cs)（95 / 205 行起）准备报表、冻结收件人、保存执行、登记异步任务；[EmailNotificationDispatcherSchedule.ProcessAsync](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Schedule/EmailNotificationDispatcherSchedule.cs) 读取到期配置并逐项创建发送执行。随后 [EmailNotificationSendEventHandler.HandleAsync](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/EventHandler/EmailNotificationSendEventHandler.cs) 生成报表与附件，在 163 行起实际调用 `SMTPEmailService.SendEmailAsync`。这是“业务请求/计划 → 持久任务 → 报表 → 邮件 → 执行查询”的完整消费者证据。

NSN [SubmitEmail](../../src/Services/Notifications/NexusStackNext.Notifications.Application/EmailRequests.cs) **已经支持附件、文本与 HTML**；[EmailDeliveryRunner](../../src/Services/Notifications/NexusStackNext.Notifications.Infrastructure/EmailDeliveryRunner.cs) 实际加载固定 MIME、调用 MailKit SMTP，并区分 Accepted / Failed / Unknown。[SmtpEmailJourneyTests](../../tests/HostIntegration.Tests/SmtpEmailJourneyTests.cs) 的断言读取收到的 MIME，核对两个收件人、文本/HTML 与附件原字节；HTTP/重启测试也断言断线后 Unknown 不自动重发。因此不能再把“SMTP 不支持附件”或“没有发送实现”作为缺口。

剩余差距是使用闭环：[NotificationsModule](../../src/Services/Notifications/NexusStackNext.Notifications.Endpoints/NotificationsModule.cs) 只有提交、按 ID 状态读取和条件处置，没有本人列表；Notifications.Contracts 当前仅包含向审计交付的 `EmailDeliveryCommittedV1`。在 Costing、Pricing、Scheduling 产品 C# 中没有找到邮件/Notifications 调用者。

建议先做一个固定业务竖切，例如明确申请将 Pricing 报价成果发送至已确认地址：所属上下文登记可靠委托，通过 Notifications.Contracts 交付不可变邮件，用户能按本人业务身份补查状态。具体用业务完成通知正文还是报表附件，需按首个真实需求选其一；不要默认将私有文件链接当成无需认证的公开地址，也不要让 Notifications 读取 Pricing / Files 的表。手工发送和计划触发可分票，不搬入 PoS 的角色收件人框架、账期规则、任意模板注册或每日摘要体系。Accepted 仍只表示 SMTP 接纳，不声称进入收件箱。[Notifications 边界](../../src/Services/Notifications/CONTEXT.md)。

## 4. 登录验证码：PoS 确实调用，NS 工具定义不能替代入口证据

PoS [TokenController.GetCaptchaAsync / PostAsync](D:/CWChina/CWChinaERP/CWChinaPoS/Host/POS.WebAPI/Controllers/TokenController.cs) 的 `GET captcha` 实际生成图片；密码登录在 65 行调用 `ValidateCaptchaAsync`，失败拒绝后才进入口令验证。其 [UserTokenService](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/Users/UserTokenService.cs) 使用缓存保存并消费验证码。NS 同名服务虽然定义生成与校验方法，本轮在 NS Host 中未找到调用，不能说两者都形成了同样链路。

NSN [LoginHandler](../../src/Services/Identity/NexusStackNext.Identity.Application/LoginUseCase.cs) 实际调用 `ICaptchaValidation.ValidateAsync`，但是 [IdentityUseCases](../../src/Services/Identity/NexusStackNext.Identity.Application/IdentityUseCases.cs) 默认注册 `NoCaptchaValidation`，它恒成功。公开 `LoginRequest` 也没有 challenge 标识，未见生成端点或真实适配器。故当前有可替换校验缝，尚无可用验证码能力。

已有失败锁定与限流不是验证码实现；但是否要求所有登录、达到某阈值才挑战或继续不启用，应由产品决定。若实施，沿实际 HTTP 登录旅程证明挑战获取、校验、期限和一次性消费；不把“生产必须验证码”的源码注释本身当成已经确认的新硬要求。[OWASP Authentication Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Authentication_Cheat_Sheet.html#captcha) 将验证码定位为附加措施，不能凭新增验证码替代已有登录限流与锁定。

## 不应据此继续扩大的范围

- **文件本人列表**：Files 没有普通上传文件列表，但 PoS DownloadController 的实际主用途是导出中心。NSN [PricingExportsModule](../../src/Services/Pricing/NexusStackNext.Pricing.Endpoints/PricingExportsModule.cs) 已有本人导出列表、筛选、状态、成果及恢复；[AuditExportsModule](../../src/Services/Auditing/NexusStackNext.Auditing.Endpoints/AuditExportsModule.cs) 也有本人调查导出列表。当前不足以推出必须再建集中跨上下文下载数据库或普通文件浏览器。
- **CSV / XLSX 后端导入**：NS 有 Excel 工具，而 PoS 已核验成本导入入口接收 JSON 模型。NSN [Costing 批次导入](../costing-batch-import.md) 保留逐行结论、重复规则、检查点与恢复；没有工作簿读取适配器不自动等于漏掉现有后端业务契约。只有确认人工文件入口后，再为相同用例添加薄解析适配器。
- **异步任务的开发便捷性**：NS [AsyncTaskService.CreateTaskAsync](D:/LeoProject/NexusStack/NexusStackBackend/Domain/NexusStack.Core/Services/AsyncTasks/AsyncTaskService.cs) 的确能以一次调用登记任务并发 MQ；PoS 有实际消费。NSN [任务管理](../business-task-management.md) 已交付所属上下文的延迟、列表、取消和续租，[ADR-0019](../adr/0019-context-owned-business-tasks.md) 明确禁止共享 Core/跨库登记。下一次真实业务接入时记录必须手写哪些重复步骤，再判断值得缩短的接口；目前没有证据支持万能任务注册框架。
- **外部调用日志**：PoS `ThirdPartyServiceLog` 有真实订单/申报调用者，但多数属于公司外部供应商集成。NSN 已有来源操作日志、业务事实、Webhook 和 SMTP 状态。没有首个相同外部协议消费者前，不先增加通用第三方日志上下文或把请求/响应全文写入审计。
- **Redis 操作 API 数量**：旧 NS 暴露 Hash / Set 等命令，PoS 用于用户/设备缓存及取货码。NSN 当前已有 Pricing / Costing 两个真实 Redis 查询缓存消费者，Identity 使用当前权威权限读取。命令封装数量不是能力验收标准，不因此复制公司设备授权或取货码领域。
- **企业身份接入**：PoS 当前存在 Authentik 用户映射/同步及 `POS.SSO.Validation`，不能写成旧项目没有 SSO 消费者；但 NSN 尚未确定企业身份提供方和接入需求，这一事实不足以要求本轮复制企业 SSO、组织或门店模型。

## 后续验证

本笔记是源码差距审计，尚未执行新增能力测试。选定一条实现票后，继续按 `typecheck → 受影响公开边界测试 → format`，合并前完整 Linux CI。优先通过管理员与普通用户的真实网关旅程验证“可发现 → 可维护 → 当前权限立即正确”，不能仅新增领域方法测试就宣称管理闭环已完成。
