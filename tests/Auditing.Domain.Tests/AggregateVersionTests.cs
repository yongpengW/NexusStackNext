using NexusStackNext.Auditing.Domain.Entries;
using NexusStackNext.Auditing.Domain.Exports;

namespace NexusStackNext.Auditing.Domain.Tests;

/// <summary>Auditing 聚合的版本号（ADR-0011）。</summary>
public sealed class AggregateVersionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Accepted_snapshot_copies_input_and_rejects_overflow_without_silent_truncation()
    {
        string[][] rows = [["original"]];
        var id = new AuditExportId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var request = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var export = AuditExport.Accept(id, "42", request, "canonical", "facts", Now, Now, Now.AddDays(-1), Now, rows).Value;
        rows[0][0] = "changed";
        Assert.Equal("original", export.Rows[0][0]);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)export.Rows[0])[0] = "changed");
        Assert.Equal(1, export.Version);
        Assert.True(AuditExport.Accept(id, "42", request, "canonical", "facts", Now, Now, Now.AddDays(-1), Now,
            Enumerable.Repeat(new[] { "row" }, 5001).ToArray()).IsFailure);
        Assert.True(AuditExport.Accept(id, "42", request, "canonical", "facts", Now, Now, Now.AddDays(-32), Now, rows).IsFailure);
    }

    [Fact]
    public void Snapshot_retention_never_discards_selected_publication_or_prevents_original_delivery_recovery()
    {
        var export = AuditExport.Accept(new AuditExportId(Guid.Parse("11111111-1111-1111-1111-111111111111")), "42",
            Guid.Parse("22222222-2222-2222-2222-222222222222"), "canonical", "facts", Now, Now, Now.AddDays(-1), Now, [["fact"]]).Value;
        Assert.True(export.TryClaim(Now, TimeSpan.FromMinutes(1), 1).Value);
        Assert.True(export.SelectPublication(1, Now, 123, new string('a', 64), 10).IsSuccess);
        Assert.True(export.Fail(1, Now, "auditing.export.files_unavailable", 1).IsSuccess);
        Assert.True(export.ExpireSnapshot(Now.AddDays(8)).IsSuccess);
        Assert.Empty(export.Rows);
        Assert.Equal("Failed", export.State);
        Assert.True(export.Retry(export.Version, Now.AddDays(8)).IsSuccess);
        Assert.True(export.TryClaim(Now.AddDays(8), TimeSpan.FromMinutes(1), 1).Value);
        Assert.True(export.Complete(2, Now.AddDays(8), Now, Now.AddDays(7)).IsSuccess);
        Assert.Equal(123, export.FileId);
        Assert.Equal(Now.AddDays(7), export.ExpiresAt);
    }

    [Fact]
    public void AuditEntry_IsImmutable_SoVersionNeverChanges()
    {
        var entry = AuditEntry.Record(new AuditEntryId(1), new AuditFact(Guid.Parse("11111111-1111-1111-1111-111111111111"), "identity.user-registered.v1", "identity", "identity.user-registered", "user", "1001", 1, null, Now, "trace-1", "correlation-1"), Now).Value;

        Assert.Equal(1, entry.Version);
    }

    [Fact]
    public void AuditExport_changes_advance_version_and_cancel_claim_expiry_replays_are_noops()
    {
        var export = AuditExport.Accept(new AuditExportId(Guid.Parse("11111111-1111-1111-1111-111111111111")), "42",
            Guid.Parse("22222222-2222-2222-2222-222222222222"), "canonical", "facts", Now, Now, Now.AddDays(-1), Now, [["fact"]]).Value;
        Assert.Equal(1, export.Version);
        Assert.True(export.ExpireSnapshot(Now).IsSuccess);
        Assert.Equal(1, export.Version);
        Assert.True(export.TryClaim(Now, TimeSpan.FromMinutes(1), 3).Value);
        Assert.Equal(2, export.Version);
        Assert.False(export.TryClaim(Now, TimeSpan.FromMinutes(1), 3).Value);
        Assert.Equal(2, export.Version);
        Assert.True(export.Cancel(2).IsSuccess);
        Assert.Equal(3, export.Version);
        Assert.True(export.Cancel(2).IsSuccess);
        Assert.False(export.TryClaim(Now, TimeSpan.FromMinutes(1), 3).Value);
        Assert.Equal(3, export.Version);
    }

    [Fact]
    public void Selected_publication_blocks_cancel_and_expired_epoch_cannot_change_original_artifact()
    {
        var export = AuditExport.Accept(new AuditExportId(Guid.Parse("11111111-1111-1111-1111-111111111111")), "42",
            Guid.Parse("22222222-2222-2222-2222-222222222222"), "canonical", "facts", Now, Now, Now.AddDays(-1), Now, [["fact"]]).Value;
        Assert.True(export.TryClaim(Now, TimeSpan.FromSeconds(1), 3).Value);
        Assert.True(export.SelectPublication(1, Now, 123, new string('a', 64), 10).IsSuccess);
        Assert.True(export.Cancel(export.Version).IsFailure);
        Assert.True(export.TryClaim(Now.AddSeconds(2), TimeSpan.FromMinutes(1), 3).Value);
        var version = export.Version;
        Assert.True(export.Complete(1, Now.AddSeconds(2), Now, Now.AddDays(7)).IsFailure);
        Assert.Equal(version, export.Version);
        Assert.Equal(123, export.FileId);
        Assert.True(export.Complete(2, Now.AddSeconds(2), Now, Now.AddDays(7)).IsSuccess);
        Assert.Equal(version + 1, export.Version);
        Assert.Equal("Succeeded", export.State);
        Assert.Empty(export.Rows);
    }
}
