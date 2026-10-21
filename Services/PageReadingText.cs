using System;
using System.Collections.Generic;
using System.Linq;
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

        /// <summary>Reads the characters in the order given (the viewer's visual reading order).</summary>
        internal static PageReadingText Build(IReadOnlyList<(string Value, int Word, int Line)> chars) =>
            BuildOrdered([.. chars.Select((c, caret) => (caret, c.Value, c.Word, c.Line, 0))], chars.Count);

        /// <summary>
        /// Reads a tagged page in its structure order: characters follow the order their marked
        /// content appears in the tag tree, and anything the tree does not reach follows in visual
        /// order so no text is dropped. Each new structure item starts a new line.
        /// </summary>
        internal static PageReadingText BuildTagged(IReadOnlyList<(string Value, int Word, int Line, int? Mcid)> chars,
            IReadOnlyList<int> structureOrder)
        {
            var rank = new Dictionary<int, int>();
            foreach (int mcid in structureOrder) rank.TryAdd(mcid, rank.Count);
            var ordered = chars
                .Select((c, caret) => (Caret: caret, c.Value, c.Word, c.Line,
                    Group: c.Mcid is int id && rank.TryGetValue(id, out int r) ? r : rank.Count))
                .OrderBy(c => c.Group).ThenBy(c => c.Caret)
                .Select(c => (c.Caret, c.Value, c.Word, c.Line, c.Group))
                .ToList();
            return BuildOrdered(ordered, chars.Count);
        }

        private static PageReadingText BuildOrdered(
            IReadOnlyList<(int Caret, string Value, int Word, int Line, int Group)> chars, int caretCount)
        {
            if (chars.Count == 0) return Empty;
            var text = new StringBuilder();
            var carets = new List<int>();
            for (int i = 0; i < chars.Count; i++)
            {
                var c = chars[i];
                if (i > 0 && (c.Line != chars[i - 1].Line || c.Group != chars[i - 1].Group)) { text.Append('\n'); carets.Add(c.Caret); }
                else if (i > 0 && c.Word != chars[i - 1].Word) { text.Append(' '); carets.Add(c.Caret); }
                foreach (char ch in c.Value) { text.Append(ch); carets.Add(c.Caret); }
            }
            carets.Add(caretCount);
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

        /// <summary>
        /// The characters a text range covers, as contiguous caret runs [start, end). Structure
        /// order can make one range span several separate places on the page.
        /// </summary>
        internal List<(int Start, int End)> Segments(int startOffset, int endOffset)
        {
            var result = new List<(int Start, int End)>();
            startOffset = Math.Clamp(startOffset, 0, Text.Length);
            endOffset = Math.Clamp(endOffset, startOffset, Text.Length);
            foreach (int caret in Enumerable.Range(startOffset, endOffset - startOffset)
                         .Where(offset => !char.IsWhiteSpace(Text[offset])).Select(offset => Carets[offset]).Distinct().Order())
            {
                if (result.Count > 0 && result[^1].End == caret) result[^1] = (result[^1].Start, caret + 1);
                else result.Add((caret, caret + 1));
            }
            return result;
        }
    }
}
