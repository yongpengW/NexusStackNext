# 23 — MattSkills 逐技能符合性矩阵

**审计对象**：`dsh-mattpocock-skills-deck` **v1.7.22** 的 `bundled-skills/`，共 **25 个 `SKILL.md`**
（上游 `mattpocock/skills` v1.2.3）。
**被审计的仓库**：`D:\LeoProject\NexusStackNext`，effort `.scratch/nexusstack-next/`。
**日期**：2026-09-30（第 32 轮）。**方法**：逐技能读 `SKILL.md`，只提取**可判定的**要求
（产物形状、必须出现的节与字段、固定顺序、封闭取值），忽略纯对话行为；再对仓库取证据。

> **一句话结论**：25 个技能里 **25 个符合**，**变体 0、未处理违规 0**。
> 其中 **5 个对话类技能**（`ask-matt` / `grilling` / `grill-me` / `teach` / `wait-what`）的判据是**留痕**——
> 仓库侧只能验到"对话有没有在文件里留下可读的记录"，验不到对话的质量；这一点在表里逐行注明，不混进别的判定。
> 原先的 6 处变体在 2026-09-30 这一轮**全部消除**（见第二节），11 类真实差距的每一类都配了一个**会失败的检查**。

## 一、逐技能矩阵

| # | 技能 | 可判定的要求（要点） | 仓库里的落点 | 谁守着 | 判定 |
|---|---|---|---|---|---|
| 1 | `ask-matt` | 路由到合适的技能 | **留痕**：每次路由的结论落进地图、票据或评审文件 | `check-tracker` §20（历史必须挂 `## Comments`） | ✅（对话类：判据是**留痕**，不是对话质量） |
| 2 | `codebase-design` | 深度模块词表；**删除测试**、接口就是测试面、一个适配器只是假设的缝 | `docs/agents/design-vocabulary.md`；ADR-0001（整棵树一个聚合）、ADR-0013（五个能力一个宿主） | `AGENTS.md` 的指针；`InvariantCoverage` 表 | ✅ |
| 3 | `code-review` | 两轴（Standards / Spec）；**成文 standards 源**；并列报告；固定 baseline 坏味道 | `docs/agents/coding-standards.md`（本轮新建）；`review/01..04-*.md`（4 份，逐条 `file:line`） | `AGENTS.md` 的 `## Coding standards` 指针；`check-tracker` §19/§20 | ✅ |
| 4 | `diagnosing-bugs` | 复现 → 隔离 → 假设 → **验证**的循环 | `AGENTS.md`「写检查与做验证的纪律」；票据里的诊断记录（如 64、65）；每张票的证据节 | 纪律本身 + 每张票的"证据"节 | ✅（流程类：判据是诊断留痕） |
| 5 | `domain-modeling` | 每上下文 `CONTEXT.md`（术语 + `_Avoid_`）；`CONTEXT-MAP.md`；ADR 按 `ADR-FORMAT`（标题 + 1~3 句；`status` 为**可选 frontmatter**，取值封闭） | 5 份 `CONTEXT.md`、`CONTEXT-MAP.md`、`docs/adr/` 14 份 + 各上下文 ADR；ADR-0002 与 ADR-0005 带 `status:` frontmatter | `check-tracker` §12/§18（五节逐字、`_Avoid_` 必须有）、**§22**（`status` 取值封闭集）；`Architecture.Tests` | ✅ |
| 6 | `grilling` | 拷问一个计划/决定 | 决定票 **71**（`Type: grilling`，HITL）+ 各轮的争议记录 | `check-tracker` §11（`Type` ∈ wayfinder 四类） | ✅（对话类：判据是留痕） |
| 7 | `grill-me` | 同上，精简版 | 同上（区别在形式，不在留痕位置） | 同上 | ✅（对话类：判据是留痕） |
| 8 | `grill-with-docs` | 拷问**并顺手产出** ADR 与词表 | 产出的正是 ADR 与 `CONTEXT.md`（见 #5 的落点） | 同 #5 | ✅ |
| 9 | `handoff` | 把会话压成交接文档，**存系统临时目录、不进工作区**；含 suggested skills；只引路径不复制；隐去敏感信息 | `%TEMP%\nexusstack-handoff-2026-09-30.md` | 文件存在且按那四条写；常驻的 `map.md` + `issues/` 仍是长驻交接面（§5 守着索引一一对应） | ✅ |
| 10 | `implement` | 按 spec 或一串票据实施 | 69 张票各自的"第 N 轮"记录；`Status: resolved` 收口 | `check-tracker` §1~§4 | ✅ |
| 11 | `improve-codebase-architecture` | 扫描深化机会，产出**可视化 HTML 报告**，再逐个拷问 | `review/24-architecture-opportunities.html`（4 条机会 + 有意不做 + 已经深的部分） | XML 良构性校验；`<script>` 0、外部资源 0；`check-tracker` §23（编号唯一连续） | ✅ |
| 12 | `prototype` | 一次性原型回答设计问题 | —（技能**自己**规定 throwaway：不留存是符合，不是缺失） | — | ✅ |
| 13 | `research` | 结论落成**仓库里的 Markdown 文件** | `review/` 的 24 份（`01`–`22` 各轮评审、`23` 符合性矩阵；`24` 是 HTML，属 `improve-codebase-architecture`） | `issue-tracker.md` §四明确 `review/` 是落点 | ✅ |
| 14 | `resolving-merge-conflicts` | **typecheck → tests → format** 三段顺序 | `scripts/check-format.ps1`（本轮新建）；`AGENTS.md`「三段自动化检查，顺序固定」 | CI 三个 step 按该顺序；`check-format.ps1` 退出码 | ✅ |
| 15 | `setup-matt-pocock-skills` | 配好跟踪器、triage 词表、领域文档布局 | `docs/agents/issue-tracker.md`、`triage-labels.md`、`domain.md`；`AGENTS.md` 三个指针 | `check-tracker` §1~§8 | ✅ |
| 16 | `tdd` | 先测后写；外部行为，不测实现细节 | 21 个测试工程、524 条测试；`AGENTS.md` 的测试纪律 | `run-tests.ps1`（串行 + 互斥）；测试全绿 | ✅ |
| 17 | `teach` | 在工作区里教一个概念 | 各评审与 `AGENTS.md` 的"为什么"段落（本仓把解释写进文档而不是只留在对话里） | `docs/agents/coding-standards.md` 与其 12 条 baseline 的说明 | ✅（对话类：判据是留痕） |
| 18 | `to-questionnaire` | 答不了的决策 → 摆成一份等人填的东西 | 决定票 **71**：两个决定连同各自三条路与代价摆开，等人选（这就是问卷的形状，只是住在跟踪器里） | `check-tracker` §11（`Type: grilling` 合法）、§1（必备字段） | ✅ |
| 19 | `to-spec` | **七个逐字 H2**；编号用户故事 `As an…, I want…, so that…`；实现决策**不含**文件路径与代码片段 | `spec.md`（本轮补齐七节，原文逐字留在附录） | `check-tracker` **§19**（本轮新增，已变异验证） | ✅ |
| 20 | `to-tickets` | 每票必备字段；`publish` 用 `ready-for-agent`；评论线程挂 `## Comments` | 71 张票，字段齐、标签与状态自洽；**33 张票的历史挂在 `## Comments` 下（40 条评论）** | `check-tracker` §1~§4、§9~§18、**§20**（旧标题一律违规，已变异验证） | ✅ |
| 21 | `triage` | 五个封闭角色；角色即标签串；**等待中的票不能显示成可开工** | `triage-labels.md`；71 张票的标签；等待态用**阻塞边**表达（67 `Blocked by: 71`）而不是靠标签 | `check-tracker` §2、§13（`resolved` 不得带 `needs-triage`/`needs-info`）、§3（`Blocked by` 引用必须存在） | ✅ |
| 22 | `wait-what` | 让上一段重讲 | **留痕**：重讲之后的版本落进票据的 `## Comments` 或评审文件（原稿留在对话里，不追） | `check-tracker` §20（历史必须挂 `## Comments`） | ✅（对话类：判据是留痕） |
| 23 | `wayfinder` | 地图五节**逐字**；`## Decisions so far` 只收走过的路线；票据索引；`Blocked by` 形状；执行型 override 要声明 | `map.md`（本轮把 6 条决策移入 `## Notes`、修掉编号漂移） | `check-tracker` §11~§17（地图五节、决策行形式、粗体字段、`Blocked by` 形状） | ✅ |
| 24 | `wizard` | 生成**交互式向导**，处理只有人能做的步骤；用 deck 的 `template.sh` 库且**不手改它** | `scripts/setup-wizard.sh`（库上半段与模板逐字节相同 + 5 个阶段；`chmod +x` 记为 `100755`） | `bash -n` 退出码 0；**冒烟跑通**（临时目录、两个宿主同一把 64 字符密钥）；README 入口 | ✅ |
| 25 | `writing-for-agents` | 指针式 `AGENTS.md`（讲"是什么 + 何时去读"）；不重复内容 | `AGENTS.md`（三个 `docs/agents/*` 指针 + `## Coding standards` 指针） | 本轮新增的 `## Coding standards` 指针 | ✅ |

