# 57 — AgileConfig 客户端的 `.serviceid` 会随模板分发

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 56

> 用户问"hosts 和 hosting 文件夹是干什么用的"。查证时顺手核对模板边界，
> 发现两个 `.serviceid` 文件**进了模板生成物**。

## 现象

模板生成物里有：

```
src\Gateway\<前缀>.Gateway\nexusstack_gateway.agileconfig.client.serviceid
src\Hosts\<前缀>.PlatformHost\nexusstack_platform.agileconfig.client.serviceid
```

32 字节，内容是一串 GUID。

## 为什么不敏感、但仍然要挡

它**不是凭据**——是 AgileConfig 给这个客户端实例的标识，写在一个 `<appid>.agileconfig.client.serviceid` 文件里。

但它仍然是**运行时产物**，不该随模板分发：

- 每个用模板新建的项目都从**同一个客户端 id** 开始，配置中心后台的「客户端」页面会把它们看成一个
- 它是本机第一次运行时生成的，与模板内容无关

**更要紧的是它揭示的规律**：AgileConfig 客户端会往**工作目录**里写文件。
今天它写的是 `.serviceid`（无害），而同一个机制写 `.cache`（**含口令**，5626 字节）。

**`.cache` 被 `**/*.cache` 正确挡住了**（本次核对确认）；`.serviceid` 只是**没进名单**。

## 修法

两道闸都补上：

| 闸 | 规则 |
|---|---|
| `.gitignore` | `*.serviceid` |
| 模板 `exclude` | `**/*.serviceid`（现 16 条） |

**干净验证**（只生成、不构建，避免 `bin/obj` 噪声）：

```
*.serviceid                       0（期望 0）
*.cache（排除 obj 下的 restore 产物）0（期望 0）
agileconfig 客户端产物              0（期望 0）
```

## 我又一次在缩进上栽了

第一次加规则时锚点写成 14 空格，实际是 **12**，`Replace` 静默无操作——
**和票据 53 那次一模一样**。

修法也一样：改用**不依赖缩进的正则**，并且**在写入之后**验证。

```powershell
$t=[regex]::Replace($t,'(?m)^(\s*)("\*\*/\*\.cache",)$','$1$2' + "`n" + '$1"**/*.serviceid",')
```

**同一个错误犯两次，说明"记住教训"是不够的**——第二次我开始用不依赖上下文的写法，
这才是能留下来的东西。

## 顺带值得你知道的一件事

AgileConfig 客户端把缓存写在**工作目录**下。用 `dotnet run` 时工作目录是**项目目录**，
所以现在这里躺着一份含真实口令的缓存：

```
src\Hosts\NexusStackNext.PlatformHost\nexusstack_platform.agileconfig.client.configs.cache
```

它**被忽略、也被模板排除**（实测 0 泄漏），但如果你希望它彻底离开仓库树，
可以用 `AgileConfig__Cache__Directory` 指到仓库外——那个键的映射在票据 55 已经做好了。
