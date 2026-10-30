using System.IO.Compression;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Writing;
using Matrix = KillerPdf.Engine.Parsing.PdfTextContentReader.Matrix;

namespace KillerPdf.Engine.Documents;

/// <summary>Why a line of page text cannot be rewritten in the page content.</summary>
public enum PdfTextLineBlocker
{
    /// <summary>The line can be rewritten.</summary>
    None,
    /// <summary>No readable text lies in the area.</summary>
    NoText,
    /// <summary>The text is drawn invisibly, as on a scan with a recognized text layer.</summary>
    InvisibleText,
    /// <summary>The text has a zero font size.</summary>
    ZeroSizeText,
    /// <summary>The text uses a font that cannot be decoded.</summary>
    UnreadableText
}

/// <summary>What a line edit would replace.</summary>
/// <param name="Glyphs">The glyphs whose centers lie in the area.</param>
/// <param name="Blocker">Why the line cannot be rewritten, or <see cref="PdfTextLineBlocker.None"/>.</param>
/// <param name="FontName">The PostScript name of the font the line starts in.</param>
/// <param name="FontSize">The size the line starts at on the page, in points.</param>
public sealed record PdfTextLineInspection(int Glyphs, PdfTextLineBlocker Blocker, string FontName, double FontSize)
{
    /// <summary>True when the line can be rewritten in the page content.</summary>
    public bool CanEdit => Blocker == PdfTextLineBlocker.None;
}

/// <summary>A replacement for the text drawn in one area of a page.</summary>
/// <param name="PageIndex">The zero-based page.</param>
/// <param name="Region">The area in unrotated points relative to the page's crop box corner. A
/// glyph is replaced when its center lies inside.</param>
/// <param name="Text">The new text; line breaks start new lines below the first.</param>
public sealed record PdfTextLineEdit(int PageIndex, PdfContentBounds Region, string Text)
{
    /// <summary>The embeddable font used when the original font cannot show the new text, or when
    /// <see cref="UseSubstitute"/> asks for it.</summary>
    public TrueTypeFont? Substitute { get; init; }
    /// <summary>Writes the new text in <see cref="Substitute"/> even when the original font could show it.</summary>
    public bool UseSubstitute { get; init; }
    /// <summary>The new size in points, or null to keep the original size.</summary>
    public double? FontSize { get; init; }
    /// <summary>The new fill color, or null to keep the original color.</summary>
    public PdfRgbColor? FillColor { get; init; }
    /// <summary>The new space added after each character in points, or null to keep the original spacing.</summary>
    public double? CharacterSpacing { get; init; }
    /// <summary>Draws a line under the new text.</summary>
    public bool Underline { get; init; }
    /// <summary>Draws a line through the new text.</summary>
    public bool Strike { get; init; }
    internal IReadOnlyList<PdfParagraphLine>? ParagraphLines { get; init; }
}

/// <summary>Which font the new text was written in.</summary>
public enum PdfTextLineFont
{
    /// <summary>The font the replaced text used.</summary>
    Original,
    /// <summary>The substitute font, embedded in the document.</summary>
    Substitute
}

/// <summary>The edited document and how the new text was written.</summary>
/// <param name="Document">The document with the edit as an incremental update.</param>
/// <param name="Font">Which font the new text was written in.</param>
/// <param name="RemovedGlyphs">How many of the old glyphs left the page content.</param>
public sealed record PdfTextLineEditResult(byte[] Document, PdfTextLineFont Font, int RemovedGlyphs);

/// <summary>
/// Rewrites a line of text inside the page content itself. The old glyphs leave the content stream
/// and the new text is written in their place, in the same text object, so it keeps the original
/// font, size, color, clipping and stacking order unless the edit changes them. No cover or other
/// drawing is added over the page.
/// </summary>
public static class PdfTextLineEditor
{
    /// <summary>Reports whether the text in an area can be rewritten, without changing the document.</summary>
    public static PdfTextLineInspection Inspect(PdfDocument document, int pageIndex, PdfContentBounds region,
        CancellationToken cancellationToken = default) =>
        PdfContentRedaction.InspectLine(document, pageIndex, region, cancellationToken);

