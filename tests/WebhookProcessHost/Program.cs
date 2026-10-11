using System.Text.Json;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Contracts;
using NexusStackNext.Pricing.Infrastructure;

var builder = WebApplication.CreateSlimBuilder(args);
if (Environment.GetEnvironmentVariable("NSN_WEBHOOK_PROBE_MODE") == "receiver")
{
    var path = Environment.GetEnvironmentVariable("NSN_WEBHOOK_PROBE_STATE")!;
    var state = File.Exists(path)
        ? JsonSerializer.Deserialize<Dictionary<Guid, Receipt>>(await File.ReadAllTextAsync(path))!
        : new Dictionary<Guid, Receipt>();
    using var gate = new SemaphoreSlim(1);
    var receiver = builder.Build();
    receiver.MapGet("/health", () => Results.Ok());
    receiver.MapGet("/stats/{id:guid}", async (Guid id) =>
    {
        await gate.WaitAsync();
        try { return Results.Json(state.GetValueOrDefault(id) ?? new Receipt(0, 0, false)); }
        finally { gate.Release(); }
    });
    receiver.MapPost("/webhooks/pricing", async (HttpRequest request) =>
    {
        var message = (await request.ReadFromJsonAsync<PricingRecalculationCompletedV1>())!;
        if (request.Headers["Idempotency-Key"].ToString() != message.IdempotencyKey.ToString("D")) { return Results.BadRequest(); }
        await gate.WaitAsync();
        try
        {
            var prior = state.GetValueOrDefault(message.EventId) ?? new Receipt(0, 0, false);
            state[message.EventId] = prior with { Requests = prior.Requests + 1, Accepted = true };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(state));
        }
        finally { gate.Release(); }
        if (Environment.GetEnvironmentVariable("NSN_WEBHOOK_PROBE_HOLD_REPLY") == "true")
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, request.HttpContext.RequestAborted);
        }
        return Results.Json(new PricingWebhookAcceptedV1 { EventId = message.EventId, IdempotencyKey = message.IdempotencyKey, Accepted = true });
    });
    receiver.MapGet("/webhooks/pricing", async (HttpRequest request) =>
    {
        var key = Guid.Parse(request.Headers["Idempotency-Key"].ToString());
        await gate.WaitAsync();
        try
        {
            var prior = state.GetValueOrDefault(key) ?? new Receipt(0, 0, false);
            state[key] = prior with { Queries = prior.Queries + 1 };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(state));
            return Results.Json(new PricingWebhookAcceptedV1 { EventId = key, IdempotencyKey = key, Accepted = prior.Accepted });
        }
        finally { gate.Release(); }
    });
    await receiver.RunAsync();
}
else
{
    builder.Services.AddNexusStackApplication();
    builder.Services.AddPricingPostgres(Environment.GetEnvironmentVariable("ConnectionStrings__Pricing")!,
        webhookOptions: new PricingWebhookOptions { Enabled = true, Endpoint = "https://receiver.test/webhooks/pricing" });
    // Only the test transport changes: real application worker and HTTP adapter, no machine-wide certificate trust.
    builder.Services.AddSingleton(new HttpClient(new ReceiverTransport(new Uri(Environment.GetEnvironmentVariable("NSN_WEBHOOK_PROBE_RECEIVER")!)))
    {
        Timeout = TimeSpan.FromSeconds(10),
    });
    var worker = builder.Build();
    worker.MapGet("/health", () => Results.Ok());
    worker.MapGet("/status/{id:guid}", async (Guid id, ISender sender) =>
    {
        var result = await sender.QueryAsync(new GetPricingWebhook(id));
        return result.IsSuccess ? Results.Json(result.Value) : Results.NotFound();
    });
    worker.MapPost("/retry/{id:guid}", async (Guid id, RetryRequest request, ISender sender) =>
    {
        var result = await sender.SendAsync(new RetryPricingWebhook(id, request.ExpectedAttemptCount));
        return result.IsSuccess ? Results.Json(result.Value, statusCode: 202) : Results.Problem(statusCode: 503);
    });
    await worker.RunAsync();
}

internal sealed record Receipt(int Requests, int Queries, bool Accepted);
internal sealed record RetryRequest(int ExpectedAttemptCount);

internal sealed class ReceiverTransport(Uri receiver) : HttpMessageHandler
{
    private readonly HttpClient _client = new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var forwarded = new HttpRequestMessage(request.Method, new Uri(receiver, request.RequestUri!.PathAndQuery)) { Content = request.Content };
        foreach (var header in request.Headers) { forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value); }
        return await _client.SendAsync(forwarded, cancellationToken);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _client.Dispose(); }
        base.Dispose(disposing);
    }
}
