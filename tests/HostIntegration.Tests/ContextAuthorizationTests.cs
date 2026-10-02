using System.Net;
using System.Net.Http.Json;

namespace NexusStackNext.HostIntegration.Tests;

/// <summary>
/// **四个上下文在进程内也做授权判定**——直连后端绕不过去。
///
/// <para>此前只有 Identity 挂了授权过滤器，其余四个模块的端点在进程内**没有任何判定**。
/// 网关确实会挡，但那是**编排**的事实，不是代码的事实：谁能直连到这个进程，
/// 谁就绕过了整套保护——而部署不变量（"业务服务不对外暴露、边缘是唯一入口"）
/// **没有任何测试守着**（AGENTS.md 自己写着这一点）。所以它的失效只能靠代码里的
/// 第二道判定兜住，这道判定此前并不存在。</para>
///
/// <para>判据与边缘的路由表**逐条对应**：公开的读能匿名访问，写要 401。
/// 与边缘不一致时，两者之中必有一个是错的——而那种不一致不会有别的东西发现。</para>
/// </summary>
/// <param name="app">真正的平台宿主（内存存储、不需要数据库与配置中心）。</param>
public sealed class ContextAuthorizationTests(PlatformApp app) : IClassFixture<PlatformApp>
{
    /// <summary>边缘上标为公开的读，直连后端也必须能匿名访问。</summary>
    [Fact]
    public async Task PublicReads_AreReachableWithoutAToken()
    {
        using var client = app.CreateClient();

        foreach (var path in new[] { "/api/scheduling" })
        {
            using var response = await client.GetAsync(new Uri(path, UriKind.Relative));

            // 200 与 400 都算"到了处理器"——这里问的是**有没有被授权层挡下**。
            Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    /// <summary>边缘上要求认证的写，直连后端也必须被挡下——<b>否则多一道防线只是说法</b>。</summary>
    [Fact]
    public async Task Writes_AreRejectedWithoutAToken()
    {
        using var client = app.CreateClient();

        using var writeSetting = await client.PutAsJsonAsync(
            new Uri("/api/platform/settings/auth.probe", UriKind.Relative),
            new { Value = "x", Description = "probe" });
        Assert.Equal(HttpStatusCode.Unauthorized, writeSetting.StatusCode);

        using var upload = await client.PostAsync(
            new Uri("/api/files?name=probe.bin", UriKind.Relative),
            new ByteArrayContent([1, 2, 3]));
        Assert.Equal(HttpStatusCode.Unauthorized, upload.StatusCode);

        using var define = await client.PostAsJsonAsync(
            new Uri("/api/scheduling/tasks/", UriKind.Relative),
            new { Code = "probe.task", IntervalSeconds = 30 });
        Assert.Equal(HttpStatusCode.Unauthorized, define.StatusCode);
    }
}
