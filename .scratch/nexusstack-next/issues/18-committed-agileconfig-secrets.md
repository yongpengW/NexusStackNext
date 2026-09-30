# 18 — 【严重】appsettings.*.json 里提交了 AgileConfig secret，且已推送到远端

Status: resolved
Type: task
Labels: wontfix
Blocked by: —

> 本票**不授权**修改参照仓库。它记录一个需要你决策的暴露面。四个评审 subagent 都没发现这一条
> ——它们查的是 `agile/*.cache`（未跟踪），而这条在 **git 跟踪范围内**。

## 事实（已核实）

**1. 被 git 跟踪、已推送到 `origin/dev`**

```
git ls-files → BackgroundServices/NexusStack.MQService/appsettings.Development.json
               Host/NexusStack.Gateway/appsettings.Development.json
               Host/NexusStack.WebAPI/appsettings.Development.json
git log --diff-filter=A → bc7244c 2026-02-25 Leo Wang（初始提交）
git branch -r --contains HEAD → origin/dev
```

**2. 14 个 appsettings 文件里含非空的 AgileConfig `secret` 与 `nodes`**

| 文件 | secret 长度 | nodes |
|---|---|---|
| `Host/NexusStack.WebAPI/appsettings.Development.json` | 8 | 已设置（`<服务器IP>`，公网） |
| `Host/NexusStack.WebAPI/appsettings.Production.json` | 8 | 已设置 |
| `Host/NexusStack.WebAPI/appsettings.Staging.json` | 16 | 已设置 |
| `Host/NexusStack.WebAPI/appsettings.Test.json` | 8 | 已设置 |
| `BackgroundServices/NexusStack.MQService/appsettings.{Development,Production,Staging,Test}.json` | 8/8/16/16 | 3 个已设置 |
| `BackgroundServices/NexusStack.PlanTaskService/appsettings.{Development,Production,Staging,Test}.json` | 8/8/16/16 | 3 个已设置 |
| `Host/NexusStack.Gateway/appsettings.Development.json` | 16 | 空 |

**3. 这与票据 16 是两回事**

- `agile/*.cache`：**未跟踪** → 从未泄露，只是会被打进模板包。
- `appsettings.*.json`：**已跟踪且已推送** → 这是真正的提交记录里的凭据。

**4. 直接影响模板使用者**

每个 `dotnet new nexusstack` 出来的工程都自带这些 secret，且默认指向你的 AgileConfig 服务器
（`<服务器IP>`）。这不只是安全问题，也是**功能问题**：使用者拿到的是一个指向别人基础设施的配置。

## 为什么严重

- AgileConfig 的 `secret` 是**应用级凭据**，能拉取该应用的全部配置——而配置中心里存着
  数据库连接串、Redis 口令、RabbitMQ 口令、阿里云 AK/SK（这正是 `agile/*.cache` 的内容来源）。
  即：**拿到 secret ⇒ 有机会拿到上面全部**。
- 已存在 7 个月（2026-02-25 至今），且已推送。
- 仓库可见性**未能确认**（本机网络策略禁止访问 github.com）。在确认是私有仓库之前，应按"已公开"处理。

## 需要你决策（三选一或组合）

1. **最小动作**：把 `appsettings.*.json` 里的 `AgileConfig.secret` / `nodes` 改成占位符，
   真实值移到用户机密 / 环境变量 / AgileConfig 自身；**并轮换全部 AgileConfig secret**。
2. **加一步**：同时轮换 AgileConfig 里存着的下游凭据（PG / Redis / RabbitMQ 口令、阿里云 AK/SK）。
3. **彻底**：改写 git 历史抹掉这些值（`git filter-repo`）。注意这会重写所有 commit hash，
   且**如果仓库是公开的，历史可能已被抓取，改写不能替代轮换**。

## 建议

先做 1 + 2（轮换是唯一能真正止损的动作），历史改写看你是否在意。

## 与本项目的关系

