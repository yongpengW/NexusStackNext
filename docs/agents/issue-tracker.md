# Issue tracker: Local Markdown

Issues and specs for this repo live as markdown files in `.scratch/`.

## Conventions

- One feature per directory: `.scratch/<feature-slug>/`
- The spec is `.scratch/<feature-slug>/spec.md`
- Implementation issues are one file per ticket at `.scratch/<feature-slug>/issues/<NN>-<slug>.md`, numbered from `01` — never a single combined tickets file
- Triage state is recorded as a `Status:` line near the top of each issue file (see `triage-labels.md` for the role strings)
- Comments and conversation history append to the bottom of the file under a `## Comments` heading

## When a skill says "publish to the issue tracker"

Create a new file under `.scratch/<feature-slug>/` (creating the directory if needed).

## When a skill says "fetch the relevant ticket"

Read the file at the referenced path. The user will normally pass the path or the issue number directly.

## Wayfinding operations

Used by `/wayfinder`. The **map** is a file with one **child** file per ticket.

- **Map**: `.scratch/<effort>/map.md` — the Notes / Decisions-so-far / Fog body.
- **Child ticket**: `.scratch/<effort>/issues/NN-<slug>.md`, numbered from `01`, with the question in the body. A `Type:` line records the ticket type (`research`/`prototype`/`grilling`/`task`); a `Status:` line records `claimed`/`resolved`.
- **Blocking**: a `Blocked by: NN, NN` line near the top. A ticket is unblocked when every file it lists is `resolved`.
- **Frontier**: scan `.scratch/<effort>/issues/` for files that are open, unblocked, and unclaimed; first by number wins.
- **Claim**: set `Status: claimed` and save before any work.
- **Resolve**: append the answer under an `## Answer` heading, set `Status: resolved`, then append a context pointer (gist + link) to the map's Decisions-so-far in `map.md`.

## 本仓的变体（**两份不同，都是有意为之**）

这个仓库按上面那套走，但有几处**不一样**——写在这里，因为"声明了却不照做"和"有意改过"从外面看是一样的
（第 30 轮 review 就是靠这句话发现别处的漂移的）。

### 一、答案的标题不是 `## Answer`，而是那一轮的标题

一张票常常**不只一轮**：第一次解决它、后来又发现新问题、再修一遍。
单个 `## Answer` 装不下这个过程，所以本仓的写法是：

- 每解决一次，追加一个 `## 第 N 轮：<做了什么>` 的小节；
- 里面的标题按内容取（`产出` / `实跑结果` / `验收标准` / `当前状态`），而不是一律叫 Answer。

`## Answer` 仍然被接受（早期 23 张票用的是它），**两种都算**。
`scripts/check-tracker.ps1` 只强制 `Status:` / `Type:` / `Labels:` / `Blocked by:` 这四行，
不强制标题名——它检查的是**能被机器判定的部分**，标题形式留给写的人。

### 二、对话历史进 `## 第 N 轮：…` 而不是 `## Comments`

同一个理由：这里的"评论"其实是**一轮一轮的工作记录**（做了什么、证据是什么、还剩什么），
不是零散对话。所以它按轮次编号，落在文件底部——位置上与 `## Comments` 等价，形式上更清楚。

### 三、面板能读到什么（实测，不是推断）

上面两条是**给人读**的选择，代价是**面板读不到它们**。这一节把机器那一侧写准，
因为"我们有意这么写"和"面板静默看不见"从外面看也是一样的：

