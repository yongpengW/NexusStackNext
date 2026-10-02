using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace NexusStackNext.Files.Infrastructure.Persistence;

internal sealed class FilesDatabaseStartupCheck(IServiceScopeFactory scopes) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<FilesDbContext>();
            var pending = await context.Database.GetPendingMigrationsAsync(timeout.Token).ConfigureAwait(false);
            if (pending.Any())
            {
                throw new InvalidOperationException("Files 数据库需要迁移；先执行 migrate-files 命令。");
            }
            _ = await context.Files.AnyAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is DbException or OperationCanceledException or ArgumentException)
        {
            throw new InvalidOperationException("Files 数据库不可用；检查 ConnectionStrings:Files 与数据库权限。");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
