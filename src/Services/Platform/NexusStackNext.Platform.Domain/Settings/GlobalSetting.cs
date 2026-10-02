using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Platform.Domain.Settings;

/// <summary>配置项标识。</summary>
public sealed record SettingId : StronglyTypedId<long>
{
    /// <summary>由底层值构造。</summary>
    /// <param name="value">底层值。</param>
    public SettingId(long value)
        : base(value)
    {
    }
}

/// <summary>
/// 配置键：<c>分组.名称</c>。
/// <para>把"分组"做成结构而不是字符串前缀，是因为其他上下文按分组读取配置——
/// 前缀匹配会让 <c>identity</c> 意外命中 <c>identity-temp</c>，而按段比较不会。</para>
/// </summary>
public sealed class SettingKey : ValueObject
{
    /// <summary>整键最大长度。</summary>
    public const int MaxLength = 128;

    private SettingKey(string scope, string name)
    {
        Scope = scope;
        Name = name;
    }

    /// <summary>分组，例如 <c>identity</c>。</summary>
    public string Scope { get; }

    /// <summary>名称，例如 <c>token.lifetime</c>。</summary>
    public string Name { get; }

    /// <summary>完整键。</summary>
    public string Value => $"{Scope}.{Name}";

    /// <summary>构造配置键。</summary>
    /// <param name="value">形如 <c>分组.名称</c> 的字符串。</param>
    /// <returns>成功时返回值对象。</returns>
    public static Result<SettingKey> Create(string? value)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return Result.Failure<SettingKey>(new Error("platform.setting_key.empty", "配置键不能为空。"));
        }

        if (trimmed.Length > MaxLength)
        {
            return Result.Failure<SettingKey>(new Error(
                "platform.setting_key.too_long",
                $"配置键不能超过 {MaxLength} 个字符。"));
        }

        var separator = trimmed.IndexOf('.', StringComparison.Ordinal);
        if (separator <= 0 || separator == trimmed.Length - 1)
        {
            return Result.Failure<SettingKey>(new Error(
                "platform.setting_key.format",
                "配置键必须是「分组.名称」的形式。"));
        }

        var allowed = trimmed.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
        return allowed
            ? Result.Success(new SettingKey(trimmed[..separator], trimmed[(separator + 1)..]))
            : Result.Failure<SettingKey>(new Error(
                "platform.setting_key.format",
                "配置键只允许字母、数字、点、短横线与下划线。"));
    }

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Scope;
        yield return Name;
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>配置值发生变化。</summary>
/// <param name="SettingId">配置标识。</param>
/// <param name="Key">配置键。</param>
/// <param name="NewValue">新值；<c>null</c> 表示被清除。</param>
/// <param name="OccurredAt">发生时刻（UTC）。</param>
public sealed record GlobalSettingChanged(SettingId SettingId, string Key, string? NewValue, DateTimeOffset OccurredAt)
    : IDomainEvent
{
    /// <inheritdoc />
    public Guid EventId { get; } = Guid.NewGuid();
}

/// <summary>
/// 全局配置聚合根。
/// <para>
/// Platform 是配置的<b>唯一真相</b>：其他上下文只按 key 读取，不 join 它的表，也不缓存到失去失效能力。
/// 值变化记录为 <see cref="GlobalSettingChanged"/>；领域事实本身不等于跨上下文消费者已收到通知。
/// </para>
/// <para><b>同值写入不发事件</b>：空操作触发缓存失效会让整个系统无谓抖动。</para>
/// </summary>
public sealed class GlobalSetting : AggregateRoot<SettingId>
{
    private GlobalSetting(GlobalSetting source)
        : base(source)
    {
        Key = source.Key;
        Value = source.Value;
        Description = source.Description;
    }

    /// <summary>保留当前状态与版本的独立副本，不携带待发布事实。</summary>
    /// <returns>与后续变更隔离的配置状态。</returns>
    public GlobalSetting Snapshot() => new(this);

    private GlobalSetting(SettingId id, SettingKey key, string? value, string? description)
        : base(id)
    {
        Key = key;
        Value = value;
        Description = description;
    }

    /// <summary>配置键。</summary>
    public SettingKey Key { get; }

    /// <summary>配置值；<c>null</c> 表示未设置。</summary>
    public string? Value { get; private set; }

    /// <summary>说明。</summary>
    public string? Description { get; private set; }

    /// <summary>创建配置项。</summary>
    /// <param name="id">标识。</param>
    /// <param name="key">配置键。</param>
    /// <param name="value">初始值。</param>
    /// <param name="description">说明。</param>
    /// <returns>配置聚合。</returns>
    public static GlobalSetting Create(SettingId id, SettingKey key, string? value = null, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new GlobalSetting(id, key, value, description);
    }

    /// <summary>修改配置值。同值写入是空操作，不发事件。</summary>
    /// <param name="newValue">新值。</param>
    /// <param name="at">变更时刻。</param>
    /// <returns>成功。</returns>
    public Result ChangeValue(string? newValue, DateTimeOffset at)
    {
        if (string.Equals(Value, newValue, StringComparison.Ordinal))
        {
            return Result.Success();
        }

        Value = newValue;
        Raise(new GlobalSettingChanged(Id, Key.Value, newValue, at));
        return Changed();
    }

    /// <summary>修改说明。传入相同的说明不是改变。</summary>
    /// <param name="description">新说明。</param>
    public void Describe(string? description)
    {
        if (string.Equals(Description, description, StringComparison.Ordinal))
        {
            return;
        }

        Description = description;
        BumpVersion();
    }
}
