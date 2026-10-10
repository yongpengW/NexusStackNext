using System.Globalization;
using System.IO.Compression;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;


namespace NexusStackNext.BuildingBlocks.Infrastructure.Exports;

/// <summary>Pricing 与 Auditing 共用的有界单工作表 XLSX；调用方提供固定白名单列与精确文本。</summary>
public static class TextWorkbook
{
    /// <summary>生成并检查完整工作簿；不关闭调用方的流。</summary>
    /// <param name="sheetName">调用模块的固定工作表名。</param>
    /// <param name="headers">调用模块的固定列集，最多四十列。</param>
    /// <param name="rows">固定的非空快照，最多五千行。</param>
    /// <param name="output">调用方拥有的可读、可写、可定位空流。</param>
    /// <param name="maxBytes">实际 ZIP 上限，最多 32 MiB。</param>
    /// <param name="cancellationToken">生成及关闭包的取消预算。</param>
    /// <returns>完整且已检查的工作簿。</returns>
    public static Task WriteAsync(string sheetName, string[] headers, IReadOnlyList<string[]> rows, Stream output, long maxBytes = 33_554_432,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentException.ThrowIfNullOrWhiteSpace(sheetName);
        if (headers.Length is < 1 or > 40 || rows.Any(row => row.Length != headers.Length || row.Any(value => value is null || value.Length > 500)))
        { throw new ArgumentException("工作簿列集或精确文本无效。", nameof(rows)); }
        ArgumentNullException.ThrowIfNull(output);
        if (rows.Count is < 1 or > 5000) { throw new ArgumentOutOfRangeException(nameof(rows)); }
        if (maxBytes is < 1 or > 33_554_432) { throw new ArgumentOutOfRangeException(nameof(maxBytes)); }
        if (!output.CanRead || !output.CanWrite || !output.CanSeek || output.Length != 0 || output.Position != 0)
        { throw new ArgumentException("工作簿需要调用方拥有的可读写空流。", nameof(output)); }
        cancellationToken.ThrowIfCancellationRequested();
        using var bounded = new WorkbookOutputStream(output, maxBytes, cancellationToken);
        using (var document = SpreadsheetDocument.Create(bounded, SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            document.ChangeIdOfPart(workbook, "rId1");
            var worksheet = workbook.AddNewPart<WorksheetPart>("rId1");
            using (var writer = OpenXmlWriter.Create(worksheet))
            {
                writer.WriteStartElement(new Worksheet());
                writer.WriteStartElement(new SheetData());
                WriteRow(writer, 1, headers);
                for (var index = 0; index < rows.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var row = rows[index];
                    WriteRow(writer, checked((uint)index + 2), row);
                }
                writer.WriteEndElement();
                writer.WriteEndElement();
            }
            workbook.Workbook = new Workbook(new Sheets(new Sheet { Id = "rId1", SheetId = 1, Name = sheetName }));
        }
        cancellationToken.ThrowIfCancellationRequested();
        // The original upload identity survives a crash before sealing. Regeneration must
        // therefore reproduce its exact digest rather than embed the new wall-clock time.
        bounded.Position = 0;
        using (var package = new ZipArchive(bounded, ZipArchiveMode.Update, leaveOpen: true))
        {
            foreach (var entry in package.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            }
        }
        if (output.Length > maxBytes) { throw new InvalidDataException("export.output_limit"); }
        ValidatePackage(bounded, sheetName, headers, rows, cancellationToken);
        return Task.CompletedTask;
    }

    private static void ValidatePackage(Stream content, string sheetName, string[] headers, IReadOnlyList<string[]> rows, CancellationToken token)
    {
        try
        {
            content.Position = 0;
            using (var zip = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true))
            {
                HashSet<string> allowed = ["[Content_Types].xml", "_rels/.rels", "xl/workbook.xml", "xl/_rels/workbook.xml.rels", "xl/worksheets/sheet1.xml"];
                if (zip.Entries.Count != allowed.Count || zip.Entries.Select(entry => entry.FullName).Distinct(StringComparer.Ordinal).Count() != allowed.Count)
                { throw InvalidWorkbook(); }
                long expanded = 0;
                var buffer = new byte[8192];
                foreach (var entry in zip.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    if (!allowed.Contains(entry.FullName) || entry.Length < 1 || entry.Length > 33_554_432 - expanded) { throw InvalidWorkbook(); }
                    using var xml = entry.Open();
                    uint crc = uint.MaxValue;
                    long read = 0;
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        var count = xml.Read(buffer);
                        if (count == 0) { break; }
                        read += count;
                        if (read > entry.Length) { throw InvalidWorkbook(); }
                        for (var index = 0; index < count; index++)
                        {
                            crc ^= buffer[index];
                            for (var bit = 0; bit < 8; bit++) { crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u); }
                        }
                    }
                    if (read != entry.Length || ~crc != entry.Crc32) { throw InvalidWorkbook(); }
                    expanded += read;
                }
            }
            content.Position = 0;
            using var document = SpreadsheetDocument.Open(content, false, new OpenSettings { MaxCharactersInPart = 33_554_432 });
            var workbook = document.WorkbookPart;
            if (document.DocumentType != SpreadsheetDocumentType.Workbook || workbook is null || document.ExternalRelationships.Any()
                || workbook.ExternalRelationships.Any() || workbook.WorksheetParts.Count() != 1
                || new OpenXmlValidator(FileFormatVersions.Office2007) { MaxNumberOfErrors = 1 }.Validate(document, token).Any())
            { throw InvalidWorkbook(); }
            var worksheet = workbook.WorksheetParts.Single();
            var workbookRoot = workbook.Workbook ?? throw InvalidWorkbook();
            var worksheetRoot = worksheet.Worksheet ?? throw InvalidWorkbook();
            var sheets = workbookRoot.GetFirstChild<Sheets>()?.Elements<Sheet>().ToArray();
            if (sheets is not { Length: 1 } || sheets[0].Name?.Value != sheetName || sheets[0].SheetId?.Value != 1
                || sheets[0].Id?.Value != workbook.GetIdOfPart(worksheet) || worksheet.ExternalRelationships.Any()) { throw InvalidWorkbook(); }
            var actualRows = worksheetRoot.GetFirstChild<SheetData>()?.Elements<Row>().ToArray();
            if (actualRows is null || actualRows.Length != rows.Count + 1 || worksheetRoot.Descendants<Hyperlink>().Any()) { throw InvalidWorkbook(); }
            for (var index = 0; index < actualRows.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var cells = actualRows[index].Elements<Cell>().ToArray();
                var expected = index == 0 ? headers : rows[index - 1];
                if (actualRows[index].RowIndex?.Value != index + 1 || cells.Length != headers.Length) { throw InvalidWorkbook(); }
                for (var column = 0; column < cells.Length; column++)
                {
                    var cell = cells[column];
                    if (cell.DataType?.Value != CellValues.InlineString || cell.CellFormula is not null || cell.CellValue is not null
                        || cell.CellReference?.Value != ColumnName(column) + (index + 1).ToString(CultureInfo.InvariantCulture)
                        || expected[column].Length > 500 || cell.InlineString?.Text?.Text != expected[column]) { throw InvalidWorkbook(); }
                }
            }
        }
        catch (Exception error) when (error is InvalidDataException or XmlException or OpenXmlPackageException)
        { throw new InvalidDataException("export.invalid_workbook", error); }
    }

    private static string ColumnName(int column) => column < 26 ? ((char)('A' + column)).ToString() : "A" + (char)('A' + column - 26);

    private static InvalidDataException InvalidWorkbook() => new("export.invalid_workbook");

    private static void WriteRow(OpenXmlWriter writer, uint index, string[] values)
    {
        writer.WriteStartElement(new Row { RowIndex = index });
        for (var column = 0; column < values.Length; column++)
        {
            writer.WriteElement(new Cell
            {
                CellReference = ColumnName(column) + index.ToString(CultureInfo.InvariantCulture),
                DataType = CellValues.InlineString,
                InlineString = new InlineString(new Text(values[column]) { Space = SpaceProcessingModeValues.Preserve }),
            });
        }
        writer.WriteEndElement();
    }
}

