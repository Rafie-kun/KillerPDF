using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using KillerPdf.Engine.Fonts;

namespace KillerPDF.Services;

/// <summary>
/// Document to PDF (#74): prints an Excel .xlsx workbook the way Excel pages it. Each visible
/// sheet's used range, or its print area, is cut into pages down then across, with the sheet's
/// column widths, row heights, fonts, fills, borders, merged cells, number formats, margins,
/// orientation, scaling and page breaks. Charts and pictures are not drawn.
/// </summary>
internal static class XlsxConverter
{
    internal static byte[] Convert(byte[] package) => Convert(package, CultureInfo.CurrentCulture);

    internal static byte[] Convert(byte[] package, CultureInfo culture)
    {
        using ZipArchive archive = OfficePackage.Open(package);
        var workbook = new Workbook(archive, culture);
        var fonts = new OfficeFonts();
        var pages = new List<OfficePage>();
        foreach (Sheet sheet in workbook.Sheets)
            pages.AddRange(workbook.Print(sheet, fonts));
        return OfficeDraw.Write(pages);
    }

    private sealed record Sheet(string Name, string Part, int Index);

    private sealed record FontStyle(string Name, double Size, bool Bold, bool Italic, bool Underline, bool Strike, OfficeColor Color);

    private sealed record BorderEdge(double Width, OfficeColor Color);

    private sealed record CellFormat(
        int NumberFormat, int Font, OfficeColor? Fill, BorderEdge? Left, BorderEdge? Right, BorderEdge? Top, BorderEdge? Bottom,
        string? Horizontal, string? Vertical, bool Wrap, int Indent);

    private enum CellKind { Text, Number, Boolean, Error }

    private sealed record Cell(int Row, int Column, CellKind Kind, string Raw, double Number, int Style);

    private sealed class Workbook
    {
        private readonly ZipArchive _archive;
        private readonly CultureInfo _culture;
        private readonly OfficeTheme _theme;
        private readonly List<string> _sharedStrings = [];
        private readonly Dictionary<int, string> _numberFormats = [];
        private readonly List<FontStyle> _fonts = [];
        private readonly List<CellFormat> _formats = [];
        private readonly bool _date1904;
        private readonly Dictionary<int, string> _printAreas = [];

        internal List<Sheet> Sheets { get; } = [];

        internal Workbook(ZipArchive archive, CultureInfo culture)
        {
            _archive = archive;
            _culture = culture;
            string main = OfficePackage.MainPart(archive, "xl/workbook.xml");
            XDocument document = OfficePackage.Xml(archive, main)
                ?? throw new InvalidDataException("The Excel workbook part is missing.");
            Dictionary<string, string> rels = OfficePackage.Relationships(archive, main);
            Dictionary<string, string> types = OfficePackage.RelationshipsByType(archive, main);
            _theme = OfficeTheme.Read(types.TryGetValue("theme", out string? themePart) ? OfficePackage.Xml(archive, themePart) : null);
            XElement root = document.Root!;
            _date1904 = Attr(Child(root, "workbookPr"), "date1904") is "1" or "true";

            int index = 0;
            foreach (XElement sheet in Child(root, "sheets")?.Elements() ?? [])
            {
                string? id = Attr(sheet, "id");
                string state = Attr(sheet, "state") ?? "visible";
                if (id is not null && rels.TryGetValue(id, out string? part) && state == "visible")
                    Sheets.Add(new Sheet(Attr(sheet, "name") ?? "", part, index));
                index++;
            }
            foreach (XElement name in Child(root, "definedNames")?.Elements() ?? [])
            {
                if (Attr(name, "name") != "_xlnm.Print_Area") continue;
                if (int.TryParse(Attr(name, "localSheetId"), out int sheetIndex)) _printAreas[sheetIndex] = name.Value;
            }

            if (types.TryGetValue("sharedStrings", out string? stringsPart) && OfficePackage.Xml(archive, stringsPart)?.Root is XElement strings)
            {
                foreach (XElement item in strings.Elements().Where(element => element.Name.LocalName == "si"))
                    _sharedStrings.Add(StringItem(item));
            }
            if (types.TryGetValue("styles", out string? stylesPart) && OfficePackage.Xml(archive, stylesPart)?.Root is XElement styles)
                ReadStyles(styles);
            if (_fonts.Count == 0) _fonts.Add(new FontStyle(_theme.MinorFont, 11, false, false, false, false, OfficeColor.Black));
            if (_formats.Count == 0) _formats.Add(new CellFormat(0, 0, null, null, null, null, null, null, null, false, 0));
        }

        private static string StringItem(XElement item)
        {
            var text = new StringBuilder();
            foreach (XElement element in item.Descendants().Where(element => element.Name.LocalName == "t"))
                if (element.Parent?.Name.LocalName != "rPh") text.Append(element.Value);
            return text.ToString();
        }

        private OfficeColor? Color(XElement? element)
        {
            if (element is null) return null;
            if (OfficeColor.Parse(Attr(element, "rgb")) is OfficeColor rgb) return Tinted(rgb, element);
            if (int.TryParse(Attr(element, "theme"), out int theme) && theme >= 0 && theme < 12)
            {
                // Spreadsheet theme indexes swap the first two pairs of the theme file order.
                int slot = theme switch { 0 => 1, 1 => 0, 2 => 3, 3 => 2, _ => theme };
                return Tinted(_theme.Colors[slot], element);
            }
            if (int.TryParse(Attr(element, "indexed"), out int indexed))
                return indexed switch
                {
                    8 or 64 => OfficeColor.Black, 9 or 65 => new OfficeColor(255, 255, 255),
                    10 => new OfficeColor(255, 0, 0), 11 => new OfficeColor(0, 255, 0), 12 => new OfficeColor(0, 0, 255),
                    13 => new OfficeColor(255, 255, 0), 14 => new OfficeColor(255, 0, 255), 15 => new OfficeColor(0, 255, 255),
                    22 => new OfficeColor(192, 192, 192), 23 => new OfficeColor(128, 128, 128),
                    _ => null
                };
            return null;
        }

