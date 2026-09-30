using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Transactions;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Identity.Domain.Users;
using NexusStackNext.Identity.Domain.ValueObjects;

namespace NexusStackNext.Identity.Application;

/// <summary>
/// 登录。
///
/// <para><b>它修的是参照仓库的三处缺陷</b>（review/01）：无锁定、泄露用户名存在性、
/// 以及一个写了却**零调用**的验证码校验。</para>
/// </summary>
/// <param name="UserName">用户名。</param>
/// <param name="Password">明文口令。</param>
/// <param name="Captcha">验证码答案；没有启用验证码时为 <c>null</c>。</param>
public sealed record LoginCommand(string UserName, string Password, string? Captcha = null)
    : ICommand<LoginOutcome>;

/// <summary>登录成功的结果。</summary>
/// <param name="UserId">用户标识。</param>
/// <param name="UserName">用户名，供响应回显。</param>
/// <param name="Tokens">一对令牌。<b>刷新令牌的原文只在这里出现一次。</b></param>
public sealed record LoginOutcome(long UserId, string UserName, TokenPair Tokens);

/// <summary>
/// 验证码校验。
///
/// <para><b>为什么它是一个端口而不是一段代码。</b>参照仓库写了 <c>ValidateCaptchaAsync</c>
/// 却**从不调用**它——一个没人调用的校验与没有校验，在安全上是同一件事，
/// 但在代码评审上是两件事（后者看得见）。做成端口之后，"当前用的是哪个实现"
/// 在组合根上一眼可见。</para>
///
/// <para><b>默认实现刻意叫 <see cref="NoCaptchaValidation"/>。</b>名字本身就是警告：
/// 生产环境必须换成真的。一个叫 <c>DefaultCaptchaValidator</c> 的恒真实现
/// 会让人以为"默认就是安全的"。</para>
/// </summary>
public interface ICaptchaValidation
{
    /// <summary>校验验证码。</summary>
    /// <param name="answer">用户提交的答案。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功，或失败原因。</returns>
    Task<Result> ValidateAsync(string? answer, CancellationToken cancellationToken = default);
}

/// <summary>
/// **不做任何校验**的验证码实现。
///
/// <para>它存在的唯一理由是本地开发与测试不该需要一套验证码服务。
/// 名字里的 "No" 是有意的——<b>生产环境用它等于没有验证码</b>。</para>
/// </summary>
public sealed class NoCaptchaValidation : ICaptchaValidation
{
    /// <inheritdoc />
    public Task<Result> ValidateAsync(string? answer, CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Success());
}

/// <summary>
/// 登录处理器。
///
/// <para><b>顺序是有讲究的：先验口令，再报"锁定/禁用"。</b></para>
///
/// <para>用户不存在与口令错误返回**同一个**错误——否则接口就成了一个用户名枚举器。
/// 而"账号已锁定"如果先于口令校验返回，同样会泄露存在性：攻击者提交任意口令，
/// 看到"已锁定"就知道这个账号是真的。<b>所以锁定只在口令正确之后才说</b>——
/// 那时对方已经证明了他是账号的主人（或已经拿到了口令），告诉他有意义；
/// 而不知道口令的人无论账号存不存在，看到的都是同一个"用户名或密码错误"。</para>
///
/// <para><b>用户不存在时也做一次哈希校验。</b>否则"不存在"会明显更快——
/// 而那是一个**计时侧信道**：几万次请求的耗时统计足以把用户名枚举出来。
/// 用一次固定的哈希计算把这个差异抹平。</para>
/// </summary>
/// <param name="users">用户仓储。</param>
/// <param name="hasher">口令哈希与校验。</param>
/// <param name="unitOfWork">工作单元——**失败也要保存**，见下。</param>
/// <param name="tokens">令牌签发。</param>
/// <param name="captcha">验证码校验。</param>
/// <param name="clock">时钟。</param>
public sealed class LoginHandler(
    IUserRepository users,
    IPasswordHasher hasher,
    ICaptchaValidation captcha,
    IClock clock,
    IUnitOfWork unitOfWork,
    TokenIssuer tokens) : ICommandHandler<LoginCommand, LoginOutcome>
{
    /// <summary>口令错误与用户不存在共用的错误——**一个字都不能差**。</summary>
    public static readonly Error InvalidCredentials =
        new("identity.credentials.invalid", "用户名或密码错误。");

    /// <summary>时间侧信道用的固定哈希：一个格式合法但永远不会匹配任何口令的值。</summary>
    private static readonly string TimingDecoy =
        $"pbkdf2-sha256.210000.{Convert.ToBase64String(new byte[16])}.{Convert.ToBase64String(new byte[32])}";

    /// <inheritdoc />
    public async Task<Result<LoginOutcome>> HandleAsync(
        LoginCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var captchaResult = await captcha.ValidateAsync(command.Captcha, cancellationToken).ConfigureAwait(false);
        if (captchaResult.IsFailure)
        {
            return Result.Failure<LoginOutcome>(captchaResult.Error);
        }

        var userName = UserName.Create(command.UserName);
        if (userName.IsFailure)
        {
            // **连"用户名格式不对"也不能单独报。** 否则攻击者可以靠格式规则
            // 反推哪些名字是合法用户名——而规则本身是公开的，于是这一步只泄露、
         	// 不保护任何东西。
            return Result.Failure<LoginOutcome>(InvalidCredentials);
        }

        var user = await users.FindByUserNameAsync(userName.Value, cancellationToken).ConfigureAwait(false);

        if (user is null)
        {
            // 抹平计时差异——见类文档。
            _ = hasher.Verify(command.Password, TimingDecoy);
            return Result.Failure<LoginOutcome>(InvalidCredentials);
        }

        if (!hasher.Verify(command.Password, user.PasswordHash.Encoded))
        {
            // 口令错：记一次失败（可能因此锁定），但仍然返回**统一**的错误。
            // 即使这一次刚好触发了锁定，也不说——那同样会泄露"这个账号存在"。
            user.RecordFailedLogin(clock.UtcNow, LockoutPolicy.Default);
            // **失败也要保存。**
            //
            // 分发器只在处理器**成功**时保存（见 `Sender`），而"登录失败"本身就是一次状态变更：
            // 失败计数加一，够阈值就锁定。不保存的话，锁定**永远不会生效**——
            // 每一次失败都被安静地丢掉，而接口照常返回"用户名或密码错误"。
            //
            // 这里仍然在分发器开的事务里（`ExecuteInTransactionAsync` 包着整个处理器），
            // 所以"一个命令一个事务"没有被破坏——只是"提交"这个动作由处理器自己发起。
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Result.Failure<LoginOutcome>(InvalidCredentials);
        }

        // 口令对了，这时才谈"锁定/禁用"。
        var allowed = user.EnsureCanAuthenticate(clock.UtcNow);
        if (allowed.IsFailure)
        {
            return Result.Failure<LoginOutcome>(allowed.Error);
        }

        user.RecordSuccessfulLogin(clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // 登录成功才签发令牌。放在最后是有意的：上面任何一条失败路径都不该产出令牌。
        var pair = await tokens.IssueAsync(user, cancellationToken).ConfigureAwait(false);
        if (pair.IsFailure)
        {
            return Result.Failure<LoginOutcome>(pair.Error);
        }

        return Result.Success(new LoginOutcome(user.Id.Value, user.UserName.Value, pair.Value));
    }
}
