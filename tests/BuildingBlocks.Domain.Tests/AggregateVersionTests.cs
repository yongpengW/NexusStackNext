using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.BuildingBlocks.Domain.Tests;

/// <summary>
/// 乐观并发版本号（ADR-0011）。
/// <para>
/// <b>契约：Version 改变，当且仅当可观察状态改变了。</b>
/// 空操作若自增，乐观并发会在**没有冲突的情况下**误报冲突——
/// 而误报的代价是调用方开始重试或干脆忽略冲突，那时这个机制就废了，且废得很安静。
/// </para>
/// <para>
/// 这条契约**编译器管不了**：忘记自增不会有任何报错。所以它只能靠测试守。
/// </para>
/// </summary>
public sealed class AggregateVersionTests
{
    [Fact]
    public void NewAggregate_StartsAtVersionOne()
    {
        Assert.Equal(1, Account.Open(new AccountId(1)).Version);
    }

    [Fact]
    public void ChangedState_BumpsVersion()
    {
        var account = Account.Open(new AccountId(1));

        Assert.True(account.Deposit(10).IsSuccess);

        Assert.Equal(2, account.Version);
        Assert.Equal(10, account.Balance);
    }

    [Fact]
    public void FailedOperation_DoesNotBumpVersion()
    {
        // 失败不是状态改变。
        var account = Account.Open(new AccountId(1));

        Assert.True(account.Deposit(-1).IsFailure);
        Assert.Equal(1, account.Version);

        account.Freeze();
        var afterFreeze = account.Version;

        Assert.True(account.Deposit(10).IsFailure);
        Assert.Equal(afterFreeze, account.Version);
    }

    [Fact]
    public void ExplicitNoOp_DoesNotBumpVersion()
    {
        // 提前返回 Result.Success()（而不是 Changed()）就是"这一步没改状态"。
        // 这里用的是一个**真实的**空操作形态：把别名改成它已经是的那个值。
        var account = Account.Open(new AccountId(1));

        Assert.True(account.Rename("主账户").IsSuccess);
        var afterFirstRename = account.Version;

        Assert.True(account.Rename("主账户").IsSuccess);

        Assert.Equal(afterFirstRename, account.Version);
    }

    [Fact]
    public void VoidChangedPath_BumpsVersion()
    {
        var account = Account.Open(new AccountId(1));

        account.Freeze();

        Assert.Equal(2, account.Version);
        Assert.True(account.Frozen);
    }

    [Fact]
    public void RepeatedVoidChange_DoesNotBumpTwice()
    {
        var account = Account.Open(new AccountId(1));
        account.Freeze();

        account.Freeze();

        Assert.Equal(2, account.Version);
    }

    [Fact]
    public void VersionIsNotPartOfIdentityEquality()
    {
        // 标识 ≠ 状态：同一个聚合的"旧快照"与"新快照"在标识上是同一个东西。
        // 并发冲突检测靠比较 Version，而不是靠 Equals——两者必须分开。
        var id = new AccountId(1);
        var stale = Account.Open(id);
        var current = Account.Open(id);
        current.Deposit(10);

        Assert.Equal(stale, current);
        Assert.NotEqual(stale.Version, current.Version);
    }

    /// <summary>测试用聚合：三种改变路径各一个（Result / void / 空操作）。</summary>
    private sealed class Account : AggregateRoot<AccountId>
    {
        private Account(AccountId id)
            : base(id)
        {
        }

        public long Balance { get; private set; }

        public bool Frozen { get; private set; }

        public string? Alias { get; private set; }

        public static Account Open(AccountId id) => new(id);

        public Result Deposit(long amount)
        {
            if (amount <= 0)
            {
                return Result.Failure(new Error("account.amount.invalid", "金额必须为正。"));
            }

            if (Frozen)
            {
                return Result.Failure(new Error("account.frozen", "账户已冻结。"));
            }

            Balance += amount;
            return Changed();
        }

        /// <summary>改名。传入相同的别名是**空操作**——什么都不做，也不该改版本。</summary>
        /// <param name="alias">新别名。</param>
        /// <returns>成功。</returns>
        public Result Rename(string alias)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(alias);

            if (string.Equals(Alias, alias, StringComparison.Ordinal))
            {
                return Result.Success();
            }

            Alias = alias;
            return Changed();
        }

        public void Freeze()
        {
            if (Frozen)
            {
                return;
            }

            Frozen = true;
            BumpVersion();
        }
    }

    private sealed record AccountId : StronglyTypedId<long>
    {
        public AccountId(long value)
            : base(value)
        {
        }
    }
}
