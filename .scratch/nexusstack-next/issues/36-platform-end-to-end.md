# 36 — Platform 端到端：验证这套模式可复制

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 34, 35

> 一条链路跑通可能是巧合，两条才说明它是个**模式**。本票把 Identity 那套三层结构
> 原样应用到 Platform，并做运行时验证。

## 实跑结果

```
PUT  identity.token.lifetime = 30m（直连）  -> HTTP 204
PUT  identity-temp.token     = 99m（直连）  -> HTTP 204
GET  经网关  identity.token.lifetime        -> HTTP 200  {"value":"30m", ...}
      X-Gateway 转换                        : nexusstack
GET  ?scope=identity（直连）                -> 只出现 identity.token.lifetime
                                               **没有**出现 identity-temp.token
GET  未知键（经网关）                        -> HTTP 200（value = null）
GET  非法键（经网关）                        -> HTTP 400
PUT  经网关（该路由只允许 GET）              -> HTTP 405
```

**"直连写、经网关读"这一条是核心**：它同时证明了转发、存储、以及两层之间的接线都对。

## 一条我预期错了的地方

我预期 `PUT` 经网关返回 **404**（"没有匹配的路由"）。实际是 **405**。

**405 才是对的**：路径匹配上了路由，是方法不被允许。YARP 在这一步给出的语义比我的预期准确——
它区分了"没这条路由"和"这条路由不接受这个方法"。

记下来，因为"预期 404"背后的想法是错的：我把边缘的方法限制理解成了"路由不存在"，
而它其实是"路由存在、此路不通"。

## 前缀匹配的坑，这次绕过去了

`SettingKey` 把键做成 **Scope + Name 两段**，不做字符串前缀匹配，因此
`scope=identity` 不会命中 `identity-temp`。

这不是假想的风险：参照仓库的 `MenuService` 用 `LIKE '%{parentId}%'` 犯的正是同一类错
——父节点 `1` 会命中 `12`、`21` 的路径。那条在 review/02 里有据可查。

## 按自己写下的约定做了

`Contexts.Tests.csproj` 里有一句注释：

> 骨架阶段把四个上下文的测试放在一起……当某个上下文开始有真实业务时，
> 它应当拆出独立的测试工程——这条迁移路径写在票据 13 的结案里。

Platform 现在有真实业务了（`SettingStore`），所以按约定拆出了
`tests/Platform.Application.Tests`。**约定写了就要走，否则它只是装饰。**

## 产出

| 文件 | 内容 |
|---|---|
| `Platform.Application/SettingStore.cs` | 端口 + 读写服务：**不存在就创建**、同值不发事件 |
| `Platform.Infrastructure/InMemorySettingRepository.cs` | 内存适配器（按两段键，不做前缀匹配） |
| `Platform.Api/Program.cs` | 四个端点：读 / 写 / 清空 / 按分组列出 |
| `tests/Platform.Application.Tests/SettingStoreTests.cs` | 11 条测试 |

## 模式确认可复制

| | Identity | Platform |
|---|---|---|
| Domain | ✓ | ✓ |
| Application（端口 + 服务） | ✓ | ✓ |
| Infrastructure（内存适配器） | ✓ | ✓ |
| Api（端点） | ✓ | ✓ |
| 独立测试工程 | ✓ | ✓ |
| **运行时端到端** | ✓（票据 34） | ✓（本票） |

**两处都不是"照抄"**：Identity 的深模块是四段链路的权限投影，
Platform 的深模块是"不存在就创建 + 同值不发事件"的写入语义。
同一个骨架装不同的东西——这才是"可复制"的意思。

## 当前状态

构建 0 警告 0 错误；测试 **360/360**；解决方案 **29 个项目**。
