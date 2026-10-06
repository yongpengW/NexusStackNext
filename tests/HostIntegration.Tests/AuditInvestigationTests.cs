using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.Auditing.Domain.Entries;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.IntegrationSupport;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class AuditInvestigationTests(JourneyDatabaseTemplates databases)
{
    [Theory]
    [InlineData("entries")]
    [InlineData("operations")]
    public async Task Investigation_RejectsUnboundedOrMalformedFilters(string endpoint)
    {
        await using var app = new PlatformAppWithRootAccount();
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        var invalid = new List<string>
        {
            "source=%20", "action=bad%0Avalue", "source=" + new string('s', 65), "action=" + new string('a', 201),
            "operationId=00000000-0000-0000-0000-000000000000", "rootOperationId=00000000-0000-0000-0000-000000000000",
            "from=2026-01-01T00:00:00Z&to=2026-02-02T00:00:00Z", "from=2026-02-02T00:00:00Z&to=2026-01-01T00:00:00Z",
        };
        if (endpoint == "operations")
        {
            invalid.Add("taskEpoch=1");
            invalid.Add("taskId=00000000-0000-0000-0000-000000000000");
            invalid.Add($"taskId={Guid.NewGuid()}&taskEpoch=0");
        }
        else
        {
            invalid.Add("relatedContext=identity");
            invalid.Add("relatedSubjectType=user&relatedSubjectId=42");
            invalid.Add("relatedContext=identity&relatedSubjectId=42");
            invalid.Add("relatedContext=identity&relatedSubjectType=user&relatedSubjectId=%20");
            invalid.Add("relatedContext=identity&relatedSubjectType=user&relatedSubjectId=" + new string('1', 201));
        }
        foreach (var query in invalid)
        {
            using var response = await client.GetAsync(new Uri($"/api/auditing/{endpoint}?{query}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        foreach (var query in new[]
        {
            "from=2026-01-01T00:00:00Z&to=2026-02-01T00:00:00Z",
            "from=2026-01-01T08:00:00%2B08:00&to=2026-01-01T00:00:00Z",
            "from=9999-12-31T00:00:00Z", "to=0001-01-01T00:00:00Z",
        })
        {
            using var response = await client.GetAsync(new Uri($"/api/auditing/{endpoint}?{query}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task MemoryOperations_FilterFinalEvidenceAndTaskRelationships()
    {
        await using var app = new PlatformAppWithRootAccount();
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        await VerifyOperationFiltersAsync(app.Services, client);
    }

    [PostgresFact]
    public async Task PersistedOperations_FilterFinalEvidenceAndTaskRelationships()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "audit-root-password");
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "audit-root-password");
        await VerifyOperationFiltersAsync(app.Services, client);
    }

    private static async Task VerifyOperationFiltersAsync(IServiceProvider services, HttpClient client)
    {
        var now = DateTimeOffset.UtcNow;
        var metadata = new OperationDetails
        {
            Action = "pricing.calculate",
            ExecutionRole = "task",
            SubjectType = "PriceQuote",
            SubjectIdKind = "guid",
            SubjectId = Guid.NewGuid().ToString(),
            CorrelationId = "selected-correlation",
            InitiatorId = "original-user",
            RootOperationId = Guid.NewGuid(),
            RootSource = "costing",
            ParentOperationId = Guid.NewGuid(),
            ParentSource = "costing",
            TaskId = Guid.NewGuid(),
            TaskEpoch = 2,
        };
        var selected = new OperationObservedV1
        {
            EventId = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            Source = "pricing",
            Kind = "task",
            Phase = "finished",
            Outcome = "completed",
            OccurredAt = now.AddMinutes(-1),
            TraceId = "selected-trace",
            DurationMs = 10,
            Metadata = metadata,
        };
        var excluded = new (string Query, OperationDetails Metadata)[]
        {
            ("action=pricing.calculate", metadata with { Action = "pricing.recalculate" }),
            ("subjectType=PriceQuote", metadata with { SubjectType = "OtherSubject" }),
            ($"subjectId={metadata.SubjectId}", metadata with { SubjectId = Guid.NewGuid().ToString() }),
            ("correlationId=selected-correlation", metadata with { CorrelationId = "other-correlation" }),
            ("initiatorId=original-user", metadata with { InitiatorId = "other-user" }),
            ($"rootOperationId={metadata.RootOperationId}", metadata with { RootOperationId = Guid.NewGuid() }),
            ("rootSource=costing", metadata with { RootSource = "other-root" }),
            ($"parentOperationId={metadata.ParentOperationId}", metadata with { ParentOperationId = Guid.NewGuid() }),
            ("parentSource=costing", metadata with { ParentSource = "other-parent" }),
            ($"taskId={metadata.TaskId}", metadata with { TaskId = Guid.NewGuid() }),
        };
        await using var scope = services.CreateAsyncScope();
        async Task DeliverAsync(OperationObservedV1 message)
        {
            var serializer = scope.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>();
            Assert.True(await scope.ServiceProvider.GetRequiredKeyedService<IIntegrationEventProcessor>(OperationObservedV1.Name)
                .HandleAsync(new EventEnvelope
                {
                    MessageId = message.EventId,
                    EventName = message.EventName,
                    OccurredAt = message.OccurredAt,
                    Payload = serializer.Serialize(message)
                }));
        }
        await DeliverAsync(selected);
        foreach (var (_, details) in excluded)
        {
            await DeliverAsync(selected with { EventId = Guid.NewGuid(), OperationId = Guid.NewGuid(), Metadata = details });
        }
        var previousAttempt = selected with
        {
            EventId = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            Outcome = "lease_lost",
            Metadata = metadata with { TaskEpoch = 1 }
        };
        await DeliverAsync(previousAttempt);
        var outsideWindow = selected with { EventId = Guid.NewGuid(), OperationId = Guid.NewGuid(), OccurredAt = now.AddDays(-8) };
        await DeliverAsync(outsideWindow);
        // 完成证据在窗外，开始证据在窗内（例如来源时钟回拨）；不能凭过滤把已结束操作变成 unconfirmed。
        await DeliverAsync(outsideWindow with { EventId = Guid.NewGuid(), Phase = "started", Outcome = null, DurationMs = null, OccurredAt = now.AddMinutes(-2) });
        var page = await ReadPageAsync(client, string.Empty, "operations");
        Assert.Equal(12, page.GetProperty("total").ReadHttpInt64());
        foreach (var (filter, _) in excluded)
        {
            var filtered = await ReadPageAsync(client, filter, "operations");
            Assert.Equal(11, filtered.GetProperty("total").ReadHttpInt64());
        }
        var combined = await ReadPageAsync(client, string.Join('&', excluded.Select(item => item.Query)) + "&taskEpoch=2", "operations");
        Assert.Equal(selected.OperationId, Assert.Single(combined.GetProperty("data").EnumerateArray()).GetProperty("operationId").GetGuid());
        var firstAttempt = await ReadPageAsync(client, $"taskId={metadata.TaskId}&taskEpoch=1", "operations");
        Assert.Equal("lease_lost", Assert.Single(firstAttempt.GetProperty("data").EnumerateArray()).GetProperty("outcome").GetString());
        var unconfirmed = await ReadPageAsync(client, "outcome=unconfirmed", "operations");
        Assert.Empty(unconfirmed.GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task MemoryFacts_CanBeInvestigatedByExactSafeFields()
    {
        await using var app = new PlatformAppWithRootAccount();
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        await VerifyFactFiltersAsync(app.Services, client);
    }

    [PostgresFact]
    public async Task PersistedFacts_CanBeInvestigatedByExactSafeFields()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "audit-root-password");
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "audit-root-password");
        await VerifyFactFiltersAsync(app.Services, client);
    }

    private static async Task VerifyFactFiltersAsync(IServiceProvider services, HttpClient client)
    {
        var now = DateTimeOffset.UtcNow;
        var execution = new AuditExecution(Guid.NewGuid(), "platform-host", Guid.NewGuid(), "gateway", "original-user");
        var selected = new AuditFact(Guid.NewGuid(), "platform.setting-committed.v1", "platform", "platform.setting.changed",
            "global-setting", "audit.selected", 2, "current-actor", now.AddMinutes(-1), "selected-trace", "selected-correlation")
        { Execution = execution };
        // 每次只改变一个条件；漏掉任何一个过滤都必须被真实查询结果发现。
        var excluded = new (string Query, AuditFact Fact)[]
        {
            ("source=platform", selected with { Source = "other-source" }),
            ("action=platform.setting.changed", selected with { Action = "platform.setting.created" }),
            ("subjectType=global-setting", selected with { SubjectType = "other-type" }),
            ("subjectId=audit.selected", selected with { SubjectId = "audit.other" }),
            ("actorId=current-actor", selected with { ActorId = "other-actor" }),
            ("traceId=selected-trace", selected with { TraceId = "other-trace" }),
            ("correlationId=selected-correlation", selected with { CorrelationId = "other-correlation" }),
            ($"operationId={execution.OperationId}", selected with { Execution = execution with { OperationId = Guid.NewGuid() } }),
            ("operationSource=platform-host", selected with { Execution = execution with { Source = "other-host" } }),
            ($"rootOperationId={execution.RootOperationId}", selected with { Execution = execution with { RootOperationId = Guid.NewGuid() } }),
            ("rootSource=gateway", selected with { Execution = execution with { RootSource = "other-root" } }),
            ("initiatorId=original-user", selected with { Execution = execution with { InitiatorId = "other-user" } }),
        };
        var expectedIds = new List<Guid> { selected.MessageId };
        await using (var scope = services.CreateAsyncScope())
        {
            var ingestion = scope.ServiceProvider.GetRequiredService<AuditIngestion>();
            Assert.True((await ingestion.IngestAsync(selected)).IsSuccess);
            foreach (var (_, fact) in excluded)
            {
                var decoy = fact with { MessageId = Guid.NewGuid() };
                Assert.True((await ingestion.IngestAsync(decoy)).IsSuccess);
                expectedIds.Add(decoy.MessageId);
            }
            Assert.True((await ingestion.IngestAsync(selected with { MessageId = Guid.NewGuid(), OccurredAt = now.AddDays(-8) })).IsSuccess);
        }
        var defaultPage = await ReadPageAsync(client, string.Empty);
        Assert.Equal(expectedIds.Count, defaultPage.GetProperty("total").ReadHttpInt64());
        foreach (var (filter, _) in excluded)
        {
            var page = await ReadPageAsync(client, filter);
            Assert.Equal(expectedIds.Count - 1, page.GetProperty("total").ReadHttpInt64());
            Assert.Contains(page.GetProperty("data").EnumerateArray(), entry => entry.GetProperty("fact").GetProperty("messageId").GetGuid() == selected.MessageId);
        }
        var combined = await ReadPageAsync(client, string.Join('&', excluded.Select(item => item.Query)));
        Assert.Equal(selected.MessageId, Assert.Single(combined.GetProperty("data").EnumerateArray()).GetProperty("fact").GetProperty("messageId").GetGuid());
        Assert.Equal(1, combined.GetProperty("total").ReadHttpInt64());
        var historical = await ReadPageAsync(client, "from=" + Uri.EscapeDataString(now.AddDays(-9).ToString("O"))
            + "&to=" + Uri.EscapeDataString(now.AddDays(-7).ToString("O")));
        Assert.Equal(1, historical.GetProperty("total").ReadHttpInt64());
    }

    private static async Task<JsonElement> ReadPageAsync(HttpClient client, string query, string endpoint = "entries")
    {
        using var response = await client.GetAsync(new Uri("/api/auditing/" + endpoint + "?" + query, UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
