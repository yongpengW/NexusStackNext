using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Contracts;
using NexusStackNext.Identity.Domain.ApiResources;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Menus;
using NexusStackNext.Identity.Domain.Roles;
using NexusStackNext.Identity.Domain.Tokens;
using NexusStackNext.Identity.Domain.Users;

namespace NexusStackNext.Identity.Infrastructure;

internal sealed class IdentityMemoryConflictException(string message) : InvalidOperationException(message);

internal sealed class IdentityMemoryState : ICommittedFactCapacityReader
{
    internal IdentityMemoryState(MemoryCommittedFactCapacityOptions? capacity = null)
    {
        Capacity = new(Gate, IdentityEntityCommittedV1.Name, capacity);
    }

    internal InMemoryCommittedFactCapacity Capacity { get; }
    internal IdentityMemoryData Data { get; set; } = new();
    internal Lock Gate { get; } = new();
    internal Dictionary<Guid, OutboxEntry> Outbox { get; set; } = [];

    public Task<Result<CommittedFactCapacitySnapshot>> ReadAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Capacity.Read("identity", cancellationToken));
}

internal sealed class IdentityMemoryData
{
    internal Dictionary<UserId, User> Users { get; init; } = [];
    internal Dictionary<RoleId, Role> Roles { get; init; } = [];
    internal Dictionary<MenuTreeId, MenuTree> Trees { get; init; } = [];
    internal Dictionary<ApiResourceId, ApiResource> Resources { get; init; } = [];
    internal Dictionary<RefreshTokenId, RefreshToken> Tokens { get; init; } = [];

    internal IdentityMemoryData Snapshot() => new()
    {
        Users = Users.ToDictionary(pair => pair.Key, pair => pair.Value.Snapshot()),
        Roles = Roles.ToDictionary(pair => pair.Key, pair => pair.Value.Snapshot()),
        Trees = Trees.ToDictionary(pair => pair.Key, pair => pair.Value.Snapshot()),
        Resources = Resources.ToDictionary(pair => pair.Key, pair => pair.Value.Snapshot()),
        Tokens = Tokens.ToDictionary(pair => pair.Key, pair => pair.Value.Snapshot()),
    };
}

