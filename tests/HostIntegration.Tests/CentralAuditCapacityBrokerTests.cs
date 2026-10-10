using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Ids;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Platform.Contracts;
using NexusStackNext.TestSupport;
using RabbitMQ.Client;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class CentralAuditCapacityBrokerTests
{
    [AuditBrokerFact]
    public async Task FullCentralStore_RetainsOriginalAtBroker_AndAcceptsOnceAfterCapacityRecovery()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        Assert.Equal(0, (await IdentityJourneyDatabase.RunMigrationAsync(database.ConnectionString, "Auditing")).ExitCode);
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-central-capacity", ClientName = prefix };
        var subscription = new EventSubscription { EventName = SettingCommittedV1.Name, ConsumerName = prefix };
        var topology = EventTopology.Create(broker.ExchangeName, [subscription]);
        var serializer = new SystemTextJsonIntegrationEventSerializer();
        var now = DateTimeOffset.UtcNow;
        var original = OutboxEntry.From(new SettingCommittedV1
        {
            EventId = Guid.NewGuid(),
            OccurredAt = now,
            Key = "capacity.first",
            Operation = "created",
            Version = 1,
            TraceId = "capacity-trace",
            CorrelationId = "capacity-correlation",
        }, serializer).ToEnvelope();
        var rejected = OutboxEntry.From(serializer.Deserialize<SettingCommittedV1>(original.Payload) with
        { EventId = Guid.NewGuid(), Key = "capacity.second" }, serializer).ToEnvelope();
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            var factory = new ConnectionFactory
            {
                HostName = broker.HostName,
                Port = broker.Port,
                UserName = broker.UserName,
                Password = broker.Password,
                VirtualHost = broker.VirtualHost,
            };
            await using var connection = await factory.CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();
            await using var bus = new RabbitMqEventBus(broker);
            await using (var app = Application(database.ConnectionString, 1, now, 1000))
            await using (var scope = app.CreateAsyncScope())
            {
                var handler = new PlatformAuditIngestion(scope.ServiceProvider.GetRequiredService<AuditIngestion>(), serializer);
                Assert.True(await handler.HandleAsync(original));
                await using var consumer = new RabbitMqConsumer(broker, topology, subscription, handler);
                using var stopping = new CancellationTokenSource();
                var running = consumer.RunAsync(stopping.Token);
                try
                {
                    Assert.True((await bus.PublishAsync(original)).IsSuccess);
                    await WaitAsync(() => Task.FromResult(consumer.HandledCount == 1));
                    Assert.True((await bus.PublishAsync(rejected)).IsSuccess);
                    await WaitAsync(async () => (await channel.QueueDeclarePassiveAsync(subscription.QueueName)).MessageCount == 0);
                    // 经过基础设施重投的两秒退避；不能把临时故障消耗成坏消息档位。
                    await Task.Delay(TimeSpan.FromMilliseconds(2200));
                    Assert.Equal(1, consumer.HandledCount);
                    Assert.Equal(0, consumer.RetriedCount);
                }
                finally
                {
                    stopping.Cancel();
                    try { await running.WaitAsync(TimeSpan.FromSeconds(10)); }
                    catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
                }
            }
            // 消费者停止后，未确认消息仍在原队列；不靠重新发布伪造恢复。
            var retained = await channel.BasicGetAsync(subscription.QueueName, autoAck: false);
            Assert.NotNull(retained);
            Assert.Equal(rejected.MessageId.ToString("D"), retained.BasicProperties.MessageId);
            Assert.Equal(rejected.Payload, Encoding.UTF8.GetString(retained.Body.Span));
            Assert.Equal(0, ConsumePolicy.ReadAttempts(retained.BasicProperties.Headers?.ToDictionary(pair => pair.Key, pair => pair.Value,
                StringComparer.Ordinal) ?? new Dictionary<string, object?>()));
            await channel.BasicNackAsync(retained.DeliveryTag, multiple: false, requeue: true);
            await using var recovered = Application(database.ConnectionString, 2, now, 10000);
            await using var retry = recovered.CreateAsyncScope();
            await using var resumed = new RabbitMqConsumer(broker, topology, subscription,
                new PlatformAuditIngestion(retry.ServiceProvider.GetRequiredService<AuditIngestion>(), serializer));
            using var finish = new CancellationTokenSource();
            var receiving = resumed.RunAsync(finish.Token);
            try
            {
                await WaitAsync(() => Task.FromResult(resumed.HandledCount == 1));
                Assert.True((await bus.PublishAsync(rejected)).IsSuccess);
                await WaitAsync(() => Task.FromResult(resumed.HandledCount == 2));
            }
            finally
            {
                finish.Cancel();
                try { await receiving.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (OperationCanceledException) when (finish.IsCancellationRequested) { }
            }
            await using var reader = recovered.CreateAsyncScope();
            var entries = await reader.ServiceProvider.GetRequiredService<IAuditEntryStore>().QueryAsync(1, 10);
            Assert.Equal(2, entries.Total);
            Assert.Single(entries.Entries, entry => entry.Fact.MessageId == rejected.MessageId);
            Assert.Equal(2, (await reader.ServiceProvider.GetRequiredService<IAuditStorageCapacityReader>().ReadAsync()).Facts.Records);
            Assert.Equal(0U, (await channel.QueueDeclarePassiveAsync(subscription.DeadLetterQueueName)).MessageCount);
            Assert.Equal(0, resumed.RetriedCount);
        }
        finally { await AuditBusinessJourneyTests.DeleteTopologyAsync(broker, topology); }
    }

    private static ServiceProvider Application(string connection, int maxFacts, DateTimeOffset now, long idsStart)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock>(new FixedClock(now));
        services.AddSingleton<IIdGenerator>(new SequentialIdGenerator(idsStart));
        // 本例验证 broker 保管与消息身份，单独的锁竞争旅程验证短等待；留出冷模型初始化预算。
        services.AddAuditingPostgresStorage(connection, new() { MaxFacts = maxFacts, WaitTimeoutMilliseconds = 10000 });
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static async Task WaitAsync(Func<Task<bool>> reached)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!await reached()) { await Task.Delay(50, timeout.Token); }
    }
}
