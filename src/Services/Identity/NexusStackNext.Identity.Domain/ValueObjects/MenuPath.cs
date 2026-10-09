using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Domain.Authorization;
using NexusStackNext.Identity.Domain.Ids;

namespace NexusStackNext.Identity.Domain.ValueObjects;

/// <summary>
/// 菜单的物化路径：从根到本节点的标识序列。
/// <para>
/// <b>用标识序列而不是分隔字符串。</b>参照仓库把路径存成 <c>"/1/5/12/"</c> 这类字符串，
/// 然后用 <c>LIKE '%{parentId}%'</c> 找子节点（<c>MenuService.cs:79</c>）——
/// 这个查询有<b>两个</b>独立的问题：父节点 <c>1</c> 会匹配到 <c>12</c>、<c>21</c> 的路径（结果错），
/// 且前导通配符让索引完全失效（性能差）。按段比较同时解决两者。
/// </para>
/// <para>值对象不可变：任何"修改"都返回新的路径，于是调用方无法就地拼字符串。</para>
/// </summary>
public sealed class MenuPath : ValueObject
{
    private readonly long[] _segments;

    private MenuPath(long[] segments) => _segments = segments;

    /// <summary>根路径：没有任何段。</summary>
    public static MenuPath Root { get; } = new([]);

    /// <summary>路径上的标识，从根到本节点。</summary>
    public IReadOnlyList<long> Segments => _segments;

    /// <summary>深度。根为 0。</summary>
    public int Depth => _segments.Length;

    /// <summary>由标识序列构造。</summary>
    /// <param name="segments">从根到本节点的标识。</param>
    /// <returns>路径。</returns>
    public static MenuPath From(params long[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return new MenuPath([.. segments]);
    }

    /// <summary>在末尾追加一段，得到后代路径。</summary>
    /// <param name="id">子节点标识。</param>
    /// <returns>新路径。</returns>
    public MenuPath Append(MenuId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return new MenuPath([.. _segments, id.Value]);
    }

    /// <summary>判断本路径是否是另一条路径的严格祖先。</summary>
    /// <param name="other">另一条路径。</param>
    /// <returns>是否为严格祖先。</returns>
    public bool IsAncestorOf(MenuPath other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (other.Depth <= Depth)
        {
            return false;
        }

        return _segments.AsSpan().SequenceEqual(other._segments.AsSpan(0, Depth));
    }

    /// <summary>
    /// 把本路径中以 <paramref name="from"/> 为前缀的部分替换成 <paramref name="to"/>。
    /// <para>子树整体搬迁时，用一次调用就能算出每个后代的新路径。</para>
    /// </summary>
    /// <param name="from">原前缀。</param>
    /// <param name="to">新前缀。</param>
    /// <returns>替换后的路径；若本路径不以 <paramref name="from"/> 为前缀（或等于它），返回原路径。</returns>
    public MenuPath Rebase(MenuPath from, MenuPath to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        if (Depth <= from.Depth || !from._segments.AsSpan().SequenceEqual(_segments.AsSpan(0, from.Depth)))
        {
            return this;
        }

        return new MenuPath([.. to._segments, .. _segments[from.Depth..]]);
    }

    /// <summary>段落式字符串表示，例如 <c>/1/5/12/</c>。仅用于展示与诊断，<b>不用于比较</b>。</summary>
    /// <returns>字符串。</returns>
    public string ToSequenceString() =>
        _segments.Length == 0 ? "/" : $"/{string.Join('/', _segments)}/";

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        foreach (var segment in _segments)
        {
            yield return segment;
        }
    }

    /// <inheritdoc />
    public override string ToString() => ToSequenceString();
}

/// <summary>
/// API 路由模板，规范化后用于权限键。
/// <para>
/// 权限键的格式 <c>路由模板:HTTP方法</c> 沿用参照仓库的约定。
/// 当前准入由 Identity 权威读取判定，预计算集合只承担诊断投影。
/// 把它放在值对象里，是为了让"大小写与格式"只有一处定义——散在各处迟早会不一致。
/// </para>
/// </summary>
public sealed class RoutePattern : ValueObject
{
    private RoutePattern(string value) => Value = value;

    /// <summary>规范化后的路由模板（小写、以 <c>/</c> 开头、无尾部斜杠）。</summary>
    public string Value { get; }

    /// <summary>构造路由模板。</summary>
    /// <param name="value">原始模板，例如 <c>/api/Users/{id}</c>。</param>
    /// <returns>成功时返回值对象。</returns>
    public static Result<RoutePattern> Create(string? value)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return Result.Failure<RoutePattern>(new Error("identity.route_pattern.empty", "路由模板不能为空。"));
        }

        if (!trimmed.StartsWith('/'))
        {
            return Result.Failure<RoutePattern>(new Error("identity.route_pattern.format", "路由模板必须以 / 开头。"));
        }

        if (trimmed.Any(char.IsWhiteSpace))
        {
            return Result.Failure<RoutePattern>(new Error("identity.route_pattern.whitespace", "路由模板不能包含空白字符。"));
        }

        var normalized = trimmed.ToLowerInvariant();
        if (normalized.Length > 1 && normalized.EndsWith('/'))
        {
            normalized = normalized.TrimEnd('/');
        }

        return Result.Success(new RoutePattern(normalized));
    }

    /// <summary>
    /// 生成权限键：<c>路由模板:HTTP方法</c>（两者都规范化）。
    /// <para>方法名不叫 <c>PermissionKey</c>——成员名与类型名同名会在成员内部**遮蔽类型名**，
    /// 于是方法体里写 <c>PermissionKey.From(...)</c> 会被解析成递归调用自己。</para>
    /// </summary>
    /// <param name="httpMethod">HTTP 方法。</param>
    /// <returns>权限键。</returns>
    public PermissionKey ToPermissionKey(string httpMethod) =>
        BuildingBlocks.Domain.Authorization.PermissionKey.From(Value, httpMethod);

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}
