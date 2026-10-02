using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.Auditing.Endpoints;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Web;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class OperationLoggingPipelineTests
{
    [Fact]
    public async Task InvalidOptionalRouteMetadata_IsOmitted_WithoutLosingTheRequest()
    {
        var journal = new ProbeJournal(fail: false);
        await using var app = CreateApp(journal);
        var prefix = "/" + new string('r', 501);
        app.MapGet(prefix + "/{id}", () => Results.NoContent()).WithMetadata(
            new OperationDescription("orders.read", subject: new OperationSubjectRoute("Order", "id", OperationSubjectIdKind.Uuid)));
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var response = await client.GetAsync(new Uri(prefix + "/private-invalid-id", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var finished = await journal.Finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null(finished.RouteTemplate);
        Assert.Null(finished.Metadata?.SubjectType);
        Assert.Null(finished.Metadata?.SubjectIdKind);
        Assert.Null(finished.Metadata?.SubjectId);
        Assert.DoesNotContain("private-", System.Text.Json.JsonSerializer.Serialize(finished), StringComparison.Ordinal);
        await using var storage = new ServiceCollection().AddOperationJournalMemoryStorage().BuildServiceProvider();
        Assert.True((await storage.GetRequiredService<IOperationJournal>().AppendAsync(finished)).IsSuccess);
    }

    [Theory]
    [InlineData("M-SEARCH", "M-SEARCH")]
    [InlineData("PROPFIND", "PROPFIND")]
    [InlineData("PRIVATE-TOKEN-UNKNOWN-METHOD", "OTHER")]
    public async Task UnusualHttpMethod_UsesBoundedClassificationWithoutLosingObservation(string method, string expected)
    {
        var journal = new ProbeJournal(fail: false);
        await using var app = CreateApp(journal);
        app.MapMethods("/orders", [method], () => Results.NoContent());
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var request = new HttpRequestMessage(new HttpMethod(method), "/orders");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var finished = await journal.Finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(expected, finished.HttpMethod);
        Assert.Equal("http." + expected.ToLowerInvariant(), finished.Metadata?.Action);
        Assert.DoesNotContain("PRIVATE", System.Text.Json.JsonSerializer.Serialize(finished), StringComparison.Ordinal);
        await using var storage = new ServiceCollection().AddOperationJournalMemoryStorage().BuildServiceProvider();
        Assert.True((await storage.GetRequiredService<IOperationJournal>().AppendAsync(finished)).IsSuccess);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("order-62.safe_ID")]
    [InlineData("private=secret-token")]
    [InlineData("private\tvalue")]
    [InlineData("first,second")]
    [InlineData("01234567890123456789012345678901234567890123456789012345678901234567890")]
    public async Task Correlation_IsBoundedAndConsistent_WithoutBecomingAnActor(string? incoming)
    {
        var journal = new ProbeJournal(fail: false);
        await using var app = CreateApp(journal);
        app.MapGet("/orders/{id}", () => Results.NoContent());
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/orders/private-path?token=private-query");
        if (incoming is not null) { Assert.True(request.Headers.TryAddWithoutValidation("X-Correlation-Id", incoming)); }
        Assert.True(request.Headers.TryAddWithoutValidation("X-User-Id", "forged-root"));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var finished = await journal.Finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var correlation = Assert.Single(response.Headers.GetValues("X-Correlation-Id"));
        Assert.Equal(correlation, finished.Metadata?.CorrelationId);
        if (incoming == "order-62.safe_ID") { Assert.Equal(incoming, correlation); }
        else { Assert.True(Guid.TryParseExact(correlation, "N", out _)); }
        Assert.Null(finished.ActorId);
        Assert.DoesNotContain("private-", System.Text.Json.JsonSerializer.Serialize(finished), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitSuppression_TakesPrecedenceOverDescription_WithoutDisablingOtherEndpoints()
    {
        var journal = new ProbeJournal(fail: false);
        await using var app = CreateApp(journal);
        app.MapGet("/api/queue/poll", () => Results.NoContent())
            .WithMetadata(new OperationDescription("queue.poll", "轮询任务状态"), new OperationLogSuppression("高频只读轮询"));
        app.MapGet("/api/queue/tasks", () => Results.NoContent());
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var excluded = await client.GetAsync(new Uri("/api/queue/poll", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NoContent, excluded.StatusCode);
        Assert.Equal(0, journal.AppendCount);
        using var included = await client.GetAsync(new Uri("/api/queue/tasks", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NoContent, included.StatusCode);
        var finished = await journal.Finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("/api/queue/tasks", finished.RouteTemplate);
        Assert.Equal(2, journal.AppendCount);
    }

    [Theory]
    [InlineData(OperationSubjectIdKind.Uuid, "98E26A25-036D-49CB-AAEF-2E6197A33CE0", "guid", "98e26a25-036d-49cb-aaef-2e6197a33ce0")]
    [InlineData(OperationSubjectIdKind.Numeric, "0009007199254740993", "int64", "9007199254740993")]
    public async Task DeclaredMetadata_RecordsOnlyTheNormalizedRouteSubject(OperationSubjectIdKind kind, string rawId, string expectedKind, string expectedId)
    {
        var journal = new ProbeJournal(fail: false);
        await using var app = CreateApp(journal);
        app.MapPost("/orders/{id}", () => Results.Accepted())
            .WithMetadata(new OperationDescription("orders.recalculate", "重新核算订单",
                new OperationSubjectRoute("Order", "id", kind)));
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var body = new StringContent("private-body-password");
        using var response = await client.PostAsync(new Uri($"/orders/{rawId}?token=private-query-token", UriKind.Relative), body);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var finished = await journal.Finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotNull(finished.Metadata);
        Assert.Equal("orders.recalculate", finished.Metadata.Action);
        Assert.Equal("重新核算订单", finished.Metadata.Description);
        Assert.Equal("endpoint", finished.Metadata.ExecutionRole);
        Assert.Equal("Order", finished.Metadata.SubjectType);
        Assert.Equal(expectedKind, finished.Metadata.SubjectIdKind);
        Assert.Equal(expectedId, finished.Metadata.SubjectId);
        Assert.Equal("/orders/{id}", finished.RouteTemplate);
        Assert.DoesNotContain("private-", System.Text.Json.JsonSerializer.Serialize(finished), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/orders/{id:guid}", "/orders/")]
    [InlineData("/api/auditing/admin/{id:guid}", "/api/auditing/admin/")]
    public async Task BusinessEndpoint_IsObservedWithoutIndividualRegistration(string route, string path)
    {
        var journal = new ProbeJournal(fail: false);
        await using var app = CreateApp(journal);
        app.MapGet(route, () => Results.NoContent());
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var response = await client.GetAsync(new Uri($"{path}{Guid.NewGuid()}?private=not-for-logs", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var finished = await journal.Finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(route, finished.RouteTemplate);
        Assert.Equal("completed", finished.Outcome);
        Assert.Equal(204, finished.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JournalAndDiagnosticsFailure_PreserveBusinessResponseAndFailureCount(bool failMetric)
    {
        using var listener = new MeterListener();
        if (failMetric)
        {
            listener.InstrumentPublished = (instrument, owner) =>
            {
                if (instrument.Meter.Name == "NexusStackNext.OperationJournal") { owner.EnableMeasurementEvents(instrument); }
            };
            listener.SetMeasurementEventCallback<long>((_, _, _, _) => throw new InvalidOperationException("injected meter failure"));
            listener.Start();
        }
        var journal = new ProbeJournal(fail: true);
        await using var app = CreateApp(journal, failLogger: !failMetric);
        app.MapPost("/api/probe", () => Results.NoContent());
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var response = await client.PostAsync(new Uri("/api/probe", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(2, app.Services.GetRequiredService<OperationJournalStatus>().FailureCount);
    }

    [Fact]
    public async Task ClientDisconnect_RecordsCanceledAfterExceptionHandlerConvertsTheException()
    {
        var journal = new ProbeJournal(fail: false);
        await using var app = CreateApp(journal);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.MapGet("/api/probe", async (HttpContext context) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
        });
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var cancel = new CancellationTokenSource();
        var request = client.GetAsync(new Uri("/api/probe", UriKind.Relative), cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        var finished = await journal.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("canceled", finished.Outcome);
        Assert.Equal(499, finished.StatusCode);
        Assert.Null(finished.ActorId);
    }

    private static WebApplication CreateApp(ProbeJournal journal, bool failLogger = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        if (failLogger) { builder.Logging.AddProvider(new ThrowingProvider()); }
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["OperationJournal:Storage:Provider"] = "Memory" });
        builder.Services.AddNexusStackApplication();
        builder.Services.AddApiResponseContract();
        builder.Services.AddOperationJournalModule(builder.Configuration, builder.Environment, "probe");
        builder.Services.AddSingleton<IOperationJournal>(journal);
        var app = builder.Build();
        app.UseRouting();
        app.UseCorrelationId();
        app.UseOperationJournal();
        app.UseExceptionHandler();
        app.UseApiResponseContract();
        return app;
    }

    private sealed class ProbeJournal(bool fail) : IOperationJournal
    {
        private int _appendCount;
        public int AppendCount => Volatile.Read(ref _appendCount);
        public TaskCompletionSource<OperationObservedV1> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Result> AppendAsync(OperationObservedV1 observation, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _appendCount);
            if (observation.Phase == "finished") { Finished.TrySetResult(observation); }
            return Task.FromResult(fail ? Result.Failure(new Error("probe.journal_failure", "injected")) : Result.Success());
        }
    }

    private sealed class ThrowingProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ThrowingLogger();
        public void Dispose() { }
    }

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 10) { throw new InvalidOperationException("injected diagnostic failure"); }
        }
    }
}
