using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Fonts;

namespace KillerPDF.Services;

// Document to PDF (#74): flowing text for Word documents. The reader builds sections of
// paragraphs and tables; this lays them out into lines, breaks them across pages, and draws the
// headers and footers once the page count is known.

internal abstract record OfficeInline;
internal sealed record OfficeText(string Text, OfficeRunStyle Style) : OfficeInline;
/// <summary>A tab; an absolute alignment comes from a positional tab that ignores the stops.</summary>
internal sealed record OfficeTab(OfficeRunStyle Style, OfficeTabKind? Absolute = null) : OfficeInline;
internal sealed record OfficeLineBreak(OfficeRunStyle Style, bool Page) : OfficeInline;
internal sealed record OfficePicture(PdfImage Image, double Width, double Height) : OfficeInline;
internal enum OfficeFieldKind { Page, NumPages }
internal sealed record OfficeField(OfficeFieldKind Kind, OfficeRunStyle Style) : OfficeInline;

internal enum OfficeTabKind { Left, Center, Right, Decimal }
internal readonly record struct OfficeTabStop(double Position, OfficeTabKind Kind, char Leader);

internal enum OfficeAlignment { Left, Center, Right, Justify }
internal enum OfficeLineRule { Multiple, Exact, AtLeast }
internal enum OfficeCellAlignment { Top, Center, Bottom }

internal abstract class OfficeBlock;

internal sealed class OfficeParagraph : OfficeBlock
{
    internal List<OfficeInline> Inlines { get; } = [];
    internal OfficeRunStyle MarkStyle { get; set; } = new("Calibri", 11);
    internal OfficeAlignment Alignment { get; set; }
    internal double IndentLeft { get; set; }
    internal double IndentRight { get; set; }
    /// <summary>First line offset from the left indent; negative for a hanging indent.</summary>
    internal double FirstLine { get; set; }
    internal double SpaceBefore { get; set; }
    internal double SpaceAfter { get; set; }
    internal double LineValue { get; set; } = 1;
    internal OfficeLineRule LineRule { get; set; }
    internal bool PageBreakBefore { get; set; }
    internal bool ContextualSpacing { get; set; }
    internal string? StyleId { get; set; }
    internal string? Label { get; set; }
    internal OfficeRunStyle? LabelStyle { get; set; }
    internal OfficeColor? Shading { get; set; }
    internal List<OfficeTabStop> Tabs { get; } = [];
}

internal sealed record OfficeBorder(double Width, OfficeColor Color);

/// <summary>A cell edge set on the cell itself; a null border there means the cell turns it off.</summary>
internal sealed record OfficeBorderOverride(OfficeBorder? Border);

internal sealed class OfficeTableCell
{
    internal List<OfficeBlock> Blocks { get; } = [];
    internal int Column { get; set; }
    internal int Span { get; set; } = 1;
    internal OfficeColor? Shading { get; set; }
    internal bool MergedFromAbove { get; set; }
    internal bool MergesBelow { get; set; }
    internal OfficeCellAlignment VerticalAlignment { get; set; }
    internal OfficeBorderOverride? Top { get; set; }
    internal OfficeBorderOverride? Bottom { get; set; }
    internal OfficeBorderOverride? Left { get; set; }
    internal OfficeBorderOverride? Right { get; set; }
}

internal sealed class OfficeTableRow
{
    internal List<OfficeTableCell> Cells { get; } = [];
    internal bool Header { get; set; }
    internal double Height { get; set; }
    internal bool ExactHeight { get; set; }
}

internal sealed class OfficeTable : OfficeBlock
{
    internal List<double> Columns { get; } = [];
    internal List<OfficeTableRow> Rows { get; } = [];
    internal double Indent { get; set; }
    internal OfficeAlignment Alignment { get; set; }
    internal OfficeBorder? Top { get; set; }
    internal OfficeBorder? Bottom { get; set; }
    internal OfficeBorder? Left { get; set; }
    internal OfficeBorder? Right { get; set; }
    internal OfficeBorder? InsideHorizontal { get; set; }
    internal OfficeBorder? InsideVertical { get; set; }
    internal double PaddingLeft { get; set; } = 5.4;
    internal double PaddingRight { get; set; } = 5.4;
    internal double PaddingTop { get; set; }
    internal double PaddingBottom { get; set; }
}

internal sealed class OfficePageSetup
{
    internal double Width { get; set; } = 612;
    internal double Height { get; set; } = 792;
    internal double MarginTop { get; set; } = 72;
    internal double MarginBottom { get; set; } = 72;
    internal double MarginLeft { get; set; } = 72;
    internal double MarginRight { get; set; } = 72;
    internal double HeaderDistance { get; set; } = 36;
    internal double FooterDistance { get; set; } = 36;
    internal double ContentWidth => Math.Max(36, Width - MarginLeft - MarginRight);
}

