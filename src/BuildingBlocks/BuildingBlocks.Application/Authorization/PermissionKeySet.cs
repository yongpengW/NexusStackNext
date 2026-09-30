using NexusStackNext.BuildingBlocks.Domain.Authorization;

namespace NexusStackNext.BuildingBlocks.Application.Authorization;

/// <summary>
/// 用户被授予的权限键集合。
/// <para>
/// 它就是鉴权热路径上被查找的东西：登录或角色变更时算好，之后每次请求只做一次哈希查找。
/// 参照仓库把这一步做对了（<c>UserContextCacheService.cs:96-119</c>），
/// 是本项目保留清单里的第一项。
/// </para>
/// </summary>
public sealed class PermissionKeySet
{
    private readonly HashSet<PermissionKey> _keys;

    private PermissionKeySet(HashSet<PermissionKey> keys) => _keys = keys;

    /// <summary>空集合。</summary>
    public static PermissionKeySet Empty { get; } = new([]);

    /// <summary>集合大小。</summary>
    public int Count => _keys.Count;

    /// <summary>由权限键构造。重复项自动去重。</summary>
    /// <param name="keys">权限键。</param>
    /// <returns>集合。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="keys"/> 为 <c>null</c>。</exception>
    public static PermissionKeySet From(IEnumerable<PermissionKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return new PermissionKeySet([.. keys]);
    }

    /// <summary>由原始字符串构造（用于从缓存/消息里读回的键）。无法解析的项被忽略。</summary>
    /// <param name="rawKeys">原始键。</param>
    /// <returns>集合。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rawKeys"/> 为 <c>null</c>。</exception>
    public static PermissionKeySet FromRaw(IEnumerable<string> rawKeys)
    {
        ArgumentNullException.ThrowIfNull(rawKeys);

        var set = new HashSet<PermissionKey>();
        foreach (var raw in rawKeys)
        {
            if (PermissionKey.TryParse(raw, out var key))
            {
                set.Add(key);
            }
        }

        return new PermissionKeySet(set);
    }

    /// <summary>是否包含该权限键。</summary>
    /// <param name="key">权限键。</param>
    /// <returns>是否包含。</returns>
    public bool Contains(PermissionKey key) => _keys.Contains(key);

    /// <summary>是否包含该原始键。</summary>
    /// <param name="rawKey">原始键。</param>
    /// <returns>是否包含；无法解析时为 <c>false</c>。</returns>
    public bool Contains(string? rawKey) => PermissionKey.TryParse(rawKey, out var key) && _keys.Contains(key);

    /// <summary>全部键的字符串表示，有序，便于日志与断言。</summary>
    /// <returns>权限键字符串。</returns>
    public IReadOnlyList<string> Values =>
        [.. _keys.Select(static key => key.Value).Order(StringComparer.Ordinal)];
}
