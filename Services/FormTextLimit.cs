using System.Linq;

namespace KillerPDF.Services
{
    /// <summary>
    /// The /MaxLen rule a text field is saved under. The engine counts Unicode characters (runes),
    /// not UTF-16 units, so an emoji is one character; the live editor applies the same count so a
    /// value it accepts is one Save and Print accept too (BF-U007).
    /// </summary>
    internal static class FormTextLimit
    {
        internal static int Count(string text) => text.EnumerateRunes().Count();

        /// <summary>
        /// True when <paramref name="next"/> may replace <paramref name="previous"/>. A value that
        /// was already too long when the file opened is never cut; it may only get shorter.
        /// </summary>
        internal static bool Accepts(string next, string previous, int maximum) =>
            maximum <= 0 || Count(next) <= maximum || Count(next) < Count(previous);

        internal static bool IsOver(string text, int maximum) => maximum > 0 && Count(text) > maximum;
    }
}