        private static OfficeColor Tinted(OfficeColor color, XElement element) =>
            double.TryParse(Attr(element, "tint"), NumberStyles.Float, CultureInfo.InvariantCulture, out double tint)
                ? OfficeTheme.Tint(color, tint) : color;

        private void ReadStyles(XElement styles)
        {
            foreach (XElement format in Child(styles, "numFmts")?.Elements() ?? [])
                if (int.TryParse(Attr(format, "numFmtId"), out int id) && Attr(format, "formatCode") is string code)
                    _numberFormats[id] = code;

            foreach (XElement font in Child(styles, "fonts")?.Elements() ?? [])
            {
                double size = double.TryParse(Attr(Child(font, "sz"), "val"), NumberStyles.Float, CultureInfo.InvariantCulture, out double sz) && sz > 0 ? sz : 11;
                string name = Attr(Child(font, "name"), "val") ?? _theme.MinorFont;
                if (Attr(Child(font, "scheme"), "val") is string scheme)
                    name = scheme == "major" ? _theme.MajorFont : scheme == "minor" ? _theme.MinorFont : name;
                _fonts.Add(new FontStyle(name, size, On(Child(font, "b")), On(Child(font, "i")),
                    Child(font, "u") is XElement underline && Attr(underline, "val") is not "none", On(Child(font, "strike")),
                    Color(Child(font, "color")) ?? OfficeColor.Black));
            }

            var fills = new List<OfficeColor?>();
            foreach (XElement fill in Child(styles, "fills")?.Elements() ?? [])
            {
                XElement? pattern = Child(fill, "patternFill");
                string? type = Attr(pattern, "patternType");
                fills.Add(type is null or "none" ? null : Color(Child(pattern, "fgColor")) ?? Color(Child(pattern, "bgColor")));
            }

            var borders = new List<(BorderEdge? Left, BorderEdge? Right, BorderEdge? Top, BorderEdge? Bottom)>();
            foreach (XElement border in Child(styles, "borders")?.Elements() ?? [])
            {
                BorderEdge? Edge(string name)
                {
                    XElement? edge = Child(border, name);
                    string? style = Attr(edge, "style");
                    if (style is null or "none") return null;
                    double width = style switch
                    {
                        "medium" or "mediumDashed" or "mediumDashDot" or "mediumDashDotDot" or "slantDashDot" => 1,
                        "thick" or "double" => 1.5,
                        "hair" => 0.25,
                        _ => 0.5
                    };
                    return new BorderEdge(width, Color(Child(edge, "color")) ?? OfficeColor.Black);
                }
                borders.Add((Edge("left") ?? Edge("start"), Edge("right") ?? Edge("end"), Edge("top"), Edge("bottom")));
            }

            foreach (XElement xf in Child(styles, "cellXfs")?.Elements() ?? [])
            {
                int numberFormat = int.TryParse(Attr(xf, "numFmtId"), out int n) ? n : 0;
                int fontId = int.TryParse(Attr(xf, "fontId"), out int f) ? f : 0;
                int fillId = int.TryParse(Attr(xf, "fillId"), out int fi) ? fi : 0;
                int borderId = int.TryParse(Attr(xf, "borderId"), out int b) ? b : 0;
                XElement? alignment = Child(xf, "alignment");
                (BorderEdge? Left, BorderEdge? Right, BorderEdge? Top, BorderEdge? Bottom) edges =
                    borderId >= 0 && borderId < borders.Count ? borders[borderId] : default;
                _formats.Add(new CellFormat(numberFormat, fontId,
                    fillId >= 0 && fillId < fills.Count ? fills[fillId] : null,
                    edges.Left, edges.Right, edges.Top, edges.Bottom,
                    Attr(alignment, "horizontal"), Attr(alignment, "vertical"),
                    Attr(alignment, "wrapText") is "1" or "true",
                    int.TryParse(Attr(alignment, "indent"), out int indent) ? indent : 0));
            }
        }

        private CellFormat Format(int style) => style >= 0 && style < _formats.Count ? _formats[style] : _formats[0];
        private FontStyle Font(int font) => font >= 0 && font < _fonts.Count ? _fonts[font] : _fonts[0];

        // ── One sheet ────────────────────────────────────────────────────────────────────

