using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Rendering;

namespace KillerPdf.Engine.Documents;

/// <summary>A typeface offered for scanned-page text, with an optional bold face.</summary>
public sealed record PdfScanTextFace(string Family, TrueTypeFont Regular, TrueTypeFont? Bold = null)
{
    /// <summary>
    /// Gets the extra stroke added around the letters, as a fraction of the x-height, so the
    /// text carries the same weight as heavily inked or photocopied print.
    /// </summary>
    public double Weight { get; init; }
}

/// <summary>One scanned page rewritten as a cleaned page image with text over it.</summary>
public sealed record PdfScanTextPage(
    int PageIndex, PdfScanTextPageAnalysis Analysis, PdfImage Background, PdfScanTextFace Face);

/// <summary>A rewritten document and the words it turned into text.</summary>
public sealed record PdfScanTextResult(byte[] Document, int ReplacedWords, int SearchableWords);

/// <summary>
/// Rewrites scanned pages so recognized words become real text in a matching typeface, drawn
/// over the page image with those words erased from it. Words that were not recognized
/// confidently stay in the image and are added as invisible text so they remain searchable.
/// </summary>
public static class PdfScanTextWriter
{
    /// <summary>
    /// The serif, sans serif and monospaced faces bundled with the engine, metric-compatible with
    /// Times, Arial and Courier and free to embed, for use when no installed face suits.
    /// </summary>
    public static IReadOnlyList<PdfScanTextFace> BundledFaces() =>
    [
        new("Liberation Serif", PdfStandardFontSubstitutes.Find("Times-Roman"),
            PdfStandardFontSubstitutes.Find("Times-Bold")),
        new("Liberation Sans", PdfStandardFontSubstitutes.Find("Helvetica"),
            PdfStandardFontSubstitutes.Find("Helvetica-Bold")),
        new("Liberation Mono", PdfStandardFontSubstitutes.Find("Courier"),
            PdfStandardFontSubstitutes.Find("Courier-Bold"))
    ];

    /// <summary>Whether a word can be set in the face, every character having a glyph.</summary>
    public static bool CanSet(PdfScanTextFace face, PdfScanTextWord word)
    {
        ArgumentNullException.ThrowIfNull(face);
        ArgumentNullException.ThrowIfNull(word);
        return Covers(FaceFor(face, word), word.Word.Text);
    }

