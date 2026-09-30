# 这份诊断报告是什么，以及它后来怎么结的

`acl-report-<hash>.jsonl` 是 `diagnose-windows-sandbox-acl` 技能在 **2026-09-29** 留下的一次**只读**诊断，
对象是 `C:\Users\Administrator\aspire-tmp`。起因：Aspire AppHost 起不来，报
`Directory.CreateTempSubdirectory` **拒绝访问**（票据 14 的"阻塞 4"）。

**用户决定保留它**（2026-09-30）。

## 它的结论

```
decision: classify  →  NOT_THIS_CLASS
reason:   No package allow ACE was observed and both required rights are available.
          Other ACL restrictions and causes of the original failure are not ruled out.
mode:     只读（"No mutation mode was requested"）——它没有改任何 ACL
```

**"不是 ACL 这一类"** —— 它没看错，问题只是不在它负责的那一类里。
而 `reason` 的后半句"**其他原因没有被排除**"，正是后来被忽略掉的那半句。

（报告里出现的 `D:\LeoProject\DotNetProject\NexusStackNext` 是**迁移前的副本路径**：
这次诊断发生在仓库搬到现在这个位置之前。那个目录——连同整个 `D:\LeoProject\DotNetProject\`——
已在 **2026-09-30 经用户确认删除**（318 MB，其中 309 MB 是 `bin/obj`）；删前核过它不是 git 仓库、
源文件是当前仓库的子集。所以上面那条路径现在**只是这份原始记录里的历史**，磁盘上不存在了。）

## 后来怎么了（2026-09-30，票据 14 第 18 轮）

1. **那个 API 现在跑得通。** 在 .NET 10 宿主里当场复现（临时控制台工程，同一个 `TEMP`）：
   `CreateTempSubdirectory → 成功`。所以当初的失败是**运行上下文**造成的——
   最可能是会话的**文件沙箱**（它拒绝工作区之外的写入），而"沙箱拒绝"与"ACL 拒绝"
   在错误消息上长得一样；这也正好解释了它为什么给 `NOT_THIS_CLASS`。
2. **真正的卡点在代码里，不在机器上。** AppHost `Require("NEXUSSTACK_REDIS")`，
   而**本仓不用 Redis**（`src/` 里 "Redis" 只出现在注释里、`Directory.Packages.props` 没有客户端包、
   两个宿主的 `appsettings.json` 里连 `Redis` 节都没有），并且那个值还被注成
   `Redis__Configuration`——**没有任何东西读它**；加上启动脚本只读 `env/platform.dev`，
   而库连接串住在 `env/test.dev` 里。两处都已修。
3. **验收 1 实测通过**：三个进程、5190/5191 双 `200`、面板 `200`、OTLP 在听；
   跑完按名字清掉进程、五个端口全部释放。**票据 14 → `resolved`。**

## 为什么留着它

它同时是两样东西：一次**正确的 "N/A 判定"**（不是 ACL，就没动 ACL），
以及一次"**结论被读得比证据更宽**"的样本——`NOT_THIS_CLASS` 说的是"不在这一类里"，
当时被读成了"**这台机器做不到，换台机器才能验**"，于是那处本来能在代码里修的问题白停了一轮。

细节与四条验收的实测记录在票据 14 的第 18 轮；机制层的教训（"换个环境再试时要问换掉了什么"）
写在 `AGENTS.md` 的「写检查与做验证的纪律」那一族的同源位置。
