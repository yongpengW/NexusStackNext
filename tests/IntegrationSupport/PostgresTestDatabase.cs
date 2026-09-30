using System.Globalization;
using Npgsql;

namespace NexusStackNext.IntegrationSupport;

/// <summary>
/// 集成测试连哪个库——**只从一个环境变量读**。
///
/// <para><b>为什么不读 appsettings、也不读配置中心。</b>测试最不该有的依赖就是配置中心：
/// 它会让"跑测试"变成需要网络与凭据才能开始的动作，而测试恰恰是那个
/// <b>在任何机器上都该能跑</b>的东西。环境变量还有一个好处——
/// 它不落进任何被提交的文件（见 <c>env/README.md</c>）。</para>
/// </summary>
public static class TestPostgres
{
    /// <summary>连接串所在的环境变量名。</summary>
    public const string ConnectionStringVariable = "NEXUSSTACK_TEST_POSTGRES";

    /// <summary>取得连接串。</summary>
    /// <returns>连接串。</returns>
    /// <exception cref="InvalidOperationException">环境变量没设。</exception>
    public static string ConnectionString()
    {
        var value = Environment.GetEnvironmentVariable(ConnectionStringVariable);

        return string.IsNullOrWhiteSpace(value)
            // 错误信息要**能直接照着做**：说清是哪个变量、去哪里配、怎么配。
            ? throw new InvalidOperationException(
                $"没有设置环境变量 {ConnectionStringVariable}，集成测试无法确定连哪个 PostgreSQL。"
                + "本机开发请填 env/test.dev（pwsh -File scripts/run-tests.ps1 -Init 会生成它），"
                + "CI 请把这个变量作为密钥注入。")
            : value;
    }
}

