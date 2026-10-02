using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Events;

/// <summary>
/// **排空发件箱的后台循环。**
///
/// <para><b>它补的是一个真实的缺口。</b>第 16 轮 review 发现：`OutboxPublisher` 写完了、
/// `OutboxDeliveryOptions` 写完了、`OutboxDeliveryOptions.BackoffFor` 也有完整的八档退避——
/// 但**没有任何东西在调用它**。<c>AddHostedService</c> 全仓只出现三次，
/// 都与发件箱无关。</para>
///
/// <para>那个缺口的表现是最难查的一种：业务写完数据、事务提交了、outbox 表里有记录，
/// 而它们**永远躺在那里**。没有任何东西报错，日志里也看不出少了什么——
/// 因为"没发出去"和"没有要发的"长得一模一样。</para>
///
/// <para><b>循环自己不做决定。</b>投递、重试、死信的判断全在 <see cref="OutboxPublisher"/> 里，
/// 这里只负责"隔一会儿叫它一次"，并且在它抛异常时**不让循环死掉**。</para>
/// </summary>
public sealed partial class OutboxDeliveryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OutboxDeliveryOptions _options;
    private readonly ILogger<OutboxDeliveryWorker> _logger;
    private readonly string? _owner;

    /// <summary>创建循环。</summary>
    /// <param name="scopeFactory">作用域工厂——投递器是 Scoped 的。</param>
    /// <param name="options">投递策略。</param>
    /// <param name="logger">日志。</param>
    /// <param name="owner">命名的上下文 Outbox；null 保留单生产者宿主的默认绑定。</param>
    public OutboxDeliveryWorker(
        IServiceScopeFactory scopeFactory,
        OutboxDeliveryOptions options,
        ILogger<OutboxDeliveryWorker> logger,
        string? owner = null)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
        _owner = owner;

        options.Validate();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(_options.PollInterval, _options.BatchSize, _options.MaxAttempts);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();

                var publisher = _owner is null ? scope.ServiceProvider.GetRequiredService<OutboxPublisher>()
                    : scope.ServiceProvider.GetRequiredKeyedService<OutboxPublisher>(_owner);
                var result = await publisher.PublishPendingAsync(stoppingToken).ConfigureAwait(false);

                // **只在真的有动作时打日志。** 每两秒一条"本轮 0 条"会把日志淹掉，
                // 而那条日志要与"积压了 500 条在重试"区分开来——后者才是要看的。
                if (result.HasWork)
                {
                    LogRound(result.Examined, result.Delivered, result.Retried, result.DeadLettered);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // **循环不能因为一次失败而死。** 死了之后进程还活着、健康检查还是绿的，
                // 而消息永远不再被投递——那正是这个类要防的那种失效。
                LogRoundFailed(exception);
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "发件箱投递循环已启动：每 {Interval} 拉一批（{BatchSize} 条，最多 {MaxAttempts} 次尝试）。")]
    private partial void LogStarted(TimeSpan interval, int batchSize, int maxAttempts);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "发件箱：读到 {Examined}，投递 {Delivered}，待重试 {Retried}，死信 {DeadLettered}。")]
    private partial void LogRound(int examined, int delivered, int retried, int deadLettered);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error,
        Message = "发件箱投递循环出现异常；下一轮继续。")]
    private partial void LogRoundFailed(Exception exception);
}
