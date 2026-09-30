using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Menus;
using NexusStackNext.Identity.Domain.Roles;
using NexusStackNext.Identity.Domain.ValueObjects;

namespace NexusStackNext.Identity.Infrastructure.Persistence;

/// <summary>
/// Identity 的强类型 ID 与值对象 → 数据库列的转换器。
///
/// <para><b>为什么是"转换器类"而不是一堆 <c>ValueConverter</c> 字段。</b>
/// 字段形式只能逐属性 <c>HasConversion(字段)</c>，而那个重载要求泛型**精确匹配**——
/// 于是可空属性（<c>MenuId?</c>、<c>EmailAddress?</c>）传非可空的转换器会被拒绝
/// （<c>CS8620</c>）。转换器类配合 <c>ConfigureConventions</c> 就没有这个问题：
/// 为 <c>T</c> 注册的约定**同时覆盖 <c>T?</c>**，空值由 EF 自己处理。</para>
///
/// <para><b>为什么必须显式写这些。</b>值对象的构造函数是 <c>private</c>，
/// 只能经静态 <c>Create</c> 拿到 <c>Result&lt;T&gt;</c>——EF 无法凭空造出一个实例。
/// 转换器把这个"从裸值重建"的动作写在一处，于是领域类型可以继续保持
/// "只能被合法地构造"这条性质。</para>
///
/// <para><b><c>Create(...).Value</c> 失败时抛异常，这是有意的。</b>
/// 它能走到失败只有一种可能：**库里的值不满足领域不变量**（有人手工改过库、
/// 或某个旧版本写入过别的格式）。那时"读出来一个半成品对象"远比抛异常危险——
/// 后续的领域方法会在一个不可能存在的状态上做判断。抛出的消息里带着领域自己写的那句
/// （"用户名长度必须在 3..32 之间"），比 <c>InvalidCastException</c> 有用得多。</para>
///
/// <para><b>它们住在 Infrastructure 而不是 Domain。</b>领域层不认识 EF，
/// 也不该认识"列该怎么存"——那是适配器的事（不变量 3）。</para>
/// </summary>
internal static class IdentityValueConverters
{
    /// <summary>把本上下文所有的转换器注册成 EF 约定。</summary>
    /// <param name="configurationBuilder">约定构建器。</param>
    public static void Apply(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        configurationBuilder.Properties<UserId>().HaveConversion<UserIdConverter>();
        configurationBuilder.Properties<RoleId>().HaveConversion<RoleIdConverter>();
        configurationBuilder.Properties<MenuId>().HaveConversion<MenuIdConverter>();
        configurationBuilder.Properties<MenuTreeId>().HaveConversion<MenuTreeIdConverter>();
        configurationBuilder.Properties<ApiResourceId>().HaveConversion<ApiResourceIdConverter>();
        configurationBuilder.Properties<RefreshTokenId>().HaveConversion<RefreshTokenIdConverter>();

        configurationBuilder.Properties<UserName>().HaveConversion<UserNameConverter>();
        configurationBuilder.Properties<EmailAddress>().HaveConversion<EmailAddressConverter>();
        configurationBuilder.Properties<PhoneNumber>().HaveConversion<PhoneNumberConverter>();
        configurationBuilder.Properties<PasswordHash>().HaveConversion<PasswordHashConverter>();
        configurationBuilder.Properties<TokenHash>().HaveConversion<TokenHashConverter>();
        configurationBuilder.Properties<RoleCode>().HaveConversion<RoleCodeConverter>();
        configurationBuilder.Properties<RoleName>().HaveConversion<RoleNameConverter>();
        configurationBuilder.Properties<RoutePattern>().HaveConversion<RoutePatternConverter>();
        configurationBuilder.Properties<MenuPath>().HaveConversion<MenuPathConverter>();
        configurationBuilder.Properties<MenuTitle>().HaveConversion<MenuTitleConverter>();
    }

