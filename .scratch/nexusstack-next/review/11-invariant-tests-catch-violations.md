# 评审 11 —— 不变量测试**抓不抓得住违反**（第 19 轮）

前两遍看的是「文档与代码的差」（评审 09）与「交付物的成色」（评审 10）。
这一遍问一个更尖锐的问题：

> **那些守着架构不变量的测试，真的抓得住违反吗？**

一张表说"不变量 3 由 `DomainAssemblies_MustNotReferenceAnythingOutsideTheAllowList` 守着"，
而 `InvariantCoverage_IsCompleteAndPointsAtRealTests` 保证**名字对得上、测试存在**——
但那只证明**指针有效**，不证明**测试抓得住**。指针指向一个空函数也是有效的。

方法：**反向验证**——制造一次真实的违反，看测试红不红。

## 先验一条，它证明测试比我想的更聪明

挑的是 AGENTS.md 里一个**具体的技术断言**：

> `*.Application` 不依赖 `*.Infrastructure`——这条测试**同时查两层**：编译产物（真的用了）与
> `csproj`（埋着可以用）。**只查编译产物会漏掉后者**：C# 只为**实际用到**的程序集发出 AssemblyRef。

给 `Auditing.Application.csproj` 加一个**用不到的** `ProjectReference` 到 Infrastructure：

```
DLL 里出现 "Auditing.Infrastructure" 吗: False        ← 编译产物确实不留痕迹
架构测试: 失败!
  应用层不得依赖基础设施——端口在这里定义，适配器在外面组装。出现了下列引用：
  NexusStackNext.Auditing.Application.csproj → NexusStackNext.Auditing.Infrastructure（工程引用）
```

**断言成立** ✓。而且它比构建**更早、更准**：构建报的是 `MSB4006`（循环依赖），
而那要等 `Infrastructure → Application` 那条边也形成之后才报得出来；
这条测试在"引用刚加上"的时候就说话。

## 然后发现：**领域层那条规则缺了第二层**

照着上面的经验去看 `DomainAssemblies_MustNotReferenceAnythingOutsideTheAllowList`，
它的实现只有编译产物那一层：

```csharp
foreach (var reference in SolutionAssemblies.ReferencedAssemblyNames(path))
```

**同一个洞，在领域层是敞着的。**

### 实证

给 `Auditing.Domain.csproj` 加 `<PackageReference Include="Microsoft.EntityFrameworkCore" />`
而**没有任何代码使用它**：

```
已通过! - 失败: 0，通过: 1
```

**全绿。** 而"seam 放错了位置"这件事，恰恰是从"埋着一颗随时可以用的引用"开始的——
那正是这条不变量要防的东西。

合法基线干净得刺眼：

| 工程 | 引用 |
|---|---|
| `BuildingBlocks.Domain` | （无） |
| 其余五个 `*.Domain` | 只引用 `BuildingBlocks.Domain` |

**一个包都没有。** 所以规则可以写得很紧：领域工程不得有任何包引用，
工程引用只允许 `BuildingBlocks.Domain`。

### 修法

补上工程级那一层。**两个方向都验过**：

| | 结果 |
|---|---|
| 干净仓库 | ✅ 绿 |
| 塞回那个用不到的包引用 | ✅ **红**：`NexusStackNext.Auditing.Domain → Microsoft.EntityFrameworkCore（包引用）` |
| 还原后 | ✅ 绿（逐字节还原） |

## 这一轮的教训

**"这条规则有测试守着"是一句需要被验证的话。**

`InvariantCoverage` 的表校验了名字与存在性——那挡得住"引用了一个不存在的测试"，
挡不住"引用了一个测不到东西的测试"。这两件事的差别，在**有人真的违反规则的那一天**才会显形，
而那一天恰恰是最不适合发现"原来没测到"的一天。

**反向验证是唯一能区分它们的办法**：不看测试怎么写，看它**在你打破规则时红不红**。

## 还欠着的

- 不变量 **4**（一个聚合 = 一个事务）与 **6**（ID 不由环境态生成）**没有结构测试**——
  表里写明了它们由行为测试守着。那两条也该反向验证一次（改个聚合让它空操作也自增版本号，
  看行为测试红不红），留待下一轮。
- 不变量 **1**（上下文独占数据）表里自己承认"只能测结构性代理"。
