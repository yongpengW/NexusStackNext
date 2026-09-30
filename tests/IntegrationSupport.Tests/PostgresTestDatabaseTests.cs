using Npgsql;

namespace NexusStackNext.IntegrationSupport.Tests;

/// <summary>
/// 测试库夹具**自己的**契约。
///
/// <para><b>为什么夹具也要有测试。</b>票据 15 的验收里写着"集成测试可重复运行且相互隔离"
/// 与"有一个测试证明：故意写脏数据，回滚后库是干净的"。那两条不是关于某个业务表的，
/// 而是关于**夹具本身**的——如果它坏了，每一个用它写的测试都会**看起来通过**
/// （写进了一个没人读的地方），或者**互相污染而时红时绿**。
/// 那种失败最难查，因为症状出现在离原因很远的地方。</para>
///
/// <para><b>四个用例写在同一个类里是有意的</b>：xUnit 对同一个类内的用例保证**顺序执行**，
/// 而其中一个要临时摘掉环境变量。分成多个类就会被并行执行，那个用例会干扰其他用例。</para>
/// </summary>
public sealed class PostgresTestDatabaseTests
{
    [PostgresFact]
    public async Task TwoDatabases_GetDifferentSchemas_AndCannotSeeEachOther()
    {
        await using var first = await PostgresTestDatabase.CreateAsync();
        await using var second = await PostgresTestDatabase.CreateAsync();

        Assert.NotEqual(first.Schema, second.Schema);

        await using (var connection = await first.OpenAsync())
        {
            // 未限定表名——它应当落进 first 的 schema，而不是 public。
            await using var command = new NpgsqlCommand("CREATE TABLE only_in_first (id integer)", connection);
            await command.ExecuteNonQueryAsync();
        }

        Assert.Contains("only_in_first", await first.ListTablesAsync());
        Assert.DoesNotContain("only_in_first", await second.ListTablesAsync());
    }

    [PostgresFact]
    public async Task DirtyWrite_RolledBack_LeavesTheTableEmpty()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();

        await using (var connection = await database.OpenAsync())
        {
            await using (var create = new NpgsqlCommand("CREATE TABLE rollback_probe (id integer)", connection))
            {
                await create.ExecuteNonQueryAsync();
            }

            await using var transaction = await connection.BeginTransactionAsync();

            await using (var insert = new NpgsqlCommand("INSERT INTO rollback_probe VALUES (1), (2), (3)", connection))
            {
                insert.Transaction = (NpgsqlTransaction)transaction;
                await insert.ExecuteNonQueryAsync();
            }

            // 3 行"脏数据"确实写进去了——不先证明这一点，回滚后为空就可能只是因为压根没写成功。
            await using (var peek = new NpgsqlCommand("SELECT count(*) FROM rollback_probe", connection))
            {
                peek.Transaction = (NpgsqlTransaction)transaction;
                Assert.Equal(3L, (long)(await peek.ExecuteScalarAsync())!);
            }

            await transaction.RollbackAsync();
        }

        await using var verify = await database.OpenAsync();
        await using var count = new NpgsqlCommand("SELECT count(*) FROM rollback_probe", verify);

        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
    }

    [PostgresFact]
    public async Task SchemaIsDropped_WhenDisposed()
    {
        var database = await PostgresTestDatabase.CreateAsync();
        var schema = database.Schema;

        await using (var connection = await database.OpenAsync())
        {
            await using var command = new NpgsqlCommand("CREATE TABLE gone_after_dispose (id integer)", connection);
            await command.ExecuteNonQueryAsync();
        }

        Assert.True(await PostgresTestDatabase.SchemaExistsAsync(schema), "销毁之前 schema 应当还在。");

        await database.DisposeAsync();

        Assert.False(await PostgresTestDatabase.SchemaExistsAsync(schema), "销毁之后 schema 应当被 DROP 掉。");

        // 幂等：再销毁一次不该抛。
        await database.DisposeAsync();
    }

    [Fact]
    public void MissingConnectionString_ThrowsWithTheVariableNameAndHowToFixIt()
    {
        var original = Environment.GetEnvironmentVariable(TestPostgres.ConnectionStringVariable);

        try
        {
            Environment.SetEnvironmentVariable(TestPostgres.ConnectionStringVariable, null);

            var exception = Assert.Throws<InvalidOperationException>(TestPostgres.ConnectionString);

            // 错误信息**要能直接照着做**：说出变量名，也说出去哪配。
            Assert.Contains(TestPostgres.ConnectionStringVariable, exception.Message, StringComparison.Ordinal);
            Assert.Contains("env/test.dev", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestPostgres.ConnectionStringVariable, original);
        }
    }

    /// <summary>
    /// schema 名里带**创建时刻**，而清理只动过期的那些。
    ///
    /// <para><b>它同时做两件事</b>：验证清理逻辑，以及**顺手把遗留清掉**。
    /// 正常路径不泄漏（每个测试用完 <c>DROP … CASCADE</c>），但进程被杀时那一步不会执行——
    /// 构建失败后误用 <c>--no-build</c>、Ctrl+C、CI 超时都会留下一个。
    /// 残留不会让任何测试变红，只会让库慢慢变脏。</para>
    /// </summary>
    [PostgresFact]
    public async Task StaleSchemas_AreCleanedUp_AndFreshOnesAreKept()
    {
        // 造一个"很久以前被中断留下"的 schema，名字格式与夹具一致。
        var stale = $"test_{DateTimeOffset.UtcNow.AddDays(-7):yyyyMMddHHmmss}_deadbeef";
        await PostgresTestDatabase.DropSchemaAsync(stale);
        await using (var connection = await OpenRawAsync())
        {
            await using var create = new NpgsqlCommand($"CREATE SCHEMA \"{stale}\"", connection);
            await create.ExecuteNonQueryAsync();
        }

        // 同时开一个**新鲜**的，它不该被清掉。
        await using var fresh = await PostgresTestDatabase.CreateAsync();

        var removed = await PostgresTestDatabase.CleanupStaleAsync(TimeSpan.FromHours(1));

        Assert.Contains(stale, removed);
        Assert.DoesNotContain(fresh.Schema, removed);

        Assert.False(await PostgresTestDatabase.SchemaExistsAsync(stale), "过期的应当被删掉。");
        Assert.True(await PostgresTestDatabase.SchemaExistsAsync(fresh.Schema), "新鲜的应当留着。");
    }

    /// <summary>名字解析：带时刻的按时刻判，解析不出来的当过期。</summary>
    [Fact]
    public void IsStale_ReadsTheTimestampFromTheName()
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-1);

        var old = $"test_{DateTimeOffset.UtcNow.AddDays(-2):yyyyMMddHHmmss}_00000000";
        var fresh = $"test_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}_00000000";

        Assert.True(PostgresTestDatabase.IsStale(old, cutoff));
        Assert.False(PostgresTestDatabase.IsStale(fresh, cutoff));

        // 对不上格式的（更早版本留下的）一律当过期。
        Assert.True(PostgresTestDatabase.IsStale("test_4b0a684f7d9b4ade98a8c23748f58d1b", cutoff));
    }

    private static async Task<NpgsqlConnection> OpenRawAsync()
    {
        var connection = new NpgsqlConnection(TestPostgres.ConnectionString());
        await connection.OpenAsync();
        return connection;
    }
}
