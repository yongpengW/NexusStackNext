using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Identity.Domain.Events;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.ValueObjects;

namespace NexusStackNext.Identity.Domain.Users;

/// <summary>登录失败锁定策略。</summary>
/// <param name="Threshold">连续失败多少次后锁定。</param>
/// <param name="Duration">锁定时长。</param>
public sealed record LockoutPolicy(int Threshold, TimeSpan Duration)
{
    /// <summary>默认策略：连续 5 次失败锁定 15 分钟。</summary>
    public static LockoutPolicy Default { get; } = new(5, TimeSpan.FromMinutes(15));

    /// <summary>校验策略是否可用。</summary>
    /// <param name="policy">策略。</param>
    /// <returns>策略本身，便于链式使用。</returns>
    /// <exception cref="ArgumentOutOfRangeException">阈值不是正数，或时长不为正。</exception>
    public static LockoutPolicy Validate(LockoutPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (policy.Threshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), policy.Threshold, "锁定阈值必须是正数。");
        }

        return policy.Duration <= TimeSpan.Zero
            ? throw new ArgumentOutOfRangeException(nameof(policy), policy.Duration, "锁定时长必须为正。")
            : policy;
    }
}

/// <summary>
/// 用户聚合根。
/// <para>
/// <b>不变量住在里面，不漏到服务层。</b>参照仓库把"密码哈希"散在 <c>UserService</c> 里，
/// 任何调用方都能绕过去改一个字段。这里密码只能经 <see cref="ChangePassword"/> 改变，
/// 且每次变更都会记录领域事件。
/// </para>
/// <para>
/// <b>不带时钟。</b>所有改变状态的方法都要求调用方传入发生时刻——
/// 领域层不依赖 <c>IClock</c>，于是"这次登录发生在什么时候"在测试里完全可控。
/// </para>
/// </summary>
public sealed class User : AggregateRoot<UserId>
{
    private readonly HashSet<RoleId> _roleIds = [];

    private User(UserId id, UserName userName, PasswordHash passwordHash, bool isBuiltIn)
        : base(id)
    {
        UserName = userName;
        PasswordHash = passwordHash;
        IsBuiltIn = isBuiltIn;
        IsEnabled = true;
    }

    /// <summary>用户名，创建后不可变。</summary>
    public UserName UserName { get; }

    /// <summary>密码哈希。</summary>
    public PasswordHash PasswordHash { get; private set; }

    /// <summary>邮箱。</summary>
    public EmailAddress? Email { get; private set; }

    /// <summary>手机号。</summary>
    public PhoneNumber? Phone { get; private set; }

    /// <summary>是否启用。</summary>
    public bool IsEnabled { get; private set; }

    /// <summary>是否内置账号（平台的根管理员）。</summary>
    public bool IsBuiltIn { get; }

    /// <summary>会话撤销版本；旧版本的访问凭据不再有效。</summary>
    public long SessionVersion { get; private set; }

    /// <summary>撤销此前签发的会话，改变聚合的可观察安全状态。</summary>
    public void RevokeSessions()
    {
        SessionVersion = checked(SessionVersion + 1);
        BumpVersion();
    }

    /// <summary>连续登录失败次数。</summary>
    public int FailedLoginCount { get; private set; }

    /// <summary>锁定到期时间；未锁定为 <c>null</c>。</summary>
    public DateTimeOffset? LockedUntil { get; private set; }

    /// <summary>最后一次登录成功时间。</summary>
    public DateTimeOffset? LastLoginAt { get; private set; }

    /// <summary>
    /// 已分配的角色标识（有序，便于比较与断言）。
    /// <para>
    /// <b>角色归属住在用户聚合里</b>，不单独成一个聚合。理由与代价见
    /// <c>docs/adr/0002-user-owns-its-role-assignments.md</c>。
    /// </para>
    /// </summary>
    public IReadOnlyList<RoleId> RoleIds => [.. _roleIds.OrderBy(static id => id.Value)];

    /// <summary>
    /// 分配一个角色。
    /// <para>已经分配时是**空操作，不发事件**——权限缓存的失效路径应当只在集合真的变了时被触发。
    /// 参照仓库的失效窗口最长有 10 小时，无谓的失效会让它更难推理。</para>
    /// </summary>
    /// <param name="roleId">角色标识。</param>
    /// <param name="at">操作时刻。</param>
    /// <returns>成功。</returns>
    public Result AssignRole(RoleId roleId, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(roleId);

        return _roleIds.Add(roleId) ? RaiseRolesChanged(at) : Result.Success();
    }

    /// <summary>撤销一个角色。未分配时是空操作，不发事件。</summary>
    /// <param name="roleId">角色标识。</param>
    /// <param name="at">操作时刻。</param>
    /// <returns>成功。</returns>
    public Result RevokeRole(RoleId roleId, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(roleId);

        return _roleIds.Remove(roleId) ? RaiseRolesChanged(at) : Result.Success();
    }

