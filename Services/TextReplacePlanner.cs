using System.Text;
using System.Windows;

namespace KillerPDF.Services;

/// <summary>One extracted word in the reading frame of its line: text runs left to right.</summary>
internal readonly record struct ReplaceWord(string Text, Rect Bounds, int SourceStart = -1);

/// <summary>
/// A line of page text that Find and Replace rewrites. <see cref="WordIndices"/> point into the
/// words passed to the planner, <see cref="Bounds"/> covers the whole line, and
/// <see cref="MatchBounds"/> holds one rectangle per replaced occurrence.
/// </summary>
internal sealed record ReplaceLine(
    IReadOnlyList<int> WordIndices,
    string OriginalText,
    string NewText,
    Rect Bounds,
    IReadOnlyList<Rect> MatchBounds);

/// <summary>
/// The text side of Find and Replace: finds occurrences with the Match case and Whole word
/// options, groups extracted words into lines the way the Text tool does, and works out each
/// line's replacement text. It knows nothing about canvases or annotations.
/// </summary>
internal static class TextReplacePlanner
{
    /// <summary>Start index of every non-overlapping occurrence of <paramref name="find"/>.</summary>
    internal static IReadOnlyList<int> FindAll(string text, string find, bool matchCase, bool wholeWord)
    {
        var starts = new List<int>();
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(find)) return starts;
        StringComparison comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (int start = text.IndexOf(find, comparison); start >= 0;
             start = text.IndexOf(find, start + 1, comparison))
        {
            if (wholeWord && !IsWholeWord(text, start, find.Length)) continue;
            if (starts.Count > 0 && start < starts[^1] + find.Length) continue;   // no overlaps
            starts.Add(start);
        }
        return starts;
    }

    /// <summary>Replaces the occurrences at <paramref name="starts"/> and returns the new text.</summary>
    internal static string ReplaceAt(string text, IReadOnlyList<int> starts, int findLength, string replace)
    {
        var result = new StringBuilder(text.Length);
        int copied = 0;
        foreach (int start in starts)
        {
            result.Append(text, copied, start - copied).Append(replace);
            copied = start + findLength;
        }
        return result.Append(text, copied, text.Length - copied).ToString();
    }

    /// <summary>
    /// Groups words into lines: words whose vertical centers sit within half a line height of
    /// each other share a line, and a horizontal gap wider than a column gutter starts a new one.
    /// Each line lists its word indices left to right.
    /// </summary>
    internal static IReadOnlyList<IReadOnlyList<int>> GroupLines(IReadOnlyList<ReplaceWord> words)
    {
        var rows = new List<List<int>>();
        foreach (int index in Enumerable.Range(0, words.Count)
                     .Where(i => !words[i].Bounds.IsEmpty && words[i].Text.Length > 0)
                     .OrderBy(i => Center(words[i].Bounds)))
        {
            Rect bounds = words[index].Bounds;
            List<int>? row = rows.LastOrDefault();
            if (row is not null)
            {
                Rect first = words[row[0]].Bounds;
                double tolerance = Math.Min(first.Height, bounds.Height) * 0.5;
                if (Math.Abs(Center(first) - Center(bounds)) <= tolerance)
                {
                    row.Add(index);
                    continue;
                }
            }
            rows.Add([index]);
        }

        // Split each row at column gutters, with the Text tool's rule: word spacing is small, a
        // gap wider than one and a half line heights (at least 24 units) is another column.
        var lines = new List<IReadOnlyList<int>>();
        foreach (List<int> row in rows)
        {
            row.Sort((a, b) => words[a].Bounds.Left.CompareTo(words[b].Bounds.Left));
            var line = new List<int> { row[0] };
            for (int i = 1; i < row.Count; i++)
            {
                Rect previous = words[row[i - 1]].Bounds;
                Rect current = words[row[i]].Bounds;
                double gutter = Math.Max(Math.Max(previous.Height, current.Height) * 1.5, 24);
                if (current.Left - previous.Right > gutter)
                {
                    lines.Add(line);
                    line = [];
                }
                line.Add(row[i]);
            }
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>
    /// Plans every line that contains <paramref name="find"/>. A match may span words on one line
    /// but never crosses lines. <paramref name="includeMatch"/> limits the plan to chosen
    /// occurrences (Replace for the current match); lines with none left are skipped.
    /// </summary>
    internal static IReadOnlyList<ReplaceLine> Plan(IReadOnlyList<ReplaceWord> words, string find,
        string replace, bool matchCase, bool wholeWord, Func<Rect, bool>? includeMatch = null,
        int? onlyStart = null)
    {
        var plans = new List<ReplaceLine>();
        if (string.IsNullOrEmpty(find)) return plans;
        replace ??= string.Empty;
        var sourceStarts = new int[words.Count];
        int sourceOffset = 0;
        for (int index = 0; index < words.Count; index++)
        {
            sourceStarts[index] = words[index].SourceStart >= 0 ? words[index].SourceStart : sourceOffset;
            sourceOffset += words[index].Text.Length + 1;
        }

        foreach (IReadOnlyList<int> line in GroupLines(words))
        {
            // Line text with one space between words, and each character mapped to its word.
            var text = new StringBuilder();
            var owner = new List<int>();
            var source = new List<int>();
            foreach (int index in line)
            {
                if (text.Length > 0) { text.Append(' '); owner.Add(-1); source.Add(-1); }
                text.Append(words[index].Text);
                owner.AddRange(Enumerable.Repeat(index, words[index].Text.Length));
                source.AddRange(Enumerable.Range(sourceStarts[index], words[index].Text.Length));
            }
            string lineText = text.ToString();

            var chosen = new List<int>();
            var matchBounds = new List<Rect>();
            foreach (int start in FindAll(lineText, find, matchCase, wholeWord))
            {
                if (onlyStart is int selected && source[start] != selected) continue;
                Rect bounds = Rect.Empty;
                foreach (int index in owner.Skip(start).Take(find.Length).Where(i => i >= 0).Distinct())
                    bounds.Union(words[index].Bounds);
                if (bounds.IsEmpty || (includeMatch is not null && !includeMatch(bounds))) continue;
                chosen.Add(start);
                matchBounds.Add(bounds);
            }
            if (chosen.Count == 0) continue;

            Rect lineBounds = Rect.Empty;
            foreach (int index in line) lineBounds.Union(words[index].Bounds);
            plans.Add(new ReplaceLine(line, lineText,
                ReplaceAt(lineText, chosen, find.Length, replace), lineBounds, matchBounds));
        }
        return plans;
    }

    private static double Center(Rect bounds) => bounds.Top + bounds.Height / 2;

    private static bool IsWholeWord(string text, int start, int length)
    {
        bool before = start == 0 || !IsWordCharacter(text[start - 1]);
        int end = start + length;
        bool after = end >= text.Length || !IsWordCharacter(text[end]);
        return before && after;
    }

    private static bool IsWordCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';
}
