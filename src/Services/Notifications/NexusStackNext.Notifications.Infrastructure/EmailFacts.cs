using System.Diagnostics;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.Notifications.Contracts;
using NexusStackNext.Notifications.Domain;

namespace NexusStackNext.Notifications.Infrastructure;

internal sealed class EmailFacts(NotificationsDbContext database, IIntegrationEventSerializer serializer,
    IClock clock, ICurrentUser user, IExecutionContext execution)
{
    public void Add(EmailDelivery email, string operation)
    {
        var origin = execution.Capture();
        var trace = origin?.TraceId ?? Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        database.Outbox.Add(OutboxEntry.From(new EmailDeliveryCommittedV1
        {
            EmailId = email.Id.Value,
            Operation = operation,
            Version = email.Version,
            Attempt = email.Attempt,
            ActorId = execution.IsSystem ? null : user.UserId,
            OccurredAt = clock.UtcNow,
            TraceId = trace,
            CorrelationId = origin?.CorrelationId ?? trace,
            Execution = origin,
        }, serializer));
    }
}
