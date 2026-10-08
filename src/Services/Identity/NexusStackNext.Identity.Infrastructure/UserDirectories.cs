using Microsoft.EntityFrameworkCore;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Users;
using NexusStackNext.Identity.Infrastructure.Persistence;

namespace NexusStackNext.Identity.Infrastructure;

internal sealed class MemoryUserDirectory(IdentityMemoryState state) : IUserDirectory
{
    public Task<UserView?> FindAsync(long userId, CancellationToken token)
    {
        using (state.Capacity.Enter(token))
        {
            var user = state.Data.Users.GetValueOrDefault(new UserId(userId));
            return Task.FromResult(user is null ? null : View(user));
        }
    }

    public Task<IReadOnlyList<UserView>> ReadAsync(long afterUserId, int limit, CancellationToken token)
    {
        using (state.Capacity.Enter(token))
        {
            IReadOnlyList<UserView> rows = state.Data.Users.Values.Where(u => u.Id.Value > afterUserId)
                .OrderBy(u => u.Id.Value).Take(limit + 1).Select(View).ToArray();
            return Task.FromResult(rows);
        }
    }

    private static UserView View(User user) => new(user.Id.Value, user.UserName.Value, user.IsEnabled,
        user.RoleIds.Select(r => r.Value).Order().ToArray(), user.Version);
}

internal sealed class EfUserDirectory(IdentityDbContext context) : IUserDirectory
{
    public async Task<UserView?> FindAsync(long userId, CancellationToken token)
        => (await ProjectAsync(userId, 0, 1, token).ConfigureAwait(false)).SingleOrDefault();

    public Task<IReadOnlyList<UserView>> ReadAsync(long afterUserId, int limit, CancellationToken token)
        => ProjectAsync(0, afterUserId, limit + 1, token);

    private async Task<IReadOnlyList<UserView>> ProjectAsync(long userId, long after, int count, CancellationToken token)
    {
        var rows = await context.Database.SqlQuery<DirectoryRow>($"""
            SELECT u."Id" AS "UserId", u."UserName", u."IsEnabled", u."Version",
                ARRAY(SELECT r.role_id FROM identity.user_roles r WHERE r.user_id = u."Id" ORDER BY r.role_id) AS "RoleIds"
            FROM identity.users u
            WHERE ({userId} = 0 OR u."Id" = {userId}) AND u."Id" > {after}
            ORDER BY u."Id" LIMIT {count}
            """).ToListAsync(token).ConfigureAwait(false);
        return rows.Select(u => new UserView(u.UserId, u.UserName, u.IsEnabled, u.RoleIds, u.Version)).ToArray();
    }

    private sealed record DirectoryRow(long UserId, string UserName, bool IsEnabled, long[] RoleIds, long Version);
}
