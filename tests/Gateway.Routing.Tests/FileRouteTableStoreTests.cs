using NexusStackNext.Gateway.Routing;

namespace NexusStackNext.Gateway.Routing.Tests;

/// <summary>
/// 文件路由存储。<b>重点是"写入原子、读到的永远是完整文档"</b>——
/// 参照仓库直接往目标文件写，读者可能拿到写了一半的 JSON。
/// </summary>
public sealed class FileRouteTableStoreTests : IDisposable
{
    // 刻意用测试输出目录，而不是系统临时目录：某些受限环境下系统临时目录的写入会被拒绝，
    // 而输出目录一定可写，并且随构建产物一起被清理。**不要"顺手"改回 Path.GetTempPath()。**
    private readonly string _directory = Path.Combine(
        AppContext.BaseDirectory,
        "routetable-tests",
        Guid.NewGuid().ToString("n"));

    private string RouteTablePath => Path.Combine(_directory, "routes.json");

    private static GatewayRouteTable Table(string firstRouteId = "r1") => GatewayRouteTable.Create(
        [new RouteDefinition { RouteId = firstRouteId, ClusterId = "c1", Path = "/api/x" }],
        [new ClusterDefinition { ClusterId = "c1", Destinations = [new DestinationDefinition("p", "http://127.0.0.1:1")] }]).Value;

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveThenLoad_RoundTrips()
    {
        var store = new FileRouteTableStore(RouteTablePath);

        var saved = await store.SaveAsync(Table());
        Assert.True(saved.IsSuccess, saved.IsFailure ? $"{saved.Error.Code}: {saved.Error.Message}" : null);

        var loaded = await store.LoadAsync();

        Assert.True(
            loaded.IsSuccess,
            loaded.IsFailure
                ? $"{loaded.Error.Code}: {loaded.Error.Message} | exists={File.Exists(RouteTablePath)} path={RouteTablePath}"
                : null);
        Assert.Equal("r1", Assert.Single(loaded.Value.Routes).RouteId);
    }

    [Fact]
    public async Task Save_CreatesTheDirectory_AndLeavesNoTempFileBehind()
    {
        var store = new FileRouteTableStore(RouteTablePath);

        await store.SaveAsync(Table());

        Assert.True(File.Exists(RouteTablePath));
        Assert.False(File.Exists(RouteTablePath + ".tmp"), "临时文件必须被 rename 走或清理掉。");
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task Load_MissingFile_FailsWithAClearReason()
    {
        var store = new FileRouteTableStore(RouteTablePath);

        var result = await store.LoadAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("gateway.route_table.file_missing", result.Error.Code);
    }

    [Fact]
    public async Task Load_CorruptFile_FailsInsteadOfReturningAnEmptyTable()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(RouteTablePath, "{ not json");

        var result = await new FileRouteTableStore(RouteTablePath).LoadAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("gateway.json.malformed", result.Error.Code);
    }

    [Fact]
    public async Task Save_OverwritesExistingContent_WithoutLeavingAPartialFile()
    {
        var store = new FileRouteTableStore(RouteTablePath);
        await store.SaveAsync(Table("first"));

        Assert.True((await store.SaveAsync(Table("second"))).IsSuccess);

        var loaded = await store.LoadAsync();

        Assert.Equal("second", Assert.Single(loaded.Value.Routes).RouteId);
    }

    [Fact]
    public async Task ConcurrentReads_NeverObserveAPartialDocument()
    {
        // 契约见 FileRouteTableStore 的类型文档：
        //   · **写者必须成功**——这是真正被修过的那条：File.Move 覆盖一个被打开的目标会
        //     "Access to the path is denied"，读流量一稳定写者就永远写不进去。
        //   · **读者永远拿不到"写了一半"的文档**——内容完整性由"先写临时文件再整体替换"保证。
        //   · 但读者**允许**拿到一个明确失败：替换期间目录项是空窗的，
        //     读取侧在有限预算内重试，超出预算就报错。"读不到就报错"比"无限等待"好。
        //
        // 早先的版本断言"读者一次都不许失败"，那比实现保证的更强，
        // 于是在全量并行跑时偶发变红——**复现不了的不稳定测试比没有测试更糟**。
        var store = new FileRouteTableStore(RouteTablePath);
        await store.SaveAsync(Table("seed"));

        const int Iterations = 150;

        var writer = Task.Run(async () =>
        {
            for (var index = 0; index < Iterations; index++)
            {
                var saved = await store.SaveAsync(Table($"r{index}"));
                Assert.True(saved.IsSuccess, saved.IsFailure ? $"写者失败：{saved.Error.Message}" : null);
            }
        });

        var partialDocuments = new List<string>();
        var successes = 0;
        var transient = 0;

        while (!writer.IsCompleted)
        {
            var loaded = await store.LoadAsync();

            if (loaded.IsSuccess)
            {
                successes++;
            }
            else if (loaded.Error.Code == "gateway.json.malformed")
            {
                // 这一条才是原子写真正要防的：读到了半个文档。
                partialDocuments.Add(loaded.Error.Message);
            }
            else
            {
                transient++;
            }
        }

        await writer;

        Assert.True(successes > 0, $"读者一次都没成功（瞬时失败 {transient} 次）——重试预算形同虚设。");
        Assert.Empty(partialDocuments);
    }

    [Fact]
    public void Constructor_RejectsEmptyPath()
    {
        Assert.ThrowsAny<ArgumentException>(() => new FileRouteTableStore("  "));
    }

    [Fact]
    public async Task Save_RejectsNullTable()
    {
        var store = new FileRouteTableStore(RouteTablePath);

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.SaveAsync(null!));
    }
}
