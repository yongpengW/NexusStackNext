using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Gateway;

namespace NexusStackNext.HostIntegration.Tests;

/// <summary>
/// 网关实时通道的契约：**状态变化时推，不变时绝不重复推。**
///
/// <para><b>为什么这条值得一个集成测试。</b>"只在变化时推"与聚合版本号的
/// "空操作不自增"是同一条道理：一个不断重复同一内容的通道会让客户端学会忽略它，
/// 于是它真正要传达的那一次也会被忽略——**而失效方式很安静**，
/// 客户端照常收消息、照常不理会。</para>
///
/// <para><b>它此前是不可重现的。</b>上一轮做过完整的端到端验证（真客户端连上、
/// 收到两条推送、50 秒里只发了 2 条），但那是在临时目录的探针里做的——
/// 仓库里没有任何东西守着这个 hub。验证是真的，**下次有人改坏它，<c>dotnet test</c> 不会红**。
/// 这个类就是把那次验证搬进仓库。</para>
///
/// <para><b>反面也要测。</b>只断言"没再推"是不够的：一个彻底坏掉的广播器同样不会推。
/// 所以测试先制造一次变化、断言推到了，再静置、断言没重复——
/// 两边都过了，"只在变化时推"才算被证明。</para>
/// </summary>
/// <param name="app">被测网关。</param>
public sealed class GatewayHubTests(GatewayApp app) : IClassFixture<GatewayApp>
{
    /// <summary>等待一条推送的上限。轮询周期是 3 秒，留足余量。</summary>
    private static readonly TimeSpan PushTimeout = TimeSpan.FromSeconds(20);

    /// <summary>静置窗口：**三个轮询周期**。少于两个就无法区分"没推"与"还没来得及推"。</summary>
    private static readonly TimeSpan QuietWindow = TimeSpan.FromSeconds(9);

    /// <summary>整个用例的上限，防止连接或推送挂死时测试永不结束。</summary>
    private static readonly TimeSpan WholeTestTimeout = TimeSpan.FromMinutes(2);

    /// <summary>状态变化时推一条；不变时一条都不多推。</summary>
    [Fact]
    public async Task PushesOnChange_AndStaysQuietWhileUnchanged()
    {
        using var overall = new CancellationTokenSource(WholeTestTimeout);

        using var httpClient = app.CreateDefaultClient();

        await using var connection = new HubConnectionBuilder()
            .WithUrl(
                new Uri(httpClient.BaseAddress!, "hubs/gateway"),
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();

                    // 走长轮询：TestServer 的内存通道对 WebSocket 升级的支持有限，
                    // 而这里要验的是**服务端推什么**，不是用哪种传输。
                    options.Transports = HttpTransportType.LongPolling;
                })
            .Build();

        var pushes = Channel.CreateUnbounded<bool>();

        connection.On<JsonElement>(
            GatewayHub.ClusterStatusMessage,
            payload => pushes.Writer.TryWrite(ReadHealthy(payload)));

        await connection.StartAsync(overall.Token);

        // 广播器在启动时已经推过一条——那时候还没有客户端，所以测试收不到。
        // 因此这里先**制造一次变化**，让"推"在客户端连上之后真的发生一次。
        app.Health.Healthy = false;

        var first = await ReadNextAsync(pushes, PushTimeout, overall.Token);
        Assert.True(first.HasValue, "后端转为不可达之后，应当推一条 clusterStatus。");
        Assert.False(first!.Value, "推送里的 healthy 应当是 false。");

        // **契约的核心**：状态没变，就不该有第二条。
        Assert.False(
            await TryReadAsync(pushes, QuietWindow, overall.Token),
            $"静置 {QuietWindow.TotalSeconds} 秒（三个轮询周期）里状态没变，却又推了一条。");

        // 反方向：再变一次**必须**推。否则上面那句"没再推"可能只是因为广播器根本是坏的。
        app.Health.Healthy = true;

