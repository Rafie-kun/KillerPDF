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
    public static string Fold(string text, out int[] origin)
    {
        ArgumentNullException.ThrowIfNull(text);
        var folded = new StringBuilder(text.Length);
        var map = new List<int>(text.Length + 1);
        for (int index = 0; index < text.Length; index++)
        {
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
