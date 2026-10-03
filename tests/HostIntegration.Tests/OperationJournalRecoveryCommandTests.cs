using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.Auditing.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.CostingHost;
using NexusStackNext.Gateway;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.PlatformHost;
using NexusStackNext.PricingHost;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class OperationJournalRecoveryCommandTests
{
    [PostgresFact]
    public async Task RecoveryCommand_ReplaysAcrossHosts_RecordsSystemActor_AndHonorsConfiguredPolicy()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await OperationJournalDatabase.MigrateAsync(database.ConnectionString);
        var services = new ServiceCollection();
        services.AddOperationJournalPostgresStorage(database.ConnectionString);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var stoppedAt = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var messages = new[] { Guid.NewGuid(), Guid.NewGuid() };
        foreach (var messageId in messages)
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<IOperationJournal>().AppendAsync(new OperationObservedV1
            {
                EventId = messageId,
                OperationId = Guid.NewGuid(),
                Source = "pricing",
                Kind = "http",
                Phase = "started",
                TraceId = "private-command-payload",
                HttpMethod = "POST",
                OccurredAt = stoppedAt,
            })).IsSuccess);
            var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
            Assert.True(await outbox.MarkDeadLetteredAsync(messageId, "private-command-failure", stoppedAt, 0));
        }
        var requestId = Guid.NewGuid();
        string? firstReceipt = null;
        foreach (var host in new[] { typeof(PricingHostMarker), typeof(CostingHostMarker), typeof(PlatformHostMarker), typeof(GatewayHostMarker) })
        {
            var start = Start(host, database.ConnectionString, "retry", requestId.ToString(), messages[0].ToString(),
                stoppedAt.ToString("O", CultureInfo.InvariantCulture), "0", "dependency-restored");
            var result = await BusinessProcess.RunToExitAsync(start);
            Assert.Equal(0, result.ExitCode);
            firstReceipt ??= result.Output;
            Assert.Equal(firstReceipt, result.Output);
            var receipt = JsonSerializer.Deserialize<OperationJournalRecoveryReceipt>(result.Output, JsonSerializerOptions.Web)!;
            Assert.Equal(requestId, receipt.Request.RequestId);
            Assert.Equal(new OperationJournalRecoveryActor(Environment.UserName, Environment.MachineName), receipt.Actor);
            Assert.Equal(1, receipt.RetryRevision);
            Assert.Equal("pricing", receipt.Source);
            Assert.Equal(TimeSpan.FromHours(2), receipt.RetainUntil - receipt.RecoveredAt);
            Assert.DoesNotContain("private-command", result.Output, StringComparison.Ordinal);
            var lookup = await BusinessProcess.RunToExitAsync(Start(host, database.ConnectionString, "receipt", requestId.ToString()));
            Assert.Equal(0, lookup.ExitCode);
            Assert.Equal(firstReceipt, lookup.Output);
        }
        var maintenance = scope.ServiceProvider.GetRequiredService<IOperationJournalMaintenance>();
        var delivery = (await maintenance.GetDeliveryAsync(messages[0])).Value;
        Assert.Equal("Pending", delivery.State);
        Assert.Equal(1, delivery.RetryRevision);

        var full = await BusinessProcess.RunToExitAsync(Start(typeof(PricingHostMarker), database.ConnectionString,
            "retry", Guid.NewGuid().ToString(), messages[1].ToString(), stoppedAt.ToString("O", CultureInfo.InvariantCulture), "0", "manual-retry"));
        Assert.Equal(3, full.ExitCode);
        Assert.Equal("operation_journal.recovery_capacity_exceeded", full.Output.Trim());
        Assert.Equal("DeadLettered", (await maintenance.GetDeliveryAsync(messages[1])).Value.State);

        var conflict = await BusinessProcess.RunToExitAsync(Start(typeof(PricingHostMarker), database.ConnectionString,
            "retry", requestId.ToString(), messages[0].ToString(), stoppedAt.ToString("O", CultureInfo.InvariantCulture), "0", "manual-retry"));
        Assert.Equal(3, conflict.ExitCode);
        Assert.Equal("operation_journal.recovery_request_conflict", conflict.Output.Trim());

        var stale = await BusinessProcess.RunToExitAsync(Start(typeof(PricingHostMarker), database.ConnectionString,
            "retry", Guid.NewGuid().ToString(), messages[0].ToString(), stoppedAt.ToString("O", CultureInfo.InvariantCulture), "0", "manual-retry"));
        Assert.Equal(3, stale.ExitCode);
        Assert.Equal("operation_journal.delivery_conflict", stale.Output.Trim());

        var invalidPolicy = Start(typeof(PricingHostMarker), database.ConnectionString,
            "retry", Guid.NewGuid().ToString(), messages[1].ToString(), stoppedAt.ToString("O", CultureInfo.InvariantCulture), "0", "manual-retry");
        invalidPolicy.Environment["OperationJournal__Cleanup__RecoveryRetention"] = "private-invalid-policy-marker";
        var invalid = await BusinessProcess.RunToExitAsync(invalidPolicy);
        Assert.Equal(1, invalid.ExitCode);
        Assert.Equal("operation_journal.command_failed", invalid.Output.Trim());
        Assert.Equal("DeadLettered", (await maintenance.GetDeliveryAsync(messages[1])).Value.State);

        var utcReplay = await BusinessProcess.RunToExitAsync(Start(typeof(PricingHostMarker), database.ConnectionString,
            "retry", requestId.ToString(), messages[0].ToString(), "2026-10-03T00:00:00Z", "0", "dependency-restored"));
        Assert.Equal(0, utcReplay.ExitCode);
        Assert.Equal(firstReceipt, utcReplay.Output);

        var missing = await BusinessProcess.RunToExitAsync(Start(typeof(PricingHostMarker), database.ConnectionString, "receipt", Guid.NewGuid().ToString()));
        Assert.Equal(3, missing.ExitCode);
        Assert.Equal("operation_journal.recovery_not_found", missing.Output.Trim());
    }

    [Theory]
    [InlineData("2026-10-03T00:00:00", "0", "manual-retry")]
    [InlineData("2026-10-03T00:00:00Z", "-1", "manual-retry")]
    [InlineData("2026-10-03T00:00:00Z", "9223372036854775808", "manual-retry")]
    [InlineData("2026-10-03T00:00:00Z", "0", "private-freeform-reason")]
    [InlineData("invalid-timestamp", "0", "manual-retry")]
    public async Task RecoveryCommand_RejectsUnsafeOrIncompletePreconditionsBeforeReadingConfiguration(string stoppedAt, string revision, string reason)
    {
        var start = Start(typeof(PricingHostMarker), "invalid;Password=<private-configuration-marker>",
            "retry", Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), stoppedAt, revision, reason);
        var result = await BusinessProcess.RunToExitAsync(start);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("operation_journal.command_invalid", result.Output.Trim());
    }

    [Theory]
    [InlineData("missing-state")]
    [InlineData("actor-override")]
    [InlineData("empty-request")]
    [InlineData("empty-message")]
    public async Task RecoveryCommand_RequiresAllPreconditions_AndDoesNotAcceptCallerSuppliedActor(string mutation)
    {
        var arguments = new List<string>
        {
            "retry", Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "2026-10-03T00:00:00Z", "0", "manual-retry",
        };
        switch (mutation)
        {
            case "missing-state": arguments.RemoveAt(3); break;
            case "actor-override": arguments.Add("--actor=administrator"); break;
            case "empty-request": arguments[1] = Guid.Empty.ToString(); break;
            case "empty-message": arguments[2] = Guid.Empty.ToString(); break;
        }
        var result = await BusinessProcess.RunToExitAsync(Start(typeof(PricingHostMarker), "invalid;Password=<private-configuration-marker>", [.. arguments]));
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("operation_journal.command_invalid", result.Output.Trim());
    }

    private static ProcessStartInfo Start(Type host, string connection, params string[] arguments)
    {
        var start = BusinessProcess.StartInfo(host.Assembly.Location, "Pricing", "invalid business configuration");
        start.ArgumentList.Add("operation-journal");
        foreach (var argument in arguments) { start.ArgumentList.Add(argument); }
        start.Environment["ConnectionStrings__OperationJournal"] = connection;
        start.Environment["OperationJournal__Capacity__MaxRecoveryRecords"] = "1";
        start.Environment["OperationJournal__Cleanup__RecoveryRetention"] = "02:00:00";
        return start;
    }
}