internal sealed class OfficeSection
{
    internal OfficePageSetup Page { get; set; } = new();
    internal List<OfficeBlock> Blocks { get; } = [];
    internal List<OfficeBlock>? Header { get; set; }
    internal List<OfficeBlock>? Footer { get; set; }
    internal List<OfficeBlock>? FirstHeader { get; set; }
    internal List<OfficeBlock>? FirstFooter { get; set; }
    internal bool TitlePage { get; set; }
    internal bool Continuous { get; set; }
}

internal sealed class OfficeFlowLayout(OfficeFonts fonts, double defaultTab)
{
    private readonly OfficeFonts _fonts = fonts;
    private readonly double _defaultTab = defaultTab > 1 ? defaultTab : 36;

    /// <summary>A horizontal slice of laid out content. Ops are relative to its top left.</summary>
    private sealed class Strip
    {
        internal double Height { get; init; }
        internal List<OfficeDrawOp> Ops { get; init; } = [];
        internal bool BreakBefore { get; set; }
        internal bool Spacer { get; init; }
    }

    // ── Pages ────────────────────────────────────────────────────────────────────────────

    private sealed class PageRecord(OfficePage page, OfficeSection section, bool firstOfSection)
    {
        internal OfficePage Page { get; } = page;
        internal OfficeSection Section { get; } = section;
        internal bool FirstOfSection { get; } = firstOfSection;
    }

    private sealed class Paginator
    {
        private readonly List<PageRecord> _pages = [];
        private OfficeSection? _section;
        private OfficePage? _page;
        private double _y;
        private bool _sectionStarted;

        internal IReadOnlyList<PageRecord> Pages => _pages;
        internal OfficeSection Section => _section!;
        private double Top => _section!.Page.MarginTop;
        private double Bottom => _page!.Height - _section!.Page.MarginBottom;
        internal double Remaining => Bottom - _y;
        internal double FullHeight => Bottom - Top;
        internal bool Empty => _y <= Top + 0.01;

        internal void StartSection(OfficeSection section)
        {
            bool newPage = _page is null || !section.Continuous;
            _section = section;
            _sectionStarted = false;
            if (newPage) NewPage();
        }

        internal void NewPage()
        {
            OfficePageSetup setup = _section!.Page;
            _page = new OfficePage(setup.Width, setup.Height);
            _pages.Add(new PageRecord(_page, _section, !_sectionStarted));
            _sectionStarted = true;
            _y = setup.MarginTop;
        }

        internal void Place(Strip strip)
        {
            if (strip.BreakBefore && !Empty) NewPage();
            if (strip.Spacer && Empty) return;
            if (_y + strip.Height > Bottom + 0.01 && !Empty)
            {
                NewPage();
                if (strip.Spacer) return;
            }
            Draw(strip.Ops, strip.Height);
        }

        internal void Draw(IEnumerable<OfficeDrawOp> ops, double height)
        {
            OfficeDraw.AddMoved(_page!.Ops, ops, _section!.Page.MarginLeft, _y);
            _y += height;
        }
    }

    internal IReadOnlyList<OfficePage> Layout(IReadOnlyList<OfficeSection> sections)
    {
        var paginator = new Paginator();
        foreach (OfficeSection section in sections)
        {
            paginator.StartSection(section);
            double width = section.Page.ContentWidth;
            for (int index = 0; index < section.Blocks.Count; index++)
            {
                OfficeBlock block = section.Blocks[index];
                if (block is OfficeTable table)
                {
                    PlaceTable(paginator, table, width);
                    continue;
                }
                var paragraph = (OfficeParagraph)block;
                OfficeParagraph? previous = index > 0 ? section.Blocks[index - 1] as OfficeParagraph : null;
                OfficeParagraph? next = index + 1 < section.Blocks.Count ? section.Blocks[index + 1] as OfficeParagraph : null;
                foreach (Strip strip in LayoutParagraph(paragraph, width, previous, next, 0, 0))
                    paginator.Place(strip);
            }
        }
        if (paginator.Pages.Count == 0) return [];

        int count = paginator.Pages.Count;
        for (int index = 0; index < count; index++)
        {
            PageRecord record = paginator.Pages[index];
            OfficeSection section = record.Section;
            bool first = record.FirstOfSection && section.TitlePage;
            List<OfficeBlock>? header = first ? section.FirstHeader : section.Header;
            List<OfficeBlock>? footer = first ? section.FirstFooter : section.Footer;
            double width = section.Page.ContentWidth;
            if (header is { Count: > 0 })
            {
                double y = section.Page.HeaderDistance;
                foreach (Strip strip in LayoutBlocks(header, width, index + 1, count))
                {
                    OfficeDraw.AddMoved(record.Page.Ops, strip.Ops, section.Page.MarginLeft, y);
                    y += strip.Height;
                }
            }
            if (footer is { Count: > 0 })
            {
                List<Strip> strips = LayoutBlocks(footer, width, index + 1, count);
                double y = record.Page.Height - section.Page.FooterDistance - strips.Sum(strip => strip.Height);
                foreach (Strip strip in strips)
                {
                    OfficeDraw.AddMoved(record.Page.Ops, strip.Ops, section.Page.MarginLeft, y);
                    y += strip.Height;
                }
            }
        }
        return [.. paginator.Pages.Select(record => record.Page)];
    }

