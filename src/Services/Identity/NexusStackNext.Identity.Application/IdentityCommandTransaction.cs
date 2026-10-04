using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Transactions;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Identity.Application;

/// <summary>Identity 独占的工作单元；其他上下文的注册不能替代它。</summary>
public interface IIdentityUnitOfWork : IUnitOfWork;

/// <summary>Identity 的命令提交边界。每个请求作用域内只有一份。</summary>
/// <param name="unitOfWork">Identity 的存储事务。</param>
/// <param name="permissions">提交后失效的权限缓存。</param>
public sealed class IdentityCommandTransaction(
    IIdentityUnitOfWork unitOfWork,
    IPermissionCache permissions)
{
    private Error? _persistedRejection;
    private bool _executing;
    private bool _invalidatePermissions;
    /// <summary>登记权限变化；只有本次命令提交成功后才让缓存失效。</summary>
    public void InvalidatePermissionsAfterCommit()
    {
        EnsureExecuting();
        _invalidatePermissions = true;
    }

    /// <summary>声明这次拒绝本身改变了安全状态，仍须提交。只匹配指定的错误。</summary>
    /// <param name="error">处理器随后返回的拒绝原因。</param>
    public void PreserveChangesOnRejection(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        EnsureExecuting();

        _persistedRejection = error;
    }

    internal async Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
        where TResult : Result
    {
        if (_executing)
        {
            throw new InvalidOperationException("不能在 Identity 命令事务中嵌套发送命令。");
        }

        _executing = true;
        try
        {
            var committed = await unitOfWork.ExecuteInTransactionAsync(async token =>
            {
                _persistedRejection = null;
                _invalidatePermissions = false;
                var result = await operation(token).ConfigureAwait(false);
                if (ShouldCommit(result))
                {
                    await unitOfWork.SaveChangesAsync(token).ConfigureAwait(false);
                }

                return result;
            }, ShouldCommit, cancellationToken).ConfigureAwait(false);

            if (ShouldCommit(committed) && _invalidatePermissions)
            {
                permissions.Invalidate();
            }

            return committed;
        }
        finally
        {
            _persistedRejection = null;
            _invalidatePermissions = false;
            _executing = false;
        }
    }

    private bool ShouldCommit(Result result) => result.IsSuccess || result.Error == _persistedRejection;

    private void EnsureExecuting()
    {
        if (!_executing)
        {
            throw new InvalidOperationException("提交意图必须在 Identity 命令事务中登记。");
        }
    }
}

internal sealed class IdentityCommandHandler<TCommand>(
    ICommandHandler<TCommand> handler,
    IdentityCommandTransaction transaction) : ICommandHandler<TCommand>
    where TCommand : ICommand
{
    public async Task<Result> HandleAsync(TCommand command, CancellationToken cancellationToken = default)
    {
        try { return await transaction.ExecuteAsync(token => handler.HandleAsync(command, token), cancellationToken).ConfigureAwait(false); }
        catch (IdentityAuditCapacityException error) { return Result.Failure(error.Reason); }
    }
}

internal sealed class IdentityCommandHandler<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> handler,
    IdentityCommandTransaction transaction) : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public async Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken = default)
    {
        try { return await transaction.ExecuteAsync(token => handler.HandleAsync(command, token), cancellationToken).ConfigureAwait(false); }
        catch (IdentityAuditCapacityException error) { return Result.Failure<TResult>(error.Reason); }
    }
}
