using System.Text;

namespace KillerPdf.Engine.Documents;

/// <summary>
/// The text a search compares: a typeset ligature reads as its letters, and a character that prints
/// nothing is dropped, so a word is found by what the page shows rather than how it was stored.
/// </summary>
public static class PdfSearchText
{
    /// <summary>Returns <paramref name="text"/> with ligatures spelled out and invisible characters removed.</summary>
    public static string Fold(string text) => Fold(text, out _);

    /// <summary>
    /// Returns the folded text and, for each of its characters, the index in <paramref name="text"/> of the
    /// character it came from. The map has one extra entry holding the length of <paramref name="text"/>,
    /// so the end of a match can be read from it.
    /// </summary>
    public static string Fold(string text, out int[] origin) => Fold(text, [], out origin);

    /// <summary>
    /// The joined text of <paramref name="words"/> with one space between them, folded for a search. A
    /// word hyphenated across a line break comes out whole, and <paramref name="origin"/> maps each
    /// character back to the plain join of the words.
    /// </summary>
    public static string FoldWords(IReadOnlyList<PdfExtractedWord> words, out int[] origin) =>
        FoldWords(words, out origin, preserveLineEndHyphens: false);

    /// <summary>Folds joined words, optionally retaining visible hyphens at line breaks.</summary>
    public static string FoldWords(IReadOnlyList<PdfExtractedWord> words, out int[] origin,
        bool preserveLineEndHyphens)
    {
        ArgumentNullException.ThrowIfNull(words);
        var dropped = new HashSet<int>();
        int offset = 0;
        for (int index = 0; index < words.Count; index++)
        {
            offset += words[index].Text.Length;
            if (index + 1 < words.Count && BreaksAcrossLines(words[index], words[index + 1]))
            {
                if (!preserveLineEndHyphens) dropped.Add(offset - 1);
                dropped.Add(offset);
            }
            offset++;
        }
        return Fold(string.Join(' ', words.Select(word => word.Text)), dropped, out origin);
    }

    /// <summary>
    /// Whether <paramref name="word"/> ends in a hyphen at a line end and <paramref name="next"/> carries
    /// on the same word on a later line. A capital after the hyphen is a compound, not a broken word.
    /// </summary>
    public static bool BreaksAcrossLines(PdfExtractedWord word, PdfExtractedWord next)
    {
        ArgumentNullException.ThrowIfNull(word);
        ArgumentNullException.ThrowIfNull(next);
        string text = word.Text;
        if (text.Length < 2 || next.Text.Length == 0 || text[^1] is not ('-' or '‐' or '­')
            || !char.IsLetter(text[^2]) || !char.IsLower(next.Text[0])) return false;
        return Math.Abs(word.BoundingBox.Bottom - next.BoundingBox.Bottom)
            > 0.5 * Math.Min(word.BoundingBox.Height, next.BoundingBox.Height);
    }

    private static string Fold(string text, HashSet<int> dropped, out int[] origin)
    {
        ArgumentNullException.ThrowIfNull(text);
        var folded = new StringBuilder(text.Length);
        var map = new List<int>(text.Length + 1);
        for (int index = 0; index < text.Length; index++)
        {
            if (dropped.Contains(index)) continue;
            foreach (char letter in Letters(text, index))
            {
                folded.Append(letter);
                map.Add(index);
            }
        }
        map.Add(text.Length);
        origin = [.. map];
        return folded.ToString();
    }

    private static string Letters(string text, int index) => text[index] switch
    {
        'ﬀ' => "ff",
        'ﬁ' => "fi",
        'ﬂ' => "fl",
        'ﬃ' => "ffi",
        'ﬄ' => "ffl",
        'ﬅ' or 'ﬆ' => "st",
        ' ' => " ",
        // A soft hyphen, the zero-width spaces and joiners, and a code the font never identified.
        '­' or '​' or '‌' or '‍' or '⁠' or '﻿' or '�' => string.Empty,
        _ => text.Substring(index, 1)
    };
}
