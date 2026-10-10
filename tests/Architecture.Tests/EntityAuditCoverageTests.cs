using System.Reflection;
using NexusStackNext.Auditing.Domain.Entries;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Costing.Domain;
using NexusStackNext.Files.Domain.Stored;
using NexusStackNext.Identity.Domain.ApiResources;
using NexusStackNext.Identity.Domain.Menus;
using NexusStackNext.Identity.Domain.Roles;
using NexusStackNext.Identity.Domain.Tokens;
using NexusStackNext.Identity.Domain.Users;
using NexusStackNext.Platform.Domain.Settings;
using NexusStackNext.Pricing.Domain;
using NexusStackNext.Scheduling.Domain.Tasks;

namespace NexusStackNext.Architecture.Tests;

public sealed class EntityAuditCoverageTests
{
    private sealed record FactObligation(string Decision, string[] Changes, string[] QueriesOrLifecycle);

    // 业务事实行为证据见 docs/committed-audit-coverage.md；登记本身不证明实现正确。
    private static readonly Dictionary<Type, FactObligation> FactDecisions = new()
    {
        [typeof(GlobalSetting)] = new("SettingCommittedV1；创建及值/说明变化随所属事务提交。Snapshot只读。",
            [nameof(GlobalSetting.Create), nameof(GlobalSetting.ChangeValue), nameof(GlobalSetting.Describe)], [nameof(GlobalSetting.Snapshot)]),
        [typeof(User)] = new("IdentityEntityCommittedV1；包含失败计数与锁定，拒绝登录也可能提交变化。认证判定及Snapshot只读。",
            [nameof(User.Register), nameof(User.AssignRole), nameof(User.RevokeRole), nameof(User.ChangePassword), nameof(User.SetContact),
                nameof(User.Enable), nameof(User.Disable), nameof(User.RevokeSessions), nameof(User.RecordSuccessfulLogin), nameof(User.RecordFailedLogin)],
            [nameof(User.EnsureCanAuthenticate), nameof(User.Snapshot)]),
        [typeof(Role)] = new("IdentityEntityCommittedV1；创建、名称/平台及菜单授权成员变化。EnsureDeletable只验证，Snapshot只读。",
            [nameof(Role.Create), nameof(Role.CreateSystem), nameof(Role.Rename), nameof(Role.ChangePlatforms), nameof(Role.Grant), nameof(Role.Revoke), nameof(Role.ReplaceGrants)],
            [nameof(Role.EnsureDeletable), nameof(Role.Snapshot)]),
        [typeof(MenuTree)] = new("IdentityEntityCommittedV1；树及节点变化。查询与Snapshot只读。",
            [nameof(MenuTree.Create), nameof(MenuTree.AddRoot), nameof(MenuTree.AddChild), nameof(MenuTree.Move), nameof(MenuTree.Remove), nameof(MenuTree.Update)],
            [nameof(MenuTree.Find), nameof(MenuTree.ChildrenOf), nameof(MenuTree.DescendantsOf), nameof(MenuTree.Snapshot)]),
        [typeof(ApiResource)] = new("IdentityEntityCommittedV1；资源登记及所属Menu关联。Snapshot只读。",
            [nameof(ApiResource.Create)], [nameof(ApiResource.Snapshot)]),
        [typeof(RefreshToken)] = new("IdentityEntityCommittedV1；签发、消费与撤销，不包含令牌或其哈希。IsUsable及Snapshot只读。",
            [nameof(RefreshToken.Issue), nameof(RefreshToken.Consume), nameof(RefreshToken.Revoke)], [nameof(RefreshToken.IsUsable), nameof(RefreshToken.Snapshot)]),
        [typeof(StoredFile)] = new("StoredFileCommittedV1；普通文件及候选登记、存储、发布/到期、删除与清理。Snapshot只读。",
            [nameof(StoredFile.Register), nameof(StoredFile.RegisterCandidate), nameof(StoredFile.SealCandidate), nameof(StoredFile.MarkStored),
                nameof(StoredFile.PublishCandidate), nameof(StoredFile.ExpireCandidate), nameof(StoredFile.Delete), nameof(StoredFile.ConfirmBytesRemoved), nameof(StoredFile.PostponeCleanup)],
            [nameof(StoredFile.Snapshot)]),
        [typeof(ScheduledTask)] = new("PlanCommittedV1；计划管理、触发推进及退避决定。IsDue及Snapshot只读；两个Create重载分别登记。",
            [nameof(ScheduledTask.Create), nameof(ScheduledTask.Create), nameof(ScheduledTask.Defer), nameof(ScheduledTask.MarkTriggered), nameof(ScheduledTask.Advance),
                nameof(ScheduledTask.ChangeInterval), nameof(ScheduledTask.ChangeRule), nameof(ScheduledTask.Enable), nameof(ScheduledTask.EnableAt), nameof(ScheduledTask.Disable)],
            [nameof(ScheduledTask.IsDue), nameof(ScheduledTask.Snapshot)]),
        [typeof(CostSheet)] = new("CostSheetCommittedV1；输入与结果实际变化。Calculate与输入验证不修改状态。",
            [nameof(CostSheet.Create), nameof(CostSheet.UpdateCost), nameof(CostSheet.ApplyCalculation)],
            [nameof(CostSheet.Calculate), nameof(CostSheet.IsValidInput), nameof(CostSheet.IsValidAmount)]),
        [typeof(PriceQuote)] = new("PriceQuoteCommittedV1；输入、上游成本与结果实际变化。Calculate与输入验证不修改状态。",
            [nameof(PriceQuote.Create), nameof(PriceQuote.UpdateCost), nameof(PriceQuote.ApplyCostingCost), nameof(PriceQuote.ApplyCalculation)],
            [nameof(PriceQuote.Calculate), nameof(PriceQuote.IsValidInput)]),
        [typeof(PricingExport)] = new("技术生命周期例外：导出委托保留自身状态/版本、冻结快照与发布裁决，不冒充报价提交；操作仍被观察，Files另存成果事实。", [],
            [nameof(PricingExport.Accept), nameof(PricingExport.Cancel), nameof(PricingExport.TryClaim), nameof(PricingExport.Renew), nameof(PricingExport.SelectPublication),
                nameof(PricingExport.RecordPublicationProgress), nameof(PricingExport.CompletePublication), nameof(PricingExport.FailGeneration), nameof(PricingExport.RetryGeneration)]),
        [typeof(AuditEntry)] = new("不可变审计记录例外：中央Inbox/指纹与接收时间证明摄入，不对审计写入递归生成事实。", [], [nameof(AuditEntry.Record)]),
    };