    // Blocks laid out without page breaks: headers, footers and table cells.
    private List<Strip> LayoutBlocks(IReadOnlyList<OfficeBlock> blocks, double width, int page, int pages)
    {
        var strips = new List<Strip>();
        for (int index = 0; index < blocks.Count; index++)
        {
            if (blocks[index] is OfficeTable table)
            {
                OfficeTableGeometry geometry = Geometry(table, width);
                for (int row = 0; row < table.Rows.Count; row++)
                {
                    RowLayout layout = LayoutRow(table, geometry, row, page, pages);
                    strips.Add(new Strip { Height = layout.Height, Ops = DrawRow(table, geometry, layout, null, layout.Height) });
                }
                continue;
            }
            var paragraph = (OfficeParagraph)blocks[index];
            OfficeParagraph? previous = index > 0 ? blocks[index - 1] as OfficeParagraph : null;
            OfficeParagraph? next = index + 1 < blocks.Count ? blocks[index + 1] as OfficeParagraph : null;
            foreach (Strip strip in LayoutParagraph(paragraph, width, previous, next, page, pages))
            {
                strip.BreakBefore = false;
                strips.Add(strip);
            }
        }
        return strips;
    }

    // ── Tables ───────────────────────────────────────────────────────────────────────────

    private sealed record OfficeTableGeometry(double Left, double[] ColumnLefts, double[] ColumnWidths);

    private sealed class CellLayout
    {
        internal required OfficeTableCell Cell { get; init; }
        internal double X { get; init; }
        internal double Width { get; init; }
        internal List<Strip> Content { get; init; } = [];
        internal double ContentHeight => Content.Sum(strip => strip.Height);
    }

    private sealed class RowLayout
    {
        internal int Index { get; init; }
        internal List<CellLayout> Cells { get; init; } = [];
        internal double Height { get; set; }
    }

    private static OfficeTableGeometry Geometry(OfficeTable table, double width)
    {
        int columns = Math.Max(table.Columns.Count,
            table.Rows.Count == 0 ? 1 : table.Rows.Max(row => row.Cells.Count == 0 ? 0 : row.Cells.Max(cell => cell.Column + cell.Span)));
        columns = Math.Max(1, columns);
        double[] widths = new double[columns];
        double known = 0;
        for (int index = 0; index < columns; index++)
        {
            widths[index] = index < table.Columns.Count ? Math.Max(0, table.Columns[index]) : 0;
            known += widths[index];
        }
        if (known <= 0.5)
        {
            for (int index = 0; index < columns; index++) widths[index] = width / columns;
            known = width;
        }
        else
        {
            int missing = widths.Count(value => value <= 0);
            if (missing > 0)
            {
                double share = Math.Max(18, (width - known) / missing);
                for (int index = 0; index < columns; index++) if (widths[index] <= 0) widths[index] = share;
                known = widths.Sum();
            }
        }
        double available = Math.Max(36, width - Math.Max(0, table.Indent));
        if (known > available)
        {
            double scale = available / known;
            for (int index = 0; index < columns; index++) widths[index] *= scale;
            known = available;
        }
        double left = table.Alignment switch
        {
            OfficeAlignment.Center => Math.Max(0, (width - known) / 2),
            OfficeAlignment.Right => Math.Max(0, width - known),
            _ => Math.Max(0, Math.Min(table.Indent, width - known))
        };
        double[] lefts = new double[columns];
        double x = left;
        for (int index = 0; index < columns; index++)
        {
            lefts[index] = x;
            x += widths[index];
        }
        return new OfficeTableGeometry(left, lefts, widths);
    }

    private RowLayout LayoutRow(OfficeTable table, OfficeTableGeometry geometry, int rowIndex, int page, int pages)
    {
        OfficeTableRow row = table.Rows[rowIndex];
        var layout = new RowLayout { Index = rowIndex };
        double tallest = 0;
        foreach (OfficeTableCell cell in row.Cells)
        {
            int first = Math.Clamp(cell.Column, 0, geometry.ColumnWidths.Length - 1);
            int last = Math.Clamp(cell.Column + cell.Span - 1, first, geometry.ColumnWidths.Length - 1);
            double x = geometry.ColumnLefts[first];
            double cellWidth = geometry.ColumnLefts[last] + geometry.ColumnWidths[last] - x;
            double inner = Math.Max(4, cellWidth - table.PaddingLeft - table.PaddingRight);
            List<Strip> content = cell.MergedFromAbove ? [] : LayoutBlocks(cell.Blocks, inner, page, pages);
            var cellLayout = new CellLayout { Cell = cell, X = x, Width = cellWidth, Content = content };
            layout.Cells.Add(cellLayout);
            tallest = Math.Max(tallest, cellLayout.ContentHeight);
        }
        double height = tallest + table.PaddingTop + table.PaddingBottom;
        if (row.Height > 0) height = row.ExactHeight ? row.Height : Math.Max(height, row.Height);
        layout.Height = Math.Max(height, 2);
        return layout;
    }

