# 单机容器入口

四个镜像目标分别运行 PlatformHost、CostingHost、PricingHost 与 Gateway。这里只给出可验证的单机起点；生产环境、TLS 终止、依赖账号、备份和多机部署仍按实际环境确定。

Compose 仅发布 Gateway 的 `127.0.0.1:5190`，三个后端只在容器网络可达。主机上的反向代理可转发到该入口。容器使用 .NET 镜像的非 root 用户；Files 的 `/data/files` 使用命名卷，重建容器保留文件。`docker compose down --volumes` 会删除该卷，日常停止只使用 `down`。

## 配置与启动

在仓库根目录的 `env/` 创建四份私有文件：`platform.container.dev`、`costing.container.dev`、`pricing.container.dev`、`gateway.container.dev`。它们沿用 [环境配置格式](../env/README.md)，但由 Compose 读取；只写入该宿主需要的设置。不要直接复用带回环数据库地址的本机配置：容器内回环指向自身。

| 宿主 | 必需配置 |
| --- | --- |
| 全部 | 同一组 `Jwt__SigningKey`（至少32字节）、Issuer/Audience；各自的 `ConnectionStrings__OperationJournal` |
| Platform | `ConnectionStrings__Identity` / `Platform` / `Files` / `Auditing` / `Scheduling`，五个上下文使用同一平台库的各自 schema；首次设置 `Identity__Root__UserName` / `Password` |
| Costing | `ConnectionStrings__Costing`，独立成本库 |
| Pricing | `ConnectionStrings__Pricing`，独立定价库 |

网关操作日志使用自己的库。数据库与日志各模块默认 PostgreSQL；新库先创建，再由下面的显式 CLI 初始化。数据库用户与权限按环境配置，本样例不创建服务器、不授予生产权限。根账户完成初始化后从文件中移除初始口令，再重建平台容器；账户继续保留。

需要异步跨服务协作时，在对应私有文件配置 RabbitMQ，并启用 `Costing__Messaging__Enabled` / `Pricing__Messaging__Enabled`；Redis 缓存、Webhook 和服务间成果发布按各模块文档配置。样例没有内置公司地址、默认口令或生产证书。配置中心可以照常使用，应用设置优先级保持环境变量最高。

从仓库根目录执行：

```powershell
pwsh -File scripts/prepare-container-routes.ps1
docker compose -f deployment/compose.yaml build

# 独立迁移；任一失败就停止，不继续启动。
$platformCommands = @('migrate-identity', 'migrate-platform', 'migrate-files', 'migrate-auditing', 'migrate-scheduling', 'migrate-operation-journal')
foreach ($command in $platformCommands) {
    docker compose -f deployment/compose.yaml run --rm --no-deps platform $command
    if ($LASTEXITCODE -ne 0) { throw 'Platform migration failed.' }
}
foreach ($service in @('costing', 'pricing')) {
    foreach ($command in @("migrate-$service", 'migrate-operation-journal')) {
        docker compose -f deployment/compose.yaml run --rm --no-deps $service $command
        if ($LASTEXITCODE -ne 0) { throw 'Business migration failed.' }
    }
}
docker compose -f deployment/compose.yaml run --rm --no-deps gateway migrate-operation-journal
if ($LASTEXITCODE -ne 0) { throw 'Gateway journal migration failed.' }
docker compose -f deployment/compose.yaml up -d --no-build
```

路由策略来自现有 `routes.business.json`，准备脚本只替换三个 cluster 的地址为容器 DNS，并把结果写入被忽略的 `env/container.routes.dev`。修改规范路由后重新运行脚本，再重建网关容器。`/health/ready` 验证业务依赖与后端可达性，`/health/logging` 诊断日志链路；详细故障边界见各模块文档。

## 生成工程的验收

Linux CI 在全新改名模板目录运行 `scripts/verify-container-deployment.ps1`：构建四个实际镜像，启动专属 PostgreSQL，创建四个空库，执行十一条迁移命令，再经唯一宿主端口完成就绪、登录、Costing 查询和 Pricing 任务处理。检查实际运行容器的端口绑定及非 root 身份；先用一次真实的 Costing 端口发布验证拒绝，再还原后执行正常旅程。文件目录的可写/可删由平台 readiness 验证。

该脚本拒绝覆盖已有容器配置，不读取 `env/test.dev`。运行专属数据库没有宿主端口；随机测试密钥仅写入临时私有文件，结束后清理该项目的容器、卷和文件。完整 MQ/Redis 故障与业务协作仍由原有测试负责，本旅程只验部署入口及持久化最小运行。验收通过前不声明模板整票完成。

Compose 的端口和服务网络语义参见 [Docker 官方服务配置](https://docs.docker.com/reference/compose-file/services/)，非 root 用户参见 [Microsoft 容器文档](https://learn.microsoft.com/en-us/dotnet/core/docker/)。
