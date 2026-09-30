# 评审 12 —— 反向验证不变量 4 与 6（第 20 轮）

评审 11 立了一条规矩：**不看测试怎么写，看它在你打破规则时红不红。**
这一轮把它用在两条**没有结构测试**的不变量上（表里写明它们由行为测试守着）。

## 不变量 4 的代理：空操作不得自增版本号 —— **测试抓住了**

`User.Disable()` 里有一处空操作：

```csharp
if (!IsEnabled)
{
    return Result.Success();   // 不带 Changed
}
```

把它改成 `return Changed();`：

```
[FAIL] User_DisableAndEnable_BumpOnce_RepeatsDoNot
Assert.Equal() Failure: Values differ
Expected: 2    Actual: 3
```

**正是那个失效模式**：空操作自增让乐观并发在**没有冲突的情况下**误报冲突，
而误报的代价是调用方开始重试或干脆忽略冲突——那时这个机制就废了，且废得很安静。

## 不变量 6：**表里的理由站不住**

覆盖表原文：

> 结构性保证来自**不变量 3 的测试**（碰不到基础设施就调不到静态生成器）

**`Guid.NewGuid()` 与 `DateTime.UtcNow` 都是 BCL。** 不变量 3 挡的是*基础设施程序集*，
挡不住它们。而事实上领域层就在调 `Guid.NewGuid()`——12 处，全在领域事件的 `EventId` 上。
**那句话没有牙齿，而它读起来像有。**

于是补了一条真的检查：`DomainAssemblies_MustNotReachForAmbientState`。

## 但这条检查我自己写错了**两次**，而且第二次被第一次掩盖了

### 错误一：过滤器匹配 0 个文件，于是**静默通过**

第一版的过滤条件是一个字符串拼接：

```csharp
if (!file.Contains($"{Path.DirectorySeparatorChar}.Domain{Path.DirectorySeparatorChar}", ...))
```

它**一个文件都没匹配上**。而匹配不上时 `violations` 是空的——**测试绿**。
所以第一次反向验证：往领域文件里塞 `Guid.NewGuid()` 与 `DateTimeOffset.UtcNow`，**两次都绿**。

**而本仓库的脚本里早就为这件事写过注释：**

> **检查没有对象可查时不得报告通过。**
> —— `check-tracker.ps1`

**我在写一条专门防这类事的检查的时候，又犯了一遍。** 加上守卫之后它立刻说话：

```
一个领域层文件都没扫到——检查等于没跑，不能当作通过。（扫的是 …\src）
```

### 错误二：例外划得太窄，把**合法代码**报成违规

修好过滤器（改成**按路径分段**判断，不拼字符串）之后，扫到 24 个文件，然后冒出两处"违规"：

```
Platform.Domain\Settings\GlobalSetting.cs   → 调了 Guid.NewGuid()
Scheduling.Domain\Tasks\ScheduledTask.cs    → 调了 Guid.NewGuid()
```

**而它们都是合法的**——那两行是 `public Guid EventId { get; } = Guid.NewGuid();`。
那两个上下文把领域事件**内联**在聚合文件里，Identity 则放在 `Events/` 下。两种都合理。

**我第一版把例外划成"只允许在 `Events/` 目录下"，那是错的。**

### 两次错误之间的关系，才是这一轮真正值得记的

**错误一掩盖了错误二。** 过滤器匹配 0 个文件时，我永远看不到那两处"违规"，
也就不会去检查判据本身对不对。**一个"永远通过"的检查，不只自己不工作——
它还会让所有依赖它的判断都失去依据。**

判据改准之后落在"**这一行在干什么**"而不是"**文件在哪**"：

```csharp
if (line.Contains("Guid.NewGuid()") && !line.Contains("EventId"))
```

## 最终验证（两个方向）

| | |
|---|---|
| 干净仓库（扫 24 个文件） | ✅ 绿 |
| 聚合里生成 ID | ✅ **红**，并把那一行原文引出来 |
| 领域里用 `UtcNow` | ✅ **红** |
| 还原后 | ✅ 绿（逐字节一致） |

架构测试从 15 条变成 **16 条**。

## 附带：我自己在仓库里留了一个 0 字节文件

第一次做变异时我把目标路径猜错（`AuditEntry.cs` 其实在 `Entries/` 下），而
`[System.IO.File]::WriteAllText($path, $null)` **不抛异常，它写一个空文件**。

结果是仓库里多了一个 0 字节的 `AuditEntry.cs`。

**而构建照样通过**——C# 编译器对空源文件没有任何意见。
它会一直待在那里，直到某天有人打开它、发现是空的、然后怀疑自己看错了。

**没有测试、没有检查会告诉你这件事。** 它是靠"我读回自己写的东西"发现的。
所以下一轮开始，任何写文件的脚本都要在写之前**先确认读到了非空内容**。