    // Draws a row, or the slice of it given by taken[cell] = (first strip, strip count).
    private static List<OfficeDrawOp> DrawRow(
        OfficeTable table, OfficeTableGeometry geometry, RowLayout layout,
        (int Start, int Count)[]? taken, double height)
    {
        var ops = new List<OfficeDrawOp>();
        int columns = geometry.ColumnWidths.Length;
        bool lastRow = layout.Index == table.Rows.Count - 1;
        for (int index = 0; index < layout.Cells.Count; index++)
        {
            CellLayout cell = layout.Cells[index];
            if (cell.Cell.Shading is OfficeColor shade)
                ops.Add(new OfficeRectOp(cell.X, 0, cell.Width, height, shade));
        }
        for (int index = 0; index < layout.Cells.Count; index++)
        {
            CellLayout cell = layout.Cells[index];
            (int start, int count) = taken?[index] ?? (0, cell.Content.Count);
            double used = 0;
            for (int strip = start; strip < start + count; strip++) used += cell.Content[strip].Height;
            double offset = table.PaddingTop;
            if (taken is null)
            {
                double free = height - table.PaddingTop - table.PaddingBottom - used;
                if (free > 0)
                    offset += cell.Cell.VerticalAlignment switch
                    {
                        OfficeCellAlignment.Center => free / 2,
                        OfficeCellAlignment.Bottom => free,
                        _ => 0
                    };
            }
            var inner = new List<OfficeDrawOp>();
            double y = offset;
            for (int strip = start; strip < start + count; strip++)
            {
                OfficeDraw.AddMoved(inner, cell.Content[strip].Ops, cell.X + table.PaddingLeft, y);
                y += cell.Content[strip].Height;
            }
            if (table.Rows[layout.Index].ExactHeight)
                ops.Add(new OfficeClipOp(cell.X, 0, cell.Width, height, inner));
            else
                ops.AddRange(inner);
        }
        for (int index = 0; index < layout.Cells.Count; index++)
        {
            CellLayout cell = layout.Cells[index];
            OfficeTableCell source = cell.Cell;
            bool firstColumn = source.Column <= 0;
            bool lastColumn = source.Column + source.Span >= columns;
            OfficeBorder? top = source.MergedFromAbove ? null
                : source.Top is { } t ? t.Border : layout.Index == 0 ? table.Top : table.InsideHorizontal;
            OfficeBorder? bottom = source.MergesBelow ? null
                : source.Bottom is { } b ? b.Border : lastRow ? table.Bottom : table.InsideHorizontal;
            OfficeBorder? left = source.Left is { } l ? l.Border : firstColumn ? table.Left : table.InsideVertical;
            OfficeBorder? right = source.Right is { } r ? r.Border : lastColumn ? table.Right : table.InsideVertical;
            double x1 = cell.X, x2 = cell.X + cell.Width;
            if (top is not null) ops.Add(new OfficeLineOp(x1, 0, x2, 0, top.Width, top.Color));
            if (bottom is not null) ops.Add(new OfficeLineOp(x1, height, x2, height, bottom.Width, bottom.Color));
            if (left is not null) ops.Add(new OfficeLineOp(x1, 0, x1, height, left.Width, left.Color));
            if (right is not null) ops.Add(new OfficeLineOp(x2, 0, x2, height, right.Width, right.Color));
        }
        return ops;
    }

    private void PlaceTable(Paginator paginator, OfficeTable table, double width)
    {
        if (table.Rows.Count == 0) return;
        OfficeTableGeometry geometry = Geometry(table, width);
        var headers = new List<RowLayout>();
        for (int row = 0; row < table.Rows.Count && table.Rows[row].Header; row++)
            headers.Add(LayoutRow(table, geometry, row, 0, 0));

        void NewPageWithHeaders(int currentRow)
        {
            paginator.NewPage();
            if (currentRow < headers.Count) return;
            foreach (RowLayout header in headers)
                if (header.Height < paginator.FullHeight / 2)
                    paginator.Draw(DrawRow(table, geometry, header, null, header.Height), header.Height);
        }

        for (int rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            RowLayout layout = rowIndex < headers.Count ? headers[rowIndex] : LayoutRow(table, geometry, rowIndex, 0, 0);
            if (layout.Height <= paginator.Remaining + 0.01)
            {
                paginator.Draw(DrawRow(table, geometry, layout, null, layout.Height), layout.Height);
                continue;
            }
            if (layout.Height <= paginator.FullHeight && !paginator.Empty)
            {
                NewPageWithHeaders(rowIndex);
                paginator.Draw(DrawRow(table, geometry, layout, null, layout.Height), layout.Height);
                continue;
            }

            // Taller than a page: split the cells line by line.
            int[] next = new int[layout.Cells.Count];
            bool fresh = paginator.Empty;
            while (true)
            {
                double room = paginator.Remaining - table.PaddingTop - table.PaddingBottom;
                var taken = new (int Start, int Count)[layout.Cells.Count];
                double used = 0;
                bool any = false;
                for (int index = 0; index < layout.Cells.Count; index++)
                {
                    List<Strip> content = layout.Cells[index].Content;
                    int start = next[index], end = start;
                    double sum = 0;
                    while (end < content.Count && (sum + content[end].Height <= room || (end == start && fresh)))
                    {
                        sum += content[end].Height;
                        end++;
                    }
                    taken[index] = (start, end - start);
                    next[index] = end;
                    used = Math.Max(used, sum);
                    any |= end > start;
                }
                bool done = layout.Cells.Select((cell, index) => next[index] >= cell.Content.Count).All(finished => finished);
                if (any || done)
                {
                    double height = used + table.PaddingTop + table.PaddingBottom;
                    if (done && table.Rows[rowIndex].Height > 0 && !table.Rows[rowIndex].ExactHeight)
                        height = Math.Max(height, Math.Min(table.Rows[rowIndex].Height, paginator.Remaining));
                    paginator.Draw(DrawRow(table, geometry, layout, taken, height), height);
                }
                if (done) break;
                NewPageWithHeaders(rowIndex);
                fresh = true;
            }
        }
    }