        internal IEnumerable<OfficePage> Print(Sheet sheet, OfficeFonts fonts)
        {
            XDocument? document = OfficePackage.Xml(_archive, sheet.Part);
            if (document?.Root is not XElement root) return [];

            double defaultRow = 15, defaultColumnChars = 8.43;
            XElement? formatProps = Child(root, "sheetFormatPr");
            if (Num(Attr(formatProps, "defaultRowHeight")) is double rowHeight && rowHeight > 0) defaultRow = rowHeight;
            if (Num(Attr(formatProps, "defaultColWidth")) is double columnWidth && columnWidth > 0) defaultColumnChars = columnWidth;
            else if (Num(Attr(formatProps, "baseColWidth")) is double baseWidth && baseWidth > 0) defaultColumnChars = baseWidth + 0.71;

            var columnWidths = new Dictionary<int, double>();
            var hiddenColumns = new HashSet<int>();
            var columnStyles = new Dictionary<int, int>();
            foreach (XElement column in Child(root, "cols")?.Elements() ?? [])
            {
                if (!int.TryParse(Attr(column, "min"), out int min) || !int.TryParse(Attr(column, "max"), out int max)) continue;
                max = Math.Min(max, 16384);
                double? width = Num(Attr(column, "width"));
                bool hidden = Attr(column, "hidden") is "1" or "true";
                int.TryParse(Attr(column, "style"), out int columnStyle);
                for (int index = min; index <= max; index++)
                {
                    if (width is double value) columnWidths[index] = value;
                    if (hidden) hiddenColumns.Add(index);
                    if (columnStyle > 0) columnStyles[index] = columnStyle;
                }
            }

            var cells = new Dictionary<(int Row, int Column), Cell>();
            var rowHeights = new Dictionary<int, double>();
            var hiddenRows = new HashSet<int>();
            int lastRow = 0, lastColumn = 0, previousRow = 0;
            foreach (XElement row in Child(root, "sheetData")?.Elements() ?? [])
            {
                if (!int.TryParse(Attr(row, "r"), out int rowNumber)) rowNumber = previousRow + 1;
                previousRow = rowNumber;
                if (Num(Attr(row, "ht")) is double height && height >= 0) rowHeights[rowNumber] = height;
                if (Attr(row, "hidden") is "1" or "true") hiddenRows.Add(rowNumber);
                int nextColumn = 1;
                foreach (XElement c in row.Elements().Where(element => element.Name.LocalName == "c"))
                {
                    (int cellRow, int cellColumn) = Reference(Attr(c, "r")) ?? (rowNumber, nextColumn);
                    nextColumn = cellColumn + 1;
                    int style = int.TryParse(Attr(c, "s"), out int s) ? s : 0;
                    string type = Attr(c, "t") ?? "n";
                    string? value = Child(c, "v")?.Value;
                    Cell? cell = type switch
                    {
                        "s" => int.TryParse(value, out int shared) && shared >= 0 && shared < _sharedStrings.Count
                            ? new Cell(cellRow, cellColumn, CellKind.Text, _sharedStrings[shared], 0, style) : null,
                        "inlineStr" => new Cell(cellRow, cellColumn, CellKind.Text, StringItem(Child(c, "is") ?? new XElement("is")), 0, style),
                        "str" => new Cell(cellRow, cellColumn, CellKind.Text, value ?? "", 0, style),
                        "b" => new Cell(cellRow, cellColumn, CellKind.Boolean, value is "1" ? "TRUE" : "FALSE", 0, style),
                        "e" => new Cell(cellRow, cellColumn, CellKind.Error, value ?? "#N/A", 0, style),
                        "d" => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime date)
                            ? new Cell(cellRow, cellColumn, CellKind.Number, "", date.ToOADate(), style) : null,
                        _ => Num(value) is double number ? new Cell(cellRow, cellColumn, CellKind.Number, "", number, style)
                            : style != 0 ? new Cell(cellRow, cellColumn, CellKind.Text, "", 0, style) : null
                    };
                    if (cell is null) continue;
                    bool visible = cell.Raw.Length > 0 || cell.Kind == CellKind.Number || Format(style).Fill is not null
                        || Format(style).Left is not null || Format(style).Right is not null
                        || Format(style).Top is not null || Format(style).Bottom is not null;
                    if (!visible) continue;
                    cells[(cellRow, cellColumn)] = cell;
                    lastRow = Math.Max(lastRow, cellRow);
                    lastColumn = Math.Max(lastColumn, cellColumn);
                }
            }
            lastRow = cells.Count == 0 ? 0 : cells.Keys.Max(key => key.Row);

            var merges = new Dictionary<(int Row, int Column), (int LastRow, int LastColumn)>();
            var covered = new HashSet<(int, int)>();
            foreach (XElement merge in Child(root, "mergeCells")?.Elements() ?? [])
            {
                if (Range(Attr(merge, "ref")) is not { } span) continue;
                (int r1, int c1, int r2, int c2) = span;
                merges[(r1, c1)] = (r2, c2);
                for (int r = r1; r <= r2; r++)
                    for (int c = c1; c <= c2; c++)
                        if (r != r1 || c != c1) covered.Add((r, c));
            }

            int firstRow = 1, firstColumn = 1;
            bool printAreaSet = false;
            if (_printAreas.TryGetValue(sheet.Index, out string? area) && Range(area.Split(',')[0]) is { } printArea)
            {
                (firstRow, firstColumn, lastRow, lastColumn) = printArea;
                printAreaSet = true;
            }
            if (lastRow < firstRow || lastColumn < firstColumn) return [];

            // Page setup.
            double pageWidth = 612, pageHeight = 792;
            XElement? setup = Child(root, "pageSetup");
            int paper = int.TryParse(Attr(setup, "paperSize"), out int paperSize) ? paperSize
                : RegionInfo.CurrentRegion.IsMetric ? 9 : 1;
            (pageWidth, pageHeight) = paper switch
            {
                5 => (612, 1008), 8 => (842, 1191), 9 => (595, 842), 11 => (420, 595), 3 => (792, 1224), _ => (612, 792)
            };
            if (Attr(setup, "orientation") == "landscape") (pageWidth, pageHeight) = (pageHeight, pageWidth);
            XElement? margins = Child(root, "pageMargins");
            double left = (Num(Attr(margins, "left")) ?? 0.7) * 72, right = (Num(Attr(margins, "right")) ?? 0.7) * 72;
            double top = (Num(Attr(margins, "top")) ?? 0.75) * 72, bottom = (Num(Attr(margins, "bottom")) ?? 0.75) * 72;
            double printableWidth = Math.Max(72, pageWidth - left - right);
            double printableHeight = Math.Max(72, pageHeight - top - bottom);
            XElement? printOptions = Child(root, "printOptions");
            bool gridLines = Attr(printOptions, "gridLines") is "1" or "true";
            bool centerHorizontally = Attr(printOptions, "horizontalCentered") is "1" or "true";
            bool centerVertically = Attr(printOptions, "verticalCentered") is "1" or "true";

