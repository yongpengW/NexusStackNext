# 41 — 五个服务 + 一个边缘：作为一个系统的验证

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 40, 35

> 前几票各自验证了单个上下文。本票把**五个服务与一个边缘放在一起**跑一次——
> 因为"每个都能跑"和"它们作为一个系统能跑"是不同的断言。

## 实跑结果

```
五个服务各自健康检查（不经边缘）
  Identity / Platform / Scheduling / Auditing / Files   /health -> HTTP 200
  Gateway                                               /health -> HTTP 200

边缘路由表（六条）
  platform-read          /api/platform/{**catch-all}      认证 False
  identity-info          /api/identity                    认证 False
  identity-management    /api/identity/{**catch-all}      认证 True
  scheduling-info        /api/scheduling                  认证 False
  scheduling-management  /api/scheduling/{**catch-all}    认证 True
  files-api              /api/files/{**catch-all}         认证 True

经边缘：公开路由
  GET /api/identity      -> 200（带关联 ID）
  GET /api/scheduling    -> 200
  GET /api/platform/...  -> 200（X-Gateway: nexusstack）

经边缘：管理路由（认证未配置 = 拒绝）
  POST /api/identity/users   -> 401
  GET  /api/scheduling/tasks -> 401
  GET  /api/files/1          -> 401
```

## 最有分量的一条：Auditing 刻意不在边缘上

```
POST /api/auditing/entries   经边缘 -> HTTP 404（没有这条路由）
                             直连   -> HTTP 202
```

**同一个端点，直连 202、经边缘 404。**

Auditing 是只写上下文：它的事件从**消息总线**进入，不是从互联网。
给它开一条边缘路由，等于**让任何人都能注入审计记录**——而审计的全部价值就在于它不可伪造。

所以路由表里没有它，而且这是**刻意的**，不是漏了。

**"是一个服务"与"在边缘上可达"是两件事。** 参照仓库里每个可部署单元都在同一个 Core 上，
"哪些该对外"从来没有被写下来过——因为写下来才发现，答案不是"全部"。

## 六条路由的分法

| 类别 | 路由 | 理由 |
|---|---|---|
| 公开 | `platform-read`、`identity-info`、`scheduling-info` | 配置读取与自述端点，本来就该公开 |
| 要求认证 | `identity-management`、`scheduling-management`、`files-api` | 用户、任务、文件——都需要知道"你是谁" |
| **不给路由** | Auditing | 事件从总线来，不经边缘 |

## 仍然只是边缘保护（承票据 35）

直连 `http://127.0.0.1:5186/api/identity/users` 仍然是 201——**谁能直连到服务，谁就绕过网关**。
这不是缺陷，是微服务的固有前提，但它必须是一条**显式的部署不变量**：
编排文件里业务服务不得发布端口，只有网关发布。已记为部署层待办。

## 一处编辑失误（编译器抓到的）

在 `/gateway/routes` 端点上面插注释时，我**多写了一个 `{`**（原文的左括号在下一行）。
八个编译错误一次冒出来。**这类错误编译器抓得住，所以它只值一次构建。**

## 产出

- `src/Gateway/NexusStackNext.Gateway/routes.json`：新增 scheduling 集群与两条路由；
  并写明为何**没有** auditing 的路由
- `Program.cs`：把"为什么没有 Auditing"写在路由自述端点的注释里

## 当前状态

构建 0 警告 0 错误；测试 **392/392**；六个进程全部实跑通过。
