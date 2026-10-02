using NexusStackNext.Auditing.Domain.Entries;

namespace NexusStackNext.Auditing.Domain.Tests;

/// <summary>Auditing 聚合的版本号（ADR-0011）。</summary>
public sealed class AggregateVersionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AuditEntry_IsImmutable_SoVersionNeverChanges()
    {
        var entry = AuditEntry.Record(new AuditEntryId(1), new AuditFact(Guid.Parse("11111111-1111-1111-1111-111111111111"), "identity.user-registered.v1", "identity", "identity.user-registered", "user", "1001", 1, null, Now, "trace-1", "correlation-1"), Now).Value;

        Assert.Equal(1, entry.Version);
    }
}
