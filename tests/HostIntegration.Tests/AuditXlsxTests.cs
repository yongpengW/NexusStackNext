using System.IO.Compression;
using System.Xml.Linq;
using NexusStackNext.Auditing.Infrastructure;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class AuditXlsxTests
{
    [Fact]
    public async Task Regenerating_the_same_snapshot_after_a_delay_produces_identical_sealable_bytes()
    {
        var row = Enumerable.Repeat("same", 22).ToArray();
        using var original = new MemoryStream();
        await AuditXlsxV1.WriteAsync("facts", [row], original);
        await Task.Delay(TimeSpan.FromSeconds(2.1));
        using var retry = new MemoryStream();
        await AuditXlsxV1.WriteAsync("facts", [row], retry);
        Assert.Equal(original.ToArray(), retry.ToArray());
    }

    [Theory]
    [InlineData("facts", 22, "CommittedFacts")]
    [InlineData("operations", 28, "OperationObservations")]
    public async Task Workbook_preserves_exact_identifiers_time_and_safe_literal_text_with_unknown_evidence_empty(string kind, int columns, string sheetName)
    {
        var row = Enumerable.Repeat("", columns).ToArray();
        row[0] = kind == "facts" ? "committed-fact" : "operation-observation";
        row[1] = "9223372036854775807";
        row[7] = "=HYPERLINK(\"https://example.invalid\",\"literal\")";
        row[kind == "facts" ? 10 : 12] = "2026-10-10T01:02:03.1234567+00:00";
        using var output = new MemoryStream();
        await AuditXlsxV1.WriteAsync(kind, [row], output);
        Assert.True(output.CanWrite);
        output.Position = 0;
        using var package = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true);
        XNamespace spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        using var workbookSource = package.GetEntry("xl/workbook.xml")!.Open();
        var workbook = XDocument.Load(workbookSource);
        Assert.Equal(sheetName, workbook.Descendants(spreadsheet + "sheet").Single().Attribute("name")!.Value);
        using var worksheetSource = package.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var worksheet = XDocument.Load(worksheetSource);
        var data = worksheet.Descendants(spreadsheet + "row").Skip(1).Single().Elements(spreadsheet + "c").ToArray();
        Assert.Equal(row, data.Select(cell => cell.Value));
        Assert.All(data, cell => { Assert.Equal("inlineStr", cell.Attribute("t")!.Value); Assert.Null(cell.Element(spreadsheet + "f")); });
        Assert.Empty(worksheet.Descendants(spreadsheet + "hyperlink"));
        if (kind == "operations")
        {
            Assert.Equal("", data[11].Value);
            Assert.Equal("AB2", data[^1].Attribute("r")!.Value);
        }
    }

    [Fact]
    public async Task Tail_limit_and_cancellation_do_not_produce_a_completed_workbook_or_take_stream_ownership()
    {
        string[] row = ["committed-fact", "1", "11111111-1111-1111-1111-111111111111", "event", "platform", "changed", "setting", "id", "1", "", "time", "recorded", "trace", "correlation", "", "", "", "", "", "", "", ""];
        using var complete = new MemoryStream();
        await AuditXlsxV1.WriteAsync("facts", [row], complete);
        using var bounded = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => AuditXlsxV1.WriteAsync("facts", [row], bounded, complete.Length - 1));
        Assert.True(bounded.CanWrite);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        using var cancelled = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AuditXlsxV1.WriteAsync("facts", [row], cancelled, cancellationToken: cancellation.Token));
        Assert.Equal(0, cancelled.Length);
        Assert.True(cancelled.CanWrite);
    }
}