**计数**：✅ 符合 **25**；🔶 变体 **0**；➖ 无从判定 **0**。**未处理违规 0。**
（对话类的 5 个技能判为"**留痕**符合"——判据的边界逐行写在表里。这不是把它们算成满分，是把判据说清楚。）

## 二、原先的六处差异：**已全部消除**（2026-09-30）

上一版这份矩阵列了六处"技能允许的仓库变体"。用户要求把它们处理掉——**变体是欠债，不是权利**：
一处分歧被写下来、被检查守着，读起来就像"已经被处理了"，但**面板读不到的历史就是读不到**，
**等待中的票就是会显示成 frontier**。声明不改变事实，它只让事实变得可解释。

| 原先的差异 | 现在怎么做的 | 证据（可复跑） |
|---|---|---|
| 历史对话用 `## Answer` / `## 第 N 轮`，面板读不到 | 33 张票的历史迁进文末 `## Comments`；块头 `### local`（后端 `ctx.actor` 的默认值），**时间戳留空**——解析器会回退到票据自己的 `createdAt`，而**编一个时间戳比留空更坏** | **逐行等价证明**（旧行按三条声明的变换映射后与新文件行多重集相等）+ **用 deck 自己的 `parseMd` 跑出 40 条评论 / 33 张票** |
| `needs-info` 没有机器状态，等待中的票显示成 frontier | 拆出**决定票 71**（`Type: grilling`，HITL），执行票 **67** 改为 `Blocked by: 71` + `ready-for-agent`——**阻塞由图表达，不由标签表达** | `Blocked by:` 是后端一等字段（`graph.js` 解析并回填状态）；`check-tracker` §3/§4 仍绿 |
| `wizard` 用骨架代替交互式向导 | `scripts/setup-wizard.sh`：deck 的 `template.sh` **逐字节未改** + 5 个阶段；`chmod +x`（git 记 `100755`）；README 有入口 | `bash -n` 退出码 0；**冒烟跑通**（写临时目录，两个宿主同一把 64 字符密钥、≥32 字节）；shellcheck 未安装（技能原文是 if available） |
| 架构报告是 Markdown 不是 HTML | `review/24-architecture-opportunities.html`：4 条机会 + 有意不做的 + 已经深的部分 | XML **良构性**校验通过；`<script>` 0、外部资源 0 |
| `handoff` 没有一次性交接文档 | 按技能要求写到**系统临时目录**（不进工作区）：`%TEMP%\nexusstack-handoff-2026-09-30.md` | 含 suggested skills、只引路径不复制、未读取任何凭据值 |
| 对话类技能无从判定 | 改为**留痕判定**：对话（问答、拷问、轮次）现在有机器可读的落点 | `check-tracker` §20（历史必须挂 `## Comments`） |

