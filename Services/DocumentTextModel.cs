using System;
using System.Collections.Generic;
using System.Text;

namespace KillerPDF.Services
{
    /// <summary>A place in the document's readable text: a page and an offset into that page's text.</summary>
    internal readonly record struct TextPosition(int Page, int Offset) : IComparable<TextPosition>
    {
        public int CompareTo(TextPosition other) =>
            Page != other.Page ? Page.CompareTo(other.Page) : Offset.CompareTo(other.Offset);

        public static bool operator <(TextPosition a, TextPosition b) => a.CompareTo(b) < 0;
        public static bool operator >(TextPosition a, TextPosition b) => a.CompareTo(b) > 0;
        public static bool operator <=(TextPosition a, TextPosition b) => a.CompareTo(b) <= 0;
        public static bool operator >=(TextPosition a, TextPosition b) => a.CompareTo(b) >= 0;
    }

    /// <summary>The text units a screen reader moves by (UI Automation TextUnit).</summary>
    internal enum ReadingUnit { Character, Format, Word, Line, Paragraph, Page, Document }

    /// <summary>
    /// The readable text of a whole PDF for UI Automation (BF-U001). Page text is fetched only when
    /// a reader reaches it, so a long document is never extracted up front. Every page but the last
    /// ends with a newline, so reading runs on from one page to the next.
    /// </summary>
    internal sealed class DocumentTextModel
    {
        private readonly Func<int, string> _pageText;
        private readonly string?[] _pages;

        internal DocumentTextModel(int pageCount, Func<int, string> pageText)
        {
            _pageText = pageText;
            _pages = new string?[Math.Max(0, pageCount)];
        }

        internal int PageCount => _pages.Length;
        internal TextPosition Start => new(0, 0);
        internal TextPosition End => PageCount == 0 ? Start : new(PageCount - 1, Page(PageCount - 1).Length);

        /// <summary>The text of one page, with the page separator.</summary>
        internal string Page(int index)
        {
            if (index < 0 || index >= _pages.Length) return string.Empty;
            if (_pages[index] is { } cached) return cached;
            string text = (_pageText(index) ?? string.Empty).Replace("\r\n", "\n").TrimEnd('\n');
            if (index < _pages.Length - 1) text += "\n";
            return _pages[index] = text;
        }

        /// <summary>Drops cached page text, for example after the document is edited.</summary>
        internal void Invalidate() => Array.Clear(_pages);

        /// <summary>The same place written one way: the end of a non-final page is the next page's start.</summary>
        internal TextPosition Normalize(TextPosition position)
        {
            if (PageCount == 0) return Start;
            int page = Math.Clamp(position.Page, 0, PageCount - 1);
            int offset = Math.Clamp(position.Offset, 0, Page(page).Length);
            if (offset == Page(page).Length && page < PageCount - 1) return new(page + 1, 0);
            return new(page, offset);
        }

        internal string GetText(TextPosition from, TextPosition to, int maximumLength = -1)
        {
            from = Normalize(from);
            to = Normalize(to);
            if (to <= from) return string.Empty;
            var text = new StringBuilder();
            for (int page = from.Page; page <= to.Page; page++)
            {
                string pageText = Page(page);
                int start = page == from.Page ? from.Offset : 0;
                int end = page == to.Page ? to.Offset : pageText.Length;
                text.Append(pageText, start, end - start);
                if (maximumLength >= 0 && text.Length >= maximumLength) return text.ToString(0, maximumLength);
            }
            return text.ToString();
        }

        internal bool IsBoundary(TextPosition position, ReadingUnit unit)
        {
            position = Normalize(position);
            if (position == Start || position == End) return true;
            string text = Page(position.Page);
            int at = position.Offset;
            return unit switch
            {
                ReadingUnit.Character or ReadingUnit.Format => !char.IsLowSurrogate(text[at]),
                ReadingUnit.Word => at == 0 || (char.IsWhiteSpace(text[at - 1]) && !char.IsWhiteSpace(text[at])),
                ReadingUnit.Line or ReadingUnit.Paragraph => at == 0 || text[at - 1] == '\n',
                ReadingUnit.Page => at == 0,
                _ => false
            };
        }

