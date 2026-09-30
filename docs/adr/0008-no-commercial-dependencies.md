# 不引入需要商业授权的依赖

原项目用 `Z.EntityFramework.Extensions.EFCore`（商业授权）做批量写入与软删除，
而这条路径**绕过了审计拦截器**——审计位点在 `SaveChangesInterceptor`，批量操作不走 `SaveChanges`。
也就是说：一条本该被审计的批量删除，在审计表里不留任何痕迹。

决定：新项目不引入任何需要付费授权的库。批量写入、软删除、审计一律走 EF Core 原生能力与我们自己的拦截器，
保证"**所有写入都经过审计**"这条不变量无法被绕过。

## Considered Options

- **保留商业库**：性能好、API 顺手，但需要授权，且它绕开审计这一点与项目的核心诉求直接冲突。
- **`ExecuteUpdate` / `ExecuteDelete`**：EF Core 原生，但同样不走 `SaveChanges`——采用它时必须在同一事务里显式补审计记录。

## Consequences

- 大批量写入的性能不如商业库，需要时才用原生批量 API，并配套显式的审计补偿。
- 依赖清单里不再出现授权不明的包，模板可以放心分发。
