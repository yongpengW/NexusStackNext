using System.Data;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Identity.Application;
using Npgsql;

namespace NexusStackNext.Identity.Infrastructure;

/// <summary>仅读取 Identity 所属用户的最小状态；专用池、单次连接、无重试。</summary>
public sealed class PostgresSessionStateReader : ISessionStateReader, IDisposable
{
    private readonly string _connectionString;
    private readonly IdentitySessionReadOptions _options;
    private readonly SemaphoreSlim _permits;

    /// <summary>构造进程共享的有界读取适配器。</summary>
    /// <param name="connectionString">Identity 自己的连接配置。</param>
    /// <param name="options">完整读取预算。</param>
    public PostgresSessionStateReader(string connectionString, IdentitySessionReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _permits = new(options.MaxConcurrency, options.MaxConcurrency);
        _connectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = "nsn-session-authority",
            MaxPoolSize = options.MaxConcurrency,
            MinPoolSize = 0,
            CancellationTimeout = -1,
            Multiplexing = false,
        }.ConnectionString;
    }

    /// <inheritdoc />
    public async Task<Result<SessionState?>> ReadAsync(long userId, CancellationToken cancellationToken = default)
    {
        if (!await _permits.WaitAsync(0, cancellationToken).ConfigureAwait(false)) { return Unavailable(); }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_options.Timeout);
        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(budget.Token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """SELECT "SessionVersion", "IsEnabled", "IsBuiltIn" FROM identity.users WHERE "Id" = @id""";
            command.Parameters.AddWithValue("id", userId);
            command.CommandTimeout = (int)Math.Ceiling(_options.Timeout.TotalSeconds);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, budget.Token).ConfigureAwait(false);
            SessionState? state = await reader.ReadAsync(budget.Token).ConfigureAwait(false)
                ? new(reader.GetInt64(0), reader.GetBoolean(1), reader.GetBoolean(2)) : null;
            budget.Token.ThrowIfCancellationRequested();
            return Result.Success(state);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Unavailable(); }
        catch (NpgsqlException) { cancellationToken.ThrowIfCancellationRequested(); return Unavailable(); }
        catch (TimeoutException) { cancellationToken.ThrowIfCancellationRequested(); return Unavailable(); }
        finally { _permits.Release(); }
    }

    private static Result<SessionState?> Unavailable() => Result.Failure<SessionState?>(SessionValidationErrors.Unavailable);
    /// <inheritdoc />
    public void Dispose() => _permits.Dispose();
}
