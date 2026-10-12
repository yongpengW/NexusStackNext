using Microsoft.EntityFrameworkCore;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Infrastructure.Persistence;

namespace NexusStackNext.Identity.Infrastructure;

internal sealed class MemoryRoleDirectory(IdentityMemoryState state) : IRoleDirectory
{
    public Task<IReadOnlyList<RoleDirectoryItem>> ReadAsync(long afterRoleId, int limit, string search, CancellationToken token)
    {
        using (state.Capacity.Enter(token))
        {
            IReadOnlyList<RoleDirectoryItem> rows = state.Data.Roles.Values
                .Where(role => !role.IsDeleted && role.Id.Value > afterRoleId && (role.Code.Value.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || role.Name.Value.Contains(search, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(role => role.Id.Value).Take(limit + 1)
                .Select(role => new RoleDirectoryItem(role.Id.Value, role.Code.Value, role.Name.Value, role.Version, role.IsEnabled)).ToArray();
            return Task.FromResult(rows);
        }
    }
}

internal sealed class EfRoleDirectory(IdentityDbContext context) : IRoleDirectory
{
    public async Task<IReadOnlyList<RoleDirectoryItem>> ReadAsync(long afterRoleId, int limit, string search, CancellationToken token)
    {
        // strpos treats % and _ as literal characters; the provider parameterizes every input.
        return await context.Database.SqlQuery<RoleDirectoryItem>($"""
            SELECT "Id" AS "RoleId", "Code", "Name", "Version", "IsEnabled"
            FROM identity.roles
            WHERE NOT "IsDeleted" AND "Id" > {afterRoleId}
                AND (strpos(lower("Code"), lower({search})) > 0 OR strpos(lower("Name"), lower({search})) > 0)
            ORDER BY "Id" LIMIT {limit + 1}
            """).ToListAsync(token).ConfigureAwait(false);
    }
}