/// <summary>
/// 一个**专属本次测试**的 PostgreSQL schema。
///
/// <para><b>为什么是 schema 而不是 database。</b>本仓共用一个库、库内按 schema 分开
/// （ADR-0013）。测试沿用同一条思路：每个实例建一个 <c>test_&lt;随机&gt;</c> schema，
/// 销毁时 <c>DROP SCHEMA … CASCADE</c>。这样并发跑两次不会互相踩，
/// 而"建库"这件事不需要额外权限。</para>
///
/// <para><b>连接串带上 <c>Search Path</c>，这是关键。</b>未限定的表名会落进这个 schema，
/// 于是被测代码**不需要知道自己在测试里**——它照常写 <c>CREATE TABLE users</c>，
/// 而那张表只存在于本次测试的 schema 中。若靠"测试里手动加 schema 前缀"，
/// 被测的就不是生产里那段 SQL 了。</para>
///
/// <para><b>两层隔离，各有各的用处。</b>schema 保证测试与测试之间不互相污染；
/// 事务回滚保证**单个测试内部**的写入不留痕迹——后者更快，也更容易看懂。
/// 两者不重复：一个管边界，一个管清理。</para>
/// </summary>
public sealed class PostgresTestDatabase : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly string _adminConnectionString;
    private bool _disposed;

    private PostgresTestDatabase(string adminConnectionString, string schema)
    {
        _adminConnectionString = adminConnectionString;
        Schema = schema;

        _connectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            SearchPath = schema,
        }.ConnectionString;
    }

    /// <summary>本次测试专属的 schema 名。</summary>
    public string Schema { get; }

    /// <summary>指向本 schema 的连接串（未限定的表名会落在这里）。</summary>
    public string ConnectionString => _connectionString;

    /// <summary>
    /// 建一个专属 schema。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>可用的测试库句柄；用完必须 <c>await using</c> 释放。</returns>
    public static async Task<PostgresTestDatabase> CreateAsync(CancellationToken cancellationToken = default)
    {
        var admin = TestPostgres.ConnectionString();

        // 名字里带**创建时刻**与随机段。
        //
        // 随机段保证并发跑两次不会撞；而时刻段是为了**清理**：
        // 进程被杀（构建失败后误用 --no-build、Ctrl+C、CI 超时）时 `DisposeAsync` 根本不会跑，
        // schema 就留下来了——而 PostgreSQL 不记录 schema 的创建时间，没有时刻段就无从判断"多久算旧"。
        var schema = $"test_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("n")[..8]}";

        var database = new PostgresTestDatabase(admin, schema);
        await database.ExecuteAdminAsync(
            string.Create(CultureInfo.InvariantCulture, $"CREATE SCHEMA \"{schema}\""),
            cancellationToken).ConfigureAwait(false);

        return database;
    }

    /// <summary>
    /// 清掉**过期的**测试 schema。
    ///
    /// <para><b>为什么需要它。</b>正常路径不泄漏（每个测试 <c>await using</c> 释放，用完
    /// <c>DROP … CASCADE</c>），但**进程被杀时那一步不会执行**：构建失败后误用
    /// <c>--no-build</c>、Ctrl+C、CI 超时都会留下一个 schema。
    /// 残留不会让任何测试变红，只会让库慢慢变脏——所以它需要一个**主动**的清理入口，
    /// 而不是靠人记得手工删。</para>
    ///
    /// <para>名字里不带可解析时刻的 <c>test_*</c> 一律视为遗留（本仓的测试 schema 只会由
    /// <see cref="CreateAsync"/> 创建，因此"名字对不上格式"本身就说明它是旧的）。</para>
    /// </summary>
    /// <param name="olderThan">多久以前的算过期。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>被清掉的 schema 名。</returns>
    public static async Task<IReadOnlyList<string>> CleanupStaleAsync(
        TimeSpan olderThan,
        CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.UtcNow - olderThan;
        var removed = new List<string>();

        foreach (var schema in await ListTestSchemasAsync(cancellationToken).ConfigureAwait(false))
        {
            if (IsStale(schema, cutoff))
            {
                await DropSchemaAsync(schema, cancellationToken).ConfigureAwait(false);
                removed.Add(schema);
            }
        }

        return removed;
    }

    /// <summary>列出库里所有 <c>test_*</c> schema。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>schema 名。</returns>
    public static async Task<IReadOnlyList<string>> ListTestSchemasAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(TestPostgres.ConnectionString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            "select nspname from pg_namespace where nspname like 'test\\_%' order by nspname",
            connection);

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>删掉一个 schema（<c>CASCADE</c>）。</summary>
    /// <param name="schema">schema 名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务。</returns>
    public static async Task DropSchemaAsync(string schema, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        await using var connection = new NpgsqlConnection(TestPostgres.ConnectionString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            string.Create(CultureInfo.InvariantCulture, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE"),
            connection);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>判断一个测试 schema 是否已过期。</summary>
    /// <param name="schema">schema 名。</param>
    /// <param name="cutoff">早于这个时刻的算过期。</param>
    /// <returns>是否过期。</returns>
    public static bool IsStale(string schema, DateTimeOffset cutoff)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        // test_yyyyMMddHHmmss_xxxxxxxx
        var parts = schema.Split('_');

        if (parts.Length >= 3
            && DateTimeOffset.TryParseExact(
                parts[1],
                "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var createdAt))
        {
            return createdAt < cutoff;
        }

        // 名字对不上格式 = 更早的版本留下的，一律当过期。
        return true;
    }

    /// <summary>打开一个指向本 schema 的连接。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已打开的连接。</returns>
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>在**管理连接**（不带 Search Path）上执行一条语句，用于建/删 schema。</summary>
    /// <param name="sql">语句。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>受影响行数。</returns>
    public async Task<int> ExecuteAdminAsync(string sql, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>本 schema 里现在有哪些表——断言用。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表名，已排序。</returns>
    public async Task<IReadOnlyList<string>> ListTablesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = @schema ORDER BY table_name",
            connection);

        command.Parameters.AddWithValue("schema", Schema);

        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    /// <summary>某个 schema 现在还在不在——断言"销毁干净了"用。</summary>
    /// <param name="schema">schema 名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>存在为 <c>true</c>。</returns>
    public static async Task<bool> SchemaExistsAsync(string schema, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        await using var connection = new NpgsqlConnection(TestPostgres.ConnectionString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.schemata WHERE schema_name = @schema",
            connection);

        command.Parameters.AddWithValue("schema", schema);

        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! > 0;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // CASCADE：测试自己建的表、约束、类型一并带走，不必逐个清。
        await ExecuteAdminAsync(
            string.Create(CultureInfo.InvariantCulture, $"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE"),
            CancellationToken.None).ConfigureAwait(false);
    }
}
