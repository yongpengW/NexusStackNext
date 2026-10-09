using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Pricing.Application;

namespace NexusStackNext.Pricing.Endpoints;

// 宿主适配器拥有后台作用域与续租作用域；业务处理器不通过服务定位器取得并发 DbContext。
internal sealed partial class PricingExportWorker(IServiceScopeFactory scopes, PricingExportOptions options, ILogger<PricingExportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { PollFailed(logger); }
            try { await Task.Delay(options.PollInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task TickAsync(CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var publication = await sender.SendAsync(new ClaimPricingExportPublication(), token).ConfigureAwait(false);
        if (publication.IsFailure) { return; }
        if (publication.Value is { } delivery)
        {
            var result = await ExecuteAsync(sender,
                (dispatch, budget) => dispatch.SendAsync(new PublishPricingExport(delivery), budget),
                async (renewal, budget) => await renewal.SendAsync(new RenewPricingExportPublication(delivery.ExportId, delivery.PublicationId, delivery.Epoch), budget).ConfigureAwait(false), token).ConfigureAwait(false);
            if (result.IsFailure && result.Error.Code != "pricing.export.lease_lost")
            { _ = await sender.SendAsync(new FailPricingExportPublication(delivery, result.Error.Code), token).ConfigureAwait(false); }
            AttemptFinished(logger, delivery.ExportId, result.IsSuccess ? "published" : result.Error.Code);
            return;
        }
        var claim = await sender.SendAsync(new ClaimPricingExport(), token).ConfigureAwait(false);
        if (claim.IsFailure || claim.Value is not { } lease) { return; }
        var generated = await ExecuteAsync(sender,
            (dispatch, budget) => dispatch.SendAsync(new GeneratePricingExport(lease), budget),
            async (renewal, budget) => await renewal.SendAsync(new RenewPricingExport(lease.ExportId, lease.Epoch), budget).ConfigureAwait(false), token).ConfigureAwait(false);
        if (generated.IsFailure && generated.Error.Code != "pricing.export.lease_lost")
        { _ = await sender.SendAsync(new FailPricingExport(lease.ExportId, lease.Epoch, generated.Error.Code), token).ConfigureAwait(false); }
        AttemptFinished(logger, lease.ExportId, generated.IsSuccess ? "selected" : generated.Error.Code);
    }

    private async Task<Result<PricingExportStatus>> ExecuteAsync(ISender sender,
        Func<ISender, CancellationToken, Task<Result<PricingExportStatus>>> execute,
        Func<ISender, CancellationToken, Task<Result>> renew, CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(options.MaxExecutionDuration);
        var heartbeat = KeepAliveAsync(renew, budget);
        try { return await execute(sender, budget.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return Result.Failure<PricingExportStatus>(new Error("pricing.export.lease_lost", "执行权或执行预算已结束。")); }
        finally { await budget.CancelAsync().ConfigureAwait(false); await heartbeat.ConfigureAwait(false); }
    }

    private async Task KeepAliveAsync(Func<ISender, CancellationToken, Task<Result>> renew, CancellationTokenSource budget)
    {
        try
        {
            while (!budget.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromTicks(options.LeaseDuration.Ticks / 3), budget.Token).ConfigureAwait(false);
                await using var scope = scopes.CreateAsyncScope();
                var result = await renew(scope.ServiceProvider.GetRequiredService<ISender>(), budget.Token).ConfigureAwait(false);
                if (result.IsFailure) { await budget.CancelAsync().ConfigureAwait(false); return; }
            }
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested) { }
        catch (Exception) { await budget.CancelAsync().ConfigureAwait(false); }
    }

    [LoggerMessage(1, LogLevel.Warning, "Pricing export poll failed; durable recovery remains active.")]
    private static partial void PollFailed(ILogger logger);
    [LoggerMessage(2, LogLevel.Information, "Pricing export {ExportId}: {ResultCode}")]
    private static partial void AttemptFinished(ILogger logger, Guid exportId, string resultCode);
}