    [Fact]
    public void EveryAggregate_MustDeclareItsCommittedFactObligation()
    {
        var aggregates = SolutionAssemblies.SourceProjectPaths("*.Domain.csproj", "Services")
            .Select(path => SolutionAssemblies.LoadFromTestOutput(Path.GetFileNameWithoutExtension(path) + ".dll"))
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IHasDomainEvents).IsAssignableFrom(type))
            .ToArray();
        Assert.NotEmpty(aggregates);
        var missing = aggregates.Except(FactDecisions.Keys).Select(type => type.FullName).ToArray();
        var stale = FactDecisions.Keys.Except(aggregates).Select(type => type.FullName).ToArray();
        Assert.True(missing.Length == 0 && stale.Length == 0,
            "未登记业务事实义务: " + string.Join(", ", missing) + "; 已失去聚合的登记: " + string.Join(", ", stale));
        Assert.All(FactDecisions.Values, obligation => Assert.False(string.IsNullOrWhiteSpace(obligation.Decision)));
    }

    [Fact]
    public void PublicAggregateEntrypoints_MustDeclareStateChangeOrQueryAndLifecycle()
    {
        Assert.NotEmpty(FactDecisions);
        var violations = new List<string>();
        foreach (var (type, obligation) in FactDecisions)
        {
            var actual = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(method => !method.IsSpecialName).Select(method => method.Name)
                .Concat(type.GetConstructors().Select(_ => ".ctor")).Order(StringComparer.Ordinal).ToArray();
            Assert.NotEmpty(actual);
            var declared = obligation.Changes.Concat(obligation.QueriesOrLifecycle).Order(StringComparer.Ordinal).ToArray();
            if (!actual.SequenceEqual(declared))
            {
                violations.Add(type.Name + ": 登记 [" + string.Join(", ", declared) + "]; 实际 [" + string.Join(", ", actual) + "]");
            }
        }
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

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
