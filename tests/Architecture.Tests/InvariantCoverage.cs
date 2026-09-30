namespace NexusStackNext.Architecture.Tests;

/// <summary>
/// 不变量 → 守着它的测试。
/// <para>
/// 这张表原先是一段 XML 注释。注释不会被编译检查（测试项目关了 <c>GenerateDocumentationFile</c>），
/// 于是它烂掉了都没人知道：里面引用了一个**根本不存在**的测试名，
/// 还写着"当前还没有任何上下文程序集，规则处于空转"——而那时已经有 13 个了。
/// </para>
/// <para>
/// 现在它是有结构的，并由 <see cref="ArchitectureInvariantTests.InvariantCoverage_IsCompleteAndPointsAtRealTests"/>
/// 校验：名字写错、测试被删、或者漏列，都会红。
/// <b>一句关于"我们测了什么"的声明，要么被机器检查，要么就是在慢慢变成谎话。</b>
/// </para>
/// </summary>
/// <param name="Number">不变量编号，对应 <c>AGENTS.md</c>。</param>
/// <param name="Statement">不变量的简述。</param>
/// <param name="EnforcingTestsHere">本测试类里守着它的测试方法名。</param>
/// <param name="Elsewhere">不在本测试类里的覆盖，或"为什么这里不测"。没有本地测试时必须写明。</param>
internal sealed record InvariantCoverageEntry(
    int Number,
    string Statement,
    IReadOnlyList<string> EnforcingTestsHere,
    string Elsewhere);

/// <summary><see cref="InvariantCoverageEntry"/> 的清单。</summary>
internal static class InvariantCoverage
{
    /// <summary>八条不变量各自的覆盖情况。</summary>
    public static IReadOnlyList<InvariantCoverageEntry> All { get; } =
    [
        new(
            1,
            "一个上下文独占自己的数据",
            ["Contexts_MustNotReferenceOtherContexts"],
            "这里只能测结构性代理（不跨引用）。真正的数据规则要连上数据库才验证得了——见 review/05 的遗留。"),
        new(
            2,
            "上下文之间只通过 *.Contracts 通信",
            ["Contexts_MustNotReferenceOtherContexts"],
            string.Empty),
        new(
            3,
            "*.Domain 不依赖任何基础设施",
            ["DomainAssemblies_MustNotReferenceAnythingOutsideTheAllowList"],
            string.Empty),
        new(
            4,
            "一个聚合 = 一个事务",
            [],
            "不是结构规则，编译器与程序集引用都管不了。行为由 BuildingBlocks.Application.Tests 的事务管线测试守着。"),
        new(
            5,
            "禁止服务定位器",
            ["NoServiceLocator_StaticContainerHoldersAreForbidden"],
            string.Empty),
        new(
            6,
            "ID 不由环境态生成",
            ["DomainAssemblies_MustNotReachForAmbientState"],
            // **这段说明第 20 轮改过，因为原来那句是错的。**
            // 原文写："结构性保证来自不变量 3 的测试（碰不到基础设施就调不到静态生成器）"——
            // 而 `Guid.NewGuid()` 与 `DateTime.UtcNow` 都是 **BCL**，不变量 3 只挡基础设施程序集。
            // **事实上领域层就在调 `Guid.NewGuid()`**（12 处，全在领域事件的 `EventId` 上，
            // 那是合法例外）。所以那句话没有牙齿，而它读起来像有。
            "行为性保证另见 BuildingBlocks.Domain.Tests 的聚合测试（ID 由调用方传入，所以每个聚合测试都能构造出确定的实例）。"),

        new(
            7,
            "共享代码要被第二个消费者证明需要才上移",
            ["BuildingBlocks_MustNotGrowBeyondTheDeclaredSharedKernel"],
            "真正的判据是「有没有第二个消费者」，那是评审时的判断；这里只能守住「没声明的不许出现」。"),
        new(
            8,
            "每个宿主显式组装自己",
            ["Hosts_MustComposeExplicitly"],
            string.Empty),
    ];

    /// <summary>
    /// 本测试类里**不属于**八条不变量的结构性卫生检查。
    /// <para>列出来的目的是让"每条测试都有归属"这件事可以被检查——
    /// 否则新增一条测试却忘了登记，这张表会悄悄变得不完整。</para>
    /// </summary>
    public static IReadOnlyList<string> StructuralHygieneTests { get; } =
    [
        "SourceAssemblyInventory_MatchesDeclaredSet",
        "AllSourceAssemblies_AreReferencedByThisTestProject",
        "ApplicationAssemblies_MustNotReferenceInfrastructure",
        "EveryServiceModule_IsWiredIntoAHost",
        "DomainEntities_MustEncapsulateTheirState",
        "InvariantCoverage_IsCompleteAndPointsAtRealTests",
    ];

    /// <summary>
    /// **整体就是一条卫生检查**的测试类——它们的所有 <c>[Fact]</c> 都不属于八条不变量。
    ///
    /// <para><b>这份清单是第 25 轮补的，因为原来那条"每条测试都有归属"的检查
    /// 只看 <c>ArchitectureInvariantTests</c> 一个类。**</para>
    ///
    /// <para>实测：孤儿检查用的是
    /// <c>typeof(ArchitectureInvariantTests).GetMethods(...)</c>，
    /// 于是另外**四个**类的测试完全不在它的射程内——
    /// <c>AuditBypassIsForbiddenTests</c>、<c>EndpointPurityTests</c>、
    /// <c>TestProjectHygieneTests</c>，以及第 24 轮新加的 <c>ContextMapTests</c>。</para>
    ///
    /// <para>失效方式与它想防的那件事一模一样：**新写一条结构检查、放进一个新类，
    /// 就不会有人提醒你登记**——而"没登记"与"登记完整"在那条检查下看起来没有区别。
    /// 所以现在孤儿检查扫**整个程序集**，而新类必须出现在这份清单里。</para>
    /// </summary>
    public static IReadOnlyList<string> StructuralHygieneClasses { get; } =
    [
        "AuditBypassIsForbiddenTests",
        "ContextMapTests",
        "EndpointPurityTests",
        "TestProjectHygieneTests",
    ];
}
