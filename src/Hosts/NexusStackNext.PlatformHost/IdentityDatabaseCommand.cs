using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Identity.Infrastructure.Persistence;

namespace NexusStackNext.PlatformHost;

/// <summary>部署时显式执行的 Identity 迁移；不启动 Web 宿主或外部服务。</summary>
internal static class IdentityDatabaseCommand
{
    public static async Task<int> RunAsync()
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Identity");
        if (string.IsNullOrWhiteSpace(connection))
        {
            Console.Error.WriteLine("Identity migration requires ConnectionStrings__Identity in the environment.");
            return 1;
        }

        try
        {
            var options = new DbContextOptionsBuilder<IdentityDbContext>()
                .UseNexusStackPostgres(connection, IdentityDbContext.SchemaName);
            await using var context = new IdentityDbContext(options.Options);
            await context.Database.MigrateAsync().ConfigureAwait(false);
            Console.WriteLine("Identity migrations applied.");
            return 0;
        }
        catch (Exception error) when (error is DbException or InvalidOperationException or ArgumentException)
        {
            // 不回显异常文本：连接配置与数据库异常可能包含凭据。
            Console.Error.WriteLine("Identity migration failed. Check database access and migration compatibility.");
            return 1;
        }
    }
}
