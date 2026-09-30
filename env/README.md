# 本地环境变量

这里放**只属于你本机**的启动配置。两个宿主各一份。

```
env/
  platform.dev    真实值（已被 .gitignore 忽略、且被模板排除）
  gateway.dev     真实值
  README.md       本文件（提交、随模板分发）
```

**没有 `.example`/`.template` 样板文件。** 骨架由脚本生成：

```powershell
pwsh -File scripts\run-host.ps1 platform -Init
pwsh -File scripts\run-host.ps1 gateway  -Init
```

把骨架放进**脚本**而不是一个样板文件，是因为样板文件与填好值的文件**长得一模一样**——
而"长得一样"正是把密钥提交进仓库的常见成因。

## 为什么需要这个

两个宿主的 AgileConfig **AppId 不同**，而它必须在**各自的进程**里设。
在一个终端里设一次然后跑两个是不行的——第二个会读到第一个的 AppId，
然后去配置中心拉**别人的**配置。

## 怎么用

```powershell
pwsh -File scripts\run-host.ps1 platform -Init    # 生成 env/platform.dev
# 填入 Secret、把 Nodes 换成真实地址
pwsh -File scripts\run-host.ps1 platform          # 启动

pwsh -File scripts\run-host.ps1 gateway -Init
pwsh -File scripts\run-host.ps1 gateway
```

`run-host.ps1` 只把变量设进**它自己那个进程**，不污染你的终端。

## 文件格式

```
AgileConfig__AppId=nexusstack_platform
AgileConfig__Secret=<从后台应用列表的眼睛图标复制>
AgileConfig__Nodes=http://your-agileconfig:8010
```

`#` 开头是注释，空行忽略。**值里不要加引号**（不像 shell 那样需要）。

## 变量名里的双下划线

`.NET` 用 `__` 表示配置层级：

| 环境变量 | 对应的配置键 |
|---|---|
| `AgileConfig__AppId` | `AgileConfig:AppId` |
| `AgileConfig__Secret` | `AgileConfig:Secret` |
| `AgileConfig__Nodes` | `AgileConfig:Nodes` |
| `AgileConfig__Env` | `AgileConfig:Env` |
| `AgileConfig__Name` | `AgileConfig:Name` |
| `AgileConfig__Tag` | `AgileConfig:Tag` |
| `Files__StorageRoot` | `Files:StorageRoot` |

## `env/test.dev` —— 集成测试的连接串

**它不遵循双下划线约定**，因为它不是应用的配置键，而是**测试的输入**：

| 变量 | 用途 |
|---|---|
| `NEXUSSTACK_TEST_POSTGRES` | 集成测试连哪个 PostgreSQL |

**为什么不从配置中心读。** 测试最不该有的依赖就是配置中心——那会让"跑测试"变成
需要网络与凭据才能开始的动作，而测试恰恰是**在任何机器上都该能跑**的东西。

### 测试不会碰你的数据

每个测试用实例**建一个独立 schema**（`test_<随机>`），用完 `DROP … CASCADE`。
连接串带 `Search Path`，所以未限定的表名自动落进那个 schema，被测代码不需要知道自己在测试里。

**但仍请把它指向开发/测试库，不要指向生产。**

### 缺配置时会发生什么

| 场景 | 行为 |
|---|---|
| 直接 `dotnet test`，没设变量 | 集成测试**报成"已跳过"并带上原因**——新克隆的仓库不会因此变红 |
| `scripts/run-tests.ps1`，没设变量 | **直接失败**——项目自己的路径上不该悄悄跳过一整层测试 |

这两条是配合的：前者保证新克隆可用，后者保证我们**不会**在不知不觉中少跑一层。


## `AgileConfig__Env` —— 多环境

**你们的配置中心在用多环境，值是 `DEV`。** 不设它时客户端请求 `?env=`（空），
拉的是"无环境"那一份——**而且不会报错**。

日志里能看出实际用的环境：

```
[INF] 已接入 AgileConfig：AppId=nexusstack_gateway，Env=DEV，Name=（无），Tag=（无），节点=…
```

`Name` / `Tag` 为空是正常的（纯运维可见性，后台「客户端」页面靠它们显示）；
**`Env` 为空就要留意**。

## `AgileConfig__Name` / `__Tag` —— 运维可见性

AgileConfig 后台左侧有个「客户端」页面。不设这两个，你的宿主在那里显示不出有意义的信息。
示例：`AgileConfig__Name=NexusStackNext 平台宿主`、`AgileConfig__Tag=platform`。

## 其他可选键

`AgileConfig__Cache__Directory`、`AgileConfig__Cache__Encrypt`、
`AgileConfig__ServiceRegister__ServiceId` / `__ServiceName`（**两个都给齐才启用**）。

**注意：这些键不要写进 `appsettings.json` 并留空值。**
库看到 `serviceRegister` 节存在就会要求 `serviceName` 非空，
**空字符串比完全不写更糟**——网关曾因此直接崩溃。它们从环境变量走。


## 为什么不能写进 appsettings.json

写进去就是**把密钥提交进仓库**——参照仓库正是这么做的：
它的模板把生产环境的 AgileConfig 密钥、数据库口令、Redis 口令、阿里云 AK/SK
分发给了每一个用它新建项目的人（票据 18）。

本仓有三道闸，`env/*.dev` 三道都过得去（因为它是本地文件）：

1. `.gitignore` 里的 `env/*.dev`
2. 模板排除规则里的 `**/env/*.dev`
3. `scripts/assert-no-credentials.ps1` 断言**模板生成物里不存在任何 `env/*.dev`**

**第 3 条是关键**：前两条靠约定，第 3 条会失败。
它断言的不是"里面有没有凭据"（本地文件本来就不该被扫），而是**这个文件根本不能出现在生成物里**。

## 没配会怎样

应用**照常启动**（走降级路径，用 `appsettings.json` 与其它环境变量），
只是不接配置中心，并在日志里明说：

```
[INF] 未配置 AgileConfig（AgileConfig:AppId / Nodes 为空），使用 appsettings 与环境变量。这是正常的降级路径，不是错误。
```

**所以"忘了配"不会报错**——这既是好事也是陷阱。启动后记得看一眼有没有那行：

```
[INF] 已接入 AgileConfig：AppId=nexusstack_platform，节点=…
```

没有它，就是没接上。

### 残留的测试 schema 会自动清掉

每个测试用完就 `DROP … CASCADE`，**正常路径不泄漏**。
但进程被杀时那一步不会执行（CI 超时、Ctrl+C、构建失败后误用 `--no-build`），
于是会留下一个 `test_*` schema——而它**不会让任何测试变红**。

所以 schema 名里带了创建时刻（`test_<yyyyMMddHHmmss>_<8位>`），
而 `PostgresTestDatabase.CleanupStaleAsync` 会清掉过期的那些。
**它由一条测试驱动**（`StaleSchemas_AreCleanedUp_AndFreshOnesAreKept`）——
放在 `scripts/` 里、没人记得执行的清理脚本，就是下一个"不会失败的检查"。

名字对不上格式的 `test_*`（更早版本留下的）一律当过期，所以升级之后第一次跑测试就会顺手清干净。
