using System.Globalization;
using AgileConfig.Client;
using AgileConfig.Client.RegisterCenter;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace NexusStackNext.Composition;

/// <summary>
/// 宿主的配置与日志组成。**两个宿主用完全相同的这几行**，所以它们住在这里而不是抄两遍。
/// （五个平台能力合成一个宿主之后就是两个：平台宿主与网关，见 ADR-0013。）
/// </summary>
public static class NexusStackHostDefaults
{
    /// <summary>AgileConfig 配置节。</summary>
    public const string AgileConfigSection = "AgileConfig";

    /// <summary>Serilog 配置节。</summary>
    public const string SerilogSection = "Serilog";

    /// <summary>
    /// 接入 AgileConfig，并按既定优先级插入到正确位置。
    ///
    /// <para><b>优先级：环境变量 &gt; AgileConfig &gt; appsettings.{Env}.json &gt; appsettings.json</b>
    /// （见 ADR-0006 与 <c>README.md</c>）。</para>
    ///
    /// <para><b>降级：</b>没有配置 <c>AppId</c> 或 <c>Nodes</c> 时**照常启动**，
    /// 只是不接入配置中心，并明确记一条日志。这是 ADR-0006 自己要求的那条
    /// "不依赖它的降级路径"——否则离线开发不可用，而"跑不起来"会成为新克隆仓库的第一印象。</para>
    ///
    /// <para><b>不可达同样不致命：</b>配置中心挂掉时应用继续用它已经拿到的（或本地）配置启动。
    /// 配置中心是**运行时**的便利，不该变成**启动时**的单点。</para>
    /// </summary>
    /// <param name="builder">Web 应用构建器。</param>
    /// <returns>同一个构建器，便于串联。</returns>
    public static WebApplicationBuilder AddNexusStackAgileConfig(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var section = builder.Configuration.GetSection(AgileConfigSection);
        var appId = section["AppId"];
        var nodes = section["Nodes"];

        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(nodes))
        {
            // 走降级路径。**这是正常状态**，不是错误——本地开发与新克隆的仓库默认就在这条路径上。
            Log.Logger.Information(
                "未配置 AgileConfig（{Section}:AppId / Nodes 为空），使用 appsettings 与环境变量。"
                + "这是正常的降级路径，不是错误。",
                AgileConfigSection);
            return builder;
        }

        builder.Configuration.AddAgileConfig(options => BindOptions(section, options));

        // **为什么再追加一个环境变量来源，而不是把 AgileConfig 插到它前面。**
        //
        // 文档与 ADR 说的是"环境变量 > AgileConfig"。但 `WebApplicationBuilder.Configuration`
        // 是 `ConfigurationManager`，对它 `Sources` 的**重排不生效**——
        // 实测：插进去的来源仍然盖过环境变量（见 ConfigurationOrderingInRealHostTests）。
        // 我此前的优先级测试用的是普通 `ConfigurationBuilder`，**测的不是宿主真正用的那个类型**，
        // 所以它一直绿着。
        //
        // 而"环境变量最高"是运维的硬需求：容器里必须能覆盖配置中心的任何一项。
        // 所以改用唯一受支持的手段——**追加**（`ConfigurationManager` 支持 Add）：
        // 再加一个环境变量来源，它排在最后，也就是最高。
        //
        // 代价是环境变量被读两遍（一次在默认位置、一次在这里）。那一遍几乎不花时间，
        // 而"容器里覆盖不了配置"是会真出事的那一类问题。
        builder.Configuration.AddEnvironmentVariables();

        // **把 env / name / tag 一起记出来。**
        // 这三个为空时应用照样启动、照样拉到配置——只是拉的是"无环境"那一份，
        // 而"配置看着有、其实少了一截"是最难发现的一类问题。让它在启动日志里可见。
        Log.Logger.Information(
            "已接入 AgileConfig：AppId={AppId}，Env={Env}，Name={Name}，Tag={Tag}，节点={Nodes}。",
            appId,
            section["Env"] ?? "（无）",
            section["Name"] ?? "（无）",
            section["Tag"] ?? "（无）",
            nodes);