    // ── Paragraphs ───────────────────────────────────────────────────────────────────────

    private enum AtomKind { Text, Space, Tab, Break, PageBreak, Picture }

    private sealed class Atom
    {
        internal AtomKind Kind { get; init; }
        internal string Text { get; init; } = "";
        internal TrueTypeFont? Font { get; init; }
        internal required OfficeRunStyle Style { get; init; }
        internal double Width { get; set; }
        internal bool Glue { get; set; }
        internal PdfImage? Image { get; init; }
        internal double ImageHeight { get; init; }
        internal OfficeTabKind? Absolute { get; init; }
    }

    private sealed class Placed(Atom atom, double x)
    {
        internal Atom Atom { get; } = atom;
        internal double X { get; set; } = x;
    }

    private sealed class Line
    {
        internal List<Placed> Items { get; } = [];
        internal List<(double From, double To, char Leader, OfficeRunStyle Style)> Leaders { get; } = [];
        internal bool HardEnd { get; set; }
        internal bool BreakBefore { get; set; }
        internal bool HasTab { get; set; }
        internal double Start { get; set; }
    }

    private static bool IsCjk(int scalar) =>
        scalar is >= 0x2E80 and <= 0x9FFF or >= 0xAC00 and <= 0xD7AF or >= 0xF900 and <= 0xFAFF
            or >= 0xFF00 and <= 0xFFEF or >= 0x20000 and <= 0x2FA1F;

    private List<Atom> Tokenize(OfficeParagraph paragraph, double width, int page, int pages)
    {
        var atoms = new List<Atom>();
        bool breakable = true;
        void AddText(string text, OfficeRunStyle style)
        {
            foreach ((string piece, TrueTypeFont font) in _fonts.Segments(text, style))
            {
                var word = new System.Text.StringBuilder();
                void Flush()
                {
                    if (word.Length == 0) return;
                    string value = word.ToString();
                    atoms.Add(new Atom
                    {
                        Kind = AtomKind.Text, Text = value, Font = font, Style = style,
                        Width = OfficeFonts.Width(value, font, style.DrawSize), Glue = !breakable
                    });
                    breakable = false;
                    word.Clear();
                }
                foreach (System.Text.Rune rune in piece.EnumerateRunes())
                {
                    if (rune.Value == ' ')
                    {
                        Flush();
                        atoms.Add(new Atom
                        {
                            Kind = AtomKind.Space, Text = " ", Font = font, Style = style,
                            Width = OfficeFonts.Width(" ", font, style.DrawSize)
                        });
                        breakable = true;
                    }
                    else if (IsCjk(rune.Value))
                    {
                        Flush();
                        breakable = true;
                        word.Append(rune.ToString());
                        Flush();
                        breakable = true;
                    }
                    else
                    {
                        word.Append(rune.ToString());
                        if (rune.Value == '-' || rune.Value == '/')
                        {
                            Flush();
                            breakable = true;
                        }
                    }
                }
                Flush();
            }
        }

        if (!string.IsNullOrEmpty(paragraph.Label))
        {
            AddText(paragraph.Label, paragraph.LabelStyle ?? paragraph.MarkStyle);
            atoms.Add(new Atom { Kind = AtomKind.Tab, Style = paragraph.LabelStyle ?? paragraph.MarkStyle });
            breakable = true;
        }
        double maxPictureWidth = Math.Max(4, width - paragraph.IndentLeft - paragraph.IndentRight);
        foreach (OfficeInline inline in paragraph.Inlines)
        {
            switch (inline)
            {
                case OfficeText text:
                    AddText(text.Text, text.Style);
                    break;
                case OfficeField field:
                    AddText(field.Kind == OfficeFieldKind.Page
                        ? page.ToString(System.Globalization.CultureInfo.CurrentCulture)
                        : pages.ToString(System.Globalization.CultureInfo.CurrentCulture), field.Style);
                    break;
                case OfficeTab tab:
                    atoms.Add(new Atom { Kind = AtomKind.Tab, Style = tab.Style, Absolute = tab.Absolute });
                    breakable = true;
                    break;
                case OfficeLineBreak lineBreak:
                    atoms.Add(new Atom { Kind = lineBreak.Page ? AtomKind.PageBreak : AtomKind.Break, Style = lineBreak.Style });
                    breakable = true;
                    break;
                case OfficePicture picture:
                    double scale = Math.Min(1, maxPictureWidth / Math.Max(1, picture.Width));
                    atoms.Add(new Atom
                    {
                        Kind = AtomKind.Picture, Image = picture.Image, Style = paragraph.MarkStyle,
                        Width = picture.Width * scale, ImageHeight = picture.Height * scale, Glue = !breakable
                    });
                    breakable = true;
                    break;
            }
        }
        return atoms;
    }