| 面板的行为 | 判据的位置 |
|---|---|
| 评论线程**只有一个锚点**：`## Comments`（大小写不敏感、需独占一行），段内每条评论是 `### <名字> — <ISO 时间>` | deck 的 markdown 后端 `parse.js` 的 `cmAnchor` |
| **`## Answer` 与 `## 第 N 轮：…` 都不被读**——前者只写不读，后者只是普通的小节边界 | 同上 |
| 票据字段必须是**行首的裸字段行**；`**Status:**` 这种粗体**匹配不到**（状态、依赖、认领会一起静默失效） | `parse.js` 的字段正则 `^\s*Status\s*[:\uFF1A]` |
| `Status:` 只有两档：`resolved`/`completed`/`closed`/`done` = 关闭，`claimed` = 已认领，**其余一律当成 open** | `parse.js` 的 `closedSet` |
| 地图正文只认五节，且名字要**逐字**：`## Destination` / `## Notes` / `## Decisions so far` / `## Not yet specified` / `## Out of scope`；`Decisions so far` 只收 `- [标题](链接) — 要点` 形式的行 | 面板的 `mapBody.js` / `parser.js` |
| 后端靠**本文件的首行**识别：`# Issue tracker: Local Markdown`。改掉这一行，面板就认不出这个仓库用的是哪种 tracker | deck 的 `explicitDetector.js` + `parseIssueTracker.js` |
| `docs/agents/label-colors.json` 由面板拥有；11 个键与它的内置调色盘一一对应，缺一个该标签就回灰 | deck 的 markdown 后端内置调色盘 |

这七条里有**六条已经进了 `scripts/check-tracker.ps1`**（第 11–17 项），并且都做过反向验证
（制造违规 → 精确变红 → 还原）。**没进检查的是"评论线程"那条**：它的修法要动 36 张票的标题层级，
是一个需要权衡的改动，不是一条能顺手加上的检查。

### 四、`review/` 是这个 effort 自己的东西

`.scratch/<effort>/review/` 不在上面的规范里：它放**整体评审**（不是某一张票的答案）。
这是本仓加的，因为它反复出现"跨票据的检查"这一类工作。

**它同时是 `research` 票据的落点。** `/research` 要求"产出**单个** Markdown、每条断言都引一手来源，
放在仓库本来放这类笔记的地方"——本仓那个地方就是这里（`review/01`–`04` 量参照仓库，逐条带 `file:line`）。
新开一份调研时放进 `review/`，文件名用 `NN-<slug>.md` 接在现有编号之后。

当前这一层有 **24 份**：`01`–`04` 是四轮通读参照仓库（逐条 `file:line`），`05`–`22` 是量本仓自己的各轮评审
（深模块尺度、覆盖率核对、不变量反向验证、真实 HTTP 旅程……），`23` 是 **MattSkills 逐技能符合性矩阵**
（25 个技能逐条给出"可判定要求 / 仓库落点 / 谁守着 / 判定"，并把差异分成"技能允许的仓库变体"与"违规"），
`24` 是**架构机会的 HTML 报告**（`improve-codebase-architecture` 要的可视化形状）。

**编号唯一且连续，由 `check-tracker.ps1` §23 守着。** 这条检查是**撞号撞出来的**：
符合性矩阵最初被编成 `05`，而 `05-deep-modules.md` 已经在那儿——同一个号两份文件，
按编号找东西的人必然找错，而当时没有任何东西会说话（规则只写在本文里，没人执行）。
**那条矩阵的产物是"符合规范"这句话的可复验形式**，改了规范相关的文件之后应当回去更新它。

### 五、引用票据用**名字**，不用裸编号

`wayfinder` 的规矩：在人读的一切地方（叙述、地图的 Decisions so far、注释、ADR），
引用一张票要**连着它的名字**——"票据 44 聚合并发令牌"，而不是"票据 44"。

理由不是文风：一串 `#42, #43, #44` 在不知道上下文的人眼里等于没有信息，
而名字一眼就能判断相不相关。编号不会消失——它待在名字里面。

这条**没有进检查**：写"票据 44"算不算带名字，机器判不准（"票据 44 的那条"里的"的"不是名字）。
本仓的规矩是"只查能被机器判定的部分"——所以它是约定，由读的人守，与 `## 第 N 轮` 那条一样。
