---
status: accepted
---

# 票据后端下一轮切到 GitHub Issues（本文件同时是切换清单，尚未实施）

## 现状与触发条件

本仓的票据一直是**本地 markdown**：`.scratch/<feature-slug>/issues/NN-<slug>.md`，
分类写在文件里的 `Status:` 行，历史对话追加在 `## Comments` 下，地图是 `map.md`。
后端由 `docs/agents/issue-tracker.md` 的**首行标题**声明（deck 的 `explicitDetector` 读它，
`/^#\s*issue\s*tracker\s*:\s*github/im` 命中即 high 置信采纳，赢过自动探测）。

触发这次决定的是**接入条件刚刚具备**：`gh` 已安装并登录（账号 `yongpengW`，scopes `repo`/`workflow`），
仓库可达——这正是 deck 的 `github/preflight.js` 按顺序查的三件事。而 deck 的 GitHub 后端是一等后端
（issues / comments / labels / label-colors / **graph** / pulls），不是降级替代。

## 决定

**票据后端切到 GitHub Issues；切换时机是下一个 effort 开始时。**

理由是这一轮的账已经结清：72 张票全部 `resolved`，切换的收益从下一轮才开始；
而现在切换要连带重跑并改写下面清单里的检查——**在一个已经收口的轮次里动检查网，收益是负的**。

## 切换后什么搬走、什么留下

| | 搬到 GitHub Issues | 留在仓库文件 |
|---|---|---|
| 票据 | ✅ 一票一个 issue（`to-tickets`：**按依赖顺序发布，阻塞者先发**） | |
| **地图** | ✅ **地图本身就是一个 issue**，标签 `wayfinder:map`；票据是它的 child issue | |
| 历史对话 | ✅ issue 原生评论 | |
| 分类状态 | ✅ **labels**（不再是 `Status:` 行） | |
| 阻塞关系 | ✅ **原生 issue 依赖**（`blockedBy` / `blocking`） | |
| spec | | ✅ `spec.md` 仍是文件 |
| `review/`、`docs/adr/`、`docs/agents/*`、`label-colors.json` | | ✅ 仍是文件 |

**调色盘不是白建的**：`label-colors.json` 里那组 `wayfinder:{map,research,prototype,grilling,task}`
标签本来就是为 GitHub 后端准备的——markdown 后端下它们没有落点，切换后由 deck 用来创建真实标签。

## 切换时必须一起做的事（不许只改那一行标题）

1. **改 `docs/agents/issue-tracker.md`**：首行标题 → `# Issue tracker: GitHub`，并把 Conventions
   重写成 GitHub 的约定（labels 表达分类、原生评论承载历史、`wayfinder:map` 承载地图、原生依赖表达阻塞）。
2. **`scripts/check-tracker.ps1` 里失去对象的组，逐组改写或显式退役，并写明理由**。
   按本仓纪律，**失去对象不得静默通过**。预计受影响：

   | 组 | 检查什么 | 切换后的处理 |
   |---|---|---|
   | §3 / §4 | 阻塞图的形状、已 resolved 却被未解决票阻塞 | 改写为对原生依赖的查询 |
   | §5 | 地图索引 ↔ 票据文件一一对应 | 改写为 parent/child 关系查询 |
   | §13 / §14 | 地图五个区块逐字存在、Decisions 行格式 | 改写为 issue body 检查（地图成了 issue） |
   | §15 / §16 | 票据字段行首裸行、`Blocked by` 形状 | **退役**（labels 与原生依赖取代了这两个形状） |
   | §17 | 后端探测锚点 + 调色盘契约 | 锚点仍在；补"标签是否真的按调色盘建出来" |
   | §20 | 历史必须住在 `## Comments` 里 | **退役**（原生评论取代） |
   | §24 | 已 resolved 的票不得留未打勾的验收框 | 改写为对 issue body 的检查 |
   | §19 / §21 / §22 / §23 | spec 七节、指针存在、ADR status 闭集、review 编号 | 不受影响（仍是文件） |

3. **归档指针**：`.scratch/nexusstack-next/` 原样保留为**这一轮的历史**，在新 `issue-tracker.md` 里写一句指针。
4. **一次端到端实跑**才算完成：建票 → 评论 → 打标签 → 建阻塞边 → 关票，且面板读得到。

## 不迁移那 72 张历史票

它们全部已 `resolved`，而每张都是长文书 + 共 41 条评论。灌成 72 个 issue 得到的是噪音，
而**历史已经在 git 里**（diff、blame、评审都在）。要可搜索的历史，就单开一票做脚本化迁移
（`gh issue create --body-file` + `gh api` 建依赖，一次跑完，可重复）。

## 后果与边界

- **票据离开 git 的 diff**：不再随分支走、也不会出现在 PR 的改动里——换来的可寻址、可链接、`Closes #N`
  与原生阻塞图。这是一次有取舍的交换，不是纯升级。
- **离线不能建票**：markdown 后端在断网时照常工作，GitHub 后端不行。
- **`main` 已受保护**（ruleset `main-pr-role`），所以这次切换本身也会走 PR：分支 → PR → CI 绿 → 人合并。
