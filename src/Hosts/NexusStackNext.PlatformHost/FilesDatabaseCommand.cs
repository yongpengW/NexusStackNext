using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Files.Infrastructure.Persistence;

namespace NexusStackNext.PlatformHost;

/// <summary>独立的 Files 迁移命令，不启动 Web、配置中心或消息总线。</summary>
internal static class FilesDatabaseCommand
{
    public static async Task<int> RunAsync()
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Files");
        if (string.IsNullOrWhiteSpace(connection))
        {
            Console.Error.WriteLine("Files migration requires ConnectionStrings__Files in the environment.");
            return 1;
        }

        try
        {
            var options = new DbContextOptionsBuilder<FilesDbContext>()
                .UseNexusStackPostgres(connection, FilesDbContext.SchemaName);
            await using var context = new FilesDbContext(options.Options);
            await context.Database.MigrateAsync().ConfigureAwait(false);
            Console.WriteLine("Files migrations applied.");
            return 0;
        }
        catch (Exception error) when (error is DbException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine("Files migration failed. Check database access and migration compatibility.");
            return 1;
        }
    }
}
