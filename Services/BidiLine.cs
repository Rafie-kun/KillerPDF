using System.Globalization;
using System.Text;

namespace KillerPDF.Services;

/// <summary>A piece of one line in visual order. Right-to-left text stays in logical order, so the
/// caller places its text elements from the right and draws each one's <see cref="BidiLine.Mirrored"/>
/// glyph while keeping the typed character for copy and search.</summary>
internal readonly record struct BidiRun(string Text, bool RightToLeft);

/// <summary>
/// Orders saved text lines the way the overlay's left-to-right TextBlock shows them (#435).
/// This is the Unicode bidirectional algorithm for a left-to-right paragraph without explicit
/// embeddings, which covers Hebrew mixed with numbers and Latin. Directions are resolved over the
/// whole paragraph and each wrapped line is reordered on its own, as UAX #9 and WPF do. Arabic
/// needs shaping the save path does not do, so it keeps reading as left-to-right text here.
/// </summary>
internal static class BidiLine
{
    private enum Kind { L, R, EN, AN, ES, ET, CS, NSM, WS, S, ON }

    internal static IReadOnlyList<BidiRun> VisualRuns(string line) => VisualRuns(line, 0, line.Length);

    /// <summary>The runs of the wrapped line that starts at <paramref name="start"/> in its hard
    /// paragraph, in visual order.</summary>
    internal static IReadOnlyList<BidiRun> VisualRuns(string paragraph, int start, int length)
    {
        string line = paragraph.Substring(start, length);
        List<(int Start, string Text)> elements = Elements(line);
        if (elements.Count == 0) return [];
        // WPF lays out a line with no right-to-left character at the paragraph level unchanged.
        if (!elements.Any(element => Classify(element.Text) == Kind.R)) return [new BidiRun(line, false)];

        int[] levels = ResolveLevels(paragraph, out Kind[] original);
        int count = elements.Count;
        var lineLevels = new int[count];
        var lineKinds = new Kind[count];
        for (int i = 0; i < count; i++)
        {
            lineLevels[i] = levels[start + elements[i].Start];
            lineKinds[i] = original[start + elements[i].Start];
        }
        // L1: tabs, and spaces before a tab or at the line end, return to the paragraph level.
        bool reset = true;
        for (int i = count - 1; i >= 0; i--)
        {
            if (lineKinds[i] == Kind.S) { lineLevels[i] = 0; reset = true; }
            else if (reset && lineKinds[i] == Kind.WS) lineLevels[i] = 0;
            else reset = false;
        }

        // L2: from the highest level down to 1, reverse every stretch at that level or above.
        var order = Enumerable.Range(0, count).ToArray();
        for (int level = lineLevels.Max(); level >= 1; level--)
            for (int i = 0; i < count; i++)
            {
                if (lineLevels[order[i]] < level) continue;
                int end = i;
                while (end < count && lineLevels[order[end]] >= level) end++;
                Array.Reverse(order, i, end - i);
                i = end - 1;
            }

        var runs = new List<BidiRun>();
        for (int i = 0; i < count;)
        {
            int level = lineLevels[order[i]];
            bool rightToLeft = level % 2 == 1;
            int step = rightToLeft ? -1 : 1;
            int end = i + 1;
            while (end < count && lineLevels[order[end]] == level && order[end] == order[end - 1] + step) end++;
            int first = rightToLeft ? order[end - 1] : order[i];
            var text = new StringBuilder();
            for (int index = first; index < first + end - i; index++) text.Append(elements[index].Text);
            runs.Add(new BidiRun(text.ToString(), rightToLeft));
            i = end;
        }
        return runs;
    }

    /// <summary>The glyph a right-to-left run draws for an element: its Bidi_Mirroring_Glyph pair
    /// when it has one (UAX #9 L4), otherwise the element itself.</summary>
    internal static string Mirrored(string element) =>
        element.Length == 1 && MirrorPairs.TryGetValue(element[0], out char mirror) ? mirror.ToString() : element;

