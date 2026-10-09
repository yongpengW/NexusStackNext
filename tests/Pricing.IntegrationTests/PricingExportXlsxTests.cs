using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using NexusStackNext.Pricing.Domain;
using NexusStackNext.Pricing.Infrastructure;

namespace NexusStackNext.Pricing.IntegrationTests;

public sealed class PricingExportXlsxTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_or_cancellation_during_zip_finalization_never_returns_a_completed_workbook(bool cancel)
    {
        PricingExportRow[] rows = [new(new PriceId(Guid.Parse("11111111-1111-1111-1111-111111111111")), 1, 80m, 0.2m, 1, 0, 0, null)];
        using var cancellation = new CancellationTokenSource();
        using var output = new FailedZipStream(cancel ? cancellation : null);
        if (cancel)
        { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PricingXlsxV1.WriteAsync(rows, output, cancellationToken: cancellation.Token)); }
        else
        { await Assert.ThrowsAsync<IOException>(() => PricingXlsxV1.WriteAsync(rows, output)); }
        Assert.True(output.FaultActivated);
        Assert.True(output.CanWrite);
    }

    [Fact]
    public async Task Maximum_supported_snapshot_has_a_complete_bounded_workbook_with_measured_process_and_temporary_file_usage()
    {
        var rows = Enumerable.Range(1, 5000).Select(index => new PricingExportRow(new PriceId(new Guid(index, 0, 0, new byte[8])),
            long.MaxValue, 99999999999999.9999m, 0.9999m, long.MaxValue, long.MaxValue - 1, long.MaxValue, 99999999999999.9999m)).ToArray();
        var path = Path.Combine(Path.GetTempPath(), "nsn-xlsx-capacity-" + Guid.NewGuid().ToString("N") + ".tmp");
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var baseline = process.WorkingSet64;
        long peakWorkingSet = baseline;
        long peakTemporaryBytes = 0;
        using var sampling = new CancellationTokenSource();
        var elapsed = Stopwatch.StartNew();
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
            65_536, FileOptions.DeleteOnClose);
        var sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                process.Refresh();
                peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
                peakTemporaryBytes = Math.Max(peakTemporaryBytes, new FileInfo(path).Length);
                try { await Task.Delay(5, sampling.Token); }
                catch (OperationCanceledException) when (sampling.IsCancellationRequested) { break; }
            }
        });
        try { await PricingXlsxV1.WriteAsync(rows, output); await output.FlushAsync(); }
        finally { await sampling.CancelAsync(); await sampler; }
        elapsed.Stop();
        peakTemporaryBytes = Math.Max(peakTemporaryBytes, output.Length);
        process.Refresh();
        peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
        Assert.InRange(output.Length, 1, 33_554_432);
        Assert.InRange(peakTemporaryBytes, output.Length, 33_554_432);
        output.Position = 0;
        using var zip = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true);
        Assert.Equal(5, zip.Entries.Count);
        var expandedBytes = zip.Entries.Sum(entry => entry.Length);
        Assert.InRange(expandedBytes, 1, 16_777_216);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var actual = ReadXml(zip.GetEntry("xl/worksheets/sheet1.xml")!).Descendants(ns + "row").ToArray();
        Assert.Equal(5001, actual.Length);
        Assert.Equal("00000001-0000-0000-0000-000000000000", actual[1].Descendants(ns + "t").First().Value);
        Assert.Equal("00001388-0000-0000-0000-000000000000", actual[^1].Descendants(ns + "t").First().Value);
        foreach (var row in actual.Skip(1))
        {
            var cells = row.Elements(ns + "c").ToArray();
            Assert.Equal(9, cells.Length);
            Assert.All(cells, cell => { Assert.Equal("inlineStr", cell.Attribute("t")!.Value); Assert.Empty(cell.Elements(ns + "f")); });
            Assert.Equal("9223372036854775807", cells[1].Descendants(ns + "t").Single().Value);
            Assert.Equal("99999999999999.9999", cells[2].Descendants(ns + "t").Single().Value);
            Assert.Equal("Stale", cells[8].Descendants(ns + "t").Single().Value);
        }
        // Only fixed numeric telemetry leaves the test output. CI validates and copies this
        // single file; raw TRX, exception text and private configuration remain private.
        var measurement = new
        {
            schema = 1,
            os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "other",
            rows = rows.Length,
            outputBytes = output.Length,
            expandedBytes,
            temporaryFilePeakBytes = peakTemporaryBytes,
            baselineWorkingSetBytes = baseline,
            peakWorkingSetBytes = peakWorkingSet,
            elapsedMilliseconds = elapsed.ElapsedMilliseconds,
        };
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "pricing-xlsx-capacity.json"), JsonSerializer.Serialize(measurement));
    }

    [Fact]
    public async Task Truncated_zip_end_record_is_rejected_before_a_workbook_is_returned()
    {
        PricingExportRow[] rows = [new(new PriceId(Guid.Parse("11111111-1111-1111-1111-111111111111")), 1, 80m, 0.2m, 1, 0, 0, null)];
        using var output = new TruncatedZipStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => PricingXlsxV1.WriteAsync(rows, output));
        Assert.True(output.FaultActivated);
        Assert.True(output.CanWrite);
    }

    [Fact]
    public async Task Zip_finalization_never_writes_bytes_beyond_the_actual_output_budget()
    {
        PricingExportRow[] rows = [new(new PriceId(Guid.Parse("11111111-1111-1111-1111-111111111111")), 1, 80m, 0.2m, 1, 0, 0, null)];
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => PricingXlsxV1.WriteAsync(rows, output, maxBytes: 512));
        Assert.InRange(output.Length, 0, 512);
        Assert.True(output.CanWrite);
    }

    [Fact]
    public async Task Workbook_reader_observes_exact_text_cells_for_large_identifiers_decimal_boundaries_and_missing_stale_results()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            PricingExportRow[] rows =
            [
                new(new PriceId(Guid.Parse("11111111-1111-1111-1111-111111111111")), long.MaxValue, 99999999999999.9999m, 0.1234m,
                    long.MaxValue, long.MaxValue - 1, long.MaxValue, 1.2345m),
                new(new PriceId(Guid.Parse("22222222-2222-2222-2222-222222222222")), 1, 0m, 0m, 1, 0, 0, null),
            ];
            using var output = new MemoryStream();
            await PricingXlsxV1.WriteAsync(rows, output);
            Assert.True(output.CanWrite);
            output.Position = 0;
            using var zip = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true);
            Assert.InRange(zip.Entries.Count, 5, 6);
            XNamespace sheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var workbook = ReadXml(zip.GetEntry("xl/workbook.xml")!);
            Assert.Equal("Quotes", Assert.Single(workbook.Descendants(sheetNamespace + "sheet")).Attribute("name")!.Value);
            var sheet = ReadXml(zip.GetEntry("xl/worksheets/sheet1.xml")!);
            var actual = sheet.Descendants(sheetNamespace + "row").Select(row => row.Elements(sheetNamespace + "c").Select(cell =>
            {
                Assert.Equal("inlineStr", cell.Attribute("t")!.Value);
                Assert.Empty(cell.Elements(sheetNamespace + "f"));
                return Assert.Single(cell.Descendants(sheetNamespace + "t")).Value;
            }).ToArray()).ToArray();
            Assert.Equal(3, actual.Length);
            Assert.Equal(new[] { "ItemId", "Version", "Cost", "FeeRate", "InputRevision", "CalculatedRevision", "CostingRevision", "BreakEvenPrice", "CalculationState" }, actual[0]);
            Assert.Equal(new[] { "11111111-1111-1111-1111-111111111111", "9223372036854775807", "99999999999999.9999", "0.1234",
                "9223372036854775807", "9223372036854775806", "9223372036854775807", "1.2345", "Stale" }, actual[1]);
            Assert.Equal(new[] { "22222222-2222-2222-2222-222222222222", "1", "0.0000", "0.0000", "1", "0", "0", "", "Pending" }, actual[2]);
            Assert.Empty(sheet.Descendants(sheetNamespace + "hyperlink"));
            foreach (var part in zip.Entries.Where(entry => entry.FullName.EndsWith(".rels", StringComparison.Ordinal)))
            { Assert.DoesNotContain(ReadXml(part).Descendants(), element => element.Attribute("TargetMode")?.Value == "External"); }
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static XDocument ReadXml(ZipArchiveEntry entry)
    {
        using var source = entry.Open();
        using var reader = XmlReader.Create(source, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16_777_216 });
        return XDocument.Load(reader);
    }

    // The fault belongs to the output boundary: erase the standard ZIP end-record
    // signature after storage accepts it, without telling the package writer.
    private sealed class TruncatedZipStream : MemoryStream
    {
        public bool FaultActivated { get; private set; }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var bytes = buffer.ToArray();
            base.Write(bytes, 0, bytes.Length);
            if (!FaultActivated && buffer.IndexOf(new byte[] { 0x50, 0x4b, 0x05, 0x06 }) >= 0)
            {
                FaultActivated = true;
                SetLength(Length - 4);
            }
        }
    }

    private sealed class FailedZipStream(CancellationTokenSource? cancellation) : MemoryStream
    {
        public bool FaultActivated { get; private set; }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (!FaultActivated && buffer.IndexOf(new byte[] { 0x50, 0x4b, 0x05, 0x06 }) >= 0)
            {
                FaultActivated = true;
                if (cancellation is null) { throw new IOException("Injected ZIP finalization failure."); }
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
            var bytes = buffer.ToArray();
            base.Write(bytes, 0, bytes.Length);
        }
    }
}