新项目（NexusStackNext）从第一天就按 ADR-0005 的结论走：**连接信息一律走配置/环境变量，不进仓库**，
模板里只留占位符。票据 01 的 `.gitignore` 与票据 17 的模板打包断言会把这条锁死。

## Comments

### local

**第 13 轮：决定与一条**扩大范围**的发现**

#### 你的决定：轮换 secret

（票据原文写着"本票**不授权**修改参照仓库"——你在第 13 轮明确选了轮换，那就是授权。）

#### 发现：那个 secret 被**多个 app 复用**，包括我在用的

核实结果：

```
参照仓库里含非空 secret 的文件     13 个
其中与**我正在用的那个**相同的      7 个
我的 secret 长度                   8
涉及 appId                        nexusstack_mq、nexusstack_plantask、…
```

**我这边 `env/platform.dev` 的 `AgileConfig__Secret` 与泄漏值逐字相同。**
（同一个 8 字符串被配到了多个 app 上——这本身是另一处该改的实践：
一个 app 一个 secret，泄漏一个只轮换一个。）

所以这次轮换**不是改 14 个文件那么简单**，它是一条跨应用、跨仓库的协调改动，
而且**会打断我当前的配置中心连接**。

#### 轮换的完整步骤（前两步需要你）

**一、在 AgileConfig 后台为每个受影响的 app 重新生成 secret。**
受影响的是所有用那个 8 字符串的 app（至少 `nexusstack_mq`、`nexusstack_plantask`、
`nexusstack_platform`，共 7 处配置引用它）。
**顺便建议**：不要再让多个 app 共用一个 secret。

**二、把新 secret 给我**（或者你自己改下面这些位置）：

| 位置 | 改什么 |
|---|---|
| 参照仓库 13 个 `appsettings.*.json` | 把 `secret` 换成新值（**这一步我可以做**，只要你确认改参照仓库） |
| 本仓 `env/platform.dev` | `AgileConfig__Secret`（已 gitignore、不进模板） |
| 任何别的引用点 | 用 `grep -r '<旧值>' ` 扫一遍 |

**三、验证**：`env/test.dev` 的连接仍然可用（配置中心读得到），且全量测试仍绿。

#### 为什么现在是 `needs-info`

**不是被技术挡住，是被"新 secret 的值"挡住**——而那个值只能由你在 AgileConfig 后台生成。
旧值留在 git 历史里这件事，**轮换之后就不再重要了**（它已经无效）。

**附加的一条**：`assert-no-credentials.ps1` 已经守着**模板生成物**，
所以本仓的新代码不会再犯同样的错。参照仓库那次泄漏发生在模板机制之前。

## Comments

### local

**决定：接受（wontfix）——2026-09-30**

用户已明确：`env/*.dev` 指向的那台测试服务器是**可随时重置的虚拟机**，所以 VM 上的凭据暴露
**不构成风险**；轮换一个马上要被重置的环境里的口令没有意义。

因此本票按 `wontfix` 结掉：**事实保留、处置决定是不处置**，并把理由写在这里，
而不是把票删掉——删掉会让下一个人重新发现同一件事，并把"已经决定过"读成"没人管过"。

**这一票的结论不覆盖另一件事**：参照仓库缓存里那个阿里云 AK（`LTAI…` + `AccessKeySecret`）
**不是虚拟机凭据**——重置 VM 不会吊销它，而且它能开**计费**资源。
那件事的建议仍是**你去 RAM 控制台轮换**，本仓不做任何验证动作（不去试它是否有效，因为那会产生计费资源）。

**本仓侧已经成立的防线**（与这票的决定无关，独立有效）：

- `scripts/assert-no-credentials.ps1`：331 个文件、8 条规则、0 命中——它守的是**模板生成物**；
- `env/*.dev` 在 `.gitignore` 里（`env/*.dev`），模板不携带任何凭据；
- 参照仓库的密钥值在本仓与源目录里都已经不存在（脱敏时扫过）。