// 包装 SDK 的全部定位与写入（包含 Dispose 时的 ZIP 目录），底层流始终由调用方拥有。
internal sealed class WorkbookOutputStream(Stream source, long maximum, CancellationToken token) : Stream
{
    public override bool CanRead => source.CanRead;
    public override bool CanSeek => source.CanSeek;
    public override bool CanWrite => source.CanWrite;
    public override long Length => source.Length;
    public override long Position
    {
        get => source.Position;
        set { CheckLength(value); source.Position = value; }
    }
    public override int Read(byte[] buffer, int offset, int count) { token.ThrowIfCancellationRequested(); return source.Read(buffer, offset, count); }
    public override int Read(Span<byte> buffer) { token.ThrowIfCancellationRequested(); return source.Read(buffer); }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        token.ThrowIfCancellationRequested();
        if (buffer.Length > maximum - source.Position) { throw new InvalidDataException("export.output_limit"); }
        source.Write(buffer);
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        var basis = origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => source.Position, SeekOrigin.End => source.Length, _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
        Position = checked(basis + offset);
        return Position;
    }
    public override void SetLength(long value) { CheckLength(value); source.SetLength(value); }
    public override void Flush() { token.ThrowIfCancellationRequested(); source.Flush(); }
    private void CheckLength(long value)
    {
        token.ThrowIfCancellationRequested();
        if (value < 0 || value > maximum) { throw new InvalidDataException("export.output_limit"); }
    }
}
