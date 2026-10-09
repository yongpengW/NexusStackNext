using System.Globalization;
using System.Text;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Domain;

namespace NexusStackNext.Pricing.IntegrationTests;

public sealed class PricingExportCsvTests
{
    [Fact]
    public async Task Frozen_quotes_produce_exact_utf8_crlf_csv_without_float_or_locale_conversion()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            PricingExportRow[] rows =
            [
                new(new PriceId(Guid.Parse("11111111-1111-1111-1111-111111111111")), 9007199254740993, 80m, 0.2m, 3, 3, 0, 100m),
                new(new PriceId(Guid.Parse("22222222-2222-2222-2222-222222222222")), 2, 1.2345m, 0.1234m, 2, 1, 7, 1.2346m),
                new(new PriceId(Guid.Parse("33333333-3333-3333-3333-333333333333")), 1, 0m, 0m, 1, 0, 0, null),
            ];
            using var output = new MemoryStream();
            await PricingCsvV1.WriteAsync(rows, output);
            const string expected = "ItemId,Version,Cost,FeeRate,InputRevision,CalculatedRevision,CostingRevision,BreakEvenPrice,CalculationState\r\n"
                + "11111111-1111-1111-1111-111111111111,9007199254740993,80.0000,0.2000,3,3,0,100.0000,Current\r\n"
                + "22222222-2222-2222-2222-222222222222,2,1.2345,0.1234,2,1,7,1.2346,Stale\r\n"
                + "33333333-3333-3333-3333-333333333333,1,0.0000,0.0000,1,0,0,,Pending\r\n";
            Assert.Equal(Encoding.UTF8.GetBytes(expected), output.ToArray());
            Assert.True(output.CanWrite);
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
    }

    [Fact]
    public async Task Actual_output_limit_stops_before_writing_bytes_beyond_its_budget_and_leaves_caller_stream_open()
    {
        PricingExportRow[] rows = Enumerable.Repeat(new PricingExportRow(new PriceId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            1, 80m, 0.2m, 1, 0, 0, null), 128).ToArray();
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => PricingCsvV1.WriteAsync(rows, output, maxBytes: 512));
        Assert.InRange(output.Length, 1, 512);
        Assert.True(output.CanWrite);
    }
}
