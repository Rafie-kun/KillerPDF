using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Documents;

/// <summary>Explains why a selected region cannot be reconstructed as a ruled table.</summary>
public enum PdfTableExtractionFailure
{
    /// <summary>A complete table was extracted.</summary>
    None,
    /// <summary>The region does not contain a rectangular ruled table.</summary>
    NoRuledTable,
    /// <summary>Grid lines are incomplete or contain merged cells.</summary>
    IncompleteGrid,
    /// <summary>The region contains text that does not run horizontally from left to right.</summary>
    UnsupportedTextDirection,
    /// <summary>The region contains image data without extractable text.</summary>
    ScannedRegion,
    /// <summary>The region exceeds the bounded table extraction limits.</summary>
    LimitExceeded,
    /// <summary>Extraction or painting state cannot establish reliable, visible cell contents.</summary>
    UncertainContent
}

/// <summary>The table or an explicit unsupported-region result.</summary>
public sealed record PdfTableExtractionResult(PdfExtractedTable? Table, PdfTableExtractionFailure Failure)
{
    /// <summary>Gets whether extraction produced a complete table.</summary>
    public bool Succeeded => Table is not null;
    /// <summary>Gets source extraction warnings that should be reviewed with the preview.</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}

/// <summary>One cell, including its original text and unrotated crop-relative bounds.</summary>
public sealed record PdfExtractedTableCell(int RowIndex, int ColumnIndex,
    PdfContentBounds Bounds, string OriginalText, string Text)
{
    /// <summary>Gets the original glyphs and their geometry, before preview corrections.</summary>
    public IReadOnlyList<PdfExtractedLetter> Letters { get; init; } = [];
}

/// <summary>An immutable, complete rectangular table in top-to-bottom row order.</summary>
public sealed class PdfExtractedTable
{
    internal PdfExtractedTable(int pageIndex, PdfContentBounds bounds, int rowCount,
        int columnCount, IEnumerable<PdfExtractedTableCell> cells)
    {
        PageIndex = pageIndex;
        Bounds = bounds;
        RowCount = rowCount;
        ColumnCount = columnCount;
        Cells = Array.AsReadOnly(cells.ToArray());
    }

    /// <summary>Gets the zero-based source page.</summary>
    public int PageIndex { get; }
    /// <summary>Gets the table's unrotated crop-relative bounds in PDF points.</summary>
    public PdfContentBounds Bounds { get; }
    /// <summary>Gets the number of rows.</summary>
    public int RowCount { get; }
    /// <summary>Gets the number of columns.</summary>
    public int ColumnCount { get; }
    /// <summary>Gets all cells in row-major order, including empty cells.</summary>
    public IReadOnlyList<PdfExtractedTableCell> Cells { get; }

    /// <summary>Returns a cell using zero-based row and column indices.</summary>
    public PdfExtractedTableCell GetCell(int rowIndex, int columnIndex)
    {
        if (rowIndex < 0 || rowIndex >= RowCount) throw new ArgumentOutOfRangeException(nameof(rowIndex));
        if (columnIndex < 0 || columnIndex >= ColumnCount) throw new ArgumentOutOfRangeException(nameof(columnIndex));
        return Cells[rowIndex * ColumnCount + columnIndex];
    }

    /// <summary>Returns a corrected copy while retaining the original text and glyph geometry.</summary>
    public PdfExtractedTable WithCellText(int rowIndex, int columnIndex, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        PdfExtractedTableCell cell = GetCell(rowIndex, columnIndex);
        PdfExtractedTableCell[] corrected = Cells.ToArray();
        corrected[rowIndex * ColumnCount + columnIndex] = cell with { Text = text };
        return new(PageIndex, Bounds, RowCount, ColumnCount, corrected);
    }
}

/// <summary>Reconstructs a complete axis-aligned ruled table from a single selected page region.</summary>
public static class PdfTableExtractor
{
    private const double Tolerance = 0.75;

