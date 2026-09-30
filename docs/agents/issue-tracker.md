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

这个仓库按上面那套走，但有两处**不一样**——写在这里，因为"声明了却不照做"和"有意改过"从外面看是一样的
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

### 三、`review/` 是这个 effort 自己的东西

`.scratch/<effort>/review/` 不在上面的规范里：它放**整体评审**（不是某一张票的答案）。
这是本仓加的，因为它反复出现"跨票据的检查"这一类工作。
