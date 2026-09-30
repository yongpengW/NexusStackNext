using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// 写入 <see cref="IAuditedEntity"/> 的审计字段。
///
/// <para><b>它是 ADR-0008 的落点。</b>那条决定要求"所有写入路径都必须经过审计拦截器"，
/// 参照仓库的反例是批量库绕过了审计——一条本该留痕的批量删除在审计表里毫无记录，
/// 而没有人会发现，因为"没留下记录"和"没发生过"看起来一样。</para>
///
/// <para><b>"所有写入路径"这件事靠两半达成</b>：这一半是<b>拦截器本身</b>（写字段），
/// 另一半是<b>禁止绕过 <c>SaveChanges</c> 的 API</b>——后者由
/// <c>AuditBypassIsForbiddenTests</c> 在源码层面守着。
/// 只有一半的话：拦截器写得再好，一次 <c>ExecuteDelete</c> 就全绕过去了。</para>
///
/// <para><b>为什么 <c>CreatedAt</c> 只在 <c>Added</c> 时写、<c>UpdatedAt</c> 只在 <c>Modified</c> 时写。</b>
/// 两者都在每次保存时覆盖的话，"创建时间"会随着每一次修改而漂移，那个字段也就失去意义了。
/// 同理，未修改过的实体 <c>UpdatedAt</c> 保持 <c>null</c>——"从未修改过"是一个有意义的状态。</para>
/// </summary>
/// <param name="clock">时钟。</param>
/// <param name="currentUser">当前发起者。</param>
public sealed class AuditInterceptor(
    IClock clock,
    ICurrentUser currentUser) : SaveChangesInterceptor
{
    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        Stamp(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        Stamp(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = clock.UtcNow;
        var user = currentUser.UserId;

        foreach (var entry in context.ChangeTracker.Entries<IAuditedEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = now;
                    entry.Entity.CreatedBy = user;

                    // 新建的实体"从未修改过"——把上一次运行留下的值清掉。
                    entry.Entity.UpdatedAt = null;
                    entry.Entity.UpdatedBy = null;
                    break;

                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now;
                    entry.Entity.UpdatedBy = user;
                    break;

                default:
                    // Deleted / Unchanged / Detached：不改审计字段。
                    // 软删走的是 Modified（IsDeleted 变了），因此会留下"谁在何时删的" ✓。
                    break;
            }
        }
    }
}
