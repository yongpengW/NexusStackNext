using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Identity.Infrastructure.Persistence;
using NexusStackNext.IntegrationSupport;

namespace NexusStackNext.Identity.IntegrationTests;

/// <summary>
/// **同一个进程里只迁移一次** `identity` schema，测试之间用 <c>TRUNCATE</c> 清数据。
///
/// <para><b>它为什么存在：一次真实的服务器事故。</b>这组测试原先每个用例都
/// <c>DROP SCHEMA identity CASCADE</c> + 重跑迁移——一个 schema 十一张表，
/// 二十来条用例就是二十来轮建表删表。而 <c>dotnet test</c> 默认**并行跑十几个工程**，
/// 于是同一台 PostgreSQL 上同时有几个进程在做 DDL。</para>
///
/// <para>后果是测试服务器磁盘读被打到 **1800 IOPS / 107 MBps、读延迟 70 ms**，
/// 全量跑了一个多小时没结束。DDL 会让 <c>pg_catalog</c> 膨胀，
/// 自动清理于是持续读取目录表（现场看到 <c>autovacuum: ANALYZE pg_catalog.pg_index</c>）。</para>
///
/// <para><b>TRUNCATE 与重新迁移的差别不是"快一点"。</b>前者只动数据页；
/// 后者每次都要写十一张表的目录项、索引、约束与外键，再全部删掉——
/// 而目录表的膨胀正是那次事故的机制。</para>
///
/// <para><b>它不负责"每个测试一个干净 schema"。</b>那套隔离仍然存在
/// （见 <c>PostgresTestDatabase</c>），只是**迁移无法按实例参数化**——
/// schema 名是编译进迁移里的。所以对这个 schema，隔离只能靠"清空数据"。</para>
/// </summary>
public sealed class IdentityDatabaseFixture : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    /// <summary>测试用的库（用于开连接做断言）。</summary>
    public PostgresTestDatabase Database =>
        _database ?? throw new InvalidOperationException("fixture 还没初始化。");

    /// <summary>这个 schema 里的数据表，按外键依赖的**逆序**排列——TRUNCATE 一次清完。</summary>
    private static readonly string[] DataTables =
    [
        "menu_nodes",
        "api_resources",
        "refresh_tokens",
        "user_roles",
        "role_menus",
        "users",
        "roles",
        "menu_trees",
        "outbox",
        "inbox",
    ];

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();

        // **先删再迁。** 这样"迁移可对空 schema 执行成功"这件事仍然被真的验证了一次
        // （而且只验证一次）——而不是像原先那样被二十来条用例各验证一遍。
        await PostgresTestDatabase.DropSchemaAsync(IdentityDbContext.SchemaName);

        await using var context = NewContext();
        await context.Database.MigrateAsync();
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>清空所有数据表，保留 schema 与迁移历史。</summary>
    public async Task ResetAsync()
    {
        // CASCADE 让外键顺序不必手工排——TRUNCATE 会连带清掉引用它的表。
        // 这条语句只动数据页，不碰目录。
        var list = string.Join(", ", DataTables.Select(static table => $"{IdentityDbContext.SchemaName}.{table}"));

        await using var connection = await Database.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new Npgsql.NpgsqlCommand($"truncate table {list} cascade", connection, transaction);
        await command.ExecuteNonQueryAsync();
        // Test reset bypasses DELETE triggers; reset the corresponding ledger in the same transaction.
        await using var capacity = new Npgsql.NpgsqlCommand("""
            UPDATE identity.fact_capacity SET "RetainedRecords" = 0, "RetainedPayloadBytes" = 0,
                "MaxRecords" = 100000, "MaxPayloadBytes" = 268435456, "MaxRecordPayloadBytes" = 16384
            """, connection, transaction);
        Assert.Equal(1, await capacity.ExecuteNonQueryAsync());
        await transaction.CommitAsync();
    }

    /// <summary>给需要独立上下文的测试用。</summary>
    public IdentityDbContext NewContext()
    {
        var builder = new DbContextOptionsBuilder<IdentityDbContext>();
        builder.UseNexusStackPostgres(Database.ConnectionString, IdentityDbContext.SchemaName);
        return new IdentityDbContext(builder.Options);
    }
}

/// <summary>把 <see cref="IdentityDatabaseFixture"/> 变成跨类共享的集合。</summary>
/// <remarks>类型名不叫 "Collection"——那会撞上 CA1711（该后缀留给集合类型）。集合名由 <c>Name</c> 给出。</remarks>
[CollectionDefinition(Name)]
public sealed class IdentityDatabaseGroup : ICollectionFixture<IdentityDatabaseFixture>
{
    /// <summary>集合名。</summary>
    public const string Name = "identity-database";
}
