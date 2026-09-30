# Coding standards

**这份文件是"评审 Standards 轴"的入口**（`/code-review` 要找的那份成文标准）。
它本身不含规则——规则在下面三个**单一事实源**里，这里只负责把它们指出来、说清哪一条管什么，
以及**哪些不是标准**。

> 为什么要有这么一份"只是指路"的文件：标准此前散在三处，`code-review` 的做法是
> "定 standards 源，逐 file/hunk 引标准"。散着的时候，评审者只能凭印象——而凭印象的标准
> 与"我个人不喜欢"没有区别。

## 规则的三个源

| 源 | 管什么 | 权威形式 |
|---|---|---|
| `AGENTS.md` | **架构不变量**（八条——条数由 `InvariantCoverage` 那张表守着）、部署不变量、**写检查与做验证的纪律**、设计用语 | 散文 + 由 `tests/Architecture.Tests` 断言 |
| `Directory.Build.props` | 构建基线：`TreatWarningsAsErrors`、`Nullable`、`GenerateDocumentationFile`、分析器级别；测试工程的豁免清单 | 构建即失败 |
| `.editorconfig` | 格式与命名：UTF-8 / LF / 4 空格、file-scoped namespace、`IDE0005 = error`、迁移文件视为生成代码 | 构建即失败（`EnforceCodeStyleInBuild`） |

**三者冲突时以更严的为准**；`AGENTS.md` 里的不变量如果与某条分析器规则冲突，改分析器配置而不是改不变量。

## 硬违规 vs 判断项

- **硬违规**：构建失败、架构测试失败、`scripts/check-tracker.ps1` 或 `scripts/assert-no-credentials.ps1` 失败。
  这些不需要讨论。
- **判断项**：下面那组 baseline 坏味道，以及"这个模块够不够深"这类设计判断。判断项要给出理由，
  不写成"不符合标准"。

## Baseline：12 条坏味道（评审时逐 hunk 对照）

`code-review` 要求除成文标准之外再带一组固定 baseline。本仓采用 Fowler 的十二条：

Mysterious Name · Duplicated Code · Feature Envy · Data Clumps · Primitive Obsession ·
Repeated Switches · Shotgun Surgery · Divergent Change · Speculative Generality ·
Message Chains · Middle Man · Refused Bequest

**baseline 永远不是硬违规**，而且**成文标准赢过 baseline**。本仓已经用成文规则取代了其中两条：

- *Speculative Generality* → 不变量 7（"一个适配器只是假设的缝，两个才是真的缝"）与
  "共享代码要被第二个消费者证明需要"；
- *Primitive Obsession* → 领域层的强类型 ID 与值对象（`UserId` 而不是 `long`，`SettingKey` 而不是 `string`）。

## 一条容易被误读的规则

`Directory.Build.props` **禁止成批 `NoWarn`**。要豁免某条规则，就在**触发它的那个类型上**用
`[SuppressMessage]` 并写明 `Justification`——规则关在源头、理由留在原处。
唯一的例外写在 `.editorconfig` 里：`**/Migrations/*.cs` 被声明为**生成代码**，
理由是"代码的来源"而不是"它报错了"。

## 命令（标准的可执行形式）

```powershell
dotnet build NexusStackNext.slnx        # 警告即错误：这一步同时是"没有坏味道"的检查
pwsh -File scripts/run-tests.ps1        # 全部测试，**串行**：十几个工程共用一台库，并行会压垮它
pwsh -File scripts/check-format.ps1     # 格式：`dotnet format --verify-no-changes`
pwsh -File scripts/check-tracker.ps1    # 跟踪器与规范的一致性
pwsh -File scripts/assert-no-credentials.ps1
```

顺序是固定的：**typecheck → tests → format**（`resolving-merge-conflicts` 的三段）。
`check-format.ps1` 第一次跑就抓到 448 处违规（24 个文件）而构建全绿——所以它不是一个形式步骤。

## 不在标准里的东西

- **不规定**测试文件的位置与命名（`tdd` 技能也不规定）；本仓的现状是 `<上下文>.<层>.Tests`。
- **不规定**命名风格的具体词表——领域用词以各上下文的 `CONTEXT.md` 为准，那是**领域**词表，
  与设计用语（`docs/agents/design-vocabulary.md`）是两套，不要混。
- **不要求**每一处都有注释；但**注释里的断言要能被验证**——本仓反复吃过"注释读起来像事实、
  而它是错的"这一类事故（见 `AGENTS.md`）。
