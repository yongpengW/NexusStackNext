# 16 — 参照仓库的凭据打包口子（唯一允许改动 NexusStackBackend 的票）

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: —

> **这是本项目唯一获授权修改 `D:\NexusStack\NexusStackBackend` 的票据**（用户已在 2026-09-29 明确同意"加排除规则，文件保留"）。
> 改动范围**仅限排除规则两处**，不改任何业务代码、不删任何文件。

## 事实（已核实）

- `Host/NexusStack.WebAPI/agile/config/*.cache` 含明文生产凭据：PostgreSQL `<服务器IP>`、Redis `<服务器IP>`、
  RabbitMQ `<服务器IP>`、阿里云 AccessKeySecret ×2、ApiSecret，另有一份指向公网 `<服务器IP>`。
- **这些文件未被 git 跟踪**（`git ls-files -- agile/` 为空，`.gitignore:244` 已忽略），**没有推到 GitHub**。
- 真实风险在**分发**：`NexusStack.Template.csproj`（`Content Include="**\*"` + `NoDefaultExcludes=true`）
  与 `.template.config/template.json` 的排除列表都未覆盖 `agile/**` 或 `*.cache`。

## Answer

**改动（2 个文件，7 insertions / 2 deletions，`git diff --stat` 已核）**

1. `NexusStack.Template.csproj` — `Content Include="**\*"` 的 `Exclude` 加入 `**\agile\**;**\*.cache`，并加注释说明原因。
2. `.template.config/template.json` — `sources[0].modifiers` 的 `exclude` 加入 `"**/agile/**"` 与 `"**/*.cache"`。

**验证（隔离模板 hive，未触碰全局模板库）**

```
dotnet new install <repo> --debug:custom-hive %TEMP%\nn-hive     → 成功
dotnet new nexusstack -n ExcludeProbe -o <tmp> --dry-run          → 清单中 agile / .cache 命中数 = 0
dotnet new nexusstack -n ExcludeProbe -o <tmp>（真实生成）        → 471 个文件
```

真实生成结果：

| 检查 | 结果 |
|---|---|
| 文件名含 `agile` / `.cache` | 仅有 `obj/project.nuget.cache` ×11 —— 是生成后 `dotnet restore` 的产物，**不是模板内容** |
| `agile/config/*.cache` | **零出现** ✅ |
| 内容含 `<已知口令样本>` / `accessKeySecret` 明文 | 零出现 ✅ |

**验收标准**

- [x] 生成产物中 `*.cache` / `agile` 零命中（除 restore 产物）。
- [x] `git status` 只有这 2 个文件被改。
- [x] `Install-Template.ps1` 的安装路径未受影响（模板仍能正常安装与生成）。
- [x] 密钥文件本体**原样保留**在磁盘上。

**发现的副作用问题（已另立票据）**

- `dotnet pack NexusStack.Template.csproj` **本身就跑不通**：该 csproj 没有 `TargetFramework`，
  报 `NETSDK1013`。而 `Install-Template.ps1:74,98` 正是用 `dotnet pack` 走"方式 2"安装的
  ——也就是说脚本的这条分支从来没成功过。csproj 的排除规则目前是**纵深防御**，不构成实际泄漏路径。
  详见票据 18。
