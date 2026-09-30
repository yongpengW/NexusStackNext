# 评审 15 —— 不变量 1/2/5/7 的反向验证（第 23 轮）

上一轮立的规矩第一次连续用在同一轮里：**变异一律先过编译，再去看测试红不红。**

**三次全部一次成功。** 而其中一次抓到了一个真实的洞。

## 不变量 5（禁止服务定位器）—— 守住了

变异：往 `BuildingBlocks.Infrastructure` 的一个已有类里插一行

```csharp
public static IServiceProvider? ProbeProvider { get; set; }
```

```
步骤 1：✅ 编译通过——变异有效，可以看测试了
步骤 2：[FAIL] NoServiceLocator_StaticContainerHoldersAreForbidden
  禁止静态服务定位器，请改用构造函数注入：
    …InfrastructureServiceCollectionExtensions.<ProbeProvider>k__BackingField（静态字段）
    …InfrastructureServiceCollectionExtensions.ProbeProvider（静态属性）
```

**它把编译器生成的幕后字段也一并报了出来**——那条检查读的是真实字段与属性，
不是源码文本，所以连自动属性的背面都跑不掉。

## 不变量 7（共享内核不长胖）—— 守住了

变异：把 `Auditing.Domain` 的 `<AssemblyName>` 改成 `NexusStackNext.BuildingBlocks.Probe`
（一行改动、能编译、还原就是删掉）。

```
步骤 1：✅ 编译通过     步骤 2：✅ 解决方案构建通过
步骤 3：[FAIL] BuildingBlocks_MustNotGrowBeyondTheDeclaredSharedKernel
  BuildingBlocks 里出现了未声明的共享内核程序集（不变量 7：被第二个消费者证明需要才允许上移）：
    NexusStackNext.BuildingBlocks.Probe
```

## 不变量 1/2（跨上下文）—— **发现一个真实的洞**

变异：给 `Auditing.Domain` 加一行指向 `Identity.Domain` 的 `ProjectReference`，
**没有任何代码使用它**。方向选的是不会形成环的那一边，所以它能编译。

```
步骤 1：✅ 编译通过     解决方案也构建通过
步骤 2：已通过! - 失败: 0        ← 跨上下文引用没有被抓到
```

### 原因就是 `AGENTS.md` 里写明的那件事

那条规则读的是**编译产物**（`ReferencedAssemblyNames`），而 C# **只为实际用到的程序集发出 AssemblyRef**。
一个挂在工程上、没有代码使用的 `ProjectReference` 在 DLL 里**不留任何痕迹**。

### 这是同一个洞的**第三次**

| 规则 | 第一层（编译产物） | 第二层（csproj） |
|---|---|---|
| `*.Application` → `*.Infrastructure` | 早就有 | **早就有** |
| `*.Domain` 的包引用 | 有 | 评审 19 补上 |
| **跨上下文引用** | 有 | **本轮补上** |

而第三次这个守着的是**不变量 1 与 2**——"上下文独占自己的数据"与"只通过 Contracts 通信"，
八条里最靠前的两条，也是这个架构最贵的那两条。

### 修法与验证

补上工程级那一层，并且**多一条判断**：指向别人的 `*.Contracts` 是**合法**的，
不变量 2 说的正是这个。

| | |
|---|---|
| 干净仓库 | ✅ 绿 |
| 塞回那行跨上下文引用 | ✅ **红**：`Auditing.Domain.csproj → NexusStack.Identity.Domain（工程引用；Auditing 引用 Identity）` |
| 还原后 | ✅ 绿 |

**新的一层给出的信息比旧的更好**：旧的只说"某个程序集引用了某个程序集"，
新的说出**哪个上下文引用了哪个上下文**。

## 一句总结

> **同一类错误犯三次，说明该把它变成一条规矩，而不是做第三次修补。**

规矩上一轮已经写进 `AGENTS.md`（「写检查与做验证的纪律」第一条）。
这一轮是它第一次发挥作用——**而且是先发现了洞，再回头发现"这已经是第三次了"。**

## 反向验证的账，现在满了

| 不变量 | 反向验证 |
|---|---|
| 1（上下文独占数据） | ✅ 本轮（与 2 共用一条测试） |
| 2（只通过 Contracts 通信） | ✅ 本轮 |
| 3（Domain 不依赖任何东西） | ✅ 两层都验过 |
| 4（聚合版本号） | ✅ 空操作自增 → 行为测试红 |
| 5（禁止服务定位器） | ✅ 本轮 |
| 6（ID 不由环境态生成） | ✅ 两个方向 |
| 7（共享内核不长胖） | ✅ 本轮 |
| 8（宿主显式组装） | ✅ |

**八条全部反向验证过。** 每一张"某条不变量由某个测试守着"的声明，
现在都有"我打破过它，它红了"作为依据，而不是只有名字对得上。
