using System.Globalization;
using System.Text;

namespace KillerPDF.Services;

/// <summary>A piece of one line in visual order. Right-to-left text stays in logical order, with
/// brackets already mirrored, so the caller places its text elements from the right.</summary>
internal readonly record struct BidiRun(string Text, bool RightToLeft);

/// <summary>
/// Orders one saved text line the way the overlay's left-to-right TextBlock shows it (#435).
/// This is the Unicode bidirectional algorithm for a left-to-right paragraph without explicit
/// embeddings, which covers Hebrew mixed with numbers and Latin. Arabic needs shaping the save
/// path does not do, so it keeps reading as left-to-right text here.
/// </summary>
internal static class BidiLine
{
    private enum Kind { L, R, EN, ES, ET, CS, NSM, WS, S, ON }

    internal static IReadOnlyList<BidiRun> VisualRuns(string line)
    {
        var elements = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(line);
        while (enumerator.MoveNext()) elements.Add(enumerator.GetTextElement());
        if (elements.Count == 0) return [];
        var original = elements.Select(element => Classify(Rune.GetRuneAt(element, 0))).ToArray();
        if (!original.Contains(Kind.R)) return [new BidiRun(line, false)];
        var kinds = (Kind[])original.Clone();
        int count = kinds.Length;

        // W1: a mark with no base takes the kind of what it follows.
        for (int i = 0; i < count; i++)
            if (kinds[i] == Kind.NSM) kinds[i] = i == 0 ? Kind.L : kinds[i - 1];
        // W4: one separator between two numbers joins them.
        for (int i = 1; i + 1 < count; i++)
            if (kinds[i - 1] == Kind.EN && kinds[i + 1] == Kind.EN && kinds[i] is Kind.ES or Kind.CS)
                kinds[i] = Kind.EN;
        // W5: currency and percent signs touching a number join it.
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
        // W7: a number after left-to-right text is left-to-right text.
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
        var levels = kinds.Select(kind => kind switch { Kind.R => 1, Kind.EN => 2, _ => 0 }).ToArray();
        // L1: tabs, and spaces before a tab or at the line end, return to the paragraph level.
        bool reset = true;
        for (int i = count - 1; i >= 0; i--)
        {
            if (original[i] == Kind.S) { levels[i] = 0; reset = true; }
            else if (reset && original[i] == Kind.WS) levels[i] = 0;
            else reset = false;
        }

        // L2: from the highest level down to 1, reverse every stretch at that level or above.
        var order = Enumerable.Range(0, count).ToArray();
        for (int level = levels.Max(); level >= 1; level--)
            for (int i = 0; i < count; i++)
            {
                if (levels[order[i]] < level) continue;
                int end = i;
                while (end < count && levels[order[end]] >= level) end++;
                Array.Reverse(order, i, end - i);
                i = end - 1;
            }

        var runs = new List<BidiRun>();
        for (int i = 0; i < count;)
        {
            int level = levels[order[i]];
            bool rightToLeft = level % 2 == 1;
            int step = rightToLeft ? -1 : 1;
            int end = i + 1;
            while (end < count && levels[order[end]] == level && order[end] == order[end - 1] + step) end++;
            int first = rightToLeft ? order[end - 1] : order[i];
            var text = new StringBuilder();
            for (int index = first; index < first + end - i; index++)
                text.Append(rightToLeft ? Mirror(elements[index]) : elements[index]);
            runs.Add(new BidiRun(text.ToString(), rightToLeft));
            i = end;
        }
        return runs;
    }

    private static Kind Direction(Kind kind) => kind == Kind.EN ? Kind.R : kind;

    private static Kind Classify(Rune rune)
    {
        int value = rune.Value;
        if (value is >= 0x0590 and <= 0x05FF or >= 0xFB1D and <= 0xFB4F or 0x200F) return Kind.R;
        if (value is >= '0' and <= '9' or 0xB2 or 0xB3 or 0xB9 or >= 0x2070 and <= 0x2079
            or >= 0x2080 and <= 0x2089 or >= 0xFF10 and <= 0xFF19) return Kind.EN;
        if (value is '+' or '-' or 0x2212 or 0xFE62 or 0xFE63 or 0xFF0B or 0xFF0D) return Kind.ES;
        if (value is '#' or '$' or '%' or >= 0xA2 and <= 0xA5 or 0xB0 or 0xB1 or >= 0x2030 and <= 0x2034
            or >= 0x20A0 and <= 0x20CF or 0x212E or 0x2213 or 0xFF03 or 0xFF04 or 0xFF05) return Kind.ET;
        if (value is ',' or '.' or '/' or ':' or 0xA0 or 0x202F or 0x2044 or 0xFF0C or 0xFF0E or 0xFF0F
            or 0xFF1A) return Kind.CS;
        if (value == '\t') return Kind.S;
        if (value is ' ' or 0x1680 or >= 0x2000 and <= 0x200A or 0x2028 or 0x205F or 0x3000) return Kind.WS;
        if (value == 0x200E) return Kind.L;
        return Rune.GetUnicodeCategory(rune) switch
        {
            UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format => Kind.NSM,
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber
                or UnicodeCategory.OtherNumber => Kind.L,
            _ => Kind.ON
        };
    }

    // L4: paired brackets show their mirror image inside right-to-left text.
    private static string Mirror(string element) => element switch
    {
        "(" => ")", ")" => "(", "[" => "]", "]" => "[", "{" => "}", "}" => "{", "<" => ">", ">" => "<",
        "«" => "»", "»" => "«", "‹" => "›", "›" => "‹",
        _ => element
    };
}