    /// <summary>
    /// 读出来是空字符串时的统一处置。
    ///
    /// <para>值对象的 <c>Create</c> 对空值本来就会返回失败，于是 <c>.Value</c> 抛一句
    /// 泛泛的"失败的结果没有值"。这里先拦一道，让错误直接指向真正的问题：
    /// **该列本应非空，却读到了空**。</para>
    /// </summary>
    /// <param name="value">从库里读到的值。</param>
    /// <returns>非空的值。</returns>
    internal static string Require(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException("数据库里出现了空字符串——该列本应非空。")
            : value;
}

/// <summary>用户标识 → <c>bigint</c>。</summary>
internal sealed class UserIdConverter : ValueConverter<UserId, long>
{
    /// <summary>创建转换器。</summary>
    public UserIdConverter()
        : base(id => id.Value, value => new UserId(value))
    {
    }
}

/// <summary>角色标识 → <c>bigint</c>。</summary>
internal sealed class RoleIdConverter : ValueConverter<RoleId, long>
{
    /// <summary>创建转换器。</summary>
    public RoleIdConverter()
        : base(id => id.Value, value => new RoleId(value))
    {
    }
}

/// <summary>菜单标识 → <c>bigint</c>。</summary>
internal sealed class MenuIdConverter : ValueConverter<MenuId, long>
{
    /// <summary>创建转换器。</summary>
    public MenuIdConverter()
        : base(id => id.Value, value => new MenuId(value))
    {
    }
}

/// <summary>菜单树标识 → <c>bigint</c>。</summary>
internal sealed class MenuTreeIdConverter : ValueConverter<MenuTreeId, long>
{
    /// <summary>创建转换器。</summary>
    public MenuTreeIdConverter()
        : base(id => id.Value, value => new MenuTreeId(value))
    {
    }
}

/// <summary>API 资源标识 → <c>bigint</c>。</summary>
internal sealed class ApiResourceIdConverter : ValueConverter<ApiResourceId, long>
{
    /// <summary>创建转换器。</summary>
    public ApiResourceIdConverter()
        : base(id => id.Value, value => new ApiResourceId(value))
    {
    }
}

/// <summary>刷新令牌标识 → <c>bigint</c>。</summary>
internal sealed class RefreshTokenIdConverter : ValueConverter<RefreshTokenId, long>
{
    /// <summary>创建转换器。</summary>
    public RefreshTokenIdConverter()
        : base(id => id.Value, value => new RefreshTokenId(value))
    {
    }
}

/// <summary>用户名 → <c>varchar</c>。</summary>
internal sealed class UserNameConverter : ValueConverter<UserName, string>
{
    /// <summary>创建转换器。</summary>
    public UserNameConverter()
        : base(name => name.Value, value => UserName.Create(IdentityValueConverters.Require(value)).Value)
    {
    }
}

/// <summary>邮箱 → <c>varchar</c>。</summary>
internal sealed class EmailAddressConverter : ValueConverter<EmailAddress, string>
{
    /// <summary>创建转换器。</summary>
    public EmailAddressConverter()
        : base(email => email.Value, value => EmailAddress.Create(IdentityValueConverters.Require(value)).Value)
    {
    }
}

/// <summary>手机号 → <c>varchar</c>。</summary>
internal sealed class PhoneNumberConverter : ValueConverter<PhoneNumber, string>
{
    /// <summary>创建转换器。</summary>
    public PhoneNumberConverter()
        : base(phone => phone.Value, value => PhoneNumber.Create(IdentityValueConverters.Require(value)).Value)
    {
    }
}

/// <summary>密码哈希 → <c>text</c>。</summary>
internal sealed class PasswordHashConverter : ValueConverter<PasswordHash, string>
{
    /// <summary>创建转换器。</summary>
    public PasswordHashConverter()
        : base(hash => hash.Encoded, value => PasswordHash.Create(IdentityValueConverters.Require(value)).Value)
    {
    }
}

/// <summary>令牌哈希 → <c>text</c>。</summary>
internal sealed class TokenHashConverter : ValueConverter<TokenHash, string>
{
    /// <summary>创建转换器。</summary>
    public TokenHashConverter()
        : base(hash => hash.Encoded, value => TokenHash.Create(IdentityValueConverters.Require(value)).Value)
    {
    }
}

/// <summary>角色编码 → <c>varchar(64)</c>。</summary>
internal sealed class RoleCodeConverter : ValueConverter<RoleCode, string>
{
    /// <summary>创建转换器。</summary>
    public RoleCodeConverter()
        : base(code => code.Value, value => RoleCode.Create(IdentityValueConverters.Require(value)).Value)
    {
    }
}

/// <summary>角色名 → <c>varchar(64)</c>。</summary>
internal sealed class RoleNameConverter : ValueConverter<RoleName, string>
{
    /// <summary>创建转换器。</summary>
    public RoleNameConverter()
        : base(name => name.Value, value => RoleName.Create(IdentityValueConverters.Require(value)).Value)
    {
    }
}

/// <summary>
/// 路由模板 → <c>varchar(256)</c>。
/// <para>它已经归一化过（小写、去尾斜杠），所以**唯一索引**才真的能挡住重复路由。</para>
/// </summary>
internal sealed class RoutePatternConverter : ValueConverter<RoutePattern, string>
{
    /// <summary>创建转换器。</summary>
    public RoutePatternConverter()
        : base(pattern => pattern.Value, value => RoutePattern.Create(IdentityValueConverters.Require(value)).Value)
    {
    }
}

/// <summary>
/// 菜单路径 → <c>text</c>，形如 <c>/1/7/23/</c>。
///
/// <para>存成路径字符串而不是数组：菜单树要回答的是"谁是它的祖先"
/// （<c>IsAncestorOf</c>），而那是一个**前缀**判断——字符串前缀在 SQL 里可以直接用
/// <c>LIKE</c> 表达，数组不行。代价是它依赖写死的分隔符，所以
/// <c>MenuPath.ToSequenceString</c> 与这里的解析必须**成对**维护。</para>
/// </summary>
internal sealed class MenuPathConverter : ValueConverter<MenuPath, string>
{
    /// <summary>创建转换器。</summary>
    public MenuPathConverter()
        : base(path => path.ToSequenceString(), value => Parse(value))
    {
    }

    private static MenuPath Parse(string value)
    {
        var segments = IdentityValueConverters.Require(value)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(static part => long.Parse(part, CultureInfo.InvariantCulture))
            .ToArray();

        return MenuPath.From(segments);
    }
}

/// <summary>菜单标题 → <c>varchar(64)</c>。</summary>
internal sealed class MenuTitleConverter : ValueConverter<MenuTitle, string>
{
    /// <summary>创建转换器。</summary>
    public MenuTitleConverter()
        : base(title => title.Value, value => MenuTitle.Create(IdentityValueConverters.Require(value)).Value)
    {
    }
}
