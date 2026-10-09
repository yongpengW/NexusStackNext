namespace NexusStackNext.BuildingBlocks.Domain.Authorization;

/// <summary>
/// 权限键：授权判定的最小单位，形如 <c>/api/users/{id}:GET</c>。
/// <para>
/// <b>在构造时归一化，因此集合比较不需要自定义比较器。</b>参照仓库依赖
/// <c>StringComparer.OrdinalIgnoreCase</c>（<c>UserContextCacheService.cs:117</c>）——
/// 那是对的，但只要有一处忘了传比较器，就会退化成大小写敏感，表现为"某些权限静默失效"。
/// 把归一化收进类型，这种遗忘就不存在了。
/// </para>
/// <para>
/// 格式沿用参照仓库的路由模板与方法约定；当前请求的许可由 Identity 权威读取裁决，
/// 预计算集合仅用于权限诊断，不作为准入来源。
/// </para>
/// </summary>
public readonly record struct PermissionKey
{
    private PermissionKey(string value) => Value = value;

    /// <summary>归一化后的键：路由模板小写、方法大写、<c>:</c> 分隔。</summary>
    public string Value { get; }

    /// <summary>由路由模板与 HTTP 方法构造。</summary>
    /// <param name="routeTemplate">路由模板，可带或不带前导斜杠。</param>
    /// <param name="httpMethod">HTTP 方法。</param>
    /// <returns>归一化后的权限键。</returns>
    /// <exception cref="ArgumentException">任一参数为空。</exception>
    public static PermissionKey From(string routeTemplate, string httpMethod)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeTemplate);
        ArgumentException.ThrowIfNullOrWhiteSpace(httpMethod);

        var route = routeTemplate.Trim().ToLowerInvariant();
        if (!route.StartsWith('/'))
        {
            route = "/" + route;
        }

        if (route.Length > 1 && route.EndsWith('/'))
        {
            route = route.TrimEnd('/');
        }

        return new PermissionKey($"{route}:{httpMethod.Trim().ToUpperInvariant()}");
    }

    /// <summary>
    /// 解析一个已归一化或未归一化的键。
    /// <para><b>按最后一个 <c>:</c> 切分</b>——路由模板里可以出现冒号
    /// （ASP.NET 的约束写法 <c>/api/items/{id:int}</c>），按第一个冒号切会切错。</para>
    /// </summary>
    /// <param name="raw">原始键。</param>
    /// <param name="key">解析结果。</param>
    /// <returns>是否解析成功。</returns>
    public static bool TryParse(string? raw, out PermissionKey key)
    {
        key = default;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var separator = raw.LastIndexOf(':');
        if (separator <= 0 || separator == raw.Length - 1)
        {
            return false;
        }

        var route = raw[..separator];
        var method = raw[(separator + 1)..];
        if (string.IsNullOrWhiteSpace(route) || string.IsNullOrWhiteSpace(method)) { return false; }
        key = From(route, method);
        return true;
    }

    /// <summary>判断两个键是否相同。</summary>
    /// <param name="other">另一个键。</param>
    /// <returns>是否相同。</returns>
    public bool Equals(PermissionKey other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => Value?.GetHashCode(StringComparison.Ordinal) ?? 0;

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}
