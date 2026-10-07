using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Files.Application;
using NexusStackNext.Files.Contracts;
using NexusStackNext.Files.Domain.Stored;
using NexusStackNext.Files.Infrastructure.Persistence;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class FilesFactCapacityTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task ConcurrentRepositories_RespectLastSlot_NoOps_AndSameScopeRetryAfterConfirmedCleanup()
    {
        await using var database = await databases.CreateAsync();
        var options = new DbContextOptionsBuilder<FilesDbContext>()
            .UseNexusStackPostgres(database.ConnectionString, FilesDbContext.SchemaName).Options;
        await using var context = new FilesDbContext(options);
        await SetMaxRecordsAsync(database.ConnectionString, 1);
        await using var provider = FilesCommittedAuditTests.BuildStorage(database.ConnectionString);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(async index =>
        {
            await using var attempt = provider.CreateAsyncScope();
            var repository = attempt.ServiceProvider.GetRequiredService<IStoredFileRepository>();
            var candidate = StoredFile.Register(new StoredFileId(77000 + index), FileName.Create("quota.bin").Value,
                "application/octet-stream", "capacity-owner", DateTimeOffset.UtcNow).Value;
            try { await repository.SaveAsync(candidate); return (candidate.Id, Accepted: true); }
            catch (FileAuditCapacityException) { return (candidate.Id, Accepted: false); }
        }));
        var winner = Assert.Single(outcomes, outcome => outcome.Accepted);
        await using var scope = provider.CreateAsyncScope();
        var files = scope.ServiceProvider.GetRequiredService<IStoredFileRepository>();
        foreach (var rejected in outcomes.Where(outcome => !outcome.Accepted))
        {
            Assert.Null(await files.FindAsync(rejected.Id));
        }
        var file = await files.FindAsync(winner.Id);
        Assert.NotNull(file);
        await files.SaveAsync(file, 1); // 未发生状态变化，满额也可保存。
        Assert.True(file.MarkStored("capacity-storage-key", 3).IsSuccess);
        await Assert.ThrowsAsync<FileAuditCapacityException>(() => files.SaveAsync(file, 1));
        var unchanged = await files.FindAsync(file.Id);
        Assert.NotNull(unchanged);
        Assert.False(unchanged.IsStored);
        Assert.Equal(1, unchanged.Version);
        var store = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("files");
        var now = DateTimeOffset.UtcNow;
        var fact = Assert.Single(await store.ReadPendingAsync(10, now));
        await store.MarkDeliveredAsync(fact.Id, now);
        var cleanup = new EfCommittedFactCleanup<FilesDbContext>(context, StoredFileCommittedV1.Name, new(), new FixedClock(now));
        Assert.Equal(0, await cleanup.CleanupAsync());
        await Assert.ThrowsAsync<FileAuditCapacityException>(() => files.SaveAsync(file, 1));
        var expired = new EfCommittedFactCleanup<FilesDbContext>(context, StoredFileCommittedV1.Name, new(), new FixedClock(now.AddDays(8)));
        Assert.Equal(1, await expired.CleanupAsync());
        await files.SaveAsync(file, 1);
        var current = await files.FindAsync(file.Id);
        Assert.NotNull(current);
        Assert.True(current.IsStored);
        Assert.Equal(2, current.Version);
        var storedFact = Assert.Single(await store.ReadPendingAsync(10, DateTimeOffset.UtcNow));
        using var payload = JsonDocument.Parse(storedFact.Payload);
        Assert.Equal("stored", payload.RootElement.GetProperty("operation").GetString());
    }

    [PostgresFact]
    public async Task AcceptedDeletion_WhenCompletionFactCannotFit_RemainsRecoverableUntilCapacityReturns()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "files-capacity-password");
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "files-capacity-password");
        await SetMaxRecordsAsync(database.ConnectionString, 3);
        using var bytes = new ByteArrayContent([4, 5, 6]);
        using var upload = await client.PostAsync(new Uri("/api/files?name=deferred-capacity.bin", UriKind.Relative), bytes);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var id = (await upload.Content.ReadApiDataAsync()).GetProperty("fileId").ReadHttpInt64();

        using var deleted = await client.DeleteAsync(new Uri($"/api/files/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Accepted, deleted.StatusCode);
        Assert.False((await deleted.Content.ReadApiDataAsync()).GetProperty("completed").GetBoolean());
        using var repeated = await client.DeleteAsync(new Uri($"/api/files/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Accepted, repeated.StatusCode);
        using var hidden = await client.GetAsync(new Uri($"/api/files/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);

        await using var scope = app.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IStoredFileRepository>();
        var pending = Assert.Single(await repository.PendingDeletionsAsync(DateTimeOffset.UtcNow, 10));
        Assert.Equal(id, pending.Id.Value);
        Assert.Null(pending.BytesRemovedAt);
        Assert.Equal(3, pending.Version);
        var origin = await repository.ReadDeletionOriginAsync(pending.Id);
        Assert.NotNull(origin);
        var recovery = scope.ServiceProvider.GetRequiredService<FileRecovery>();
        Assert.False(await recovery.CompleteDeletionAsync(pending));
        Assert.False(await recovery.CompleteDeletionAsync(pending));
        await SetMaxRecordsAsync(database.ConnectionString, 4);
        await recovery.RunOnceAsync();
        using var state = await client.GetAsync(new Uri($"/api/files/{id}/deletion", UriKind.Relative));
        Assert.True((await state.Content.ReadApiDataAsync()).GetProperty("completed").GetBoolean());
        Assert.Equal(origin, await repository.ReadDeletionOriginAsync(pending.Id));
        Assert.Empty(await repository.PendingDeletionsAsync(DateTimeOffset.UtcNow, 10));
        using var completed = await client.DeleteAsync(new Uri($"/api/files/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("files");
        Assert.Equal(4, (await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow)).Count);
    }

    [PostgresFact]
    public async Task UploadBatch_WhenOnlyOneFactFits_IsRejectedWithoutPartialMetadataOrFacts()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "files-capacity-password");
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "files-capacity-password");
        await SetMaxRecordsAsync(database.ConnectionString, 1);

        using var bytes = new ByteArrayContent([1, 2, 3]);
        using var rejected = await client.PostAsync(new Uri("/api/files?name=private-capacity.bin", UriKind.Relative), bytes);
        await AssertCapacityFailureAsync(rejected);
        await using var scope = app.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("files");
        Assert.Empty(await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow));

        await SetMaxRecordsAsync(database.ConnectionString, 2);
        using var retryBytes = new ByteArrayContent([1, 2, 3]);
        using var accepted = await client.PostAsync(new Uri("/api/files?name=private-capacity.bin", UriKind.Relative), retryBytes);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var id = (await accepted.Content.ReadApiDataAsync()).GetProperty("fileId").ReadHttpInt64();
        using var downloaded = await client.GetAsync(new Uri($"/api/files/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode);
        Assert.Equal(new byte[] { 1, 2, 3 }, await downloaded.Content.ReadAsByteArrayAsync());
        Assert.Equal(2, (await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow)).Count);

        using var deletion = await client.DeleteAsync(new Uri($"/api/files/{id}", UriKind.Relative));
        await AssertCapacityFailureAsync(deletion);
        using var stillReadable = await client.GetAsync(new Uri($"/api/files/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, stillReadable.StatusCode);
        var repository = scope.ServiceProvider.GetRequiredService<IStoredFileRepository>();
        Assert.Null(await repository.FindDeletedAsync(new(id)));
        Assert.Null(await repository.ReadDeletionOriginAsync(new(id)));
        Assert.Equal(2, (await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow)).Count);
    }

    private static async Task SetMaxRecordsAsync(string connectionString, long limit)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE files.fact_capacity SET \"MaxRecords\" = @limit", connection);
        command.Parameters.AddWithValue("limit", limit);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task AssertCapacityFailureAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("files.audit_capacity.exhausted", error.GetProperty("errorCode").GetString());
        Assert.DoesNotContain("fact_capacity", error.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-capacity", error.GetRawText(), StringComparison.Ordinal);
    }
}