// 一个调用作用域拥有工作副本；共享状态只在最终提交时替换，读者从不拿到共享可变对象。
internal sealed class IdentityMemorySession(IdentityMemoryState state, IdentityMemoryFacts facts, IClock clock,
    ICurrentUser? currentUser = null, IExecutionContext? execution = null)
{
    private IdentityMemoryData? _original;
    private IdentityMemoryData? _working;
    private IdentityMemoryData? _saved;
    private bool _transaction;

    internal IdentityMemoryData Data(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_working is not null) { return _working; }
        lock (state.Gate)
        {
            _original = state.Data;
            return _working = _original.Snapshot();
        }
    }

    internal Task<int> SaveAsync(CancellationToken token)
    {
        try
        {
            var saved = Data(token).Snapshot();
            var changed = Changes(saved.Users, _original!.Users) + Changes(saved.Roles, _original.Roles)
                + Changes(saved.Trees, _original.Trees) + Changes(saved.Resources, _original.Resources) + Changes(saved.Tokens, _original.Tokens);
            if (_transaction) { _saved = saved; }
            else
            {
                Commit(saved, token);
                CopyAudit(saved.Users, _working!.Users);
                CopyAudit(saved.Roles, _working.Roles);
                CopyAudit(saved.Trees, _working.Trees);
                CopyAudit(saved.Resources, _working.Resources);
                _original = saved.Snapshot();
            }
            return Task.FromResult(changed);
        }
        catch (OperationCanceledException) { Reset(); throw; }
        catch (IdentityMemoryConflictException) { Reset(); throw; }
        catch { _saved = null; throw; }
    }

    internal async Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation,
        Func<TResult, bool>? shouldCommit, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (_transaction) { throw new InvalidOperationException("Identity Memory 不支持嵌套事务。"); }
        _transaction = true;
        try
        {
            token.ThrowIfCancellationRequested();
            var result = await operation(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if ((shouldCommit?.Invoke(result) ?? true) && _saved is not null) { Commit(_saved, token); }
            return result;
        }
        finally { Reset(); _transaction = false; }
    }

    private void Commit(IdentityMemoryData saved, CancellationToken token)
    {
        lock (state.Gate)
        {
            var next = state.Data.Snapshot();
            Merge(saved.Users, _original!.Users, next.Users);
            Merge(saved.Roles, _original.Roles, next.Roles);
            Merge(saved.Trees, _original.Trees, next.Trees);
            Merge(saved.Resources, _original.Resources, next.Resources);
            Merge(saved.Tokens, _original.Tokens, next.Tokens);
            if (next.Users.Values.GroupBy(user => user.UserName).Any(group => group.Count() > 1)
                || next.Users.Values.Where(user => user.Email is not null).GroupBy(user => user.Email).Any(group => group.Count() > 1)
                || next.Users.Values.Where(user => user.Phone is not null).GroupBy(user => user.Phone).Any(group => group.Count() > 1)
                || next.Roles.Values.GroupBy(role => role.Code).Any(group => group.Count() > 1)
                || next.Resources.Values.GroupBy(resource => (resource.RoutePattern, resource.HttpMethod)).Any(group => group.Count() > 1)
                || next.Tokens.Values.GroupBy(refresh => refresh.TokenHash).Any(group => group.Count() > 1)
                || next.Trees.Count > 1)
            {
                throw new IdentityMemoryConflictException("Identity Memory 唯一约束冲突。");
            }
            token.ThrowIfCancellationRequested();
            var batch = facts.Create(_original, saved);
            if (!state.Capacity.TryCommit(batch, () =>
            {
                var nextOutbox = new Dictionary<Guid, OutboxEntry>(state.Outbox);
                foreach (var fact in batch) { nextOutbox.Add(fact.Id, fact); }
                token.ThrowIfCancellationRequested();
                state.Data = next;
                state.Outbox = nextOutbox;
            }, token)) { throw new IdentityAuditCapacityException(); }
        }
    }

    private void Reset() { _original = null; _working = null; _saved = null; }
    private static int Changes<TId, T>(Dictionary<TId, T> saved, Dictionary<TId, T> original)
        where TId : notnull where T : AggregateRoot<TId> =>
        saved.Values.Count(item => !original.TryGetValue(item.Id, out var before) || item.Version != before.Version);

    private void Merge<TId, T>(Dictionary<TId, T> saved, Dictionary<TId, T> original, Dictionary<TId, T> next)
        where TId : notnull where T : AggregateRoot<TId>
    {
        foreach (var item in saved.Values)
        {
            var before = original.GetValueOrDefault(item.Id);
            if (before?.Version == item.Version)
            {
                if (before is IAuditedEntity priorAudit && item is IAuditedEntity unchanged) { CopyAudit(priorAudit, unchanged); }
                continue;
            }
            var current = next.GetValueOrDefault(item.Id);
            if (before is null ? current is not null : current?.Version != before.Version)
            {
                throw new IdentityMemoryConflictException("Identity Memory 状态已被其他调用修改。");
            }
            if (item is IAuditedEntity audit)
            {
                var actor = execution?.IsSystem == true ? null : currentUser?.UserId;
                audit.CreatedAt = current is IAuditedEntity prior ? prior.CreatedAt : clock.UtcNow.ToUniversalTime();
                audit.CreatedBy = current is IAuditedEntity creator ? creator.CreatedBy : actor;
                audit.UpdatedAt = current is null ? null : clock.UtcNow.ToUniversalTime();
                audit.UpdatedBy = current is null ? null : actor;
            }
            next[item.Id] = item;
        }
    }

    private static void CopyAudit<TId, T>(Dictionary<TId, T> source, Dictionary<TId, T> target)
        where TId : notnull where T : AuditedAggregateRoot<TId>
    {
        foreach (var pair in source) { CopyAudit(pair.Value, target[pair.Key]); }
    }

    private static void CopyAudit(IAuditedEntity source, IAuditedEntity target)
    {
        target.CreatedAt = source.CreatedAt;
        target.CreatedBy = source.CreatedBy;
        target.UpdatedAt = source.UpdatedAt;
        target.UpdatedBy = source.UpdatedBy;
    }
}
