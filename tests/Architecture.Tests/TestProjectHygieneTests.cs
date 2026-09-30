namespace NexusStackNext.Architecture.Tests;

/// <summary>
/// 测试工程本身的卫生规则。
///
/// <para><b>为什么不放在 <see cref="ArchitectureInvariantTests"/> 里。</b>
/// 那个类是"把 AGENTS.md 的八条架构不变量变成测试"，而这里管的是**测试怎么摆**，
/// 不是产品代码的结构。混在一起会让那张不变量覆盖表变得不诚实。</para>
/// </summary>
public sealed class TestProjectHygieneTests
{
    /// <summary>
    /// **领域层测试不得引用任何做 I/O 的东西。**
    ///
    /// <para>票据 15 的验收里有一条"领域层测试无任何 IO 依赖"。今天它是**真的**——
    /// 六个 <c>*.Domain.Tests</c> 都只引用 xunit、自己的 Domain、BuildingBlocks.Domain 与 TestSupport。
    /// 但**没有任何东西守着它**：谁往 <c>Identity.Domain.Tests</c> 里加一个 <c>Npgsql</c>，
    /// 构建照样通过，测试照样全绿。</para>
    ///
    /// <para>而这条依赖一旦出现，它是**会蔓延的**：领域测试从"毫秒级、无外部依赖"变成
    /// "需要一台数据库才能跑"，而反馈环一旦变慢，人就少跑它——那时领域层那些
    /// 聚合不变量的测试会第一批被跳过。</para>
    ///
    /// <para><b>只查工程文件，不查编译产物。</b>理由与
    /// <c>ApplicationAssemblies_MustNotReferenceInfrastructure</c> 的第二层相同：
    /// C# 只为**实际用到**的程序集发出 AssemblyRef，所以一个加进来还没被使用的
    /// 包引用在编译产物里不留痕迹——只查产物的话，反向验证通不过。</para>
    /// </summary>
    [Fact]
    public void DomainTestProjects_MustNotReferenceAnythingThatDoesIo()
    {
        var testsRoot = Path.Combine(SolutionAssemblies.RepositoryRoot, "tests");
        Assert.True(Directory.Exists(testsRoot), $"找不到 {testsRoot}。");

        var domainTestProjects = Directory
            .EnumerateFiles(testsRoot, "*.Domain.Tests.csproj", SearchOption.AllDirectories)
            .ToArray();

        Assert.NotEmpty(domainTestProjects);

        // 一旦有人给领域测试加进这些，就已经越过了"无 IO"这条线。
        string[] forbidden =
        [
            "Npgsql",
            "EntityFrameworkCore",
            "StackExchange.Redis",
            "RabbitMQ",
            "Testcontainers",
            "Microsoft.AspNetCore",
            "System.Net.Http",
        ];

        var violations = new List<string>();

        foreach (var csproj in domainTestProjects)
        {
            var text = File.ReadAllText(csproj);

            foreach (var package in forbidden)
            {
                if (text.Contains(package, StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"{Path.GetFileNameWithoutExtension(csproj)} → {package}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "领域层测试引用了做 I/O 的东西，它就不再是「毫秒级、无外部依赖」的那一层了："
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", violations)
            + Environment.NewLine
            + "需要真数据库的测试请放进 *IntegrationTests 工程，并复用 tests/IntegrationSupport 的夹具。");
    }
}
