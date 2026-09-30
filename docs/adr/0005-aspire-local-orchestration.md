# 用 .NET Aspire 编排应用进程，中间件走外部依赖

决定：新增 `aspire/NexusStackNext.AppHost`，用 .NET Aspire 一键拉起 5 个服务与网关。
PostgreSQL、Redis、RabbitMQ、Seq、AgileConfig **不由 Aspire 拉起**，而是作为外部依赖，
由配置提供连接信息。Aspire **只用于本地开发与集成测试**，生产仍走容器编排。

## Status

accepted（2026-09-29 修订。原方案设想由 Aspire 托管中间件容器，但本机**没有任何容器运行时**——
Docker / Podman / WSL 均未安装，也没有本机的 PostgreSQL / Redis / RabbitMQ，故改为"应用进程编排 + 外部中间件"。）

## Considered Options

- **手工 docker-compose / 由 Aspire 托管容器**：都需要容器运行时，本机不具备。
- **本机原生安装 PostgreSQL + Memurai + RabbitMQ(Erlang)**：可行，但运维成本高，且与生产形态偏离更远。

## Consequences

- 本地开发不再"一条命令起全栈"，前提是有一组可用的中间件环境（本地或远端）。这反而让本地与生产的连接方式一致。
- 集成测试**无法使用 Testcontainers**（同样需要容器运行时），必须针对一个真实可用的测试库运行，
  或把持久化适配器在测试中整体替换掉。测试策略必须显式选定其中一条。
- 连接信息一律走配置 / 环境变量，**不进仓库**——这也是密钥不再被打进模板包的前提。
