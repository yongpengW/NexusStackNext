# 61 — 界面从 Scalar 换成 Swagger UI

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 60

> 用户："现在这个 scalar api 页面太丑了，不如 swagger ui 好看啊"

**界面好看与否是使用者的判断，不是实现者的。** 换。

## 换法：只换 UI，不动文档生成

`Swashbuckle.AspNetCore.SwaggerUI` 是一个**独立包**——它只提供界面，
可以指向**任意** OpenAPI JSON 端点。所以聚合逻辑（票据 59）**一行都没改**。

| | 之前 | 现在 |
|---|---|---|
| 文档生成 | `Microsoft.AspNetCore.OpenApi` | **不变** |
| 界面 | `Scalar.AspNetCore` 2.17.11 | **`Swashbuckle.AspNetCore.SwaggerUI` 10.2.3** |
| 路由 | `/scalar/v1` | `/swagger` |

`Swashbuckle.AspNetCore` 那个元包会连带引入 `SwaggerGen`（自己生成文档）——
我们**不要**它：文档来源只能有一处，否则界面上看到的和聚合出来的是两份东西。

**许可证**：MIT ✓（ADR-0008 要求，已在 nuget 上核实）。

## 接线

```csharp
// 网关：指向聚合文档，生产也开
app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "NexusStackNext 边缘聚合文档"));

// 平台宿主：指向自己的文档，仅 Development
if (app.Environment.IsDevelopment())
{
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "平台宿主"));
}
```

## 验证

```
A. Production
   网关 /swagger            -> 200   标题 "Swagger UI"，页面含 swagger-ui
   网关 /swagger/index.html -> 200
   平台宿主 /swagger        -> 404   ← 仅 Development 的闸仍然关得住
   网关 /health/ready       -> 200   /api/identity -> 200（边缘转发未受影响）

B. Development
   平台宿主 /swagger -> 200
```

## 追错的一条线索（记下来，因为它很像真问题）

我抓了 `/swagger/swagger-initializer.js`，里面写着：

```js
url: "https://petstore.swagger.io/v2/swagger.json",
```

**看起来像配置没生效**——界面指向了 petstore 示例。

于是去查 `index.html`，发现它只有 735 字节，加载的是 **`index.js`**，而 `index.js` 里是：

```js
var configObject = JSON.parse('{"urls":[{"url":"/openapi/v1.json","name":"NexusStackNext 边缘聚合文档"}],...}');
```

**配置是对的。** `swagger-initializer.js` 属于包里**没被这套 HTML 引用**的遗留文件——
它自己的注释就写着 "the following lines will be replaced by docker/configurator, when it runs in a docker-container"。

**教训**：一个文件里写着"错误的值"，不等于那个文件**被用到**了。
先确认它是否在生效路径上，再据此判断。否则会去"修"一个根本没被读的地方。

## 顺带清理

- `Directory.Packages.props` 与两个 `csproj` 里的 Scalar 引用已移除，全仓**产品代码与文档零残留**。
- `.scratch/` 里票据 58、59 提到 Scalar 是**历史记录，应当保留**——
  它们记述的是当时的决定与理由，票据 61 记述的是改主意的理由。改历史会让跟踪器失去意义。
- `launchSettings.json` 的 `launchUrl` 从 `scalar/v1` 改为 `swagger`。
