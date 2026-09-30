# 评审 16 —— `CONTEXT-MAP.md` 说的是不是真的（第 24 轮）

前六遍 review 看的是**测试**与**代码**。这一遍换一个对象：**上下文地图**。

它是这个架构里最像"结论"的一份文档——读它的人会据此判断"这个系统怎么通信"。
而它不受任何检查：`check-tracker.ps1` 只保证它**提到了**五个上下文。

## 发现：11 个名字里 **6 个不存在**

拿地图里点名的标识符逐个去代码里找：

```
TokenLog             ❌ 不存在
Region               ❌ 不存在
TaskDue              ❌ 不存在
OperationPerformed   ❌ 不存在
TokenIssued          ❌ 不存在
TokenRevoked         ❌ 不存在
```

而那一节整个用**现在时**写着：

> **Identity → Auditing**：Identity 发出 `UserLoggedIn`、`TokenIssued`、`TokenRevoked`、`LoginFailed`；
> Auditing 消费后落审计。

**读起来每一条都已经成立。**

### 实际情况（核实过）

| 地图说 | 实际 |
|---|---|
| Identity 发出 `TokenIssued`、`TokenRevoked` | 真实事件叫 `RefreshTokenIssued`、`RefreshTokenRevoked`（**领域事件**，不是集成事件） |
| Auditing 有 `TokenLog` | **没有** |
| 有统一的 `OperationPerformed` | **没有** |
| Scheduling 发出 `TaskDue` | **没有** |
| Platform 拥有"区域" | 只有 `GlobalSetting` |
| 跨上下文走 `*.Contracts` | **一个 `*.Contracts` 工程都没建** |
| —— | **没有任何上下文发布过集成事件** |

最后一条值得单独说：**事件总线（RabbitMQ）与发件箱已经建好并验过**（6 条真 broker 验收 + 投递循环），
但**没有生产者**——所以发件箱永远是空的，投递循环每两秒跑一次、每次读到 0 条。

地基是**真的**，房子**还没盖**。而地图说的是"房子已经在那儿了"。

## 这为什么比"没有地图"更糟

> 下一个人会照着它去 `grep` 一个不存在的事件，然后**怀疑自己的搜索方式**。

这跟本仓库反复批判的那些失效是同一类，而它自己的章程里早就写着：

> 一句关于"我们做了什么"的声明，如果不受检查，就会在没人注意的时候变成谎话。

## 修法：把地图分成两半，并给它加一条会失败的检查

**一、`Relationships` 分成「已经成立的」与「还没建的」。**
后者开头写明白：**不要照着它 grep**，并列出实际存在的替代名字。
`False friends` 里的 `Token` / `Region` 同样标注。

**二、新增 `ContextMapTests`。** 判据：

> 地图里用反引号标出的标识符，要么在 `src/` 或 `tests/` 里真的存在，
> 要么**同一行**上写明它是计划中的（`计划` / `还没` / `未建` / `不存在` / `预留`）。

**两样都不占，就是悬空的。**

### 验证

| | |
|---|---|
| 修好的地图 | ✅ 绿（解析 28 个标识符——不是空跑） |
| 塞一个不存在的名字进去 | ✅ **红**：`` `IConfigurationTruthKeeper` —— (那一行原文) `` |
| 还原后 | ✅ 绿，逐字节一致 |

而且它带**守卫**：一个标识符都没解析出来时会报
"检查等于没跑，不能当作通过"——**上一轮刚立的规矩，这一轮用上了。**

## 顺带发现的一处**单向**检查

`InvariantCoverage_IsCompleteAndPointsAtRealTests` 保证"表里登记的测试都存在"，
但**不保证"新写的结构检查都被登记"**。我刚才加的 `ContextMapTests` 没登记，而它照样绿。

**这是单向的**：登记了的不许写错，写了的可以不登记——于是这张表会慢慢变得不完整，
而"不完整"与"完整"在它自己的检查下看起来一样。

本轮没有改它（那是另一件事），记在这里。

---

## 更正（第 25 轮）：上面那节最后一段写错了

上一节我写"`InvariantCoverage_IsCompleteAndPointsAtRealTests` 是**单向**的，
它保证登记了的都存在，但不保证新写的都被登记"。**这句话是错的。**

那条检查里本来就有：

```csharp
var orphans = facts.Except(claimed).Order(StringComparer.Ordinal).ToList();
Assert.True(orphans.Count == 0, "……请登记，别让覆盖表悄悄失真");
```

**方向是对的。错的是作用域**：`facts` 来自

```csharp
typeof(ArchitectureInvariantTests).GetMethods(...)
```

——**只看一个类**。所以真正逃出射程的不是"新写的检查"，而是"**写在别的类里**的检查"。

实测当时有**四个**类在射程外：`AuditBypassIsForbiddenTests`、`EndpointPurityTests`、
`TestProjectHygieneTests`，以及第 24 轮新加的 `ContextMapTests`。前三个**比我的还早**。

而失效方式与那条检查想防的完全一样：**新写一条结构检查、放进一个新类，就不会有人提醒你登记**——
"没登记"与"登记完整"在旧版下看起来没有区别。

**我为什么会写错**：我只看到"我的新测试没被要求登记"，就断定机制是单向的，
**没有去读它到底怎么取 `facts`**。结论下在了证据前面。

修法：孤儿检查扫**整个程序集**，并要求"整体就是一条卫生检查"的类出现在
`StructuralHygieneClasses` 清单里。反向验证：往一个**新类**里放一条没登记的 `[Fact]`，
检查红，并点名 `Probe_StructuralCheckThatIsNotRegistered`。
