using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Costing.Endpoints;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.CostingHost;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Endpoints;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.PricingHost;
using NexusStackNext.TestSupport;
using Npgsql;
using CostingPolicyMigration = NexusStackNext.Costing.Infrastructure.Migrations.AuditedFactCapacityPolicy;
using PricingPolicyMigration = NexusStackNext.Pricing.Infrastructure.Migrations.AuditedFactCapacityPolicy;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class FactCapacityPolicyBusinessMigrationTests
{
    [PostgresFact]
    public Task CostingPolicyMigration_PreservesOldCostTaskPolicyAndUsage_AndRejectsRollbackOfAcceptedHistory()
        => VerifyAsync(costing: true);

    [PostgresFact]
    public Task PricingPolicyMigration_PreservesOldCostTaskPolicyAndUsage_AndRejectsRollbackOfAcceptedHistory()
        => VerifyAsync(costing: false);

    private static async Task VerifyAsync(bool costing)
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var context = costing ? "Costing" : "Pricing";
        var source = costing ? "costing" : "pricing";
        var assembly = costing ? typeof(CostingHostMarker).Assembly.Location : typeof(PricingHostMarker).Assembly.Location;
        var previousMigration = costing ? "20261004070201_FactCapacityWaitBudget" : "20261004070409_FactCapacityWaitBudget";
        var policyMigrationId = costing ? "20261004164558_AuditedFactCapacityPolicy" : "20261004164907_AuditedFactCapacityPolicy";
        if (costing) { await CostingDatabase.MigrateAsync(database.ConnectionString, deadline.Token); }
        else { await PricingDatabase.MigrateAsync(database.ConnectionString, deadline.Token); }
        var itemId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var policyPath = new Uri($"/api/{source}/audit-capacity", UriKind.Relative);
        var itemPath = new Uri($"/api/{source}/items/{itemId}", UriKind.Relative);
        var taskPath = new Uri($"/api/{source}/tasks/{taskId}", UriKind.Relative);
        string originalItem;
        string originalTask;
        JsonElement originalCapacity;
        await using (var original = await BusinessProcess.StartAsync(assembly, context, database.ConnectionString))
        {
            original.Authenticate();
            object inputs = costing
                ? new { requestId = taskId, itemId, expectedVersion = "0", purchaseCost = 80m, freightCost = 20m }
                : new { requestId = taskId, itemId, expectedVersion = "0", cost = 80m, feeRate = 0.2m };
            using var created = await original.Client.PostAsJsonAsync(new Uri($"/api/{source}/cost", UriKind.Relative), inputs, deadline.Token);
            Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
            originalItem = (await ReadAsync(original.Client, itemPath, deadline.Token)).GetRawText();
            originalTask = (await ReadAsync(original.Client, taskPath, deadline.Token)).GetRawText();
            originalCapacity = await ReadAsync(original.Client, policyPath, deadline.Token);
            Assert.Equal(1, originalCapacity.GetProperty("retainedRecords").ReadHttpInt64());
        }
        await using var module = CreateModule(context, database.ConnectionString);
        await using var scope = module.Services.CreateAsyncScope();
        Migration policyMigration = costing ? new CostingPolicyMigration() : new PricingPolicyMigration();
        // Public EF migration metadata identifies the owning context; do not expose an internal DbContext for tests.
        var contextType = policyMigration.GetType().GetCustomAttribute<DbContextAttribute>()?.ContextType;
        Assert.NotNull(contextType);
        var ownedContext = Assert.IsAssignableFrom<DbContext>(scope.ServiceProvider.GetRequiredService(contextType));
        var migrator = ownedContext.GetService<IMigrator>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var originalFacts = await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue, deadline.Token);
        Assert.Single(originalFacts);
        await migrator.MigrateAsync(previousMigration, deadline.Token);
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(deadline.Token);
            await using var arrange = new NpgsqlCommand($"""
                UPDATE {source}.fact_capacity SET "MaxRecords" = 3, "MaxPayloadBytes" = 8192, "MaxRecordPayloadBytes" = 2048
                """, connection);
            Assert.Equal(1, await arrange.ExecuteNonQueryAsync(deadline.Token));
        }
        await migrator.MigrateAsync(cancellationToken: deadline.Token);
        Assert.False(ownedContext.Database.HasPendingModelChanges());
        await using var upgraded = await BusinessProcess.StartAsync(assembly, context, database.ConnectionString);
        upgraded.Authenticate();
        var initial = await ReadAsync(upgraded.Client, policyPath, deadline.Token);
        Assert.Equal(1, initial.GetProperty("policyRevision").ReadHttpInt64());
        Assert.Equal(3, initial.GetProperty("maxRecords").ReadHttpInt64());
        Assert.Equal(8192, initial.GetProperty("maxPayloadBytes").ReadHttpInt64());
        Assert.Equal(2048, initial.GetProperty("maxRecordPayloadBytes").GetInt32());
        Assert.Equal(1, initial.GetProperty("retainedRecords").ReadHttpInt64());
        Assert.Equal(originalCapacity.GetProperty("retainedPayloadBytes").ReadHttpInt64(), initial.GetProperty("retainedPayloadBytes").ReadHttpInt64());
        Assert.Equal(0, initial.GetProperty("controlCapacity").GetProperty("retainedRecords").ReadHttpInt64());
        var request = new FactCapacityPolicyRequest(Guid.Parse("00000000-0000-0000-0000-000000000014"), 1, 4, 8192, 2048, "operator-adjustment");
        using var accepted = await upgraded.Client.PutAsJsonAsync(policyPath, request, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var receipt = await accepted.Content.ReadApiDataAsync();
        await migrator.MigrateAsync(cancellationToken: deadline.Token);
        using var replay = await upgraded.Client.PutAsJsonAsync(policyPath, request, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(receipt.GetRawText(), (await replay.Content.ReadApiDataAsync()).GetRawText());
        var refusal = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync(previousMigration, deadline.Token));
        Assert.Equal(PostgresErrorCodes.RaiseException, refusal.SqlState);
        Assert.Equal($"{source}_fact_policy_history_exists", refusal.ConstraintName);
        Assert.Contains(policyMigrationId, await ownedContext.Database.GetAppliedMigrationsAsync(deadline.Token));
        Assert.Equal(originalItem, (await ReadAsync(upgraded.Client, itemPath, deadline.Token)).GetRawText());
        Assert.Equal(originalTask, (await ReadAsync(upgraded.Client, taskPath, deadline.Token)).GetRawText());
        var after = await ReadAsync(upgraded.Client, policyPath, deadline.Token);
        Assert.Equal(2, after.GetProperty("policyRevision").ReadHttpInt64());
        Assert.Equal(4, after.GetProperty("maxRecords").ReadHttpInt64());
        Assert.Equal(1, after.GetProperty("retainedRecords").ReadHttpInt64());
        Assert.Equal(originalCapacity.GetProperty("retainedPayloadBytes").ReadHttpInt64(), after.GetProperty("retainedPayloadBytes").ReadHttpInt64());
        Assert.Equal(1, after.GetProperty("controlCapacity").GetProperty("retainedRecords").ReadHttpInt64());
        var preserved = await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue, deadline.Token);
        Assert.Equal(2, preserved.Count);
        Assert.All(originalFacts, fact => Assert.Contains(fact, preserved));
        Assert.Equal(receipt.GetProperty("eventId").GetGuid(), Assert.Single(preserved, fact => fact.EventName == $"{source}.fact-capacity-policy-changed.v1").Id);
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, Uri path, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadApiDataAsync();
    }

    private static WebApplication CreateModule(string context, string connection)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{context}"] = connection,
            [$"{context}:Messaging:Enabled"] = "false",
            [$"{context}:Worker:Enabled"] = "false",
            [$"{context}:AuditDelivery:Cleanup:Enabled"] = "false",
            [$"{context}:AuditDelivery:PolicyMaintenance:Enabled"] = "false",
        });
        builder.Services.AddNexusStackApplication();
        builder.Services.AddSingleton<ICurrentUser>(new FixedCurrentUser(null));
        builder.Services.AddSingleton<IClock>(new FixedClock(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero)));
        builder.Services.AddSingleton<IIntegrationEventSerializer>(new SystemTextJsonIntegrationEventSerializer());
        builder.Services.AddSingleton<IIntegrationEventMapper, NoIntegrationEventsMapper>();
        if (context == "Costing") { builder.Services.AddCostingModule(builder.Configuration); }
        else { builder.Services.AddPricingModule(builder.Configuration); }
        return builder.Build();
    }
}
