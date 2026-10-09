using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Architecture.Tests;

public sealed class EntityAuditCoverageTests
{
    [Fact]
    public void BusinessAggregates_MustDeclareRowAuditOrAnExplicitLifecycle()
    {
        var projects = SolutionAssemblies.SourceProjectPaths("*.Domain.csproj", "Services");
        Assert.Equal(7, projects.Count);
        var aggregates = projects.Select(path => SolutionAssemblies.LoadFromTestOutput(Path.GetFileNameWithoutExtension(path) + ".dll"))
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IHasDomainEvents).IsAssignableFrom(type))
            .ToArray();
        Assert.NotEmpty(aggregates);

        // 完整操作事实与令牌的生命周期已经明确表达时间/归属，不伪造可编辑业务行语义。
        string[] lifecycleTypes =
        [
            "NexusStackNext.Auditing.Domain.Entries.AuditEntry",
            "NexusStackNext.Identity.Domain.Tokens.RefreshToken",
        ];
        foreach (var lifecycle in lifecycleTypes)
        {
            Assert.Contains(aggregates, type => type.FullName == lifecycle);
        }
        var business = aggregates.Where(type => !lifecycleTypes.Contains(type.FullName, StringComparer.Ordinal)).ToArray();
        Assert.Equal(10, business.Length);
        Assert.All(business, type => Assert.True(typeof(IAuditedEntity).IsAssignableFrom(type), type.FullName + " 缺少行审计契约。"));
    }
}
