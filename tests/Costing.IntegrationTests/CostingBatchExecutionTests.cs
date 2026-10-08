using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.Costing.Application;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.Costing.IntegrationTests;

public sealed class CostingBatchExecutionTests(CostingDatabaseFixture database) : IClassFixture<CostingDatabaseFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => database.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [PostgresFact]
    public async Task CancellationAndFinalRow_UseTheParentLockToChooseOneObservableWinner()
    {
        foreach (var (cancelFirst, totalRows) in new[] { (true, 2), (false, 2), (false, 3) })
        {
            await database.ResetAsync();
            await using var app = CreateApplication();
            await using var scope = app.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var batchId = Guid.NewGuid();
            var tail = Guid.NewGuid();
            var uncommitted = Guid.NewGuid();
            Assert.True((await sender.SendAsync(new AcceptCostBatch(batchId,
                new[] { new CostBatchInput(1, Guid.NewGuid(), 0, 80m, 20m), new(2, tail, 0, 10m, 20m), new(3, uncommitted, 0, 5m, 6m) }
                    .Take(totalRows).ToArray()))).IsSuccess);
            var lease = (await sender.SendAsync(new ClaimCostBatch())).Value!;
            Assert.True((await sender.SendAsync(new ExecuteCostBatchSegment(batchId, lease.Epoch))).Value);
            var cancelName = "nsn_batch_cancel_" + Guid.NewGuid().ToString("N");
            var rowName = "nsn_batch_row_" + Guid.NewGuid().ToString("N");
            await using var cancelling = CreateApplication(connectionString: new NpgsqlConnectionStringBuilder(database.ConnectionString) { ApplicationName = cancelName }.ConnectionString);
            await using var importing = CreateApplication(connectionString: new NpgsqlConnectionStringBuilder(database.ConnectionString) { ApplicationName = rowName }.ConnectionString);
            await using var cancelScope = cancelling.CreateAsyncScope();
            await using var rowScope = importing.CreateAsyncScope();
            var canceller = cancelScope.ServiceProvider.GetRequiredService<ISender>();
            var importer = rowScope.ServiceProvider.GetRequiredService<ISender>();
            await using var barrier = new NpgsqlConnection(database.ConnectionString);
            await barrier.OpenAsync();
            Task<NexusStackNext.BuildingBlocks.Domain.Result<CostBatchStatus>> cancel;
            Task<NexusStackNext.BuildingBlocks.Domain.Result<bool>> row;
            if (cancelFirst)
            {
                await using var hold = await barrier.BeginTransactionAsync();
                await using (var block = new NpgsqlCommand("SELECT 1 FROM costing.batches WHERE \"BatchId\" = @batch FOR UPDATE", barrier, hold))
                {
                    block.Parameters.AddWithValue("batch", batchId);
                    await block.ExecuteScalarAsync();
                }
                cancel = canceller.SendAsync(new CancelCostBatch(batchId, lease.Epoch));
                await WaitForLockAsync(barrier, cancelName);
                row = importer.SendAsync(new ExecuteCostBatchSegment(batchId, lease.Epoch));
                await WaitForLockAsync(barrier, rowName);
                await hold.CommitAsync();
            }
            else
            {
                await using (var install = new NpgsqlCommand("""
                    SELECT pg_advisory_lock(500051);
                    CREATE FUNCTION costing.pause_final_batch_row() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN PERFORM pg_advisory_xact_lock(500051); RETURN NEW; END $$;
                    CREATE TRIGGER pause_final_batch_row BEFORE UPDATE ON costing.batch_rows FOR EACH ROW EXECUTE FUNCTION costing.pause_final_batch_row();
                    """, barrier)) { await install.ExecuteNonQueryAsync(); }
                try
                {
                    row = importer.SendAsync(new ExecuteCostBatchSegment(batchId, lease.Epoch));
                    await WaitForLockAsync(barrier, rowName);
                    cancel = canceller.SendAsync(new CancelCostBatch(batchId, lease.Epoch));
                    await WaitForLockAsync(barrier, cancelName);
                }
                finally
                {
                    await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(500051)", barrier);
                    await release.ExecuteNonQueryAsync();
                }
            }
            try
            {
                var cancelled = await cancel;
                var imported = await row;
                var status = (await sender.QueryAsync(new GetCostBatch(batchId))).Value;
                if (cancelFirst)
                {
                    Assert.True(cancelled.IsSuccess);
                    Assert.False(imported.Value);
                    Assert.Equal("Cancelled", status.State);
                    Assert.Equal(1, status.Checkpoint);
                    Assert.True((await sender.QueryAsync(new GetCostSheet(tail))).IsFailure);
                }
                else
                {
                    Assert.True(imported.Value);
                    Assert.Equal(2, status.Checkpoint);
                    Assert.Equal(1, (await sender.QueryAsync(new GetCostSheet(tail))).Value.Version);
                    if (totalRows == 2)
                    {
                        Assert.Equal("costing.batch.cancel_conflict", cancelled.Error.Code);
                        Assert.Equal("Completed", status.State);
                    }
                    else
                    {
                        Assert.True(cancelled.IsSuccess);
                        Assert.Equal(2, cancelled.Value.Checkpoint);
                        Assert.Equal("Cancelled", status.State);
                        Assert.True((await sender.QueryAsync(new GetCostSheet(uncommitted))).IsFailure);
                    }
                }
            }
            finally
            {
                if (!cancelFirst)
                {
                    await using var cleanup = new NpgsqlCommand("DROP TRIGGER pause_final_batch_row ON costing.batch_rows; DROP FUNCTION costing.pause_final_batch_row()", barrier);
                    await cleanup.ExecuteNonQueryAsync();
                }
            }
        }
    }

    private static async Task WaitForLockAsync(NpgsqlConnection connection, string application)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (true)
        {
            await using var command = new NpgsqlCommand("SELECT pg_stat_clear_snapshot(); SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname = current_database() AND application_name = @application AND wait_event_type = 'Lock')", connection);
            command.Parameters.AddWithValue("application", application);
            await using var reader = await command.ExecuteReaderAsync(timeout.Token);
            Assert.True(await reader.NextResultAsync(timeout.Token));
            Assert.True(await reader.ReadAsync(timeout.Token));
            if (reader.GetBoolean(0)) { return; }
            await Task.Delay(10, timeout.Token);
        }
    }

    private ServiceProvider CreateApplication(CostingTaskOptions? options = null, int segmentSize = 1, string? connectionString = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexusStackApplication();
        services.AddCostingPostgres(connectionString ?? database.ConnectionString, options, new CostingBatchOptions { SegmentSize = segmentSize });
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    [PostgresFact]
    public async Task LostCommitReplies_AcceptanceAndTailRowAreReconciledThroughPublicQueriesAndReplay()
    {
        await using var app = CreateApplication();
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var batchId = Guid.NewGuid();
        var tailItem = Guid.NewGuid();
        Assert.True((await sender.SendAsync(new AcceptCostBatch(batchId,
            [new(1, Guid.NewGuid(), 0, 80m, 20m), new(2, tailItem, 0, 10m, 20m), new(3, Guid.NewGuid(), 0, 5m, 6m)]))).IsSuccess);
        var lease = (await sender.SendAsync(new ClaimCostBatch())).Value!;
        Assert.True((await sender.SendAsync(new ExecuteCostBatchSegment(batchId, lease.Epoch))).Value);
        Assert.Equal(1, (await sender.QueryAsync(new GetCostBatch(batchId))).Value.Checkpoint);
        var reserved = (await sender.QueryAsync(new ListCostBatchRows(batchId))).Value.Items[1].TaskId;
        var target = new NpgsqlConnectionStringBuilder(database.ConnectionString);
        await using var proxy = new TcpReplyDelayProxy(target.Host!, target.Port, postgresProtocol: true);
        var proxied = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Host = "127.0.0.1",
            Port = proxy.Port,
            Pooling = false,
            SslMode = SslMode.Disable,
        }.ConnectionString;
        await using var uncertainApp = CreateApplication(connectionString: proxied);
        await using var uncertainScope = uncertainApp.CreateAsyncScope();
        var uncertain = uncertainScope.ServiceProvider.GetRequiredService<ISender>();
        var request = new AcceptCostBatch(Guid.NewGuid(), [new(10, Guid.NewGuid(), 0, 5m, 6m)]);
        proxy.DropNextPostgresCommitReply();
        await Assert.ThrowsAnyAsync<NpgsqlException>(() => uncertain.SendAsync(request));
        Assert.True(proxy.CommitReplyDropped);
        var accepted = (await sender.QueryAsync(new GetCostBatch(request.BatchRequestId))).Value;
        Assert.Equal(accepted, (await sender.SendAsync(request)).Value);
        Assert.True((await sender.SendAsync(new CancelCostBatch(request.BatchRequestId, 0))).IsSuccess);
        proxy.DropNextPostgresCommitReply();
        await Assert.ThrowsAnyAsync<NpgsqlException>(() => uncertain.SendAsync(new ExecuteCostBatchSegment(batchId, lease.Epoch)));
        Assert.True(proxy.CommitReplyDropped);
        var reconciled = (await sender.QueryAsync(new GetCostBatch(batchId))).Value;
        Assert.Equal("Running", reconciled.State);
        Assert.Equal(2, reconciled.Checkpoint);
        Assert.Equal(1, (await sender.QueryAsync(new GetCostSheet(tailItem))).Value.Version);
        Assert.Equal(reserved, (await sender.QueryAsync(new GetCostCalculation(reserved!.Value))).Value.TaskId);
        Assert.True((await sender.SendAsync(new FailCostBatch(batchId, lease.Epoch))).Value);
        await Task.Delay(1100);
        var resumed = (await sender.SendAsync(new ClaimCostBatch())).Value!;
        Assert.Equal(2, resumed.Epoch);
        Assert.False((await sender.SendAsync(new ExecuteCostBatchSegment(batchId, lease.Epoch))).Value);
        Assert.True((await sender.SendAsync(new ExecuteCostBatchSegment(batchId, resumed.Epoch))).Value);
        Assert.Equal("Completed", (await sender.QueryAsync(new GetCostBatch(batchId))).Value.State);
        Assert.Equal(1, (await sender.QueryAsync(new GetCostSheet(tailItem))).Value.Version);
    }

    [PostgresFact]
    public async Task DeadlineExpiringDuringSave_RollsBackTailAndResumesFromTheCommittedCheckpoint()
    {
        await using var app = CreateApplication();
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var batchId = Guid.NewGuid();
        var tail = Guid.NewGuid();
        Assert.True((await sender.SendAsync(new AcceptCostBatch(batchId,
            [new(1, Guid.NewGuid(), 0, 80m, 20m), new(2, tail, 0, 10m, 20m)]))).IsSuccess);
        var lease = (await sender.SendAsync(new ClaimCostBatch())).Value!;
        Assert.True((await sender.SendAsync(new ExecuteCostBatchSegment(batchId, lease.Epoch))).Value);
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var inject = new NpgsqlCommand("""
            UPDATE costing.batches SET "LeaseUntil" = clock_timestamp() + interval '1.2 seconds',
              "MaxLeaseUntil" = clock_timestamp() + interval '1.2 seconds' WHERE "BatchId" = @batch;
            CREATE SEQUENCE costing.batch_expiry_probe;
            CREATE FUNCTION costing.expire_batch_row() RETURNS trigger LANGUAGE plpgsql AS $$
            DECLARE remaining double precision;
            BEGIN
              PERFORM nextval('costing.batch_expiry_probe');
              SELECT extract(epoch FROM "MaxLeaseUntil" - clock_timestamp()) INTO remaining
                FROM costing.batches WHERE "BatchId" = NEW."BatchId";
              IF remaining > 0 THEN PERFORM pg_sleep(remaining + 0.02); END IF;
              RETURN NEW;
            END $$;
            CREATE TRIGGER expire_batch_row BEFORE UPDATE ON costing.batch_rows FOR EACH ROW EXECUTE FUNCTION costing.expire_batch_row();
            """, connection))
        {
            inject.Parameters.AddWithValue("batch", batchId);
            await inject.ExecuteNonQueryAsync();
        }
        try
        {
            Assert.False((await sender.SendAsync(new ExecuteCostBatchSegment(batchId, lease.Epoch))).Value);
            await using (var probe = new NpgsqlCommand("SELECT is_called FROM costing.batch_expiry_probe", connection))
            {
                Assert.True((bool)(await probe.ExecuteScalarAsync())!, "The expiry fault must actually run during SaveChanges.");
            }
            Assert.Equal(1, (await sender.QueryAsync(new GetCostBatch(batchId))).Value.Checkpoint);
            Assert.True((await sender.QueryAsync(new GetCostSheet(tail))).IsFailure);
            Assert.Equal("Pending", (await sender.QueryAsync(new ListCostBatchRows(batchId))).Value.Items[1].Outcome);
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand("DROP TRIGGER expire_batch_row ON costing.batch_rows; DROP FUNCTION costing.expire_batch_row(); DROP SEQUENCE costing.batch_expiry_probe", connection);
            await cleanup.ExecuteNonQueryAsync();
        }
        var resumed = (await sender.SendAsync(new ClaimCostBatch())).Value!;
        Assert.Equal(2, resumed.Epoch);
        Assert.True((await sender.SendAsync(new ExecuteCostBatchSegment(batchId, resumed.Epoch))).Value);
        Assert.Equal(2, (await sender.QueryAsync(new GetCostBatch(batchId))).Value.Checkpoint);
    }

    [PostgresFact]
    public async Task UnchangedAndAllRejectedBatches_HaveCompleteCountsWithoutPretendingCalculationIsFinished()
    {
        await using var app = CreateApplication();
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var item = Guid.NewGuid();
        var manualId = Guid.NewGuid();
        Assert.True((await sender.SendAsync(new UpdateCostInputs(manualId, item, 0, 80m, 20m))).IsSuccess);
        Assert.Null((await sender.QueryAsync(new GetCostCalculation(manualId))).Value.Batch);
        var batchId = Guid.NewGuid();
        Assert.True((await sender.SendAsync(new AcceptCostBatch(batchId, [new(1, item, 1, 80m, 20m)]))).IsSuccess);
        var lease = (await sender.SendAsync(new ClaimCostBatch())).Value!;
        Assert.True((await sender.SendAsync(new ExecuteCostBatchSegment(batchId, lease.Epoch))).Value);
        var completed = (await sender.QueryAsync(new GetCostBatch(batchId))).Value;
        Assert.Equal("Completed", completed.State);
        Assert.Equal(1, completed.Unchanged);
        Assert.Equal(0, completed.Pending);
        Assert.Equal(1, (await sender.QueryAsync(new GetCostSheet(item))).Value.Version);
        Assert.Equal("Pending", Assert.Single((await sender.QueryAsync(new ListCostBatchRows(batchId))).Value.Items).CalculationState);
        var rejectedId = Guid.NewGuid();
        Assert.True((await sender.SendAsync(new AcceptCostBatch(rejectedId, [new(1, Guid.NewGuid(), 1, 10m, 20m)]))).IsSuccess);
        var rejectedLease = (await sender.SendAsync(new ClaimCostBatch())).Value!;
        Assert.True((await sender.SendAsync(new ExecuteCostBatchSegment(rejectedId, rejectedLease.Epoch))).Value);
        var rejected = (await sender.QueryAsync(new GetCostBatch(rejectedId))).Value;
        Assert.Equal("CompletedWithErrors", rejected.State);
        Assert.Equal(1, rejected.Rejected);
        Assert.Equal(0, rejected.Pending);
        Assert.Equal(0, rejected.Imported);
    }

    [PostgresFact]
    public async Task ReservedChildIdentity_CannotBeOccupiedByAManualRequest_BeforeImportOrAfterCancellation()
    {
        await using var app = CreateApplication();
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var batchId = Guid.NewGuid();
        Assert.True((await sender.SendAsync(new AcceptCostBatch(batchId, [new(1, Guid.NewGuid(), 0, 80m, 20m)]))).IsSuccess);
        var child = Assert.Single((await sender.QueryAsync(new ListCostBatchRows(batchId))).Value.Items).TaskId!.Value;
        var unrelatedItem = Guid.NewGuid();
        var manual = new UpdateCostInputs(child, unrelatedItem, 0, 10m, 20m);
        Assert.Equal("costing.request_conflict", (await sender.SendAsync(manual)).Error.Code);
        Assert.True((await sender.QueryAsync(new GetCostSheet(unrelatedItem))).IsFailure);
        Assert.True((await sender.SendAsync(new CancelCostBatch(batchId, 0))).IsSuccess);
        Assert.Equal("costing.request_conflict", (await sender.SendAsync(manual)).Error.Code);
    }

    [PostgresFact]
    public async Task AnyRowWriteFailure_RollsBackCostChildResultAndCheckpoint_WhilePreservingThePreviousRow()
    {
        foreach (var table in new[] { "tasks", "batch_rows", "batches" })
        {
            await database.ResetAsync();
            await using var app = CreateApplication();
            await using var scope = app.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var batchId = Guid.NewGuid();
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            Assert.True((await sender.SendAsync(new AcceptCostBatch(batchId, [new(1, first, 0, 80m, 20m), new(2, second, 0, 10m, 20m)]))).IsSuccess);
            var lease = (await sender.SendAsync(new ClaimCostBatch())).Value!;
            Assert.True((await sender.SendAsync(new ExecuteCostBatchSegment(batchId, lease.Epoch))).Value);
            var child = (await sender.QueryAsync(new ListCostBatchRows(batchId))).Value.Items[1].TaskId!.Value;
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync();
            var operation = table == "tasks" ? "INSERT" : "UPDATE";
            await using (var inject = new NpgsqlCommand($"""
                CREATE FUNCTION costing.reject_batch_write() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'controlled batch write failure'; END $$;
                CREATE TRIGGER reject_batch_write BEFORE {operation} ON costing.{table}
                  FOR EACH ROW EXECUTE FUNCTION costing.reject_batch_write();
                """, connection)) { await inject.ExecuteNonQueryAsync(); }
            try
            {
                await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() => sender.SendAsync(new ExecuteCostBatchSegment(batchId, lease.Epoch)));
                Assert.Equal(1, (await sender.QueryAsync(new GetCostBatch(batchId))).Value.Checkpoint);
                Assert.Equal(80m, (await sender.QueryAsync(new GetCostSheet(first))).Value.PurchaseCost);
                Assert.True((await sender.QueryAsync(new GetCostSheet(second))).IsFailure);
                Assert.True((await sender.QueryAsync(new GetCostCalculation(child))).IsFailure);
                Assert.Equal("Pending", (await sender.QueryAsync(new ListCostBatchRows(batchId))).Value.Items[1].Outcome);
            }
            finally
            {
                await using var cleanup = new NpgsqlCommand($"DROP TRIGGER reject_batch_write ON costing.{table}; DROP FUNCTION costing.reject_batch_write()", connection);
                await cleanup.ExecuteNonQueryAsync();
            }
            Assert.True((await sender.SendAsync(new ExecuteCostBatchSegment(batchId, lease.Epoch))).Value);
            Assert.Equal("Completed", (await sender.QueryAsync(new GetCostBatch(batchId))).Value.State);
        }
    }

    [PostgresFact]
    public async Task LongSegment_RenewsBeforeTheOldDeadline_WhileKeepingItsFixedExecutionBudget()
    {
        await using var app = CreateApplication(segmentSize: 5);
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var batchId = Guid.NewGuid();
        var rows = Enumerable.Range(1, 5).Select(x => new CostBatchInput(x, Guid.NewGuid(), 0, 80m, 20m)).ToArray();
        Assert.True((await sender.SendAsync(new AcceptCostBatch(batchId, rows))).IsSuccess);
        var lease = (await sender.SendAsync(new ClaimCostBatch())).Value!;
        var original = (await sender.QueryAsync(new GetCostBatch(batchId))).Value;
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var prepare = new NpgsqlCommand("""
            UPDATE costing.batches SET "LeaseUntil" = clock_timestamp() + interval '3 seconds' WHERE "BatchId" = @batch;
            CREATE FUNCTION costing.slow_batch_row() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN PERFORM pg_sleep(0.7); RETURN NEW; END $$;
            CREATE TRIGGER slow_batch_row BEFORE UPDATE ON costing.batch_rows FOR EACH ROW EXECUTE FUNCTION costing.slow_batch_row();
            """, connection))
        {
            prepare.Parameters.AddWithValue("batch", batchId);
            await prepare.ExecuteNonQueryAsync();
        }
        try
        {
            Assert.True((await sender.SendAsync(new ExecuteCostBatchSegment(batchId, lease.Epoch))).Value);
            var completed = (await sender.QueryAsync(new GetCostBatch(batchId))).Value;
            Assert.Equal("Completed", completed.State);
            Assert.Equal(original.MaxLeaseUntil, completed.MaxLeaseUntil);
            Assert.Equal(1, completed.Epoch);
            Assert.Equal(5, completed.Checkpoint);
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand("DROP TRIGGER slow_batch_row ON costing.batch_rows; DROP FUNCTION costing.slow_batch_row()", connection);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task LeaseRenewal_PreservesFixedDeadline_AndTakeoverRejectsEveryOldWritePath()
    {
        await using var app = CreateApplication();
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var batchId = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Assert.True((await sender.SendAsync(new AcceptCostBatch(batchId, [new(1, first, 0, 80m, 20m), new(2, second, 0, 10m, 20m)]))).IsSuccess);
        var lease = (await sender.SendAsync(new ClaimCostBatch())).Value!;
        var original = (await sender.QueryAsync(new GetCostBatch(batchId))).Value;
        Assert.True((await sender.SendAsync(new ExecuteCostBatchSegment(batchId, lease.Epoch))).Value);
        var renewed = (await sender.SendAsync(new RenewCostBatch(batchId, lease.Epoch))).Value;
        Assert.True(renewed.ExpiresAt > lease.ExpiresAt);
        Assert.Equal(original.MaxLeaseUntil, (await sender.QueryAsync(new GetCostBatch(batchId))).Value.MaxLeaseUntil);
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var expiry = new NpgsqlCommand("UPDATE costing.batches SET \"LeaseUntil\" = clock_timestamp() - interval '1 second' WHERE \"BatchId\" = @batch", connection);
            expiry.Parameters.AddWithValue("batch", batchId);
            await expiry.ExecuteNonQueryAsync();
        }
        Assert.Equal("costing.batch.renew_conflict", (await sender.SendAsync(new RenewCostBatch(batchId, 1))).Error.Code);
        var takeover = (await sender.SendAsync(new ClaimCostBatch())).Value!;
        Assert.Equal(2, takeover.Epoch);
        Assert.False((await sender.SendAsync(new ExecuteCostBatchSegment(batchId, 1))).Value);
        Assert.False((await sender.SendAsync(new FailCostBatch(batchId, 1))).Value);
        Assert.Equal("costing.batch.renew_conflict", (await sender.SendAsync(new RenewCostBatch(batchId, 1))).Error.Code);
        Assert.Equal("costing.batch.cancel_conflict", (await sender.SendAsync(new CancelCostBatch(batchId, 1))).Error.Code);
        Assert.True((await sender.SendAsync(new ExecuteCostBatchSegment(batchId, takeover.Epoch))).Value);
        Assert.Equal("Completed", (await sender.QueryAsync(new GetCostBatch(batchId))).Value.State);
        Assert.Equal(1, (await sender.QueryAsync(new GetCostSheet(first))).Value.Version);
    }

    [PostgresFact]
    public async Task FailedBatch_ConditionalRetryContinuesCheckpointWithStableChildIdentity_AfterApplicationRestart()
    {
        var batchId = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Guid? firstChild = null;
        Guid? secondChild = null;
        await using (var app = CreateApplication(new CostingTaskOptions { MaxAttempts = 1 }))
        {
            await using var scope = app.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            Assert.True((await sender.SendAsync(new AcceptCostBatch(batchId, [new(1, first, 0, 80m, 20m), new(2, second, 0, 10m, 20m)]))).IsSuccess);
            var lease = (await sender.SendAsync(new ClaimCostBatch())).Value!;
            var rows = (await sender.QueryAsync(new ListCostBatchRows(batchId))).Value.Items;
            firstChild = rows[0].TaskId;
            secondChild = rows[1].TaskId;
            Assert.True((await sender.SendAsync(new ExecuteCostBatchSegment(batchId, lease.Epoch))).Value);
            Assert.True((await sender.SendAsync(new FailCostBatch(batchId, lease.Epoch))).Value);
            Assert.Equal("Failed", (await sender.QueryAsync(new GetCostBatch(batchId))).Value.State);
        }
        await using var reopened = CreateApplication(segmentSize: 2);
        await using var read = reopened.CreateAsyncScope();
        var query = read.ServiceProvider.GetRequiredService<ISender>();
        Assert.Equal("costing.batch.retry_conflict", (await query.SendAsync(new RetryCostBatch(batchId, 0))).Error.Code);
        Assert.Equal(1, (await query.SendAsync(new RetryCostBatch(batchId, 1))).Value.Checkpoint);
        var recovered = (await query.SendAsync(new ClaimCostBatch())).Value!;
        Assert.Equal(2, recovered.Epoch);
        Assert.False((await query.SendAsync(new ExecuteCostBatchSegment(batchId, 1))).Value);
        Assert.True((await query.SendAsync(new ExecuteCostBatchSegment(batchId, 2))).Value);
        Assert.Equal("Completed", (await query.QueryAsync(new GetCostBatch(batchId))).Value.State);
        var finalRows = (await query.QueryAsync(new ListCostBatchRows(batchId))).Value.Items;
        Assert.Equal(new[] { firstChild, secondChild }, finalRows.Select(x => x.TaskId));
        Assert.Equal(1, (await query.QueryAsync(new GetCostSheet(first))).Value.Version);
        Assert.Equal(1, (await query.QueryAsync(new GetCostSheet(second))).Value.Version);
        var history = (await query.QueryAsync(new ListCostBatchAttempts(batchId, Limit: 1))).Value;
        Assert.Equal(2, history.Total);
        Assert.Equal("Failed", Assert.Single(history.Items).Outcome);
        Assert.Equal("Completed", Assert.Single((await query.QueryAsync(new ListCostBatchAttempts(batchId, Page: 2, Limit: 1))).Value.Items).Outcome);
    }

    [PostgresFact]
    public async Task Cancellation_PreservesCommittedRowAndChildCalculation_AndRejectsOldExecution()
    {
        await using var app = CreateApplication();
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var batchId = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Assert.True((await sender.SendAsync(new AcceptCostBatch(batchId,
            [new(10, first, 0, 80m, 20m), new(20, second, 0, 10m, 20m)]))).IsSuccess);
        var lease = (await sender.SendAsync(new ClaimCostBatch())).Value!;
        Assert.True((await sender.SendAsync(new ExecuteCostBatchSegment(batchId, lease.Epoch))).Value);
        var cancelled = (await sender.SendAsync(new CancelCostBatch(batchId, lease.Epoch))).Value;
        Assert.Equal("Cancelled", cancelled.State);
        Assert.Equal(1, cancelled.Checkpoint);
        Assert.Equal(cancelled, (await sender.SendAsync(new CancelCostBatch(batchId, lease.Epoch))).Value);
        Assert.False((await sender.SendAsync(new ExecuteCostBatchSegment(batchId, lease.Epoch))).Value);
        Assert.False((await sender.SendAsync(new FailCostBatch(batchId, lease.Epoch))).Value);
        Assert.True((await sender.QueryAsync(new GetCostSheet(second))).IsFailure);
        var child = (await sender.SendAsync(new ClaimCostingWork())).Value!;
        Assert.Equal(new CostBatchReference(batchId, 1, 10), (await sender.QueryAsync(new GetCostCalculation(child.TaskId))).Value.Batch);
        Assert.True((await sender.SendAsync(new CompleteCostingWork(child.TaskId, child.Epoch))).Value);
        Assert.Equal(100m, (await sender.QueryAsync(new GetCostSheet(first))).Value.UnitCost);
        Assert.Equal("Cancelled", (await sender.SendAsync(new AcceptCostBatch(batchId,
            [new(10, first, 0, 80m, 20m), new(20, second, 0, 10m, 20m)]))).Value.State);
        Assert.Null((await sender.SendAsync(new ClaimCostBatch())).Value);
    }
}
