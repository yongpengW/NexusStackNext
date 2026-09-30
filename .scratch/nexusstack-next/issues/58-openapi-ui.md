# 58 — OpenAPI 有文档、没有界面；补上 Scalar（仅开发环境）

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 51

> 用户问："现在可执行宿主有没有 web api 项目，集成了 swagger 没有"

## 现状（改之前）

**可执行宿主 2 个**（`Microsoft.NET.Sdk.Web`，产出 `.exe`）：

| 工程 | 是什么 |
|---|---|
| `src/Hosts/NexusStackNext.PlatformHost` | 五个平台能力的唯一进程 |
| `src/Gateway/NexusStackNext.Gateway` | YARP 边缘 |

**那五个 `*.Api` 已经不是 Web API 项目**——票据 51 合并之后它们是类库（`Microsoft.NET.Sdk`，无 `.exe`），
是五个模块的 HTTP 面。

**Swagger 没有集成**：有 OpenAPI **文档**（`/openapi/v1.json`，20 条路径），**没有可视化界面**。
全仓 `Swashbuckle` / `Scalar` 零引用。

**原因**：`Microsoft.AspNetCore.OpenApi` 从 .NET 9 起**只生成文档**，微软把 Swagger UI 从模板里移除了。
我当初在 ADR 里写的是"用内置 OpenAPI，不引入第三方；参照仓库用的是 Swashbuckle"——
**文档那一半做到了，界面那一半没有**，而我没在交付清单里说明这一点。

## 决定

| 项 | 选择 | 理由 |
|---|---|---|
| UI | **Scalar**（`Scalar.AspNetCore` 2.17.11） | 现代、活跃维护，微软文档现在推荐它。**MIT**，满足 ADR-0008 |
| 位置 | **仅 Development** | 见下 |

### 为什么只在开发环境开

部署不变量说"业务服务不对外暴露，边缘是唯一入口"。

**API 参考界面会暴露全部 API 面。** 挂在平台宿主上并总是开着，等于给 5191 端口加了一个
绕过边缘的信息出口——而**所有测试都不会发现这件事**（测试从不经过网络拓扑）。

开发时它是便利，生产里它是漏洞。所以用 `app.Environment.IsDevelopment()` 关起来：

```csharp
app.MapOpenApi();

if (app.Environment.IsDevelopment())
{
    app.MapScalarApiReference();
}
```

**OpenAPI 文档本身（`/openapi/v1.json`）仍然总是开着**——它是纯数据，
而"生产要看文档"属于网关聚合的范畴，不是这里的事。

## 验证：两个环境各测一次

| 环境 | `/openapi/v1.json` | `/scalar/v1` |
|---|---|---|
| **Development**（`dotnet run`） | 200 | **200** ✓ |
| **Production**（直接跑 DLL） | 200 | **404** ✓ |

**这条验证本身就有意义**：它同时确认了
① UI 真的挂上了；② **dev-only 的闸真的关得住**。
只测开发环境的话，第二种可能永远不会被发现。

## 顺带更正我之前的一处不准确表述

我在报告里说过"用 OpenAPI 替代了参照仓库的 Swashbuckle"。
准确说法是：**文档格式换了（内置 OpenAPI），但界面当时没有补上**。
现在补上了，且换成了 Scalar。
