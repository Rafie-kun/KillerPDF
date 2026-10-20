using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace KillerPdf.Engine.Documents;

/// <summary>The spreadsheet representation for a reviewed table.</summary>
public enum PdfTableExportFormat
{
    /// <summary>An Excel workbook with explicitly typed text cells.</summary>
    Xlsx,
    /// <summary>Quoted UTF-8 CSV with formula initiators neutralized.</summary>
    Csv
}

/// <summary>Exports a reviewed table without evaluating cell contents or changing the source.</summary>
public static class PdfTableExport
{
    private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    /// <summary>Returns workbook or CSV bytes; the caller chooses and atomically writes the destination.</summary>
    public static byte[] Export(PdfExtractedTable table, PdfTableExportFormat format,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        cancellationToken.ThrowIfCancellationRequested();
        long totalCharacters = 0;
        foreach (PdfExtractedTableCell cell in table.Cells)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cell.Text.Length > 32_767)
                throw new ArgumentException("A table cell cannot contain more than 32,767 characters.", nameof(table));
            totalCharacters += cell.Text.Length;
            if (totalCharacters > 2_000_000)
                throw new ArgumentException("The table exceeds the two-million-character export limit.", nameof(table));
            for (int index = 0; index < cell.Text.Length; index++)
                if (char.IsSurrogate(cell.Text[index]))
                {
                    if (index + 1 >= cell.Text.Length || !char.IsSurrogatePair(cell.Text[index], cell.Text[index + 1]))
                        throw new ArgumentException("A table cell contains an invalid Unicode surrogate sequence.", nameof(table));
                    index++;
                }
        }
        return format switch
        {
            PdfTableExportFormat.Xlsx => Xlsx(table, cancellationToken),
            PdfTableExportFormat.Csv => Csv(table, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
    }

    private static byte[] Csv(PdfExtractedTable table, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        using (var writer = new StreamWriter(output, new UTF8Encoding(true), leaveOpen: true))
            for (int row = 0; row < table.RowCount; row++)
            {
                for (int column = 0; column < table.ColumnCount; column++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (column > 0) writer.Write(',');
                    string text = table.GetCell(row, column).Text;
                    writer.Write('"');
                    if (StartsFormula(text)) writer.Write('\'');
                    writer.Write(text.Replace("\"", "\"\"", StringComparison.Ordinal));
                    writer.Write('"');
                }
                writer.Write("\r\n");
            }
        cancellationToken.ThrowIfCancellationRequested();
        return output.ToArray();
    }

    private static bool StartsFormula(string text)
    {
        foreach (Rune rune in text.Normalize(NormalizationForm.FormKC).EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsWhiteSpace(rune) || category is UnicodeCategory.Control or UnicodeCategory.Format) continue;
            return rune.Value is '=' or '+' or '-' or '@';
        }
        return false;
    }

    private static byte[] Xlsx(PdfExtractedTable table, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "[Content_Types].xml", """
                <?xml version="1.0" encoding="utf-8"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                  <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
                </Types>
                """);
            WriteEntry(archive, "_rels/.rels", """
                <?xml version="1.0" encoding="utf-8"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            WriteEntry(archive, "xl/workbook.xml", """
                <?xml version="1.0" encoding="utf-8"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="Table" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """);
            WriteEntry(archive, "xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="utf-8"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
                </Relationships>
                """);
            WriteEntry(archive, "xl/styles.xml", """
                <?xml version="1.0" encoding="utf-8"?>
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts>
                  <fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>
                  <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
                  <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
                  <cellXfs count="1"><xf numFmtId="49" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1" applyAlignment="1"><alignment vertical="top" wrapText="1"/></xf></cellXfs>
                  <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
                </styleSheet>
                """);
            using Stream stream = archive.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal).Open();
            using XmlWriter writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
            writer.WriteStartDocument();
            writer.WriteStartElement("worksheet", SpreadsheetNamespace);
            writer.WriteStartElement("cols");
            writer.WriteStartElement("col");
            writer.WriteAttributeString("min", "1");
            writer.WriteAttributeString("max", table.ColumnCount.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("width", "24");
            writer.WriteAttributeString("customWidth", "1");
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteStartElement("sheetData");
            for (int row = 0; row < table.RowCount; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.WriteStartElement("row");
                writer.WriteAttributeString("r", (row + 1).ToString(CultureInfo.InvariantCulture));
                for (int column = 0; column < table.ColumnCount; column++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.WriteStartElement("c");
                    writer.WriteAttributeString("r", ColumnName(column) + (row + 1).ToString(CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("t", "inlineStr");
                    writer.WriteAttributeString("s", "0");
                    writer.WriteStartElement("is");
                    writer.WriteStartElement("t");
                    writer.WriteAttributeString("xml", "space", null, "preserve");
                    writer.WriteString(SpreadsheetText(table.GetCell(row, column).Text));
                    writer.WriteEndElement();
                    writer.WriteEndElement();
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return output.ToArray();
    }

    private static string SpreadsheetText(string text)
    {
        var encoded = new StringBuilder(text.Length);
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (character == '_' && index + 6 < text.Length && text[index + 1] == 'x'
                && text[index + 6] == '_' && text.AsSpan(index + 2, 4).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0)
                encoded.Append("_x005F_");
            else if (character == '\r' || character < ' ' && character is not ('\n' or '\t')
                || character is '\uFFFE' or '\uFFFF')
                encoded.Append("_x").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture)).Append('_');
            else encoded.Append(character);
        }
        return encoded.ToString();
    }

    private static string ColumnName(int zeroBasedColumn)
    {
        string name = "";
        for (int value = zeroBasedColumn + 1; value > 0; value = (value - 1) / 26)
            name = (char)('A' + (value - 1) % 26) + name;
        return name;
    }

    private static void WriteEntry(ZipArchive archive, string name, string text)
    {
        using Stream stream = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(text);
    }
}
