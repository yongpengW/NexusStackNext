# Issue tracker: GitHub

Issues and specs for this repo live on **GitHub Issues** (`gh issue ...`). The map, the tickets,
their labels, their history and their blocking edges are all GitHub objects — nothing is mirrored
into the repo.

## Conventions

- **One ticket per issue.** The title is the ticket's name — reference tickets by name, never by bare number
  (`#42, #43` says nothing to someone without the context; the number stays inside the name).
- **Triage state is a label** (`docs/agents/triage-labels.md` has the five role strings; the colours come
  from `label-colors.json` and are applied to the real GitHub labels).
- **History and conclusions are native comments.** One comment per round, with the round's own heading inside.
- **The map is a single issue** labelled `wayfinder:map`; tickets are its **child issues** (native sub-issues).
- **Blocking is a native dependency** (`blocked_by`) — a ticket is unblocked when every blocker is closed.

## When a skill says "publish to the issue tracker"

```powershell
gh issue create --title '<name>' --body-file <file> --label <triage-label> --label wayfinder:task
```

Publish in **dependency order** — blockers first — so the edges can be wired as you go
(`gh api -X POST repos/{owner}/{repo}/issues/{number}/dependencies/blocked_by -f issue_id=<id>`).

**A caveat learned the hard way**: `sub_issues` and `dependencies` take the **parent's issue number**
in the path but a **numeric issue id** in the body. Passing an id in the path returns `404`,
which reads exactly like "this endpoint isn't available to my token" — it is not. **Check the parameter
before concluding anything about permissions.**

## When a skill says "fetch the relevant ticket"

`gh issue view <number> --comments`. The user will normally pass the number or the URL.

## Wayfinding operations

Used by `/wayfinder`.

- **Map**: the issue labelled `wayfinder:map` — its body is the destination / notes / decisions-so-far / fog.
- **Child ticket**: an issue labelled `wayfinder:task` (or `research` / `prototype` / `grilling`), attached as a
  sub-issue of the map. Open/closed is the ticket's state.
- **Blocking**: native dependencies. A ticket is unblocked when every blocker is closed.
- **Frontier**: open, unblocked child issues of the map — lowest number first.
- **Claim**: assign yourself (or comment) before any work starts.
- **Resolve**: comment the answer, close the issue, then add a context pointer (gist + link) to the map's
  Decisions-so-far body.

## 这一轮之前的历史（归档，**不是**跟踪器）

2026-09-30 之前的 72 张票、24 份评审、地图与 spec 都在 **`.scratch/nexusstack-next/`**（markdown）。
那个目录现在是**冻结的归档**：它记录这一轮怎么走过来的，**不再承载新票据**。

- **为什么不迁移**：全部已 resolved，且是长文书 + 41 条评论；灌成 72 个 issue 只会是噪音，
  而历史已经在 git 里。想要可搜索的历史，就单开一张票做脚本化迁移。
- **为什么它的检查还留着**：`scripts/check-tracker.ps1` 里那些"读 `.scratch/` 的票与地图"的组
  **没有退役**——它们现在的职责是**守住这份归档的自洽**（票据形状、阻塞图、地图索引、编号、评论锚点），
  而且一旦有人把后端切回 markdown，它们就是那天要用的检查。
  **在线票据的形状由 `scripts/check-issues.ps1` 守**（地图唯一、票据挂在地图下、阻塞边指向真实票据、标签与调色盘一致）。
- **两边的分工写在各自脚本的头部**，谁的后端谁检查。

决定与完整清单：`docs/adr/0016-issue-tracker-moves-to-github-issues.md`。
