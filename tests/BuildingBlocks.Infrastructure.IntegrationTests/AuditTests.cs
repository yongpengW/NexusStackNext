using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.TestSupport;

namespace NexusStackNext.BuildingBlocks.Infrastructure.IntegrationTests;

/// <summary>审计拦截器（ADR-0008 的落点）在真库上验证。</summary>
public sealed class AuditTests
{
    /// <summary>会记下"谁做的"的探针发起者。</summary>
    private sealed class StubCurrentUser(string? userId) : ICurrentUser
    {
        public string? UserId { get; set; } = userId;

        /// <inheritdoc />
        public bool IsRoot => false;

        /// <inheritdoc />
        public long? SessionVersion => null;
    }

    /// <summary>
    /// 创建时写 <c>CreatedAt</c>，修改时写 <c>UpdatedAt</c>，而 <c>CreatedAt</c> **不跟着漂**。
    ///
    /// <para>最后那半句才是这条测试的重点：两个字段都在每次保存时覆盖的实现同样能让
    /// "创建时间有值"，但那个值会在第一次修改后被悄悄改掉——而"创建时间"这个字段
    /// 一旦会漂，它就什么都不是了。</para>
    /// </summary>
    [PostgresFact]
    public async Task AuditFields_AreStampedOnCreate_AndCreationTimeDoesNotDrift()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();

        var clock = new MutableClock(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var user = new StubCurrentUser("leo");

        await using var context = await ProbeDatabase.CreateAsync(database, clock, user);

        // 一、创建。
        var probe = ProbeAggregate.Create(1, "原名");
        context.Probes.Add(probe);
        await context.SaveChangesAsync();

        var createdAt = probe.CreatedAt;
        Assert.Equal(clock.UtcNow, createdAt);
        Assert.Equal("leo", probe.CreatedBy);
        Assert.Null(probe.UpdatedAt);
        Assert.Null(probe.UpdatedBy);

        // 二、修改：**时间往前走**，于是"UpdatedAt 是新值"这件事才是真的被验到。
        clock.Advance(TimeSpan.FromHours(5));
        user.UserId = "someone-else";

        probe.Rename("新名");
        await context.SaveChangesAsync();

        Assert.Equal(clock.UtcNow, probe.UpdatedAt);
        Assert.Equal("someone-else", probe.UpdatedBy);

        // **创建时间与创建者没有漂。**
        Assert.Equal(createdAt, probe.CreatedAt);
        Assert.Equal("leo", probe.CreatedBy);
    }

    /// <summary>
    /// **软删会留下"谁在何时删的"。**
    ///
    /// <para>软删是 <c>Modified</c>（<c>IsDeleted</c> 变了），不是 <c>Deleted</c>——
    /// 所以它照样会被盖审计戳。这一点值得单独断言：一个只处理 <c>Added</c>/<c>Modified</c>
    /// 之外还处理 <c>Deleted</c> 的实现会漏掉它，而硬删在本仓是被软删取代的。</para>
    /// </summary>
    [PostgresFact]
    public async Task SoftDelete_LeavesAnAuditTrail()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();

        var clock = new MutableClock(new DateTimeOffset(2026, 6, 7, 8, 9, 10, TimeSpan.Zero));
        var user = new StubCurrentUser("creator");

        await using var context = await ProbeDatabase.CreateAsync(database, clock, user);

        var probe = ProbeAggregate.Create(1, "将被软删");
        context.Probes.Add(probe);
        await context.SaveChangesAsync();

        clock.Advance(TimeSpan.FromDays(1));
        user.UserId = "deleter";

        probe.SoftDelete();
        await context.SaveChangesAsync();

        Assert.True(probe.IsDeleted);
        Assert.Equal(clock.UtcNow, probe.UpdatedAt);
        Assert.Equal("deleter", probe.UpdatedBy);
        Assert.Equal("creator", probe.CreatedBy);
    }

    /// <summary>系统自身的写入（没有发起者）留空是**事实**，不是缺失。</summary>
    [PostgresFact]
    public async Task AnonymousWriter_LeavesTheActorEmpty()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var context = await ProbeDatabase.CreateAsync(database);

        var probe = ProbeAggregate.Create(1, "系统写入");
        context.Probes.Add(probe);
        await context.SaveChangesAsync();

        Assert.Null(probe.CreatedBy);
        Assert.NotEqual(default, probe.CreatedAt);
    }
}
