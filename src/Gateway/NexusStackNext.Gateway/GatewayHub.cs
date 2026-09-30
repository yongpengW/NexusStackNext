using Microsoft.AspNetCore.SignalR;

namespace NexusStackNext.Gateway;

/// <summary>
/// 边缘的实时通道。客户端连到 <c>/hubs/gateway</c>。
///
/// <para><b>为什么 hub 在网关上而不是业务服务上：</b>部署不变量说"业务服务不对外暴露，
/// 边缘是唯一入口"。客户端只到得了边缘，所以实时通道也只能在这里——
/// 放在任何一个上下文里，它都会变成第二个对外入口，而那正是这条不变量要禁止的。</para>
///
/// <para>这个 hub 目前只有一个方法面：**接收服务端推送**。
/// 业务语义的推送（订单状态、任务完成……）应当由产生它的上下文经消息总线发到边缘，
/// 而不是让边缘自己去查——那会把业务知识搬进边缘。</para>
/// </summary>
/// <param name="logger">日志。</param>
public sealed partial class GatewayHub(ILogger<GatewayHub> logger) : Hub
{
    /// <summary>服务端推送的消息名。</summary>
    public const string ClusterStatusMessage = "clusterStatus";

    /// <inheritdoc />
    public override async Task OnConnectedAsync()
    {
        LogClientConnected(logger, Context.ConnectionId);
        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        LogClientDisconnected(logger, Context.ConnectionId);
        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "实时客户端已连接：{ConnectionId}")]
    private static partial void LogClientConnected(ILogger logger, string connectionId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "实时客户端已断开：{ConnectionId}")]
    private static partial void LogClientDisconnected(ILogger logger, string connectionId);
}
