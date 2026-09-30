# 70 — MattSkills 符合性收口：spec 七节、标准单源、format 门禁、词表收紧、变体声明

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: —

## 目标

把"完全符合 MattSkills"从一句话变成**可复验的事实**：修完剩下的五类差距，
把每一处差异**分类**（技能允许的仓库变体 / 违规），并给出能重新跑一遍的复验入口。

## 做了什么

1. **`spec.md` 补齐 `to-spec` 的七节**：Problem Statement / Solution / User Stories /
   Implementation Decisions / Testing Decisions / Out of Scope / Further Notes。
   20 条用户故事统一为 `As an <actor>, I want a <feature>, so that <benefit>`（编号 1..20 连续）。
   **原文（`## 1. 目标` 起）逐字留在附录**——它记录的是当时的推理与实测，不是草稿。
2. **`docs/agents/coding-standards.md`（新建）**：`code-review` 要找的成文 standards 源。
   它本身不含规则，而是指向三个单一事实源（`AGENTS.md` 不变量、`Directory.Build.props`、
   `.editorconfig`），并区分**硬违规**与**判断项**、列出 12 条 baseline 坏味道、
   说明本仓已用成文规则取代其中两条。`AGENTS.md` 加了指针（写规范 R3：讲"是什么 + 何时去读"）。
3. **`scripts/check-format.ps1`（新建）**：`resolving-merge-conflicts` 的三段
   typecheck → tests → format 此前只有两段。**第一次跑就抓到 448 处违规**
   （363 处行尾标记、69 处空白、10 处 `using` 顺序、6 处缺行尾换行），分布在 24 个文件里，
   而**构建全绿**；其中两个测试文件是 CRLF，而 `.editorconfig` 写的是 LF。
   已用 `dotnet format` 修平并验证（`--verify-no-changes` 退出码 0），CI 加了第三个 step。
4. **5 份 `CONTEXT.md` 收紧为纯词表**：移出端口、缓存、存储故障史等实现细节（6 处）。
5. **ADR 状态形状按 `ADR-FORMAT` 统一**：`ADR-FORMAT` 把状态定义成**可选 frontmatter**
   （取值封闭：`proposed | accepted | deprecated | superseded by ADR-NNNN`），而本仓一处用引用块、
   一处用 `## Status` 节。两处都改成文件头 `status:`（0002 → `superseded by ADR-0013`，
   0005 → `accepted`），解释性散文留在正文——**机器要读的那个词在文件头上**。
6. **跟踪器变体变成机器可见的声明**：`check-tracker.ps1` 新增 **§19**（`spec.md` 七节 +
   用户故事形式）、**§20**（变体必须被声明）、**§21**（`AGENTS.md` 的指针必须指向存在的文件）、
   **§22**（ADR `status` 取值封闭集），四组都做了变异验证。
7. **产出逐技能矩阵**：`review/23-mattskills-conformance-matrix.md`——
   25 个技能逐条给出"可判定要求 / 仓库落点 / 谁守着 / 判定"，并把差异**分类说明**。
   （最初编成 `05`，**撞了已有的 `05-deep-modules.md`**；改成 `23` 之后补了 `check-tracker` §23
   守着"编号唯一且连续"。**我违反了自己写下的规则，而当时没有任何检查会说话。**）

## 证据

```
pwsh -File scripts/check-tracker.ps1     → 干净（69 张票据、5 个上下文、6 个 ADR 目录）；退出码 0
pwsh -File scripts/check-format.ps1      → 格式干净：0 处违规；退出码 0
pwsh -File scripts/run-tests.ps1         → 21 个工程全部通过，约 99 秒，**524 条测试，0 跳过**
dotnet build NexusStackNext.slnx         → 0 错误（1 个已知 ASPIRE010 警告）
```

**变异验证（本轮 5 次，每次先确认能解析/能编译）**：

| 变异 | 结果 |
|---|---|
| `## Testing Decisions` 改名 + 一条故事去掉 `so that` | §19 报 **2 个问题**，逐条点名 |
| `### 三、面板能读到什么` 改名 | §20 报"变体没被声明" |
| `docs/agents/coding-standards.md` 改名 | §21 报"指着空气的指针比没有指针更坏" |
| ADR-0005 的 `status` 写成散文 | §22 报"不在封闭集里"并给出四个合法取值 |
| 摘掉三个聚合的空操作守卫（上一轮，同源） | 三条测试各自变红 |

**期间修掉的三个自身缺陷**（都是"我以为"与"事实"的差距）：
`dotnet format` 之前全仓不干净（448 处）；§20 的第一版锚点文本是我**凭记忆**写的
（`## 面板能读到什么`），实际标题是 `### 三、面板能读到什么`——**检查第一次跑就抓到了它**；
ADR 状态我先改成了 `## Status` 节，读了 `ADR-FORMAT` 才知道它要的是 frontmatter——
**"我记得的规范"不等于规范**。

## 仍存的差异（分类，不藏）

| 差异 | 分类 | 代价 |
|---|---|---|
| `to-tickets` 的评论线程用 `## Answer` / `## 第 N 轮：…`，不挂 `## Comments` | **技能允许的仓库变体**（已声明、§20 守着） | 36 张票的历史在面板里不显示 |
| `triage` 的 `needs-info` 在 markdown 后端没有对应状态 | **技能与后端的语义落差**（写进 `issue-tracker.md` §三） | 带该标签的票在面板显示为 open/frontier |
| `wizard` 未生成交互式 bash 向导（用 `run-host.ps1 -Init` 骨架代替） | **平台性变体**（目标机器是 Windows） | 三件"只有人能做的步骤"靠 README 说明 |
| `improve-codebase-architecture` 的报告是 Markdown 不是 HTML | **技能允许的变体**（与 `research` 落点统一） | 无（Markdown 能进版本库、能 diff） |
| `handoff` 用常驻跟踪器代替一次性交接文档 | **技能允许的变体** | 没有"本次会话"的快照 |
| 4 个对话类技能（`ask-matt` / `grilling` / `grill-me` / `teach` / `wait-what`） | **仓库侧无从判定** | 任何"符合"的说法在仓库里都不可证伪 |

**未处理的违规：0。**

## Comments

### local

**第 1 轮：为什么这一票的产物是一张表，而不是一段话**

"符合规范"如果没有**逐条判据 + 落点 + 守护者**，它读起来像结论，而实际上只是印象。
这张表把每一条都落到一个能被重新执行的东西上；而**表里那 6 处差异是它的价值所在**——
一份全是 ✅ 的矩阵多半是因为判据取得太松，或者差异没有被看见。
