using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Infrastructure;
using NexusStackNext.BuildingBlocks.Infrastructure.Ids;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Infrastructure;

namespace NexusStackNext.Identity.IntegrationTests;

/// <summary>
/// **和宿主一样地组装 Identity**，供集成测试用。
///
/// <para><b>它存在的理由是一次真实的漏改。</b>三个测试类原先各自搭容器，
/// 内容九成相同。给 `LoginHandler` 加了一个 <c>IAccessTokenIssuer</c> 依赖之后，
/// 我只在其中一个里注册了实现——另外两个立刻红，因为
/// <c>ValidateOnBuild</c> 说得对：那两处容器**组装不出应用**。</para>
///
/// <para><c>ValidateOnBuild</c> + <c>ValidateScopes</c> 是有意开着的：
/// 它们让"容器组装不出来"在**测试里**就暴露，而不是等到宿主启动。
/// 但前提是**测试用的容器和宿主的形状一致**——三份各自演化的容器迟早会不一致，
/// 而那种不一致的表现就是"测试全绿、宿主起不来"。</para>
///
/// <para>所以组装这件事**只写一遍**。新增一个依赖时只改这里一处，
/// 而所有用它的测试都会立刻跟着验证。</para>
/// </summary>
internal static class IdentityTestHost
{
    /// <summary>测试用签名密钥——**够 32 字节**，否则签发会被拒绝。</summary>
    public const string SigningKey = "test-signing-key-that-is-long-enough-for-hs256";

    /// <summary>用 EF 存储组装一个与宿主同构的容器。</summary>
    /// <param name="connectionString">数据库连接串。</param>
    /// <returns>容器。</returns>
    public static ServiceProvider Build(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var services = new ServiceCollection();

        services.AddLogging();

        // 与 `PlatformHost` 的 `Program.cs` 保持同样的调用顺序与内容。
        services.AddNexusStackApplication();
        services.AddNexusStackInfrastructure(new IdGeneratorOptions { WorkerId = 1 });
        services.AddIdentityEntityFrameworkStorage(connectionString);
        services.AddIdentityUseCases();
        services.AddIdentityJwtIssuer(new JwtOptions { SigningKey = SigningKey });

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    /// <summary>
    /// **每条命令一个新作用域——与宿主一致。**
    ///
    /// <para>整个用例共用一个作用域时，<c>IdentityDbContext</c> 会被两次
    /// <c>SendAsync</c> 共用，处理器从**变更跟踪器**里读到上一条命令缓存的实体。
    /// 真实宿主每个请求一个作用域，所以那不是"测试跑得快一点"的区别——
    /// 是形状不一致，而它足以让一条本该失败的命令通过。</para>
    /// </summary>
    /// <typeparam name="T">结果类型。</typeparam>
    /// <param name="provider">容器。</param>
    /// <param name="action">要做的事。</param>
    /// <returns>结果。</returns>
    public static async Task<T> InScopeAsync<T>(ServiceProvider provider, Func<IServiceProvider, Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(action);

        await using var scope = provider.CreateAsyncScope();
        return await action(scope.ServiceProvider).ConfigureAwait(false);
    }
}
