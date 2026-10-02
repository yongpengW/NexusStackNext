using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusStackNext.BuildingBlocks.Application.Events;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;

internal sealed partial class RabbitMqSubscriptionWorker(RabbitMqOptions broker, EventSubscription subscription,
    IServiceScopeFactory scopes, ILogger<RabbitMqSubscriptionWorker> logger) : BackgroundService, IIntegrationEventProcessor
{
    public string EventName => subscription.EventName;

    public async Task<bool> HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredKeyedService<IIntegrationEventProcessor>(EventName);
        return await processor.HandleAsync(envelope, cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var topology = EventTopology.Create(broker.ExchangeName, [subscription]);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology), stoppingToken).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    await using var consumer = new RabbitMqConsumer(broker, topology, subscription, this);
                    await consumer.RunAsync(stoppingToken).ConfigureAwait(false);
                }
                else { ConsumerUnavailable(logger, subscription.ConsumerName, EventName); }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception) { ConsumerUnavailable(logger, subscription.ConsumerName, EventName); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    [LoggerMessage(4, LogLevel.Warning, "RabbitMQ consumer {ConsumerName} for {EventName} unavailable; retrying without acknowledging pending messages.")]
    private static partial void ConsumerUnavailable(ILogger logger, string consumerName, string eventName);
}

/// <summary>由 Pricing 与 Auditing 共同使用的消息消费生命周期装配。</summary>
public static class RabbitMqSubscriptionServiceCollectionExtensions
{
    /// <summary>显式启动一个订阅，每条消息使用独立作用域内匹配事件名的处理器。</summary>
    /// <param name="services">服务集合，需按事件名注册 keyed IIntegrationEventProcessor。</param>
    /// <param name="broker">所属 broker 配置。</param>
    /// <param name="subscription">显式订阅。</param>
    /// <returns>服务集合。</returns>
    public static IServiceCollection AddNexusStackRabbitMqConsumer(this IServiceCollection services,
        RabbitMqOptions broker, EventSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(subscription);
        // 每次调用都保留一个订阅，不用 AddHostedService 按实现类型去重。
        services.AddSingleton<IHostedService>(provider => new RabbitMqSubscriptionWorker(broker, subscription,
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<ILogger<RabbitMqSubscriptionWorker>>()));
        return services;
    }
}