    // Embedding levels per UTF-16 index for a paragraph, from rules W1 to I1.
    private static int[] ResolveLevels(string paragraph, out Kind[] originalByChar)
    {
        List<(int Start, string Text)> elements = Elements(paragraph);
        int count = elements.Count;
        var original = elements.Select(element => Classify(element.Text)).ToArray();
        var kinds = (Kind[])original.Clone();

        // W1: a mark with no base takes the kind of what it follows.
        for (int i = 0; i < count; i++)
            if (kinds[i] == Kind.NSM) kinds[i] = i == 0 ? Kind.L : kinds[i - 1];
        // W4: one separator between two numbers of a kind joins them.
        for (int i = 1; i + 1 < count; i++)
        {
            if (kinds[i - 1] == Kind.EN && kinds[i + 1] == Kind.EN && kinds[i] is Kind.ES or Kind.CS)
                kinds[i] = Kind.EN;
            else if (kinds[i - 1] == Kind.AN && kinds[i + 1] == Kind.AN && kinds[i] == Kind.CS)
                kinds[i] = Kind.AN;
        }
        // W5: currency and percent signs touching a European number join it.
        for (int i = 0; i < count; i++)
        {
            if (kinds[i] != Kind.ET) continue;
            int end = i;
            while (end < count && kinds[end] == Kind.ET) end++;
            if ((i > 0 && kinds[i - 1] == Kind.EN) || (end < count && kinds[end] == Kind.EN))
                for (int j = i; j < end; j++) kinds[j] = Kind.EN;
            i = end - 1;
        }
        // W6: the separators and terminators left over are neutral.
        for (int i = 0; i < count; i++)
            if (kinds[i] is Kind.ES or Kind.ET or Kind.CS) kinds[i] = Kind.ON;
        // W7: a European number after left-to-right text is left-to-right text.
        Kind strong = Kind.L;
        for (int i = 0; i < count; i++)
        {
            if (kinds[i] is Kind.L or Kind.R) strong = kinds[i];
            else if (kinds[i] == Kind.EN && strong == Kind.L) kinds[i] = Kind.L;
        }
        // N1 and N2: neutrals between text of one direction take it (numbers count as right-to-left),
        // and the rest follow the left-to-right paragraph.
        for (int i = 0; i < count; i++)
        {
            if (kinds[i] is not (Kind.WS or Kind.S or Kind.ON)) continue;
            int end = i;
            while (end < count && kinds[end] is Kind.WS or Kind.S or Kind.ON) end++;
            Kind before = i == 0 ? Kind.L : Direction(kinds[i - 1]);
            Kind after = end == count ? Kind.L : Direction(kinds[end]);
            for (int j = i; j < end; j++) kinds[j] = before == after ? before : Kind.L;
            i = end - 1;
        }

        // I1: right-to-left text sits one level up and numbers two.
        var levels = new int[paragraph.Length];
        originalByChar = new Kind[paragraph.Length];
        for (int i = 0; i < count; i++)
        {
            int level = kinds[i] switch { Kind.R => 1, Kind.EN or Kind.AN => 2, _ => 0 };
            for (int c = elements[i].Start; c < elements[i].Start + elements[i].Text.Length; c++)
            {
                levels[c] = level;
                originalByChar[c] = original[i];
            }
        }
        return levels;
    }

