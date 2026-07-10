using System.IO;
using System.IO.Compression;
using System.Text;

namespace StockPlatform.Analyzer.Export;

/// <summary>
/// Minimal, dependency-free .xlsx (OOXML SpreadsheetML) writer — just enough to dump a single sheet
/// of a header row + data rows. Deliberately not a NuGet Excel library (ClosedXML/EPPlus/NPOI): this
/// app publishes as a self-contained single-file exe and only depends on OxyPlot, and a one-sheet
/// text dump doesn't justify pulling in (and bundling) a full spreadsheet engine. An .xlsx is just a
/// ZIP of a few fixed XML parts + one sheet part, all buildable with the BCL's System.IO.Compression.
///
/// Every cell is written as an inline string (t="inlineStr"), including numbers — this keeps stock
/// codes like "000066" from losing their leading zeros and preserves the exact formatted text the
/// grid shows (e.g. "+2.95%", "19.88（2026-07-09）"). The trade-off is numeric columns arrive in
/// Excel as text; for a review/export-what-you-see use case that's the right call.
/// </summary>
public static class XlsxWriter
{
    public static void Write(string path, string sheetName, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        AddEntry(zip, "[Content_Types].xml", ContentTypesXml);
        AddEntry(zip, "_rels/.rels", RootRelsXml);
        AddEntry(zip, "xl/workbook.xml", WorkbookXml(sheetName));
        AddEntry(zip, "xl/_rels/workbook.xml.rels", WorkbookRelsXml);
        AddEntry(zip, "xl/worksheets/sheet1.xml", SheetXml(headers, rows));
    }

    private static void AddEntry(ZipArchive zip, string entryPath, string content)
    {
        var entry = zip.CreateEntry(entryPath, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private const string ContentTypesXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
        "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
        "</Types>";

    private const string RootRelsXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
        "</Relationships>";

    private const string WorkbookRelsXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
        "</Relationships>";

    private static string WorkbookXml(string sheetName) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
        $"<sheets><sheet name=\"{Escape(SanitizeSheetName(sheetName))}\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" +
        "</workbook>";

    private static string SheetXml(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");

        int rowNum = 1;
        AppendRow(sb, rowNum++, headers);
        foreach (var row in rows) AppendRow(sb, rowNum++, row);

        sb.Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, int rowNum, IReadOnlyList<string> cells)
    {
        sb.Append($"<row r=\"{rowNum}\">");
        for (int col = 0; col < cells.Count; col++)
        {
            string cellRef = $"{ColumnName(col)}{rowNum}";
            sb.Append($"<c r=\"{cellRef}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Escape(cells[col] ?? "")}</t></is></c>");
        }
        sb.Append("</row>");
    }

    /// <summary>0-based column index → A, B, ..., Z, AA, AB, ...</summary>
    private static string ColumnName(int index)
    {
        var sb = new StringBuilder();
        index++;
        while (index > 0)
        {
            int rem = (index - 1) % 26;
            sb.Insert(0, (char)('A' + rem));
            index = (index - 1) / 26;
        }
        return sb.ToString();
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                default:
                    // strip control chars OOXML forbids (except tab/newline/carriage-return)
                    if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') sb.Append(' ');
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Excel sheet names: ≤31 chars, none of []:*?/\.</summary>
    private static string SanitizeSheetName(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name)
            sb.Append(c is '[' or ']' or ':' or '*' or '?' or '/' or '\\' ? '_' : c);
        var s = sb.ToString();
        return s.Length > 31 ? s[..31] : s;
    }
}
