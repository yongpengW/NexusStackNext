using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.Identity.Application;

namespace NexusStackNext.Identity.Endpoints;

/// <summary>
/// 启动时按配置播种一个**内置根账号**（决定见票据 71，实现见票据 67）。
///
/// <para><b>它为什么存在。</b>`AccessPolicy` 里有一条 `IsRoot` 旁路，而它此前**不可达**：
/// `User.Register` 的 `isBuiltIn` 参数永远是 `false`，没有任何账号能走上去。
/// 于是权限链断在第一环——建不出菜单、授不出权限、所有受保护的端点永远 403。</para>
///
/// <para><b>幂等。</b>每次启动都跑，而"已经存在"时**什么都不做**——绝不用配置里的口令
/// 覆盖一个可能已经被人改过的账号。那是一条没人预期、事后也查不出来的行为。</para>
///
/// <para><b>配置缺失时安静跳过，配置非法时响亮失败。</b>这两件事不一样：
/// 没配 `Identity:Root:*` 是合法的（很多部署不需要根账号），记一条 Information 就够；
/// 而配了却建不出来（用户名非法、口令哈希失败）是**配置事故**，必须让宿主起不来——
/// 否则它会以"启动成功、但没有人能授权"的形态活着，而那正是这个仓库反复批判的失败模式。</para>
///
/// <para><b>口令从配置来，而不是"口令哈希"。</b>票据 71 的选项原文写的是哈希，但那不可行：
/// 本仓的口令哈希是 Pbkdf2 带**随机盐**，人手算不出可复现的值。播种时当场哈希，
/// 安全性质与"配置里放哈希"相同（哈希本身也是能登录的凭据），而前者是可操作的。
/// 这处偏离记在票据 71 里。</para>
///
/// <para>口令**绝不进日志**：日志里只有用户名与"做了什么"。</para>
/// </summary>
/// <param name="configuration">配置——`Identity:Root:UserName` 与 `Identity:Root:Password`。</param>
/// <param name="scopeFactory">作用域工厂——播种走的是一条完整用例（含事务），所以要开作用域。</param>
/// <param name="logger">日志。</param>
internal sealed partial class RootAccountSeeder(
    IConfiguration configuration,
    IServiceScopeFactory scopeFactory,
    ILogger<RootAccountSeeder> logger) : IHostedService
{
    /// <summary>根账号用户名的配置键。</summary>
    public const string UserNameKey = "Identity:Root:UserName";

    /// <summary>根账号口令的配置键。</summary>
    public const string PasswordKey = "Identity:Root:Password";

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var userName = configuration[UserNameKey];
        var password = configuration[PasswordKey];

        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
        {
            LogSkipped(logger, UserNameKey, PasswordKey);
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        // 走**用例**而不是在这里直接建聚合：幂等、口令哈希、`isBuiltIn` 三件事于是都能被
        // 单独测到（`SeedRootAccountHandler`），宿主这边只剩"读配置 + 发命令"。
        var result = await sender
            .SendAsync(new SeedRootAccountCommand(userName, password), cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            // **响亮失败。** 配了根账号却建不出来，是一个必须当场知道的事故。
            LogFailed(logger, result.Error.Code, result.Error.Message);

            throw new InvalidOperationException(
                $"根账号播种失败：{result.Error.Code} {result.Error.Message}");
        }

        if (result.Value)
        {
            LogSeeded(logger, userName);
        }
        else
        {
            LogAlreadyPresent(logger, userName);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "未配置 {UserNameKey} / {PasswordKey}，跳过根账号播种。受权限保护的端点需要先有人能授权。")]
    private static partial void LogSkipped(ILogger logger, string userNameKey, string passwordKey);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "根账号播种失败：{Code} {Message}")]
    private static partial void LogFailed(ILogger logger, string code, string message);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "已播种内置根账号 {UserName}。它走 IsRoot 旁路，权限判定对它不生效——"
            + "请在生产里轮换它的口令，并把它写进部署文档。")]
    private static partial void LogSeeded(ILogger logger, string userName);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "根账号 {UserName} 已存在，跳过播种（口令不动）。")]
    private static partial void LogAlreadyPresent(ILogger logger, string userName);
}