            double ColumnWidth(int column)
            {
                if (hiddenColumns.Contains(column)) return 0;
                double chars = columnWidths.TryGetValue(column, out double width) ? width : defaultColumnChars;
                double pixels = Math.Truncate((256 * chars + Math.Truncate(128.0 / 7)) / 256 * 7);
                return Math.Max(0, pixels * 0.75);
            }
            double RowHeight(int row) => hiddenRows.Contains(row) ? 0 : rowHeights.TryGetValue(row, out double height) ? height : defaultRow;

            // Without a print area, text that spills past the last used column still prints whole.
            if (!printAreaSet)
            {
                foreach (Cell cell in cells.Values)
                {
                    if (cell.Kind != CellKind.Text || cell.Raw.Length == 0 || merges.ContainsKey((cell.Row, cell.Column))) continue;
                    CellFormat format = Format(cell.Style);
                    if (format.Wrap || format.Horizontal is not (null or "general" or "left")) continue;
                    FontStyle font = Font(format.Font);
                    double needed = fonts.Measure(cell.Raw, new OfficeRunStyle(font.Name, font.Size, font.Bold, font.Italic)) + 4;
                    int column = cell.Column;
                    double span = ColumnWidth(column);
                    while (span < needed && column < 16384 && !cells.ContainsKey((cell.Row, column + 1)))
                    {
                        column++;
                        span += ColumnWidth(column);
                    }
                    lastColumn = Math.Max(lastColumn, column);
                }
            }

            double totalWidth = 0, totalHeight = 0;
            for (int column = firstColumn; column <= lastColumn; column++) totalWidth += ColumnWidth(column);
            for (int row = firstRow; row <= lastRow; row++) totalHeight += RowHeight(row);
            if (totalWidth <= 0 || totalHeight <= 0) return [];

            double scale = 1;
            bool fitToPage = Attr(Child(Child(root, "sheetPr"), "pageSetUpPr"), "fitToPage") is "1" or "true";
            if (fitToPage)
            {
                int fitWide = int.TryParse(Attr(setup, "fitToWidth"), out int wide) ? wide : 1;
                int fitTall = int.TryParse(Attr(setup, "fitToHeight"), out int tall) ? tall : 1;
                if (fitWide > 0) scale = Math.Min(scale, printableWidth * fitWide / totalWidth);
                if (fitTall > 0) scale = Math.Min(scale, printableHeight * fitTall / totalHeight);
            }
            else if (Num(Attr(setup, "scale")) is double percent && percent >= 10 && percent <= 400)
            {
                scale = percent / 100;
            }
            scale = Math.Max(0.1, scale);

            var rowBreaks = new HashSet<int>((Child(root, "rowBreaks")?.Elements() ?? [])
                .Select(brk => int.TryParse(Attr(brk, "id"), out int id) ? id : -1));
            var columnBreaks = new HashSet<int>((Child(root, "colBreaks")?.Elements() ?? [])
                .Select(brk => int.TryParse(Attr(brk, "id"), out int id) ? id : -1));

            List<(int First, int Last)> Bands(int first, int last, Func<int, double> size, double room, HashSet<int> breaks)
            {
                var bands = new List<(int, int)>();
                int start = first;
                double used = 0;
                for (int index = first; index <= last; index++)
                {
                    double extent = size(index) * scale;
                    if (index > start && used + extent > room + 0.01)
                    {
                        bands.Add((start, index - 1));
                        start = index;
                        used = 0;
                    }
                    used += extent;
                    if (breaks.Contains(index) && index < last)
                    {
                        bands.Add((start, index));
                        start = index + 1;
                        used = 0;
                    }
                }
                if (start <= last) bands.Add((start, last));
                return bands;
            }

