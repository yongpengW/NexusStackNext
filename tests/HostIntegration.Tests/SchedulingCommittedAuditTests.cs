using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Ids;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Contracts;
using NexusStackNext.Scheduling.Domain.Tasks;
using NexusStackNext.Scheduling.Infrastructure;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class SchedulingCommittedAuditTests(JourneyDatabaseTemplates databases)
{
    [AuditBrokerFact]
    public async Task PlanFacts_SurviveRestart_AndDeliverManagementAndSystemDecisionsThroughRabbitMq()
    {
        await using var database = await databases.CreateAsync();
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-scheduling", ClientName = prefix };
        var topology = EventTopology.Create(broker.ExchangeName,
        [
            new EventSubscription { EventName = PlanCommittedV1.Name, ConsumerName = prefix + "-scheduling" },
            new EventSubscription { EventName = "platform.setting-committed.v1", ConsumerName = prefix },
        ]);
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            long id;
            Guid decisionId;
            await using (var source = await PlatformHostProcess.StartAsync(database.ConnectionString, "scheduling-root-password"))
            {
                await PlatformSettingsAccessTests.LoginAsync(source.Client, "journey-root", "scheduling-root-password");
                source.Client.DefaultRequestHeaders.Add("X-Correlation-ID", "restarted-scheduling-facts");
                using var created = await source.Client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative), new
                {
                    code = "private-restarted-plan",
                    intervalSeconds = 3600,
                    targetKind = "costing.recalculate",
                    targetId = Guid.NewGuid(),
                });
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                id = (await created.Content.ReadApiDataAsync()).GetProperty("taskId").ReadHttpInt64();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (true)
                {
                    using var response = await source.Client.GetAsync(new Uri($"/api/scheduling/tasks/{id}/decisions", UriKind.Relative), timeout.Token);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    var decisions = (await response.Content.ReadApiDataAsync()).EnumerateArray().ToArray();
                    if (decisions.Length == 0) { await Task.Delay(100, timeout.Token); continue; }
                    decisionId = Assert.Single(decisions).GetProperty("decisionId").GetGuid();
                    break;
                }
                using var paused = await source.Client.PostAsJsonAsync(new Uri($"/api/scheduling/tasks/{id}/pause", UriKind.Relative), new { expectedVersion = 2 });
                Assert.Equal(HttpStatusCode.NoContent, paused.StatusCode);
                await source.CrashAsync();
            }
            await using var resumed = await PlatformHostProcess.StartAsync(database.ConnectionString, "scheduling-root-password",
                settings: AuditBusinessJourneyTests.Settings(broker, prefix));
            await PlatformSettingsAccessTests.LoginAsync(resumed.Client, "journey-root", "scheduling-root-password");
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            JsonElement[] facts;
            while (true)
            {
                using var response = await resumed.Client.GetAsync(new Uri($"/api/auditing/entries?source=scheduling&subjectId={id}", UriKind.Relative), budget.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                facts = (await response.Content.ReadApiDataAsync()).EnumerateArray().Select(entry => entry.GetProperty("fact").Clone())
                    .OrderBy(fact => fact.GetProperty("subjectVersion").ReadHttpInt64()).ToArray();
                Assert.True(facts.Length <= 3, "重复交付不能增加计划提交事实。");
                if (facts.Length == 3) { break; }
                await Task.Delay(100, budget.Token);
            }
            Assert.Equal(new[] { "scheduling.plan.created", "scheduling.plan.triggered", "scheduling.plan.disabled" }, facts.Select(fact => fact.GetProperty("action").GetString()));
            Assert.Equal(new long[] { 1, 2, 3 }, facts.Select(fact => fact.GetProperty("subjectVersion").ReadHttpInt64()));
            var triggered = facts[1];
            Assert.Equal(JsonValueKind.Null, triggered.GetProperty("actorId").ValueKind);
            Assert.Equal(facts[0].GetProperty("actorId").GetString(), triggered.GetProperty("execution").GetProperty("initiatorId").GetString());
            Assert.Equal(facts[0].GetProperty("execution").GetProperty("rootOperationId").GetGuid(), triggered.GetProperty("execution").GetProperty("rootOperationId").GetGuid());
            Assert.Equal("scheduling", triggered.GetProperty("relatedSubject").GetProperty("context").GetString());
            Assert.Equal("schedule-decision", triggered.GetProperty("relatedSubject").GetProperty("type").GetString());
            Assert.Equal(decisionId.ToString("D"), triggered.GetProperty("relatedSubject").GetProperty("id").GetString());
            Assert.All(facts, fact =>
            {
                Assert.Equal("scheduled-task", fact.GetProperty("subjectType").GetString());
                Assert.Equal("restarted-scheduling-facts", fact.GetProperty("correlationId").GetString());
                Assert.DoesNotContain("private-restarted-plan", fact.GetRawText(), StringComparison.Ordinal);
            });
        }
        finally { await AuditBusinessJourneyTests.DeleteTopologyAsync(broker, topology); }
    }

    [Fact]
    public async Task FailureBackoffAndRecovery_AreCommittedFacts_AndRepeatedStateAddsNothing()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        await AssertBackoffAsync(app.Services);
    }

    [PostgresFact]
    public async Task PostgresFailureBackoffAndRecovery_AreCommittedFacts_AndRepeatedStateAddsNothing()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "schedule-root-password", schedulingWorkerEnabled: false);
        await AssertBackoffAsync(app.Services);
    }

    private static async Task AssertBackoffAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IScheduledTaskStore>();
        var now = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var plan = ScheduledTask.Create(new ScheduledTaskId(77001), TaskCode.Create("private-failure-code").Value,
            TimeSpan.FromHours(1), now, ScheduleTarget.Create("costing.recalculate", Guid.NewGuid()).Value, "42").Value;
        Assert.True((await store.AddAsync(plan)).IsSuccess);
        Assert.True(plan.Defer(now, "scheduling.commit.failed").IsSuccess);
        Assert.True((await store.SaveAsync(plan, 1)).IsSuccess);
        Assert.True((await store.SaveAsync(plan, 2)).IsSuccess);
        Assert.True(plan.Defer(now.AddMinutes(1), "scheduling.commit.failed").IsSuccess);
        Assert.True((await store.SaveAsync(plan, 2)).IsSuccess);
        Assert.True(plan.ChangeInterval(TimeSpan.FromHours(2)).IsSuccess);
        Assert.True((await store.SaveAsync(plan, 3)).IsSuccess);
        plan.EnableAt(now.AddMinutes(10));
        Assert.True((await store.SaveAsync(plan, 4)).IsSuccess);
        plan.Disable();
        Assert.True((await store.SaveAsync(plan, 5)).IsSuccess);
        plan.Disable();
        Assert.True((await store.SaveAsync(plan, 6)).IsSuccess);
        plan.EnableAt(now.AddMinutes(20));
        Assert.True((await store.SaveAsync(plan, 6)).IsSuccess);
        Assert.True(plan.MarkTriggered(now.AddMinutes(20)).IsSuccess);
        Assert.True((await store.SaveAsync(plan, 7)).IsSuccess);
        var pending = await scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(SchedulingInfrastructureServiceCollectionExtensions.OutboxKey)
            .ReadPendingAsync(100, DateTimeOffset.UtcNow);
        var serializer = scope.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>();
        var facts = pending.Where(entry => entry.EventName == PlanCommittedV1.Name).Select(entry => serializer.Deserialize<PlanCommittedV1>(entry.Payload))
            .OrderBy(fact => fact.Version).ThenBy(fact => fact.Operation, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "created", "deferred", "deferred", "failure-cleared", "rule-changed", "rescheduled", "disabled", "enabled", "advanced" }, facts.Select(fact => fact.Operation));
        Assert.Equal(new long[] { 1, 2, 3, 4, 4, 5, 6, 7, 8 }, facts.Select(fact => fact.Version));
        Assert.All(facts, fact => { Assert.Null(fact.ActorId); Assert.Null(fact.DecisionId); });
    }

    [Theory]
    [InlineData("FireOnce", 1, "triggered", 1)]
    [InlineData("FireOnce", 3, "coalesced", 1)]
    [InlineData("Skip", 3, "skipped", 0)]
    public async Task CalendarDecision_CommitsItsOwnFact_WithoutConfusingItWithBusinessExecution(string policy, int minutes, string operation, int occurrences)
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero));
        await using var rootApp = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        await using var app = rootApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TaskRegistry>();
            services.RemoveAll<ScheduleRunner>();
            services.AddScoped(provider => new TaskRegistry(provider.GetRequiredService<IScheduledTaskStore>(), provider.GetRequiredService<IIdGenerator>(), clock,
                provider.GetRequiredService<IScheduleCalendar>(), provider.GetRequiredService<IExecutionContext>()));
            services.AddScoped(provider => new ScheduleRunner(provider.GetRequiredService<IScheduledTaskStore>(), clock, provider.GetRequiredService<IScheduleCalendar>(),
                provider.GetRequiredService<IBackgroundExecutionObservation>(), provider.GetRequiredService<IExecutionContext>()));
        }));
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "calendar-fact-origin");
        using var created = await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative), new
        {
            code = "private-decision-code",
            rule = new { kind = "Cron", expression = "* * * * *", timeZoneId = "UTC", misfirePolicy = policy },
            targetKind = "costing.recalculate",
            targetId = Guid.NewGuid(),
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadApiDataAsync()).GetProperty("taskId").ReadHttpInt64();
        clock.Advance(TimeSpan.FromMinutes(minutes));
        await using var scope = app.Services.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<ScheduleRunner>();
        Assert.Equal(occurrences, (await runner.RunOnceAsync()).Triggered);
        Assert.Equal(0, (await runner.RunOnceAsync()).Examined);
        var store = scope.ServiceProvider.GetRequiredService<IScheduledTaskStore>();
        var decision = Assert.Single((await store.ReadDecisionsAsync(id, 0, 100)).Items);
        var pending = await scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(SchedulingInfrastructureServiceCollectionExtensions.OutboxKey)
            .ReadPendingAsync(100, DateTimeOffset.UtcNow);
        Assert.Equal(occurrences, pending.Count(entry => entry.EventName == ScheduleTriggeredV1.Name));
        var facts = pending.Where(entry => entry.EventName == PlanCommittedV1.Name).Select(entry =>
        {
            using var document = JsonDocument.Parse(entry.Payload);
            return document.RootElement.Clone();
        }).OrderBy(fact => fact.GetProperty("version").GetInt64()).ToArray();
        Assert.Equal(new[] { "created", operation }, facts.Select(fact => fact.GetProperty("operation").GetString()));
        var triggered = facts[1];
        Assert.Equal(2, triggered.GetProperty("version").GetInt64());
        Assert.Equal(decision.DecisionId, triggered.GetProperty("decisionId").GetGuid());
        Assert.Equal(JsonValueKind.Null, triggered.GetProperty("actorId").ValueKind);
        Assert.Equal(facts[0].GetProperty("actorId").GetString(), triggered.GetProperty("execution").GetProperty("initiatorId").GetString());
        Assert.Equal(facts[0].GetProperty("execution").GetProperty("rootOperationId").GetGuid(), triggered.GetProperty("execution").GetProperty("rootOperationId").GetGuid());
        Assert.NotEqual(facts[0].GetProperty("execution").GetProperty("operationId").GetGuid(), triggered.GetProperty("execution").GetProperty("operationId").GetGuid());
    }

    [Fact]
    public async Task PlanMaintenance_RecordsOnlyCommittedChanges_WithTheCurrentActorAndOperation()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        await AssertMaintenanceAsync(client, app.Services);
    }

    [PostgresFact]
    public async Task PostgresPlanMaintenance_RecordsOnlyCommittedChanges_WithTheCurrentActorAndOperation()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "scheduling-root-password", schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "scheduling-root-password");
        await AssertMaintenanceAsync(client, app.Services);
    }

    private static async Task AssertMaintenanceAsync(HttpClient client, IServiceProvider services)
    {
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "schedule-maintenance-facts");
        using var created = await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative), new
        {
            code = "private-plan-code",
            intervalSeconds = 3600,
            targetKind = "costing.recalculate",
            targetId = Guid.NewGuid(),
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadApiDataAsync()).GetProperty("taskId").ReadHttpInt64();
        using var paused = await client.PostAsJsonAsync(new Uri($"/api/scheduling/tasks/{id}/pause", UriKind.Relative), new { expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.NoContent, paused.StatusCode);
        using var repeated = await client.PostAsJsonAsync(new Uri($"/api/scheduling/tasks/{id}/pause", UriKind.Relative), new { expectedVersion = 2 });
        Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);
        using var stale = await client.PostAsJsonAsync(new Uri($"/api/scheduling/tasks/{id}/resume", UriKind.Relative), new { expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        foreach (var version in new[] { 2, 3 })
        {
            using var updated = await client.PutAsJsonAsync(new Uri($"/api/scheduling/tasks/{id}/rule", UriKind.Relative),
                new { expectedVersion = version, rule = new { kind = "Interval", intervalSeconds = 7200 } });
            Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        }
        using var resumed = await client.PostAsJsonAsync(new Uri($"/api/scheduling/tasks/{id}/resume", UriKind.Relative), new { expectedVersion = 3 });
        Assert.Equal(HttpStatusCode.NoContent, resumed.StatusCode);
        await using var scope = services.CreateAsyncScope();
        var pending = await scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(SchedulingInfrastructureServiceCollectionExtensions.OutboxKey)
            .ReadPendingAsync(100, DateTimeOffset.UtcNow);
        var facts = pending.Where(entry => entry.EventName == "scheduling.plan-committed.v1").Select(entry =>
        {
            using var document = JsonDocument.Parse(entry.Payload);
            return document.RootElement.Clone();
        }).OrderBy(fact => fact.GetProperty("version").GetInt64()).ToArray();
        Assert.Equal(new[] { "created", "disabled", "rule-changed", "enabled" }, facts.Select(fact => fact.GetProperty("operation").GetString()));
        Assert.Equal(new long[] { 1, 2, 3, 4 }, facts.Select(fact => fact.GetProperty("version").GetInt64()));
        Assert.All(facts, fact =>
        {
            Assert.Equal(id, fact.GetProperty("planId").GetInt64());
            Assert.False(string.IsNullOrWhiteSpace(fact.GetProperty("actorId").GetString()));
            Assert.Equal("schedule-maintenance-facts", fact.GetProperty("correlationId").GetString());
            Assert.Equal("platform", fact.GetProperty("execution").GetProperty("source").GetString());
            Assert.DoesNotContain("private-plan-code", fact.GetRawText(), StringComparison.Ordinal);
            Assert.False(fact.TryGetProperty("rule", out _));
            Assert.False(fact.TryGetProperty("targetId", out _));
        });
    }
}
