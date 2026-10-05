using System.Data;
using System.Globalization;
using System.Text.Json;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Domain;
using Npgsql;
using NpgsqlTypes;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Events;

/// <summary>所属独立连接内的策略、控制额度、凭据与 Outbox 原子提交。</summary>
/// <param name="connectionString">仅所属模块的已装配连接。</param>
/// <param name="serializer">固定最小契约的序列化器。</param>
/// <param name="timeout">连接、执行与提交共用的有限访问预算。</param>
/// <param name="source">模块代码声明的所属来源与固定事件投影。</param>
public sealed class PostgresFactCapacityPolicyStore(string connectionString, IIntegrationEventSerializer serializer,
    TimeSpan timeout, FactCapacityPolicySource source) : ICommittedFactCapacityPolicyStore, ICommittedFactCapacityPolicyCleanup
{
    private readonly string _schema = (source ?? throw new ArgumentNullException(nameof(source))).Schema;

    /// <inheritdoc />
    public async Task<int> CleanupAsync(int batchSize, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(batchSize, 1000);
        cancellationToken.ThrowIfCancellationRequested();
        if (System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException("策略凭据清理必须使用独立的维护作用域。");
        }
        // An accepted receipt cannot reach its seven-day deadline this early in the supported calendar.
        if (now < DateTimeOffset.MinValue.AddDays(1)) { return 0; }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout);
        await using var connection = NewConnection();
        await connection.OpenAsync(budget.Token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(budget.Token).ConfigureAwait(false);
        await using (var configure = NewCommand(connection, transaction, "SELECT set_config('lock_timeout', @wait, true)"))
        {
            configure.Parameters.AddWithValue("wait", Math.Max(1L, (long)timeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture));
            await configure.ExecuteNonQueryAsync(budget.Token).ConfigureAwait(false);
        }
        long retainedRecords;
        long retainedBytes;
        await using (var control = NewCommand(connection, transaction, $"""
            SELECT "RetainedRecords", "RetainedPayloadBytes" FROM {_schema}.fact_policy_control WHERE "Id" = 1 FOR UPDATE
            """))
        await using (var reader = await control.ExecuteReaderAsync(CommandBehavior.SingleRow, budget.Token).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(budget.Token).ConfigureAwait(false)) { throw new InvalidOperationException("策略控制账本不存在。"); }
            retainedRecords = reader.GetInt64(0);
            retainedBytes = reader.GetInt64(1);
        }
        var candidates = new List<(Guid RequestId, int PayloadBytes, Guid? EventId)>();
        await using (var select = NewCommand(connection, transaction, $"""
            SELECT r."RequestId", r."PayloadBytes", r."EventId"
            FROM {_schema}.fact_policy_receipts r LEFT JOIN {_schema}.outbox o ON o."Id" = r."EventId"
            WHERE r."RetainUntil" <= @now AND (r."EventId" IS NULL OR
                (o."EventName" = @event AND o."DeliveredAt" <= @cutoff AND o."DeadLetteredAt" IS NULL))
            ORDER BY r."RetainUntil", r."RequestId" LIMIT @batch FOR UPDATE OF r
            """))
        {
            select.Parameters.AddWithValue("now", now.ToUniversalTime());
            select.Parameters.AddWithValue("cutoff", now.AddDays(-1).ToUniversalTime());
            select.Parameters.AddWithValue("event", source.EventName);
            select.Parameters.AddWithValue("batch", batchSize);
            await using var reader = await select.ExecuteReaderAsync(budget.Token).ConfigureAwait(false);
            while (await reader.ReadAsync(budget.Token).ConfigureAwait(false))
            {
                candidates.Add((reader.GetGuid(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetGuid(2)));
            }
        }
        if (candidates.Count == 0) { return 0; }
        var releasedBytes = candidates.Sum(record => (long)record.PayloadBytes);
        if (candidates.Count > retainedRecords || releasedBytes > retainedBytes)
        {
            throw new InvalidOperationException("策略凭据容量计量不一致。");
        }
        await using (var delete = NewCommand(connection, transaction, $"DELETE FROM {_schema}.fact_policy_receipts WHERE \"RequestId\" = ANY(@ids)"))
        {
            delete.Parameters.AddWithValue("ids", candidates.Select(record => record.RequestId).ToArray());
            if (await delete.ExecuteNonQueryAsync(budget.Token).ConfigureAwait(false) != candidates.Count)
            {
                throw new InvalidOperationException("策略凭据清理对象发生变化。");
            }
        }
        var events = candidates.Where(record => record.EventId is not null).Select(record => record.EventId!.Value).ToArray();
        if (events.Length > 0)
        {
            await using var delete = NewCommand(connection, transaction, $"""
                DELETE FROM {_schema}.outbox WHERE "Id" = ANY(@ids) AND "EventName" = @event
                    AND "DeliveredAt" <= @cutoff AND "DeadLetteredAt" IS NULL
                """);
            delete.Parameters.AddWithValue("ids", events);
            delete.Parameters.AddWithValue("event", source.EventName);
            delete.Parameters.AddWithValue("cutoff", now.AddDays(-1).ToUniversalTime());
            if (await delete.ExecuteNonQueryAsync(budget.Token).ConfigureAwait(false) != events.Length)
            {
                throw new InvalidOperationException("策略控制事实清理对象发生变化。");
            }
        }
        await using (var update = NewCommand(connection, transaction, $"""
            UPDATE {_schema}.fact_policy_control SET "RetainedRecords" = "RetainedRecords" - @records,
                "RetainedPayloadBytes" = "RetainedPayloadBytes" - @bytes WHERE "Id" = 1
            """))
        {
            update.Parameters.AddWithValue("records", candidates.Count);
            update.Parameters.AddWithValue("bytes", releasedBytes);
            if (await update.ExecuteNonQueryAsync(budget.Token).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("策略控制账本不存在。");
            }
        }
        await transaction.CommitAsync(budget.Token).ConfigureAwait(false);
        return candidates.Count;
    }

    /// <inheritdoc />
    public async Task<Result<FactCapacityPolicySnapshot>> ReadPolicyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout);
        try
        {
            await using var connection = NewConnection();
            await connection.OpenAsync(budget.Token).ConfigureAwait(false);
            await using var command = NewCommand(connection, null, $"""
                SELECT b."MaxRecords", b."MaxPayloadBytes", b."MaxRecordPayloadBytes", b."RetainedRecords", b."RetainedPayloadBytes",
                    c."PolicyRevision", c."MaxRecords", c."MaxPayloadBytes", c."MaxRecordPayloadBytes", c."RetainedRecords", c."RetainedPayloadBytes"
                FROM {_schema}.fact_capacity b JOIN {_schema}.fact_policy_control c ON b."Id" = c."Id" WHERE b."Id" = 1
                """);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, budget.Token).ConfigureAwait(false);
            if (!await reader.ReadAsync(budget.Token).ConfigureAwait(false)) { return Unavailable<FactCapacityPolicySnapshot>(); }
            var business = new CommittedFactCapacitySnapshot(source.Context, true, reader.GetInt64(0), reader.GetInt64(1),
                reader.GetInt32(2), reader.GetInt64(3), reader.GetInt64(4));
            var result = new FactCapacityPolicySnapshot(business, reader.GetInt64(5),
                new(reader.GetInt64(6), reader.GetInt64(7), reader.GetInt32(8), reader.GetInt64(9), reader.GetInt64(10)));
            budget.Token.ThrowIfCancellationRequested();
            return Result.Success(result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Unavailable<FactCapacityPolicySnapshot>(); }
        catch (NpgsqlException) { cancellationToken.ThrowIfCancellationRequested(); return Unavailable<FactCapacityPolicySnapshot>(); }
        catch (TimeoutException) { cancellationToken.ThrowIfCancellationRequested(); return Unavailable<FactCapacityPolicySnapshot>(); }
    }

    /// <inheritdoc />
    public async Task<Result<FactCapacityPolicyReceipt>> AdjustAsync(FactCapacityPolicyRequest request, string actorId,
        DateTimeOffset occurredAt, ExecutionOrigin? execution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!FactCapacityPolicyPreparation.IsValid(request, actorId, occurredAt, execution))
        {
            return Result.Failure<FactCapacityPolicyReceipt>(source.Invalid);
        }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout);
        var acquiringCapacityLedger = false;
        try
        {
            await using var connection = NewConnection();
            await connection.OpenAsync(budget.Token).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(budget.Token).ConfigureAwait(false);
            await using (var configure = NewCommand(connection, transaction, "SELECT set_config('lock_timeout', @wait, true)"))
            {
                configure.Parameters.AddWithValue("wait", Math.Max(1L, (long)timeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture));
                await configure.ExecuteNonQueryAsync(budget.Token).ConfigureAwait(false);
            }
            FactPolicyControl state;
            acquiringCapacityLedger = true;
            await using (var stateCommand = NewCommand(connection, transaction, $"""
                SELECT "PolicyRevision", "MaxRecords", "MaxPayloadBytes", "MaxRecordPayloadBytes", "RetainedRecords", "RetainedPayloadBytes"
                FROM {_schema}.fact_policy_control WHERE "Id" = 1 FOR UPDATE
                """))
            await using (var stateReader = await stateCommand.ExecuteReaderAsync(CommandBehavior.SingleRow, budget.Token).ConfigureAwait(false))
            {
                if (!await stateReader.ReadAsync(budget.Token).ConfigureAwait(false)) { return Unavailable<FactCapacityPolicyReceipt>(); }
                state = new()
                {
                    Id = 1,
                    PolicyRevision = stateReader.GetInt64(0),
                    MaxRecords = stateReader.GetInt64(1),
                    MaxPayloadBytes = stateReader.GetInt64(2),
                    MaxRecordPayloadBytes = stateReader.GetInt32(3),
                    RetainedRecords = stateReader.GetInt64(4),
                    RetainedPayloadBytes = stateReader.GetInt64(5),
                };
            }
            acquiringCapacityLedger = false;
            await using (var replay = NewCommand(connection, transaction, $"SELECT \"RecordJson\" FROM {_schema}.fact_policy_receipts WHERE \"RequestId\" = @id"))
            {
                replay.Parameters.AddWithValue("id", request.RequestId);
                if (await replay.ExecuteScalarAsync(budget.Token).ConfigureAwait(false) is string recordJson)
                {
                    using var record = JsonDocument.Parse(recordJson);
                    var original = record.RootElement.GetProperty("request").Deserialize<FactCapacityPolicyRequest>();
                    return original == request && record.RootElement.GetProperty("actorId").GetString() == actorId
                        ? Result.Success(record.RootElement.GetProperty("receipt").Deserialize<FactCapacityPolicyReceipt>()
                            ?? throw new InvalidOperationException("策略凭据不可解析。"))
                        : Result.Failure<FactCapacityPolicyReceipt>(source.Conflict);
                }
            }
            if (request.ExpectedPolicyRevision != state.PolicyRevision) { return Result.Failure<FactCapacityPolicyReceipt>(source.Conflict); }
            FactCapacityPolicyLimits previous;
            acquiringCapacityLedger = true;
            await using (var business = NewCommand(connection, transaction, $"""
                SELECT "MaxRecords", "MaxPayloadBytes", "MaxRecordPayloadBytes" FROM {_schema}.fact_capacity WHERE "Id" = 1 FOR UPDATE
                """))
            await using (var reader = await business.ExecuteReaderAsync(CommandBehavior.SingleRow, budget.Token).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(budget.Token).ConfigureAwait(false)) { return Unavailable<FactCapacityPolicyReceipt>(); }
                previous = new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2));
            }
            acquiringCapacityLedger = false;
            var next = request.Limits;
            var changed = previous != next;
            if (changed && state.PolicyRevision == long.MaxValue) { return Result.Failure<FactCapacityPolicyReceipt>(source.Conflict); }
            var prepared = FactCapacityPolicyPreparation.Prepare(request, actorId, occurredAt, execution,
                previous, state.PolicyRevision, source, serializer);
            var receipt = prepared.Receipt;
            var fact = prepared.Fact;
            var revision = receipt.PolicyRevision;
            var json = prepared.Json;
            var size = prepared.PayloadBytes;
            if (state.RetainedRecords >= state.MaxRecords || size > state.MaxRecordPayloadBytes || size > state.MaxPayloadBytes - state.RetainedPayloadBytes)
            {
                return Result.Failure<FactCapacityPolicyReceipt>(source.Exhausted);
            }
            await using (var update = NewCommand(connection, transaction, $"""
                UPDATE {_schema}.fact_capacity SET "MaxRecords" = @records, "MaxPayloadBytes" = @bytes, "MaxRecordPayloadBytes" = @single WHERE "Id" = 1;
                UPDATE {_schema}.fact_policy_control SET "PolicyRevision" = @revision, "RetainedRecords" = "RetainedRecords" + 1,
                    "RetainedPayloadBytes" = "RetainedPayloadBytes" + @size WHERE "Id" = 1;
                INSERT INTO {_schema}.fact_policy_receipts ("RequestId", "RecordJson", "PayloadBytes", "EventId", "RetainUntil")
                    VALUES (@id, @json, @size, @event,
                        CASE WHEN @retain_has_fraction THEN @retain + INTERVAL '1 microsecond' ELSE @retain END);
                """))
            {
                // Outbox must exist before the receipt FK is checked; both remain in this transaction.
                if (fact is not null)
                {
                    await using var insert = NewCommand(connection, transaction, $"""
                        INSERT INTO {_schema}.outbox ("Id", "EventName", "Payload", "OccurredAt", "AttemptCount", "RetryRevision")
                            VALUES (@id, @name, @payload, @time, 0, 0)
                        """);
                    insert.Parameters.AddWithValue("id", fact.Id);
                    insert.Parameters.AddWithValue("name", fact.EventName);
                    insert.Parameters.AddWithValue("payload", fact.Payload);
                    insert.Parameters.AddWithValue("time", fact.OccurredAt);
                    await insert.ExecuteNonQueryAsync(budget.Token).ConfigureAwait(false);
                }
                update.Parameters.AddWithValue("records", next.MaxRecords);
                update.Parameters.AddWithValue("bytes", next.MaxPayloadBytes);
                update.Parameters.AddWithValue("single", next.MaxRecordPayloadBytes);
                update.Parameters.AddWithValue("revision", revision);
                update.Parameters.AddWithValue("size", size);
                update.Parameters.AddWithValue("id", request.RequestId);
                update.Parameters.AddWithValue("json", json);
                update.Parameters.Add("event", NpgsqlDbType.Uuid).Value = fact is null ? DBNull.Value : fact.Id;
                update.Parameters.AddWithValue("retain", receipt.RetainUntil);
                // PostgreSQL truncates sub-microsecond ticks; cleanup must never precede the advertised deadline.
                update.Parameters.AddWithValue("retain_has_fraction", receipt.RetainUntil.UtcTicks % 10 != 0);
                await update.ExecuteNonQueryAsync(budget.Token).ConfigureAwait(false);
            }
            await transaction.CommitAsync(budget.Token).ConfigureAwait(false);
            return Result.Success(receipt);
        }
        catch (PostgresException error) when (acquiringCapacityLedger && error.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Result.Failure<FactCapacityPolicyReceipt>(CommittedFactCapacityErrors.Busy);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Unavailable<FactCapacityPolicyReceipt>(); }
        catch (TimeoutException) { cancellationToken.ThrowIfCancellationRequested(); return Unavailable<FactCapacityPolicyReceipt>(); }
    }

    private NpgsqlConnection NewConnection() => new(new NpgsqlConnectionStringBuilder(connectionString)
    {
        CancellationTimeout = -1,
        Enlist = false
    }.ConnectionString);

    private static NpgsqlCommand NewCommand(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql)
        => new(sql, connection, transaction) { CommandTimeout = 0 };

    private static Result<T> Unavailable<T>() => Result.Failure<T>(CommittedFactCapacityErrors.Unavailable);
}
