# 25 — Identity 宿主骨架

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 01, 06

> **2026-09-29 说明**：这是从票据 09（Identity API）切出的**结构性片段**。
> 票据 09 的端点实现依赖应用层与持久化（票据 08 → 07 → 19 → 15），
> 但"Identity 得先是个能独立启动的进程"不依赖任何外部条件。

## Comments

### local

**Answer**

**产出（2 个文件）**

- `src/Services/Identity/NexusStackNext.Identity.Api/NexusStackNext.Identity.Api.csproj`
- `src/Services/Identity/NexusStackNext.Identity.Api/Program.cs`

**为什么这值得单独做**

在此之前，五个上下文里**只有 Identity 没有宿主**——它只是一个类库。
票据 13 验证过"四个上下文各自能起来"，但那句话里没有 Identity。
一个没有宿主的上下文在结构上就是二等公民：它不能被部署、不能被健康探测、
也不能证明"这个上下文真的独立"。

**实跑验证**

```
Identity /health        -> HTTP 200
Identity /api/identity  -> HTTP 200
```

五个上下文现在**结构一致**：每个都是能独立启动、带健康检查、显式组装自己的进程。

**自述端点刻意不返回假数据**

它说明这个服务是什么、当前能做什么（领域就绪、授权就绪、端点待接入持久化），
而不是返回一个编造的示例响应。参照仓库的 README 声称支持三种数据库而实际只有一种——
**能跑的假象比空缺更难发现**。

**当前状态**：构建 0 警告 0 错误；测试 **317/317**；解决方案 **23 个项目**；
五个上下文各有一个宿主。
