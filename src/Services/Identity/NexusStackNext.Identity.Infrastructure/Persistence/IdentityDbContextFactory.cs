using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;

namespace NexusStackNext.Identity.Infrastructure.Persistence;

/// <summary>
/// 供 <c>dotnet ef</c> 使用的设计时工厂。
///
/// <para><b>为什么需要它。</b>迁移命令要在**不启动宿主**的情况下拿到上下文——
/// 启动宿主意味着要连配置中心、要满足一堆运行时依赖，而生成迁移只需要**模型**。
/// 这个工厂把两者的耦合切断：它给一个占位连接串，从不真的连库。</para>
///
/// <para><b>连接串是占位的，这不是偷懒。</b><c>migrations add</c> 只读模型、
/// <c>migrations script</c> 只读模型，两者都不碰数据库；<c>database update</c> 才需要真连接串，
/// 本仓部署使用宿主的 <c>migrate-identity</c> 命令，从明确的环境变量读取连接配置，
/// 不把凭据放入命令参数；本工厂仅用于生成迁移与 SQL。</para>
/// </summary>
internal sealed class IdentityDbContextFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    /// <inheritdoc />
    public IdentityDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<IdentityDbContext>();

        // 只为让 provider 能构建模型；迁移命令不会用它连库。
        //
        // **刻意不带用户名与口令**：迁移只读模型，从不连库，所以那两项在这里没有用途；
        // 而写上任何一个口令字段的字面量，都会让 `assert-no-credentials.ps1`
        // 的"连接串口令"规则命中——那条规则分不出"占位符"与"真口令"，也不该去分。
        builder.UseNexusStackPostgres(
            "Host=design-time;Database=design-time",
            IdentityDbContext.SchemaName);

        return new IdentityDbContext(builder.Options);
    }
}
