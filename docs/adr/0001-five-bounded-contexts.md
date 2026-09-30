# 按限界上下文切分，而不是按技术分层

原 NexusStackBackend 按技术关注点分层（`Infrastructure` / `Domain` / `Host` / `BackgroundServices`），
而 `Domain/` 里装的其实是 Excel、Redis、RabbitMQ、Serilog 这类技术库。四个可部署单元全部引用同一个
`NexusStack.Core`，"我是哪个服务"由运行时枚举 `PlatformType` 表达（`Domain/NexusStack.Core/PlatformType.cs:81-99`）。
后果是网关被迫携带 EF Core、PostgreSQL、Aliyun OSS、SkiaSharp、FFmpeg 与 Excel，且跨服务误用能编译通过。

决定：新项目按**限界上下文**切分程序集——Identity / Platform / Scheduling / Auditing / Files。
每个上下文自成一个目录、自有一套 `Domain` / `Application` / `Infrastructure` / `Api`，
**不存在**被所有宿主引用的公共 Core。共享代码只有在第二个消费者出现后才允许上移到 `BuildingBlocks`。

## Considered Options

- **保留分层目录、只拆数据库**：改造成本最低，但"能不能跨服务误用"仍靠人的自觉，编译器帮不上忙。
- **模块化单体（一个进程、多个模块）**：边界更稳、事务更简单，但用户明确要的是真正的微服务。

## Consequences

- 每个上下文都要自带宿主装配，重复代码增加——这是刻意用重复换取边界的清晰。
- 原 `NexusStack.Core` 的 182 个文件要切开；其中 `UserContextCacheService.BuildFromDbAsync` 单方法 join 了 7 张表，是最难切的一处。