            List<(int First, int Last)> columnBands = Bands(firstColumn, lastColumn, ColumnWidth, printableWidth, columnBreaks);
            List<(int First, int Last)> rowBands = Bands(firstRow, lastRow, RowHeight, printableHeight, rowBreaks);
            var pages = new List<OfficePage>();
            foreach ((int c0, int c1) in columnBands)
            {
                foreach ((int r0, int r1) in rowBands)
                {
                    var page = new OfficePage(pageWidth, pageHeight);
                    double[] xs = new double[c1 - c0 + 2];
                    double[] ys = new double[r1 - r0 + 2];
                    for (int column = c0; column <= c1; column++) xs[column - c0 + 1] = xs[column - c0] + ColumnWidth(column) * scale;
                    for (int row = r0; row <= r1; row++) ys[row - r0 + 1] = ys[row - r0] + RowHeight(row) * scale;
                    double originX = left + (centerHorizontally ? Math.Max(0, (printableWidth - xs[^1]) / 2) : 0);
                    double originY = top + (centerVertically ? Math.Max(0, (printableHeight - ys[^1]) / 2) : 0);
                    double X(int column) => originX + xs[Math.Clamp(column - c0, 0, xs.Length - 1)];
                    double Y(int row) => originY + ys[Math.Clamp(row - r0, 0, ys.Length - 1)];

                    var fillsOps = new List<OfficeDrawOp>();
                    var textOps = new List<OfficeDrawOp>();
                    var lineOps = new List<OfficeDrawOp>();
                    if (gridLines)
                    {
                        var gray = new OfficeColor(208, 208, 208);
                        for (int column = c0; column <= c1 + 1; column++)
                            lineOps.Add(new OfficeLineOp(X(column), Y(r0), X(column), Y(r1 + 1), 0.4, gray));
                        for (int row = r0; row <= r1 + 1; row++)
                            lineOps.Add(new OfficeLineOp(X(c0), Y(row), X(c1 + 1), Y(row), 0.4, gray));
                    }

                    bool Occupied(int row, int column) =>
                        cells.TryGetValue((row, column), out Cell? other) && (other.Raw.Length > 0 || other.Kind == CellKind.Number)
                        || covered.Contains((row, column));

                    for (int row = r0; row <= r1; row++)
                    {
                        if (RowHeight(row) <= 0) continue;
                        for (int column = c0; column <= c1; column++)
                        {
                            if (ColumnWidth(column) <= 0 || covered.Contains((row, column))) continue;
                            cells.TryGetValue((row, column), out Cell? cell);
                            int style = cell?.Style ?? (columnStyles.TryGetValue(column, out int columnStyle) ? columnStyle : 0);
                            if (cell is null && style == 0) continue;
                            CellFormat format = Format(style);
                            int lastMergeRow = row, lastMergeColumn = column;
                            if (merges.TryGetValue((row, column), out var merge))
                            {
                                lastMergeRow = Math.Min(merge.LastRow, r1);
                                lastMergeColumn = Math.Min(merge.LastColumn, c1);
                            }
                            double x1 = X(column), x2 = X(lastMergeColumn + 1), y1 = Y(row), y2 = Y(lastMergeRow + 1);
                            if (format.Fill is OfficeColor fill) fillsOps.Add(new OfficeRectOp(x1, y1, x2 - x1, y2 - y1, fill));
                            if (format.Top is BorderEdge borderTop) lineOps.Add(new OfficeLineOp(x1, y1, x2, y1, borderTop.Width, borderTop.Color));
                            if (format.Bottom is BorderEdge borderBottom) lineOps.Add(new OfficeLineOp(x1, y2, x2, y2, borderBottom.Width, borderBottom.Color));
                            if (format.Left is BorderEdge borderLeft) lineOps.Add(new OfficeLineOp(x1, y1, x1, y2, borderLeft.Width, borderLeft.Color));
                            if (format.Right is BorderEdge borderRight) lineOps.Add(new OfficeLineOp(x2, y1, x2, y2, borderRight.Width, borderRight.Color));
                            if (cell is null) continue;

                            string text = Display(cell, format);
                            if (text.Length == 0) continue;
                            FontStyle font = Font(format.Font);
                            var runStyle = new OfficeRunStyle(font.Name, font.Size * scale, font.Bold, font.Italic, font.Underline,
                                font.Strike, font.Color);
                            string horizontal = format.Horizontal ?? "general";
                            if (horizontal == "general")
                                horizontal = cell.Kind switch
                                {
                                    CellKind.Number => "right",
                                    CellKind.Boolean or CellKind.Error => "center",
                                    _ => "left"
                                };
                            if (horizontal is "centerContinuous" or "distributed") horizontal = "center";
                            if (horizontal == "fill") horizontal = "left";
                            double padding = 2 * scale + format.Indent * 9 * scale;
                            double cellWidth = x2 - x1;
                            bool wrap = format.Wrap || horizontal == "justify";
                            List<string> lines = wrap ? Wrap(text, runStyle, cellWidth - 4 * scale, fonts) : [text];

                            if (cell.Kind == CellKind.Number && !wrap && fonts.Measure(text, runStyle) > cellWidth - 2 * scale)
                            {
                                double hashWidth = fonts.Measure("#", runStyle);
                                int count = hashWidth > 0 ? Math.Max(1, (int)((cellWidth - 2 * scale) / hashWidth)) : 1;
                                lines = [new string('#', count)];
                                horizontal = "left";
                            }

                            // Text that does not wrap spills into empty neighbors, as Excel prints it.
                            double clipLeft = x1, clipRight = x2;
                            if (!wrap && cell.Kind == CellKind.Text && lastMergeColumn == column)
                            {
                                double needed = fonts.Measure(lines[0], runStyle) + padding * 2;
                                if (horizontal is "left" or "center")
                                    for (int next = column + 1; next <= c1 && clipRight - clipLeft < needed && !Occupied(row, next); next++)
                                        clipRight = X(next + 1);
                                if (horizontal is "right" or "center")
                                    for (int previous = column - 1; previous >= c0 && clipRight - clipLeft < needed && !Occupied(row, previous); previous--)
                                        clipLeft = X(previous);
                            }

                            TrueTypeFont primary = fonts.Primary(runStyle);
                            OfficeFontMetrics metrics = fonts.Metrics(primary);
                            double lineHeight = metrics.LineHeight * runStyle.Size;
                            double blockHeight = lineHeight * lines.Count;
                            double cellHeight = y2 - y1;
                            double firstTop = format.Vertical switch
                            {
                                "top" => y1 + scale,
                                "center" or "distributed" or "justify" => y1 + (cellHeight - blockHeight) / 2,
                                _ => y2 - blockHeight - scale
                            };
                            var inner = new List<OfficeDrawOp>();
                            for (int index = 0; index < lines.Count; index++)
                            {
                                string line = lines[index];
                                double width = fonts.Measure(line, runStyle);
                                double baseline = firstTop + lineHeight * index + (metrics.Ascent + metrics.Gap) * runStyle.Size;
                                double x = horizontal switch
                                {
                                    "right" => x2 - padding - width,
                                    "center" => x1 + (cellWidth - width) / 2,
                                    _ => x1 + padding
                                };
                                foreach ((string piece, TrueTypeFont face) in fonts.Segments(line, runStyle))
                                {
                                    inner.Add(new OfficeTextOp(x, baseline, piece, face, runStyle.Size, runStyle.Color));
                                    x += OfficeFonts.Width(piece, face, runStyle.Size);
                                }
                                if (runStyle.Underline)
                                    inner.Add(new OfficeLineOp(x - width, baseline + runStyle.Size * 0.12, x, baseline + runStyle.Size * 0.12,
                                        Math.Max(0.4, runStyle.Size * 0.06), runStyle.Color));
                                if (runStyle.Strike)
                                    inner.Add(new OfficeLineOp(x - width, baseline - runStyle.Size * 0.28, x, baseline - runStyle.Size * 0.28,
                                        Math.Max(0.4, runStyle.Size * 0.06), runStyle.Color));
                            }
                            if (inner.Count > 0)
                                textOps.Add(new OfficeClipOp(clipLeft, y1, clipRight - clipLeft, cellHeight, inner));
                        }
                    }
                    page.Ops.AddRange(fillsOps);
                    page.Ops.AddRange(lineOps);
                    page.Ops.AddRange(textOps);
                    pages.Add(page);
                }
            }
            return pages;
        }