        var second = await ReadNextAsync(pushes, PushTimeout, overall.Token);
        Assert.True(second.HasValue, "后端恢复之后，应当推一条 clusterStatus。");
        Assert.True(second!.Value, "推送里的 healthy 应当是 true。");

        Assert.False(
            await TryReadAsync(pushes, QuietWindow, overall.Token),
            "状态恢复之后又静置了一轮，不该再收到推送。");
    }

    /// <summary>从推送负载里读 <c>healthy</c>，**大小写不敏感**（不依赖序列化命名策略）。</summary>
    private static bool ReadHealthy(JsonElement payload)
    {
        foreach (var property in payload.EnumerateObject())
        {
            if (string.Equals(property.Name, "healthy", StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.GetBoolean();
            }
        }

        throw new InvalidOperationException("clusterStatus 推送里没有 healthy 字段。");
    }

    /// <summary>等下一条推送；超时返回 <c>null</c>。</summary>
    private static async Task<bool?> ReadNextAsync(Channel<bool> pushes, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            return await pushes.Reader.ReadAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>在给定窗口内**不该**收到推送；收到了返回 <c>true</c>。</summary>
    private static async Task<bool> TryReadAsync(Channel<bool> pushes, TimeSpan window, CancellationToken cancellationToken)
    {
        using var windowSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        windowSource.CancelAfter(window);

        try
        {
            _ = await pushes.Reader.ReadAsync(windowSource.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>
/// 起一个真正的网关，并把"后端此刻是否可达"交给测试控制。
///
/// <para><b>探针一行都没改。</b>它走 <c>IHttpClientFactory.CreateClient(nameof(ClusterReachabilityProbe))</c>——
/// 一个**具名**客户端。测试只要给那个名字换上一个可控的 <see cref="HttpMessageHandler"/>，
/// 真正的 <c>ClusterReachabilityProbe</c> 就原样跑起来了。
/// 这比"为了测试把探针抽成接口"更好：生产代码不必为测试让步，
/// 而被测的也就真的是生产里那段代码。</para>
/// </summary>
public sealed class GatewayApp : WebApplicationFactory<GatewayHostMarker>
{
    /// <summary>后端可达性的开关。</summary>
    public ControllableHealthHandler Health { get; } = new();

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureServices(services =>
            services.AddHttpClient(nameof(ClusterReachabilityProbe))
                .ConfigurePrimaryHttpMessageHandler(() => Health));
    }
}

/// <summary>按开关回答一切请求的 HTTP 处理器——探针只关心状态码，不关心正文。</summary>
public sealed class ControllableHealthHandler : HttpMessageHandler
{
    /// <summary>
    /// 为 <c>true</c> 时答 200，否则答 503。
    ///
    /// <para><b>初值是 <c>true</c>，这一点是有意的。</b>广播器在启动时读一次并推一次；
    /// 如果初值是"不可达"，那么测试第一次把它设成"不可达"就是**空操作**，
    /// 收不到推送——而那既可能是断言写错了，也可能是这个处理器**根本没接上**
    /// （真实探针打不到 <c>127.0.0.1:5191</c>，同样报不可达）。两者症状一模一样。
    /// 从"可达"起步，第一次翻转就必然是一次真实的变化，于是它顺带证明了
    /// **这个可控处理器确实替换掉了真实探针的网络调用**。</para>
    /// </summary>
    public bool Healthy { get; set; } = true;

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Task.FromResult(new HttpResponseMessage(
            Healthy ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable));
    }

    /// <summary>
    /// 这个实例由测试持有并复用，**不能让工厂把它释放掉**——
    /// 释放之后它的开关就失效了，而测试会以"收不到推送"的形式失败，看不出真正原因。
    /// <c>base.Dispose</c> 在 <see cref="HttpMessageHandler"/> 上是空实现，所以调用它是安全的。
    /// </summary>
    /// <param name="disposing">是否释放托管资源。</param>
    protected override void Dispose(bool disposing) => base.Dispose(disposing);
}
