using System.Net.Sockets;
using System.Text.Json;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Notifications.Application;

namespace NexusStackNext.Notifications.Infrastructure;

// 一次调用最多领取一封。数据库重试只包住持久化，绝不包住 SMTP。
internal sealed class EmailDeliveryRunner(NotificationsDbContext database, SmtpOptions smtp, IClock clock, EmailFacts facts,
    IBackgroundExecutionObservation observation) : ICommandHandler<DispatchPendingEmail, int>
{
    public async Task<Result<int>> HandleAsync(DispatchPendingEmail command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Result.Success(await DeliverPendingAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task<int> DeliverPendingAsync(CancellationToken cancellationToken)
    {
        var candidate = await database.Emails.AsNoTracking().Where(email => email.State == "Pending")
            .OrderBy(email => email.CreatedAt).ThenBy(email => email.Id)
            .Select(email => new { email.Id, Origin = EF.Property<string?>(email, "ExecutionOrigin") }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (candidate is null) { return 0; }
        var origin = candidate.Origin is null ? null : JsonSerializer.Deserialize<ExecutionOrigin>(candidate.Origin);
        var result = await observation.ObserveAsync(new RecoveryExecutionDescriptor("notifications.email.deliver", "email-delivery", "guid", candidate.Id.Value.ToString("D")),
            () => Task.FromResult(new BackgroundExecutionInput<Guid>(candidate.Id.Value, origin)),
            id => DeliverOneAsync(id, cancellationToken), result => result, cancellationToken).ConfigureAwait(false);
        return result == BackgroundExecutionOutcome.Skipped ? 0 : 1;
    }

    private async Task<BackgroundExecutionOutcome> DeliverOneAsync(Guid id, CancellationToken cancellationToken)
    {
        var claim = await database.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            database.ChangeTracker.Clear();
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var email = await EmailCommands.LockAsync(database, id, cancellationToken).ConfigureAwait(false);
            if (email is null || !email.TryStart(clock.UtcNow)) { return null; }
            var bytes = database.Entry(email).Property<byte[]>("MimeContent").CurrentValue!;
            facts.Add(email, "attempt-started");
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ClaimedEmail(email.Id.Value, email.Attempt, bytes);
        }).ConfigureAwait(false);
        if (claim is null) { return BackgroundExecutionOutcome.Skipped; }

        var result = await SendAsync(claim.Content, cancellationToken).ConfigureAwait(false);
        await database.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            database.ChangeTracker.Clear();
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var email = await EmailCommands.LockAsync(database, claim.Id, cancellationToken).ConfigureAwait(false);
            if (email is null || !email.Complete(claim.Attempt, result == "accepted", result == "unknown" ? null : result, clock.UtcNow)) { return; }
            facts.Add(email, result == "accepted" ? "accepted" : "failed");
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return result switch
        {
            "accepted" => BackgroundExecutionOutcome.Completed,
            "unknown" => BackgroundExecutionOutcome.Deferred,
            _ => BackgroundExecutionOutcome.Failed,
        };
    }

    private async Task<string> SendAsync(byte[] content, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var client = new SmtpClient();
        using var bytes = new MemoryStream(content, writable: false);
        using var message = await MimeMessage.LoadAsync(bytes, timeout.Token).ConfigureAwait(false);
        var sending = false;
        try
        {
            await client.ConnectAsync(smtp.Host, smtp.Port, smtp.Security, timeout.Token).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(smtp.UserName)) { await client.AuthenticateAsync(smtp.UserName, smtp.Password!, timeout.Token).ConfigureAwait(false); }
            sending = true;
            _ = await client.SendAsync(message, timeout.Token).ConfigureAwait(false);
            // 250 已确认接纳；不让 QUIT/断连错误覆盖它。Dispose 关闭连接。
            return "accepted";
        }
        catch (SmtpCommandException) { return "smtp_rejected"; }
        catch (Exception error) when (error is AuthenticationException or ServiceNotAuthenticatedException) { return "smtp_authentication_failed"; }
        catch (SslHandshakeException) { return "smtp_tls_failed"; }
        catch (Exception error) when (error is IOException or SocketException or SmtpProtocolException or NotSupportedException
            || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return sending ? "unknown" : "smtp_connection_failed";
        }
    }

    private sealed record ClaimedEmail(Guid Id, int Attempt, byte[] Content);
}