        private string Display(Cell cell, CellFormat format)
        {
            if (cell.Kind != CellKind.Number) return cell.Raw;
            string? code = _numberFormats.TryGetValue(format.NumberFormat, out string? custom) ? custom : null;
            return OfficeNumberFormat.Format(cell.Number, format.NumberFormat, code, _date1904, _culture);
        }

        private static List<string> Wrap(string text, OfficeRunStyle style, double width, OfficeFonts fonts)
        {
            var lines = new List<string>();
            foreach (string paragraph in text.Replace("\r\n", "\n").Split('\n'))
            {
                var line = new StringBuilder();
                foreach (string word in paragraph.Split(' '))
                {
                    string candidate = line.Length == 0 ? word : line + " " + word;
                    if (line.Length > 0 && fonts.Measure(candidate, style) > width)
                    {
                        lines.Add(line.ToString());
                        line.Clear().Append(word);
                    }
                    else
                    {
                        line.Clear().Append(candidate);
                    }
                }
                lines.Add(line.ToString());
            }
            return lines;
        }
    }

    // ── References ───────────────────────────────────────────────────────────────────────

    private static (int Row, int Column)? Reference(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return null;
        int index = 0, column = 0, row = 0;
        reference = reference.Replace("$", "", StringComparison.Ordinal);
        while (index < reference.Length && char.IsAsciiLetter(reference[index]))
        {
            column = column * 26 + (char.ToUpperInvariant(reference[index]) - 'A' + 1);
            if (column > 16384) return null;
            index++;
        }
        while (index < reference.Length && char.IsAsciiDigit(reference[index]))
        {
            row = row * 10 + (reference[index] - '0');
            if (row > 1_048_576) return null;
            index++;
        }
        return index == reference.Length && row > 0 && column > 0 ? (row, column) : null;
    }

    private static (int Row1, int Column1, int Row2, int Column2)? Range(string? range)
    {
        if (string.IsNullOrWhiteSpace(range)) return null;
        int bang = range.LastIndexOf('!');
        if (bang >= 0) range = range[(bang + 1)..];
        string[] parts = range.Split(':');
        if (Reference(parts[0]) is not { } start) return null;
        (int r1, int c1) = start;
        if (parts.Length < 2) return (r1, c1, r1, c1);
        if (Reference(parts[1]) is not { } finish) return null;
        (int r2, int c2) = finish;
        return (Math.Min(r1, r2), Math.Min(c1, c2), Math.Max(r1, r2), Math.Max(c1, c2));
    }

    private static XElement? Child(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(element => element.Name.LocalName == localName);

    private static string? Attr(XElement? element, string localName)
    {
        if (element is null) return null;
        XAttribute? match = null;
        foreach (XAttribute attribute in element.Attributes())
        {
            if (attribute.Name.LocalName != localName) continue;
            if (attribute.Name.Namespace != XNamespace.None || match is null) match = attribute;
        }
        return match?.Value;
    }

    private static bool On(XElement? element) =>
        element is not null && Attr(element, "val") is not ("0" or "false");

    private static double? Num(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number)
            ? number : null;
}

/// <summary>Excel number formats: the built-in ones by id and the common custom codes.</summary>
internal static class OfficeNumberFormat
{
    private static readonly Dictionary<int, string> BuiltIn = new()
    {
        [1] = "0", [2] = "0.00", [3] = "#,##0", [4] = "#,##0.00",
        [5] = "$#,##0_);($#,##0)", [6] = "$#,##0_);[Red]($#,##0)",
        [7] = "$#,##0.00_);($#,##0.00)", [8] = "$#,##0.00_);[Red]($#,##0.00)",
        [9] = "0%", [10] = "0.00%", [11] = "0.00E+00",
        [15] = "d-mmm-yy", [16] = "d-mmm", [17] = "mmm-yy", [18] = "h:mm AM/PM", [19] = "h:mm:ss AM/PM",
        [20] = "h:mm", [21] = "h:mm:ss",
        [37] = "#,##0 ;(#,##0)", [38] = "#,##0 ;[Red](#,##0)", [39] = "#,##0.00;(#,##0.00)", [40] = "#,##0.00;[Red](#,##0.00)",
        [41] = "_(* #,##0_);_(* \\(#,##0\\);_(* \"-\"_);_(@_)",
        [42] = "_(\"$\"* #,##0_);_(\"$\"* \\(#,##0\\);_(\"$\"* \"-\"_);_(@_)",
        [43] = "_(* #,##0.00_);_(* \\(#,##0.00\\);_(* \"-\"??_);_(@_)",
        [44] = "_(\"$\"* #,##0.00_);_(\"$\"* \\(#,##0.00\\);_(\"$\"* \"-\"??_);_(@_)",
        [45] = "mm:ss", [46] = "[h]:mm:ss", [47] = "mm:ss.0", [48] = "##0.0E+0", [49] = "@"
    };

