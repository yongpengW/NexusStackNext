namespace NexusStackNext.Architecture.Tests;

/// <summary>
/// **端点层不得直接碰持久化。**
///
/// <para>它守的是票据 09 的验收 5，也是参照仓库 <c>OperationLogController.cs</c>
/// 的直接教训：控制器里直接跨表 join。那让"这个请求用到了哪些数据"变成一个
/// **只有读完整段代码才知道**的问题——而控制器本该只描述 HTTP。</para>
///
/// <para><b>为什么查程序集引用，而不是查源码里有没有 <c>DbContext</c> 这个词。</b>
/// 字符串搜索会被注释、局部变量名、以及"引用了但没用"三种情况骗过；
/// 而程序集引用是**编译器的结论**——它不在乎你怎么写，只在乎你用了什么。</para>
///
/// <para><b>但只查编译产物也有缺口</b>（本仓实测过）：C# 只为**实际用到**的程序集
/// 发出 AssemblyRef，所以一个加进 <c>csproj</c> 却没被使用的引用在产物里不留痕迹。
/// 于是这里两层都查——与 <c>ApplicationAssemblies_MustNotReferenceInfrastructure</c> 同一形状。</para>
/// </summary>
public sealed class EndpointPurityTests
{
    private static readonly string[] ForbiddenAssemblyFragments =
    [
        "EntityFrameworkCore",
        "Npgsql",
        "StackExchange.Redis",
        "RabbitMQ",
        "Dapper",
    ];

    /// <summary>端点程序集不得引用任何持久化实现。</summary>
    [Fact]
    public void EndpointAssemblies_MustNotReferencePersistence()
    {
        var endpoints = SolutionAssemblies.BuiltSourceAssemblyPaths()
            .Where(static path => Path.GetFileNameWithoutExtension(path)!.EndsWith(".Endpoints", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(endpoints);

        var violations = new List<string>();

        // 第一层：**类型级**——读编译产物，看端点是否真的引用了持久化程序集。
        foreach (var path in endpoints)
        {
            var assemblyName = Path.GetFileNameWithoutExtension(path)!;

            foreach (var reference in SolutionAssemblies.ReferencedAssemblyNames(path))
            {
                if (ForbiddenAssemblyFragments.Any(fragment => reference.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
                {
                    violations.Add($"{assemblyName} → {reference}");
                }
            }
        }

        // 第二层：**工程级**——读 csproj，看是否埋着一颗"随时可以用"的引用。
        var projectRoot = Path.Combine(SolutionAssemblies.RepositoryRoot, "src");
        foreach (var csproj in Directory.EnumerateFiles(projectRoot, "*.Endpoints.csproj", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(csproj);

            foreach (var fragment in ForbiddenAssemblyFragments)
            {
                if (text.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"{Path.GetFileName(csproj)} → {fragment}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "端点层引用了持久化实现。它只该描述 HTTP：解请求、交给分发器、把结果映射成状态码。"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", violations));
    }
}
