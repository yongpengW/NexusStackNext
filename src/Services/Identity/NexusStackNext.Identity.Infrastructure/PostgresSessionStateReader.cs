using System.Data;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Domain.Authorization;
using NexusStackNext.Identity.Application;
using Npgsql;

namespace NexusStackNext.Identity.Infrastructure;

/// <summary>以一个已提交快照读取会话与可选操作许可；专用池、单次连接、无重试。</summary>
public sealed class PostgresSessionStateReader : ISessionStateReader, IAccessStateReader, IDisposable
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
        var current = await ReadCurrentAsync(userId, null, cancellationToken).ConfigureAwait(false);
        return current.IsSuccess ? Result.Success(current.Value?.Session) : Result.Failure<SessionState?>(current.Error);
    }

    /// <inheritdoc />
    public Task<Result<AccessState?>> ReadAsync(long userId, PermissionKey required, CancellationToken cancellationToken = default)
        => ReadCurrentAsync(userId, required, cancellationToken);

    private async Task<Result<AccessState?>> ReadCurrentAsync(long userId, PermissionKey? required, CancellationToken cancellationToken)
    {
        if (!await _permits.WaitAsync(0, cancellationToken).ConfigureAwait(false)) { return Unavailable(); }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_options.Timeout);
        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(budget.Token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = required is null
                ? """SELECT "SessionVersion", "IsEnabled", "IsBuiltIn", false FROM identity.users WHERE "Id" = @id"""
                : """
                  SELECT u."SessionVersion", u."IsEnabled", u."IsBuiltIn", u."IsBuiltIn" OR EXISTS (
                      SELECT 1 FROM identity.user_roles ur
                      JOIN identity.roles r ON r."Id" = ur.role_id
                      JOIN identity.role_menus rm ON rm.role_id = r."Id"
                      JOIN identity.menu_nodes mn ON mn."Id" = rm.menu_id
                      JOIN identity.menu_trees mt ON mt."Id" = mn.menu_tree_id
                      JOIN identity.api_resources ar ON ar."MenuId" = mn."Id"
                      WHERE ur.user_id = u."Id" AND ar."RoutePattern" = @path AND ar."HttpMethod" = @method)
                  FROM identity.users u WHERE u."Id" = @id
                  """;
            if (required is { } operation)
            {
                var separator = operation.Value.LastIndexOf(':');
                command.Parameters.AddWithValue("path", operation.Value[..separator]);
                command.Parameters.AddWithValue("method", operation.Value[(separator + 1)..]);
            }
            command.Parameters.AddWithValue("id", userId);
            command.CommandTimeout = (int)Math.Ceiling(_options.Timeout.TotalSeconds);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, budget.Token).ConfigureAwait(false);
            AccessState? state = await reader.ReadAsync(budget.Token).ConfigureAwait(false)
                ? new(new(reader.GetInt64(0), reader.GetBoolean(1), reader.GetBoolean(2)), reader.GetBoolean(3)) : null;
            budget.Token.ThrowIfCancellationRequested();
            return Result.Success(state);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Unavailable(); }
        catch (NpgsqlException) { cancellationToken.ThrowIfCancellationRequested(); return Unavailable(); }
        catch (TimeoutException) { cancellationToken.ThrowIfCancellationRequested(); return Unavailable(); }
        finally { _permits.Release(); }
    }

    private static Result<AccessState?> Unavailable() => Result.Failure<AccessState?>(SessionValidationErrors.Unavailable);
    /// <inheritdoc />
    public void Dispose() => _permits.Dispose();
}
