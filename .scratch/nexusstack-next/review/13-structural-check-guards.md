# 评审 13 —— 结构检查的守卫（第 21 轮）

评审 12 结尾留下一个动作：**把"会不会有检查永远通过"这件事本身变成一条检查。**

这一轮去做了，而它挖出了本仓库自查体系里的一处**系统性**问题。

## 发现：四条结构检查会在"什么都没扫到"时**静默通过**

类里 8 处调用 `SolutionAssemblies.BuiltSourceAssemblyPaths()`，其中四处的形状是：

```csharp
var violations = new List<string>();
foreach (var path in SolutionAssemblies.BuiltSourceAssemblyPaths())
{
    ...收集违规...
}
Assert.True(violations.Count == 0, "...");
```

**枚举出来是空的，违规就是空的，测试通过。**

它不只是理论。**评审 12 里刚发生过一次**：`DomainAssemblies_MustNotReachForAmbientState`
第一版的过滤器一个文件都没匹配上，于是往领域层塞 `Guid.NewGuid()` 与 `DateTimeOffset.UtcNow`
**两次都绿**。修好过滤器之后，立刻又暴露出判据本身也是错的。

### 而同一个类里，有的地方**有**守卫

`DomainAssemblies_MustNotReferenceAnythingOutsideTheAllowList` 里就有：

```csharp
Assert.NotEmpty(domainPaths);
```

**同一个类，同一类过滤器，一处有、四处没有。** 这不是"有人判断后决定不加"，
而是**加守卫这件事没有被一致地执行**——而它没有测试守着，所以没人发现。

## 修法：补在**源头**，不补在四处

与其在四个方法里各加一句，不如把它们调用的取数换成带守卫的版本：

```csharp
private static IReadOnlyList<string> SourceAssemblyPaths()
{
    var paths = SolutionAssemblies.BuiltSourceAssemblyPaths();
    Assert.True(paths.Count > 0, "一个源码程序集都没找到——这组结构检查等于没跑，不能当作通过。");
    return paths;
}
```

类里 **10 处**直接调用换成它，只剩取数函数内部那 1 处。**当前的与将来新增的调用点都受保护。**

判断"哪些需要守卫"时我逐条看过，有一条**故意不改**：
`SourceAssemblyInventory_MatchesDeclaredSet` 是拿扫描结果与**声明表**比对，
扫到 0 必然不匹配、必然红——它自己就是守卫。

## 一个我必须如实标注的缺口

**这条守卫的反向验证，我试了三次都没做成。**

三次的变异都没编译过（`CA1859`、`CS0019`），而**前两次的输出是空的**，
我一度读成"测试没说话"。实际是 `dotnet test` 打出了构建错误，而我的 grep 没匹配上。

第三条才是**这一轮第二个真正有价值的教训**：

> **"没匹配到输出"不等于"没有结果"。** 看测试结论之前，先看**退出码**，
> 再看**有没有构建错误**——否则"构建失败"与"测试通过"在筛选后的视图里长得一样。

关于守卫本身的证据，我能给的是**等价证据**而不是直接证据：

- 它是 `Assert.True(paths.Count > 0)`，取数只剩一处，逻辑上没有绕过的路径；
- **同一条断言、同一个形状，我亲眼见它报过错**——评审 12 里
  `DomainAssemblies_MustNotReachForAmbientState` 的守卫说过
  "一个领域层文件都没扫到——检查等于没跑，不能当作通过"。

**等价证据不等于直接证据**，所以写在这里，而不是把它算成"已验证"。

## 改动的验证

```
架构测试：16/16 通过
构建调试产物：0 错误
调用点：10 处直接调用 → 1 处（只剩取数函数内部）
```

## 这一轮没做的

- **不变量 5（禁止服务定位器）的反向验证**没做成：第一次探针缺 XML 注释没编译过，
  后两次探针写入被文件工具的陈旧状态拒绝。**没有证据，就不说它守住了。**
- 不变量 **1、7、8** 的反向验证还没做。

**留下的方法学**：反向验证这条路本会话成功过五次（发布确认、两层 csproj 断言、
领域包引用的洞、不变量 4 的版本号、不变量 6 的新检查），失败过三次。
失败的每一次都是**变异本身没编译过**——所以下一次做变异，**第一步先确认它编译通过**，
再去看测试红不红。
