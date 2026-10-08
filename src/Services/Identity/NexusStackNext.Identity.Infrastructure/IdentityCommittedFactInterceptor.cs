using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Identity.Contracts;
using NexusStackNext.Identity.Domain.ApiResources;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Menus;
using NexusStackNext.Identity.Domain.Roles;
using NexusStackNext.Identity.Domain.Tokens;
using NexusStackNext.Identity.Domain.Users;
using NexusStackNext.Identity.Infrastructure.Persistence;

namespace NexusStackNext.Identity.Infrastructure;

// 只检查 Identity 拥有的持久变化，不序列化领域事件或 EF 属性值。
internal sealed class IdentityCommittedFactInterceptor(IClock clock, IIntegrationEventSerializer serializer,
    ICurrentUser? currentUser = null, IExecutionContext? execution = null) : CommittedFactInterceptor<IdentityDbContext>
{
    private sealed record CommittedChange(string Type, long Id, long Version, string Operation, IdentitySubjectReference? RelatedSubject = null);

    protected override IReadOnlyList<OutboxEntry> CreateFacts(IdentityDbContext identity)
    {
        var changes = new List<CommittedChange>();
        var changedUserRoles = identity.ChangeTracker.Entries<RoleId>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Deleted)
            .ToLookup(entry => entry.Property<UserId>("user_id").CurrentValue);
        var users = identity.ChangeTracker.Entries<User>().ToArray();
        foreach (var entry in users)
        {
            if (entry.State == EntityState.Added)
            {
                changes.Add(new("user", entry.Entity.Id.Value, entry.Entity.Version, "created"));
            }
            else if (entry.State != EntityState.Modified || entry.Entity.Version == entry.Property(user => user.Version).OriginalValue) { continue; }
            if (entry.Entity.FailedLoginCount > entry.Property(user => user.FailedLoginCount).OriginalValue)
            {
                changes.Add(new("user", entry.Entity.Id.Value, entry.Entity.Version,
                    entry.Property(user => user.LockedUntil).IsModified ? "login-locked" : "login-failed"));
            }
            foreach (var assignmentGroup in changedUserRoles[entry.Entity.Id].GroupBy(item => item.Entity.Value).OrderBy(group => group.Key))
            {
                if (assignmentGroup.Any(item => item.State == EntityState.Added) && assignmentGroup.Any(item => item.State == EntityState.Deleted)) { continue; }
                var assignment = assignmentGroup.First();
                changes.Add(new("user", entry.Entity.Id.Value, entry.Entity.Version,
                    assignment.State == EntityState.Added ? "role-assigned" : "role-revoked", new("role", assignment.Entity.Value)));
            }
            // 启用也会清除失败计数和锁定；只有启用状态未改变时，这些重置才证明同时间戳登录。
            if (entry.Property(user => user.LastLoginAt).IsModified
                || (!entry.Property(user => user.IsEnabled).IsModified
                    && (entry.Entity.FailedLoginCount < entry.Property(user => user.FailedLoginCount).OriginalValue
                        || (entry.Entity.LockedUntil is null && entry.Property(user => user.LockedUntil).OriginalValue is not null))))
            {
                changes.Add(new("user", entry.Entity.Id.Value, entry.Entity.Version, "login-succeeded"));
            }
            if (entry.Property(user => user.SessionVersion).IsModified)
            {
                changes.Add(new("user", entry.Entity.Id.Value, entry.Entity.Version, "sessions-revoked"));
            }
            if (entry.Property(user => user.Email).IsModified || entry.Property(user => user.Phone).IsModified)
            {
                changes.Add(new("user", entry.Entity.Id.Value, entry.Entity.Version, "contact-changed"));
            }
            if (entry.Property(user => user.PasswordHash).IsModified)
            {
                changes.Add(new("user", entry.Entity.Id.Value, entry.Entity.Version, "password-changed"));
            }
            if (entry.Property(user => user.IsEnabled).IsModified)
            {
                changes.Add(new("user", entry.Entity.Id.Value, entry.Entity.Version, entry.Entity.IsEnabled ? "enabled" : "disabled"));
            }
        }
        var changedRoleMenus = identity.ChangeTracker.Entries<MenuId>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Deleted)
            .ToLookup(entry => entry.Property<RoleId>("role_id").CurrentValue);
        foreach (var entry in identity.ChangeTracker.Entries<Role>().ToArray())
        {
            if (entry.State == EntityState.Added)
            {
                changes.Add(new("role", entry.Entity.Id.Value, entry.Entity.Version, "created"));
            }
            else if (entry.State != EntityState.Modified || entry.Entity.Version == entry.Property(role => role.Version).OriginalValue) { continue; }
            foreach (var permissionGroup in changedRoleMenus[entry.Entity.Id].GroupBy(item => item.Entity.Value).OrderBy(group => group.Key))
            {
                // 替换集合可能把相同标识先删后加；成员关系未变，不能伪造撤权再授权。
                if (permissionGroup.Any(item => item.State == EntityState.Added) && permissionGroup.Any(item => item.State == EntityState.Deleted)) { continue; }
                var permission = permissionGroup.First();
                changes.Add(new("role", entry.Entity.Id.Value, entry.Entity.Version,
                    permission.State == EntityState.Added ? "menu-granted" : "menu-revoked", new("menu", permission.Entity.Value)));
            }
            if (entry.Property(role => role.Name).IsModified)
            {
                changes.Add(new("role", entry.Entity.Id.Value, entry.Entity.Version, "renamed"));
            }
            if (entry.Property(role => role.Platforms).IsModified)
            {
                changes.Add(new("role", entry.Entity.Id.Value, entry.Entity.Version, "platforms-changed"));
            }
        }
        foreach (var entry in identity.ChangeTracker.Entries<RefreshToken>().ToArray())
        {
            if (entry.State == EntityState.Added)
            {
                changes.Add(new("refresh-token", entry.Entity.Id.Value, entry.Entity.Version, "issued", new("user", entry.Entity.UserId.Value)));
            }
            else if (entry.State == EntityState.Modified && entry.Entity.Version != entry.Property(token => token.Version).OriginalValue)
            {
                if (entry.Property(token => token.ConsumedAt).IsModified)
                {
                    changes.Add(new("refresh-token", entry.Entity.Id.Value, entry.Entity.Version, "consumed", new("user", entry.Entity.UserId.Value)));
                }
                if (entry.Property(token => token.RevokedAt).IsModified)
                {
                    changes.Add(new("refresh-token", entry.Entity.Id.Value, entry.Entity.Version, "revoked", new("user", entry.Entity.UserId.Value)));
                }
            }
        }
        var changedNodes = identity.ChangeTracker.Entries<MenuNode>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToLookup(entry => entry.Property<MenuTreeId>("menu_tree_id").CurrentValue);
        foreach (var entry in identity.ChangeTracker.Entries<MenuTree>().ToArray())
        {
            if (entry.State == EntityState.Added) { changes.Add(new("menu-tree", entry.Entity.Id.Value, entry.Entity.Version, "created")); }
            else if (entry.State != EntityState.Modified || entry.Entity.Version == entry.Property(tree => tree.Version).OriginalValue) { continue; }
            foreach (var group in changedNodes[entry.Entity.Id].GroupBy(item => item.Entity.Id.Value).OrderBy(item => item.Key))
            {
                // 同一节点可以被删后重建；比较提交前后内容，不能把存储替换当作业务增删。
                var before = group.FirstOrDefault(item => item.State == EntityState.Deleted)
                    ?? group.FirstOrDefault(item => item.State == EntityState.Modified);
                var after = group.FirstOrDefault(item => item.State == EntityState.Added)
                    ?? group.FirstOrDefault(item => item.State == EntityState.Modified);
                var related = new IdentitySubjectReference("menu", group.Key);
                if (before is null || after is null)
                {
                    changes.Add(new("menu-tree", entry.Entity.Id.Value, entry.Entity.Version,
                        before is null ? "node-added" : "node-removed", related));
                    continue;
                }
                if (!Equals(before.Property(item => item.ParentId).OriginalValue, after.Entity.ParentId))
                {
                    changes.Add(new("menu-tree", entry.Entity.Id.Value, entry.Entity.Version, "node-moved", related));
                }
                else if (!Equals(before.Property(item => item.Path).OriginalValue, after.Entity.Path))
                {
                    changes.Add(new("menu-tree", entry.Entity.Id.Value, entry.Entity.Version, "node-ancestry-changed", related));
                }
                if (!Equals(before.Property(item => item.Title).OriginalValue, after.Entity.Title))
                {
                    changes.Add(new("menu-tree", entry.Entity.Id.Value, entry.Entity.Version, "node-renamed", related));
                }
                if (before.Property(item => item.SortOrder).OriginalValue != after.Entity.SortOrder)
                {
                    changes.Add(new("menu-tree", entry.Entity.Id.Value, entry.Entity.Version, "node-reordered", related));
                }
            }
        }
        foreach (var entry in identity.ChangeTracker.Entries<ApiResource>().Where(entry => entry.State == EntityState.Added).ToArray())
        {
            changes.Add(new("api-resource", entry.Entity.Id.Value, entry.Entity.Version, "registered",
                entry.Entity.MenuId is { } menuId ? new("menu", menuId.Value) : null));
        }
        return changes.Select(change =>
        {
            var origin = execution?.Capture();
            var trace = origin?.TraceId ?? Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
            var fact = new IdentityEntityCommittedV1
            {
                SubjectType = change.Type,
                SubjectId = change.Id.ToString(CultureInfo.InvariantCulture),
                Operation = change.Operation,
                Version = change.Version,
                ActorId = execution?.IsSystem == true ? null : currentUser?.UserId,
                OccurredAt = clock.UtcNow,
                TraceId = trace,
                CorrelationId = origin?.CorrelationId ?? trace,
                Execution = origin,
                RelatedSubject = change.RelatedSubject,
            };
            return OutboxEntry.From(fact, serializer);
        }).ToArray();
    }
}
