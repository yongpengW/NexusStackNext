using Mono.Cecil;
using NetArchTest.Rules;

namespace NexusStackNext.Architecture.Tests;

/// <summary>
/// 把 <c>AGENTS.md</c> 的架构不变量变成**会失败的测试**。
/// <para>
/// 上一版（NexusStackBackend）把边界寄托在人的自觉上：四个可部署单元共享一个 Core，
/// 跨服务误用能编译通过。这里让违反边界直接构建失败。
/// </para>
/// <para>
/// 覆盖情况见 <see cref="InvariantCoverage"/> ——它是有结构的表，不是散文：
/// <see cref="InvariantCoverage_IsCompleteAndPointsAtRealTests"/> 会校验八条不变量都在表里、
/// 表里引用的测试都真实存在、且每条测试都有归属。
/// </para>
/// </summary>
public sealed class ArchitectureInvariantTests
{
    private const string DomainSuffix = ".Domain";
    private const string ApplicationSuffix = ".Application";
    private const string InfrastructureSuffix = ".Infrastructure";

    /// <summary>
    /// 当前应有的 src 程序集清单。
    /// <para>
    /// 这是一道<b>哨兵</b>：新增 src 项目会先让这个测试红，逼着加的人回到这里确认
    /// 新程序集被下面哪些规则覆盖、是否让某条规则变成空转。
    /// </para>
    /// </summary>
    private static readonly string[] ExpectedSourceAssemblies =
    [
        "NexusStackNext.Auditing.Endpoints",
        "NexusStackNext.Auditing.Application",
        "NexusStackNext.Auditing.Domain",
        "NexusStackNext.Auditing.Infrastructure",
        "NexusStackNext.BuildingBlocks.Application",
        "NexusStackNext.BuildingBlocks.Domain",
        "NexusStackNext.BuildingBlocks.Infrastructure",
        "NexusStackNext.BuildingBlocks.Web",
        "NexusStackNext.Files.Endpoints",
        "NexusStackNext.Files.Application",
        "NexusStackNext.Files.Domain",
        "NexusStackNext.Files.Infrastructure",
        "NexusStackNext.Gateway",
        "NexusStackNext.Gateway.Routing",
        "NexusStackNext.Composition",
        "NexusStackNext.Identity.Endpoints",
        "NexusStackNext.Identity.Application",
        "NexusStackNext.Identity.Domain",
        "NexusStackNext.Identity.Infrastructure",
        "NexusStackNext.Platform.Endpoints",
        "NexusStackNext.Platform.Application",
        "NexusStackNext.Platform.Domain",
        "NexusStackNext.Platform.Infrastructure",
        "NexusStackNext.PlatformHost",
        "NexusStackNext.Scheduling.Endpoints",
        "NexusStackNext.Scheduling.Application",
        "NexusStackNext.Scheduling.Domain",
        "NexusStackNext.Scheduling.Infrastructure",
        // Aspire 的 AppHost 与 ServiceDefaults。它们住在 aspire/ 而不是 src/，
        // 但**是源码程序集**，所以必须出现在这张表里——
        // "多了一个程序集"是一件需要有人签字的事，而不是编译一下就算了。
        "NexusStackNext.ServiceDefaults",
    ];

    /// <summary>
    /// 架构决定（ADR-0001、ADR-0018）里声明的共享程序集。<b>只有清单中的允许存在</b>——
    /// 不变量 7 说"被第二个消费者证明需要才允许上移"，那是评审时的判断，编译器管不了；
    /// 这里守住可执行的下界：<b>BuildingBlocks 不得长出声明之外的程序集</b>。
    /// 新增就得改这份清单，改的时候必须回答"谁需要它、为什么不能留在上下文里"。
    /// </summary>
    private static readonly string[] DeclaredBuildingBlocks =
    [
        "NexusStackNext.BuildingBlocks.Application",
        "NexusStackNext.BuildingBlocks.Contracts",
        "NexusStackNext.BuildingBlocks.Domain",
        "NexusStackNext.BuildingBlocks.Infrastructure",
        // Identity 与 Platform 首先共同使用，随后 Files、Scheduling、Auditing 与网关接入。
        // 只共享 HTTP 线协议；模块自己的状态映射仍留在 Endpoints（ADR-0018）。
        "NexusStackNext.BuildingBlocks.Web",
    ];

    /// <summary>宿主里禁止出现的隐藏装配调用（含参照仓库里那个拼错的字面量）。</summary>
    private static readonly string[] BannedCompositionCalls =
    [
        "InitApplication(",
        "InitAppliation(",
        "App.Init(",
        "new ServiceCollection().BuildServiceProvider()",
    ];

