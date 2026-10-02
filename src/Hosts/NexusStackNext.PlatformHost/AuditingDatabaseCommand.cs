using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using NexusStackNext.Auditing.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;

namespace NexusStackNext.PlatformHost;

/// <summary>独立的 Auditing 迁移命令，不启动 Web、配置中心或消息总线。</summary>
internal static class AuditingDatabaseCommand
{
    public static async Task<int> RunAsync()
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Auditing");
        if (string.IsNullOrWhiteSpace(connection))
        {
            Console.Error.WriteLine("Auditing migration requires ConnectionStrings__Auditing in the environment.");
            return 1;
        }

        try
        {
            var options = new DbContextOptionsBuilder<AuditingDbContext>()
                .UseNexusStackPostgres(connection, AuditingDbContext.SchemaName);
            await using var context = new AuditingDbContext(options.Options);
            await context.Database.MigrateAsync().ConfigureAwait(false);
            Console.WriteLine("Auditing migrations applied.");
            return 0;
        }
        catch (Exception error) when (error is DbException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine("Auditing migration failed. Check database access and migration compatibility.");
            return 1;
        }
    }
}