    /// <summary>Extracts cells from unrotated, crop-relative PDF points without changing the source.</summary>
    public static PdfTableExtractionResult Extract(PdfDocument document, int pageIndex,
        PdfContentBounds region, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        if (!double.IsFinite(region.Left) || !double.IsFinite(region.Bottom)
            || !double.IsFinite(region.Right) || !double.IsFinite(region.Top)
            || region.Width <= 0 || region.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(region));
        PdfPageContent page = new PdfPageContentReader(document).Read(pageIndex, cancellationToken);
        if (page.Letters.Count > 100_000 || page.Paths.Count > 100_000)
            return Result(null, PdfTableExtractionFailure.LimitExceeded);
        PdfExtractedLetter[] selectedLetters = page.Letters.Where(letter =>
            Contains(region, (letter.BoundingBox.Left + letter.BoundingBox.Right) / 2,
                (letter.BoundingBox.Bottom + letter.BoundingBox.Top) / 2)).ToArray();
        if (!selectedLetters.Any(letter => !string.IsNullOrWhiteSpace(letter.Value))
            && page.Images.Any(image => Overlaps(region, image.BoundingBox)))
            return Result(null, PdfTableExtractionFailure.ScannedRegion);
        if (page.Diagnostics.Count > 0 || HasUncertainPaint(page, cancellationToken)
            || page.Images.Any(image => Overlaps(region, image.BoundingBox))
            || page.Shadings.Any(shading => Overlaps(region, shading.BoundingBox)))
            return Result(null, PdfTableExtractionFailure.UncertainContent);
        if (selectedLetters.Any(letter => !string.IsNullOrWhiteSpace(letter.Value)
            && (letter.EndBaseLine.X < letter.StartBaseLine.X - 0.01
                || Math.Abs(letter.EndBaseLine.Y - letter.StartBaseLine.Y) > 0.01)))
            return Result(null, PdfTableExtractionFailure.UnsupportedTextDirection);
        if (selectedLetters.Sum(letter => (long)letter.Value.Length) > 2_000_000)
            return Result(null, PdfTableExtractionFailure.LimitExceeded);
        var horizontal = new List<Rule>();
        var vertical = new List<Rule>();
        int segmentCount = 0;
        foreach (PdfExtractedPath path in page.Paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            segmentCount += path.Segments.Count;
            if (segmentCount > 100_000) return Result(null, PdfTableExtractionFailure.LimitExceeded);
            if (path.PaintOperator is "f" or "F" or "f*" or "B" or "B*" or "b" or "b*"
                && Overlaps(region, path.BoundingBox))
            {
                if (!AddFilledRules(path)) return Result(null, PdfTableExtractionFailure.UncertainContent);
                continue;
            }
            if (path.PaintOperator is not ("S" or "s" or "B" or "B*" or "b" or "b*")) continue;
            PdfPoint? current = null, start = null;
            foreach (PdfExtractedPathSegment segment in path.Segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (segment.Operator == "re" && segment.Points.Count == 4)
                {
                    for (int index = 0; index < 4; index++)
                        Add(segment.Points[index], segment.Points[(index + 1) % 4]);
                    current = start = segment.Points[0];
                }
                else if (segment.Operator == "m" && segment.Points.Count == 1)
                    current = start = segment.Points[0];
                else if (segment.Operator == "l" && segment.Points.Count == 1)
                {
                    if (current is PdfPoint from) Add(from, segment.Points[0]);
                    current = segment.Points[0];
                }
                else if (segment.Operator == "h")
                {
                    if (current is PdfPoint from && start is PdfPoint to) Add(from, to);
                    current = start;
                }
                else current = null;
            }
            if (path.PaintOperator is "s" or "b" or "b*"
                && current is PdfPoint last && start is PdfPoint first) Add(last, first);
        }

        double[] xs = Coordinates(vertical, cancellationToken);
        double[] ys = Coordinates(horizontal, cancellationToken);
        if (xs.Length < 2 || ys.Length < 2)
            return Result(null, PdfTableExtractionFailure.NoRuledTable);
        if (xs.Length > 51 || ys.Length > 101)
            return Result(null, PdfTableExtractionFailure.LimitExceeded);
        if (xs.Any(x => !Covers(vertical, x, ys[0], ys[^1], cancellationToken))
            || ys.Any(y => !Covers(horizontal, y, xs[0], xs[^1], cancellationToken)))
            return Result(null, PdfTableExtractionFailure.IncompleteGrid);
        var buckets = new List<PdfExtractedLetter>[(xs.Length - 1) * (ys.Length - 1)];
        foreach (PdfExtractedLetter letter in selectedLetters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int column = Interval(xs, (letter.BoundingBox.Left + letter.BoundingBox.Right) / 2);
            int bottomRow = Interval(ys, (letter.BoundingBox.Bottom + letter.BoundingBox.Top) / 2);
            if (column < 0 || bottomRow < 0) continue;
            int index = (ys.Length - bottomRow - 2) * (xs.Length - 1) + column;
            (buckets[index] ??= []).Add(letter);
        }
        var cells = new List<PdfExtractedTableCell>();
        for (int row = 0; row < ys.Length - 1; row++)
            for (int column = 0; column < xs.Length - 1; column++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bounds = new PdfContentBounds(xs[column], ys[ys.Length - row - 2],
                    xs[column + 1], ys[ys.Length - row - 1]);
                PdfExtractedLetter[] letters = buckets[row * (xs.Length - 1) + column]?.ToArray() ?? [];
                if (letters.Any(letter => letter.BoundingBox.Left < bounds.Left - Tolerance
                    || letter.BoundingBox.Right > bounds.Right + Tolerance
                    || letter.BoundingBox.Bottom < bounds.Bottom - Tolerance
                    || letter.BoundingBox.Top > bounds.Top + Tolerance))
                    return Result(null, PdfTableExtractionFailure.UncertainContent);
                string text = ReadText(letters, cancellationToken);
                cells.Add(new(row, column, bounds, text, text) { Letters = Array.AsReadOnly(letters) });
            }
        return Result(new(pageIndex, new(xs[0], ys[0], xs[^1], ys[^1]),
            ys.Length - 1, xs.Length - 1, cells), PdfTableExtractionFailure.None);

        PdfTableExtractionResult Result(PdfExtractedTable? table, PdfTableExtractionFailure failure) =>
            new(table, failure) { Diagnostics = page.Diagnostics };

        bool AddFilledRules(PdfExtractedPath path)
        {
            foreach (PdfExtractedPathSegment segment in path.Segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (segment.Operator != "re" || segment.Points.Count != 4) return false;
                for (int index = 0; index < 4; index++)
                {
                    PdfPoint from = segment.Points[index], to = segment.Points[(index + 1) % 4];
                    if (Math.Abs(from.X - to.X) > 0.01 && Math.Abs(from.Y - to.Y) > 0.01) return false;
                }
                var bounds = new PdfContentBounds(segment.Points.Min(point => point.X),
                    segment.Points.Min(point => point.Y), segment.Points.Max(point => point.X),
                    segment.Points.Max(point => point.Y));
                if (!Overlaps(region, bounds)) continue;
                if (bounds.Height > 0 && bounds.Height <= 2 && bounds.Width > bounds.Height * 4)
                    Add(new(bounds.Left, (bounds.Bottom + bounds.Top) / 2),
                        new(bounds.Right, (bounds.Bottom + bounds.Top) / 2));
                else if (bounds.Width > 0 && bounds.Width <= 2 && bounds.Height > bounds.Width * 4)
                    Add(new((bounds.Left + bounds.Right) / 2, bounds.Bottom),
                        new((bounds.Left + bounds.Right) / 2, bounds.Top));
                else return false;
            }
            return true;
        }

        void Add(PdfPoint from, PdfPoint to)
        {
            if (!Contains(region, from.X, from.Y) || !Contains(region, to.X, to.Y)) return;
            if (Math.Abs(from.Y - to.Y) <= Tolerance && Math.Abs(from.X - to.X) > Tolerance)
                horizontal.Add(new((from.Y + to.Y) / 2, Math.Min(from.X, to.X), Math.Max(from.X, to.X)));
            else if (Math.Abs(from.X - to.X) <= Tolerance && Math.Abs(from.Y - to.Y) > Tolerance)
                vertical.Add(new((from.X + to.X) / 2, Math.Min(from.Y, to.Y), Math.Max(from.Y, to.Y)));
        }
    }

