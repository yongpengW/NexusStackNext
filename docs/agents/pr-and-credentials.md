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

`.github/workflows/ci.yml` 跑的就是那三段（`dotnet build` → `run-tests.ps1 -Configuration Release` → `check-format.ps1`），
**再加三条断言**：模板生成物不含凭据、跟踪器与规范一致、模板能生成可构建的工程。

2026-10-02 起，CI 提供运行专属的 PostgreSQL 容器，数据库测试串行执行；连接配置由步骤内构造并
通过环境传给测试脚本，不需要 secret。CI 同时启动运行专属 RabbitMQ 管理镜像，随机口令只走环境，
真实 broker 与业务协作旅程一并执行；本机配置 `env/test.dev` 后可运行同一套测试。
Pricing 缓存旅程另用 CI 专属 Redis 7.2.14；本机的 `NEXUSSTACK_TEST_REDIS` 仍只写入被忽略的测试配置。

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
- 仓库里机器守着的那一半是 `scripts/assert-no-credentials.ps1`（336 个文件、8 条规则），
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

## 分支模型（2026-09-30 起）

| 分支 | 直推 | 推送时跑 CI | 怎么进 |
|---|---|---|---|
| **`dev`** | ✓ 开发中的改动都落这里 | ✓ **也跑** | 直接 `git push` |
| **`main`** | ✗ | ✓（PR 触发，全套） | 只能经 `dev → main` 的 PR，且必需检查 `构建与测试` 通过 |

**为什么 `dev` 也跑 CI**（2026-09-30 定）：公开仓库的 Actions **不计费** ✓，而 CI 是**唯一的"平台差异"检查**
（Linux / 干净检出）—— 这一轮它抓到过"路径正则只认 Windows 反斜杠、本机永远绿而 ubuntu 上冤枉五个上下文" ✓。
所以 `dev` 上的直推**也过 CI**：坏提交**立刻**看到红 ✓，不会攒到 `dev → main` 的 PR ✗。

仍然推荐：**推之前先本地跑三段**（`dotnet build` → `scripts/run-tests.ps1` → `scripts/check-format.ps1`，约 100 秒 ✓）
—— 它比 CI 快，能在推送前拦下问题 ✓（这是习惯，不是闸）。

另：`dev` 不要活太久 ✓ —— 每完成一个完整小块就 `dev → main` 一次，PR 才好看、才不容易冲突 ✓。

**`dev` 只受一条 ruleset 保护：禁删除** ✓ —— **刻意不要求状态检查** ✗：
要求了就直推不了（`main` 上实测过 `GH013: Required status check … is expected` ✓）。
而这条禁删除是必要的：仓库开着"合并后自动删除 head branch" ✓，`dev → main` 的 PR 其 head 正是 `dev` ✗ ——
没有它，**一合并 `dev` 就没了**。
## 实跑记录（2026-09-30，规则上线当天）

| PR | 内容 | 观察 |
|---|---|---|
| [#1](https://github.com/yongpengW/NexusStackNext/pull/1) | 文档：保护规则已开启 | 首个 PR。CI 没跑完时 `mergeState=BLOCKED`，跑完 **2m51s** 变 `CLEAN` —— **这就是那道闸在动** |
| [#2](https://github.com/yongpengW/NexusStackNext/pull/2) | ADR-0016：票据后端下一轮切 GitHub Issues | 与 #1 无文件重叠；`required_status_checks` 没开 strict，所以**不要求分支追平 main**，两条都能独立合并 |
| [#17](https://github.com/yongpengW/NexusStackNext/pull/17) | 文档：更正八处过期声明 | 完整走了一遍：本地三段 → CI **2m48s** 绿 → `mergeState=CLEAN` → 合并。**合并后远端 head branch 被仓库设置自动删除**——见下面第二条 |

四件事值得记：

- **规则生效的第一个后果是"agent 改不动 `main` 了"**：连"把保护规则写进文档"这件事本身也只能走 PR
  —— #1 就是它。**一条只写在文档里的约定，与一条会拒绝你的规则，差别就在这里。**
- **"谁删了分支"有两个来源，命令行只是其中一个。** `gh pr merge --squash` 不带 `--delete-branch`
  确实不删，但**仓库设置 `delete_branch_on_merge=true` 会替你把远端的 head branch 删掉**。
  实测（2026-09-30）：PR #17 合并后再 `git fetch --prune`，输出是
  `- [deleted] (none) -> origin/docs/stale-claims-cleanup`。所以合并之后**两层的结果不一样**：

  | 层 | 结果 | 谁决定的 |
  |---|---|---|
  | 远端 head branch | **已删** | 仓库设置——与命令行参数无关 |
  | 本地分支 | **留着** | 人——删本地分支是 agent"必须先问"的动作 |

  **"看到残留的已合并分支是约定"这句话只对本地成立。** 它原来写的是"本地与远端都会留着"——
  那是**只验了命令那一层、没验仓库设置那一层**（`AGENTS.md` 纪律第九条：定性的话要指到某一层）。
  与它相邻的那段（"仓库开着'合并后自动删除 head branch'"）写的是对的，两段当时互相矛盾。
- **squash 合并过的分支，`git branch -d` 会拒绝删除。** `main` 上那个提交是新造的，
  分支上的原提交从 SHA 看永远"没被合并"。**别把这个拒绝当成"还有东西没合进去"的证据**——
  先证明等价（`git diff main <branch>` 为空、且分支那笔提交与合并提交空 diff），再用 `-D` 删。
- **`mergeStateStatus` 是一个能读的状态**（`gh pr view --json mergeStateStatus`）：
  `BLOCKED` = 还差必需检查/批准，`CLEAN` = 可以合。合并前读它一眼，比猜省事。
