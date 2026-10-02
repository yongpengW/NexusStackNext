using System.Text.Json;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;
using Xunit;

namespace NexusStackNext.IntegrationSupport;

/// <summary>
/// 真 broker 的连接信息与可用性判断。
///
/// <para><b>为什么需要一个"响亮地跳过"的机制。</b>票据 21 的六条验收**全部**只有真 broker
/// 能验证——发布确认、不可路由、通道重建、重复投递、死信。用替身跑它们会全绿，
/// 而绿的是一段"我自己写的行为"，不是 broker 的行为。</para>
///
/// <para>但那也不该让新克隆的仓库在没配 broker 时变红。所以与 <c>[PostgresFact]</c> 同一处理：
/// **缺配置就跳过，并且把"跳过了什么、为什么"写在跳过原因里**——
/// 跳过与通过必须能被区分开，否则这套测试就成了"一个不会失败的检查"。</para>
/// </summary>
internal static class RabbitMqTestBroker
{
    /// <summary>连接信息所在的环境变量名。</summary>
    public const string VariableName = "NEXUSSTACK_TEST_RABBITMQ";

    /// <summary>读连接配置；没配时返回 <c>null</c>。</summary>
    /// <returns>配置，或 <c>null</c>。</returns>
    public static RabbitMqOptions? TryGetOptions()
    {
        var raw = Environment.GetEnvironmentVariable(VariableName);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RabbitMqOptions>(raw, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>取配置；没配时抛——供已经确认可用的测试使用。</summary>
    /// <returns>配置。</returns>
    public static RabbitMqOptions Options =>
        TryGetOptions() ?? throw new InvalidOperationException($"没有配置 {VariableName}。");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>给这次测试运行生成一个**唯一的**名字前缀，避免与别的运行互相干扰。</summary>
    /// <remarks>
    /// <para>broker 是**共享的**——它上面还跑着别的服务。测试用的交换机与队列必须
    /// 每次运行都不同名，否则两次运行会撞在同一个队列上，而那种失败看起来像
    /// "消费者有问题"，不像"两个测试在抢同一个队列"。</para>
    /// </remarks>
    public static string UniquePrefix() => $"nstest-{Guid.NewGuid():N}"[..20];
}

/// <summary>需要真 broker 的测试。</summary>
internal sealed class RabbitMqFactAttribute : FactAttribute
{
    /// <summary>创建特性。</summary>
    public RabbitMqFactAttribute()
    {
        if (RabbitMqTestBroker.TryGetOptions() is null)
        {
            Skip = $"没有 {RabbitMqTestBroker.VariableName}（真 RabbitMQ 的连接信息）："
                + "这条测试只在有 broker 时才有意义——替身会证明我自己写的行为，不是 broker 的行为。";
        }
    }
}
