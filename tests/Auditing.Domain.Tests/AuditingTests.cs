using System.Reflection;
using NexusStackNext.Auditing.Domain.Entries;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Auditing.Domain.Tests;

/// <summary>Auditing：审计条目只写不改。</summary>
public sealed class AuditingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static Result<AuditEntry> Record(string? action = "identity.user-registered") =>
        AuditEntry.Record(new AuditEntryId(1), new AuditFact(Guid.Parse("11111111-1111-1111-1111-111111111111"), "identity.user-registered.v1", "identity", action!, "User", "42", 1, "actor-1", Now, "trace-1", "correlation-1"), Now);

    [Fact]
    public void Record_KeepsTheFacts()
    {
        var entry = Record().Value;

        Assert.Equal("identity.user-registered", entry.Fact.Action);
        Assert.Equal("User", entry.Fact.SubjectType);
        Assert.Equal("42", entry.Fact.SubjectId);
        Assert.Equal("actor-1", entry.Fact.ActorId);
        Assert.Equal(Now, entry.RecordedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void Record_RejectsEmptyAction(string? action)
    {
        Assert.True(Record(action).IsFailure);
    }

    [Fact]
    public void Record_RejectsMissingSubject()
    {
        var result = AuditEntry.Record(new AuditEntryId(1), new AuditFact(Guid.Parse("11111111-1111-1111-1111-111111111111"), "identity.user-registered.v1", "identity", "x", "  ", "42", 1, null, Now, "trace-1", "correlation-1"), Now);

        Assert.True(result.IsFailure);
        Assert.Equal("auditing.subject.empty", result.Error.Code);
    }

    [Fact]
    public void AuditEntry_IsAppendOnly_NoPublicSettersAndNoMutators()
    {
        // "不可改写"如果只靠约定，迟早会有人在某处加一个 Update。让它在类型上就不可能。
        var type = typeof(AuditEntry);

        var writable = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(static property => property.SetMethod is { IsPublic: true })
            .Select(static property => property.Name)
            .ToList();
        Assert.Empty(writable);

        var mutators = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(static method => method.Name)
            .Where(static name => name is "Update" or "Delete" or "Remove" or "Change" or "Edit" or "Set")
            .ToList();
        Assert.Empty(mutators);
    }
}
