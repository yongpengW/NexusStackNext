using RabbitMQ.Client;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;

/// <summary>有界验证 broker 认证与交换机可达性；不发布探测消息。</summary>
public static class RabbitMqReadiness
{
    /// <summary>检查配置的 broker 与交换机；不声明拓扑、不修改消息。</summary>
    /// <param name="options">宿主配置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否可以连接并找到交换机；不等同于下游业务已完成。</returns>
    public static async Task<bool> IsReadyAsync(RabbitMqOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var factory = new ConnectionFactory
            {
                HostName = options.HostName,
                Port = options.Port,
                UserName = options.UserName,
                Password = options.Password,
                VirtualHost = options.VirtualHost,
                AutomaticRecoveryEnabled = false,
                ClientProvidedName = options.ClientName + "-readiness",
            };
            await using var connection = await factory.CreateConnectionAsync(timeout.Token).ConfigureAwait(false);
            await using var channel = await connection.CreateChannelAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
            await channel.ExchangeDeclarePassiveAsync(options.ExchangeName, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception) { return false; }
    }
}
