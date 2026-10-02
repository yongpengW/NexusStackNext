using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Ids;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.TestSupport;

namespace NexusStackNext.Auditing.Application.Tests;

public sealed class OperationObservationTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task StartedWithoutCompletion_RemainsUnconfirmed()
    {
        await using var application = CreateApplication();
        await using var scope = application.CreateAsyncScope();
        var started = Started();
        Assert.True(await DeliverAsync(scope.ServiceProvider, started));

        var operations = scope.ServiceProvider.GetRequiredService<IOperationObservationStore>();
        var page = await operations.QueryAsync(new OperationQuery(1, 100, Outcome: "unconfirmed"));
        var operation = Assert.Single(page.Operations);
        Assert.Equal(1, page.Total);
        Assert.Equal(started.OperationId, operation.OperationId);
        Assert.Equal(StartedAt, operation.StartedAt);
        Assert.Equal("unconfirmed", operation.Outcome);
        Assert.Null(operation.FinishedAt);
        Assert.Null(operation.StatusCode);
        Assert.Null(operation.DurationMs);
        Assert.Empty((await operations.QueryAsync(new OperationQuery(1, 100, Outcome: "completed"))).Operations);
    }

    [Fact]
    public async Task CompletionArrivingFirst_MergesLaterStartWithoutLosingItsResult()
    {
        await using var application = CreateApplication();
        await using var scope = application.CreateAsyncScope();
        var started = Started();
        var finished = Finished(started);
        var operations = scope.ServiceProvider.GetRequiredService<IOperationObservationStore>();
        Assert.True(await DeliverAsync(scope.ServiceProvider, finished));
        var beforeStart = Assert.Single((await operations.QueryAsync(new OperationQuery(1, 100))).Operations);
        Assert.Null(beforeStart.StartedAt);
        Assert.Equal("completed", beforeStart.Outcome);
        Assert.Equal(204, beforeStart.StatusCode);

        Assert.True(await DeliverAsync(scope.ServiceProvider, started));
        var merged = Assert.Single((await operations.QueryAsync(new OperationQuery(1, 100))).Operations);
        Assert.Equal(StartedAt, merged.StartedAt);
        Assert.Equal(StartedAt.AddSeconds(1), merged.FinishedAt);
        Assert.Equal("authenticated-actor", merged.ActorId);
        Assert.Equal("completed", merged.Outcome);
        Assert.Equal(204, merged.StatusCode);
        Assert.Equal(1000, merged.DurationMs);
        Assert.Empty((await operations.QueryAsync(new OperationQuery(1, 100, Outcome: "unconfirmed"))).Operations);
    }

    [Fact]
    public async Task Redelivery_IsIdempotentAndCannotReplaceOriginalMessageContent()
    {
        await using var application = CreateApplication();
        await using var scope = application.CreateAsyncScope();
        var finished = Finished(Started());
        Assert.True(await DeliverAsync(scope.ServiceProvider, finished));
        Assert.True(await DeliverAsync(scope.ServiceProvider, finished));
        Assert.False(await DeliverAsync(scope.ServiceProvider, finished with { StatusCode = 202, Outcome = "accepted" }));
        Assert.True(await DeliverAsync(scope.ServiceProvider, finished));

        var page = await scope.ServiceProvider.GetRequiredService<IOperationObservationStore>().QueryAsync(new OperationQuery(1, 100));
        Assert.Equal(1, page.Total);
        var original = Assert.Single(page.Operations);
        Assert.Equal("completed", original.Outcome);
        Assert.Equal(204, original.StatusCode);
    }

    [Fact]
    public async Task AnotherMessageForTheSamePhase_IsRejectedWithoutConsumingItsIdentity()
    {
        await using var application = CreateApplication();
        await using var scope = application.CreateAsyncScope();
        var started = Started();
        var conflicting = started with { EventId = Guid.NewGuid(), ActorId = "cannot-overwrite" };
        Assert.True(await DeliverAsync(scope.ServiceProvider, started));
        Assert.False(await DeliverAsync(scope.ServiceProvider, conflicting));

        var corrected = conflicting with { OperationId = Guid.NewGuid(), ActorId = null };
        Assert.True(await DeliverAsync(scope.ServiceProvider, corrected));
        var page = await scope.ServiceProvider.GetRequiredService<IOperationObservationStore>().QueryAsync(new OperationQuery(1, 100));
        Assert.Equal(2, page.Total);
        Assert.Null(Assert.Single(page.Operations, item => item.OperationId == started.OperationId).ActorId);
        Assert.Single(page.Operations, item => item.OperationId == corrected.OperationId);
    }

    [Theory]
    [InlineData("completed", 202)]
    [InlineData("accepted", 204)]
    [InlineData("rejected", 500)]
    [InlineData("failed", 403)]
    [InlineData("unconfirmed", null)]
    [InlineData("completed", null)]
    public async Task InconsistentOutcomeAndStatus_IsRejectedWithoutConsumingTheMessage(string outcome, int? statusCode)
    {
        await using var application = CreateApplication();
        await using var scope = application.CreateAsyncScope();
        var valid = Finished(Started());
        Assert.False(await DeliverAsync(scope.ServiceProvider, valid with { Outcome = outcome, StatusCode = statusCode }));
        var operations = scope.ServiceProvider.GetRequiredService<IOperationObservationStore>();
        Assert.Empty((await operations.QueryAsync(new OperationQuery(1, 100))).Operations);

        Assert.True(await DeliverAsync(scope.ServiceProvider, valid));
        Assert.Equal("completed", Assert.Single((await operations.QueryAsync(new OperationQuery(1, 100))).Operations).Outcome);
    }

    [Fact]
    public async Task EqualOperationIdsFromDifferentSources_AreInvestigatedSeparately()
    {
        await using var application = CreateApplication();
        await using var scope = application.CreateAsyncScope();
        var platform = Started();
        var pricing = Finished(platform) with { Source = "pricing-host", StatusCode = 202, Outcome = "accepted" };
        Assert.True(await DeliverAsync(scope.ServiceProvider, platform));
        Assert.True(await DeliverAsync(scope.ServiceProvider, pricing));

        var operations = scope.ServiceProvider.GetRequiredService<IOperationObservationStore>();
        var combined = await operations.QueryAsync(new OperationQuery(1, 100, OperationId: platform.OperationId));
        Assert.Equal(2, combined.Total);
        Assert.Equal("unconfirmed", Assert.Single(combined.Operations, item => item.Source == "platform-host").Outcome);
        var pricingOnly = Assert.Single((await operations.QueryAsync(new OperationQuery(1, 100, Source: "pricing-host"))).Operations);
        Assert.Equal("accepted", pricingOnly.Outcome);
        Assert.Null(pricingOnly.StartedAt);
        Assert.Equal(202, pricingOnly.StatusCode);
    }

    [Fact]
    public async Task TimeRangeWithAnOffset_SelectsTheSameInstantWithInclusiveBounds()
    {
        await using var application = CreateApplication();
        await using var scope = application.CreateAsyncScope();
        var withinRange = Finished(Started());
        var later = Finished(Started()) with { OccurredAt = StartedAt.AddSeconds(2) };
        Assert.True(await DeliverAsync(scope.ServiceProvider, withinRange));
        Assert.True(await DeliverAsync(scope.ServiceProvider, later));

        var operations = scope.ServiceProvider.GetRequiredService<IOperationObservationStore>();
        var localInstant = new DateTimeOffset(2026, 10, 3, 8, 0, 1, TimeSpan.FromHours(8));
        var localPage = await operations.QueryAsync(new OperationQuery(1, 100, From: localInstant, To: localInstant));
        var utcInstant = new DateTimeOffset(2026, 10, 3, 0, 0, 1, TimeSpan.Zero);
        var utcPage = await operations.QueryAsync(new OperationQuery(1, 100, From: utcInstant, To: utcInstant));
        Assert.Equal(1, localPage.Total);
        Assert.Equal(withinRange.OperationId, Assert.Single(localPage.Operations).OperationId);
        Assert.Equal(Assert.Single(utcPage.Operations), Assert.Single(localPage.Operations));
    }

    private static OperationObservedV1 Started() => new()
    {
        EventId = Guid.NewGuid(),
        OperationId = Guid.NewGuid(),
        Source = "platform-host",
        Kind = "http",
        Phase = "started",
        OccurredAt = StartedAt,
        TraceId = "trusted-trace",
        HttpMethod = "PUT",
        RouteTemplate = "/api/platform/settings/{key}",
    };

    private static OperationObservedV1 Finished(OperationObservedV1 started) => started with
    {
        EventId = Guid.NewGuid(),
        Phase = "finished",
        OccurredAt = StartedAt.AddSeconds(1),
        ActorId = "authenticated-actor",
        Outcome = "completed",
        StatusCode = 204,
        DurationMs = 1000,
    };

    private static ServiceProvider CreateApplication()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(new FixedClock(StartedAt.AddDays(1)));
        services.AddSingleton<IIdGenerator>(new SequentialIdGenerator(1000));
        services.AddSingleton<IIntegrationEventSerializer>(new SystemTextJsonIntegrationEventSerializer());
        services.AddAuditingInMemoryStorage();
        services.AddScoped<IIntegrationEventProcessor, OperationObservationIngestion>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static Task<bool> DeliverAsync(IServiceProvider services, OperationObservedV1 observation) =>
        services.GetRequiredService<IIntegrationEventProcessor>().HandleAsync(new EventEnvelope
        {
            MessageId = observation.EventId,
            EventName = observation.EventName,
            OccurredAt = observation.OccurredAt,
            Payload = services.GetRequiredService<IIntegrationEventSerializer>().Serialize(observation),
        });
}
