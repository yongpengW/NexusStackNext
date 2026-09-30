# 23 — 网关路由表模型与校验

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 01

> **2026-09-29 说明**：这是从票据 12（Gateway 真正的边缘）里切出的**可验证片段**。
> 票据 12 的其余部分（YARP 宿主、验签、限流、路由持久化）依赖认证形态（票据 10）与 broker，
> 而"路由配置长什么样、写错了会怎样"是纯逻辑，可以先做掉。

## Answer

**产出（3 个源码文件 + 27 个测试）**

| 文件 | 内容 |
|---|---|
| `src/Gateway/NexusStackNext.Gateway.Routing/RouteDefinition.cs` | `RouteDefinition` / `ClusterDefinition` / `DestinationDefinition` |
| `.../GatewayRouteTable.cs` | `RouteTableValidator` + `GatewayRouteTable`（校验通过才可构造）+ JSON 载入 |

**零 `PackageReference`**——序列化用 in-box 的 `System.Text.Json`。路由模型与 YARP 宿主解耦，
因此校验能在没有边缘进程的情况下被穷举验证。

**针对参照仓库三个有据可查的缺陷**

| 参照仓库 | 问题 | 这里的做法 |
|---|---|---|
| `ConvertConfig`（`DynamicProxyConfigProvider.cs:102-152`） | 转换配置时**丢掉** Transforms / HttpRequest / HttpClientConfig——配了转换却不生效，且无任何报错 | 字段显式建模；**JSON 往返测试**守住"不丢字段" |
| `LoadConfigAsync`（`:93-96`） | **吞掉异常**，控制器仍返回 `Ok`——"配置无效但 API 说成功" | 解析失败返回带原因的错误；有两条测试盯着 |
| 仓库里那份 `proxy-config.json` | 是**死文件**：没有任何 csproj 引用它，也没人校验路由指向的集群是否存在 | 校验**悬空引用**（`gateway.route.unknown_cluster`） |

**两个设计选择**

1. **问题一次列全，不是报第一个就返回。**
   改配置的人应该一次看到所有错，而不是修一个跑一次。参照仓库的风格恰好相反。

2. **`RequireAuthentication` 默认 `true`。**
   与 ADR-0010 一致：路由是边缘的公开面，忘记声明时的默认答案是"要认证"，
   公开端点必须**显式**写成 `false`。

**一个不显眼但会让断言失效的细节**

"不丢字段"这条断言**不能用 record 的相等性**——record 对集合成员比较的是**引用**而不是内容，
往返之后必然"不相等"，那样断言会在什么都没检查的情况下通过。
改用"序列化 → 反序列化 → 再序列化，两次文本必须相同"。

**测试抓到了我自己写的缺陷（值得记下来）**

第一版 `RouteTableValidator` 在路由标识为空时写了 `continue`——**后面的集群引用、路径、超时检查全被跳过**。
也就是说，我一边在注释里写着"要把问题一次列全"，一边写出了一个"只报第一个"的实现。
是 `Validate_ReportsEveryProblem_NotJustTheFirst` 把它抓出来的。改成"记下问题但继续检查"后修复。
集群那一处有同样的毛病，一并改了。

**反向验证（代码变异）**

把 JSON 解析异常的处理改回参照仓库的行为（吞掉、返回空表当成功）：

```
失败! - 失败: 2，通过: 25
  ✗ MalformedJson_FailsInsteadOfSilentlySucceeding
  ✗ MissingRequiredMember_Fails
```

两条精确命中。改动已还原。

**当前状态**：构建 0 警告 0 错误；测试 **306/306**；解决方案 **21 个项目**。

**仍未完成的部分（留在票据 12）**

YARP 宿主、边缘验签、限流、关联 ID、路由持久化（不放在 `AppContext.BaseDirectory`）、以及管理 API 的鉴权。
其中验签需要认证形态（票据 10）。