    /// <summary>Replaces the content of each listed page with its background image and text.</summary>
    public static PdfScanTextResult Write(PdfDocument document, IReadOnlyList<PdfScanTextPage> pages,
        Func<string, TrueTypeFont?> searchableFont)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(searchableFont);
        IReadOnlyList<PdfPageInformation> information = PdfPageInformation.Read(document);
        var editor = new PdfIncrementalPageEditor(document);
        int replaced = 0, searchable = 0;
        foreach (PdfScanTextPage page in pages)
        {
            if (page.PageIndex < 0 || page.PageIndex >= information.Count)
                throw new ArgumentOutOfRangeException(nameof(pages), "A scanned page is outside the document.");
            PdfPageInformation geometry = information[page.PageIndex];
            bool quarter = geometry.Rotation is 90 or 270;
            double displayWidth = quarter ? geometry.Height : geometry.Width;
            double displayHeight = quarter ? geometry.Width : geometry.Height;
            double scaleX = displayWidth / page.Analysis.PixelWidth;
            double scaleY = displayHeight / page.Analysis.PixelHeight;

            var content = new PdfContentStreamBuilder().SaveState();
            ApplyDisplayTransform(content, geometry);
            content.DrawImage(page.Background, 0, displayHeight, displayWidth, -displayHeight);
            content.BeginText();
            PdfTextRenderingMode? mode = null;
            List<PdfScanTextWord> set = [.. page.Analysis.Words
                .Where(word => word.Replaceable && CanSet(page.Face, word))];
            Dictionary<int, double> lineScales = LineScales(page.Face, set);
            var setWords = set.ToHashSet();
            foreach (PdfScanTextWord word in page.Analysis.Words)
            {
                if (string.IsNullOrWhiteSpace(word.Word.Text)) continue;
                if (setWords.Contains(word))
                {
                    SetMode(content, ref mode, page.Face.Weight > 0
                        ? PdfTextRenderingMode.FillAndStroke : PdfTextRenderingMode.Fill);
                    SetWord(content, page.Face, word, scaleX, scaleY, lineScales[word.Line], space: true);
                    replaced++;
                }
                else if (searchableFont(word.Word.Text) is TrueTypeFont font)
                {
                    SetMode(content, ref mode, PdfTextRenderingMode.Invisible);
                    SetInvisibleWord(content, font, word.Word, scaleX, scaleY);
                    searchable++;
                }
            }
            content.EndText().RestoreState();

            editor.SetPageContentAndPruneResources(page.PageIndex, []);
            editor.AppendPageContent(page.PageIndex, new PdfContentBounds(
                geometry.Left, geometry.Bottom,
                geometry.Left + geometry.Width, geometry.Bottom + geometry.Height), content);
        }
        return new PdfScanTextResult(editor.Build(), replaced, searchable);
    }

    /// <summary>
    /// Picks the face whose letters best cover the ink of a sample of the page's words, by
    /// setting the sample in each face and comparing the result with the scan pixel for pixel.
    /// </summary>
    public static PdfScanTextFace ChooseFace(PdfScanTextPageAnalysis analysis,
        IReadOnlyList<PdfScanTextFace> faces, int sampleSize = 40,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(faces);
        if (faces.Count == 0) throw new ArgumentException("At least one face is required.", nameof(faces));
        if (faces.Count == 1) return faces[0];
        IReadOnlyList<(PdfScanTextFace Face, double Score)> ranked =
            RankFaces(analysis, faces, sampleSize, cancellationToken);
        return ranked.Count == 0 ? faces[0] : ranked[0].Face;
    }

    /// <summary>Scores each face against a sample of the page's words, best first.</summary>
    public static IReadOnlyList<(PdfScanTextFace Face, double Score)> RankFaces(
        PdfScanTextPageAnalysis analysis, IReadOnlyList<PdfScanTextFace> faces, int sampleSize = 40,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(faces);

        List<PdfScanTextWord> candidates = [.. analysis.Words.Where(word => word.Replaceable && !word.Bold
            && word.Word.Text.Count(char.IsLetter) >= 3)];
        if (candidates.Count == 0) return [];
        int step = Math.Max(1, candidates.Count / sampleSize);
        List<PdfScanTextWord> sample = [.. candidates.Where((_, index) => index % step == 0).Take(sampleSize)];

        var scores = new List<(PdfScanTextFace Face, double Score)>(faces.Count);
        foreach (PdfScanTextFace face in faces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<PdfScanTextWord> settable = [.. sample.Where(word => Covers(face.Regular, word.Word.Text))];
            if (settable.Count < sample.Count * 0.9) continue;
            scores.Add(Score(analysis, face, settable));
        }
        return [.. scores.OrderByDescending(item => item.Score)];
    }

    /// <summary>
    /// Sets each replaceable word in the face and compares it with the scanned ink, keeping in
    /// the page image any word whose letters do not match it closely, such as a misread word.
    /// </summary>
    public static int Verify(PdfScanTextPageAnalysis analysis, PdfScanTextFace face,
        double minimumMatch = 0.75, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(face);
        List<PdfScanTextWord> words = [.. analysis.Words.Where(word => word.Replaceable && CanSet(face, word))];
        if (words.Count == 0) return 0;
        cancellationToken.ThrowIfCancellationRequested();
        double[] matches = MatchWords(analysis, face, words, out _, out _);
        double typical = matches.Order().ElementAt(matches.Length / 2);

        // Each reading is also set with single swaps of letters a recognizer often confuses.
        // When a swap fits the ink better than what was recognized, the recognized text is
        // probably wrong, and the word stays as it was scanned.
        var probes = new List<PdfScanTextWord>(words.Count * 2);
        var owners = new List<int>(words.Count * 2);
        for (int i = 0; i < words.Count; i++)
        {
            probes.Add(words[i]);
            owners.Add(i);
            foreach (string variant in Variants(words[i].Word.Text).Take(6))
            {
                PdfScanTextWord probe = words[i].WithText(variant);
                if (!CanSet(face, probe)) continue;
                probes.Add(probe);
                owners.Add(i);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        double[] shapes = MatchWords(analysis, face, probes, out _, out _, strict: true);
        double[] own = new double[words.Count], rival = new double[words.Count];
        for (int k = 0; k < probes.Count; k++)
        {
            if (ReferenceEquals(probes[k], words[owners[k]])) own[owners[k]] = shapes[k];
            else rival[owners[k]] = Math.Max(rival[owners[k]], shapes[k]);
        }

        int rejected = 0;
        for (int i = 0; i < words.Count; i++)
        {
            words[i].Match = matches[i];
            if (matches[i] >= minimumMatch && matches[i] >= typical - 0.15
                && rival[i] <= own[i] + 0.02) continue;
            words[i].Replaceable = false;
            rejected++;
        }
        return rejected;
    }

    // Letters and pairs a recognizer commonly takes for one another, most telling first.
    private static readonly (string A, string B)[] Confusions =
    [
        ("E", "F"), ("I", "l"), ("I", "1"), ("l", "1"), ("I", "["), ("l", "T"), ("I", "T"),
        ("O", "0"), ("m", "rn"), ("d", "cl"), ("h", "li"), ("w", "vv"), ("S", "5"), ("B", "8"),
        ("c", "e"), ("n", "h"), ("i", "l"), ("f", "t"), ("C", "G"), ("p", "v"), ("u", "n")
    ];

    private static IEnumerable<string> Variants(string text)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { text };
        foreach ((string a, string b) in Confusions)
            foreach ((string from, string to) in new[] { (a, b), (b, a) })
                for (int at = text.IndexOf(from, StringComparison.Ordinal); at >= 0;
                     at = text.IndexOf(from, at + 1, StringComparison.Ordinal))
                {
                    string variant = string.Concat(text.AsSpan(0, at), to, text.AsSpan(at + from.Length));
                    if (seen.Add(variant)) yield return variant;
                }
    }

    private static (PdfScanTextFace Face, double Score) Score(PdfScanTextPageAnalysis analysis,
        PdfScanTextFace face, List<PdfScanTextWord> words)
    {
        PdfScanTextFace regular = face with { Bold = null, Weight = 0 };
        double[] matches = MatchWords(analysis, regular, words,
            out Dictionary<int, double> lineScales, out double[] strokes, strict: true);
        // Heavy print is matched by thickening the face to the scan's stroke weight before the
        // shapes are compared, so a bold-looking scan does not simply favor a heavy face.
        double extra = Median(words.Select((word, i) => word.StrokeWidth - strokes[i]));
        double xHeight = Median(words.Select(word => word.XHeight));
        if (extra > 0.5 && xHeight > 0)
        {
            regular = regular with { Weight = Math.Min(0.15, extra / xHeight) };
            matches = MatchWords(analysis, regular, words, out lineScales, out _, strict: true);
        }
        // A face that has to be squeezed or stretched far to fit the scan is the wrong design
        // even when its letters happen to overlap the ink.
        double distortion = lineScales.Values.Average(scale => Math.Abs(Math.Log(scale / 100)));
        return (face with { Weight = regular.Weight }, matches.Average() - 0.5 * distortion);
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = [.. values.Order()];
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }

    // Renders the words as they would be written and measures, per word, the overlap of the
    // rendered letters with the scanned ink as intersection over union.
    private static double[] MatchWords(PdfScanTextPageAnalysis analysis, PdfScanTextFace face,
        List<PdfScanTextWord> words, out Dictionary<int, double> lineScales, out double[] strokes,
        bool strict = false)
    {
        // The words are packed side by side onto a small canvas, each with a margin, instead of
        // rendering the page area they span.
        int width = Math.Max(words.Max(word => word.MaskWidth), Math.Min(analysis.PixelWidth, 2048));
        var places = new (int X, int Y)[words.Count];
        int cursorX = 0, cursorY = 0, shelf = 0;
        for (int i = 0; i < words.Count; i++)
        {
            PdfScanTextWord word = words[i];
            int gap = Math.Max(4, word.MaskHeight / 2);
            if (cursorX > 0 && cursorX + word.MaskWidth > width)
            {
                cursorX = 0;
                cursorY += shelf;
                shelf = 0;
            }
            places[i] = (cursorX, cursorY);
            cursorX += word.MaskWidth + gap;
            shelf = Math.Max(shelf, word.MaskHeight + gap);
        }
        int height = Math.Max(1, cursorY + shelf);
        lineScales = LineScales(face, words);
        var content = new PdfContentStreamBuilder().Transform(1, 0, 0, -1, 0, height).BeginText();
        if (face.Weight > 0) content.SetTextRenderingMode(PdfTextRenderingMode.FillAndStroke);
        for (int i = 0; i < words.Count; i++)
        {
            PdfScanTextWord word = words[i];
            SetWord(content, face, word, 1, 1, lineScales[word.Line], space: false,
                offsetX: word.MaskLeft - places[i].X, offsetY: word.MaskTop - places[i].Y,
                inkColor: false);
        }
        content.EndText();
        byte[] pdf = new PdfDocumentBuilder().AddPage(width, height, content).Build();
        PdfRenderedPage rendered = new PdfPageRenderer(PdfDocument.Open(pdf))
            .Render(0, new PdfRenderOptions(width, height) { CacheResult = false });
        ReadOnlySpan<byte> pixels = rendered.Pixels.Span;

        // Overlap is judged with a tolerance of about a stroke width, so a slightly lighter or
        // heavier face still matches while a wrong or misplaced letter does not.
        double[] matches = new double[words.Count];
        strokes = new double[words.Count];
        for (int i = 0; i < words.Count; i++)
        {
            PdfScanTextWord word = words[i];
            int w = word.MaskWidth, h = word.MaskHeight;
            bool[] set = new bool[w * h];
            for (int y = 0; y < h; y++)
            {
                int py = places[i].Y + y;
                if ((uint)py >= (uint)height) continue;
                for (int x = 0; x < w; x++)
                {
                    int px = places[i].X + x;
                    if ((uint)px >= (uint)width) continue;
                    set[y * w + x] = pixels[(py * width + px) * 4 + 1] < 128;
                }
            }
            // Choosing a face needs its letter shapes, so that comparison allows only a pixel.
            int tolerance = strict ? 1 : Math.Max(1, (int)Math.Round(word.XHeight / 10));
            bool[] nearScan = PdfScanTextAnalyzer.Grow(word.Mask, w, h, tolerance);
            bool[] nearSet = PdfScanTextAnalyzer.Grow(set, w, h, tolerance);
            int setCount = 0, setHits = 0, scanCount = 0, scanHits = 0;
            for (int k = 0; k < set.Length; k++)
            {
                if (set[k]) { setCount++; if (nearScan[k]) setHits++; }
                if (word.Mask[k]) { scanCount++; if (nearSet[k]) scanHits++; }
            }
            double precision = Ratio(setHits, setCount);
            double recall = Ratio(scanHits, scanCount);
            if (strict)
                matches[i] = precision + recall > 0 ? 2 * precision * recall / (precision + recall) : 0;
            else
                // The weaker side decides: a letter missing from the setting, or one the scan
                // does not have, is a misread even when the rest of the word lines up. Letter by
                // letter, scanned ink with no set letter anywhere near it marks a wrong letter
                // that the whole-word count would average away.
                matches[i] = Math.Min(Math.Min(precision, recall), UnexplainedInk(word, set, w, h));
            strokes[i] = StrokeWidth(set, w, h,
                (int)Math.Round(word.Baseline - word.XHeight) - word.MaskTop,
                (int)Math.Round(word.Baseline) - 1 - word.MaskTop);
        }
        return matches;
    }

    private static double Ratio(int part, int whole) => whole == 0 ? 0 : part / (double)whole;

    // The lowest share, over letter-wide columns, of the scanned ink that lies within a quarter
    // of the x-height of the set letters. Different typefaces stay well inside that distance;
    // a letter that is not there at all does not.
    private static double UnexplainedInk(PdfScanTextWord word, bool[] set, int w, int h)
    {
        int reach = Math.Max(2, (int)Math.Round(word.XHeight / 4));
        bool[] near = PdfScanTextAnalyzer.Grow(set, w, h, reach);
        int columnWidth = Math.Max(2, (int)Math.Round(word.XHeight * 1.5));
        int columns = (w + columnWidth - 1) / columnWidth;
        int[] ink = new int[columns], explained = new int[columns];
        for (int k = 0; k < set.Length; k++)
        {
            if (!word.Mask[k]) continue;
            int column = k % w / columnWidth;
            ink[column]++;
            if (near[k]) explained[column]++;
        }
        int busy = Math.Max(1, ink.Sum() / Math.Max(1, columns) / 3);
        double lowest = 1;
        for (int column = 0; column < columns; column++)
            if (ink[column] >= busy) lowest = Math.Min(lowest, Ratio(explained[column], ink[column]));
        return lowest;
    }

    // Mean horizontal run of ink across the x-height band.
    private static double StrokeWidth(bool[] mask, int w, int h, int bandTop, int bandBottom)
    {
        long runs = 0, length = 0;
        for (int y = Math.Max(0, bandTop); y <= Math.Min(h - 1, bandBottom); y++)
        {
            int run = 0;
            for (int x = 0; x <= w; x++)
            {
                if (x < w && mask[y * w + x]) { run++; continue; }
                if (run > 0) { runs++; length += run; run = 0; }
            }
        }
        return runs == 0 ? 0 : length / (double)runs;
    }

    // One horizontal scale per line, so every word on it is set at the same width.
    private static Dictionary<int, double> LineScales(PdfScanTextFace face, IEnumerable<PdfScanTextWord> words)
    {
        var scales = new Dictionary<int, double>();
        foreach (IGrouping<int, PdfScanTextWord> line in words.GroupBy(word => word.Line))
        {
            double target = 0, natural = 0;
            foreach (PdfScanTextWord word in line)
            {
                TrueTypeFont font = FaceFor(face, word);
                double size = PixelSize(font, word);
                target += word.Right - word.Left;
                natural += Advance(font, word.FitText ?? word.Word.Text) * size / 1000;
            }
            scales[line.Key] = natural > 0 ? Math.Clamp(target / natural * 100, 50, 200) : 100;
        }
        return scales;
    }

    private static void SetWord(PdfContentStreamBuilder content, PdfScanTextFace face,
        PdfScanTextWord word, double scaleX, double scaleY, double lineScale, bool space,
        double offsetX = 0, double offsetY = 0, bool inkColor = true)
    {
        TrueTypeFont font = FaceFor(face, word);
        double size = PixelSize(font, word) * scaleY;
        double advance = Advance(font, word.FitText ?? word.Word.Text) * size / 1000;
        double target = (word.Right - word.Left) * scaleX;
        double own = advance > 0 ? Math.Clamp(target / advance * 100, 50, 200) : 100;
        // The line's scale keeps the words uniform; staying near the word's own fit keeps a
        // word from running into its neighbor when the line mixes wide and narrow words.
        double scale = Math.Clamp(lineScale, own * 0.9, own * 1.1);
        (byte red, byte green, byte blue) = inkColor ? word.InkColor : ((byte)0, (byte)0, (byte)0);
        // A slightly skewed scan keeps its lines' slant; the text runs along each line.
        double angle = Math.Atan(word.Slope * scaleY / scaleX);
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        content.SetFillRgb(red / 255.0, green / 255.0, blue / 255.0);
        if (face.Weight > 0)
            content.SetStrokeRgb(red / 255.0, green / 255.0, blue / 255.0)
                .SetLineWidth(face.Weight * word.XHeight * scaleY);
        content.SetFont(font, size)
            .SetHorizontalTextScale(scale)
            .SetTextMatrix(cos, sin, sin, -cos, (word.Left - offsetX) * scaleX, (word.Baseline - offsetY) * scaleY)
            .ShowUnicodeText(space ? word.Word.Text + " " : word.Word.Text);
    }

    private static void SetInvisibleWord(PdfContentStreamBuilder content, TrueTypeFont font,
        PdfOcrPixelWord word, double scaleX, double scaleY)
    {
        double height = (word.Bottom - (double)word.Top) * scaleY;
        double width = (word.Right - (double)word.Left) * scaleX;
        double natural = Advance(font, word.Text) * height / 1000;
        double scale = natural > 0 ? Math.Clamp(width / natural * 100, 10, 1000) : 100;
        content.SetFont(font, height).SetHorizontalTextScale(scale)
            .SetTextMatrix(1, 0, 0, -1, word.Left * scaleX, word.Top * scaleY + height * 0.8)
            .ShowUnicodeText(word.Text + " ");
    }

    private static void SetMode(PdfContentStreamBuilder content, ref PdfTextRenderingMode? current,
        PdfTextRenderingMode mode)
    {
        if (current == mode) return;
        content.SetTextRenderingMode(mode);
        current = mode;
    }

    private static TrueTypeFont FaceFor(PdfScanTextFace face, PdfScanTextWord word) =>
        word.Bold && face.Bold is not null && Covers(face.Bold, word.Word.Text) ? face.Bold : face.Regular;

    private static double Advance(TrueTypeFont font, string text) =>
        text.EnumerateRunes().Sum(rune => (double)font.GetPdfAdvanceWidth(font.GetGlyphId(rune.Value)));

    private static bool Covers(TrueTypeFont font, string text) =>
        text.EnumerateRunes().All(rune => Rune.IsWhiteSpace(rune) || font.GetGlyphId(rune.Value) != 0);

    // The font size, in pixels, that gives the face the word's measured letter height.
    private static double PixelSize(TrueTypeFont font, PdfScanTextWord word) =>
        word.CapitalsOnly ? word.CapHeight / CapHeightRatio(font) : word.XHeight / XHeightRatio(font);

    /// <summary>The face's x-height as a fraction of its em, from its OS/2 table when present.</summary>
    internal static double XHeightRatio(TrueTypeFont font) => Os2Ratio(font, 86, 0.5, 0.3, 0.7);

    /// <summary>The face's capital height as a fraction of its em, from its OS/2 table when present.</summary>
    internal static double CapHeightRatio(TrueTypeFont font) => Os2Ratio(font, 88, 0.7, 0.5, 0.9);

    private static double Os2Ratio(TrueTypeFont font, int field, double fallback, double low, double high)
    {
        ReadOnlySpan<byte> data = font.FontData.Span;
        if (data.Length >= 12)
        {
            int tables = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
            for (int i = 0; i < tables && 12 + i * 16 + 16 <= data.Length; i++)
            {
                ReadOnlySpan<byte> record = data.Slice(12 + i * 16, 16);
                if (!record[..4].SequenceEqual("OS/2"u8)) continue;
                int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
                if (offset < 0 || offset + 90 > data.Length) break;
                ReadOnlySpan<byte> os2 = data[offset..];
                int version = BinaryPrimitives.ReadUInt16BigEndian(os2);
                short value = BinaryPrimitives.ReadInt16BigEndian(os2[field..]);
                if (version >= 2 && value > 0 && font.UnitsPerEm > 0)
                    return Math.Clamp(value / (double)font.UnitsPerEm, low, high);
                break;
            }
        }
        return fallback;
    }

    private static void ApplyDisplayTransform(PdfContentStreamBuilder content, PdfPageInformation page)
    {
        switch (page.Rotation)
        {
            case 0: content.Transform(1, 0, 0, -1, 0, page.Height); break;
            case 90: content.Transform(0, 1, 1, 0, 0, 0); break;
            case 180: content.Transform(-1, 0, 0, 1, page.Width, 0); break;
            case 270: content.Transform(0, -1, -1, 0, page.Width, page.Height); break;
            default: throw new InvalidOperationException("The PDF page rotation is unsupported.");
        }
    }
}
