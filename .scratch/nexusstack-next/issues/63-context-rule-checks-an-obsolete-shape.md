# 63 — 上下文规范检查查着一个早已不存在的形状

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 52

> 把 `*.Api` 改名 `.Endpoints` 之后，`check-tracker.ps1` 报了 5 个问题：
>
> ```
> [上下文规范] Auditing 没有宿主项目（*.Api），它就不是一个可独立部署的服务
> ```

## 那条规则错了两处，而且**在改名之前就已经错了**

```powershell
$hostProject = @(Get-ChildItem -Recurse -File $dir -Filter '*.Api.csproj')
if ($hostProject.Count -eq 0) {
    Add-Problem '上下文规范' "$context 没有宿主项目（*.Api），它就不是一个可独立部署的服务"
}
```

| # | 它说的 | 实际 |
|---|---|---|
| 1 | 找 `*.Api` | 票据 52 之后是 `*.Endpoints` |
| 2 | 那是**宿主项目**、"**可独立部署的服务**" | **与 ADR-0013 直接矛盾**：五个平台能力由 `PlatformHost` **一个**宿主组装 |

第 2 条不是改名带来的——**票据 51 合并之后它就不成立了**。
规则一直没跟着改，于是它**静默地查着一个早已不存在的形状**：

- 五张票据以来它一直是绿的
- 而它绿的**理由**是：文件确实叫 `*.Api` ✓
- 至于那些项目是不是"可独立部署的服务"——**它从来没查过**，只是那么写着

这与票据 44、45、53、62 是同一族：**检查里写着一句没人验证过的断言**。
区别是这次那句断言**写在错误信息里**，而错误信息只在失败时才会被人读到——
所以它一直没机会被纠正，直到一个无关的改名把它翻出来。

## 修法

```powershell
# 每个上下文都要有自己的 **HTTP 面**（模块）。
# 注意它**不等于**"可独立部署的服务"：当前五个上下文都是平台能力，
# 由 src/Hosts/NexusStackNext.PlatformHost **一个**宿主组装（ADR-0013）。
$endpointsProject = @(Get-ChildItem -Recurse -File $dir -Filter '*.Endpoints.csproj')
if ($endpointsProject.Count -eq 0) {
    Add-Problem '上下文规范' "$context 没有 HTTP 面项目（*.Endpoints）——它的端点无处安放"
}
```

**"可独立部署"这个说法被删掉了，不是被改小了。** 当前它对五个上下文都不成立，
而未来业务上下文各自成服务时，那件事由 ADR-0013 管，不由这条文件存在性检查管。

## 验证

```
1. 修完再跑              → 跟踪器干净：62 张票据 …
2. 反向验证：把 Auditing 的 .Endpoints.csproj 临时移走
                        → [上下文规范] Auditing 没有 HTTP 面项目（*.Endpoints）——它的端点无处安放
                        → [解决方案项目] NexusStackNext.slnx 里有 … 但磁盘上没有这个文件
3. 还原                  → 跟踪器干净
```

第 2 步顺带证明了**两条规则会互相印证**：文件不见了，上下文规范与解决方案一致性各报一次，
从两个角度指向同一件事。

## 值得记的一句

一条检查的价值不在于它**通过**，而在于它**知道自己在查什么**。
这次暴露的不是漏查，而是**查的东西和写的东西不是一回事**——
而它绿着，所以没人会去看那句话。