    private Result RaiseRolesChanged(DateTimeOffset at)
    {
        Raise(new UserRolesChanged(Id, _roleIds.Count, at));
        return Changed();
    }

    /// <summary>注册一个用户。</summary>
    /// <param name="id">标识，由调用方提供。</param>
    /// <param name="userName">用户名。</param>
    /// <param name="passwordHash">密码哈希——<b>明文永远不进领域</b>。</param>
    /// <param name="registeredAt">注册时刻。</param>
    /// <param name="isBuiltIn">是否内置账号。</param>
    /// <returns>新用户聚合。</returns>
    public static User Register(
        UserId id,
        UserName userName,
        PasswordHash passwordHash,
        DateTimeOffset registeredAt,
        bool isBuiltIn = false)
    {
        ArgumentNullException.ThrowIfNull(userName);
        ArgumentNullException.ThrowIfNull(passwordHash);

        var user = new User(id, userName, passwordHash, isBuiltIn);
        user.Raise(new UserRegistered(id, userName.Value, registeredAt));
        return user;
    }

    /// <summary>修改密码。</summary>
    /// <param name="newPasswordHash">新密码哈希。</param>
    /// <param name="changedAt">变更时刻。</param>
    /// <returns>成功，或"与当前密码相同"。</returns>
    public Result ChangePassword(PasswordHash newPasswordHash, DateTimeOffset changedAt)
    {
        ArgumentNullException.ThrowIfNull(newPasswordHash);

        if (newPasswordHash == PasswordHash)
        {
            return Result.Failure(IdentityErrors.PasswordUnchanged());
        }

        PasswordHash = newPasswordHash;
        Raise(new UserPasswordChanged(Id, changedAt));
        return Changed();
    }

    /// <summary>设置联系方式。</summary>
    /// <param name="email">邮箱。</param>
    /// <param name="phone">手机号。</param>
    public void SetContact(EmailAddress? email, PhoneNumber? phone)
    {
        // 传入相同的值不是改变。空操作自增会让乐观并发在没有冲突时误报冲突。
        if (Equals(Email, email) && Equals(Phone, phone))
        {
            return;
        }

        Email = email;
        Phone = phone;
        BumpVersion();
    }

    /// <summary>禁用账号。<b>内置账号不允许禁用</b>——那会把所有人锁在门外。</summary>
    /// <param name="at">操作时刻。</param>
    /// <returns>成功，或内置账号受保护。</returns>
    public Result Disable(DateTimeOffset at)
    {
        if (IsBuiltIn)
        {
            return Result.Failure(IdentityErrors.BuiltInAccountProtected("禁用"));
        }

        if (!IsEnabled)
        {
            return Result.Success();
        }

        IsEnabled = false;
        Raise(new UserDisabled(Id, at));
        return Changed();
    }

    /// <summary>启用账号。幂等：已启用时不重复发事件。</summary>
    /// <param name="at">操作时刻。</param>
    /// <returns>成功。</returns>
    public Result Enable(DateTimeOffset at)
    {
        if (IsEnabled)
        {
            return Result.Success();
        }

        IsEnabled = true;
        LockedUntil = null;
        FailedLoginCount = 0;
        Raise(new UserEnabled(Id, at));
        return Changed();
    }

    /// <summary>判断此刻能否登录。</summary>
    /// <param name="now">当前时刻。</param>
    /// <returns>成功，或账号被禁用/锁定。</returns>
    public Result EnsureCanAuthenticate(DateTimeOffset now)
    {
        if (!IsEnabled)
        {
            return Result.Failure(IdentityErrors.UserDisabled());
        }

        return LockedUntil is { } until && until > now
            ? Result.Failure(IdentityErrors.UserLocked(until))
            : Result.Success();
    }

    /// <summary>记录一次登录成功：清零失败计数并解除锁定。</summary>
    /// <param name="at">登录时刻。</param>
    public void RecordSuccessfulLogin(DateTimeOffset at)
    {
        FailedLoginCount = 0;
        LockedUntil = null;
        LastLoginAt = at;
        Raise(new UserLoggedIn(Id, at));
        BumpVersion();
    }

    /// <summary>记录一次登录失败；达到阈值即锁定。</summary>
    /// <param name="at">失败时刻。</param>
    /// <param name="policy">锁定策略。</param>
    public void RecordFailedLogin(DateTimeOffset at, LockoutPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        FailedLoginCount++;
        if (FailedLoginCount >= policy.Threshold)
        {
            LockedUntil = at + policy.Duration;
        }

        Raise(new LoginFailed(Id, FailedLoginCount, LockedUntil, at));
        BumpVersion();
    }
}
