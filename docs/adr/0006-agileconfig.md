# 保留 AgileConfig 作为配置中心

决定：继续用 AgileConfig 作为运行时配置中心，各服务启动时拉取配置并支持热更新。
Aspire 负责本地**编排**（见 0005），AgileConfig 负责**配置内容**，二者不互相替代。

## Considered Options

- **Consul / Nacos / K8s ConfigMap**：能力更强或更标准化，但既有资产与运维习惯在 AgileConfig，原 README 已有完整搭建指引。
- **纯 appsettings + 环境变量**：最简单，但失去热更新与集中管理——原项目已验证过的能力，不应在重做时倒退。
- **交给 Aspire 的配置注入**：省一个组件，但它只在本地开发成立，生产环境没有对应物。

## Consequences

- AgileConfig 成为本地开发的**强外部依赖**：AppHost 必须把它一并拉起，否则新克隆的仓库跑不起来。
- 敏感配置集中到 AgileConfig 之后，本地必须保留一条不依赖它的降级路径，否则离线开发不可用。
- `appsettings.*.json` 与 AgileConfig 的**优先级必须一次定清楚**并写进文档，否则"改了不生效"会成为长期困惑源。
