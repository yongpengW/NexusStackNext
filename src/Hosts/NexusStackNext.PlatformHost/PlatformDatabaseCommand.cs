using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Platform.Infrastructure.Persistence;

namespace NexusStackNext.PlatformHost;

/// <summary>独立的 Platform 迁移命令，不启动 Web、配置中心或消息总线。</summary>
internal static class PlatformDatabaseCommand
{
    public static async Task<int> RunAsync()
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Platform");
        if (string.IsNullOrWhiteSpace(connection))
        {
            Console.Error.WriteLine("Platform migration requires ConnectionStrings__Platform in the environment.");
            return 1;
        }

        try
        {
            var options = new DbContextOptionsBuilder<PlatformDbContext>()
                .UseNexusStackPostgres(connection, PlatformDbContext.SchemaName);
            await using var context = new PlatformDbContext(options.Options);
            await context.Database.MigrateAsync().ConfigureAwait(false);
            Console.WriteLine("Platform migrations applied.");
            return 0;
        }
        catch (Exception error) when (error is DbException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine("Platform migration failed. Check database access and migration compatibility.");
            return 1;
        }
    }
}
