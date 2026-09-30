using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.IntegrationSupport;
using Npgsql;

namespace NexusStackNext.BuildingBlocks.Infrastructure.IntegrationTests;

/// <summary>
/// <see cref="Application.Transactions.IUnitOfWork"/> 的 EF 实现，在真库上验证。
/// </summary>
public sealed class UnitOfWorkTests
{
    private static async Task<long> CountAsync(PostgresTestDatabase database)
    {
        await using var connection = await database.OpenAsync();
        await using var command = new NpgsqlCommand("select count(*) from probe_aggregate", connection);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// **执行策略与事务可以共存**（票据 19 验收 6）。
    ///
    /// <para>参照仓库在这里必然失败：它开了 <c>EnableRetryOnFailure()</c>，
    /// 又用裸 <c>BeginTransactionAsync()</c>，EF Core 抛
    /// "The configured execution strategy does not support user-initiated transactions"。</para>
    ///
    /// <para>所以这个用例要跑两个方向：**提交**确实落库，**异常**确实回滚。
    /// 只测提交是不够的——一个从不回滚的实现同样能让数据落库。</para>
    /// </summary>
    [PostgresFact]
    public async Task ExecutionStrategyAndTransaction_Coexist_AndCommitAndRollbackBothWork()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var context = await ProbeDatabase.CreateAsync(database).ConfigureAwait(false);
        var unitOfWork = new EfUnitOfWork<ProbeDbContext>(context);

        await using (context.ConfigureAwait(false))
        {
            // 一、正常返回即提交。
            await unitOfWork.ExecuteInTransactionAsync(
                async cancellationToken =>
                {
                    context.Probes.Add(ProbeAggregate.Create(11, "应当留下"));
                    return await context.SaveChangesAsync(cancellationToken);
                });

            Assert.Equal(1L, await CountAsync(database));

            // 二、抛异常即回滚——**而且不能吞掉异常**。
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                unitOfWork.ExecuteInTransactionAsync<int>(async cancellationToken =>
                {
                    context.Probes.Add(ProbeAggregate.Create(22, "不应当留下"));
                    await context.SaveChangesAsync(cancellationToken);

                    throw new InvalidOperationException("故意失败");
                }));

            // 表里仍然只有第一条。注意这里用的是**原始 SQL**：
            // EF 的查询会带上软删过滤器，而这条断言要的是"表里到底有几行"。
            Assert.Equal(1L, await CountAsync(database));
        }
    }
}
