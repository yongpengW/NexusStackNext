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

## 本仓的变体（**只剩一处，而且它是有意的**）

这个仓库按上面那套走，但有一处**不一样**——写在这里，因为"声明了却不照做"和"有意改过"从外面看是一样的
（第 30 轮 review 就是靠这句话发现别处的漂移的）。

### 一、一轮工作的正文写在评论里，用普通小标题

一张票常常**不只一轮**：第一次解决它、后来又发现新问题、再修一遍。

**写法**：历史与结论都放在 `## Comments` 下，每条是一个评论块，第一行定身份：

```
## Comments

### local

**第 17 轮：验收 1 的四个环境阻塞，逐个定位**

（正文里用 `**加粗**` 或 `####` 小节，**不要再用 `###`**）
```

- 身份行是 `### <名字> — <ISO 时间>`；本仓的 agent 名字就叫 `local`。
- deck 的解析器按 `^### ` **切块**，所以**评论块内部不要再出现 `### `**：
  它会被切成一条新评论，而"作者"会变成那行标题的第一个词。
  （2026-09-30 量过一次：全仓 `## Comments` 区段里 48 个块，**全是**身份行、0 个幽灵块——
  这条规则是为了让它继续这样。）
- 正文里的小标题按内容取（`产出` / 实跑结果 / 验收标准 / 当前状态），不必叫 `Answer`。

> **2026-09-30 改过一版。** 此前本仓用的是 `## 第 N 轮：<做了什么>` 与 `## Answer`。
> 那不是"另一种风格"，是**面板看不见的形式**——deck 的解析器只认 `## Comments` 这个锚点。
> 票据 72 把 33 张票的历史整批迁了进来：现在全仓 `## 第 N 轮` 与 `## Answer` **各 0 处**，
> `scripts/check-tracker.ps1` 第 20 组守着这件事。
> **"位置等价"不是等价**：位置决定了它读不读得到。

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
本仓的规矩是"只查能被机器判定的部分"——所以它是约定，由读的人守，与评论块的形状那条一样。

---

## 计划中的变更（**尚未实施**）

票据后端**将在下一个 effort 开始时切到 GitHub Issues**：决定、切换清单、哪些检查会失去对象、
以及"为什么不迁移这 72 张历史票"，见 `docs/adr/0016-issue-tracker-moves-to-github-issues.md`。

**现在这份文件仍然有效，上面那行标题也不要动**——`# Issue tracker: Local Markdown` 就是后端声明本身
（deck 的 `explicitDetector` 读它；标题命中是 high 置信、赢过自动探测，所以正文里提到 GitHub 不会误切）。
切换时才改标题，并按 ADR-0016 的清单一起把检查改掉。