    /// <summary>Replaces the text in an area and returns the document with an incremental update.</summary>
    /// <exception cref="NotSupportedException">The line cannot be rewritten, or the new text needs a
    /// substitute font that was not given or cannot be used.</exception>
    public static PdfTextLineEditResult Replace(PdfDocument document, PdfTextLineEdit edit,
        CancellationToken cancellationToken = default) =>
        PdfContentRedaction.ReplaceLine(document, edit, cancellationToken);
}

public static partial class PdfContentRedaction
{
    private const double ReplacementSpaceUnits = 250;

    internal static PdfTextLineInspection InspectLine(PdfDocument document, int pageIndex, PdfContentBounds region,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        LineProbe probe = Probe(document, pageIndex, region, null, cancellationToken);
        return probe.Inspection;
    }

    internal static PdfTextLineEditResult ReplaceLine(PdfDocument document, PdfTextLineEdit edit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(edit.Text);
        if (edit.FontSize is double requested && (!double.IsFinite(requested) || requested <= 0 || requested > 1000))
            throw new ArgumentOutOfRangeException(nameof(edit), "The font size must be above 0 and at most 1000 points.");
        if (edit.CharacterSpacing is double spacing && (!double.IsFinite(spacing) || Math.Abs(spacing) > 1000))
            throw new ArgumentOutOfRangeException(nameof(edit), "The character spacing must be finite.");
        if (edit.FillColor is PdfRgbColor color && new[] { color.Red, color.Green, color.Blue }
                .Any(component => !double.IsFinite(component) || component is < 0 or > 1))
            throw new ArgumentOutOfRangeException(nameof(edit), "Color components must be from 0 through 1.");
        if (edit.UseSubstitute && edit.Substitute is null)
            throw new ArgumentException("A substitute font is required.", nameof(edit));

        var used = new Dictionary<object, Dictionary<uint, PdfDecodedCharacter>>();
        LineProbe probe = Probe(document, edit.PageIndex, edit.Region, used, cancellationToken);
        if (!probe.Inspection.CanEdit)
            throw new NotSupportedException(probe.Inspection.Blocker switch
            {
                PdfTextLineBlocker.NoText => "No readable text lies in the area.",
                PdfTextLineBlocker.InvisibleText => "The text in the area is drawn invisibly.",
                PdfTextLineBlocker.ZeroSizeText => "The text in the area has a zero font size.",
                _ => "The text in the area uses a font that cannot be decoded."
            });
        TextState first = probe.Insertion.State!;
        PdfExtractionFont font = first.Font!;
        string[] lines = ReplacementLines(edit.Text);

        List<EncodedLine>? encoded = null;
        PdfDictionary? fontDictionary = first.FontObject is null ? null : Resolve(document, first.FontObject) as PdfDictionary;
        if (!edit.UseSubstitute && fontDictionary is not null)
        {
            object? key = FontKey(document, first.FontObject);
            bool composite = IsNamed(fontDictionary, SubtypeName, "Type0");
            bool embedded = IsEmbedded(document, fontDictionary);
            encoded = EncodeOriginal(font, composite, embedded, Used(key), lines);
            if (encoded is null && embedded && key is not null && probe.Tree.Pages.Count > 1)
            {
                // A subset holds the glyphs the document draws, so look at the other pages too.
                var fonts = new Dictionary<PdfDictionary, PdfExtractionFont?>(ReferenceEqualityComparer.Instance);
                for (int index = 0; index < probe.Tree.Pages.Count; index++)
                {
                    if (index == edit.PageIndex) continue;
                    PdfPageTreeEntry other = probe.Tree.Pages[index];
                    new ContentRewriter(document, null, [], false, fonts, cancellationToken, glyphsOnly: true,
                        usedCodes: used).RewritePage(other, Geometry(document, other));
                }
                encoded = EncodeOriginal(font, composite, embedded, Used(key), lines);
            }
        }

        var update = new PdfIncrementalUpdateBuilder(document);
        PdfIndirectReference? substituteReference = null;
        PdfTextLineFont written = PdfTextLineFont.Original;
        if (encoded is null)
        {
            TrueTypeFont substitute = edit.Substitute
                ?? throw new NotSupportedException("The original font cannot show the new text.");
            if (font.IsVertical)
                throw new NotSupportedException("Vertical text can only be rewritten in its original font.");
            if (!substitute.EmbeddingAllowed)
                throw new NotSupportedException($"The embedding permissions in {substitute.PostScriptName} prohibit PDF embedding.");
            var usage = new EmbeddedFontUsage(substitute, Name("KPEF"));
            encoded = EncodeSubstitute(substitute, usage, lines);
            if (usage.Mappings.Count > 0) substituteReference = AddEmbeddedFont(update, substitute, usage);
            written = PdfTextLineFont.Substitute;
        }
        if (font.IsVertical && (edit.Underline || edit.Strike))
            throw new NotSupportedException("Vertical text cannot be underlined or struck through.");

        // Sizes and spacing given in points convert through the text's own transformation.
        Matrix device = probe.Insertion.Glyph.Then(first.Ctm);
        double vertical = Math.Sqrt(device.C * device.C + device.D * device.D);
        double horizontal = Math.Sqrt(device.A * device.A + device.B * device.B);
        double? sizeFactor = null;
        if (edit.FontSize is double points && vertical > 0 && first.Size != 0)
        {
            double factor = points / (Math.Abs(first.Size) * vertical);
            if (Math.Abs(factor - 1) > 1e-6) sizeFactor = factor;
        }
        double? characterSpacing = edit.CharacterSpacing is double tracking && horizontal > 0 && first.Scale != 0
            ? tracking / (horizontal * first.Scale) : null;

        var writer = new LineWriter(encoded, substituteReference, sizeFactor, edit.FillColor, characterSpacing,
            edit.Underline, edit.Strike, edit.ParagraphLines);
        var insertion = new LineInsertion { Writer = writer };
        PdfPageTreeEntry page = probe.Tree.Pages[edit.PageIndex];
        var rewriter = new ContentRewriter(document, update, [edit.Region], false,
            new Dictionary<PdfDictionary, PdfExtractionFont?>(ReferenceEqualityComparer.Instance),
            cancellationToken, glyphsOnly: true, insertion: insertion);
        List<PdfContentInstruction> content = rewriter.RewritePage(page, probe.Geometry);
        if (!insertion.Written)
            throw new InvalidOperationException("The page changed while its text was being rewritten.");
        if (insertion.AfterText is { } rules) content.AddRange(rules);

        var instructions = new List<PdfContentInstruction>(content.Count + 2) { Op("q") };
        instructions.AddRange(content);
        instructions.Add(Op("Q"));
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(PdfContentStreamWriter.Write(instructions));
        PdfIndirectReference contentReference = update.AddObject(new PdfStream(
            new PdfDictionary(new Dictionary<PdfName, PdfObject> { [Name("Filter")] = Name("FlateDecode") }),
            compressed.ToArray()));
        var entries = page.Dictionary.ToDictionary(entry => entry.Key, entry => entry.Value);
        entries[ContentsName] = contentReference;
        entries[ResourcesName] = rewriter.PageScope!.Build();
        update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(entries));
        return new PdfTextLineEditResult(update.Build(), written, rewriter.RemovedGlyphs);

