# Identity 持久化运行

Identity 默认使用 PostgreSQL，数据与迁移历史均在 `identity` schema。
同一宿主中的 Platform 也需独立连接配置与迁移，见 [全局设置持久化](platform-settings.md)。
现有 EF 仓储由实际平台宿主装配；其余平台模块仍有内存状态，本轮不是整个平台的生产验收。

## 配置与启动

在私有 `env/platform.dev` 或部署环境配置 `ConnectionStrings__Identity`，值不要进入仓库、命令参数或日志。
宿主也可从配置中心的 `ConnectionStrings:Identity` 读取；迁移命令仅从环境读取，不依赖配置中心。
JWT 密钥与根账号仍按 [环境配置](../env/README.md) 提供。

```powershell
# 本机读取 env/platform.dev；先迁移，再启动。
pwsh -File scripts/migrate-identity.ps1
pwsh -File scripts/run-host.ps1 platform
```

发布后的迁移入口：`dotnet NexusStackNext.PlatformHost.dll migrate-identity`。
它不启动 HTTP、RabbitMQ、配置中心或根账号播种；成功退出码为 0，失败非 0。
重复运行只应用尚未执行的迁移。普通 HTTP 启动仅检查迁移齐全及用户表可读，10 秒内检查失败就退出。
运行期 `/health/ready` 检查数据库连通性；数据库掉线返回 503，`/health/live` 不查数据库。

部署先备份并检查迁移脚本，使用有 schema 修改权限的部署账号运行迁移；运行账号只给必需的数据访问权限。
本轮迁移为用户和刷新令牌增加会话版本列，不删除现有数据。旧内存模式的数据不会自动导入。
生成审阅用 SQL 可使用与项目相同版本的 EF 工具；设计时工厂使用无凭据占位配置，不连接数据库。
迁移流程依据 [EF Core 官方迁移文档](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying)。

## 开发与测试

无库演示须显式选择 `Identity__Storage__Provider=Memory` 和 `Platform__Storage__Provider=Memory`，仅 `Development` / `Testing` 环境允许。
未指定或空白 Provider 使用 Postgres；拼错值直接失败。三个手动 HTTP 验证脚本已显式选择开发内存模式。
Aspire 将 `NEXUSSTACK_DB` 分别注入 `ConnectionStrings__Identity` / `ConnectionStrings__Platform`，启动前仍需单独完成两项迁移。
AppHost 启动脚本只接受明确的运行库配置，不再把 `NEXUSSTACK_TEST_POSTGRES` 隐式用作应用数据库。

新增真实 HTTP 测试使用 `NEXUSSTACK_TEST_POSTGRES` 所在服务器上的临时数据库（`nsn_identity_journey_*`），
需要测试账号具备建库、删库权限。每条旅程串行运行，结束后删除自己创建的数据库；断线测试只关闭该临时库的连接。
全量仍使用 `scripts/run-tests.ps1`，不并行执行解决方案测试。

## 安全状态与当前限制

会话版本由 User 持有；访问令牌和刷新令牌都记录签发时版本，重启后旧版本继续被拒绝。
登出及重放撤销只修改 User 聚合；旧版本重放不会再次撤销后来重新登录的新会话。
权限缓存仍在本进程内，在提交后失效；跨实例权限失效和其他上下文的撤销校验另行设计。
每个 Identity 受保护请求会查询用户的当前安全状态，数据库失败时不会接受旧的缓存结论。
已有令牌流程的跨聚合事务建模债务、固定 WorkerId 以及提交结果不确定时的命令幂等仍未解决。
此轮支持单实例持久化运行，不宣称已经具备多副本高可用。
