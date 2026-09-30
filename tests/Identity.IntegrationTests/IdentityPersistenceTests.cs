using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Users;
using NexusStackNext.Identity.Domain.ValueObjects;
using NexusStackNext.Identity.Infrastructure.Persistence;
using NexusStackNext.IntegrationSupport;
using Npgsql;

namespace NexusStackNext.Identity.IntegrationTests;

/// <summary>
/// Identity 的持久化约定，**对着真实 PostgreSQL 的迁移结果**验证。
///
/// <para><b>为什么这组测试不用每个用例独立 schema。</b>迁移里的 schema 名是**编译进去**的
/// （<c>schema: "identity"</c>），无法按实例参数化——所以"每个测试一个临时 schema"
/// 那套隔离对迁移不适用。这组测试操作的是**那个真实的 <c>identity</c> schema**，
/// 每个用例开始前先把它 <c>DROP … CASCADE</c> 再迁移一遍，以此获得确定性。
/// 它对测试库的破坏性是**有意的**：这正是"迁移可对空库执行成功"要验的东西。</para>
/// </summary>
[Collection(IdentityDatabaseGroup.Name)]
public sealed class IdentityPersistenceTests(IdentityDatabaseFixture fixture)
{
    private const string IdentitySchema = IdentityDbContext.SchemaName;

    /// <summary>固定的注册时刻——测试里不该出现"现在几点"。</summary>
    private static readonly DateTimeOffset RegisteredAt = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static DbContextOptions<IdentityDbContext> Options(PostgresTestDatabase database)
    {
        var builder = new DbContextOptionsBuilder<IdentityDbContext>();
        builder.UseNexusStackPostgres(database.ConnectionString, IdentitySchema);
        return builder.Options;
    }


    private async Task<long> CountAsync(string table)
    {
        await using var connection = await fixture.Database.OpenAsync();
        await using var command = new NpgsqlCommand($"select count(*) from {IdentitySchema}.{table}", connection);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// **迁移可对空库执行成功，且 <c>Id</c> 列不带 IDENTITY**（票据 07 验收 1）。
    ///
    /// <para>对照的是参照仓库 <c>SqlMigration/InitDatabase.sql:9</c> 的反例：
    /// 实体上标了 <c>ValueGeneratedNever</c>，而迁移生成的列却带着自增——
    /// 两个权威并存，序列停在 1，与种子 admin 撞车（ADR-0009）。</para>
    /// </summary>
    [PostgresFact]
    public async Task Migration_AppliesToAnEmptySchema_AndIdHasNoIdentity()
    {
        // fixture 初始化时**先删 schema 再迁一次**——"迁移可对空 schema 执行成功"
        // 就是在那一步验证的，这里读的是它的结果。原先这条测试自己删自己迁，
        // 二十来条用例加起来就是二十来轮建表删表，而那正是把测试服务器磁盘读打满的东西。
        await using var context = fixture.NewContext();

        // 一、迁移真的建了表。
        var tables = new List<string>();
        await using (var connection = await fixture.Database.OpenAsync())
        {
            await using var command = new NpgsqlCommand(
                "select tablename::text from pg_tables where schemaname = @schema order by tablename",
                connection);
            command.Parameters.AddWithValue("schema", IdentitySchema);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
        }

        Assert.Contains("users", tables);
        Assert.Contains("roles", tables);
        Assert.Contains("api_resources", tables);
        Assert.Contains("refresh_tokens", tables);
        Assert.Contains("menu_trees", tables);
        Assert.Contains("menu_nodes", tables);

        // 基座提供的两张表也在这个 schema 里（同事务那条保证的前提）。
        Assert.Contains("outbox", tables);
        Assert.Contains("inbox", tables);

        // 二、**每个 Id 列都不带数据库生成的标识**。查 pg_catalog 而不是比对 DDL 文本：
        // attidentity 是权威来源，而 DDL 文本可能被格式变化骗过。
        await using (var connection = await fixture.Database.OpenAsync())
        {
            await using var command = new NpgsqlCommand(
                "select c.relname::text, a.attname::text, a.attidentity::text from pg_attribute a "
                + "join pg_class c on c.oid = a.attrelid "
                + "join pg_namespace n on n.oid = c.relnamespace "
                + "where n.nspname = @schema and a.attname = 'Id' and a.attnum > 0 and c.relkind = 'r'",
                connection);
            command.Parameters.AddWithValue("schema", IdentitySchema);

            var offending = new List<string>();
            var scanned = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                scanned.Add($"{reader.GetString(0)}.{reader.GetString(1)}");

                if (!string.IsNullOrEmpty(reader.GetString(2)))
                {
                    offending.Add($"{reader.GetString(0)}.{reader.GetString(1)} → {reader.GetString(2)}");
                }
            }

            // **守卫：先问"扫到了几列"。**
            //
            // 上面那句 `a.attname = 'Id'` 是大小写敏感的。列名一旦被折成 `id`，
            // 这个循环体一次都不执行，`offending` 就是空的——
            // 于是"每一个 Id 列都没有 IDENTITY"在**什么都没查**的情况下通过。
            // 这正是 AGENTS.md「检查没有对象可查时不得报告通过」说的那个形状。
            //
            // 有 Id 列的表是 6 张（users / roles / api_resources / refresh_tokens /
            // menu_trees / menu_nodes）；outbox / inbox / user_roles / role_menus 没有 Id 列。
            Assert.True(
                scanned.Count >= 6,
                $"只扫到 {scanned.Count} 个 Id 列（至少应有 6 个）——这组断言等于没跑，不能当作通过。"
                    + "（多半是列名大小写变了，而不是「表里真的没有 Id 列」）");

            Assert.True(
                offending.Count == 0,
                "这些 Id 列带了数据库生成的标识（ADR-0009 要求由应用侧赋值）："
                + string.Join("；", offending));
        }

        // 三、迁移历史表落在 **identity** schema 里，不是 public。
        Assert.True(
            await SchemaOwnsTableAsync(IdentitySchema, NexusStackDbContextOptionsExtensions.MigrationsHistoryTableName),
            "迁移历史表不在 identity schema 里——五个上下文会因此共用一张历史表。");
    }