    private List<Line> BreakLines(OfficeParagraph paragraph, List<Atom> atoms, double width)
    {
        var lines = new List<Line>();
        double right = width - paragraph.IndentRight;
        Line line = new() { Start = paragraph.IndentLeft + paragraph.FirstLine };
        double x = line.Start;
        (OfficeTabKind Kind, double Stop, int From, double TabStart, char Leader, OfficeRunStyle Style)? pending = null;

        void ResolvePending()
        {
            if (pending is not { } tab) return;
            pending = null;
            double segment = x - tab.TabStart;
            double start = tab.Kind == OfficeTabKind.Center ? tab.Stop - segment / 2 : tab.Stop - segment;
            start = Math.Max(tab.TabStart, start);
            double shift = start - tab.TabStart;
            for (int index = tab.From; index < line.Items.Count; index++) line.Items[index].X += shift;
            x += shift;
            if (tab.Leader != '\0' && shift > 0) line.Leaders.Add((tab.TabStart, start, tab.Leader, tab.Style));
        }

        void Finish(bool hard, bool pageBreakAfter)
        {
            ResolvePending();
            line.HardEnd = hard;
            lines.Add(line);
            bool breakNext = pageBreakAfter;
            line = new Line { Start = paragraph.IndentLeft, BreakBefore = breakNext };
            x = line.Start;
        }

        bool HasContent() => line.Items.Any(item => item.Atom.Kind != AtomKind.Space);

        int i = 0;
        while (i < atoms.Count)
        {
            Atom atom = atoms[i];
            switch (atom.Kind)
            {
                case AtomKind.Break:
                case AtomKind.PageBreak:
                    if (line.Items.Count == 0)
                        line.Items.Add(new Placed(new Atom { Kind = AtomKind.Space, Style = atom.Style, Width = 0 }, x));
                    Finish(true, atom.Kind == AtomKind.PageBreak);
                    i++;
                    continue;
                case AtomKind.Space:
                    if (line.Items.Count == 0 && lines.Count > 0 && !lines[^1].HardEnd) { i++; continue; }
                    line.Items.Add(new Placed(atom, x));
                    x += atom.Width;
                    i++;
                    continue;
                case AtomKind.Tab:
                {
                    ResolvePending();
                    line.HasTab = true;
                    if (atom.Absolute is OfficeTabKind absolute)
                    {
                        double stopAbsolute = absolute switch
                        {
                            OfficeTabKind.Center => width / 2,
                            OfficeTabKind.Right => right,
                            _ => paragraph.IndentLeft
                        };
                        pending = absolute == OfficeTabKind.Left ? null
                            : (absolute, stopAbsolute, line.Items.Count, x, '\0', atom.Style);
                        if (absolute == OfficeTabKind.Left) x = Math.Max(x, stopAbsolute);
                        i++;
                        continue;
                    }
                    OfficeTabStop stop = NextTabStop(paragraph, x);
                    if (stop.Position > right + 0.01 && HasContent())
                    {
                        Finish(false, false);
                        i++;
                        continue;
                    }
                    if (stop.Kind is OfficeTabKind.Left)
                    {
                        if (stop.Leader != '\0') line.Leaders.Add((x, stop.Position, stop.Leader, atom.Style));
                        x = stop.Position;
                    }
                    else
                    {
                        pending = (stop.Kind, stop.Position, line.Items.Count, x, stop.Leader, atom.Style);
                    }
                    i++;
                    continue;
                }
            }

            int end = i + 1;
            while (end < atoms.Count && atoms[end].Glue && atoms[end].Kind is AtomKind.Text or AtomKind.Picture) end++;
            double groupWidth = 0;
            for (int index = i; index < end; index++) groupWidth += atoms[index].Width;
            if (x + groupWidth > right + 0.01 && HasContent() && pending is null)
            {
                Finish(false, false);
                continue;
            }
            if (x + groupWidth > right + 0.01 && !HasContent() && end - i >= 1 && SplitGroup(atoms, i, end))
                continue;
            for (int index = i; index < end; index++)
            {
                line.Items.Add(new Placed(atoms[index], x));
                x += atoms[index].Width;
            }
            i = end;
        }
        if (line.Items.Count > 0 || lines.Count == 0 || line.BreakBefore) Finish(true, false);
        return lines;
    }

