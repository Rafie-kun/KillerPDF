using System;
using System.Collections.Generic;
using System.Text;

namespace KillerPDF.Services
{
    /// <summary>
    /// One page's readable text built from its reading-order characters: a space between words,
    /// a newline between lines, and a map from each text offset back to the character (caret)
    /// it belongs to, so a screen reader's range can be drawn and selected on the page.
    /// </summary>
    internal sealed class PageReadingText
    {
        private PageReadingText(string text, int[] carets)
        {
            Text = text;
            Carets = carets;
        }

        internal string Text { get; }

        /// <summary>The caret for each text offset, plus one entry for the end of the text.</summary>
        internal int[] Carets { get; }

        internal static readonly PageReadingText Empty = new(string.Empty, [0]);

        internal static PageReadingText Build(IReadOnlyList<(string Value, int Word, int Line)> chars)
        {
            if (chars.Count == 0) return Empty;
            var text = new StringBuilder();
            var carets = new List<int>();
            for (int i = 0; i < chars.Count; i++)
            {
                if (i > 0 && chars[i].Line != chars[i - 1].Line) { text.Append('\n'); carets.Add(i); }
                else if (i > 0 && chars[i].Word != chars[i - 1].Word) { text.Append(' '); carets.Add(i); }
                foreach (char c in chars[i].Value) { text.Append(c); carets.Add(i); }
            }
            carets.Add(chars.Count);
            return new PageReadingText(text.ToString(), [.. carets]);
        }

        /// <summary>The caret at a text offset.</summary>
        internal int CaretAt(int offset) => Carets[Math.Clamp(offset, 0, Carets.Length - 1)];

        /// <summary>The first text offset at or after a caret.</summary>
        internal int OffsetOf(int caret)
        {
            for (int offset = 0; offset < Carets.Length; offset++)
                if (Carets[offset] >= caret) return offset;
            return Text.Length;
        }
    }
}