    private static bool Contains(PdfContentBounds bounds, double x, double y) =>
        x >= bounds.Left && x <= bounds.Right && y >= bounds.Bottom && y <= bounds.Top;

    private static bool HasUncertainPaint(PdfPageContent page, CancellationToken cancellationToken)
    {
        if (page.MarkedContent.Any(mark => mark.IsOptionalContent)) return true;
        for (int index = 0; index < page.Instructions.Count; index++)
        {
            var instruction = page.Instructions[index];
            cancellationToken.ThrowIfCancellationRequested();
            // An isolated image placement cannot alter the following table's graphics state.
            // Image overlap with the selected region is checked separately.
            if (instruction.Operator == "q" && index + 3 < page.Instructions.Count
                && page.Instructions[index + 1].Operator == "cm"
                && page.Instructions[index + 2].Operator == "Do"
                && page.Instructions[index + 3].Operator == "Q"
                && page.Instructions[index + 2].Operands is [PdfName name]
                && page.Images.Any(image => !image.IsInline && image.ResourceName == name.ValueAsLatin1()))
            {
                index += 3;
                continue;
            }
            // The extracted paths do not carry visibility, clipping, or resource state.
            // Reject these states for the whole page rather than infer visible grid lines.
            if (instruction.Operator is "gs" or "W" or "W*" or "Do" or "CS" or "cs"
                or "SC" or "sc" or "SCN" or "scn") return true;
            // Glyph placements omit text spacing and text-show boundaries, so gaps
            // from explicit spacing cannot be distinguished from word separators.
            if (instruction.Operator is "Tc" or "Tw" && instruction.Operands.Count == 1
                && Number(instruction.Operands[0]) != 0) return true;
            if (instruction.Operator == "\"" && instruction.Operands.Count >= 2
                && (Number(instruction.Operands[0]) != 0 || Number(instruction.Operands[1]) != 0)) return true;
            // Path geometry omits the painted stroke footprint and paint order.
            // Restrict widths and transforms so broad strokes cannot masquerade as thin rules.
            if (instruction.Operator == "w" && instruction.Operands.Count == 1)
            {
                double width = Number(instruction.Operands[0]);
                if (!double.IsFinite(width) || width < 0 || width > 2) return true;
            }
            if (instruction.Operator == "cm" && instruction.Operands.Count == 6)
            {
                double[] matrix = instruction.Operands.Take(4).Select(Number).ToArray();
                if (matrix.Any(value => !double.IsFinite(value))
                    || Math.Abs(matrix[0]) + Math.Abs(matrix[2]) > 1
                    || Math.Abs(matrix[1]) + Math.Abs(matrix[3]) > 1) return true;
            }
            if (instruction.Operator == "Tr" && instruction.Operands.Count == 1
                && Number(instruction.Operands[0]) >= 3) return true;
            if (instruction.Operator is "G" or "g" or "RG" or "rg"
                && instruction.Operands.Count > 0 && instruction.Operands.All(value => Number(value) >= 0.99)) return true;
            if (instruction.Operator is "K" or "k" && instruction.Operands.Count == 4
                && instruction.Operands.All(value => Number(value) <= 0.01)) return true;
        }
        return false;

        static double Number(PdfObject value) => value switch
        {
            PdfInteger integer => integer.Value,
            PdfReal real => real.Value,
            _ => double.NaN
        };
    }