    [Fact]
    public void InvariantCoverage_IsCompleteAndPointsAtRealTests()
    {
        // 一条关于"我们测了什么"的声明，要么被机器检查，要么就是在慢慢变成谎话。
        // 这张表原先正是一段注释——它烂掉时没有任何东西会响。
        var declared = InvariantCoverage.All;

        Assert.Equal(8, declared.Count);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], declared.Select(static entry => entry.Number));

        // **扫整个程序集，不只看这一个类。**
        //
        // 第一版是 `typeof(ArchitectureInvariantTests).GetMethods(...)`，于是另外四个类的
        // 测试完全不在射程内：AuditBypassIsForbiddenTests、EndpointPurityTests、
        // TestProjectHygieneTests，以及第 24 轮新加的 ContextMapTests。
        //
        // 失效方式与这条检查想防的事一模一样：**新写一条结构检查、放进一个新类，
        // 就不会有人提醒你登记**——而"没登记"与"登记完整"在旧版检查下看起来没有区别。
        var facts = typeof(ArchitectureInvariantTests).Assembly
            .GetTypes()
            .Where(static type => type.IsClass && !type.IsAbstract)
            .SelectMany(static type => type
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Where(static method => method.GetCustomAttributes(typeof(FactAttribute), inherit: false).Length > 0)
                .Select(method => (Type: type, Method: method.Name)))
            .ToList();

        var factNames = facts.Select(static pair => pair.Method).ToHashSet(StringComparer.Ordinal);

        // 整体就是一条卫生检查的类：它们的所有 [Fact] 都不属于八条不变量，
        // 但**必须在这份清单里出现过**——否则新类又悄悄溜出射程。
        var hygieneClasses = new HashSet<string>(
            InvariantCoverage.StructuralHygieneClasses,
            StringComparer.Ordinal);

        foreach (var className in hygieneClasses)
        {
            Assert.Contains(
                className,
                facts.Select(static pair => pair.Type.Name).Distinct(StringComparer.Ordinal));
        }

        var claimed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in declared)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(entry.Statement),
                $"不变量 {entry.Number} 没有写清是什么。");

            foreach (var testName in entry.EnforcingTestsHere)
            {
                Assert.Contains(testName, factNames);
                claimed.Add(testName);
            }

            // 没有本地测试的不变量，必须写明去哪儿找、或者为什么这里不测。
            if (entry.EnforcingTestsHere.Count == 0)
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(entry.Elsewhere),
                    $"不变量 {entry.Number} 在本测试类里没有守着它的测试，因此必须写明理由或缺席原因。");
            }
        }

        foreach (var hygiene in InvariantCoverage.StructuralHygieneTests)
        {
            Assert.Contains(hygiene, factNames);
            claimed.Add(hygiene);
        }

        // 反向：每条测试都要有归属。新增一条测试却忘了登记，这张表会悄悄变得不完整。
        // 纯卫生类里的方法全部算已归属——它们不在八条不变量里，也不在那份类内清单里，
        // 但**类本身**已经登记过了（见上面的 StructuralHygieneClasses）。
        var claimedByHygieneClass = facts
            .Where(pair => hygieneClasses.Contains(pair.Type.Name))
            .Select(static pair => pair.Method);

        var orphans = factNames
            .Except(claimed)
            .Except(claimedByHygieneClass)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            orphans.Count == 0,
            "以下测试既没有归属到某条不变量，也没有登记为结构性卫生检查——请登记，别让覆盖表悄悄失真：\n  "
                + string.Join("\n  ", orphans));
    }

    [Fact]
    public void SourceAssemblyInventory_MatchesDeclaredSet()
    {
        var actual = SourceAssemblyPaths()
            .Select(Path.GetFileNameWithoutExtension)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedSourceAssemblies.OrderBy(static n => n, StringComparer.Ordinal), actual, StringComparer.Ordinal);
    }

    [Fact]
    public void AllSourceAssemblies_AreReferencedByThisTestProject()
    {
        // 类型级检查需要真实加载程序集。若某个 src 项目没被引用，它的 DLL 不在测试输出目录，
        // 那些检查就会**静默漏掉它**——这条测试专门堵这个洞。
        var missing = SourceAssemblyPaths()
            .Select(Path.GetFileName)
            .Where(static fileName => !File.Exists(Path.Combine(AppContext.BaseDirectory, fileName!)))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            $"以下 src 程序集没有被 Architecture.Tests 引用，类型级检查会漏掉它们：{string.Join(", ", missing)}");
    }

    [Fact]
    public void DomainAssemblies_MustNotReferenceAnythingOutsideTheAllowList()
    {
        // 不变量 3：*.Domain 不依赖任何基础设施。
        // 用**白名单**而不是黑名单：黑名单只能挡住你想到的名字，白名单挡住的是没想到的。
        var domainPaths = SourceAssemblyPaths()
            .Where(static path => Path.GetFileNameWithoutExtension(path).EndsWith(DomainSuffix, StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(domainPaths);

        var violations = new List<string>();
        foreach (var path in domainPaths)
        {
            var assemblyName = Path.GetFileNameWithoutExtension(path);
            foreach (var reference in SolutionAssemblies.ReferencedAssemblyNames(path))
            {
                if (!IsAllowedInDomain(assemblyName, reference))
                {
                    violations.Add($"{assemblyName} → {reference}");
                }
            }
        }

        // 第二层：**工程级**——读 csproj。
        //
        // **这一层是第 19 轮 review 补上的，因为上面那一层有个洞。**
        // 它只读编译产物，而 C# 只为**实际用到**的程序集发出 AssemblyRef——
        // 于是一个挂在 Domain 工程上、**没有任何代码使用**的 EF 包引用
        // 在 DLL 里不留任何痕迹，上面那层看不见它。
        //
        // 实测过：给 `Auditing.Domain` 加上 `<PackageReference Include="Microsoft.EntityFrameworkCore" />`
        // 而不用它，这条测试**照样全绿**。而"seam 放错了位置"这件事，
        // 恰恰是从"埋着一颗随时可以用的引用"开始的。
        //
        // `*.Application` 那条规则早就有这两层，且注释里写明了"两层缺一不可"。
        // 同一条道理在领域层同样成立，只是当时漏了。
        foreach (var csproj in SolutionAssemblies.SourceProjectPaths("*.Domain.csproj"))
        {
            var projectName = Path.GetFileNameWithoutExtension(csproj);

            foreach (System.Text.RegularExpressions.Match match in
                System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(csproj), "<PackageReference Include=\"([^\"]+)\""))
            {
                violations.Add($"{projectName} → {match.Groups[1].Value}（包引用）");
            }

            foreach (System.Text.RegularExpressions.Match match in
                System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(csproj), "ProjectReference Include=\"[^\"]*?([^\\\\/\"]+)\\.csproj\""))
            {
                // 领域层之间唯一的合法引用是共享内核本身；`BuildingBlocks.Domain` 自己谁也不引。
                var referenced = match.Groups[1].Value;
                if (!string.Equals(referenced, "BuildingBlocks.Domain", StringComparison.Ordinal))
                {
                    violations.Add($"{projectName} → {referenced}（工程引用）");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "领域层只允许依赖 BCL 与自己。出现了下列引用，说明 seam 放错了位置：\n  "
                + string.Join("\n  ", violations));
    }

    [Fact]
    public void DomainAssemblies_MustNotReachForAmbientState()
    {
        // 不变量 6：**ID 不由环境态生成**。
        //
        // **这条检查是第 20 轮 review 补上的，因为原来的说法站不住。**
        // `InvariantCoverage` 里不变量 6 的 `Elsewhere` 写着：
        //
        //   "结构性保证来自不变量 3 的测试（碰不到基础设施就调不到静态生成器）"
        //
        // 而 `Guid.NewGuid()` 与 `DateTime.UtcNow` 都是 **BCL** —— 不变量 3 只挡住
        // *基础设施程序集*，挡不住它们。**事实上领域层就在调 `Guid.NewGuid()`**
        // （12 处，全在领域事件的 `EventId` 上）。所以那句话没有牙齿，而它读起来像有。
        //
        // 现在把它变成一条真的检查，判据只有两条：
        //
        //   1. **时间一律由调用方传入。** 聚合里出现 `DateTime.UtcNow` 就意味着
        //      它的行为依赖"此刻"——那让"改状态 +1 / 空操作不变"这类测试变成碰运气。
        //   2. **`Guid.NewGuid()` 只允许出现在 `Events/` 下。** 领域事件的 `EventId`
        //      天生就是"每次发生都不同"，要求它由调用方传入是荒谬的；
        //      而**聚合的 ID** 由调用方给出，聚合内部再生成一个就是环境态。
        //
        // 判据是文本级的，比编译产物弱一档——但它盯的正是最现实的那种手滑：
        // **在聚合里顺手写了一个 `Guid.NewGuid()`。**
        var domainRoot = Path.Combine(SolutionAssemblies.RepositoryRoot, "src");
        var violations = new List<string>();
        var scanned = new List<string>();

        foreach (var file in Directory.EnumerateFiles(domainRoot, "*.cs", SearchOption.AllDirectories))
        {
            // **按路径分段判断，不拼字符串。**
            // 第一版写的是 `file.Contains($"\\.Domain\\")` 那一类的拼接，
            // 结果是**一个文件都没匹配上**——而它静默通过了。
            // 分段写法不受分隔符与拼接细节影响，而且"工程目录叫什么"这件事一眼能看出来。
            var segments = file.Split(Path.DirectorySeparatorChar);

            var inDomainProject = Array.Exists(
                segments,
                static segment => segment.EndsWith(".Domain", StringComparison.Ordinal));

            if (!inDomainProject || Array.Exists(segments, static segment => segment == "obj"))
            {
                continue;
            }

            scanned.Add(file);

            var relative = Path.GetRelativePath(SolutionAssemblies.RepositoryRoot, file);
            var text = File.ReadAllText(file);

            if (text.Contains("DateTime.UtcNow", StringComparison.Ordinal)
                || text.Contains("DateTimeOffset.UtcNow", StringComparison.Ordinal))
            {
                violations.Add($"{relative} → 用了 UtcNow（时间必须由调用方传入）");
            }

            // **领域事件的 `EventId` 是唯一合法的例外，判据落在"那一行在初始化 EventId"上。**
            //
            // 第一版把例外划成"只允许在 `Events/` 目录下"——**那是错的**，
            // 而它错了这件事差点没被发现：那时过滤器一个文件都没匹配上，
            // 检查静默通过。修好过滤器之后立刻冒出两处"违规"，而它们都是合法的：
            //
            //   Platform.Domain\Settings\GlobalSetting.cs   public Guid EventId { get; } = Guid.NewGuid();
            //   Scheduling.Domain\Tasks\ScheduledTask.cs    public Guid EventId { get; } = Guid.NewGuid();
            //
            // 那两个上下文把领域事件**内联**在聚合文件里，Identity 则放在 `Events/` 下。
            // 两种都合理，所以判据不能落在"文件在哪"，只能落在"这一行在干什么"。
            foreach (var line in File.ReadLines(file))
            {
                if (line.Contains("Guid.NewGuid()", StringComparison.Ordinal)
                    && !line.Contains("EventId", StringComparison.Ordinal))
                {
                    violations.Add($"{relative} → 调了 Guid.NewGuid()（ID 必须由调用方传入）：{line.Trim()}");
                }
            }
        }

        // **这条守卫是必须的，而且我第一版漏了它。**
        //
        // 过滤器一个文件都没匹配上时，`violations` 是空的——测试**静默通过**。
        // 而"检查没有对象可查"与"检查通过"是两件完全不同的事，
        // 本仓库的脚本里已经为这件事写过一次注释（`check-tracker.ps1`），
        // 我在这里又犯了一遍，而且是**在写一条专门防这类事的检查的时候**。
        //
        // 真实发生过：加上这条守卫之前，往领域文件里塞 `Guid.NewGuid()`
        // 与 `DateTimeOffset.UtcNow`，测试**两次都绿**。
        Assert.True(
            scanned.Count > 0,
            "一个领域层文件都没扫到——检查等于没跑，不能当作通过。"
                + $"（扫的是 {domainRoot}）");

        Assert.True(
            violations.Count == 0,
            $"领域层不得依赖环境态（不变量 6）。扫了 {scanned.Count} 个文件，出现了下列引用：\n  "
                + string.Join("\n  ", violations));
    }

    [Fact]
    public void ApplicationAssemblies_MustNotReferenceInfrastructure()
    {
        // **端口在里，实现在外。** *.Application 只允许引用自己的 Domain 与 BuildingBlocks.Application。
        //
        // 这条规则此前不存在，而它的缺位是有代价的：三个消息端口（IEventBus / IInboxStore / IOutboxStore）
        // 曾住在 BuildingBlocks.Infrastructure 里，于是 Auditing.Application **不得不**引用基础设施——
        // 那是整份引用矩阵里唯一一处 Application → Infrastructure，而没有任何东西发现它（评审 07 第二节）。
        //
        // 与领域白名单同一条道理：**先有守规则的测试，规则才存在。**
        var applicationPaths = SourceAssemblyPaths()
            .Where(static path => Path.GetFileNameWithoutExtension(path).EndsWith(ApplicationSuffix, StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(applicationPaths);

        var violations = new List<string>();

        // 第一层：**类型级**——读编译产物，看应用层是否真的引用了基础设施程序集。
        foreach (var path in applicationPaths)
        {
            var assemblyName = Path.GetFileNameWithoutExtension(path)!;
            var context = ContextOf(assemblyName) ?? "BuildingBlocks";

            foreach (var reference in SolutionAssemblies.ReferencedAssemblyNames(path))
            {
                if (reference.EndsWith(InfrastructureSuffix, StringComparison.Ordinal))
                {
                    violations.Add($"{assemblyName}（{context}）→ {reference}");
                }
            }
        }

        // 第二层：**工程级**——读 csproj，看是否埋着一颗"随时可以用"的引用。
        //
        // 两层缺一不可，这是实测出来的：C# 只为**实际用到**的程序集发出 AssemblyRef，
        // 所以一个没被使用的 ProjectReference 在编译产物里**不留任何痕迹**。
        // 只有第一层时，反向验证通不过——把引用注回去，测试照样全绿。
        foreach (var csproj in SolutionAssemblies.SourceProjectPaths("*.Application.csproj"))
        {
            var text = File.ReadAllText(csproj);
            foreach (System.Text.RegularExpressions.Match match in
                System.Text.RegularExpressions.Regex.Matches(text, "ProjectReference Include=\"([^\"]*Infrastructure[^\"]*)\""))
            {
                violations.Add($"{Path.GetFileName(csproj)} → {Path.GetFileNameWithoutExtension(match.Groups[1].Value)}（工程引用）");
            }
        }

        Assert.True(
            violations.Count == 0,
            "应用层不得依赖基础设施——端口在这里定义，适配器在外面组装。出现了下列引用：\n  "
                + string.Join("\n  ", violations));
    }

    [Fact]
    public void EveryServiceModule_IsWiredIntoAHost()
    {
        // **合并之后的新的失效模式**：加了一个模块却忘了在宿主里 Map，端点是**静默消失**的——
        // 编译通过、测试通过、启动正常，只是路由表里少了几条。没有任何东西会响。
        //
        // 这条检查是文本级的（读宿主的 Program.cs），比运行时验证弱一档，
        // 但它盯的正是最现实的失误：**新加一个模块时漏掉一行**。
        var servicesRoot = Path.Combine(SolutionAssemblies.RepositoryRoot, "src", "Services");
        var hostsRoot = Path.Combine(SolutionAssemblies.RepositoryRoot, "src", "Hosts");

        Assert.True(Directory.Exists(servicesRoot), "找不到 src/Services。");
        Assert.True(Directory.Exists(hostsRoot), "找不到 src/Hosts。");

        var modules = Directory
            .EnumerateDirectories(servicesRoot)
            .Select(Path.GetFileName)
            .Where(static name => name is not null)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // 检查了**零个**模块与"检查通过"必须能区分开。
        Assert.NotEmpty(modules);

        var hostSource = string.Join(
            "\n",
            Directory.EnumerateFiles(hostsRoot, "Program.cs", SearchOption.AllDirectories).Select(File.ReadAllText));

        Assert.False(string.IsNullOrWhiteSpace(hostSource), "src/Hosts 下没有读到任何 Program.cs。");

        var missing = new List<string>();
        foreach (var module in modules)
        {
            if (!hostSource.Contains($"Add{module}Module", StringComparison.Ordinal))
            {
                missing.Add($"{module}：宿主没有调用 Add{module}Module()");
            }

            if (!hostSource.Contains($"Map{module}Endpoints", StringComparison.Ordinal))
            {
                missing.Add($"{module}：宿主没有调用 Map{module}Endpoints()——它的端点在运行时会**静默消失**");
            }
        }

        Assert.True(
            missing.Count == 0,
            "有模块没有接进任何宿主：\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void DomainEntities_MustEncapsulateTheirState()
    {
        // 参照仓库的 23 个实体全是无行为 POCO，状态可以被任何调用方随手改。
        // 这条规则要求：实体/聚合根的属性不得有 public setter，状态只能通过领域方法改变。
        var domainPaths = SourceAssemblyPaths()
            .Where(static path => Path.GetFileNameWithoutExtension(path).EndsWith(DomainSuffix, StringComparison.Ordinal))
            .ToArray();

        var failures = new List<string>();
        foreach (var path in domainPaths)
        {
            var assembly = SolutionAssemblies.LoadFromTestOutput(Path.GetFileName(path));
            var result = Types.InAssembly(assembly)
                .Should()
                .MeetCustomRule(new DomainEntitiesMustEncapsulateState())
                .GetResult();

            if (!result.IsSuccessful)
            {
                failures.AddRange(result.FailingTypeNames ?? []);
            }
        }

        Assert.True(
            failures.Count == 0,
            "以下领域类型的属性带 public setter，状态可以被外部随意改：\n  " + string.Join("\n  ", failures));
    }

    [Fact]
    public void Contexts_MustNotReferenceOtherContexts()
    {
        // 不变量 1、2。五个上下文共 13 个程序集都在这条规则的射程内——它不再空转。
        // 而 SourceAssemblyInventory_MatchesDeclaredSet 确保新程序集的出现是有意识的行为，
        // 因此"规则覆盖了全部上下文"这件事本身也是被守住的。
        var violations = new List<string>();

        foreach (var path in SourceAssemblyPaths())
        {
            var assemblyName = Path.GetFileNameWithoutExtension(path);
            var context = ContextOf(assemblyName);
            if (context is null)
            {
                continue;
            }

            foreach (var reference in SolutionAssemblies.ReferencedAssemblyNames(path))
            {
                // **指向别人的 `*.Contracts` 是合法的**——不变量 2 说的正是这个。
                //
                // 这一条原来只写在下面第二层（csproj），于是**两层判据不一致**：
                // 真出现 `NexusStackNext.Identity.Contracts` 那天，引用它的上下文
                // 会在这一层被判违规——而那恰恰是不变量 2 允许的通信方式。
                if (reference.EndsWith(".Contracts", StringComparison.Ordinal))
                {
                    continue;
                }

                var referencedContext = ContextOf(reference);
                if (referencedContext is not null && !string.Equals(referencedContext, context, StringComparison.Ordinal))
                {
                    violations.Add($"{assemblyName}（{context}）→ {reference}（{referencedContext}）");
                }
            }
        }

        // 第二层：**工程级**——读 csproj。
        //
        // **这一层是第 23 轮 review 补的，因为上面那一层漏了整整一类引用。**
        // 上面读的是编译产物，而 C# 只为**实际用到**的程序集发出 AssemblyRef——
        // 一个挂在工程上、没有任何代码使用的 `ProjectReference` 在 DLL 里不留痕迹。
        //
        // 实测过：给 `Auditing.Domain` 加上指向 `Identity.Domain` 的引用
        // （一行 csproj，没有代码用它），解决方案**构建通过**，而这条测试**全绿**。
        //
        // 这是同一个洞的**第三次**出现：
        //   · `*.Application` → `*.Infrastructure`：早就有两层；
        //   · `*.Domain` 的包引用：评审 19 补上第二层；
        //   · 跨上下文引用：本轮补上——而它守着的是**不变量 1 与 2**，
        //     "上下文独占自己的数据"与"只通过 Contracts 通信"，八条里最靠前的两条。
        //
        // **同一类错误犯三次，说明该把它变成一条规矩，而不是做第三次修补**——
        // 规矩已经写在 `AGENTS.md` 的「写检查与做验证的纪律」里。
        // 取数走带守卫的版本（与 Domain / Application / Endpoints 三处一致）：
        // 枚举为空时它会**响亮地失败**，而不是"零个工程、零条违规、测试通过"。
        foreach (var csproj in SolutionAssemblies.SourceProjectPaths("NexusStackNext.*.csproj", "Services"))
        {
            var ownContext = ContextOf(Path.GetFileNameWithoutExtension(csproj));
            if (ownContext is null)
            {
                continue;
            }

            foreach (System.Text.RegularExpressions.Match match in
                System.Text.RegularExpressions.Regex.Matches(
                    File.ReadAllText(csproj),
                    "ProjectReference Include=\"[^\"]*?([^\\\\/\"]+)\\.csproj\""))
            {
                var referenced = match.Groups[1].Value;
                var referencedContext = ContextOf(referenced);

                if (referencedContext is null || string.Equals(referencedContext, ownContext, StringComparison.Ordinal))
                {
                    continue;
                }

                // **指向别人的 Contracts 是合法的**——不变量 2 说的正是这个。
                if (referenced.EndsWith(".Contracts", StringComparison.Ordinal))
                {
                    continue;
                }

                violations.Add($"{Path.GetFileName(csproj)} → {referenced}（工程引用；{ownContext} 引用 {referencedContext}）");
            }
        }

        Assert.True(
            violations.Count == 0,
            "上下文之间不得直接引用；请改为 *.Contracts 或集成事件（ADR-0004）：\n  "
                + string.Join("\n  ", violations));
    }

    [Fact]
    public void NoServiceLocator_StaticContainerHoldersAreForbidden()
    {
        // 不变量 5。参照仓库的 App.cs 持有 static IServiceProvider，正是它让 new User() 在单测里抛。
        var violations = new List<string>();

        foreach (var path in SourceAssemblyPaths())
        {
            var assembly = SolutionAssemblies.LoadFromTestOutput(Path.GetFileName(path));

            foreach (var type in assembly.GetTypes())
            {
                foreach (var field in type.GetFields(
                    System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    if (IsServiceProvider(field.FieldType))
                    {
                        violations.Add($"{type.FullName}.{field.Name}（静态字段）");
                    }
                }

                foreach (var property in type.GetProperties(
                    System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    if (IsServiceProvider(property.PropertyType))
                    {
                        violations.Add($"{type.FullName}.{property.Name}（静态属性）");
                    }
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "禁止静态服务定位器，请改用构造函数注入：\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void BuildingBlocks_MustNotGrowBeyondTheDeclaredSharedKernel()
    {
        var undeclared = SourceAssemblyPaths()
            .Select(Path.GetFileNameWithoutExtension)
            .Where(static name => name!.StartsWith("NexusStackNext.BuildingBlocks.", StringComparison.Ordinal))
            .Where(name => !DeclaredBuildingBlocks.Contains(name, StringComparer.Ordinal))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            undeclared.Length == 0,
            "BuildingBlocks 里出现了未声明的共享内核程序集（不变量 7：被第二个消费者证明需要才允许上移）：\n  "
                + string.Join("\n  ", undeclared));
    }

    [Fact]
    public void Hosts_MustComposeExplicitly()
    {
        // 不变量 8。参照仓库每个 Program.cs 只有 8 行，全部装配藏在 InitAppliation(moduleKey) 里，
        // 于是"这个服务由什么组成"在代码里读不出来。
        var sourceRoot = Path.Combine(SolutionAssemblies.RepositoryRoot, "src");
        var programFiles = Directory.Exists(sourceRoot)
            ? Directory
                .EnumerateFiles(sourceRoot, "Program.cs", SearchOption.AllDirectories)
                .Where(static path => !IsBuildArtifact(path))
                .ToArray()
            : [];

        // **守卫**：一个 `Program.cs` 都没扫到，与"每个宿主都显式组装"是两件事。
        // 少了这一句，这条检查在枚举为空时会**静默通过**——正是 AGENTS.md
        // 「写检查与做验证的纪律」第一条的形状，而这条检查此前恰好没有守卫。
        Assert.True(
            programFiles.Length > 0,
            "src/ 下一个 Program.cs 都没扫到——这条检查等于没跑，不能当作通过。"
                + "（多半是构建产物不在预期位置，而不是「真的没有宿主」）");

        var violations = new List<string>();
        foreach (var file in programFiles)
        {
            // 只看代码，不看注释：否则"记录一条禁止事项"本身就会触发这条规则
            // （这个假阳性真的发生过——Platform 的 Program.cs 注释里写了被禁的入口名）。
            var code = StripComments(File.ReadAllText(file));
            violations.AddRange(BannedCompositionCalls
                .Where(banned => code.Contains(banned, StringComparison.Ordinal))
                .Select(banned => $"{Path.GetRelativePath(SolutionAssemblies.RepositoryRoot, file)} 含 \"{banned}\""));
        }

        Assert.True(
            violations.Count == 0,
            "宿主必须显式组装自己，不得调用隐藏装配入口：\n  " + string.Join("\n  ", violations));
    }

    /// <summary>
    /// 取全部源码程序集，**并保证确实取到了**。
    ///
    /// <para><b>这条守卫是第 21 轮 review 补的，而且是补在源头上的。</b>
    /// 类里原本有 8 处直接调 <c>SourceAssemblyPaths()</c>，
    /// 其中 4 处的形状是"遍历 → 收集违规 → 断言违规为空"。
    /// 那种形状有一个安静的失效模式：**枚举出来是空的，违规就是空的，测试通过。**</para>
    ///
    /// <para>而它不只是理论：同一类写法在 <c>DomainAssemblies_MustNotReachForAmbientState</c>
    /// 里真的发生过——过滤器一个文件都没匹配上，往领域层塞 <c>Guid.NewGuid()</c> 与
    /// <c>UtcNow</c> 两次都绿。修好之后才发现判据本身也是错的。</para>
    ///
    /// <para>兄弟测试 <c>DomainAssemblies_MustNotReferenceAnythingOutsideTheAllowList</c>
    /// 早就有 <c>Assert.NotEmpty</c>——**同一个类里，有的地方有，有的地方没有。**
    /// 与其补四处，不如补在取数这一处：当前的与将来新增的调用点都受它保护。</para>
    /// </summary>
    /// <returns>源码程序集路径，保证非空。</returns>
    private static IReadOnlyList<string> SourceAssemblyPaths()
    {
        var paths = SolutionAssemblies.BuiltSourceAssemblyPaths();

        Assert.True(
            paths.Count > 0,
            "一个源码程序集都没找到——这组结构检查等于没跑，不能当作通过。"
                + "（多半是构建产物不在预期位置，而不是「项目里真的没有程序集」）");

        return paths;
    }

    /// <summary>
    /// 去掉行注释与块注释。
    /// <para>启发式实现：不解析字符串字面量，因此字符串里出现的被禁模式仍会命中——
    /// 这是**故意保守**的：宁可误报一次，也不要因为"它只是个字符串"而漏掉真正的调用。</para>
    /// </summary>
    /// <param name="source">源码文本。</param>
    /// <returns>去掉注释后的文本。</returns>
    private static string StripComments(string source)
    {
        var builder = new System.Text.StringBuilder(source.Length);
        var index = 0;

        while (index < source.Length)
        {
            if (source[index] == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                while (index < source.Length && source[index] != '\n')
                {
                    index++;
                }
            }
            else if (source[index] == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < source.Length && !(source[index] == '*' && source[index + 1] == '/'))
                {
                    index++;
                }

                index = Math.Min(source.Length, index + 2);
            }
            else
            {
                builder.Append(source[index]);
                index++;
            }
        }

        return builder.ToString();
    }

    private static bool IsBuildArtifact(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    /// <summary>共享内核的领域程序集——唯一允许被其他 <c>*.Domain</c> 引用的非 BCL 程序集。</summary>
    private const string SharedKernelDomainAssembly = "NexusStackNext.BuildingBlocks.Domain";

    private static bool IsAllowedInDomain(string ownAssemblyName, string reference) =>
        reference.StartsWith("System", StringComparison.Ordinal)
        || string.Equals(reference, "netstandard", StringComparison.OrdinalIgnoreCase)
        || string.Equals(reference, "mscorlib", StringComparison.OrdinalIgnoreCase)
        || string.Equals(reference, ownAssemblyName, StringComparison.OrdinalIgnoreCase)

        // 共享内核的领域基元（Entity / AggregateRoot / ValueObject / Result / IDomainEvent）。
        // 只放行这一个：BuildingBlocks.Application 与 BuildingBlocks.Infrastructure 不在白名单里，
        // 领域层依赖它们就是分层违规。
        || string.Equals(reference, SharedKernelDomainAssembly, StringComparison.OrdinalIgnoreCase);

    private static bool IsServiceProvider(Type type) =>
        string.Equals(type.FullName, "System.IServiceProvider", StringComparison.Ordinal);

    /// <summary>程序集名 → 上下文名。<c>BuildingBlocks</c> 与 <c>Gateway</c> 不是上下文。</summary>
    /// <param name="assemblyName">程序集简单名。</param>
    /// <returns>上下文名；不属于任何上下文时返回 <c>null</c>。</returns>
    private static string? ContextOf(string assemblyName)
    {
        var parts = assemblyName.Split('.');
        if (parts.Length < 3 || !string.Equals(parts[0], "NexusStackNext", StringComparison.Ordinal))
        {
            return null;
        }

        return parts[1] is "BuildingBlocks" or "Gateway" ? null : parts[1];
    }

    /// <summary>实体/聚合根的属性不得有 public setter。</summary>
    private sealed class DomainEntitiesMustEncapsulateState : ICustomRule
    {
        public bool MeetsRule(TypeDefinition type)
        {
            if (!IsEntity(type))
            {
                return true;
            }

            return !type.Properties
                .Where(static property => property.HasThis && property.Parameters.Count == 0)
                .Any(static property => property.SetMethod is { IsPublic: true });
        }

        private static bool IsEntity(TypeDefinition type)
        {
            var current = type.BaseType;
            while (current is not null)
            {
                var name = current.Name;
                if (name is "Entity`1" or "AggregateRoot`1")
                {
                    return true;
                }

                current = current.Resolve()?.BaseType;
            }

            return false;
        }
    }
}
