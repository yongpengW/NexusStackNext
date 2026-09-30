using System.Text;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Domain;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;

/// <summary>RabbitMQ 连接配置。</summary>
public sealed record RabbitMqOptions
{
    /// <summary>主机。</summary>
    public string HostName { get; init; } = "localhost";

    /// <summary>端口。</summary>
    public int Port { get; init; } = 5672;

    /// <summary>用户名。</summary>
    public string UserName { get; init; } = "guest";

    /// <summary>口令。</summary>
    public string Password { get; init; } = string.Empty;

    /// <summary>虚拟主机。</summary>
    public string VirtualHost { get; init; } = "/";

    /// <summary>交换机名。</summary>
    public string ExchangeName { get; init; } = "nexusstack";

    /// <summary>连接名——在 broker 的管理界面上用来认人。</summary>
    public string ClientName { get; init; } = "nexusstack";
}

/// <summary>
/// 用 RabbitMQ 发布集成事件。
///
/// <para><b>它最重要的一条性质：发不出去就是失败，绝不静默成功。</b>
/// 参照仓库的 <c>EventPublisher.cs</c> 在发布异常时只记一条日志，然后**照样 ACK** ——
/// 上游因此认为事件已经发出去了，而实际上没有任何人收到。
/// 那种失效最难查：生产端一切正常，消费端什么都没发生，中间没有任何东西报错。</para>
///
/// <para>做法是两件事一起开：<b>发布确认</b>（broker 确认收到了）与
/// <b>确认跟踪</b>（客户端把确认跟发布调用对上号）。再加上 <c>mandatory: true</c>，
/// 一条路由不到任何队列的消息会被 broker **退回**，而客户端会把它变成一次失败。</para>
///
/// <para><b>这条行为只有真 broker 能验证</b>——替身只会证明我自己写的行为。
/// 所以票据 21 一直没有开工，直到确认那台 broker 真的可用。</para>
/// </summary>
public sealed class RabbitMqEventBus : IEventBus, IAsyncDisposable
{
    private readonly RabbitMqOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IConnection? _connection;
    private IChannel? _channel;

    /// <summary>创建发布端。</summary>
    /// <param name="options">连接配置。</param>
    public RabbitMqEventBus(RabbitMqOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc />
    public async Task<Result> PublishAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        try
        {
            var channel = await GetChannelAsync(cancellationToken).ConfigureAwait(false);

            var properties = new BasicProperties
            {
                // 持久化：broker 重启后消息还在。它与队列的 durable 是两件事，都要开。
                Persistent = true,
                MessageId = envelope.MessageId.ToString("D", System.Globalization.CultureInfo.InvariantCulture),
                ContentType = "application/json",
                Type = envelope.EventName,
                Timestamp = new AmqpTimestamp(envelope.OccurredAt.ToUnixTimeSeconds()),
                Headers = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["EventName"] = envelope.EventName,
                },
            };

            await channel.BasicPublishAsync(
                exchange: _options.ExchangeName,
                routingKey: envelope.RoutingKey,
                mandatory: true,
                basicProperties: properties,
                body: Encoding.UTF8.GetBytes(envelope.Payload),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return Result.Success();
        }
        catch (PublishReturnException ex)
        {
            // **broker 把消息退回来了**：这条路由键没有任何队列绑定在听。
            // 注意它与"发布失败"的区别：消息**发出去了**，只是没人接。
            // 参照仓库在这里记一条日志就过去了，于是"发出去没人收"变成了"发成功了"。
            return Result.Failure(MessagingErrors.Unroutable($"{envelope.EventName}（{ex.Message}）"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is BrokerUnreachableException or NotSupportedException or System.Net.Sockets.SocketException)
        {
            return Result.Failure(MessagingErrors.BrokerUnavailable(ex.Message));
        }
    }

    /// <summary>
    /// 取通道，必要时建立连接。
    ///
    /// <para>发布端**不假设连接一直活着**：broker 重启、网络抖动、心跳超时都会让它断。
    /// 每次发布前检查一次、断了就重建——比"启动时连一次、之后听天由命"多一次判断，
    /// 但少一整类"跑着跑着就发不出去了，而没有任何东西重启它"的问题。</para>
    /// </summary>
    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_channel is { IsOpen: true })
            {
                return _channel;
            }

            if (_connection is not { IsOpen: true })
            {
                var factory = new ConnectionFactory
                {
                    HostName = _options.HostName,
                    Port = _options.Port,
                    UserName = _options.UserName,
                    Password = _options.Password,
                    VirtualHost = _options.VirtualHost,
                    ClientProvidedName = _options.ClientName,
                };

                _connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
            }

            // **发布确认 + 确认跟踪。** 少了这两个，`BasicPublishAsync` 在"消息还在路上"时就返回了，
            // 于是"发不出去"与"发出去了"在调用方看来一模一样。
            _channel = await _connection.CreateChannelAsync(
                new CreateChannelOptions(
                    publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true),
                cancellationToken).ConfigureAwait(false);

            return _channel;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>关掉连接。</summary>
    /// <returns>任务。</returns>
    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync().ConfigureAwait(false);
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }
}