    private static bool Overlaps(PdfContentBounds first, PdfContentBounds second) =>
        first.Left < second.Right && first.Right > second.Left
        && first.Bottom < second.Top && first.Top > second.Bottom;

    private static int Interval(double[] coordinates, double value)
    {
        if (value < coordinates[0] || value > coordinates[^1]) return -1;
        int index = Array.BinarySearch(coordinates, value);
        return index >= 0 ? Math.Min(index, coordinates.Length - 2) : ~index - 1;
    }

    private static double[] Coordinates(List<Rule> rules, CancellationToken cancellationToken)
    {
        var coordinates = new List<double>();
        foreach (double value in rules.Select(rule => rule.Coordinate).Order())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (coordinates.Count == 0 || value - coordinates[^1] > Tolerance) coordinates.Add(value);
        }
        return coordinates.ToArray();
    }

    private static bool Covers(List<Rule> rules, double coordinate, double start, double end,
        CancellationToken cancellationToken)
    {
        double reached = start;
        foreach (Rule rule in rules.Where(rule => Math.Abs(rule.Coordinate - coordinate) <= Tolerance)
            .OrderBy(rule => rule.Start))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rule.End < reached) continue;
            if (rule.Start > reached + Tolerance) return false;
            reached = Math.Max(reached, rule.End);
        }
        return reached >= end - Tolerance;
    }

    private static string ReadText(PdfExtractedLetter[] letters, CancellationToken cancellationToken)
    {
        var lines = new List<List<PdfExtractedLetter>>();
        foreach (PdfExtractedLetter letter in letters.OrderByDescending(letter => letter.StartBaseLine.Y))
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<PdfExtractedLetter>? line = lines.LastOrDefault();
            if (line is null || Math.Abs(line[0].StartBaseLine.Y - letter.StartBaseLine.Y)
                > Math.Max(1, letter.PointSize * 0.3)) lines.Add(line = []);
            line.Add(letter);
        }
        return string.Join('\n', lines.Select(line =>
        {
            var text = new StringBuilder();
            PdfExtractedLetter? previous = null;
            foreach (PdfExtractedLetter letter in line.OrderBy(letter => letter.StartBaseLine.X))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (previous is not null && !string.IsNullOrWhiteSpace(previous.Value)
                    && !string.IsNullOrWhiteSpace(letter.Value)
                    && letter.StartBaseLine.X - previous.EndBaseLine.X > Math.Max(1, letter.PointSize * 0.22))
                    text.Append(' ');
                text.Append(letter.Value);
                previous = letter;
            }
            return text.ToString();
        }));
    }

    private readonly record struct Rule(double Coordinate, double Start, double End);
}
