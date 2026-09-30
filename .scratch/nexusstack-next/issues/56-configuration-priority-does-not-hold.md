# 56 — 【严重】配置优先级在真实宿主里不成立：环境变量盖不过配置中心

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 55

> 由兑现"优先级冲突验证"引出。**验证失败了**——环境变量没能覆盖配置中心。

## 缺陷

文档、ADR、以及 **4 条通过的测试**都在说：

```
环境变量  >  AgileConfig  >  appsettings.{Environment}.json  >  appsettings.json
```

**实际不是。** AgileConfig 盖过了环境变量。

## 根因：测的不是宿主真正用的那个类型

我实现重排的代码是：

```csharp
var sources = builder.Configuration.Sources;
var countBefore = sources.Count;
builder.Configuration.AddAgileConfig(...);
if (sources.Count > countBefore)
    InsertBeforeEnvironmentVariables(sources, sources[countBefore]);   // ← 不生效
```

`WebApplicationBuilder.Configuration` 是 **`ConfigurationManager`**，
对它 `Sources` 的**重排不生效**——插进去的来源仍然排在环境变量之后（即更高）。

而我那 4 条优先级测试用的是普通 `ConfigurationBuilder`，**它的 `Sources` 是真的列表**。
所以测试全绿，**而宿主里根本不是那么回事**。

**这是"验证方式与被验证方式不一致"的第二次出现**（第一次是票据 54 的 `dotnet run` vs `dotnet <dll>`）。
上一次是跑法不同，这一次是**测的类不同**。

## 影响

容器里 `RabbitMQ__HostName=x` 想覆盖配置中心的某一项——**会静默失败**。
运维会以为改生效了，而应用还在用配置中心的值。

## 修法：改用唯一受支持的手段——**追加**

`ConfigurationManager` 支持 `Add`，不支持重排。所以：

```csharp
builder.Configuration.AddAgileConfig(options => BindOptions(section, options));
builder.Configuration.AddEnvironmentVariables();   // 再追加一个环境变量来源 → 它最高
```

代价是环境变量被读两遍。那一遍几乎不花时间，而"容器里覆盖不了配置"是会真出事的那一类问题。

## 同时删掉了错的测试

- 删除 `ConfigurationPriorityTests.cs`（4 条）——它们测的是普通 `ConfigurationBuilder` 上的重排，
  **不是宿主用的机制**。留着它们比没有更糟：它们会让人以为这条契约被守住了。
- 删除 `InsertBeforeEnvironmentVariables` / `IndexOfEnvironmentVariables` 两个公开辅助方法——
  生产代码不再用它们，而且它们被证明在真实宿主里无效。
- 新增 `ConfigurationOrderingInRealHostTests.cs`（2 条）——**用 `WebApplication.CreateBuilder()`**，
  与宿主同款构建器。

**反向验证**：修之前，`InsertedSource_IsOverriddenByEnvironmentVariables_InTheRealHostBuilder`
是**红**的（那正是发现它的方式）；修之后 2/2 绿。

## 端到端验证

```
设 Files__StorageRoot = D:\LeoProject\DotNetProject\_priority_probe
  进程活着: True    /health/live -> 200
  环境变量指定的目录被创建: True      ← 修之前是 False
不设环境变量
  目录未创建（走配置中心/默认）: True
  配置中心仍然接通: True
```

## 一个无关但值得记的插曲

第一次重测时进程**崩了**，一度看起来像"修了但更糟"。实际原因是
`$env:TEMP` 返回的是 **8.3 短路径**（`C:\Users\ADMINI~1\...`），
而 `LocalDiskFileStore` 无法在那种形式下建目录——**与本次改动无关**。

换个普通路径就通过了。**"测试失败"和"修复失败"是两件事**，
而这一条只有把 stderr 读出来才能分清。

## 教训

本仓反复出现的三种形态，这次是第三种：

| # | 形态 | 例子 |
|---|---|---|
| 1 | **不会失败的检查** | 票据 44、45、53 |
| 2 | **照不到缺陷的验证方式** | 票据 54（`dotnet run` vs `dotnet <dll>`） |
| 3 | **测了错的类型** | 本条（`ConfigurationBuilder` vs `ConfigurationManager`） |

三者共同的解法只有一个：**让验证尽量贴近真实运行路径**。
不能贴近时，就要**明说它离了多远**。
