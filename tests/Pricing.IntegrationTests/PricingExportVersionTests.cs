using NexusStackNext.Pricing.Domain;

namespace NexusStackNext.Pricing.IntegrationTests;

public sealed class PricingExportVersionTests
{
    [Fact]
    public void Generation_and_publication_changes_bump_once_but_lease_and_receipt_replays_do_not()
    {
        var now = new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
        var export = PricingExport.Accept(new PricingExportId(Guid.NewGuid()), "owner", Guid.NewGuid(), "request/v1", new string('a', 64),
            new string('b', 64), 100, now, now, [new(new PriceId(Guid.NewGuid()), 1, 80m, 0.2m, 1, 0, 0, null)]).Value;
        var version = export.Version;
        Assert.True(export.TryClaim(now, Guid.NewGuid(), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), 3).Value);
        Assert.Equal(++version, export.Version);
        Assert.False(export.TryClaim(now, Guid.NewGuid(), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), 3).Value);
        Assert.Equal(version, export.Version);
        Assert.False(export.Renew(export.Epoch, now, TimeSpan.FromSeconds(30)).Value);
        Assert.Equal(version, export.Version);
        Assert.True(export.Renew(export.Epoch, now.AddSeconds(1), TimeSpan.FromSeconds(30)).Value);
        Assert.Equal(++version, export.Version);
        var publication = Guid.NewGuid();
        Assert.True(export.SelectPublication(export.Epoch, now.AddSeconds(2), 42, publication, "pricing").Value);
        Assert.Equal(++version, export.Version);
        Assert.False(export.SelectPublication(export.Epoch, now.AddSeconds(3), 42, publication, "pricing").Value);
        Assert.Equal(version, export.Version);
        Assert.True(export.CompletePublication(now.AddSeconds(4), now.AddDays(7)).IsSuccess);
        Assert.Equal(++version, export.Version);
        Assert.True(export.CompletePublication(now.AddSeconds(4), now.AddDays(7)).IsSuccess);
        Assert.Equal(version, export.Version);
        Assert.True(export.CompletePublication(now.AddSeconds(5), now.AddDays(7)).IsFailure);
        Assert.Equal(version, export.Version);
    }
}
