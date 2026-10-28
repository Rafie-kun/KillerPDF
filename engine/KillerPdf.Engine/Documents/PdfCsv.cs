using System.Globalization;
using System.Text;

namespace KillerPdf.Engine.Documents;

/// <summary>
/// CSV fields for every report KillerPDF writes. Values come from PDFs and file names, so one that a
/// spreadsheet would evaluate as a formula is prefixed with an apostrophe and stays text.
/// </summary>
public static class PdfCsv
{
    /// <summary>
    /// True when a spreadsheet would treat the value as a formula: its first visible character, after
    /// compatibility normalization and skipping white space and invisible format characters, is =, +, - or @.
    /// </summary>
    public static bool StartsFormula(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (Rune rune in text.Normalize(NormalizationForm.FormKC).EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsWhiteSpace(rune) || category is UnicodeCategory.Control or UnicodeCategory.Format) continue;
            return rune.Value is '=' or '+' or '-' or '@';
        }
        return false;
    }

    /// <summary>
    /// One CSV field: formula-looking text gets an apostrophe, and the field is quoted when
    /// <paramref name="alwaysQuote"/> is set or it contains a comma, quote or line break.
    /// </summary>
    public static string Field(string value, bool alwaysQuote)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (StartsFormula(value)) value = "'" + value;
        return alwaysQuote || value.AsSpan().IndexOfAny(",\"\r\n") >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }
}
