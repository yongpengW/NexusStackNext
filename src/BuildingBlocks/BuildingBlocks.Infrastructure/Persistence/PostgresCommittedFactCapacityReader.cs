using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using Npgsql;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Persistence;

/// <summary>所属 PostgreSQL 账本的只读适配器；独立连接不使用当前 DbContext 的事务或执行重试。</summary>
/// <typeparam name="TContext">实际拥有账本的数据库上下文。</typeparam>
/// <param name="context">所属上下文，只提供已装配的连接与 schema。</param>
/// <param name="owner">装配代码声明的诊断名称。</param>
/// <param name="options">独立读取预算。</param>
public sealed class PostgresCommittedFactCapacityReader<TContext>(TContext context, string owner,
    CommittedFactCapacityReadOptions options) : ICommittedFactCapacityReader where TContext : NexusStackDbContext
{
    /// <inheritdoc />
    public async Task<Result<CommittedFactCapacitySnapshot>> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.Timeout);
        try
        {
            // 诊断取消直接终止本次专用连接，不额外等待服务器取消确认，也不改业务连接的选项。
            var connectionOptions = new NpgsqlConnectionStringBuilder(context.Database.GetConnectionString()) { CancellationTimeout = -1 };
            await using var connection = new NpgsqlConnection(connectionOptions.ConnectionString);
            await connection.OpenAsync(budget.Token).ConfigureAwait(false);
            using var identifiers = new NpgsqlCommandBuilder();
            var schema = identifiers.QuoteIdentifier(context.Schema);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT "MaxRecords", "MaxPayloadBytes", "MaxRecordPayloadBytes", "RetainedRecords", "RetainedPayloadBytes"
                FROM {schema}.fact_capacity WHERE "Id" = 1
                """;
            command.CommandTimeout = (int)Math.Ceiling(options.Timeout.TotalSeconds);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, budget.Token).ConfigureAwait(false);
            if (!await reader.ReadAsync(budget.Token).ConfigureAwait(false)) { return Unavailable(); }
            var snapshot = new CommittedFactCapacitySnapshot(owner, true, reader.GetInt64(0), reader.GetInt64(1),
                reader.GetInt32(2), reader.GetInt64(3), reader.GetInt64(4));
            budget.Token.ThrowIfCancellationRequested();
            return snapshot.MaxRecords > 0 && snapshot.MaxPayloadBytes > 0 && snapshot.MaxRecordPayloadBytes > 0
                && snapshot.MaxRecordPayloadBytes <= snapshot.MaxPayloadBytes && snapshot.RetainedRecords >= 0 && snapshot.RetainedPayloadBytes >= 0
                ? Result.Success(snapshot) : Unavailable();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Unavailable(); }
        catch (NpgsqlException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Unavailable();
        }
        catch (TimeoutException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Unavailable();
        }
    }

    private static Result<CommittedFactCapacitySnapshot> Unavailable()
        => Result.Failure<CommittedFactCapacitySnapshot>(CommittedFactCapacityErrors.Unavailable);
}

/// <summary>由实际来源模块显式注册自己的只读诊断；不同键不共享连接或账本。</summary>
public static class CommittedFactCapacityReadServices
{
    /// <summary>注册所属 PostgreSQL 读取适配器并验证独立预算。</summary>
    /// <typeparam name="TContext">所属数据库上下文。</typeparam>
    /// <param name="services">宿主服务。</param>
    /// <param name="owner">上下文和解析键。</param>
    /// <param name="options">独立读取预算。</param>
    /// <returns>原服务集合。</returns>
    public static IServiceCollection AddCommittedFactCapacityReader<TContext>(this IServiceCollection services, string owner,
        CommittedFactCapacityReadOptions? options = null) where TContext : NexusStackDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        var policy = options ?? new();
        policy.Validate();
        services.AddKeyedScoped<ICommittedFactCapacityReader>(owner,
            (provider, _) => new PostgresCommittedFactCapacityReader<TContext>(provider.GetRequiredService<TContext>(), owner, policy));
        return services;
    }
}