    // Breaks an over-long word into single characters so it can wrap. False when it already is.
    private static bool SplitGroup(List<Atom> atoms, int start, int end)
    {
        var pieces = new List<Atom>();
        bool changed = false;
        for (int index = start; index < end; index++)
        {
            Atom atom = atoms[index];
            if (atom.Kind != AtomKind.Text || atom.Font is null)
            {
                pieces.Add(atom);
                continue;
            }
            bool first = true;
            foreach (System.Text.Rune rune in atom.Text.EnumerateRunes())
            {
                string text = rune.ToString();
                pieces.Add(new Atom
                {
                    Kind = AtomKind.Text, Text = text, Font = atom.Font, Style = atom.Style,
                    Width = OfficeFonts.Width(text, atom.Font, atom.Style.DrawSize),
                    Glue = first && index == start && atom.Glue
                });
                first = false;
            }
            changed |= atom.Text.Length > 1 || index > start;
        }
        if (!changed) return false;
        atoms.RemoveRange(start, end - start);
        atoms.InsertRange(start, pieces);
        return true;
    }

    private OfficeTabStop NextTabStop(OfficeParagraph paragraph, double x)
    {
        OfficeTabStop? custom = null;
        foreach (OfficeTabStop stop in paragraph.Tabs.OrderBy(stop => stop.Position))
        {
            if (stop.Position > x + 0.01)
            {
                custom = stop;
                break;
            }
        }
        if (paragraph.FirstLine < 0 && x < paragraph.IndentLeft - 0.01
            && (custom is null || custom.Value.Position > paragraph.IndentLeft))
            return new OfficeTabStop(paragraph.IndentLeft, OfficeTabKind.Left, '\0');
        if (custom is not null) return custom.Value;
        double next = (Math.Floor(x / _defaultTab + 0.0001) + 1) * _defaultTab;
        return new OfficeTabStop(next, OfficeTabKind.Left, '\0');
    }

    private List<Strip> LayoutParagraph(
        OfficeParagraph paragraph, double width, OfficeParagraph? previous, OfficeParagraph? next, int page, int pages)
    {
        var strips = new List<Strip>();
        double before = paragraph.ContextualSpacing && previous?.StyleId == paragraph.StyleId && previous is not null
            ? 0 : paragraph.SpaceBefore;
        double after = paragraph.ContextualSpacing && next?.StyleId == paragraph.StyleId && next is not null
            ? 0 : paragraph.SpaceAfter;
        if (before > 0) strips.Add(new Strip { Height = before, Spacer = true });

        List<Atom> atoms = Tokenize(paragraph, width, page, pages);
        List<Line> lines = BreakLines(paragraph, atoms, width);
        for (int index = 0; index < lines.Count; index++)
        {
            Line line = lines[index];
            bool last = index == lines.Count - 1;
            strips.Add(DrawLine(paragraph, line, width, last));
        }
        if (after > 0) strips.Add(new Strip { Height = after, Spacer = true });
        if (paragraph.PageBreakBefore && strips.Count > 0) strips[0].BreakBefore = true;
        return strips;
    }