    internal static string Format(double value, int id, string? code, bool date1904, CultureInfo culture)
    {
        if (code is null)
        {
            if (id == 14) return Date(value, date1904) is DateTime shortDate ? shortDate.ToString("d", culture) : General(value, culture);
            if (id == 22)
                return Date(value, date1904) is DateTime stamp
                    ? stamp.ToString("d", culture) + " " + stamp.ToString("H:mm", culture) : General(value, culture);
            if (!BuiltIn.TryGetValue(id, out code)) return General(value, culture);
        }
        try { return Custom(value, code, date1904, culture); }
        catch (FormatException) { return General(value, culture); }
    }

    internal static string General(double value, CultureInfo culture)
    {
        if (value == 0) return "0";
        double magnitude = Math.Abs(value);
        if (magnitude >= 1e11 || magnitude < 1e-9) return value.ToString("0.#####E+00", culture);
        if (Math.Abs(value - Math.Round(value)) < 1e-9 * Math.Max(1, magnitude))
            return Math.Round(value).ToString("0", culture);
        for (int decimals = 9; decimals >= 0; decimals--)
        {
            string text = Math.Round(value, decimals).ToString("0." + new string('#', Math.Max(1, decimals)), culture);
            if (text.Length <= 11) return text;
        }
        return Math.Round(value).ToString("0", culture);
    }