    private static List<(int Start, string Text)> Elements(string text)
    {
        var elements = new List<(int, string)>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext()) elements.Add((enumerator.ElementIndex, enumerator.GetTextElement()));
        return elements;
    }

    private static Kind Direction(Kind kind) => kind is Kind.EN or Kind.AN ? Kind.R : kind;

    // A text element takes the class of its first scalar; a lone surrogate reads as U+FFFD.
    private static Kind Classify(string element)
    {
        Rune.DecodeFromUtf16(element, out Rune rune, out _);
        return Classify(rune);
    }

    private static Kind Classify(Rune rune)
    {
        int value = rune.Value;
        if (value == 0xFB29) return Kind.ES;
        if (value is >= 0x0590 and <= 0x05FF or >= 0xFB1D and <= 0xFB4F or 0x200F) return Kind.R;
        if (value is >= '0' and <= '9' or 0xB2 or 0xB3 or 0xB9 or 0x2070 or >= 0x2074 and <= 0x2079
            or >= 0x2080 and <= 0x2089 or >= 0x2488 and <= 0x249B or >= 0x06F0 and <= 0x06F9
            or >= 0xFF10 and <= 0xFF19 or >= 0x1F100 and <= 0x1F10A) return Kind.EN;
        if (value is >= 0x0660 and <= 0x0669 or 0x066B or 0x066C) return Kind.AN;
        if (value is '+' or '-' or 0x207A or 0x207B or 0x208A or 0x208B or 0x2212 or 0xFE62 or 0xFE63
            or 0xFF0B or 0xFF0D) return Kind.ES;
        if (value is '#' or '$' or '%' or >= 0xA2 and <= 0xA5 or 0xB0 or 0xB1 or 0x058F or 0x0E3F or 0x17DB
            or >= 0x2030 and <= 0x2034 or >= 0x20A0 and <= 0x20CF or 0x212E or 0x2213 or 0xFE5F or 0xFE69
            or 0xFE6A or 0xFF03 or 0xFF04 or 0xFF05 or 0xFFE0 or 0xFFE1 or 0xFFE5 or 0xFFE6) return Kind.ET;
        if (value is ',' or '.' or '/' or ':' or 0xA0 or 0x202F or 0x2044 or 0xFE50 or 0xFE52 or 0xFE55
            or 0xFF0C or 0xFF0E or 0xFF0F or 0xFF1A) return Kind.CS;
        if (value == '\t') return Kind.S;
        if (value is ' ' or 0x1680 or >= 0x2000 and <= 0x200A or 0x2028 or 0x205F or 0x3000) return Kind.WS;
        if (value == 0x200E) return Kind.L;
        // Most spacing modifier letters are neutral; only the letter-like ones are left-to-right.
        if (value is >= 0x02B9 and <= 0x02FF)
            return value is <= 0x02BA or >= 0x02C2 and <= 0x02CF or >= 0x02D2 and <= 0x02DF
                or >= 0x02E5 and <= 0x02ED or >= 0x02EF ? Kind.ON : Kind.L;
        return Rune.GetUnicodeCategory(rune) switch
        {
            UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format => Kind.NSM,
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber
                or UnicodeCategory.PrivateUse => Kind.L,
            // Fractions, circled and other number signs are neutral (the European ones are above).
            _ => value is >= 0x1F1E6 and <= 0x1F1FF ? Kind.L : Kind.ON
        };
    }

    // L4: Bidi_Mirroring_Glyph pairs from the Unicode BidiMirroring data for the characters text
    // boxes meet: brackets, quotation marks, comparison and set signs, and their full-width forms.
    private static readonly Dictionary<char, char> MirrorPairs = BuildMirrorPairs(
        "()", "<>", "[]", "{}", "«»", "‹›", "⁅⁆", "⁽⁾", "₍₎",
        "∈∋", "∉∌", "∊∍", "∼∽", "≃⋍", "≒≓",
        "≔≕", "≤≥", "≦≧", "≨≩", "≪≫", "≮≯",
        "≰≱", "≲≳", "≴≵", "≶≷", "≸≹", "≺≻",
        "≼≽", "≾≿", "⊀⊁", "⊂⊃", "⊄⊅", "⊆⊇",
        "⊈⊉", "⊊⊋", "⊏⊐", "⊑⊒", "⊢⊣", "⊰⊱",
        "⊲⊳", "⊴⊵", "⊶⊷", "⋉⋊", "⋋⋌", "⋐⋑",
        "⋖⋗", "⋘⋙", "⋚⋛", "⋜⋝", "⋞⋟", "⋠⋡",
        "⋢⋣", "⋤⋥", "⋦⋧", "⋨⋩", "⋪⋫", "⋬⋭",
        "⋰⋱", "⌈⌉", "⌊⌋", "〈〉", "❨❩", "❪❫",
        "❬❭", "❮❯", "❰❱", "❲❳", "❴❵", "⟃⟄",
        "⟅⟆", "⟦⟧", "⟨⟩", "⟪⟫", "⟬⟭", "⟮⟯",
        "⦃⦄", "⦅⦆", "⦇⦈", "⦉⦊", "⦋⦌", "⦍⦐",
        "⦏⦎", "⦑⦒", "⦓⦔", "⦕⦖", "⦗⦘", "⧼⧽",
        "⸂⸃", "⸄⸅", "⸉⸊", "⸌⸍", "⸜⸝", "⸠⸡",
        "⸢⸣", "⸤⸥", "⸦⸧", "⸨⸩", "〈〉", "《》",
        "「」", "『』", "【】", "〔〕", "〖〗", "〘〙",
        "〚〛", "﹙﹚", "﹛﹜", "﹝﹞", "﹤﹥", "（）",
        "＜＞", "［］", "｛｝", "｟｠", "｢｣");

    private static Dictionary<char, char> BuildMirrorPairs(params string[] pairs)
    {
        var map = new Dictionary<char, char>();
        foreach (string pair in pairs)
        {
            map[pair[0]] = pair[1];
            map[pair[1]] = pair[0];
        }
        return map;
    }
}