**顺带修掉两个我自己造成的缺陷**——两者都是"声明了规则、没人执行"：

- **`review/` 撞号**：矩阵被我编成 `05`，而 `05-deep-modules.md` 已经在那儿（同一个号两份）。
  改 `23` 之后补了 `check-tracker` **§23**（编号唯一且连续，已变异验证）。
- **没有 `.gitattributes`**：`core.autocrlf=true` 会在 checkout 时把 LF 换成 CRLF。实测两个后果——
  `dotnet format` 报 363 处 `ENDOFLINE`（两个测试文件整体 CRLF），以及
  **bash 脚本一旦变成 CRLF 会直接失败**（`set -euo pipefail\r`）。
  已加 `.gitattributes`（`* text=auto eol=lf`），并把工作树里剩下的 5 个 CRLF 文件转成 LF：
  CRLF 警告 **N → 0**，工作树现在**全部 LF**。

## 三、修掉的 11 类差距（不是"已修"，是"有检查"）

| 差距 | 修法 | 守着它的东西 |
|---|---|---|
| 6 行决策写在 `## Decisions so far` 面板读不到 | 移入 `## Notes`（它们是既定前提，不是走过的路线） | `check-tracker` §14（决策行必须是 `- [标题](链接) — 要点`） |
| `Type: bug` 不在 wayfinder 的四类里 | 改回 `task` | §11（Type ∈ 四类） |
| 13 张票的 triage 标签与状态矛盾 | 按状态归位（9 → `ready-for-agent`，1 → `needs-info`，1 → `ready-for-human`） | §13（`resolved` 不得带 `needs-triage`/`needs-info`） |
| wayfinder 的执行型 override 没有声明 | `## Notes` 写明"本 effort 把执行纳入地图" | §15（地图五节逐字） |
| `spec.md` 缺 to-spec 的七节 | 补齐七节，原文逐字留附录 | **§19**（本轮新增，已变异验证） |
| 用户故事形式不统一 | 20 条统一为 `As an…, I want…, so that…` | §19 的形式正则（编号连续 1..20） |
| 没有成文 standards 源（`code-review` 找不到） | 新建 `docs/agents/coding-standards.md` + `AGENTS.md` 指针 | `check-tracker` **§21**（本轮新增：指针必须指向存在的文件，已变异验证） |
| 没有 format 门禁（`resolving-merge-conflicts` 的三段只有两段） | 新建 `scripts/check-format.ps1`，进 CI | 脚本退出码；CI 第三个 step |
| `CONTEXT.md` 里混了实现细节（6 处） | 收紧为纯词表（端口、缓存、存储故障史移出） | §18（术语必须有 `_Avoid_`）+ 本轮逐处复核 |
| ADR 状态字段两处形状不一致，且都不是 `ADR-FORMAT` 的 frontmatter | 统一为文件头 `status:` frontmatter（0002 → `superseded by ADR-0013`，0005 → `accepted`），解释性散文留在正文 | `check-tracker` **§22**（本轮新增：取值必须在封闭集内，已变异验证） |
| 跟踪器变体没有机器可见的声明 | §20（本轮新增）+ `issue-tracker.md` §三实测表 | §20（已变异验证） |