    private static List<string> Sections(string code)
    {
        var sections = new List<string>();
        var current = new StringBuilder();
        bool quoted = false, bracket = false;
        for (int index = 0; index < code.Length; index++)
        {
            char c = code[index];
            if (c == '\\' && !quoted && index + 1 < code.Length)
            {
                current.Append(c).Append(code[++index]);
                continue;
            }
            if (c == '"' && !bracket) quoted = !quoted;
            else if (c == '[' && !quoted) bracket = true;
            else if (c == ']' && !quoted) bracket = false;
            if (c == ';' && !quoted && !bracket)
            {
                sections.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        sections.Add(current.ToString());
        return sections;
    }

    private static string Custom(double value, string code, bool date1904, CultureInfo culture)
    {
        List<string> sections = Sections(code);
        string section = sections[0];
        bool absolute = false;
        if (value < 0 && sections.Count >= 2 && sections[1].Length > 0)
        {
            section = sections[1];
            absolute = true;
        }
        else if (value == 0 && sections.Count >= 3 && sections[2].Length > 0)
        {
            section = sections[2];
        }
        if (section.Trim().Equals("General", StringComparison.OrdinalIgnoreCase) || section.Trim() == "@")
            return General(absolute ? Math.Abs(value) : value, culture);
        if (IsDate(section)) return DateText(value, section, date1904, culture);
        return NumberText(absolute ? Math.Abs(value) : value, section, culture);
    }

    private static bool IsDate(string section)
    {
        bool quoted = false;
        for (int index = 0; index < section.Length; index++)
        {
            char c = section[index];
            if (c == '\\') { index++; continue; }
            if (c == '"') { quoted = !quoted; continue; }
            if (quoted) continue;
            if (c == '[')
            {
                int close = section.IndexOf(']', index);
                if (close < 0) return false;
                string inside = section[(index + 1)..close].ToLowerInvariant();
                if (inside is "h" or "hh" or "m" or "mm" or "s" or "ss") return true;
                index = close;
                continue;
            }
            if (c is 'y' or 'Y' or 'd' or 'D' or 'h' or 'H' or 's' or 'S' or 'm' or 'M') return true;
        }
        return false;
    }

    private static DateTime? Date(double value, bool date1904)
    {
        if (date1904) value += 1462;
        if (value < 0 || value > 2958465) return null;
        // Excel counts the 29th of February 1900, so its serials before March 1900 run one day ahead.
        if (value >= 1 && value < 61) value += 1;
        try { return DateTime.FromOADate(value); }
        catch (ArgumentException) { return null; }
    }

    private static string NumberText(double value, string section, CultureInfo culture)
    {
        var format = new StringBuilder();
        bool hasDigit = false;
        for (int index = 0; index < section.Length; index++)
        {
            char c = section[index];
            switch (c)
            {
                case '"':
                    int close = section.IndexOf('"', index + 1);
                    if (close < 0) close = section.Length;
                    format.Append(Literal(section[(index + 1)..close]));
                    index = close;
                    break;
                case '\\':
                    if (index + 1 < section.Length) format.Append(Literal(section[++index].ToString()));
                    break;
                case '_':
                    index++;
                    format.Append(' ');
                    break;
                case '*':
                    index++;
                    break;
                case '[':
                    int end = section.IndexOf(']', index);
                    if (end < 0) { index = section.Length; break; }
                    string inside = section[(index + 1)..end];
                    if (inside.StartsWith('$'))
                    {
                        string symbol = inside[1..].Split('-')[0];
                        if (symbol.Length > 0) format.Append(Literal(symbol));
                    }
                    index = end;
                    break;
                case '?':
                    format.Append('#');
                    hasDigit = true;
                    break;
                case '0' or '#':
                    format.Append(c);
                    hasDigit = true;
                    break;
                case '.' or ',' or '%' or 'E' or 'e' or '+' or '-':
                    format.Append(c);
                    break;
                case '@':
                    break;
                default:
                    format.Append(Literal(c.ToString()));
                    break;
            }
        }
        if (!hasDigit && format.Length == 0) return General(value, culture);
        string pattern = format.ToString();
        if (!hasDigit) return value.ToString(pattern, culture);
        return value.ToString(pattern, culture);
    }

    private static string Literal(string text)
    {
        var result = new StringBuilder();
        foreach (char c in text) result.Append('\\').Append(c);
        return result.ToString();
    }

    private enum Token { Literal, Year, Month, Day, Hour, Minute, Second, AmPm, Elapsed, Fraction }

    private static string DateText(double value, string section, bool date1904, CultureInfo culture)
    {
        DateTime? maybe = Date(value, date1904);
        if (maybe is not DateTime date) return General(value, culture);
        var tokens = new List<(Token Kind, string Text)>();
        bool twelveHour = section.Contains("AM/PM", StringComparison.OrdinalIgnoreCase)
            || section.Contains("A/P", StringComparison.OrdinalIgnoreCase);
        for (int index = 0; index < section.Length; index++)
        {
            char c = section[index];
            char lower = char.ToLowerInvariant(c);
            int Run()
            {
                int count = 1;
                while (index + count < section.Length && char.ToLowerInvariant(section[index + count]) == lower) count++;
                return count;
            }
            if (c == '"')
            {
                int close = section.IndexOf('"', index + 1);
                if (close < 0) close = section.Length;
                tokens.Add((Token.Literal, section[(index + 1)..close]));
                index = close;
            }
            else if (c == '\\' && index + 1 < section.Length) tokens.Add((Token.Literal, section[++index].ToString()));
            else if (c == '_') { index++; tokens.Add((Token.Literal, " ")); }
            else if (c == '*') index++;
            else if (c == '[')
            {
                int close = section.IndexOf(']', index);
                if (close < 0) break;
                string inside = section[(index + 1)..close].ToLowerInvariant();
                if (inside.Length > 0 && inside.All(ch => ch == inside[0]) && inside[0] is 'h' or 'm' or 's')
                {
                    double total = value * (inside[0] == 'h' ? 24 : inside[0] == 'm' ? 1440 : 86400);
                    tokens.Add((Token.Elapsed, Math.Floor(total + 1e-9).ToString(new string('0', inside.Length), CultureInfo.InvariantCulture)));
                }
                index = close;
            }
            else if (section.Length - index >= 5 && section.Substring(index, 5).Equals("AM/PM", StringComparison.OrdinalIgnoreCase))
            {
                tokens.Add((Token.AmPm, date.Hour < 12 ? "AM" : "PM"));
                index += 4;
            }
            else if (section.Length - index >= 3 && section.Substring(index, 3).Equals("A/P", StringComparison.OrdinalIgnoreCase))
            {
                tokens.Add((Token.AmPm, date.Hour < 12 ? "A" : "P"));
                index += 2;
            }
            else if (lower is 'y' or 'e')
            {
                int count = Run();
                tokens.Add((Token.Year, count <= 2 ? "yy" : "yyyy"));
                index += count - 1;
            }
            else if (lower == 'm')
            {
                int count = Run();
                tokens.Add((Token.Month, new string('m', Math.Min(count, 5))));
                index += count - 1;
            }
            else if (lower == 'd')
            {
                int count = Run();
                tokens.Add((Token.Day, count switch { 1 => "d", 2 => "dd", 3 => "ddd", _ => "dddd" }));
                index += count - 1;
            }
            else if (lower == 'h')
            {
                int count = Run();
                tokens.Add((Token.Hour, twelveHour ? (count == 1 ? "h" : "hh") : (count == 1 ? "H" : "HH")));
                index += count - 1;
            }
            else if (lower == 's')
            {
                int count = Run();
                tokens.Add((Token.Second, count == 1 ? "s" : "ss"));
                index += count - 1;
            }
            else if (c == '.' && index + 1 < section.Length && section[index + 1] == '0'
                && tokens.Count > 0 && tokens[^1].Kind == Token.Second)
            {
                int count = 0;
                while (index + 1 + count < section.Length && section[index + 1 + count] == '0') count++;
                tokens.Add((Token.Fraction, new string('f', Math.Min(count, 3))));
                index += count;
            }
            else tokens.Add((Token.Literal, c.ToString()));
        }

        // m is minutes right after hours or right before seconds.
        for (int index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Kind != Token.Month) continue;
            Token? before = tokens.Take(index).Where(token => token.Kind is not Token.Literal).Select(token => (Token?)token.Kind).LastOrDefault();
            Token? after = tokens.Skip(index + 1).Where(token => token.Kind is not Token.Literal).Select(token => (Token?)token.Kind).FirstOrDefault();
            if (before is Token.Hour or Token.Elapsed && tokens[index].Text.Length <= 2 || after is Token.Second)
                tokens[index] = (Token.Minute, tokens[index].Text.Length == 1 ? "m" : "mm");
        }

        var result = new StringBuilder();
        foreach ((Token kind, string text) in tokens)
        {
            switch (kind)
            {
                case Token.Literal:
                case Token.Elapsed:
                case Token.AmPm:
                    result.Append(text);
                    break;
                case Token.Month when text.Length == 5:
                    result.Append(date.ToString("MMMM", culture)[..1]);
                    break;
                case Token.Month:
                    result.Append(date.ToString(text.Length switch { 1 => "%M", 2 => "MM", 3 => "MMM", _ => "MMMM" }, culture));
                    break;
                default:
                    string single = text.Length == 1 ? "%" + text : text;
                    result.Append(date.ToString(single, culture));
                    break;
            }
        }
        return result.ToString();
    }
}
