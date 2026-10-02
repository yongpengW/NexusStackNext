using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;
using NexusStackNext.Platform.Contracts;

namespace NexusStackNext.Auditing.Endpoints;

internal sealed record AuditingMessaging(RabbitMqOptions Broker, string ConsumerName);

internal sealed partial class AuditingConsumer(AuditingMessaging messaging, IServiceScopeFactory scopes, ILogger<AuditingConsumer> logger)
    : BackgroundService, IIntegrationEventProcessor
{
    public string EventName => SettingCommittedV1.Name;

    // 每条消息一个作用域，跨消息不共享 DbContext；接纳事务在应用适配器内完成。
    public async Task<bool> HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IIntegrationEventProcessor>().HandleAsync(envelope, cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscription = new EventSubscription { EventName = EventName, ConsumerName = messaging.ConsumerName };
        var topology = EventTopology.Create(messaging.Broker.ExchangeName, [subscription]);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await new RabbitMqTopologyBootstrapper(messaging.Broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology), stoppingToken).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    await using var consumer = new RabbitMqConsumer(messaging.Broker, topology, subscription, this);
                    await consumer.RunAsync(stoppingToken).ConfigureAwait(false);
                }
                else { ConsumerUnavailable(logger); }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception) { ConsumerUnavailable(logger); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    [LoggerMessage(4, LogLevel.Warning, "Auditing consumer unavailable; retrying without acknowledging pending messages.")]
    private static partial void ConsumerUnavailable(ILogger logger);
}
