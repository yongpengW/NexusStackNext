using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Domain;
using Npgsql;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Events;

/// <summary>来源声明约束下的独立连接、有限等待与原子恢复协议。</summary>
/// <param name="connectionString">只属于该来源的已装配连接。</param>
/// <param name="source">模块代码声明的归属、事件与拒绝语义。</param>
public sealed class PostgresFactDeliveryRecoveryStore(string connectionString, FactDeliveryRecoverySource source)
{
    private readonly string _schema = (source ?? throw new ArgumentNullException(nameof(source))).Schema;
    private static readonly TimeSpan RecoveryTimeout = TimeSpan.FromSeconds(3);

    /// <summary>以所属独立连接和有限等待读取单条状态，只选择安全列。</summary>
    /// <param name="messageId">稳定消息标识。</param>
    /// <param name="cancellationToken">调用者取消。</param>
    /// <returns>所属状态、未找到或明确不可用。</returns>
    public async Task<Result<FactDeliveryState>> GetAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(RecoveryTimeout);
        try
        {
            await using var connection = NewRecoveryConnection();
            await connection.OpenAsync(budget.Token).ConfigureAwait(false);
            await using var read = RecoveryCommand(connection, null, $"""
                SELECT "DeliveredAt", "DeadLetteredAt", "AttemptCount", "NextAttemptAt", "RetryRevision"
                FROM {_schema}.outbox WHERE "Id" = @id AND "EventName" = ANY(@names)
                """);
            read.Parameters.AddWithValue("id", messageId);
            read.Parameters.AddWithValue("names", source.EventNames);
            await using var reader = await read.ExecuteReaderAsync(CommandBehavior.SingleRow, budget.Token).ConfigureAwait(false);
            if (!await reader.ReadAsync(budget.Token).ConfigureAwait(false))
            {
                budget.Token.ThrowIfCancellationRequested();
                return Result.Failure<FactDeliveryState>(source.Errors.DeliveryNotFound);
            }
            DateTimeOffset? stoppedAt = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1);
            var state = !reader.IsDBNull(0) ? "Delivered" : stoppedAt is not null ? "DeadLettered" : "Pending";
            var observed = new FactDeliveryState(messageId, state, reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3), stoppedAt)
            { RetryRevision = reader.GetInt64(4) };
            budget.Token.ThrowIfCancellationRequested();
            return Result.Success(observed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Result.Failure<FactDeliveryState>(CommittedFactCapacityErrors.Unavailable);
        }
        catch (NpgsqlException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Result.Failure<FactDeliveryState>(CommittedFactCapacityErrors.Unavailable);
        }
        catch (TimeoutException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Result.Failure<FactDeliveryState>(CommittedFactCapacityErrors.Unavailable);
        }
    }

    /// <summary>从适配器的所属 Outbox 有界投影安全列，不读取正文或异常。</summary>
    /// <param name="outbox">适配器提供的所属数据库查询。</param>
    /// <param name="state">Pending / Delivered / DeadLettered。</param>
    /// <param name="limit">一至一百。</param>
    /// <param name="cancellationToken">调用者取消。</param>
    /// <returns>稳定排序的安全状态。</returns>
    public async Task<IReadOnlyList<FactDeliveryState>> ListAsync(IQueryable<OutboxEntry> outbox, string state, int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);
        var names = source.EventNames;
        var query = outbox.AsNoTracking().Where(entry => names.Contains(entry.EventName));
        query = state switch
        {
            "Pending" => query.Where(entry => entry.DeliveredAt == null && entry.DeadLetteredAt == null),
            "Delivered" => query.Where(entry => entry.DeliveredAt != null),
            "DeadLettered" => query.Where(entry => entry.DeliveredAt == null && entry.DeadLetteredAt != null),
            _ => throw new ArgumentException("未知投递状态。", nameof(state)),
        };
        return await query.OrderBy(entry => entry.OccurredAt).ThenBy(entry => entry.Id).Take(limit)
            .Select(entry => new FactDeliveryState(entry.Id, entry.DeliveredAt != null ? "Delivered"
                : entry.DeadLetteredAt != null ? "DeadLettered" : "Pending", entry.AttemptCount, entry.NextAttemptAt, entry.DeadLetteredAt)
            { RetryRevision = entry.RetryRevision }).ToArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按固定期限和稳定顺序原子删除有限批次凭据并释放所属计量。</summary>
    /// <param name="batchSize">每轮一至一千个请求。</param>
    /// <param name="now">维护时钟给出的当前时刻。</param>
    /// <param name="cancellationToken">实际提交之前可取消。</param>
    /// <returns>实际释放的恢复请求数。</returns>
    public async Task<int> CleanupRecoveriesAsync(int batchSize, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(batchSize, 1000);
        cancellationToken.ThrowIfCancellationRequested();
        if (System.Transactions.Transaction.Current is not null)
        { throw new InvalidOperationException("恢复凭据清理必须使用独立的维护作用域。"); }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(RecoveryTimeout);
        var acquiringRecoveryLedger = false;
        try
        {
            await using var connection = NewRecoveryConnection();
            await connection.OpenAsync(budget.Token).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(budget.Token).ConfigureAwait(false);
            await using (var configure = RecoveryCommand(connection, transaction, "SELECT set_config('lock_timeout', @wait, true)"))
            {
                configure.Parameters.AddWithValue("wait", ((long)RecoveryTimeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture));
                await configure.ExecuteNonQueryAsync(budget.Token).ConfigureAwait(false);
            }
            long retainedRecords;
            long retainedBytes;
            acquiringRecoveryLedger = true;
            await using (var control = RecoveryCommand(connection, transaction, $"""
                SELECT "RetainedRecords", "RetainedPayloadBytes" FROM {_schema}.fact_recovery_control WHERE "Id" = 1 FOR UPDATE
                """))
            await using (var reader = await control.ExecuteReaderAsync(CommandBehavior.SingleRow, budget.Token).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(budget.Token).ConfigureAwait(false))
                { throw new InvalidOperationException("恢复凭据容量账本不存在。"); }
                retainedRecords = reader.GetInt64(0);
                retainedBytes = reader.GetInt64(1);
            }
            acquiringRecoveryLedger = false;
            var candidates = new List<(Guid RequestId, int PayloadBytes)>();
            await using (var select = RecoveryCommand(connection, transaction, $"""
                SELECT "RequestId", "PayloadBytes" FROM {_schema}.fact_recovery_receipts
                WHERE "RetainUntil" <= @now ORDER BY "RetainUntil", "RequestId" LIMIT @batch FOR UPDATE
                """))
            {
                select.Parameters.AddWithValue("now", now.ToUniversalTime());
                select.Parameters.AddWithValue("batch", batchSize);
                await using var reader = await select.ExecuteReaderAsync(budget.Token).ConfigureAwait(false);
                while (await reader.ReadAsync(budget.Token).ConfigureAwait(false))
                { candidates.Add((reader.GetGuid(0), reader.GetInt32(1))); }
            }
            if (candidates.Count == 0) { budget.Token.ThrowIfCancellationRequested(); return 0; }
            var releasedBytes = candidates.Sum(record => (long)record.PayloadBytes);
            if (candidates.Count > retainedRecords || releasedBytes > retainedBytes)
            { throw new InvalidOperationException("恢复凭据容量计量不一致。"); }
            await using (var delete = RecoveryCommand(connection, transaction,
                $"DELETE FROM {_schema}.fact_recovery_receipts WHERE \"RequestId\" = ANY(@ids)"))
            {
                delete.Parameters.AddWithValue("ids", candidates.Select(record => record.RequestId).ToArray());
                if (await delete.ExecuteNonQueryAsync(budget.Token).ConfigureAwait(false) != candidates.Count)
                { throw new InvalidOperationException("恢复凭据清理对象发生变化。"); }
            }
            await using (var update = RecoveryCommand(connection, transaction, $"""
                UPDATE {_schema}.fact_recovery_control SET "RetainedRecords" = "RetainedRecords" - @records,
                    "RetainedPayloadBytes" = "RetainedPayloadBytes" - @bytes WHERE "Id" = 1
                """))
            {
                update.Parameters.AddWithValue("records", candidates.Count);
                update.Parameters.AddWithValue("bytes", releasedBytes);
                if (await update.ExecuteNonQueryAsync(budget.Token).ConfigureAwait(false) != 1)
                { throw new InvalidOperationException("恢复凭据容量账本不存在。"); }
            }
            await transaction.CommitAsync(budget.Token).ConfigureAwait(false);
            return candidates.Count;
        }
        catch (PostgresException error) when (acquiringRecoveryLedger && error.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new CommittedFactCapacityBusyException();
        }
    }

    /// <summary>读取独立恢复池的已提交最小计量。</summary>
    /// <param name="cancellationToken">调用者取消。</param>
    /// <returns>容量快照或明确不可用。</returns>
    public async Task<Result<FactDeliveryRecoveryCapacity>> ReadRecoveryCapacityAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(RecoveryTimeout);
        try
        {
            await using var connection = NewRecoveryConnection();
            await connection.OpenAsync(budget.Token).ConfigureAwait(false);
            await using var read = RecoveryCommand(connection, null, $"""
                SELECT "MaxRecords", "MaxPayloadBytes", "MaxRecordPayloadBytes", "RetainedRecords", "RetainedPayloadBytes"
                FROM {_schema}.fact_recovery_control WHERE "Id" = 1
                """);
            await using var reader = await read.ExecuteReaderAsync(CommandBehavior.SingleRow, budget.Token).ConfigureAwait(false);
            if (!await reader.ReadAsync(budget.Token).ConfigureAwait(false))
            { return Result.Failure<FactDeliveryRecoveryCapacity>(CommittedFactCapacityErrors.Unavailable); }
            var capacity = new FactDeliveryRecoveryCapacity(source.Context, true,
                new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetInt64(4)));
            budget.Token.ThrowIfCancellationRequested();
            return Result.Success(capacity);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Result.Failure<FactDeliveryRecoveryCapacity>(CommittedFactCapacityErrors.Unavailable);
        }
        catch (NpgsqlException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Result.Failure<FactDeliveryRecoveryCapacity>(CommittedFactCapacityErrors.Unavailable);
        }
        catch (TimeoutException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Result.Failure<FactDeliveryRecoveryCapacity>(CommittedFactCapacityErrors.Unavailable);
        }
    }

    /// <summary>读取已保留的原恢复裁决。</summary>
    /// <param name="requestId">稳定请求。</param>
    /// <param name="cancellationToken">调用者取消。</param>
    /// <returns>原凭据或所属拒绝。</returns>
    public async Task<Result<FactDeliveryRecoveryReceipt>> GetRecoveryAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(RecoveryTimeout);
        try
        {
            await using var connection = NewRecoveryConnection();
            await connection.OpenAsync(budget.Token).ConfigureAwait(false);
            await using var read = RecoveryCommand(connection, null,
                $"SELECT \"RecordJson\" FROM {_schema}.fact_recovery_receipts WHERE \"RequestId\" = @id");
            read.Parameters.AddWithValue("id", requestId);
            var json = await read.ExecuteScalarAsync(budget.Token).ConfigureAwait(false) as string;
            budget.Token.ThrowIfCancellationRequested();
            return json is null ? Result.Failure<FactDeliveryRecoveryReceipt>(source.Errors.NotFound)
                : Result.Success(ReadReceipt(json));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Result.Failure<FactDeliveryRecoveryReceipt>(CommittedFactCapacityErrors.Unavailable);
        }
        catch (TimeoutException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Result.Failure<FactDeliveryRecoveryReceipt>(CommittedFactCapacityErrors.Unavailable);
        }
    }

    /// <summary>重放或在所属事务中原子恢复、保存凭据和计量。</summary>
    /// <param name="request">稳定双条件请求。</param>
    /// <param name="actorId">可信操作者。</param>
    /// <param name="occurredAt">可信裁决时刻。</param>
    /// <param name="execution">可信执行关联。</param>
    /// <param name="cancellationToken">实际提交之前可取消。</param>
    /// <returns>稳定原裁决或明确拒绝。</returns>
    public async Task<Result<FactDeliveryRecoveryReceipt>> RecoverAsync(FactDeliveryRecoveryRequest request, string actorId,
        DateTimeOffset occurredAt, ExecutionOrigin? execution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.IsValidFor(actorId, occurredAt, execution))
        { return Result.Failure<FactDeliveryRecoveryReceipt>(source.Errors.Invalid); }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(RecoveryTimeout);
        var acquiringRecoveryLedger = false;
        try
        {
            // This connection never enlists in the caller's ambient transaction or modifies its tracked work.
            await using var connection = NewRecoveryConnection();
            await connection.OpenAsync(budget.Token).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(budget.Token).ConfigureAwait(false);
            await using (var configure = RecoveryCommand(connection, transaction, "SELECT set_config('lock_timeout', @wait, true)"))
            {
                configure.Parameters.AddWithValue("wait", ((long)RecoveryTimeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture));
                await configure.ExecuteNonQueryAsync(budget.Token).ConfigureAwait(false);
            }
            long maxRecords;
            long maxBytes;
            int maxSingle;
            long retainedRecords;
            long retainedBytes;
            acquiringRecoveryLedger = true;
            await using (var control = RecoveryCommand(connection, transaction, $"""
                SELECT "MaxRecords", "MaxPayloadBytes", "MaxRecordPayloadBytes", "RetainedRecords", "RetainedPayloadBytes"
                FROM {_schema}.fact_recovery_control WHERE "Id" = 1 FOR UPDATE
                """))
            await using (var reader = await control.ExecuteReaderAsync(CommandBehavior.SingleRow, budget.Token).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(budget.Token).ConfigureAwait(false))
                { return Result.Failure<FactDeliveryRecoveryReceipt>(CommittedFactCapacityErrors.Unavailable); }
                maxRecords = reader.GetInt64(0);
                maxBytes = reader.GetInt64(1);
                maxSingle = reader.GetInt32(2);
                retainedRecords = reader.GetInt64(3);
                retainedBytes = reader.GetInt64(4);
            }
            acquiringRecoveryLedger = false;
            await using (var replay = RecoveryCommand(connection, transaction,
                $"SELECT \"RecordJson\" FROM {_schema}.fact_recovery_receipts WHERE \"RequestId\" = @id"))
            {
                replay.Parameters.AddWithValue("id", request.RequestId);
                if (await replay.ExecuteScalarAsync(budget.Token).ConfigureAwait(false) is string previousJson)
                {
                    var previous = ReadReceipt(previousJson);
                    budget.Token.ThrowIfCancellationRequested();
                    return previous.Matches(request, actorId)
                        ? Result.Success(previous) : Result.Failure<FactDeliveryRecoveryReceipt>(source.Errors.RequestConflict);
                }
            }
            await using var kind = RecoveryCommand(connection, transaction,
                $"SELECT \"EventName\" FROM {_schema}.outbox WHERE \"Id\" = @id");
            kind.Parameters.AddWithValue("id", request.MessageId);
            var eventName = await kind.ExecuteScalarAsync(budget.Token).ConfigureAwait(false) as string;
            budget.Token.ThrowIfCancellationRequested();
            if (eventName is not null && !source.Manages(eventName))
            { return Result.Failure<FactDeliveryRecoveryReceipt>(source.Errors.Unmanaged); }
            // Npgsql truncates ticks when sending timestamps. A changed observation must not become equal in SQL.
            if (request.ExpectedDeadLetteredAt.UtcTicks % 10 != 0)
            { return Result.Failure<FactDeliveryRecoveryReceipt>(source.Errors.Conflict); }
            // Compare both observations in the actual update. A concurrent broker confirmation remains authoritative.
            await using (var update = RecoveryCommand(connection, transaction, $"""
                UPDATE {_schema}.outbox SET "AttemptCount" = 0, "NextAttemptAt" = NULL, "DeadLetteredAt" = NULL,
                    "LastFailure" = NULL, "RetryRevision" = "RetryRevision" + 1
                WHERE "Id" = @id AND "EventName" = ANY(@names) AND "DeliveredAt" IS NULL
                    AND "DeadLetteredAt" = @stopped AND "RetryRevision" = @revision AND "RetryRevision" < @maximum
                """))
            {
                update.Parameters.AddWithValue("id", request.MessageId);
                update.Parameters.AddWithValue("names", source.EventNames);
                update.Parameters.AddWithValue("stopped", request.ExpectedDeadLetteredAt.ToUniversalTime());
                update.Parameters.AddWithValue("revision", request.ExpectedRetryRevision);
                update.Parameters.AddWithValue("maximum", long.MaxValue);
                if (await update.ExecuteNonQueryAsync(budget.Token).ConfigureAwait(false) != 1)
                { return Result.Failure<FactDeliveryRecoveryReceipt>(source.Errors.Conflict); }
            }
            var prepared = FactDeliveryRecoveryPreparation.Prepare(request, source.Context, actorId, occurredAt, execution);
            var receipt = prepared.Receipt;
            var json = prepared.Json;
            var bytes = prepared.PayloadBytes;
            if (retainedRecords >= maxRecords || bytes > maxSingle || bytes > maxBytes - retainedBytes)
            { return Result.Failure<FactDeliveryRecoveryReceipt>(source.Errors.Exhausted); }
            await using (var save = RecoveryCommand(connection, transaction, $"""
                INSERT INTO {_schema}.fact_recovery_receipts ("RequestId", "RecordJson", "PayloadBytes", "RetainUntil")
                    VALUES (@id, @json, @bytes, CASE WHEN @fraction THEN @retain + INTERVAL '1 microsecond' ELSE @retain END);
                UPDATE {_schema}.fact_recovery_control SET "RetainedRecords" = "RetainedRecords" + 1,
                    "RetainedPayloadBytes" = "RetainedPayloadBytes" + @bytes WHERE "Id" = 1;
                """))
            {
                save.Parameters.AddWithValue("id", request.RequestId);
                save.Parameters.AddWithValue("json", json);
                save.Parameters.AddWithValue("bytes", bytes);
                save.Parameters.AddWithValue("retain", receipt.RetainUntil.ToUniversalTime());
                save.Parameters.AddWithValue("fraction", receipt.RetainUntil.UtcTicks % 10 != 0);
                await save.ExecuteNonQueryAsync(budget.Token).ConfigureAwait(false);
            }
            await transaction.CommitAsync(budget.Token).ConfigureAwait(false);
            return Result.Success(receipt);
        }
        catch (PostgresException error) when (acquiringRecoveryLedger && error.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Result.Failure<FactDeliveryRecoveryReceipt>(CommittedFactCapacityErrors.Busy);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Result.Failure<FactDeliveryRecoveryReceipt>(CommittedFactCapacityErrors.Unavailable);
        }
        catch (TimeoutException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Result.Failure<FactDeliveryRecoveryReceipt>(CommittedFactCapacityErrors.Unavailable);
        }
    }

    private NpgsqlConnection NewRecoveryConnection() => new(new NpgsqlConnectionStringBuilder(connectionString)
    { CancellationTimeout = -1, Enlist = false }.ConnectionString);

    private static NpgsqlCommand RecoveryCommand(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql)
        => new(sql, connection, transaction) { CommandTimeout = 0 };

    private static FactDeliveryRecoveryReceipt ReadReceipt(string json) => JsonSerializer.Deserialize<FactDeliveryRecoveryReceipt>(json)
        ?? throw new InvalidOperationException("恢复凭据不可解析。");

}