        /// <summary>The nearest unit boundary at or before <paramref name="position"/>.</summary>
        internal TextPosition Floor(TextPosition position, ReadingUnit unit)
        {
            position = Normalize(position);
            if (unit == ReadingUnit.Document) return Start;
            for (int at = position.Offset; at >= 0; at--)
            {
                var candidate = new TextPosition(position.Page, at);
                if (IsBoundary(candidate, unit)) return candidate;
            }
            return new(position.Page, 0);
        }

        /// <summary>The first unit boundary after <paramref name="position"/>, or <see cref="End"/>.</summary>
        internal TextPosition Next(TextPosition position, ReadingUnit unit)
        {
            position = Normalize(position);
            if (unit == ReadingUnit.Document || position >= End) return End;
            for (int page = position.Page; page < PageCount; page++)
            {
                string text = Page(page);
                int from = page == position.Page ? position.Offset + 1 : 0;
                for (int at = from; at <= text.Length; at++)
                {
                    TextPosition candidate = Normalize(new(page, at));
                    if (candidate > position && IsBoundary(candidate, unit)) return candidate;
                }
            }
            return End;
        }

        /// <summary>The last unit boundary before <paramref name="position"/>, or <see cref="Start"/>.</summary>
        internal TextPosition Previous(TextPosition position, ReadingUnit unit)
        {
            position = Normalize(position);
            if (position <= Start) return Start;
            TextPosition before = position.Offset > 0
                ? new(position.Page, position.Offset - 1)
                : new(position.Page - 1, Page(position.Page - 1).Length - 1);
            return Floor(before, unit);
        }

        /// <summary>The unit that contains <paramref name="position"/> (UI Automation ExpandToEnclosingUnit).</summary>
        internal (TextPosition Start, TextPosition End) Enclosing(TextPosition position, ReadingUnit unit)
        {
            if (unit == ReadingUnit.Document) return (Start, End);
            position = Normalize(position);
            if (position == End && position != Start) position = Previous(position, ReadingUnit.Character);
            TextPosition start = Floor(position, unit);
            return (start, Next(start, unit));
        }

        /// <summary>
        /// Moves a range by whole units, the way ITextRangeProvider.Move does: the range snaps to the
        /// unit, moves, and stays degenerate if it began degenerate. Returns the units moved.
        /// </summary>
        internal int Move(ref TextPosition start, ref TextPosition end, ReadingUnit unit, int count)
        {
            bool degenerate = Normalize(start) == Normalize(end);
            if (degenerate)
            {
                start = Normalize(start);
                int steps = MoveEndpoint(ref start, unit, count);
                end = start;
                return steps;
            }
            TextPosition at = Floor(start, unit);
            int moved = 0;
            while (moved < count)
            {
                TextPosition next = Next(at, unit);
                if (next >= End) break;
                at = next;
                moved++;
            }
            while (moved > count)
            {
                if (at <= Start) break;
                at = Previous(at, unit);
                moved--;
            }
            if (moved == 0) return 0;
            start = at;
            end = Next(at, unit);
            return moved;
        }

        /// <summary>Moves one endpoint by whole units (MoveEndpointByUnit). Returns the units moved.</summary>
        internal int MoveEndpoint(ref TextPosition endpoint, ReadingUnit unit, int count)
        {
            TextPosition at = Normalize(endpoint);
            int moved = 0;
            while (moved < count && at < End) { at = Next(at, unit); moved++; }
            while (moved > count && at > Start) { at = Previous(at, unit); moved--; }
            endpoint = at;
            return moved;
        }

        /// <summary>Finds <paramref name="text"/> between two positions, or null.</summary>
        internal (TextPosition Start, TextPosition End)? Find(TextPosition from, TextPosition to, string text,
            bool backward, bool ignoreCase)
        {
            if (string.IsNullOrEmpty(text)) return null;
            from = Normalize(from);
            to = Normalize(to);
            var positions = new List<TextPosition>();
            var buffer = new StringBuilder();
            for (int page = from.Page; page <= to.Page && page < PageCount; page++)
            {
                string pageText = Page(page);
                int start = page == from.Page ? from.Offset : 0;
                int end = page == to.Page ? to.Offset : pageText.Length;
                for (int at = start; at < end; at++) { positions.Add(new(page, at)); buffer.Append(pageText[at]); }
            }
            positions.Add(to);
            StringComparison comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            string haystack = buffer.ToString();
            int index = backward ? haystack.LastIndexOf(text, comparison) : haystack.IndexOf(text, comparison);
            if (index < 0) return null;
            return (positions[index], Normalize(positions[index + text.Length]));
        }
    }
}
