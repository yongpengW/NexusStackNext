using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Notifications.Application;
using NexusStackNext.Notifications.Domain;

namespace NexusStackNext.Notifications.Infrastructure;

internal sealed class EmailCommands(NotificationsDbContext database, SmtpOptions smtp, ICurrentUser user, IClock clock, EmailFacts facts, IExecutionContext execution)
    : ICommandHandler<SubmitEmail, EmailStatus>, IQueryHandler<GetEmail, EmailStatus>, ICommandHandler<ResolveEmail, EmailStatus>
{
    public Task<Result<EmailStatus>> HandleAsync(SubmitEmail command, CancellationToken cancellationToken) =>
        database.Database.CreateExecutionStrategy().ExecuteAsync(() => SubmitAsync(command, cancellationToken));

    private async Task<Result<EmailStatus>> SubmitAsync(SubmitEmail command, CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        ArgumentNullException.ThrowIfNull(command);
        if (user.UserId is not { Length: > 0 } owner) { return Failure("unauthorized"); }
        if (!Valid(command)) { return Failure("invalid"); }
        var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command)));
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"notifications.email:" + command.RequestId.ToString("D")}, 0))", cancellationToken).ConfigureAwait(false);
        var id = new EmailDeliveryId(command.RequestId);
        var existing = await database.Emails.SingleOrDefaultAsync(item => item.Id == id, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.OwnerId == owner && existing.Digest == digest ? Result.Success(Status(existing)) : Failure("conflict");
        }
        using var message = new MimeMessage { MessageId = command.RequestId.ToString("N") + "@notifications.nexusstack.invalid", Date = clock.UtcNow, Subject = command.Subject };
        message.From.Add(MailboxAddress.Parse(smtp.Sender));
        foreach (var address in command.Recipients) { message.To.Add(MailboxAddress.Parse(address)); }
        var body = new BodyBuilder { TextBody = command.TextBody, HtmlBody = command.HtmlBody };
        foreach (var attachment in command.Attachments) { body.Attachments.Add(attachment.Name, attachment.Content, ContentType.Parse(attachment.ContentType)); }
        message.Body = body.ToMessageBody();
        using var bytes = new MemoryStream();
        await message.WriteToAsync(bytes, cancellationToken).ConfigureAwait(false);
        var email = EmailDelivery.Create(id, owner, digest);
        database.Emails.Add(email);
        database.Entry(email).Property<byte[]>("MimeContent").CurrentValue = bytes.ToArray();
        database.Entry(email).Property<string?>("ExecutionOrigin").CurrentValue = JsonSerializer.Serialize(execution.Capture());
        facts.Add(email, "submitted");
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await database.Entry(email).ReloadAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(Status(email));
    }

    public async Task<Result<EmailStatus>> HandleAsync(GetEmail query, CancellationToken cancellationToken)
    {
        var id = new EmailDeliveryId(query.Id);
        var email = await database.Emails.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id && item.OwnerId == user.UserId, cancellationToken).ConfigureAwait(false);
        return email is null ? Failure("not_found") : Result.Success(Status(email));
    }

    public Task<Result<EmailStatus>> HandleAsync(ResolveEmail command, CancellationToken cancellationToken) =>
        database.Database.CreateExecutionStrategy().ExecuteAsync(() => ResolveAsync(command, cancellationToken));

    private async Task<Result<EmailStatus>> ResolveAsync(ResolveEmail command, CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var email = await LockAsync(database, command.Id, cancellationToken).ConfigureAwait(false);
        if (email is null || email.OwnerId != user.UserId) { return Failure("not_found"); }
        if (!email.Resolve(command.ExpectedAttempt, command.Decision, clock.UtcNow)) { return Failure("conflict"); }
        facts.Add(email, command.Decision);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(Status(email));
    }

    internal static Task<EmailDelivery?> LockAsync(NotificationsDbContext database, Guid id, CancellationToken token)
        => database.Emails.FromSqlInterpolated($"SELECT * FROM notifications.emails WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(token);

    internal static EmailStatus Status(EmailDelivery email) => new(email.Id.Value, email.State, email.Attempt, email.Version,
        email.CreatedAt, email.UpdatedAt, email.AcceptedAt, email.ResolveAfter, email.ErrorCode);

    private static Result<EmailStatus> Failure(string code) => Result.Failure<EmailStatus>(new("notifications." + code, "邮件请求未被接受。"));

    private static bool Valid(SubmitEmail command)
    {
        if (command.RequestId == Guid.Empty || command.Recipients is not { Length: > 0 and <= 10 } || command.Attachments is not { Length: <= 5 }
            || string.IsNullOrWhiteSpace(command.Subject) || command.Subject.Length > 200 || command.Subject.Contains('\r') || command.Subject.Contains('\n')
            || (string.IsNullOrEmpty(command.TextBody) && string.IsNullOrEmpty(command.HtmlBody))
            || (command.TextBody?.Length ?? 0) + (command.HtmlBody?.Length ?? 0) > 262_144) { return false; }
        if (command.Recipients.Any(address => address is null || address.Length > 320 || address.Contains('\r') || address.Contains('\n') || !MailboxAddress.TryParse(address, out _))) { return false; }
        long size = 0;
        foreach (var attachment in command.Attachments)
        {
            if (attachment is null || attachment.Content is null || string.IsNullOrWhiteSpace(attachment.Name) || attachment.Name.Length > 200
                || attachment.Name.IndexOfAny(['/', '\\', '\r', '\n']) >= 0 || !ContentType.TryParse(attachment.ContentType, out _)) { return false; }
            size += attachment.Content.Length;
            if (size > 2 * 1024 * 1024) { return false; }
        }
        return true;
    }
}