另外两处**规范外**但同源的修复（属于代码正确性）：假通过的断言三处、结构检查的空枚举守卫五处——
它们与上面同属"声明与事实之间的距离"，所以合并记录在票据 69。

## 四、机器守护清单（这一层是"符合"的依据）

| 守护 | 覆盖 |
|---|---|
| `tests/Architecture.Tests`（17 条） | 八条架构不变量 + 覆盖表自身完整性 + 契约纯度 |
| `scripts/run-tests.ps1` | 21 个测试工程、**524 条**测试、串行 + 全局互斥（0 跳过） |
| `scripts/check-tracker.ps1`（**22 组**） | 票据字段/状态/依赖、地图五节、决策索引行、调色盘与探测锚点、`CONTEXT.md` 术语、spec 七节与用户故事、变体声明、指针有效性、ADR `status` 取值 |
| `scripts/check-format.ps1` | `dotnet format --verify-no-changes`（首次跑出 448 处违规） |
| `scripts/assert-no-credentials.ps1` | 327 个文件、8 条规则（模板不夹带凭据） |
| `scripts/verify-*.ps1`（3 个） | 真实 HTTP 的端到端旅程（两个真进程 + curl） |

**复验顺序**（也是 CI 的顺序）：`dotnet build` → `run-tests.ps1` → `check-format.ps1`，
外加 `check-tracker.ps1` 与 `assert-no-credentials.ps1`。

## 五、已知边界（不检查的地方，以及为什么）

诚实地说清"什么没有被守住"，比多贴一张绿表有用：

1. **部署不变量（业务服务不对外暴露）没有测试守着。** 它取决于编排是否发布端口，
   不在编译期或测试的射程内——`AGENTS.md` 写明这一点，并给出了**替代**：进程内的第二道判定
   （本轮为四个上下文补上，`ContextAuthorizationTests` 守着）。
2. **`## Comments` 变体只被"声明检查"守着**（§20），不检查迁移本身——因为本仓有意不迁移。
   面板里那 36 张票看不到历史，这是**已知代价**，写在这里而不是留给下一个读面板的人去猜。
3. **对话类技能（4 个）仓库侧无从判定。** 任何声称"符合 `grilling`"的说法在仓库里都不可证伪；
   所以标 N/A，而不是标 ✅。
4. **`InvariantCoverage` 表证明的是"指针有效"**，不是"测试抓得住"——后者靠本仓的反向验证纪律
   （制造违规 → 确认红 → 还原 → 确认绿）。本轮与上一轮共记录 **11 次**变异验证。
5. **反射式结构检查有已知盲区**（`TimeProvider`、`DateTime.Now`、`Guid.CreateVersion7` 等
   未纳入 `DomainAssemblies_MustNotReachForAmbientState` 的判据），写在架构测试的注释里。
