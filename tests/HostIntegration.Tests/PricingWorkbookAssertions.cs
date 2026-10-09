using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace NexusStackNext.HostIntegration.Tests;

// Independent reader: no Open XML SDK and no production formatter are used here.
internal static class PricingWorkbookAssertions
{
    public static void FrozenPendingQuote(byte[] bytes)
    {
        using var source = new MemoryStream(bytes);
        using var zip = new ZipArchive(source, ZipArchiveMode.Read);
        Assert.Equal(new[] { "[Content_Types].xml", "_rels/.rels", "xl/_rels/workbook.xml.rels", "xl/workbook.xml", "xl/worksheets/sheet1.xml" },
            zip.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal));
        Assert.InRange(zip.Entries.Sum(entry => entry.Length), 1, 16_777_216);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var workbook = ReadXml(zip.GetEntry("xl/workbook.xml")!);
        Assert.Equal("Quotes", Assert.Single(workbook.Descendants(ns + "sheet")).Attribute("name")!.Value);
        var worksheet = ReadXml(zip.GetEntry("xl/worksheets/sheet1.xml")!);
        var rows = worksheet.Descendants(ns + "row").Select(row => row.Elements(ns + "c").Select(cell =>
        {
            Assert.Equal("inlineStr", cell.Attribute("t")!.Value);
            Assert.Empty(cell.Elements(ns + "f"));
            return Assert.Single(cell.Descendants(ns + "t")).Value;
        }).ToArray()).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(new[] { "ItemId", "Version", "Cost", "FeeRate", "InputRevision", "CalculatedRevision", "CostingRevision", "BreakEvenPrice", "CalculationState" }, rows[0]);
        Assert.Equal(new[] { "11111111-1111-1111-1111-111111111111", "1", "80.0000", "0.2000", "1", "0", "0", "", "Pending" }, rows[1]);
        Assert.Empty(worksheet.Descendants(ns + "hyperlink"));
        foreach (var part in zip.Entries.Where(entry => entry.FullName.EndsWith(".rels", StringComparison.Ordinal)))
        { Assert.DoesNotContain(ReadXml(part).Descendants(), element => element.Attribute("TargetMode")?.Value == "External"); }
    }

    private static XDocument ReadXml(ZipArchiveEntry entry)
    {
        using var source = entry.Open();
        using var reader = XmlReader.Create(source, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16_777_216 });
        return XDocument.Load(reader);
    }
}
