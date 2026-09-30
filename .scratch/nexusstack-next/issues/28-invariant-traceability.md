# 28 — 不变量可追溯性：把声明变成被检查的声明

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 05

> **2026-09-29 说明**：本票来自一次自我审计——`AGENTS.md` 写着"违反八条不变量即 bug"，
> 但"哪条由哪条测试守着"此前只是**一段 XML 注释**。

## 审计发现的两个真实问题

**问题一：那段注释已经烂了，而且没有任何东西会响。**

测试项目关了 `GenerateDocumentationFile`（票据 01 的决定），
因此测试文件里的 XML 注释**不参与编译检查**。于是：

- 它引用了 `BuildingBlocks_MustStayAtTheFoundingSet`——**这个测试根本不存在**
  （真名是 `BuildingBlocks_MustNotGrowBeyondTheDeclaredSharedKernel`）。
- 它写着"不变量 1、2：**当前还没有任何上下文程序集，规则处于空转**"——
  而实际已有 13 个上下文程序集，这条规则早就在满负荷工作。

两处都活了很久没人发现。**一句关于"我们测了什么"的声明，要么被机器检查，
要么就是在慢慢变成谎话。**

**问题二：五个上下文里只有一个有上下文级 ADR。**

`docs/adr/` 在每个上下文都该有，但实际只有 Identity 一份。

## Comments

### local

**Answer**

**产出（5 个文件）**

| 文件 | 内容 |
|---|---|
| `tests/Architecture.Tests/InvariantCoverage.cs` | 不变量 → 测试的结构化映射表 |
| `ArchitectureInvariantTests.InvariantCoverage_IsCompleteAndPointsAtRealTests` | 校验这张表 |
| `ArchitectureInvariantTests`（修订） | 删掉烂掉的注释块；修正"规则处于空转"那句 |
| `src/Services/Auditing/docs/adr/0001-write-only-context.md` | 上下文级 ADR |
| `src/Services/Files/docs/adr/0001-store-and-url-provider-are-separate.md` | 上下文级 ADR |

**新的覆盖检查守四条**

1. 八条不变量**全部在表里**，编号 1..8 一个不缺。
2. 表里引用的每个测试方法名**必须真实存在**（反射核对）。
3. 没有本地测试的不变量**必须写明**去哪儿找、或为什么这里不测——
   不留"以为测了其实没测"。
4. **反向**：本测试类里每条 `[Fact]` 都必须有归属
   （要么挂在某条不变量下，要么登记为结构性卫生检查）。
   新增测试却忘了登记，这张表会红。

**反向验证（代码变异）**

把当初那句真实的腐烂注回去——引用不存在的测试名：

```
失败! - 失败: 1，通过: 8
  ✗ InvariantCoverage_IsCompleteAndPointsAtRealTests
```

精确命中。改动已还原。

**顺带说明：不变量 4 和 6 没有本地测试，这是刻意的**

- 不变量 4（一个聚合 = 一个事务）**不是结构规则**，程序集引用管不了它；
  行为由 `BuildingBlocks.Application.Tests` 的管线测试守着。
- 不变量 6（ID 不由环境态生成）的结构性保证来自不变量 3 的测试，
  行为性保证在 `BuildingBlocks.Domain.Tests`。

这两条现在被**显式写成"这里不测，理由是……"**，而不是留白。

**当前状态**：构建 0 警告 0 错误；架构测试 **9/9**。

**仍未完成**

- Platform、Scheduling 两个上下文仍缺上下文级 ADR（各有一处值得记录的决定）。
- `AGENTS.md` 里可以补一张"不变量 → 测试"的简表，让读者不必进代码就能知道哪条被守着。