        Dictionary<uint, PdfDecodedCharacter> Used(object? key) =>
            key is not null && used.TryGetValue(key, out Dictionary<uint, PdfDecodedCharacter>? codes) ? codes : [];
    }

    private sealed record LineProbe(PdfTextLineInspection Inspection, LineInsertion Insertion,
        PdfPageTree Tree, PageGeometry Geometry);

    private static LineProbe Probe(PdfDocument document, int pageIndex, PdfContentBounds region,
        Dictionary<object, Dictionary<uint, PdfDecodedCharacter>>? used, CancellationToken cancellationToken,
        bool paragraph = false)
    {
        if (!double.IsFinite(region.Left) || !double.IsFinite(region.Bottom) || !double.IsFinite(region.Right)
            || !double.IsFinite(region.Top) || region.Right <= region.Left || region.Top <= region.Bottom)
            throw new ArgumentOutOfRangeException(nameof(region), "The area must be finite and not empty.");
        PdfPageTree tree = PdfPageTree.Read(document);
        if (pageIndex < 0 || pageIndex >= tree.Pages.Count) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        PdfPageTreeEntry page = tree.Pages[pageIndex];
        PageGeometry geometry = Geometry(document, page);
        var insertion = new LineInsertion { InspectParagraph = paragraph };
        var rewriter = new ContentRewriter(document, null, [region], false,
            new Dictionary<PdfDictionary, PdfExtractionFont?>(ReferenceEqualityComparer.Instance),
            cancellationToken, glyphsOnly: true, insertion: insertion, usedCodes: used);
        rewriter.RewritePage(page, geometry);
        PdfTextLineBlocker blocker = insertion.Glyphs == 0
            ? rewriter.Reasons.Contains("UnreadableFont") || rewriter.Reasons.Contains("UndecodableText")
                ? PdfTextLineBlocker.UnreadableText : PdfTextLineBlocker.NoText
            : insertion.Invisible ? PdfTextLineBlocker.InvisibleText
            : insertion.ZeroSize ? PdfTextLineBlocker.ZeroSizeText
            : PdfTextLineBlocker.None;
        string fontName = insertion.State?.Font?.FontName ?? string.Empty;
        double size = 0;
        if (insertion.State is TextState state)
        {
            Matrix device = insertion.Glyph.Then(state.Ctm);
            size = Math.Abs(state.Size) * Math.Sqrt(device.C * device.C + device.D * device.D);
        }
        return new LineProbe(new PdfTextLineInspection(insertion.Glyphs, blocker, fontName, size), insertion, tree, geometry);
    }

    private static string[] ReplacementLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace('\t', ' ').Split('\n');

    // Fonts are told apart by object number, since one font object may resolve more than once.
    private static object? FontKey(PdfDocument document, PdfObject? fontObject) => fontObject switch
    {
        null => null,
        PdfIndirectReference reference => reference.ObjectNumber,
        _ => Resolve(document, fontObject) as PdfDictionary
    };

    private static bool IsEmbedded(PdfDocument document, PdfDictionary font)
    {
        if (IsNamed(font, SubtypeName, "Type3")) return true;
        PdfDictionary described = font;
        if (IsNamed(font, SubtypeName, "Type0") && font.TryGetValue(Name("DescendantFonts"), out PdfObject? descendants)
            && Resolve(document, descendants) is PdfArray array && array.Count > 0
            && Resolve(document, array[0]) is PdfDictionary descendant)
            described = descendant;
        return described.TryGetValue(Name("FontDescriptor"), out PdfObject? value)
            && Resolve(document, value) is PdfDictionary descriptor
            && (descriptor.ContainsKey(Name("FontFile")) || descriptor.ContainsKey(Name("FontFile2"))
                || descriptor.ContainsKey(Name("FontFile3")));
    }

    private sealed record EncodedLine(IReadOnlyList<PdfObject> Items, double Units, int Glyphs);

    // An embedded font can only show the glyphs its program holds, and a subset holds the ones the
    // document draws, so only codes already drawn in that font are used. A font that is not
    // embedded comes from the reader's own copy, so any code it maps with a width will do.
    // A space the font cannot show becomes a gap of the same width instead.
    private static List<EncodedLine>? EncodeOriginal(PdfExtractionFont font, bool composite, bool embedded,
        Dictionary<uint, PdfDecodedCharacter> used, string[] lines)
    {
        var byText = new Dictionary<string, PdfDecodedCharacter>(StringComparer.Ordinal);
        foreach (PdfDecodedCharacter character in used.Values) Consider(character);
        if (!embedded)
        {
            foreach (PdfDecodedCharacter mapping in font.Unicode.Mappings) Consider(mapping);
            if (!composite)
                for (int code = 0; code < 256; code++)
                {
                    IReadOnlyList<PdfDecodedCharacter> decoded;
                    try { decoded = font.Decode(new[] { (byte)code }); }
                    catch (Exception error) when (error is FormatException or ArgumentException or NotSupportedException)
                    {
                        continue;
                    }
                    if (decoded.Count == 1 && decoded[0].ByteLength == 1) Consider(decoded[0]);
                }
        }
        int longest = byText.Count == 0 ? 1 : Math.Max(1, byText.Keys.Max(key => key.Length));

        var result = new List<EncodedLine>(lines.Length);
        foreach (string line in lines)
        {
            var items = new List<PdfObject>();
            var pending = new List<byte>();
            double units = 0;
            int glyphs = 0;
            int index = 0;
            while (index < line.Length)
            {
                PdfDecodedCharacter? match = null;
                int length = Math.Min(longest, line.Length - index);
                for (; length > 0; length--)
                {
                    if (index + length < line.Length && char.IsLowSurrogate(line[index + length])) continue;
                    if (byText.TryGetValue(line.Substring(index, length), out PdfDecodedCharacter found))
                    {
                        match = found;
                        break;
                    }
                }
                if (match is PdfDecodedCharacter character)
                {
                    for (int shift = (character.ByteLength - 1) * 8; shift >= 0; shift -= 8)
                        pending.Add((byte)(character.Code >> shift));
                    units += font.GetWidth(character.Code);
                    glyphs++;
                    index += length;
                    continue;
                }
                if (line[index] != ' ') return null;
                if (pending.Count > 0)
                {
                    items.Add(new PdfString(pending.ToArray(), PdfStringForm.Hexadecimal));
                    pending.Clear();
                }
                items.Add(new PdfReal(-ReplacementSpaceUnits));
                units += ReplacementSpaceUnits;
                index++;
            }
            if (pending.Count > 0) items.Add(new PdfString(pending.ToArray(), PdfStringForm.Hexadecimal));
            result.Add(new EncodedLine(Merge(items), units, glyphs));
        }
        return result;

        void Consider(PdfDecodedCharacter character)
        {
            if (string.IsNullOrEmpty(character.Text) || character.Text.Contains('�', StringComparison.Ordinal)
                || char.IsControl(character.Text[0]) || byText.ContainsKey(character.Text)) return;
            if (!used.ContainsKey(character.Code) && !(font.GetWidth(character.Code) > 0)) return;
            byText[character.Text] = character;
        }

        static List<PdfObject> Merge(List<PdfObject> items)
        {
            var merged = new List<PdfObject>();
            foreach (PdfObject item in items)
            {
                if (item is PdfReal real && merged.Count > 0 && merged[^1] is PdfReal previous)
                    merged[^1] = new PdfReal(previous.Value + real.Value);
                else merged.Add(item);
            }
            return merged;
        }
    }

    private static List<EncodedLine> EncodeSubstitute(TrueTypeFont font, EmbeddedFontUsage usage, string[] lines)
    {
        var result = new List<EncodedLine>(lines.Length);
        foreach (string line in lines)
        {
            var bytes = new List<byte>();
            double units = 0;
            int glyphs = 0;
            foreach (FontGlyphMapping mapping in font.MapText(line))
            {
                if (mapping.Glyph == 0)
                    throw new NotSupportedException($"{font.PostScriptName} has no glyph for {mapping.UnicodeSequence}.");
                ushort code = usage.AddMapping(mapping.Glyph, mapping.UnicodeSequence);
                bytes.Add((byte)(code >> 8));
                bytes.Add((byte)code);
                units += font.GetPdfAdvanceWidth(mapping.Glyph);
                glyphs++;
            }
            result.Add(new EncodedLine(bytes.Count == 0 ? [] : [new PdfString(bytes.ToArray(), PdfStringForm.Hexadecimal)],
                units, glyphs));
        }
        return result;
    }

    private static PdfIndirectReference AddEmbeddedFont(PdfIncrementalUpdateBuilder update, TrueTypeFont font,
        EmbeddedFontUsage usage)
    {
        PdfIndirectReference type0 = update.ReserveObject();
        PdfIndirectReference cidFont = update.ReserveObject();
        PdfIndirectReference descriptor = update.ReserveObject();
        PdfIndirectReference fontFile = update.ReserveObject();
        PdfIndirectReference toUnicode = update.ReserveObject();
        PdfIndirectReference encoding = update.ReserveObject();
        EmbeddedTrueTypeFontObjects values = PdfEmbeddedTrueTypeFontFactory.Create(
            font, usage.Mappings, type0, cidFont, descriptor, fontFile, toUnicode, encoding);
        update.SetObject(type0, values.Type0).SetObject(cidFont, values.CidFont)
            .SetObject(descriptor, values.Descriptor).SetObject(fontFile, values.FontFile)
            .SetObject(toUnicode, values.ToUnicode).SetObject(encoding, values.Encoding);
        return type0;
    }

    private sealed record LineWriter(IReadOnlyList<EncodedLine> Lines, PdfIndirectReference? Substitute,
        double? SizeFactor, PdfRgbColor? Fill, double? CharacterSpacing, bool Underline, bool Strike,
        IReadOnlyList<PdfParagraphLine>? ParagraphLines);

    /// <summary>
    /// Follows the glyphs of an edited line through the content. The first one marks where the new
    /// text goes: right after the text-showing operator that held it, inside the same text object,
    /// so it inherits the state the old text was drawn with. After the new text the text state and
    /// position are put back exactly, so everything drawn later in that text object stays in place.
    /// </summary>
    private sealed class LineInsertion
    {
        private static readonly PdfName FontCategory = Name("Font");
        private bool _pending;

        internal bool InspectParagraph { get; init; }
        internal LineWriter? Writer { get; init; }
        internal TextState? State { get; private set; }
        internal Matrix Glyph { get; private set; }
        internal int Glyphs { get; private set; }
        internal bool Invisible { get; private set; }
        internal bool ZeroSize { get; private set; }
        internal bool ParagraphUnsafe { get; private set; }
        internal bool Written { get; private set; }
        internal List<PdfContentInstruction>? AfterText { get; set; }
        internal int AfterTextDepth { get; private set; }

        internal void Observe(TextState state, Matrix glyph, bool barredFromParagraphs)
        {
            Glyphs++;
            if (state.RenderMode is 3 or 7) Invisible = true;
            if (state.Size == 0) ZeroSize = true;
            if (InspectParagraph)
            {
                if (barredFromParagraphs || state.SoftMasked || state.Size <= 0 || state.RenderMode != 0
                    || state.CharacterSpacing != 0 || state.WordSpacing != 0
                    || state.Scale != 1 || state.Rise != 0 || state.Font?.IsVertical == true
                    || Math.Abs(glyph.A - 1) > 1e-8 || Math.Abs(glyph.B) > 1e-8 || Math.Abs(glyph.C) > 1e-8
                    || Math.Abs(glyph.D - 1) > 1e-8 || Math.Abs(state.Ctm.A - 1) > 1e-8
                    || Math.Abs(state.Ctm.B) > 1e-8 || Math.Abs(state.Ctm.C) > 1e-8 || Math.Abs(state.Ctm.D - 1) > 1e-8)
                    ParagraphUnsafe = true;
                if (State is { } first && (first.FontObject != state.FontObject || first.Size != state.Size
                    || first.Ctm != state.Ctm || first.Clip != state.Clip || first.ExtGStates != state.ExtGStates
                    || !PdfContentStreamWriter.Write(first.Fill).AsSpan().SequenceEqual(PdfContentStreamWriter.Write(state.Fill))))
                    ParagraphUnsafe = true;
            }
            if (State is not null) return;
            State = state;
            Glyph = glyph;
            _pending = Writer is not null;
        }

        internal void WritePending(List<PdfContentInstruction> output, TextState state, Matrix text, Matrix line,
            ResourceScope scope, int depth)
        {
            if (!_pending) return;
            _pending = false;
            LineWriter writer = Writer!;
            double size0 = state.Size;
            double factor = writer.SizeFactor ?? 1;
            double size = size0 * factor;
            bool fontChange = writer.Substitute is not null || writer.SizeFactor is not null;
            PdfName? original = state.FontResource
                ?? (fontChange && state.FontObject is { } fontObject ? scope.Add(FontCategory, "KPEO", fontObject) : null);
            if (fontChange && original is null)
                throw new NotSupportedException("The original font cannot be selected again after the new text.");
            if (writer.Substitute is { } substitute)
                output.Add(new PdfContentInstruction("Tf", 0, [scope.Add(FontCategory, "KPEF", substitute), new PdfReal(size)]));
            else if (writer.SizeFactor is not null)
                output.Add(new PdfContentInstruction("Tf", 0, [original!, new PdfReal(size)]));
            if (writer.Fill is PdfRgbColor fill) output.Add(Op("rg", fill.Red, fill.Green, fill.Blue));
            if (writer.CharacterSpacing is double spacing) output.Add(Op("Tc", spacing));
            output.Add(Op("Tm", Glyph.A, Glyph.B, Glyph.C, Glyph.D, Glyph.E, Glyph.F));
            double leading = state.Leading > 0 ? state.Leading * factor : Math.Abs(size) * 1.2;
            for (int index = 0; index < writer.Lines.Count; index++)
            {
                if (writer.ParagraphLines is { } positions && index < positions.Count)
                    output.Add(Op("Tm", 1, 0, 0, 1, positions[index].X - state.Ctm.E,
                        positions[index].Baseline - state.Ctm.F));
                else if (index > 0) output.Add(Op("Td", 0, -leading));
                if (writer.Lines[index].Items.Count > 0)
                    output.Add(new PdfContentInstruction("TJ", 0, [new PdfArray(writer.Lines[index].Items)]));
            }

            if (fontChange) output.Add(new PdfContentInstruction("Tf", 0, [original!, new PdfReal(size0)]));
            if (writer.Fill is not null) output.AddRange(state.Fill.Length > 0 ? state.Fill : [Op("g", 0)]);
            if (writer.CharacterSpacing is not null) output.Add(Op("Tc", state.CharacterSpacing));
            output.Add(Op("Tm", line.A, line.B, line.C, line.D, line.E, line.F));
            bool vertical = state.Font?.IsVertical == true;
            double axisX = vertical ? line.C : line.A, axisY = vertical ? line.D : line.B;
            double length = axisX * axisX + axisY * axisY;
            double moved = length > 0 ? ((text.E - line.E) * axisX + (text.F - line.F) * axisY) / length : 0;
            if (Math.Abs(moved) > 1e-9)
            {
                double scale = vertical ? size0 : size0 * state.Scale;
                if (scale == 0) throw new NotSupportedException("The text position cannot be restored at a zero font size.");
                output.Add(new PdfContentInstruction("TJ", 0, [new PdfArray([new PdfReal(-moved * 1000 / scale)])]));
            }
            Written = true;

            if (writer.Underline || writer.Strike)
            {
                AfterText = Rules(writer, state, size, leading);
                AfterTextDepth = depth;
            }
        }

        // Underline and strikethrough are drawn after the text object ends, since paths cannot be
        // drawn inside one. Nothing changes the transformation inside a text object, so the text
        // matrix of the first glyph still places them.
        private List<PdfContentInstruction> Rules(LineWriter writer, TextState state, double size, double leading)
        {
            double spacing = writer.CharacterSpacing ?? state.CharacterSpacing;
            double scale = Math.Sqrt(Math.Abs(Glyph.A * Glyph.D - Glyph.B * Glyph.C));
            var rules = new List<PdfContentInstruction> { Op("q") };
            if (writer.Fill is PdfRgbColor fill) rules.Add(Op("RG", fill.Red, fill.Green, fill.Blue));
            else if (state.Fill.Length == 0) rules.Add(Op("G", 0));
            else rules.AddRange(state.Fill.Select(item => new PdfContentInstruction(
                item.Operator.ToUpperInvariant(), 0, [.. item.Operands])));
            rules.Add(Op("w", Math.Max(0.01, Math.Abs(size) / 16 * scale)));
            for (int index = 0; index < writer.Lines.Count; index++)
            {
                EncodedLine line = writer.Lines[index];
                double width = (line.Units / 1000 * size + line.Glyphs * spacing) * state.Scale;
                if (width <= 0) continue;
                double baseline = -index * leading;
                foreach (double offset in new[] { writer.Underline ? -0.12 : double.NaN, writer.Strike ? 0.3 : double.NaN })
                {
                    if (double.IsNaN(offset)) continue;
                    PdfPoint start = Glyph.Point(0, baseline + offset * size);
                    PdfPoint end = Glyph.Point(width, baseline + offset * size);
                    rules.Add(Op("m", start.X, start.Y));
                    rules.Add(Op("l", end.X, end.Y));
                }
            }
            rules.Add(Op("S"));
            rules.Add(Op("Q"));
            return rules;
        }
    }
}
