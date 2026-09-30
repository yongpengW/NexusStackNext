# 01 — 解决方案骨架与构建基线

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: —

## 未做（有意）

- **未 `git init`** —— 等你确认新仓库的托管方式（独立仓库 / 子目录 / 后续挂在别处）再建，避免建错位置。
- 未创建 `aspire/`、`deploy/` 空目录 —— 空目录不进 git，等有内容时再建。

## Comments

### local

**Answer**

**产出（7 个文件）**

| 文件 | 作用 |
|---|---|
| `global.json` | 固定 SDK `10.0.401`，`rollForward: latestFeature` |
| `Directory.Build.props` | TFM 默认 net10.0、`Nullable`、`TreatWarningsAsErrors`、`EnforceCodeStyleInBuild`、`AnalysisLevel=latest-recommended`、`GenerateDocumentationFile`、测试项目豁免组 |
| `Directory.Packages.props` | 中央包版本管理；测试包版本取自本机 SDK 内置 xunit 模板（不猜版本） |
| `.editorconfig` | UTF-8 / LF / 缩进；`IDE0005=error`；file-scoped namespace |
| `.gitignore` | 含参照仓库吃过的亏：`agile/`、`*.cache`、`appsettings.*.local.json`、`*.pfx` |
| `NexusStackNext.slnx` | 解决方案 |
| `src/BuildingBlocks/BuildingBlocks.Domain/BuildingBlocks.Domain.csproj` | 空壳（内容见票据 02） |

**验证结果**

| 验收项 | 结果 |
|---|---|
| `dotnet build` 成功 | ✅ 0 警告 0 错误 |
| `TreatWarningsAsErrors` 真的生效 | ✅ 反向验证：注入探针后 `CS0219`/`CS0169`/`IDE0005`/`CA1822` **全部作为 error 拦住构建**；移除后恢复 0 警告 |
| 无 NU1701/NU1605 类包告警 | ✅ 0 警告 |
| `AGENTS.md` / `CONTEXT-MAP.md` / `docs/adr/` / `docs/agents/` / `.scratch/` 齐备 | ✅ |
| 未覆盖旧模板 | ✅ 用 `--debug:custom-hive` 隔离验证，全局 `dotnet new list` 仍只有原有的一条 `nexusstack` |

**过程中踩到并已处理的三个坑（写下来免得复发）**

1. **`.slnx` 不支持我最初写的嵌套 `<Folder>`** —— 项目根本没被解析，`dotnet build` 报"找不到可还原的项目"却仍返回成功。改成扁平 `<Project Path=... />` 才生效。**注意：这种情况下 exit code 是 0，不能只看退出码。**

   > **2026-09-29 补充实测**（票据 43 重做分组时）：原因比当时写的更准确——
   > `<Folder>` 套 `<Folder>` 时，**被套住的那一层里的项目会被静默吞掉**（实测 37 → 35），
   > 而 `sln list` 与 `build` 都不报错。另外 `<Folder Name>` 必须首尾带 `/`，
   > 否则整份文件加载失败（`MSB4025`，反而响亮）。
   > **正确写法**：扁平 `<Folder>` + 路径式名字，如 `<Folder Name="/src/Services/Identity/">`。
   > 现在 `check-tracker.ps1` 有双向比对守着这件事。
2. **`CA1716`：`Error` 与 VB 保留字冲突** —— 处理方式是**显式 `[SuppressMessage]` + 写明 Justification**，并把"不许成批 `NoWarn`，要豁免就关在源头"这条纪律写进 `Directory.Build.props`。
3. **`IDE0005=error` 与测试项目的 `GenerateDocumentationFile=false` 冲突** —— 该规则需要文档生成才能运行，编译器直接报 `EnableGenerateDocumentationFile`。在测试项目组里精准豁免这两条。
