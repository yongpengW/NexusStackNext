# 35 — 客户端 → 网关 → Identity 拓扑打通

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 34, 24

> 上一票让 Identity 端到端跑通。本票把它接到网关后面，验证**完整拓扑**——
> 仍然不需要 PostgreSQL，也不需要认证形态。

## 实跑结果

```
直连 Identity 创建用户          -> HTTP 201
经网关 GET  /api/identity       -> HTTP 200    （真的转发到了 Identity，带关联 ID）
经网关 POST /api/identity/users -> HTTP 401    （管理端点走认证策略）
经网关 GET  /api/platform/...   -> HTTP 200    （X-Gateway: nexusstack）
路由自述 /gateway/routes        -> 4 条路由，各自标出是否需要认证
```

**同一个端点：直连 201，经网关 401。** 这就是边缘在做它该做的事。

## 一个我拒绝走的捷径

要让演示"好看"，最省事的做法是把 Identity 的管理端点标成公开。
**那正是我批评了二十轮的 fail-open**——为了演示效果把保护关掉，然后演示"一切正常"。

所以路由表长这样：

| 路由 | 路径 | 认证 |
|---|---|---|
| `identity-info` | `/api/identity` | 公开（信息端点，本来就该公开） |
| `identity-management` | `/api/identity/{**catch-all}` | **要求认证** —— 未配置认证处理器 = 拒绝 |
| `platform-read` | `/api/platform/{**catch-all}` | 公开 |
| `files-api` | `/api/files/{**catch-all}` | 要求认证 |

对照之下，`identity-info` 能走通证明了**转发本身是好的**；
`identity-management` 返回 401 证明了**保护是开的**。两件事分开验证，不用互相将就。

## 一个必须说清的架构事实

**保护目前只存在于边缘。** 直连 `http://127.0.0.1:5186/api/identity/users` 是 201——
也就是说，**谁能直接访问到 Identity，谁就绕过了网关的认证**。

这不是本项目的缺陷，是微服务的固有前提：**服务不对外暴露，边缘是唯一入口。**
但它必须是一条**显式的部署不变量**，而不是一个假设——因为一旦某个服务被误配成可直连，
整套边缘保护就在无人察觉的情况下失效了。

参照仓库恰好提供了这个失效的样本：四个可部署单元共享一个 Core，
网关是唯一入口这句话从来没有被写下来过，也就没有人能检查它。

**记在这里，作为部署层的一条待办**：编排（compose / Aspire / K8s）里，
业务服务不得发布端口到宿主机，只有网关发布。

## 产出

- `src/Gateway/NexusStackNext.Gateway/routes.json`：新增 identity 集群与两条路由
- 实跑验证（见上）

## 当前状态

构建 0 警告 0 错误；测试 **349/349**。

## 下一步

仍然不需要外部输入的候选：
1. 把 Platform 也做成端到端（设置的读写 + 配置解析），验证这套模式**可复制**
2. 部署层的端口约束写进编排文件（等一个能跑容器的环境才能验证）
