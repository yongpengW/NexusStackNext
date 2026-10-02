using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Scheduling.Infrastructure.Persistence;

namespace NexusStackNext.PlatformHost;

/// <summary>独立的 Scheduling 迁移命令，不启动 Web、配置中心或消息总线。</summary>
internal static class SchedulingDatabaseCommand
{
    public static async Task<int> RunAsync()
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Scheduling");
        if (string.IsNullOrWhiteSpace(connection))
        {
            Console.Error.WriteLine("Scheduling migration requires ConnectionStrings__Scheduling in the environment.");
            return 1;
        }

        try
        {
            var options = new DbContextOptionsBuilder<SchedulingDbContext>()
                .UseNexusStackPostgres(connection, SchedulingDbContext.SchemaName);
            await using var context = new SchedulingDbContext(options.Options);
            await context.Database.MigrateAsync().ConfigureAwait(false);
            Console.WriteLine("Scheduling migrations applied.");
            return 0;
        }
        catch (Exception error) when (error is DbException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine("Scheduling migration failed. Check database access and migration compatibility.");
            return 1;
        }
    }
}
