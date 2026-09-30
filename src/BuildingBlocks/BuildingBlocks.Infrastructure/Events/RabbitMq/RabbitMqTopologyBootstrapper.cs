using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Domain;
using RabbitMQ.Client;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;

/// <summary>
/// 把 <see cref="RabbitTopologyPlanner.Plan"/> 的产物**幂等地**声明到 broker。
///
/// <para><b>幂等不是"顺便"。</b>每个服务实例启动时都会跑一遍——多副本部署时它们
/// **同时**跑。RabbitMQ 的 <c>declare</c> 本身是幂等的（存在就校验参数），
/// 所以真正的风险不是"重复声明"，而是**参数不一致**：一次带 <c>x-message-ttl</c>、
/// 一次不带，broker 会以 <c>PRECONDITION_FAILED</c> 拒绝——而那条错误说的是
/// "队列参数不匹配"，不会告诉你"是两个服务用了不同的计划"。</para>
///
/// <para>所以这里只声明**计划里有的东西**：多声明一个队列不报错，但会在 broker 上留下
/// 一个永远没人消费的队列，而它看起来像"某个消费者还没起来"。</para>
/// </summary>
/// <param name="options">连接配置。</param>
public sealed class RabbitMqTopologyBootstrapper(RabbitMqOptions options)
{
    private readonly RabbitMqOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>把计划声明到 broker。</summary>
    /// <param name="plan">拓扑计划。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功，或失败原因。</returns>
    public async Task<Result> ApplyAsync(TopologyPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        try
        {
            var factory = new ConnectionFactory
            {
                HostName = _options.HostName,
                Port = _options.Port,
                UserName = _options.UserName,
                Password = _options.Password,
                VirtualHost = _options.VirtualHost,
                ClientProvidedName = $"{_options.ClientName}-topology",
            };

            await using var connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            // 交易所：与队列一样，`durable` 必须与既有的一致，否则 PRECONDITION_FAILED。
            await channel.ExchangeDeclareAsync(
                exchange: plan.ExchangeName,
                type: plan.ExchangeType,
                durable: true,
                autoDelete: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            foreach (var queue in plan.Queues)
            {
                await channel.QueueDeclareAsync(
                    queue: queue.Name,
                    durable: queue.Durable,
                    // **独占为 false**：这些队列要被多个消费者实例共享。
                    // 注意这台 broker 上"临时的非独占队列"已被禁用
                    // （`transient_nonexcl_queues` 废弃），所以计划里的队列必须
                    // `durable: true`——否则声明会当场失败。
                    exclusive: false,
                    autoDelete: false,
                    arguments: queue.Arguments.Count > 0
                        ? new Dictionary<string, object?>(queue.Arguments, StringComparer.Ordinal)
                        : null,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            foreach (var binding in plan.Bindings)
            {
                await channel.QueueBindAsync(
                    queue: binding.Queue,
                    exchange: plan.ExchangeName,
                    routingKey: binding.RoutingKey,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result.Failure(MessagingErrors.BrokerUnavailable($"拓扑声明失败：{ex.Message}"));
        }
    }
}
