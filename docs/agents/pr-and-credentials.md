# 提交、PR 与凭据

`AGENTS.md` 的「提交、PR 与凭据」一节把**护栏**留在那里（谁能做什么、凭据纪律）；
这份文件放**细则**：命令、CI 与本地的一致性、以及当前的工具与权限现状。

## 一次 PR 的形状

```powershell
git switch -c fix/<票据号>-<短名>
# 改动 → 本地三段：dotnet build → scripts/run-tests.ps1 → scripts/check-format.ps1
git push -u origin HEAD
gh pr create --fill          # 标题/正文从提交取；CI 会在 PR 上再跑一遍
gh pr checks --watch
gh run view --log-failed     # 红了先读原文，别猜
```

`gh` 装在 `C:\Program Files\GitHub CLI\gh.exe`；新开的终端直接 `gh` 就能用
（MSI 改的是机器 PATH，已启动的进程要等重启）。

## CI 与本地是同一套，但**本地绿不是终态**

`.github/workflows/ci.yml` 跑的就是那三段（`dotnet build` → `dotnet test` → `check-format.ps1`），
**再加三条断言**：模板生成物不含凭据、跟踪器与规范一致、模板能生成可构建的工程。

所以"本地全绿"是**必要条件**，不是替代品：平台差异（路径分隔符、大小写敏感、换行、区域设置、
可用工具）只会在这里现形。它真的现形过一次——检查脚本里三处路径正则写死了 Windows 反斜杠，
本机永远绿，而 ubuntu 上检查**逐个上下文指控"没有 docs/adr/"：五个冤枉**
（详见 `AGENTS.md` 纪律第六条）。

**推之前想清楚"我在哪个平台上验过"**：本机绿 + CI 绿都拿到，才算验完。

## 凭据

- **值走文件或 stdin**：`gh secret set NAME < <文件>`。**不要用 `-b <值>`**——
  命令行会进 shell 历史与进程列表。
- **不回显**：读日志、读配置、验证连接时只报"有没有"与"多长"。
- **不进会话**：任何口令、令牌、连接串都不要粘进对话——粘进去就进了对话记录。
  要 agent 用某个值，就把值放进文件、只告诉它**路径**。
- 仓库里机器守着的那一半是 `scripts/assert-no-credentials.ps1`（334 个文件、8 条规则），
  它守**模板生成物**；人手里的动作它管不了，所以上面三条靠约定。

## 现状（读到这一段时请顺手更新）

| | |
|---|---|
| `gh` | 已装并登录：2.102.0，账号 `yongpengW`，scopes `repo` / `workflow` / `gist` / `read:org` |
| 仓库可见性 | **公开**（`private=false`）——只读的 `gh api` 与 `curl api.github.com` 无需凭据 |
| git 的推送凭据 | Windows 凭据管理器（`credential.helper=manager`），**没有**切给 gh |
| `main` 保护规则 | **已开启**（2026-09-30）：ruleset `main-pr-role`，Active，目标 = 默认分支，**绕过名单为空**。规则 = 必须走 PR（批准数 **0**）+ 必需检查 **`构建与测试`** + 禁删除 + 禁强推 |

两条值得说明：

- **为什么没把 git 切给 gh（`gh auth setup-git`）**：现在这条路是通的，切换只会多一个失败点。
  等哪天真需要 gh 的作用域去推 `.github/workflows/`，再切。
- **这套约定的执行者现在是 GitHub 自己**：直推 `main` 会被拒。实测（2026-09-30，造一个空提交试推）：

  ```
  remote: error: GH013: Repository rule violations found for refs/heads/main.
  remote: - Changes must be made through a pull request.
  remote: - Required status check "构建与测试" is expected.
  ```

  **为什么"绕过名单为空"是关键**：agent 用的是仓库所有者的身份，而规则集里**不在绕过名单上的人都要遵守**
  ——名单一填上 `Repository admin` 或所有者本人，规则对 agent 就失效了。
  **代价是所有者自己也直推不了 `main`**：要改就临时把 Enforcement 设成 Disabled，改完设回 Active。

  **两个容易设错的地方**（都踩过）：Enforcement 默认是 `Disabled`（**不改成 Active 等于没设**）；
  `Required approvals` 必须为 **0**——本仓只有一个账号，而 **PR 作者不能批准自己的 PR**，
  设成 1 会让 PR 永远拿不到批准、永远合不了。

  **核实方法**（以 API 为准，不看网页）：

  ```powershell
  gh api repos/yongpengW/NexusStackNext/rules/branches/main   # 生效的规则
  gh api repos/yongpengW/NexusStackNext/rulesets              # 规则集本身
  ```

  **一个仍未强制的地方**：合并由谁点。规则集拦的是"直推"，而**谁来按合并键**仍靠约定——
  合并属于 agent"必须先问"的动作。想在机制上也强制"只有人能合并"，单账号做不到
  （作者不能自批），得另建一个协作者账号当批准人。

## 实跑记录（2026-09-30，规则上线当天）

| PR | 内容 | 观察 |
|---|---|---|
| [#1](https://github.com/yongpengW/NexusStackNext/pull/1) | 文档：保护规则已开启 | 首个 PR。CI 没跑完时 `mergeState=BLOCKED`，跑完 **2m51s** 变 `CLEAN` —— **这就是那道闸在动** |
| [#2](https://github.com/yongpengW/NexusStackNext/pull/2) | ADR-0016：票据后端下一轮切 GitHub Issues | 与 #1 无文件重叠；`required_status_checks` 没开 strict，所以**不要求分支追平 main**，两条都能独立合并 |

三件事值得记：

- **规则生效的第一个后果是"agent 改不动 `main` 了"**：连"把保护规则写进文档"这件事本身也只能走 PR
  —— #1 就是它。**一条只写在文档里的约定，与一条会拒绝你的规则，差别就在这里。**
- **`gh pr merge --squash` 不删分支**（要 `--delete-branch` 才删）。本仓把"删分支或 tag"列在
  agent **必须先问**的动作里，所以合并后本地与远端都会留着分支——
  **看到残留的已合并分支是约定，不是故障。**
- **`mergeStateStatus` 是一个能读的状态**（`gh pr view --json mergeStateStatus`）：
  `BLOCKED` = 还差必需检查/批准，`CLEAN` = 可以合。合并前读它一眼，比猜省事。
