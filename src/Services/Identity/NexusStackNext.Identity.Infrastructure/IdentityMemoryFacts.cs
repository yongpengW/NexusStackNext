using System.Diagnostics;
using System.Globalization;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.Identity.Contracts;

namespace NexusStackNext.Identity.Infrastructure;

// Memory 的提交差异来自独立快照，不能把命令成功或工作副本中的领域事件当作已提交事实。
internal sealed class IdentityMemoryFacts(IClock clock, IIntegrationEventSerializer serializer,
    ICurrentUser? currentUser = null, IExecutionContext? execution = null)
{
    internal IReadOnlyList<OutboxEntry> Create(IdentityMemoryData before, IdentityMemoryData after)
    {
        var facts = new List<OutboxEntry>();
        var origin = execution?.Capture();
        var trace = origin?.TraceId ?? Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        void Add(string type, long id, long version, string operation, IdentitySubjectReference? related = null) =>
            facts.Add(OutboxEntry.From(new IdentityEntityCommittedV1
            {
                SubjectType = type,
                SubjectId = id.ToString(CultureInfo.InvariantCulture),
                Version = version,
                Operation = operation,
                RelatedSubject = related,
                OccurredAt = clock.UtcNow,
                ActorId = execution?.IsSystem == true ? null : currentUser?.UserId,
                TraceId = trace,
                CorrelationId = origin?.CorrelationId ?? trace,
                Execution = origin,
            }, serializer));

        foreach (var user in after.Users.Values)
        {
            var old = before.Users.GetValueOrDefault(user.Id);
            if (old?.Version == user.Version) { continue; }
            void UserFact(string action, IdentitySubjectReference? related = null) => Add("user", user.Id.Value, user.Version, action, related);
            if (old is null) { UserFact("created"); }
            foreach (var role in user.RoleIds.Except(old?.RoleIds ?? [])) { UserFact("role-assigned", new("role", role.Value)); }
            foreach (var role in (old?.RoleIds ?? []).Except(user.RoleIds)) { UserFact("role-revoked", new("role", role.Value)); }
            if (old is null) { continue; }
            if (user.FailedLoginCount > old.FailedLoginCount) { UserFact(user.LockedUntil != old.LockedUntil ? "login-locked" : "login-failed"); }
            // 启用也会清除失败计数和锁定；只有启用状态未改变时，这些重置才证明同时间戳登录。
            if (user.LastLoginAt != old.LastLoginAt
                || (user.IsEnabled == old.IsEnabled && (user.FailedLoginCount < old.FailedLoginCount
                    || (user.LockedUntil is null && old.LockedUntil is not null)))) { UserFact("login-succeeded"); }
            if (user.SessionVersion != old.SessionVersion) { UserFact("sessions-revoked"); }
            if (user.Email != old.Email || user.Phone != old.Phone) { UserFact("contact-changed"); }
            if (user.PasswordHash != old.PasswordHash) { UserFact("password-changed"); }
            if (user.IsEnabled != old.IsEnabled) { UserFact(user.IsEnabled ? "enabled" : "disabled"); }
        }
        foreach (var role in after.Roles.Values)
        {
            var old = before.Roles.GetValueOrDefault(role.Id);
            if (old?.Version == role.Version) { continue; }
            void RoleFact(string action, IdentitySubjectReference? related = null) => Add("role", role.Id.Value, role.Version, action, related);
            if (old is null) { RoleFact("created"); }
            foreach (var menu in role.GrantedMenuIds.Except(old?.GrantedMenuIds ?? [])) { RoleFact("menu-granted", new("menu", menu.Value)); }
            foreach (var menu in (old?.GrantedMenuIds ?? []).Except(role.GrantedMenuIds)) { RoleFact("menu-revoked", new("menu", menu.Value)); }
            if (old is null) { continue; }
            if (role.Name != old.Name) { RoleFact("renamed"); }
            if (role.Platforms != old.Platforms) { RoleFact("platforms-changed"); }
        }
        foreach (var tree in after.Trees.Values)
        {
            var old = before.Trees.GetValueOrDefault(tree.Id);
            if (old?.Version == tree.Version) { continue; }
            if (old is null) { Add("menu-tree", tree.Id.Value, tree.Version, "created"); }
            foreach (var node in tree.Nodes)
            {
                var prior = old?.Find(node.Id);
                void NodeFact(string action) => Add("menu-tree", tree.Id.Value, tree.Version, action, new("menu", node.Id.Value));
                if (prior is null) { NodeFact("node-added"); continue; }
                if (node.ParentId != prior.ParentId) { NodeFact("node-moved"); }
                else if (node.Path != prior.Path) { NodeFact("node-ancestry-changed"); }
                if (node.Title != prior.Title) { NodeFact("node-renamed"); }
                if (node.SortOrder != prior.SortOrder) { NodeFact("node-reordered"); }
            }
            foreach (var node in old?.Nodes ?? [])
            {
                if (tree.Find(node.Id) is null) { Add("menu-tree", tree.Id.Value, tree.Version, "node-removed", new("menu", node.Id.Value)); }
            }
        }
        foreach (var resource in after.Resources.Values)
        {
            if (!before.Resources.ContainsKey(resource.Id))
            {
                Add("api-resource", resource.Id.Value, resource.Version, "registered", resource.MenuId is { } menu ? new("menu", menu.Value) : null);
            }
        }
        foreach (var token in after.Tokens.Values)
        {
            var old = before.Tokens.GetValueOrDefault(token.Id);
            if (old?.Version == token.Version) { continue; }
            void TokenFact(string action) => Add("refresh-token", token.Id.Value, token.Version, action, new("user", token.UserId.Value));
            if (old is null) { TokenFact("issued"); continue; }
            if (token.ConsumedAt != old.ConsumedAt) { TokenFact("consumed"); }
            if (token.RevokedAt != old.RevokedAt) { TokenFact("revoked"); }
        }
        return facts;
    }
}