        return builder;
    }

    /// <summary>
    /// 把 <c>AgileConfig</c> 配置节映射到客户端的选项对象。
    ///
    /// <para><b>为什么需要显式映射。</b>AgileConfig 客户端**不会**自动读取配置节——
    /// <c>AddAgileConfig</c> 只收一个回调或一个已构造好的选项对象。
    /// 照着"只填 AppId/Secret/Nodes"来写，其余选项会被**静默丢掉**：
    /// 应用能启动、能拉到配置，只是 <c>env</c> 为空、客户端在后台显示为空白、
    /// 而<b>多环境配置一条都拉不到</b>。这正是本仓踩过的坑。</para>
    ///
    /// <para>抽成公开静态方法是为了**可单测**：每一个键都要有一条断言盯着，
    /// 否则下一次加字段时同样的静默丢字段会再来一遍。</para>
    /// </summary>
    /// <param name="section">AgileConfig 配置节。</param>
    /// <param name="options">要填充的选项。</param>
    public static void BindOptions(IConfiguration section, ConfigClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(options);

        options.AppId = section["AppId"] ?? string.Empty;
        options.Secret = section["Secret"] ?? string.Empty;
        options.Nodes = section["Nodes"] ?? string.Empty;

        // 运维可见性：后台「客户端」页面靠这两个把连接显示成人能看懂的东西。
        options.Name = section["Name"] ?? string.Empty;
        options.Tag = section["Tag"] ?? string.Empty;

        // 多环境。为空表示拉"无环境"那一份——若配置中心按环境分了配置，那就少了一截。
        options.ENV = section["Env"] ?? string.Empty;

        // 落地缓存。不配则用客户端的默认位置（宿主目录下，已被 *.cache 与模板排除覆盖）。
        var cacheDirectory = section["Cache:Directory"];
        if (!string.IsNullOrWhiteSpace(cacheDirectory))
        {
            options.CacheEnabled = true;
            options.CacheDirectory = cacheDirectory;
        }

        if (bool.TryParse(section["Cache:Encrypt"], out var encrypt))
        {
            options.ConfigCacheEncrypt = encrypt;
        }

        // 服务注册是**可选特性**，两个键都给齐才启用——
        // 只写一半就注册出一个没有名字的服务，比不注册更难查。
        var serviceId = section["ServiceRegister:ServiceId"];
        var serviceName = section["ServiceRegister:ServiceName"];
        if (!string.IsNullOrWhiteSpace(serviceId) && !string.IsNullOrWhiteSpace(serviceName))
        {
            options.RegisterInfo = new ServiceRegisterInfo
            {
                ServiceId = serviceId,
                ServiceName = serviceName,
            };

            if (section["ServiceRegister:Ip"] is { Length: > 0 } ip)
            {
                options.RegisterInfo.Ip = ip;
            }

            if (int.TryParse(section["ServiceRegister:Port"], out var port))
            {
                options.RegisterInfo.Port = port;
            }
        }
    }

    /// <summary>
    /// 接入 Serilog。
    ///
    /// <para><b>配置形态是标准的 <c>ReadFrom.Configuration</c></b>——<c>Using</c> / <c>WriteTo</c> /
    /// <c>LevelSwitches</c> / <c>Enrich</c> / <c>MinimumLevel:ControlledBy</c> 都吃。
    /// 这样从 AgileConfig 导出的配置可以**原样使用**，不必为这个模板改写一套形状。</para>
    ///
    /// <para><b>兜底：配置里没有任何 <c>WriteTo</c> 时，加一个控制台 sink。</b>
    /// 盯的是"<c>UseSerilog</c> 收到空配置 = 一个 sink 都没有 = 一行日志都不输出"这个失败模式
    /// （参照仓库正是如此，而它恰恰发生在最需要日志的时候）。
    /// 注意兜底条件是"<b>零个 sink</b>"，不是"永远加控制台"——
    /// 配置里已经写了 sink 时不该再塞一个进去，否则同一个事件会被打印两遍。</para>
    /// </summary>
    /// <param name="builder">Web 应用构建器。</param>
    /// <returns>同一个构建器，便于串联。</returns>
    public static WebApplicationBuilder AddNexusStackLogging(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var hasConfiguredSinks = builder.Configuration
            .GetSection(SerilogSection)
            .GetSection("WriteTo")
            .GetChildren()
            .Any();

        var loggerConfiguration = new LoggerConfiguration()
            .ReadFrom.Configuration(builder.Configuration)
            // 关联 ID 由中间件塞进 LogContext，这里让它能出现在每一条日志上。
            .Enrich.FromLogContext();

        if (!hasConfiguredSinks)
        {
            loggerConfiguration.WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}",
                formatProvider: CultureInfo.InvariantCulture);
        }

        // 先赋给静态 Log.Logger：配置中心还没接上时就要能记日志，
        // 而那正是最需要看到日志的时刻。
        Log.Logger = loggerConfiguration.CreateLogger();
        builder.Services.AddSerilog(Log.Logger, dispose: true);

        if (!hasConfiguredSinks)
        {
            Log.Logger.Information(
                "Serilog 配置里没有 WriteTo，已用控制台 sink 兜底——否则这会是一条都看不到的日志。");
        }

        return builder;
    }

}
