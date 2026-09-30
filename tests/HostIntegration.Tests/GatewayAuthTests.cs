using System.Net;

namespace NexusStackNext.HostIntegration.Tests;

/// <summary>
/// 边缘的**认证边界**：受保护的路由在未认证时返回 **401**。
///
/// <para><b>为什么这条值得一个测试。</b>参照仓库在这个位置的表现是
/// "**HTTP 200 + 响应体里一个 code=401**"——SDK 与网关据此判断不了任何事，
/// 它们看到的是"每个请求都成功了"。状态码是**协议层的事实**，
/// 把它放进响应体等于要求每一个人都记得去看那个字段。</para>
///
/// <para><b>它不需要后端在跑。</b>认证发生在转发**之前**——
/// 拒绝的请求根本不会到达后端，所以这一条验的正是"边缘自己会不会挡"。</para>
/// </summary>
public sealed class GatewayAuthTests(GatewayApp app) : IClassFixture<GatewayApp>
{
    /// <summary>受保护路由（写操作）在未认证时 → **401**。</summary>
    [Fact]
    public async Task ProtectedRoute_WithoutCredentials_Returns401()
    {
        using var client = app.CreateDefaultClient();

        // **样本换过一次，而换它是因为它原来错了。**
        //
        // 原来这里是 `POST /api/identity/users`——**自注册，产品决定里就是匿名的**。
        // 那时它返回 401，不是因为"边缘正确地挡住了写操作"，
        // 而是因为路由表里一条前缀路由 `/api/identity/{**catch-all}` + `requireAuthentication: true`
        // 把**所有** identity 端点都吞了，包括 `POST /login`。
        //
        // 于是这条测试**把 bug 当成了规格**：它绿着，而没有任何用户能登录。
        // 第 27 轮的真实 HTTP 旅程发现了它（注册 401、登录 401），
        // 修好路由表之后这条测试才红——红得对，因为它挑错了样本。
        //
        // 现在挑的是**真正需要管理员权限**的操作：给人分配角色。
        using var response = await client.PostAsync(
            new Uri("/api/identity/users/1/roles/1", UriKind.Relative),
            content: null);

        // 401 而不是 502：502 会说明请求已经尝试转发（即认证没挡住），
        // 而那时后端不在，测试看起来"失败了"却指向了错误的原因。
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// **自注册是匿名的**——这是产品决定，不是遗漏，所以它值得一条断言。
    ///
    /// <para>没有令牌的人必须能注册，否则新用户永远进不来。
    /// 代价写在 Identity 模块里：任何人都能建账号，防滥用交给限流与验证码，
    /// 而不是靠"要求先登录"这种自相矛盾的门槛。</para>
    ///
    /// <para>断言的是"**没被认证层拦下**"，不是后端返回了什么——这里没有后端。</para>
    /// </summary>
    [Fact]
    public async Task SelfRegistration_IsNotBlockedByAuthentication()
    {
        using var client = app.CreateDefaultClient();

        using var response = await client.PostAsync(
            new Uri("/api/identity/users", UriKind.Relative),
            content: null);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// **公开路由不会被认证挡住**——否则这条边界就从"保护"变成了"全堵"。
    ///
    /// <para>它不要求后端可达：请求会被转发然后失败，但**不是 401**。
    /// 断言的是"没被认证层拦下"，而不是"后端返回了什么"。</para>
    /// </summary>
    [Fact]
    public async Task PublicRoute_IsNotBlockedByAuthentication()
    {
        using var client = app.CreateDefaultClient();

        using var response = await client.GetAsync(new Uri("/api/identity", UriKind.Relative));

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>边缘自己的端点不要求认证——健康检查与路由表要能被运维看到。</summary>
    [Fact]
    public async Task EdgeOwnEndpoints_AreNotGated()
    {
        using var client = app.CreateDefaultClient();

        using var health = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        using var routes = await client.GetAsync(new Uri("/gateway/routes", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, routes.StatusCode);
    }
}
