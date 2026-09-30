# 评审 17 —— 孤儿检查的作用域（第 25 轮）

## 我上一轮写错了一句话

评审 16 的结尾我写：

> `InvariantCoverage_IsCompleteAndPointsAtRealTests` 保证"登记了的测试都存在"，
> 但**不保证"新写的结构检查都被登记"**。……**这是单向的。**

**方向是对的，我判断错了。** 那条检查里本来就有：

```csharp
var orphans = facts.Except(claimed).Order(StringComparer.Ordinal).ToList();
Assert.True(orphans.Count == 0, "……请登记，别让覆盖表悄悄失真");
```

## 错在哪：作用域，不是方向

```csharp
var facts = typeof(ArchitectureInvariantTests)
    .GetMethods(...)          // ← 只看一个类
```

所以真正逃出射程的是"**写在别的类里**的检查"。实测当时有**四个**：

| 类 | 在射程外 | 备注 |
|---|---|---|
| `AuditBypassIsForbiddenTests` | ✗ | 比我的早 |
| `EndpointPurityTests` | ✗ | 比我的早 |
| `TestProjectHygieneTests` | ✗ | 比我的早 |
| `ContextMapTests` | ✗ | 第 24 轮我加的 |

**失效方式与那条检查想防的一模一样**：新写一条结构检查、放进一个新类，
就不会有人提醒你登记——而"没登记"与"登记完整"在旧版下看起来没有区别。

## 我为什么会写错

我只看到"我的新测试没被要求登记"，就断定机制是**单向**的，
**没有去读它到底怎么取 `facts`**。

> **结论下在了证据前面。** 而这正是这一整轮 review 反复在抓的那件事——
> 只不过这次犯错的是我自己，对象是我自己对一份测试的理解。

## 修法与验证

孤儿检查改为扫**整个程序集**，并要求"整体就是一条卫生检查"的类出现在新的
`StructuralHygieneClasses` 清单里（现在四个）。

反向验证（**先确认编译通过**，这条纪律第四次生效）：

```
步骤 1：✅ 编译通过
步骤 2：[FAIL] InvariantCoverage_IsCompleteAndPointsAtRealTests
  以下测试既没有归属到某条不变量，也没有登记为结构性卫生检查——请登记，别让覆盖表悄悄失真：
    Probe_StructuralCheckThatIsNotRegistered
步骤 3：还原 → 17/17 绿
```

**一条没登记的结构检查写在一个新类里——现在会被点名。**