    /// <summary>
    /// **唯一索引存在，且重复用户名插入失败**（票据 07 验收 2）。
    ///
    /// <para>参照仓库**零**唯一索引，于是"两个同名用户"只能靠业务代码先查后插去挡——
    /// 而那有竞态。这里验的是数据库自己会挡。</para>
    /// </summary>
    [PostgresFact]
    public async Task DuplicateUserName_IsRejectedByTheDatabase()
    {
        await fixture.ResetAsync();
        await using var context = fixture.NewContext();

        var first = User.Register(
            new UserId(1),
            UserName.Create("leo").Value,
            PasswordHash.Create(new string('a', 64)).Value,
            RegisteredAt);

        context.Users.Add(first);
        await context.SaveChangesAsync();

        Assert.Equal(1L, await CountAsync("users"));

        // 换一个上下文：同一个上下文里插入重复唯一键会先撞变更跟踪器，
        // 那是客户端异常，验不到数据库。
        await using var second = new IdentityDbContext(Options(fixture.Database));

        var duplicate = User.Register(
            new UserId(2),
            UserName.Create("leo").Value,
            PasswordHash.Create(new string('b', 64)).Value,
            RegisteredAt);

        second.Users.Add(duplicate);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());

        // 明确断言是**唯一约束**而不是别的什么失败。
        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal("23505", postgres.SqlState);
        Assert.Contains("ux_users_user_name", postgres.ConstraintName, StringComparison.Ordinal);

        Assert.Equal(1L, await CountAsync("users"));
    }

    /// <summary>
    /// **并发令牌让"两个人同时改同一条记录"变成一次失败**，而不是后写覆盖先写。
    ///
    /// <para>参照仓库的更新是**全列覆盖**、没有乐观并发——两个请求同时保存时，
    /// 后一个会安静地把前一个的改动抹掉，而没有任何人会发现。</para>
    /// </summary>
    [PostgresFact]
    public async Task ConcurrentUpdates_DoNotSilentlyOverwrite()
    {
        await fixture.ResetAsync();
        await using var setup = fixture.NewContext();

        var user = User.Register(
            new UserId(1),
            UserName.Create("leo").Value,
            PasswordHash.Create(new string('a', 64)).Value,
            RegisteredAt);

        setup.Users.Add(user);
        await setup.SaveChangesAsync();

        // 两个上下文各读一份**同一个版本**。
        await using var first = new IdentityDbContext(Options(fixture.Database));
        await using var second = new IdentityDbContext(Options(fixture.Database));

        var copyA = await first.Users.SingleAsync(candidate => candidate.Id == new UserId(1));
        var copyB = await second.Users.SingleAsync(candidate => candidate.Id == new UserId(1));

        copyA.ChangePassword(PasswordHash.Create(new string('b', 64)).Value, DateTimeOffset.UtcNow);
        copyB.ChangePassword(PasswordHash.Create(new string('c', 64)).Value, DateTimeOffset.UtcNow);

        await first.SaveChangesAsync();

        // 第二个人拿着**过期版本**保存——必须失败，而不是覆盖。
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    private async Task<bool> SchemaOwnsTableAsync(string schema, string table)
    {
        await using var connection = await fixture.Database.OpenAsync();
        await using var command = new NpgsqlCommand(
            "select count(*) from pg_tables where schemaname = @schema and tablename = @table",
            connection);

        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);

        return (long)(await command.ExecuteScalarAsync())! > 0;
    }
}
