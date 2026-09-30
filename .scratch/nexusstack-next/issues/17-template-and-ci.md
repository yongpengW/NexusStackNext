# 17 — 模板化与 CI

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 01, 13

> **2026-09-29 说明**：本票原写 `Blocked by: 01, 13, 15`。交付时只做了不依赖测试库的那一半，
> 却把状态改成了 resolved 而没改依赖字段——**一句自相矛盾的声明**，
> 15 轮后由 `scripts/check-tracker.ps1` 抓出来。现已改正：交付的这一半确实不依赖 15。

> **2026-09-29 范围说明**：原票依赖票据 15（测试库）——那是为了模板里的集成测试能跑。
> 但模板化的**核心验收**（生成物能构建、能测试、不带凭据、自带规范）不依赖测试库，
> 因此先交付这一半。待测试库到位后，模板会连带获得集成测试，无需再改模板机制。

## Comments

### local

**Answer**

**产出（4 个文件）**

| 文件 | 内容 |
|---|---|
| `.template.config/template.json` | `sourceName=NexusStackNext`、短名 `nexusstack-next`、排除规则、`primaryOutputs` |
| `scripts/assert-no-credentials.ps1` | **可执行**的凭据断言：生成探针工程并扫描 |
| `.github/workflows/ci.yml` | 构建 + 测试 + 凭据断言 + 模板可生成性断言 |
| `README.md` | 门面：结构、八条不变量、命令、约定 |
| `AGENTS.md`（修订） | 修正悬空引用（原来提到还不存在的 `aspire/`、`deploy/`） |

**端到端验证（真的生成了一个工程并跑它）**

```
dotnet new install <repo> --debug:custom-hive %TEMP%\nsn-hive   → 安装成功
dotnet new nexusstack-next -n DemoShop -o %TEMP%\demogen        → 生成成功
  ├─ 名字替换：全树扫描 "NexusStackNext" 残留 = 0
  ├─ 规范随模板走：AGENTS.md ✓  CONTEXT-MAP.md ✓  docs/adr/ ✓  各上下文 CONTEXT.md ✓
  ├─ 正确排除：.template.config ✓ 已排除   .scratch ✓ 已排除
  ├─ dotnet build DemoShop.slnx                                 → 已成功生成
  ├─ dotnet test  DemoShop.slnx                                 → 279/279 通过
  └─ 跑起来：DemoShop.Platform.Api → /health = HTTP 200 Healthy，业务端点 = HTTP 200
```

**279 个测试在生成物里全绿，其中包含架构不变量测试**——它们检查的是 `DemoShop.*` 这个全新命名空间。
这是最强的验证：模板产出的不只是"能编译的代码"，而是一个**自带边界守卫的自洽工程**。

**凭据断言的实跑结果**

```
模板生成物干净：检查了 133 个文件，6 条规则，0 命中。
```

六条规则找的是**值**而不是属性名：连接串口令、AMQP 连接串里带明文口令的写法、阿里云 `LTAI…`、
`-----BEGIN … PRIVATE KEY-----`、内网地址、以及参照仓库里那个已知口令样本。
（脚本排除自身——它里面写着样本口令与正则，扫自己必然命中。）

**验收标准**

| 验收项 | 结果 |
|---|---|
| 干净目录 `dotnet new` 出的工程能 `dotnet build` 通过 | ✅ 且 **279/279 测试通过**、宿主能跑 |
| 打包产物经脚本断言不含凭据（CI 中作为失败条件） | ✅ 脚本实跑通过，已接进 CI |
| CI 在 PR 上自动运行，build 与 test 都必须绿 | ⚠️ **工作流已写但未在 GitHub 上跑过**（本机无法执行 Actions）。YAML 已按本机验证过的命令编写 |
| README 明确写出不支持 PostgreSQL 以外的数据库 | ✅ 写在"要求"一节，并指向 ADR-0002 |
| 模板生成物自带 `AGENTS.md` / `CONTEXT.md` / `docs/adr/` | ✅ 已核实 |

**一处诚实标注**

`ci.yml` 我只做了本地等价验证（同样的命令在本机逐条跑过），**没有**在 GitHub Actions 上执行过——
本机无法运行 Actions。等到仓库推到 GitHub 才能确认；在那之前它属于"已写未验"，不应被当作已通过。

**仍未完成的部分**

- 模板参数面（`--contexts` 选装、是否含网关）需要符号与条件排除，属票据 17 的下一段。
  当前版本生成全部五个上下文。
- 集成测试进入模板，等票据 15 的测试库。
- **各宿主里的自述文案还带着本项目的票据号**（例如"持久化见票据 19"）。
  对模板使用者来说那是作者的待办清单，不是他要的文档。发布模板前应改成中性表述
  （"尚未接入持久化"）。Identity 的新宿主已经是中性写法，其余四个待改。