    private Strip DrawLine(OfficeParagraph paragraph, Line line, double width, bool lastLine)
    {
        double ascent = 0, descent = 0, gap = 0;
        bool measured = false;
        foreach (Placed placed in line.Items)
        {
            Atom atom = placed.Atom;
            if (atom.Kind == AtomKind.Picture)
            {
                ascent = Math.Max(ascent, atom.ImageHeight);
                measured = true;
            }
            else if (atom.Font is not null)
            {
                OfficeFontMetrics metrics = _fonts.Metrics(atom.Font);
                double size = atom.Style.Size;
                ascent = Math.Max(ascent, metrics.Ascent * size + Math.Max(0, atom.Style.Rise));
                descent = Math.Max(descent, metrics.Descent * size);
                gap = Math.Max(gap, metrics.Gap * size);
                measured = true;
            }
        }
        if (!measured)
        {
            OfficeFontMetrics metrics = _fonts.Metrics(_fonts.Primary(paragraph.MarkStyle));
            ascent = metrics.Ascent * paragraph.MarkStyle.Size;
            descent = metrics.Descent * paragraph.MarkStyle.Size;
            gap = metrics.Gap * paragraph.MarkStyle.Size;
        }
        double natural = ascent + descent + gap;
        double height = paragraph.LineRule switch
        {
            OfficeLineRule.Exact => Math.Max(1, paragraph.LineValue),
            OfficeLineRule.AtLeast => Math.Max(natural, paragraph.LineValue),
            _ => natural * (paragraph.LineValue > 0 ? paragraph.LineValue : 1)
        };
        double baseline = height - descent;

        // Alignment and justification move items right of the start.
        int lastContent = line.Items.FindLastIndex(item => item.Atom.Kind != AtomKind.Space);
        double contentEnd = lastContent >= 0 ? line.Items[lastContent].X + line.Items[lastContent].Atom.Width : line.Start;
        double right = width - paragraph.IndentRight;
        double free = right - contentEnd;
        if (free > 0.01 && !line.HasTab)
        {
            switch (paragraph.Alignment)
            {
                case OfficeAlignment.Center:
                    foreach (Placed placed in line.Items) placed.X += free / 2;
                    break;
                case OfficeAlignment.Right:
                    foreach (Placed placed in line.Items) placed.X += free;
                    break;
                case OfficeAlignment.Justify when !lastLine && !line.HardEnd:
                    int firstContent = line.Items.FindIndex(item => item.Atom.Kind != AtomKind.Space);
                    int gaps = 0;
                    for (int index = Math.Max(firstContent, 0); index < lastContent; index++)
                        if (line.Items[index].Atom.Kind == AtomKind.Space) gaps++;
                    if (gaps > 0)
                    {
                        double extra = free / gaps, shift = 0;
                        for (int index = Math.Max(firstContent, 0); index <= lastContent; index++)
                        {
                            line.Items[index].X += shift;
                            if (line.Items[index].Atom.Kind == AtomKind.Space && index < lastContent) shift += extra;
                        }
                    }
                    break;
            }
        }

        var background = new List<OfficeDrawOp>();
        var foreground = new List<OfficeDrawOp>();
        if (paragraph.Shading is OfficeColor shade)
            background.Add(new OfficeRectOp(paragraph.IndentLeft, 0, Math.Max(0, right - paragraph.IndentLeft), height, shade));

        foreach ((double from, double to, char leader, OfficeRunStyle style) in line.Leaders)
        {
            string dot = leader.ToString();
            TrueTypeFont font = _fonts.Primary(style);
            if (font.GetGlyphId(leader) == 0) continue;
            double dotWidth = OfficeFonts.Width(dot, font, style.DrawSize);
            if (dotWidth <= 0) continue;
            int count = (int)Math.Floor((to - from - dotWidth) / dotWidth);
            if (count > 0)
                foreground.Add(new OfficeTextOp(to - dotWidth * (count + 0.5), baseline, new string(leader, count), font,
                    style.DrawSize, style.Color));
        }

        // Neighboring text in one face, size and color becomes one text op.
        int i = 0;
        while (i < line.Items.Count)
        {
            Placed placed = line.Items[i];
            Atom atom = placed.Atom;
            if (atom.Kind == AtomKind.Picture && atom.Image is not null)
            {
                foreground.Add(new OfficeImageOp(atom.Image, placed.X, baseline - atom.ImageHeight, atom.Width, atom.ImageHeight));
                i++;
                continue;
            }
            if (atom.Font is null || atom.Kind is not (AtomKind.Text or AtomKind.Space))
            {
                i++;
                continue;
            }
            var text = new System.Text.StringBuilder(atom.Text);
            double runEnd = placed.X + atom.Width;
            int j = i + 1;
            while (j < line.Items.Count)
            {
                Placed other = line.Items[j];
                if (other.Atom.Kind is not (AtomKind.Text or AtomKind.Space) || !ReferenceEquals(other.Atom.Font, atom.Font)
                    || other.Atom.Style != atom.Style || Math.Abs(other.X - runEnd) > 0.01)
                    break;
                text.Append(other.Atom.Text);
                runEnd = other.X + other.Atom.Width;
                j++;
            }
            bool trailing = j > lastContent;
            string value = trailing ? text.ToString().TrimEnd(' ') : text.ToString();
            double runWidth = trailing ? OfficeFonts.Width(value, atom.Font, atom.Style.DrawSize) : runEnd - placed.X;
            OfficeRunStyle style = atom.Style;
            double y = baseline - style.Rise;
            if (style.Highlight is OfficeColor highlight && runWidth > 0)
                background.Add(new OfficeRectOp(placed.X, Math.Max(0, baseline - ascent), runWidth, ascent + descent, highlight));
            if (value.Length > 0)
                foreground.Add(new OfficeTextOp(placed.X, y, value, atom.Font, style.DrawSize, style.Color));
            double thickness = Math.Max(0.5, style.DrawSize * 0.06);
            if (style.Underline && runWidth > 0)
                foreground.Add(new OfficeLineOp(placed.X, y + style.DrawSize * 0.12, placed.X + runWidth, y + style.DrawSize * 0.12,
                    thickness, style.Color));
            if (style.Strike && runWidth > 0)
                foreground.Add(new OfficeLineOp(placed.X, y - style.DrawSize * 0.28, placed.X + runWidth, y - style.DrawSize * 0.28,
                    thickness, style.Color));
            i = j;
        }
        background.AddRange(foreground);
        return new Strip { Height = height, Ops = background, BreakBefore = line.BreakBefore };
    }
}
