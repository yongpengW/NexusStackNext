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
| `main` 保护规则 | **尚未开启（2026-09-30）** |

两条值得说明：

- **为什么没把 git 切给 gh（`gh auth setup-git`）**：现在这条路是通的，切换只会多一个失败点。
  等哪天真需要 gh 的作用域去推 `.github/workflows/`，再切。
- **`main` 保护规则是这套约定的执行者**：要求 PR + CI 通过才可合并。它**不在仓库文件里**，
  只能在 GitHub 网页上设，所以**开了之后请把上表那一行改成"已开启（日期）"**——
  留着旧的"未开启"比不写更坏：读的人会以为直推被拦，而实际上没有。
